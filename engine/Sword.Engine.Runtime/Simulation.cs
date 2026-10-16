using System.Runtime.CompilerServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>Время тика — ресурс мира. <see cref="Time"/> копится <c>+= dt</c> во float в начале тика: в тике k — сумма
/// k+1 шагов с округлением float, на котором держатся таймеры.</summary>
public readonly record struct SimTime(ulong Tick, float Dt, float Time);

/// <summary>Настройки симуляции (секция <c>sim</c>).</summary>
public sealed record SimSettings
{
    /// <summary>Тиков в секунду.</summary>
    public int TickRate { get; init; } = 120;

    /// <summary>Потолок тиков за кадр клиента: дальше симуляция отстаёт от часов.</summary>
    public int MaxTicksPerFrame { get; init; } = 8;

    /// <summary>Главное зерно счётчикового ГСЧ (<see cref="SimRandom"/>); пишется в реплей.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Кодовые инварианты проверяются (в бенче — нет: их стоимость меряется отдельно). На состояние и хеш не
    /// влияет.</summary>
    public bool Invariants { get; init; } = true;
}

/// <summary>Настройки модуля (секция групп <c>modules.&lt;id&gt;</c>).</summary>
public sealed record ModuleSettings
{
    /// <summary>Выключенный модуль не регистрирует ничего (<c>SWORD_MODULES_&lt;ID&gt;_ENABLED</c>, <c>--set modules.&lt;id&gt;.enabled=false</c>).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Модуль симуляции, подключённый к exe статически; неактивный (<c>modules.&lt;id&gt;.enabled=false</c>) не
/// регистрирует ничего.</summary>
public interface IModule
{
    /// <summary>Id — ключ конфига, логов, реестра и токена регистраций, поэтому только <see cref="IdRule"/>: без точки
    /// (она делит путь конфига), регистра и не-латиницы.</summary>
    string Id { get; }

    const string IdRule = "строчная латиница, цифры и '_', первой — буква";

    static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] is < 'a' or > 'z') return false;
        foreach (char c in id)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

    void Configure(SimulationBuilder b);
}

/// <summary>
/// Симуляция без окна: мир ECS со стадиями, фиксированный тик, модули, конфиг, экспорт тика. Всё, что меняет её на ходу, —
/// команда с номером тика (<see cref="Enqueue{T}(in T)"/>), событие служебной стадии до стадий игры своего тика.
/// </summary>
public sealed class Simulation
{
    private readonly TickCommands _commands;
    private readonly World _world;
    private readonly IStateContributor[] _state;
    private readonly ulong[] _hashParts;
    private float _time;

    private Simulation(World world, SimSettings settings, TickCommands commands, TickExport export,
        IReadOnlyList<string> active, IReadOnlyList<string> inactive, ConfigSet config, IStateContributor[] state,
        SimulationInvariants invariants)
    {
        _state = state;
        Invariants = invariants;
        _afterBarrier = AfterBarrier;
        StateParts = [.. state.Select(c => c.Id)];
        _hashParts = new ulong[state.Length];
        _world = world;
        Settings = settings;
        Dt = 1f / settings.TickRate;
        _commands = commands;
        Export = export;
        ActiveModules = active;
        InactiveModules = inactive;
        Config = config;
    }

    /// <summary>Мир только для чтения и у владельца: менять симуляцию — только командами тика.</summary>
    public WorldReader World => _world.Reader;

    public SimSettings Settings { get; }
    public ConfigSet Config { get; }

    /// <summary>Фиксированный шаг, с.</summary>
    public float Dt { get; }

    /// <summary>Номер следующего тика (сколько тиков уже сделано).</summary>
    public ulong Tick { get; private set; }

    public IReadOnlyList<string> ActiveModules { get; }
    public IReadOnlyList<string> InactiveModules { get; }

    /// <summary>Модули только с клиентской частью (огонь): симуляция их не исполняет, в реплей и хеш они не входят;
    /// включение читает клиент.</summary>
    public IReadOnlyList<string> ClientOnlyModules { get; private init; } = [];
    public IReadOnlyList<string> InactiveClientOnlyModules { get; private init; } = [];

