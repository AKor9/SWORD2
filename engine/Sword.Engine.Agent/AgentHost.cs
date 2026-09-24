using System.Reflection;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;

namespace Sword.Engine.Agent;

/// <summary>Как создать агентский хост: вид процесса, контент, каталог данных мира, куда класть артефакты.</summary>
public sealed record AgentHostOptions
{
    /// <summary>Вид процесса: <c>headless</c> (симуляция без окна) или <c>app</c> (живая игра).</summary>
    public required string Mode { get; init; }

    /// <summary>Отпечаток контента (<c>PakSource.ContentHash</c>); симуляция без контента — <c>none</c>.</summary>
    public string ContentHash { get; init; } = "none";

    /// <summary>Имена данных мира (собирается на потоке симуляции до старта агента).</summary>
    public required Catalog Catalog { get; init; }

    /// <summary>Управление временем симуляции.</summary>
    public required TimeControl Time { get; init; }

    /// <summary>Точка обслуживания: её обслуживает хозяин цикла симуляции.</summary>
    public required ServiceQueue Service { get; init; }

    /// <summary>Кольцо журнала для <c>log.tail</c> (уже подключено к <see cref="Log"/>).</summary>
    public required LogRing Log { get; init; }

    /// <summary>Каталог артефактов сеанса: <c>artifacts/agent/&lt;сеанс&gt;</c> создаётся внутри.</summary>
    public required string ArtifactsRoot { get; init; }

    /// <summary>Остановить процесс (<c>session.stop</c>): Headless — выйти из цикла, App — закрыть окно.</summary>
    public required Action Stop { get; init; }

    /// <summary>Сколько ждать точку обслуживания, прежде чем ответить ошибкой.</summary>
    public TimeSpan ServiceTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Самое долгое ожидание, которое инструмент может заказать (<c>timeoutSeconds</c> у <c>time.*</c> и
    /// <c>capture.bundle</c>): не больше предела вызова транспорта, иначе ожидание обречено на ошибку предела. HTTP живой
    /// игры — <see cref="HttpTransport.MaxWait"/>.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Сколько тиков держит история событий (<c>events.history</c>).</summary>
    public int HistoryTicks { get; init; } = ObservationHistory.DefaultTicks;
}

/// <summary>
/// Агентский хост (раздел 8 плана): сеанс, реестр инструментов, квитанции, артефакты; инструменты движка
/// (<see cref="CoreTools"/>) регистрируются сразу, клиент (Engine.Agent.Client) и игра (Game.Agent) добавляют свои.
/// Транспорт — отдельно (<see cref="StdioTransport"/>, <see cref="HttpTransport"/>) над одним <see cref="McpServer"/>.
/// К миру инструменты обращаются только через точку обслуживания; хост на потоке симуляции ничего не исполняет.
/// </summary>
public sealed class AgentHost
{
    private readonly List<AgentTool> _tools = [];
    private readonly AgentHostOptions _options;

    public AgentHost(AgentHostOptions options)
    {
        _options = options;
        SessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        BuildId = BuildIdOf(Assembly.GetEntryAssembly() ?? typeof(AgentHost).Assembly);
        Artifacts = Path.Combine(options.ArtifactsRoot, SessionId);
        _catalog = options.Catalog;
        Observation = new ObservationHub(options.Time, options.HistoryTicks);
        Server = new McpServer(this);
        CoreTools.Register(this);
        ReplayTools.Register(this);
        ObservationTools.Register(this);
        InvariantTools.Register(this);
        FeatureTools.Register(this);
        ExperimentTools.Register(this);
        ProfileTools.Register(this);
        CauseTools.Register(this);
    }

    public string Mode => _options.Mode;
    public string SessionId { get; }
    public string BuildId { get; }
    public string ContentHash => _options.ContentHash;
    /// <summary>Имена данных мира текущей симуляции: сменилась симуляция (<c>session.start</c>) — сменился и каталог.</summary>
    public Catalog Catalog
    {
        get => Volatile.Read(ref _catalog);
        set => Volatile.Write(ref _catalog, value);
    }

    private Catalog _catalog = null!;

    /// <summary>
    /// Симуляция сеанса (Headless: <see cref="SimulationLoop"/>) — пересборка для <c>session.start</c>, <c>time.seek</c>
    /// назад и <c>replay.check</c>; у живой игры — null.
    /// </summary>
    public AgentSimulation? Simulation { get; internal set; }
    public TimeControl Time => _options.Time;
    public ServiceQueue Service => _options.Service;

