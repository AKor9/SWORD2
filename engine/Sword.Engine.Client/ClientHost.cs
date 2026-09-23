using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Render;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>Как вести клиент.</summary>
public sealed record ClientOptions
{
    public bool Vsync { get; init; }

    /// <summary>Отладочный слой D3D12 (сообщения — в журнал, категория «d3d12»).</summary>
    public bool DebugLayer { get; init; }

    /// <summary>GPU-валидация D3D12 (со слоем отладки): состояния ресурсов из bindless-кучи. Медленно.</summary>
    public bool GpuValidation { get; init; }

    /// <summary>Выйти после стольких кадров; 0 — без ограничения. Для проверочных прогонов и агента.</summary>
    public int MaxFrames { get; init; }

    /// <summary>
    /// Проверочный прогон изменения размера: каждые <see cref="ResizeCycleFrames"/> кадров окно получает следующий
    /// размер из <see cref="ResizeCycleSizes"/>. Проверяет пересоздание задних буферов без рук.
    /// </summary>
    public bool ResizeCycle { get; init; }

    public int ResizeCycleFrames { get; init; } = 20;

    public IReadOnlyList<(int Width, int Height)> ResizeCycleSizes { get; init; } =
        [(1280, 720), (800, 600), (1600, 900), (640, 480)];

    /// <summary>Escape закрывает окно.</summary>
    public bool CloseOnEscape { get; init; } = true;

    /// <summary>
    /// Снять кадр в PNG: путь файла; снимается кадр <see cref="ScreenshotFrame"/>, с бенчем — последний кадр замера.
    /// </summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>Номер снимаемого кадра (с 1); 0 — последний кадр прогона (<see cref="MaxFrames"/>), без него — 30-й.</summary>
    public int ScreenshotFrame { get; init; }

    /// <summary>
    /// Куда класть снимки по действию <see cref="ClientHost.Screenshot"/> (F4): <c>sword-&lt;дата-время&gt;.png</c>;
    /// null — <c>screenshots</c> в текущем каталоге.
    /// </summary>
    public string? ScreenshotDirectory { get; init; }

    /// <summary>
    /// Бенч кадра (<see cref="FrameBench"/>): замер до стольких секунд игрового времени после прогрева, затем выход
    /// (<c>bench</c>) с итогом в <see cref="ClientRunReport.Bench"/>; 0 — без бенча. Вертикальная синхронизация в бенче
    /// выключена (как в SWORD).
    /// </summary>
    public double BenchSeconds { get; init; }

    /// <summary>Каталог шейдеров; по умолчанию — Shaders рядом с exe.</summary>
    public string? ShaderRoot { get; init; }

    /// <summary>
    /// Тиков симуляции на кадр ровно столько (проверочные прогоны, снимки: результат не зависит от скорости машины);
    /// 0 — по часам, накопителем (<c>Program.cs</c> SWORD: шаг кадра до 0,25 с, не больше
    /// <see cref="SimSettings.MaxTicksPerFrame"/> тиков, после упора остаток срезается до одного тика).
    /// </summary>
    public int TicksPerFrame { get; init; }

    /// <summary>
    /// Шаг кадра для накопителя, с, вместо часов (проверочные прогоны «как при N кадрах в секунду»: тики и доля тика
    /// повторяются от прогона к прогону); 0 — по часам. Вместе с <see cref="TicksPerFrame"/> не задаётся.
    /// </summary>
    public double FrameSeconds { get; init; }

    /// <summary>
    /// Не больше стольких кадров в секунду (0 — без ограничения): кадр досыпает до своей доли секунды. Проверка плавности
    /// при низкой и «неровной» частоте кадров вживую (30, 45…).
    /// </summary>
    public double MaxFps { get; init; }

    /// <summary>Привязки действий ввода (секция конфига <c>input</c>); null — клавиши игры по умолчанию.</summary>
    public InputBindings? Bindings { get; init; }

    /// <summary>
    /// Действия, удерживаемые весь прогон, по именам (проверочные прогоны и агент: управление без клавиатуры). Действие,
    /// а не клавиша: привязки не участвуют; в первом кадре действие ещё и «нажато», как клавиша, зажатая с начала.
    /// Неизвестное действие — ошибка; регистр не важен.
    /// </summary>
    public IReadOnlyList<string> HeldActions { get; init; } = [];

    /// <summary>
    /// Профайлер CPU (<see cref="ClientHost.Profiler"/>, точка Па) включён с первого кадра (<c>--profile</c>); с бенчем
    /// итоги профайлера — только кадры замера (после прогрева). Выключенный стоит проверку флага на зону.
    /// </summary>
    public bool Profile { get; init; }