    /// <summary>Записи тиков для клиента: срабатывания, объявленные <see cref="SimulationBuilder.Export{TTrigger}"/>.</summary>
    public TickExport Export { get; }

    /// <summary>Секции конфига движка — симуляции и клиента: Id модуля (имя его секции) с ними совпадать не может.</summary>
    public static readonly IReadOnlyList<string> EngineSections = ["sim", "modules", .. ConfigSet.ClientSections];

    /// <summary>Служебные стадии: команды тика — до стадий игры (их события видны всем в том же тике), экспорт — после
    /// барьера последней.</summary>
    public const string CommandStage = "runtime.commands", ExportStage = "runtime.export";

    /// <summary>
    /// Собрать симуляцию: секция <c>sim</c>, ядро, активные модули, мир со стадиями <paramref name="stages"/> между
    /// <see cref="CommandStage"/> и <see cref="ExportStage"/>. Неизвестные секции проверяет exe
    /// (<see cref="ConfigSet.Complete"/>): их читает и клиент.
    /// </summary>
    /// <param name="core">Ядро — не модуль, не выключается, регистрирует без токена. Null — без ядра (тесты движка).</param>
    /// <param name="clientModules">Id модулей только с клиентской частью (<see cref="ClientOnlyModules"/>).</param>
    public static Simulation Build(ConfigSet config, IReadOnlyList<string> stages, Action<SimulationBuilder>? core,
        IEnumerable<IModule> modules, IReadOnlyList<string>? clientModules = null) =>
        Build(config, stages, core, modules, new WorldOptions(), clientModules);

    /// <summary>Симуляция без ядра — модули движка и тесты.</summary>
    public static Simulation Build(ConfigSet config, IReadOnlyList<string> stages, IEnumerable<IModule> modules) =>
        Build(config, stages, null, modules, new WorldOptions());