    /// <summary>
    /// Наблюдение агента (этап 3в): история событий, наблюдения <c>watch.*</c>, условие остановки. Подключает к симуляции
    /// хозяин цикла (<see cref="Observe"/>): Headless — <see cref="SimulationLoop"/> (к каждой симуляции сеанса), живая игра —
    /// exe при старте агента.
    /// </summary>
    public ObservationHub Observation { get; }

    // Снимки мира (world.snapshot) — на потоке агента, под своей блокировкой; старейшие вытесняются.
    private readonly Dictionary<string, WorldSnapshot> _snapshots = [];
    private readonly Queue<string> _snapshotOrder = new();
    private int _nextSnapshot = 1;

    /// <summary>Снимков хранится не больше (старейший вытесняется; файлы в артефактах остаются).</summary>
    public const int MaxSnapshots = 32;

    internal string NextSnapshotId()
    {
        lock (_snapshots) return $"s{_nextSnapshot++}";
    }

    internal void PutSnapshot(WorldSnapshot snapshot)
    {
        lock (_snapshots)
        {
            if (!_snapshots.ContainsKey(snapshot.Id)) _snapshotOrder.Enqueue(snapshot.Id);
            _snapshots[snapshot.Id] = snapshot;
            while (_snapshotOrder.Count > MaxSnapshots) _snapshots.Remove(_snapshotOrder.Dequeue());
        }
    }

    internal WorldSnapshot GetSnapshot(string id)
    {
        lock (_snapshots)
            return _snapshots.TryGetValue(id, out var s) ? s
                : throw new ArgumentException($"нет снимка «{id}»; есть: {string.Join(", ", _snapshotOrder)} (или now)");
    }

    /// <summary>Наблюдать за симуляцией <paramref name="simulation"/> (поток симуляции, до её тиков или между ними).</summary>
    public void Observe(Runtime.Simulation simulation) =>
        Observation.Attach(simulation, new Catalog(simulation), Names);
    public LogRing LogRing => _options.Log;

    /// <summary>Предел <c>timeoutSeconds</c> инструментов, с (<see cref="AgentHostOptions.MaxWait"/>).</summary>
    public double MaxWaitSeconds => _options.MaxWait.TotalSeconds;
    public McpServer Server { get; }

    /// <summary>Каталог артефактов сеанса (создаётся при первой записи).</summary>
    public string Artifacts { get; }

    /// <summary>Имена сущностей игры (Game.Agent); null — только номера.</summary>
    public IEntityNames? Names { get; set; }

    /// <summary>
    /// Вычисляемые поля сущностей игры (<see cref="IEntityFields"/>): метрики <c>feature.verify</c> читают их как поля, наблюдения
    /// (<c>watch.add</c>, <c>replay.observe</c>, 5г) пишут их по тикам.
    /// </summary>
    public IEntityFields? Fields
    {
        get => Observation.Fields;
        set => Observation.Fields = value;
    }

    /// <summary>
    /// Профайлер CPU (точка Па; <c>profile.stages</c>): у Headless — свой, <see cref="SimulationLoop"/> подключает его к каждой
    /// симуляции сеанса; у живой игры exe задаёт профайлер клиента (кадр, тики, проходы). Читать и менять — только в точке
    /// обслуживания (однопоточный).
    /// </summary>
    public CpuProfiler Profiler { get; set; } = new();

    /// <summary>Где манифесты фич и чем снимать виды (<c>feature.*</c>, этап 3д) — задаёт exe игры; null — фич нет.</summary>
    public FeatureSetup? Features { get; set; }

    /// <summary>Последний кадр клиента (App); у Headless — null. Пишет поток кадра, читает поток агента.</summary>
    public ulong? LastFrame
    {
        get => Volatile.Read(ref _lastFrame) is var f and >= 0 ? (ulong)f : null;
        set => Volatile.Write(ref _lastFrame, value is { } v ? (long)v : -1);
    }

    private long _lastFrame = -1;

    /// <summary>Сыграно тиков (по сообщениям хозяина цикла).</summary>
    public ulong LastTick => Time.Tick;

    public string Title => $"SWORD ({Mode})";

    /// <summary>Инструменты по порядку регистрации.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools;

    /// <summary>Добавить инструмент; имя — уникальное.</summary>
    public void Add(AgentTool tool)
    {
        if (_tools.Any(t => t.Name == tool.Name)) throw new InvalidOperationException($"инструмент {tool.Name} уже есть");
        _tools.Add(tool);
    }

    /// <summary>Остановить процесс (session.stop).</summary>
    public void RequestStop() => _options.Stop();