    /// <summary>
    /// Диагностический режим профайлера (<c>--profile runtime</c>, точка Пб): события среды — выделения по типам, сборки,
    /// JIT — с кадром, тиком и зоной (<see cref="CpuProfiler.SetEvents"/>); включает и профайлер. Дорогой: JIT медленнее.
    /// </summary>
    public bool ProfileEvents { get; init; }
}

/// <summary>
/// Итог работы клиента: чем кончилось, сколько кадров и как быстро. Для людей и агента (JSON).
///
/// Бюджет обычного кадра считается только по обычным кадрам. Служебные — <see cref="ServiceFrames"/>: первый
/// (компиляция шейдеров), кадры со сменой размера (ожидание GPU, пересоздание целей, в том числе при перетаскивании
/// рамки) и кадр снимка (ожидание GPU, PNG) — в <see cref="AverageFrameMs"/>, <see cref="MaxFrameMs"/>,
/// <see cref="AllocatedBytesPerFrame"/> и <see cref="Gen0Collections"/> не входят. Кадр — от конца прошлого до конца
/// этого, вместе с прокачкой сообщений. <see cref="AllocatedBytesPerFrame"/> — выделения главного потока (другие потоки
/// процесса не в счёт); <see cref="Gen0Collections"/> — сборки поколения 0, пришедшиеся на обычные кадры (сборка
/// останавливает весь процесс, поэтому считается любая). <see cref="Machine"/> — машина за замер (точка Пб): с бенчем — кадры
/// замера, без него — с конца первого кадра до выхода; загрузка системы другими процессами, частота, GC, JIT и вердикт
/// «код или машина» (кадр ждёт GPU — доля CPU потока в вердикт не идёт). Точка Пг: <see cref="Passes"/> — проходы последнего
/// кадра с GPU-временем того же кадра (после ожидания простоя GPU в конце прогона), <see cref="Gpu"/> — GPU-время этого кадра
/// целиком и сумма проходов. После Пв (решение пользователя): служебные — ещё кадры инструментов (<see cref="ToolFrames"/>:
/// точка обслуживания выполняла запросы агента — выделения и время агента, а не игры); <see cref="DisplayHz"/> — частота
/// монитора окна в конце прогона (0 — неизвестна), по ней профайлер судит «заметность» аномалии кадра.
/// </summary>
public sealed record ClientRunReport(
    string ExitReason,
    string Adapter,
    ulong AdapterVramMb,
    int Frames,
    int ServiceFrames,
    ulong SimTicks,
    int Resizes,
    double Seconds,
    double FirstFrameMs,
    double AverageFrameMs,
    double MaxFrameMs,
    int Width,
    int Height,
    int Pipelines,
    int PooledResources,
    long AllocatedBytesPerFrame,
    int Gen0Collections,
    float Alpha,
    string? Screenshot,
    int Screenshots,
    FrameStats Stats,
    FrameBenchReport? Bench,
    IReadOnlyList<RenderPassSummary> Passes,
    MachineReport? Machine = null,
    GpuFrameReport? Gpu = null,
    int ToolFrames = 0,
    int DisplayHz = 0);

/// <summary>
/// Клиент движка (раздел 7 ARCHITECTURE_PLAN.md): окно → ввод по привязкам (<see cref="FrameInput"/>) → тики симуляции
/// (<see cref="SimulationClock"/>: очередь кадра, накопитель, команды тиков от игры) → мир представления с долей тика
/// (<see cref="FrameView"/>, с числами кадра <see cref="FrameStats"/> для HUD) → кадр (проходы игры, её UI-проход) → показ;
/// снимок по действию <see cref="Screenshot"/> (F4), бенч кадра (<see cref="ClientOptions.BenchSeconds"/>).
/// Окно и симуляцию создаёт exe и отдаёт сюда: клиент знает <see cref="IWindow"/>, API рендера (устройство D3D12 —
/// внутри рендера) и симуляцию; клиентской части игры он отдаёт только <see cref="SimulationLink"/>.
/// </summary>
public sealed class ClientHost : IDisposable
{
    /// <summary>Снимок кадра в <see cref="ClientOptions.ScreenshotDirectory"/> (<c>Program.cs</c> SWORD: F4).</summary>
    public static readonly InputAction Screenshot = new("screenshot", "F4");