    /// <inheritdoc cref="Build(ConfigSet, IReadOnlyList{string}, Action{SimulationBuilder}, IEnumerable{IModule})"/>
    /// <param name="worldOptions">Настройки мира (размер чанка, проверки) — для тестов и замеров.</param>
    public static Simulation Build(ConfigSet config, IReadOnlyList<string> stages, Action<SimulationBuilder>? core,
        IEnumerable<IModule> modules, WorldOptions worldOptions, IReadOnlyList<string>? clientModules = null)
    {
        if (stages.Count < 1) throw new ArgumentException("нужна хотя бы одна стадия игры");
        if (stages.Contains(CommandStage) || stages.Contains(ExportStage))
            throw new ArgumentException($"стадии {CommandStage} и {ExportStage} — служебные, их добавляет Runtime");
        var settings = config.Section<SimSettings>("sim");
        if (settings.TickRate is < 1 or > 10_000) throw new InvalidDataException($"sim.tickRate = {settings.TickRate}: 1…10000");
        if (settings.MaxTicksPerFrame < 1) throw new InvalidDataException($"sim.maxTicksPerFrame = {settings.MaxTicksPerFrame}: ≥ 1");
        var list = modules.ToList();
        var ids = new List<string>();
        foreach (var m in list)
        {
            if (!IModule.IsValidId(m.Id))
                throw new InvalidOperationException($"модуль {m.GetType().Name}: Id \"{m.Id}\" — нужно {IModule.IdRule}");
            if (EngineSections.Contains(m.Id))
                throw new InvalidOperationException($"модуль {m.Id}: Id совпадает с секцией конфига движка ({string.Join(", ", EngineSections)})");
            if (ids.Contains(m.Id, StringComparer.Ordinal)) throw new InvalidOperationException($"модуль {m.Id} дважды");
            ids.Add(m.Id);
        }
        var clientOnly = clientModules ?? [];
        foreach (string id in clientOnly)
        {
            if (!IModule.IsValidId(id)) throw new InvalidOperationException($"клиентский модуль \"{id}\": нужно {IModule.IdRule}");
            if (EngineSections.Contains(id))
                throw new InvalidOperationException($"клиентский модуль {id}: Id совпадает с секцией конфига движка ({string.Join(", ", EngineSections)})");
            if (ids.Contains(id, StringComparer.Ordinal) || clientOnly.Count(c => c == id) > 1)
                throw new InvalidOperationException($"клиентский модуль {id} дважды или совпадает с модулем симуляции (у модуля один Id на обе стороны)");
        }
        var moduleSettings = config.Groups<ModuleSettings>("modules", [.. ids, .. clientOnly]);

        var world = new WorldBuilder { Options = worldOptions }.Stages([CommandStage, .. stages, ExportStage]);
        world.Resource(new SimTime(0, 1f / settings.TickRate, 0));
        world.Resource(new SimRandom(settings.Seed));
        var commands = new TickCommands();
        var export = new TickExport();
        var b = new SimulationBuilder(config, world, commands, export, stages);

        b.Module = null;
        core?.Invoke(b);

        var active = new List<string>();
        var inactive = new List<string>();
        foreach (var m in list)
        {
            if (!moduleSettings[m.Id].Enabled)
            {
                inactive.Add(m.Id);
                config.Skip(m.Id);   // секция настроек выключенного модуля — не «неизвестная»
                continue;
            }
            b.Module = m.Id;
            m.Configure(b);
            active.Add(m.Id);
        }
        b.Module = null;
        var clientActive = new List<string>();
        var clientInactive = new List<string>();
        foreach (string id in clientOnly)
        {
            if (moduleSettings[id].Enabled)
            {
                clientActive.Add(id);
                continue;
            }
            clientInactive.Add(id);
            config.Skip(id);   // секция выключенного клиентского модуля — не «неизвестная», как у модуля симуляции
        }

        world.System(CommandStage, commands);
        world.System(ExportStage, export);
        var built = world.Build();
        // Мир собран: поздняя регистрация (например, из OnWorldBuilt) молча потерялась бы — построитель закрыт.
        b.Sealed = true;
        foreach (var init in b.WorldInits) init(built);
        // Мир наполнен ядром и модулями: то, что зависит от их начального наполнения (тело бойца по позе постройки,
        // которую задаёт модуль), — после всех OnWorldBuilt.
        foreach (var init in b.WorldReady) init(built);
        // Участники хеша — по Id: порядок не зависит от порядка регистрации.
        IStateContributor[] state = [.. b.Contributors.Append(new EcsStateContributor(built.Reader)).OrderBy(c => c.Id, StringComparer.Ordinal)];
        // Инварианты: базовые движка, затем ядра и модулей в порядке регистрации.
        var invariants = new SimulationInvariants(built.Reader, settings.Invariants,
            [.. EcsInvariants.For(built.Reader).Select(i => (i, "engine")), .. b.Registered]);
        Log.Info("runtime", $"симуляция: {settings.TickRate} тиков/с, модули: {string.Join(", ", active)}" +
            (inactive.Count > 0 ? $"; выключены: {string.Join(", ", inactive)}" : "") +
            (clientOnly.Count > 0 ? $"; клиентские по конфигу: {string.Join(", ", clientActive)}" +
                (clientInactive.Count > 0 ? $" (выключены: {string.Join(", ", clientInactive)})" : "") : ""));
        var sim = new Simulation(built, settings, commands, export, active, inactive, config, state, invariants)
        {
            ClientOnlyModules = clientActive,
            InactiveClientOnlyModules = clientInactive,
            Tuning = [.. b.Knobs],
            // Сведения реплея — после начального наполнения мира: модуль строит то, от чего они (база MM), в OnWorldBuilt.
            Facts = b.Facts.Select(f => (f.Name, f.Value())).OrderBy(f => f.Name, StringComparer.Ordinal).ToDictionary(f => f.Name, f => f.Item2),
        };
        sim.Diagnostics = new SimulationDiagnostics(b.Diagnostics);
        return sim;
    }

    /// <summary>Ручки симуляции по порядку регистрации: значение — в ресурсе мира, изменение — <see cref="TuningCommand"/>.</summary>
    public IReadOnlyList<TuningKnob> Tuning { get; private init; } = [];

    /// <summary>Сведения ядра и модулей для реплея (<see cref="Replay.Facts"/>): запись кладёт в заголовок, воспроизведение
    /// сверяет.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; private init; } = new Dictionary<string, string>();