    /// <summary>
    /// Запрос в точку обслуживания с ожиданием: ответ — после ближайшей точки; не пришёл за
    /// <see cref="AgentHostOptions.ServiceTimeout"/> — <see cref="TimeoutException"/> (симуляция стоит: окно тянут за рамку,
    /// кадр завис).
    /// </summary>
    public async Task<T> Serve<T>(Func<ServiceContext, T> run, CancellationToken cancel)
    {
        // Таймаут — отменой запроса: не дождались — запрос и не исполнится (иначе команда, на которую агент получил
        // ошибку, прошла бы позже, а повтор дал бы её дважды).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(_options.ServiceTimeout);
        try
        {
            return await Service.Post(run, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"симуляция не дошла до точки обслуживания за {_options.ServiceTimeout.TotalSeconds:0} с " +
                "(окно тянут за рамку или кадр завис?); запрос не исполнен");
        }
    }

    /// <summary>Квитанция к ответу: тик — из ответа, иначе последний сыгранный.</summary>
    public Receipt Receipt(ToolOutput output) =>
        // Тик задан без кадра — кадр неизвестен (null), а не текущий: текущий мог уйти вперёд от тика ответа.
        new(BuildId, ContentHash, SessionId, output.Tick ?? LastTick, output.Stage, output.Frame ?? (output.Tick is null ? LastFrame : null),
            Complete: !output.Truncated, output.Truncated, output.Continuation, output.ArtifactId);

    /// <summary>Файл артефакта сеанса (каталог создаётся); <paramref name="name"/> — имя файла.</summary>
    public string ArtifactPath(string name)
    {
        Directory.CreateDirectory(Artifacts);
        return Path.Combine(Artifacts, name);
    }

    /// <summary>
    /// Инструкции сервера MCP — коротко: порядок расследования, ограничения, стоимость. Клиент показывает их модели.
    /// </summary>
    public string Instructions => InstructionsText.ReplaceLineEndings("\n");

    private string InstructionsText =>
        $"""
        SWORD — симуляция игры ({Mode}). Порядок: session.status → architecture.projects / ecs.plan (устройство) →
        world.query / entity.get (состояние; сущность — номер «12v3» или имя, например player; state.hash — хеш по
        участникам, state.describe — что участник хеширует) → time.step / time.runUntil
        (время) → command.send (воздействие; всегда на будущий тик, в живой игре команда агента перебивает ввод клавиатуры
        того же тика) → log.tail. Мир агент видит только между тиками: каждый ответ несёт квитанцию (tickId — сыграно
        тиков, frameId — кадр клиента). Большие ответы постраничны (skip/take, continuationToken), артефакты — файлы в
        {Artifacts}. Произвольного кода нет, память ECS напрямую не читается.
        Сеанс пишет реплей с тика 0: replay.save — файл, replay.check и replay.diff — сверка и сравнение прогонов.
        Условия (один JSON-язык): time.runUntil с condition — встать на тике, где выполнилось; watch.add → entity.history —
        значения полей по тикам; events.history — события последних тиков; world.snapshot / world.diff — что изменилось;
        world.query с where и aggregate; replay.observe — то же на прогоне реплея.
        Инварианты: invariant.list — что проверяется и что нарушено; time.breakOnInvariant — встать на нарушении;
        invariant.run — проверить сейчас; invariant.define — свой инвариант условием; нарушения — events.history (InvariantViolation).
        Приёмка и эксперименты: feature.list, feature.verify (id) — вердикт фичи по манифесту (сценарии, инварианты, метрики, бюджеты,
        виды); experiment.fork — реплей до тика и ветками дальше, experiment.compare — чем разошлись, experiment.sweep — перебор конфига.
        Производительность: profile.stages (enable: true) → поработать → profile.stages — где время кадра и тика (стадии, системы,
        инварианты, фазы кадра, проходы), мс и байты; anomalies — медленные кадры и тики (класс причины, главная зона, файл);
        trace — Chrome Trace для Perfetto; симуляцию не меняет.
        {(Mode == "app" ? "Живая игра: время идёт само, time.pause останавливает симуляцию (кадры рисуются); capture.bundle — снимок кадра с глубиной, видимыми сущностями и камерой." : "Headless стартует на паузе: время двигают time.step и time.runUntil; time.seek — назад и вперёд, session.start {replay} — воспроизвести реплей (свои команды ветвят его); time.break {stage, condition} — встать посреди тика после стадии.")}
        """;

    /// <summary>Сборка: информационная версия и MVID главного модуля (меняется с каждой пересборкой).</summary>
    private static string BuildIdOf(Assembly entry)
    {
        string version = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? entry.GetName().Version?.ToString() ?? "0";
        return $"{entry.GetName().Name} {version} {entry.ManifestModule.ModuleVersionId.ToString("N")[..8]}";
    }
}