    /// <summary>
    /// Действия ввода клиента движка — привязываются вместе с действиями игры (секция <c>input</c>): композиция
    /// разрешает привязки для <c>[.. game.Actions, .. ClientHost.Actions]</c>.
    /// </summary>
    public static IReadOnlyList<InputAction> Actions { get; } = [Screenshot];

    private readonly IWindow _window;
    private readonly ClientOptions _options;
    private readonly Renderer _renderer;
    private readonly IClientGame _game;
    private readonly Action<RenderFrame> _buildFrame;
    private readonly int _screenshotFrame;
    private string? _screenshot;
    private readonly Simulation? _simulation;
    private readonly SimulationClock? _clock;
    private readonly InputBindings _bindings;
    private readonly InputEdges _edges;
    private readonly List<IClientTool> _tools = [];
    private ulong _frame;
    private bool _running;
    private volatile bool _closeRequested;
    private readonly IReadOnlySet<string>? _held;
    private FrameView _view;
    private readonly bool _vsync;
    private readonly FrameStatsMeter _meter = new();
    private int _screenshots;

    /// <summary>Частота монитора окна, отданная профайлеру (0 — неизвестна).</summary>
    private int _displayHz;

    /// <summary>Исключение кадра, нарисованного из системного цикла перетаскивания; бросается в главном цикле.</summary>
    private ExceptionDispatchInfo? _liveResizeError;

    public ClientHost(IWindow window, ClientOptions options, IClientGame game, Simulation? simulation = null)
    {
        if (options.ResizeCycle && (options.ResizeCycleFrames < 1 || options.ResizeCycleSizes.Count == 0))
            throw new ArgumentException("ResizeCycle: нужны ResizeCycleFrames ≥ 1 и хотя бы один размер", nameof(options));
        if (options.TicksPerFrame > 0 && options.FrameSeconds > 0)
            throw new ArgumentException("TicksPerFrame и FrameSeconds вместе не задаются", nameof(options));
        if (options.BenchSeconds != 0 && !(options.BenchSeconds > FrameBench.Warmup && double.IsFinite(options.BenchSeconds)))
            throw new ArgumentException($"бенч — больше {FrameBench.Warmup} с игрового времени (прогрев), задано {options.BenchSeconds}", nameof(options));
        if (options.BenchSeconds > 0 && simulation is null)
            throw new ArgumentException("бенч идёт по игровому времени: нужна симуляция", nameof(options));
        if (options.BenchSeconds > 0 && options.MaxFps > 0)
            throw new ArgumentException("бенч и ограничитель кадров вместе не задаются: ограничитель исказил бы замер", nameof(options));
        IReadOnlyList<InputAction> actions = [.. game.Actions, .. Actions];
        _bindings = options.Bindings ?? InputBindings.Resolve(actions, null);
        foreach (var a in actions)
            if (!_bindings.Has(a.Name)) throw new ArgumentException($"привязки не знают действия {a.Name}", nameof(options));
        // Имена действий — строчные (правило ключей конфига): регистр в --hold не важен, как и в конфиге.
        var held = options.HeldActions.Select(n => n.ToLowerInvariant()).ToList();
        foreach (var name in held)
            if (!actions.Any(a => a.Name == name))
                throw new ArgumentException($"удерживаемое действие {name}: такого нет; есть: {string.Join(", ", actions.Select(a => a.Name))}");
        _held = held.Count > 0 ? new HashSet<string>(held, StringComparer.Ordinal) : null;
        if (options.CloseOnEscape && _bindings.ActionsOn(Key.Escape).FirstOrDefault() is { } onEscape)
            throw new ArgumentException($"действие {onEscape} привязано к Escape, а Escape закрывает окно (CloseOnEscape)", nameof(options));
        _window = window;
        _options = options;
        _vsync = options.Vsync && options.BenchSeconds <= 0;
        _view = new FrameView(1, 0, 0, _meter.Stats(0, _vsync));
        _game = game;
        _simulation = simulation;
        _edges = new InputEdges(_bindings);
        _clock = simulation is null ? null
            : new SimulationClock(simulation, (link, input) => game.BeforeTick(link, input), options.TicksPerFrame, _edges);
        _buildFrame = game.BuildFrame;
        _screenshotFrame = options.ScreenshotFrame > 0 ? options.ScreenshotFrame : options.MaxFrames > 0 ? options.MaxFrames : 30;
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        game.ContributeShaders(contract);
        _renderer = new Renderer(new RendererOptions(window.NativeHandle, window.Width, window.Height,
            options.DebugLayer || options.GpuValidation, options.ShaderRoot ?? Path.Combine(AppContext.BaseDirectory, "Shaders"),
            options.GpuValidation), contract);
        // Профайлер CPU (точка Па) — один на поток кадра: фазы кадра, тики симуляции, проходы графа, зоны игры. Рендеру —
        // до Initialize: игра заводит свои зоны в нём же.
        _zones = new FrameZones(Profiler);
        _renderer.Profiler = Profiler;
        simulation?.AttachProfiler(Profiler);
        if (options.Profile) Profiler.SetEnabled(true);
        if (options.ProfileEvents) Profiler.SetEvents(true);
        // После Пв: интервал показа — частота монитора окна (порог «заметной» аномалии кадра); окно перенесли — обновляется в кадре.
        _displayHz = window.RefreshHz;
        Profiler.DisplayHz = _displayHz;
        MachineSample.Take();   // первый снимок машины — здесь: его разовая компиляция и статика — не в кадрах Run
        try
        {
            game.Initialize(_renderer, _clock?.Link);
        }
        catch
        {
            // Игра могла успеть создать часть ресурсов GPU: они освобождаются раньше рендера (он ждёт GPU).
            _renderer.WaitForIdle();
            game.Dispose();
            _renderer.Dispose();
            throw;
        }
        _window.LiveResize += OnLiveResize;
        Log.Info("input", "привязки: " + string.Join(", ", _bindings.Keys.Select(p => $"{p.Key}={p.Value}")));
        Log.Info("client", $"{_renderer.AdapterName}, {_renderer.AdapterVramMb} МБ, {window.Width}×{window.Height}, " +
            $"вертикальная синхронизация {(_vsync ? "вкл" : "выкл")}, монитор {(_displayHz > 0 ? $"{_displayHz} Гц" : "— Гц (неизвестно)")}" +
            (options.GpuValidation ? ", отладочный слой D3D12 с GPU-валидацией" : options.DebugLayer ? ", отладочный слой D3D12" : ""));
    }