    /// <summary>Диагностические режимы: включает владелец между тиками; мир, хеш и реплей от них не зависят.</summary>
    public SimulationDiagnostics Diagnostics { get; private set; } = null!;

    /// <summary>Ручка по полному имени «секция.ключ» (регистр не важен); нет — null.</summary>
    public TuningKnob? Knob(string name) => Tuning.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Принимает ли симуляция команду <typeparamref name="T"/>: её регистрирует модуль, и без модуля команды нет.
    /// </summary>
    public bool Accepts<T>() where T : unmanaged => _commands.Accepts<T>();

    /// <summary>Команда на следующий тик (<see cref="Tick"/>).</summary>
    public void Enqueue<T>(in T command) where T : unmanaged => _commands.Enqueue(Tick, command);

    /// <summary>Команда на тик <paramref name="tick"/> (не раньше <see cref="Tick"/>).</summary>
    public void Enqueue<T>(ulong tick, in T command) where T : unmanaged
    {
        if (tick < Tick) throw new ArgumentOutOfRangeException(nameof(tick), tick, $"тик {tick} уже прошёл (ближайший — {Tick})");
        _commands.Enqueue(tick, command);
    }

    private readonly Action<int> _afterBarrier;

    /// <summary>Инварианты: проверяются после барьеров своих стадий.</summary>
    public SimulationInvariants Invariants { get; }

    private void AfterBarrier(int stage) => Invariants.AfterBarrier(stage, Tick, Dt);

    /// <summary>Типы команд тика (их регистрируют ядро и модули) в порядке регистрации.</summary>
    public IReadOnlyList<Type> CommandTypes => _commands.Types;

    /// <summary>Участники хеша состояния по Id (<c>ecs</c> и зарегистрированные <see cref="SimulationBuilder.StateContributor"/>).</summary>
    public IReadOnlyList<string> StateParts { get; }

    /// <summary>Хеш состояния между тиками: <paramref name="parts"/> — хеш каждого участника в порядке
    /// <see cref="StateParts"/>, итог — хеш хешей. Без выделений.</summary>
    // Без PGO: JIT по профилю встраивал сюда обход одного из участников, и хеш толпы от запуска к запуску шёл
    // в 1,5 раза дольше (1,8 мс против 1,2). Здесь только вызовы — код участников остаётся с PGO.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ulong HashState(Span<ulong> parts)
    {
        if (parts.Length < _state.Length) throw new ArgumentException($"участников {_state.Length}, места {parts.Length}", nameof(parts));
        var total = new StateHasher(0);
        for (int i = 0; i < _state.Length; i++)
        {
            var h = new StateHasher(StateHasher.Of(_state[i].Id));
            _state[i].AppendHash(ref h);
            parts[i] = h.Value;
            total.Add(parts[i]);
        }
        return total.Value;
    }

    /// <summary>Хеш состояния с участниками по Id — для тестов и сверки (выделяет память).</summary>
    public StateHash HashState()
    {
        var parts = new ulong[_state.Length];
        ulong total = HashState(parts);
        return new StateHash(Tick, total, StateParts.Select((id, i) => (id, parts[i])).ToDictionary(p => p.id, p => p.Item2));
    }

    /// <summary>Запись реплея (<see cref="Record"/>); null — не пишется.</summary>
    public ReplayRecorder? Recorder { get; private set; }

    /// <summary>Воспроизводимый реплей (<see cref="Play"/>); null — нет.</summary>
    public ReplayPlayer? Player { get; private set; }

    /// <summary>Писать реплей: все команды тиков и хеш по участникам на тике 0 и каждые <paramref name="hashEvery"/>
    /// тиков. Только до первого тика.</summary>
    public ReplayRecorder Record(ReplayInfo info, int hashEvery = 1)
    {
        if (Tick != 0) throw new InvalidOperationException($"реплей пишется с начала, а сыграно {Tick} тиков");
        if (Recorder is not null) throw new InvalidOperationException("реплей уже пишется");
        if (hashEvery < 1) throw new ArgumentOutOfRangeException(nameof(hashEvery), hashEvery, "хеш — не реже чем через ≥ 1 тик");
        Recorder = new ReplayRecorder(this, info, hashEvery);
        _commands.Sink = Recorder;
        HashState(_hashParts);
        Recorder.AddHash(0, _hashParts);
        return Recorder;
    }

    /// <summary>Воспроизводить реплей: его команды — первыми в тике (команды клиента — после: ветвление), хеши сверяются
    /// по участникам. Симуляция собрана из <see cref="Replay.CreateConfig"/>; только до первого тика.</summary>
    /// <exception cref="InvalidDataException">Команды реплея не привязываются к командам симуляции.</exception>
    public ReplayPlayer Play(Replay replay)
    {
        if (Tick != 0) throw new InvalidOperationException($"реплей воспроизводится с начала, а сыграно {Tick} тиков");
        if (Player is not null) throw new InvalidOperationException("реплей уже воспроизводится");
        Player = new ReplayPlayer(this, replay);
        _commands.Source = Player;
        foreach (var w in Player.Warnings) Log.Warning("replay", w);
        if (Player.HashDue(0))
        {
            HashState(_hashParts);
            Player.Check(0, _hashParts);
        }
        return Player;
    }

    private CpuProfiler _profiler = CpuProfiler.Off;
    private int _recordZone;

    /// <summary>Профайлер CPU тиков (<see cref="AttachProfiler"/>); <see cref="CpuProfiler.Off"/> — не подключён.</summary>
    public CpuProfiler Profiler => _profiler;

    /// <summary>
    /// Подключить профайлер (владелец, между тиками): зона <c>tick</c> — период, в ней стадии, системы, барьеры,
    /// <c>invariant.&lt;id&gt;</c> и <c>sim.record</c>. Профайлер только читает часы: состояние, хеш и реплей от него не зависят.
    /// </summary>
    public void AttachProfiler(CpuProfiler profiler)
    {
        ArgumentNullException.ThrowIfNull(profiler);
        _world.AttachProfiler(profiler);
        Invariants.AttachProfiler(profiler);
        _recordZone = profiler.Zone("sim.record");
        _profiler = profiler;
    }

    /// <summary>Один тик: время (<c>DuelArena.Tick</c>: <c>Time += dt</c> первым делом), стадии с барьерами, экспорт.</summary>
    public void Step()
    {
        var profiler = _profiler;
        profiler.BeginTick(Tick);
        _time += Dt;
        _world.SetResourceImmediate(new SimTime(Tick, Dt, _time));
        _world.RunTick(Tick, Invariants.Active ? _afterBarrier : null);
        Tick++;
        if (Recorder is not null || Player is not null)
        {
            profiler.Begin(_recordZone);
            Recorder?.EndTick(Tick - 1);
            bool record = Recorder?.HashDue(Tick) ?? false, play = Player?.HashDue(Tick) ?? false;
            if (record || play)
            {
                HashState(_hashParts);
                if (record) Recorder!.AddHash(Tick, _hashParts);
                if (play) Player!.Check(Tick, _hashParts);
            }
            profiler.End(_recordZone);
        }
        profiler.EndTick();
    }
}

/// <summary>Построитель симуляции для модулей: регистрации с токеном модуля, настройки, экспорт клиенту, начальное
/// наполнение.</summary>
public sealed class SimulationBuilder
{
    private readonly WorldBuilder _world;
    private readonly TickCommands _commands;
    private readonly TickExport _export;
    internal readonly List<Action<World>> WorldInits = [];
    internal readonly List<Action<World>> WorldReady = [];

    /// <summary>Мир собран — регистрировать поздно.</summary>
    internal bool Sealed;

    internal readonly List<IStateContributor> Contributors = [];
    internal readonly List<(IInvariant Invariant, string Owner)> Registered = [];

    internal SimulationBuilder(ConfigSet config, WorldBuilder world, TickCommands commands, TickExport export, IReadOnlyList<string> stages)
    {
        Config = config;
        _world = world;
        _commands = commands;
        _export = export;
        Stages = stages;
    }

    public ConfigSet Config { get; }
    public IReadOnlyList<string> Stages { get; }

    /// <summary>Модуль, который сейчас настраивается (токен его регистраций); null — ядро.</summary>
    public string? Module { get; internal set; }

    private SimulationBuilder Open()
    {
        if (Sealed) throw new InvalidOperationException("мир симуляции уже собран: регистрировать можно только в Configure");
        return this;
    }