    /// <summary>Главный цикл до закрытия окна, Escape или <see cref="ClientOptions.MaxFrames"/>.</summary>
    /// <remarks>
    /// Сразу оптимизированным: иначе многоуровневая компиляция через ~4 с заменяет долгий цикл на ходу (OSR) — разовый
    /// кадр 6–8 мс между кадрами (найдено профайлером, точка Па; события JIT подтвердили компиляцию OSR этого метода).
    /// Сам метод — тонкий, из вызовов: динамический PGO ему не нужен.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public ClientRunReport Run()
    {
        _running = true;
        var total = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp(), lastSim = last;
        long lastAllocated = GC.GetAllocatedBytesForCurrentThread();
        int lastGen0 = GC.CollectionCount(0), lastResizes = _resizes;
        int frames = 0, serviceFrames = 0, toolFrames = 0, cycle = 0;
        double firstFrameMs = 0, frameMsSum = 0, frameMsMax = 0;
        long allocatedSum = 0;
        int gen0Sum = 0;
        string reason = "closed";
        var bench = _options.BenchSeconds > 0 ? new FrameBench(_options.BenchSeconds) : null;
        var profiler = Profiler;
        bool profileMeasuring = false;
        // Окно машины (точка Пб): границы замера — не каждый кадр. С бенчем — с первого кадра замера, без него — с конца первого.
        MachineSample? machineStart = null;
        bool takeMachine = false;

        while (true)
        {
            // Кадр профайлера — от прокачки сообщений до конца кадра; включение профайлера вступает в силу здесь.
            profiler.BeginFrame(_frame + 1);
            profiler.Begin(_zones.Messages);
            bool alive = _window.PumpMessages();
            profiler.End(_zones.Messages);
            if (!alive) break;
            _liveResizeError?.Throw();
            if (_closeRequested)
            {
                reason = "closed-by-tool";
                break;
            }
            if (_options.CloseOnEscape && _window.Input.WasPressed(Key.Escape))
            {
                reason = "escape";
                break;
            }

            if (_window.Minimized)
            {
                // Свёрнутое окно не рисуется: DXGI ответил бы «перекрыто», а цикл крутился бы впустую. Инструменты
                // обслуживаются и так — агент не ждёт, пока окно развернут. Кадра нет — и у профайлера.
                profiler.AbandonFrame();
                ServeTools();
                Thread.Sleep(16);
                last = lastSim = Stopwatch.GetTimestamp();
                lastAllocated = GC.GetAllocatedBytesForCurrentThread();
                lastGen0 = GC.CollectionCount(0);
                continue;
            }

            if (_options.ResizeCycle && frames > 0 && frames % _options.ResizeCycleFrames == 0)
            {
                var (w, h) = _options.ResizeCycleSizes[cycle++ % _options.ResizeCycleSizes.Count];
                _window.SetClientSize(w, h);
            }

            profiler.Begin(_zones.Resize);
            ResizeIfNeeded();
            profiler.End(_zones.Resize);
            long simNow = Stopwatch.GetTimestamp();
            long cpuStart = simNow;
            double seconds = _options.FrameSeconds > 0 ? _options.FrameSeconds : Stopwatch.GetElapsedTime(lastSim, simNow).TotalSeconds;
            lastSim = simNow;
            // Кадр (раздел 7): ввод и камера → тики с командами → мир представления → проходы.
            profiler.Begin(_zones.Input);
            float frameSeconds = _clock?.FrameSeconds(seconds) ?? (float)Math.Max(seconds, 0);
            var input = new FrameInput(_window.Input, _bindings, _held, heldPressed: frames == 0, _window.MouseInside,
                _renderer.Width, _renderer.Height, frameSeconds);
            _game.Input(input);
            _edges.Note(input);   // фронты кадра — до ближайшего тика (раздел 7)
            profiler.End(_zones.Input);
            profiler.Begin(_zones.Simulate);
            int ticks = _clock?.RunFrame(seconds) ?? 0;
            profiler.End(_zones.Simulate);
            profiler.Begin(_zones.Tools);
            // Точка обслуживания инструментов: тики кадра сыграны, мир согласован (и на паузе). Выполнены запросы агента — кадр
            // инструментов (после Пв): служебный — не в среднем и бюджете кадра, профайлер не судит его как кадр.
            bool toolWork = ServeTools();
            profiler.End(_zones.Tools);
            _view = new FrameView(_clock?.Alpha ?? 1, ticks, frameSeconds, _meter.Stats(ticks, _vsync));
            profiler.Begin(_zones.Extract);
            _game.Extract(_view);
            profiler.End(_zones.Extract);
            double simTime = _clock is { } clock ? clock.Link.Tick * (double)clock.Link.Dt : 0;
            // Последний кадр замера — по игровому времени после тиков кадра (BenchRecorder.Done SWORD).
            bool lastBenchFrame = bench?.Done(simTime) == true;
            string? shotPath = null;
            if (_options.ScreenshotPath is not null && (bench is not null ? lastBenchFrame : frames + 1 == _screenshotFrame))
                shotPath = _options.ScreenshotPath;
            else if (bench is null && input.WasPressed(Screenshot))   // в бенче F4 нет (SWORD: !bench && F4)
                shotPath = ScreenshotFile();
            double preRenderMs = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
            profiler.Begin(_zones.Render);
            RenderFrame(shotPath, required: shotPath == _options.ScreenshotPath, tools: true);
            profiler.End(_zones.Render);
            double cpuMs = preRenderMs + _renderer.LastRecordMs;
            if (_options.MaxFps > 0)
            {
                profiler.Begin(_zones.Wait);
                WaitForFrameSlot(last, 1 / _options.MaxFps);
                profiler.End(_zones.Wait);
            }

            long now = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0);
            double ms = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            // Служебный кадр: первый, со сменой размера с конца прошлого кадра (и из цикла перетаскивания рамки), снимок; после Пв —
            // и кадр инструментов (профайлеру — отдельной пометкой: в кольце видно, почему кадр не судился).
            bool service = frames == 0 || _resizes != lastResizes || shotPath is not null || _toolCaptured;
            if (_window.RefreshHz != _displayHz)
            {
                _displayHz = _window.RefreshHz;   // окно перенесли на монитор с другой частотой (поле окна — без вызовов ОС)
                profiler.DisplayHz = _displayHz;
            }
            // Бенч: итоги профайлера — кадры замера (первый кадр после прогрева — первый в итогах).
            if (bench is not null && !profileMeasuring && FrameBench.Measuring(simTime))
            {
                profiler.Reset();
                profileMeasuring = true;
                takeMachine = true;
            }
            if (bench is null && frames == 0) takeMachine = true;
            profiler.EndFrame(_view.Alpha, service || lastBenchFrame, toolWork);
            if (toolWork)
            {
                toolFrames++;
                service = true;
            }
            var gpu = _renderer.Gpu;   // точка Пг: GPU-время последнего разобранного кадра (на 3 раньше этого)
            _meter.Frame(ms, cpuMs, _renderer.LastFenceWaitMs, gpu.Valid ? gpu.FrameMs : 0, gpu.Valid ? gpu.PassesMs : 0);
            // Последний кадр замера (со снимком — ожидание GPU и PNG) в замер не входит: SWORD копит шаг кадра, измеренный
            // в начале следующего, и после последнего кадра выходит — его шаг не попадает в итог.
            if (!lastBenchFrame && bench is not null)
            {
                bench.Frame(simTime, ms, cpuMs, _renderer.LastFenceWaitMs);
                BenchGpu(bench, simTime);
            }
            if (frames == 0) firstFrameMs = ms;
            if (service)
                serviceFrames++;
            else
            {
                frameMsSum += ms;
                frameMsMax = Math.Max(frameMsMax, ms);
                allocatedSum += allocated - lastAllocated;
                gen0Sum += gen0 - lastGen0;
            }
            last = now;
            lastAllocated = allocated;
            lastGen0 = gen0;
            lastResizes = _resizes;
            frames++;
            if (takeMachine)
            {
                // Снимок — после отметок кадра и вне кадров: его ~50 мкс (проба частоты) не идут ни в шаг, ни в байты кадра.
                machineStart = MachineSample.Take();
                takeMachine = false;
                last = Stopwatch.GetTimestamp();
                lastAllocated = GC.GetAllocatedBytesForCurrentThread();
            }

            if (lastBenchFrame)
            {
                reason = "bench";
                break;
            }
            if (_options.MaxFrames > 0 && frames >= _options.MaxFrames)
            {
                reason = "max-frames";
                break;
            }
        }
        profiler.AbandonFrame();   // выход посреди кадра (окно закрыто, Escape, инструмент): кадр не доделан — не в итогах
        var machine = machineStart is { } ms0 ? MachineReport.Between(ms0, MachineSample.Take(), computeBound: false) : null;
        var benchReport = bench?.Result();
        if (benchReport is not null)
        {
            Log.Info("bench", benchReport.ToString());
            // Точка Пг: проходы GPU — мс на кадр, как «[bench]   проход  мс/кадр» BenchRecorder SWORD.
            foreach (var pass in benchReport.Passes ?? [])
                Log.Info("bench", $"  {pass.Name,-22} {pass.Ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} мс/кадр GPU");
        }
        // «вердикт: машина: …» / «вердикт: код: …» — без повтора слова «машина» (было «машина: машина: …», доводка Пб на Пв).
        if (benchReport is not null && machine is not null) Log.Info("bench", "вердикт: " + machine.Text);