    /// <summary>Секция конфига с <c>Id</c> модуля; у выключенного модуля она пропущена.</summary>
    public T Settings<T>() where T : new() =>
        Open().Module is { } id ? Config.Section<T>(id) : throw new InvalidOperationException("Settings<T>() — только в Configure модуля");

    /// <summary>Участник хеша состояния — подсистема с состоянием вне ECS. Id — как у модуля, не <c>ecs</c>, без повторов.</summary>
    public SimulationBuilder StateContributor(IStateContributor contributor)
    {
        Open();
        ArgumentNullException.ThrowIfNull(contributor);
        if (!IModule.IsValidId(contributor.Id) || contributor.Id == "ecs")
            throw new InvalidOperationException($"участник хеша «{contributor.Id}»: нужно {IModule.IdRule}, не «ecs»");
        if (Contributors.Any(c => c.Id == contributor.Id)) throw new InvalidOperationException($"участник хеша «{contributor.Id}» дважды");
        Contributors.Add(contributor);
        return this;
    }

    /// <summary>Инвариант ядра или модуля: проверяется после барьера стадии <see cref="IInvariant.After"/> (или
    /// <see cref="Simulation.ExportStage"/> — конец тика). Id — <see cref="IInvariant.IdRule"/>, без повторов.</summary>
    public SimulationBuilder Invariant(IInvariant invariant)
    {
        Open();
        ArgumentNullException.ThrowIfNull(invariant);
        if (!IInvariant.IsValidId(invariant.Id)) throw new InvalidOperationException($"инвариант «{invariant.Id}»: нужно {IInvariant.IdRule}");
        if (Registered.Any(i => i.Invariant.Id == invariant.Id)) throw new InvalidOperationException($"инвариант «{invariant.Id}» дважды");
        if (!Stages.Contains(invariant.After) && invariant.After != Simulation.ExportStage)
            throw new ArgumentException($"инвариант {invariant.Id}: нет стадии {invariant.After}; есть: {string.Join(", ", Stages)}, {Simulation.ExportStage}");
        Registered.Add((invariant, Module ?? "core"));
        return this;
    }

    internal readonly List<(IDiagnosticMode Mode, string Owner)> Diagnostics = [];

    /// <summary>Диагностический режим: выключен, пока не включит владелец; мир, хеш и реплей от него не зависят.</summary>
    public SimulationBuilder Diagnostic(IDiagnosticMode mode)
    {
        Open();
        ArgumentNullException.ThrowIfNull(mode);
        if (!IInvariant.IsValidId(mode.Id)) throw new InvalidOperationException($"диагностический режим «{mode.Id}»: нужно {IInvariant.IdRule}");
        if (Diagnostics.Any(d => d.Mode.Id == mode.Id)) throw new InvalidOperationException($"диагностический режим «{mode.Id}» дважды");
        Diagnostics.Add((mode, Module ?? "core"));
        return this;
    }

    internal readonly List<(string Name, Func<string> Value)> Facts = [];

    /// <summary>
    /// Сведение для реплея: производное контента, кода и конфига, которого нет в хеше (отпечаток базы MM). Считается раз
    /// после наполнения мира; воспроизведение сверяет и при отличии называет причину расхождения. Имя — <see cref="IInvariant.IdRule"/>.
    /// </summary>
    public SimulationBuilder ReplayFact(string name, Func<string> value)
    {
        Open();
        ArgumentNullException.ThrowIfNull(value);
        if (!IInvariant.IsValidId(name)) throw new InvalidOperationException($"сведение реплея «{name}»: нужно {IInvariant.IdRule}");
        if (Facts.Any(f => f.Name == name)) throw new InvalidOperationException($"сведение реплея «{name}» дважды");
        Facts.Add((name, value));
        return this;
    }

    /// <summary>Несколько инвариантов подсистемы — <see cref="Invariant"/> по порядку.</summary>
    public SimulationBuilder Invariants(IEnumerable<IInvariant> invariants)
    {
        foreach (var i in invariants) Invariant(i);
        return this;
    }

    public SimulationBuilder Component<T>() where T : unmanaged
    {
        Open()._world.Component<T>();
        return this;
    }

    public SimulationBuilder Event<E>() where E : unmanaged
    {
        Open()._world.Event<E>();
        return this;
    }

    public SimulationBuilder Resource<T>(T initial)
    {
        Open()._world.Resource(initial);
        return this;
    }

    /// <summary>Хук добавления компонента (<see cref="WorldBuilder.OnAdd{T}"/>).</summary>
    public SimulationBuilder OnAdd<T>(AddHook<T> hook) where T : unmanaged
    {
        Open()._world.OnAdd(hook);
        return this;
    }

    /// <summary>Хук снятия компонента — освободить ресурс подсистемы (<see cref="WorldBuilder.OnRemove{T}"/>).</summary>
    public SimulationBuilder OnRemove<T>(RemoveHook<T> hook) where T : unmanaged
    {
        Open()._world.OnRemove(hook);
        return this;
    }

    /// <summary>Вид только для чтения для наблюдателей симуляции (клиент, инструменты) — <see cref="WorldBuilder.ReadView{TView}"/>.</summary>
    public SimulationBuilder ReadView<TView>(TView view) where TView : class, IReadView
    {
        Open()._world.ReadView(view);
        return this;
    }

    /// <summary>Тип команды тика: команда, поставленная на тик, — событие <see cref="Event{E}"/> первой стадии этого тика.</summary>
    public SimulationBuilder Command<T>() where T : unmanaged
    {
        Open()._world.Event<T>();
        _commands.Register<T>();
        return this;
    }

    /// <summary>Система в стадию игры (служебные стадии Runtime модулям недоступны).</summary>
    /// <remarks>Место регистрации (файл и строка вызова) попадает в описание системы.</remarks>
    public SimulationBuilder System(string stage, ISystem system,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!Open().Stages.Contains(stage))
            throw new ArgumentException($"{system.GetType().Name}: нет стадии игры {stage}; есть: {string.Join(", ", Stages)}");
        _world.System(stage, system, Module, file.Length > 0 ? $"{file}:{line}" : null);
        return this;
    }

    internal readonly List<TuningKnob> Knobs = [];
    private readonly HashSet<string> _tuningSections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ручки секции конфига <paramref name="section"/> над полями ресурса <typeparamref name="T"/>: изменение на ходу —
    /// <see cref="TuningCommand"/>, его применяет система <c>tuning.&lt;секция&gt;</c> в первой стадии игры. Секция — один раз.
    /// </summary>
    public TuningSection<T> Tuning<T>(string section) where T : unmanaged
    {
        Open();
        if (!IModule.IsValidId(section)) throw new ArgumentException($"секция ручек «{section}»: нужно {IModule.IdRule}", nameof(section));
        if (!_tuningSections.Add(section)) throw new InvalidOperationException($"ручки секции {section} уже заведены");
        if (_tuningSections.Count == 1) Command<TuningCommand>();
        var system = new TuningSystem<T>(section);
        System(Stages[0], system);
        return new TuningSection<T>(this, section, system);
    }

    internal void AddKnob(TuningKnob knob)
    {
        Open();
        if (Knobs.Any(k => k.Id == knob.Id)) throw new InvalidOperationException($"ручка {knob.Name} дважды");
        Knobs.Add(knob);
    }

    /// <summary>Срабатывание уходит клиенту: экспорт тика копирует записи с номером тика (<see cref="Simulation.Export"/>).</summary>
    public SimulationBuilder Export<TTrigger>() where TTrigger : struct
    {
        Open()._export.Register<TTrigger>();
        return this;
    }

    /// <summary>Начальное наполнение мира после сборки (немедленные изменения: уровень, игрок).</summary>
    public SimulationBuilder OnWorldBuilt(Action<World> init)
    {
        Open().WorldInits.Add(init);
        return this;
    }

    /// <summary>Наполнение после всех <see cref="OnWorldBuilt"/> (по порядку регистрации) — когда нужен ресурс, заданный
    /// модулем, например тело бойца по позе. Немедленные изменения до первого тика.</summary>
    public SimulationBuilder OnWorldReady(Action<World> init)
    {
        Open().WorldReady.Add(init);
        return this;
    }
}