        _liveResizeError?.Throw();
        // Точка Пг: GPU доделал отправленное — времена последнего кадра разобраны, проходы итога — с GPU-временем того же кадра.
        _renderer.WaitForIdle();
        int steady = frames - serviceFrames;
        return new ClientRunReport(
            ExitReason: reason,
            Adapter: _renderer.AdapterName,
            AdapterVramMb: _renderer.AdapterVramMb,
            Frames: frames,
            ServiceFrames: serviceFrames,
            SimTicks: _simulation?.Tick ?? 0,
            Resizes: _resizes,
            Seconds: Math.Round(total.Elapsed.TotalSeconds, 3),
            FirstFrameMs: Math.Round(firstFrameMs, 3),
            AverageFrameMs: steady > 0 ? Math.Round(frameMsSum / steady, 3) : 0,
            MaxFrameMs: Math.Round(frameMsMax, 3),
            Width: _renderer.Width,
            Height: _renderer.Height,
            Pipelines: _renderer.PipelineCount,
            PooledResources: _renderer.Graph.PooledResources,
            AllocatedBytesPerFrame: steady > 0 ? allocatedSum / steady : 0,
            Gen0Collections: gen0Sum,
            Alpha: _view.Alpha,
            Screenshot: _screenshot,
            Screenshots: _screenshots,
            Stats: _meter.Stats(_view.Ticks, _vsync),
            Bench: benchReport,
            Passes: _renderer.Gpu.Join(_renderer.Graph.LastFrame),
            Machine: machine,
            Gpu: _renderer.Gpu.Report(),
            ToolFrames: toolFrames,
            DisplayHz: _displayHz);
    }

    private long _benchGpu;

    /// <summary>Новые GPU-времена (раз на разобранный кадр, точка Пг) — в бенч: кадр целиком и проходы. 0 байт в установившемся режиме.</summary>
    private void BenchGpu(FrameBench bench, double simTime)
    {
        var gpu = _renderer.Gpu;
        if (!gpu.Valid || gpu.Sequence == _benchGpu) return;
        _benchGpu = gpu.Sequence;
        bench.GpuFrame(simTime, gpu.FrameMs);
        for (int i = 0; i < gpu.Count; i++) bench.GpuPass(gpu.NameOf(i), gpu.MillisecondsOf(i));
    }

    private int _resizes;

    /// <summary>
    /// Размер сверяется с задним буфером, а не по флагу Resized: флаг живёт до следующей прокачки сообщений, а размер
    /// могли поменять и вне её (SetClientSize присылает WM_SIZE прямо внутри вызова).
    /// </summary>
    private void ResizeIfNeeded()
    {
        if (_window.Width == _renderer.Width && _window.Height == _renderer.Height) return;
        _renderer.Resize(_window.Width, _window.Height);
        _resizes++;
        Log.Info("client", $"размер {_window.Width}×{_window.Height}");
    }

    /// <summary>
    /// Ограничитель кадров: досыпает до доли секунды кадра. Сон в Windows идёт шагом системного таймера (~15,6 мс),
    /// поэтому спим, только пока до конца кадра больше 20 мс, остаток — ожидание с уступкой процессора.
    /// </summary>
    private static void WaitForFrameSlot(long frameStart, double seconds)
    {
        while (true)
        {
            double left = seconds - Stopwatch.GetElapsedTime(frameStart).TotalSeconds;
            if (left <= 0) return;
            if (left > 0.020) Thread.Sleep(TimeSpan.FromSeconds(left - 0.017));
            else Thread.Yield();
        }
    }

    /// <param name="required">
    /// Снимок заказан прогоном (<see cref="ClientOptions.ScreenshotPath"/>): не записался — ошибка прогона. Снимок по
    /// F4 не записался — ошибка в журнал, игра идёт дальше.
    /// </param>
    /// <param name="tools">Кадр главного цикла: инструменты могут заказать снимок. Итог кадра они получают всегда.</param>
    private void RenderFrame(string? screenshot = null, bool required = true, bool tools = false)
    {
        _frame++;
        _toolCaptured = false;
        // Снимок инструмента — в кадре без своего снимка (прогона или F4): инструмент просит, пока не получит.
        FrameCapture? toolCapture = null;
        IClientTool? capturer = null;
        if (tools && screenshot is null)
            foreach (var t in _tools)
                if (t.Capture(_frame) is { } c)
                {
                    toolCapture = c;
                    capturer = t;
                    break;
                }
        var capture = screenshot is not null ? new FrameCapture(screenshot) : toolCapture;
        bool captured = true;
        try
        {
            _renderer.Render(_buildFrame, _vsync, capture);
        }
        catch (CaptureException e) when (!required || toolCapture is not null)
        {
            Log.Error("client", e.Message);
            captured = false;
        }
        if (_simulation is not null && _tools.Count > 0)   // итог — каждого кадра: инструмент видит граф и тик показанного
        {
            Profiler.Begin(_zones.Presented);
            foreach (var t in _tools)
                t.Presented(_simulation, _frame, t == capturer ? toolCapture : null,
                    t == capturer && captured ? _renderer.LastCapture : null);
            Profiler.End(_zones.Presented);
        }
        _toolCaptured = toolCapture is not null;
        if (screenshot is not null && captured)
        {
            _screenshots++;
            _screenshot = Path.GetFullPath(screenshot);
            Log.Info("client", $"снимок {_screenshot}");
        }
    }

    private bool _toolCaptured;

    /// <summary>Точка обслуживания инструментов; истина — хоть один выполнил работу (кадр инструментов, после Пв).</summary>
    private bool ServeTools()
    {
        if (_simulation is null) return false;
        bool work = false;
        foreach (var t in _tools) work |= t.Serve(_simulation, _frame + 1);
        return work;
    }

    /// <summary>
    /// Подключить инструмент (агент, DevTools) — до <see cref="Run"/>: он обслуживается каждый кадр на потоке кадра.
    /// </summary>
    public void AddTool(IClientTool tool)
    {
        if (_running) throw new InvalidOperationException("инструменты подключаются до Run");
        _tools.Add(tool);
    }

    /// <summary>Ворота тиков (пауза и шаги инструмента); null — тики по часам. Нужна симуляция.</summary>
    public ITickGate? TickGate
    {
        get => _clock?.Gate;
        set => (_clock ?? throw new InvalidOperationException("ворота тиков без симуляции")).Gate = value;
    }

    /// <summary>Рендер клиента — для инструментов (граф проходов, снимки).</summary>
    public Renderer Renderer => _renderer;

    /// <summary>
    /// Профайлер CPU потока кадра (точка Па): кадр — период (зона <c>frame</c>) с фазами <c>frame.messages</c>,
    /// <c>frame.resize</c>, <c>frame.input</c>, <c>frame.simulate</c> (в нём тики — зона <c>tick</c>, стадии, системы,
    /// инварианты), <c>frame.tools</c>, <c>frame.extract</c> (зоны игры), <c>frame.render</c> (зоны рендера и
    /// проходов графа, <c>frame.presented</c> — итог кадра инструментам), <c>frame.wait</c> (ограничитель кадров). Включают
    /// <see cref="ClientOptions.Profile"/> и инструмент агента (<c>profile.stages</c>) — с начала следующего кадра.
    /// </summary>
    public CpuProfiler Profiler { get; } = new();

    private readonly FrameZones _zones;

    /// <summary>Номера зон фаз кадра — заведены при создании клиента.</summary>
    private sealed class FrameZones(CpuProfiler p)
    {
        public readonly int Messages = p.Zone("frame.messages"), Resize = p.Zone("frame.resize"), Input = p.Zone("frame.input"),
            Simulate = p.Zone("frame.simulate"), Tools = p.Zone("frame.tools"), Extract = p.Zone("frame.extract"),
            Render = p.Zone("frame.render"), Presented = p.Zone("frame.presented"), Wait = p.Zone("frame.wait", ProfileZoneKind.Idle);
    }

    /// <summary>Клиентская часть игры — для инструментов (наблюдаемость: <see cref="IClientObservable"/>).</summary>
    public IClientGame Game => _game;

    /// <summary>Номер последнего нарисованного кадра (с 1).</summary>
    public ulong Frame => _frame;

    /// <summary>Доля тика последнего кадра (1 — кадр показывает ровно последний тик).</summary>
    public float Alpha => _view.Alpha;

    /// <summary>Закончить <see cref="Run"/> после текущего кадра (с любого потока: инструмент, агент).</summary>
    public void RequestClose() => _closeRequested = true;

    /// <summary>Файл снимка F4: <c>sword-ггггММдд-ччммсс-мсм.png</c> (местное время, инвариантный календарь); занят — с номером.</summary>
    private string ScreenshotFile()
    {
        string dir = _options.ScreenshotDirectory ?? Path.Combine(Environment.CurrentDirectory, "screenshots");
        string stem = "sword-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, stem + ".png");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}-{i}.png");
        return path;
    }

    /// <summary>
    /// Край окна тянут прямо сейчас: главный цикл стоит внутри системного цикла перетаскивания, поэтому кадр под новый
    /// размер рисуется отсюда — иначе до отпускания мыши картинка замерла бы.
    ///
    /// Исключение отсюда не должно пройти через оконную процедуру (из неё управляемое исключение не раскручивается,
    /// процесс упал бы без итога и очистки): оно запоминается и бросается главным циклом, как только тот вернёт
    /// управление. Кадров после ошибки не рисуется.
    /// </summary>
    private void OnLiveResize()
    {
        if (_window.Minimized || _liveResizeError is not null) return;
        // Кадр рисуется внутри зоны frame.messages: зоны, брошенные исключением, снимаются здесь — иначе конец frame.messages
        // в главном цикле (Debug) бросил бы ошибку профайлера вместо запомненной.
        int profileDepth = Profiler.Depth;
        try
        {
            ResizeIfNeeded();
            // Кадр без тиков: очередь кадра пуста — события прошлого кадра не обрабатываются второй раз.
            _clock?.EmptyFrame();
            ServeTools();   // тянут рамку — главный цикл стоит, инструменты обслуживаются отсюда
            _game.Extract(_view with { Ticks = 0, Seconds = 0, Stats = _view.Stats with { Ticks = 0 } });
            RenderFrame();
        }
        catch (Exception ex)
        {
            Profiler.UnwindTo(profileDepth);
            _liveResizeError = ExceptionDispatchInfo.Capture(ex);
        }
    }

    public void Dispose()
    {
        _window.LiveResize -= OnLiveResize;
        // Ресурсы кадра освобождаются только после того, как GPU их отпустил: сначала ожидание, затем сцена игры,
        // затем рендер с устройством. Ожидание может упасть (потеря устройства) — освобождение всё равно идёт.
        try
        {
            _renderer.WaitForIdle();
        }
        finally
        {
            try
            {
                _game.Dispose();
            }
            finally
            {
                _renderer.Dispose();
            }
        }
    }
}
