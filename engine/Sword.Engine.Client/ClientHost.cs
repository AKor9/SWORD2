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

    /// <summary>Выйти после стольких кадров; 0 — без ограничения. Для проверочных прогонов.</summary>
    public int MaxFrames { get; init; }

    /// <summary>Проверочный прогон: каждые <see cref="ResizeCycleFrames"/> кадров окно получает следующий размер из
    /// <see cref="ResizeCycleSizes"/> — пересоздание задних буферов без рук.</summary>
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

    /// <summary>Каталог снимков F4 (<c>sword-&lt;дата-время&gt;.png</c>); null — <c>screenshots</c> в текущем каталоге.</summary>
    public string? ScreenshotDirectory { get; init; }

    /// <summary>Бенч кадра (<see cref="FrameBench"/>): секунд игрового времени после прогрева, затем выход с итогом в
    /// <see cref="ClientRunReport.Bench"/>; 0 — без бенча. В бенче vsync выключен.</summary>
    public double BenchSeconds { get; init; }

    /// <summary>Каталог шейдеров; по умолчанию — Shaders рядом с exe.</summary>
    public string? ShaderRoot { get; init; }

    /// <summary>
    /// Тиков на кадр ровно столько (результат не зависит от скорости машины); 0 — по часам, накопителем: шаг кадра до
    /// 0,25 с, не больше <see cref="SimSettings.MaxTicksPerFrame"/> тиков, после упора остаток срезается до одного тика.
    /// </summary>
    public int TicksPerFrame { get; init; }

    /// <summary>Шаг кадра для накопителя вместо часов, с (тики и доля тика повторяются от прогона к прогону); 0 — по часам.
    /// Вместе с <see cref="TicksPerFrame"/> не задаётся.</summary>
    public double FrameSeconds { get; init; }

    /// <summary>Предел кадров в секунду (0 — нет): кадр досыпает до своей доли секунды — проверка плавности при низкой
    /// частоте.</summary>
    public double MaxFps { get; init; }

    /// <summary>Привязки действий ввода (секция конфига <c>input</c>); null — клавиши игры по умолчанию.</summary>
    public InputBindings? Bindings { get; init; }

    /// <summary>
    /// Действия, удерживаемые весь прогон (управление без клавиатуры), по именам без учёта регистра; в первом кадре
    /// действие ещё и «нажато». Неизвестное — ошибка.
    /// </summary>
    public IReadOnlyList<string> HeldActions { get; init; } = [];

    /// <summary>Профайлер CPU включён с первого кадра (<c>--profile</c>); с бенчем итоги — только кадры замера.</summary>
    public bool Profile { get; init; }

    /// <summary>Сторож кадров: кадров нет дольше стольких секунд — <see cref="Stalled"/>; 0 — без сторожа. Чтобы
    /// проверочные прогоны не висели без конца.</summary>
    public double StallSeconds { get; init; }

    /// <summary>Что делать, когда сторож сработал (из потока таймера): exe пишет итог-ошибку и выходит.</summary>
    public Action<string>? Stalled { get; init; }
}

/// <summary>
/// Итог работы клиента (JSON). Служебные кадры (<see cref="ServiceFrames"/>: первый, до первого тика включительно,
/// смена размера, снимок) в средние, максимум, выделения и сборки не входят. Кадр — от конца прошлого до конца этого.
/// <see cref="AllocatedBytesPerFrame"/> — только главный поток; <see cref="Gen0Collections"/> — любые сборки за обычные
/// кадры (сборка останавливает процесс). <see cref="Passes"/> и <see cref="Gpu"/> — последний кадр после простоя GPU.
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
    GpuFrameReport? Gpu = null,
    int DisplayHz = 0,
    int MinimizedFrames = 0,
    ClientHdrReport? Hdr = null,
    ClientLightsReport? Lights = null,
    IReadOnlyList<ClientTextureReport>? Textures = null,
    IReadOnlyList<ClientModuleReport>? Modules = null);

/// <summary>Текстура пака в видеопамяти (<see cref="Renderer.Textures"/>): путь, размер, слои, мипы, формат, sRGB, байт видеопамяти.</summary>
public sealed record ClientTextureReport(string Path, int Width, int Height, int Slices, int Mips, GpuTextureFormat Format, bool Srgb, long Bytes)
{
    public static ClientTextureReport From(GpuTexture t) => new(t.Path, t.Width, t.Height, t.Slices, t.Mips, t.Format, t.Srgb, t.Bytes);
}

/// <summary>
/// Точечный свет последнего кадра: показано (до <see cref="Max"/>), отброшено сверх предела, скрыто дальностью, light
/// functions; у показанного — параметры, угасание (<see cref="ClientLightReport.Fade"/>) и индекс LF (−1 — нет).
/// </summary>
public sealed record ClientLightsReport(int Shown, int Dropped, int Hidden, int Functions, int DroppedFunctions, int Max,
    IReadOnlyList<ClientLightReport> Lights)
{
    public static ClientLightsReport From(PointLightsReport r) => new(r.Shown, r.Dropped, r.Hidden, r.Functions, r.DroppedFunctions,
        PointLights.MaxLights, [.. r.Lights.Select(ClientLightReport.From)]);
}

/// <summary>Источник кадра: позиция (м), линейный цвет, интенсивность, радиус, затухание, дальность и угасание показа,
/// LF, множитель рассеяния в объёмах.</summary>
public sealed record ClientLightReport(float[] Position, float[] Color, float Intensity, float Radius, LightFalloff Falloff, float Exponent,
    float MaxDrawDistance, float FadeRange, float Fade, int Function, float VolumetricScattering)
{
    public static ClientLightReport From(ShownPointLight s)
    {
        var l = s.Light;
        return new([l.Position.X, l.Position.Y, l.Position.Z], [l.Color.X, l.Color.Y, l.Color.Z], l.Intensity, l.Radius, l.Falloff,
            l.Exponent, l.MaxDrawDistance, l.FadeRange, s.Fade, l.Function, l.VolumetricScattering);
    }
}

/// <summary>HDR в конце прогона: включён ли, экспозиция, bloom и — если прогон снимал кадр (<c>--shot</c>) с HDR —
/// замеры на нём (<see cref="HdrCapture"/>).</summary>
public sealed record ClientHdrReport(bool Enabled, float Exposure, BloomSettings Bloom, HdrCapture? Shot);

/// <summary>
/// Клиент движка: окно → ввод (<see cref="FrameInput"/>) → тики (<see cref="SimulationClock"/>) → мир представления
/// с долей тика (<see cref="FrameView"/>) → кадр → показ; панели (<see cref="AddPanel"/>) — до ввода игры, их квады —
/// после игры. Окно и симуляцию создаёт exe; клиентской части игры отдаётся только <see cref="SimulationLink"/>.
/// </summary>
public sealed class ClientHost : IDisposable
{
    /// <summary>Снимок кадра в <see cref="ClientOptions.ScreenshotDirectory"/>.</summary>
    public static readonly InputAction Screenshot = new("screenshot", "F4");

    /// <summary>Вертикальная синхронизация: включить / выключить на ходу (<see cref="Vsync"/>; в бенче не действует).</summary>
    public static readonly InputAction VsyncToggle = new("vsync", "V");

    /// <summary>Действия клиента движка — привязываются вместе с действиями игры (секция <c>input</c>).</summary>
    public static IReadOnlyList<InputAction> Actions { get; } = [Screenshot, VsyncToggle];

    private readonly IWindow _window;
    private readonly ClientOptions _options;
    private readonly Renderer _renderer;
    private readonly IClientGame _game;
    private readonly ClientModules _modules;
    private readonly Action<RenderFrame> _buildFrame;
    private readonly int _screenshotFrame;
    private string? _screenshot;

    /// <summary>HDR-сводка последнего снимка прогона (<see cref="ClientOptions.ScreenshotPath"/>); null — не снимали или без HDR.</summary>
    private HdrCapture? _screenshotHdr;
    private readonly Simulation? _simulation;
    private readonly SimulationClock? _clock;
    private readonly InputBindings _bindings;
    private readonly InputEdges _edges;
    private ulong _frame;
    private bool _running;
    private readonly IReadOnlySet<string>? _held;
    private FrameView _view;
    private bool _vsync;
    private readonly FrameStatsMeter _meter = new();
    private int _screenshots;

    /// <summary>Частота монитора окна (0 — неизвестна).</summary>
    private int _displayHz;

    /// <summary>Исключение кадра, нарисованного из системного цикла перетаскивания; бросается в главном цикле.</summary>
    private ExceptionDispatchInfo? _liveResizeError;

    /// <summary>Проверочный прогон рисует и в свёрнутом окне: иначе кадров нет, а бенч считает игровое время и висел бы
    /// без конца. Живая игра в свёрнутом окне ждёт.</summary>
    private readonly bool _drawMinimized;

    private readonly FrameWatchdog? _watchdog;

    /// <param name="modules">Клиентские модули (<see cref="ClientModules.Compose"/>): наблюдатель после тика, извлечение
    /// после игры, проходы после проходов игры; null — без модулей.</param>
    public ClientHost(IWindow window, ClientOptions options, IClientGame game, Simulation? simulation = null, ClientModules? modules = null)
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
        // Имена действий — строчные (правило ключей конфига): регистр в --hold не важен, как и в конфиге. Удерживать можно любое
        // привязанное действие — и действие панели (--hold tuning открывает панель ручек: в первом кадре действие ещё и нажато).
        var held = options.HeldActions.Select(n => n.ToLowerInvariant()).ToList();
        foreach (var name in held)
            if (!_bindings.Has(name))
                throw new ArgumentException($"удерживаемое действие {name}: такого нет; есть: {string.Join(", ", _bindings.Actions.Select(a => a.Name))}");
        _held = held.Count > 0 ? new HashSet<string>(held, StringComparer.Ordinal) : null;
        if (options.CloseOnEscape && _bindings.ActionsOn(Key.Escape).FirstOrDefault() is { } onEscape)
            throw new ArgumentException($"действие {onEscape} привязано к Escape, а Escape закрывает окно (CloseOnEscape)", nameof(options));
        if (options.StallSeconds < 0 || !double.IsFinite(options.StallSeconds))
            throw new ArgumentException($"StallSeconds — не меньше 0, задано {options.StallSeconds}", nameof(options));
        _window = window;
        _options = options;
        _drawMinimized = options.MaxFrames > 0 || options.BenchSeconds > 0;
        _vsync = options.Vsync && options.BenchSeconds <= 0;
        _view = new FrameView(1, 0, 0, _meter.Stats(0, _vsync));
        _game = game;
        _simulation = simulation;
        _modules = modules ?? ClientModules.None(simulation?.Dt ?? 1f / 120);
        _edges = new InputEdges(_bindings);
        _clock = simulation is null ? null
            : new SimulationClock(simulation, (link, input) => game.BeforeTick(link, input), options.TicksPerFrame, _edges, _modules.AfterTick);
        _buildFrame = BuildFrame;
        _screenshotFrame = options.ScreenshotFrame > 0 ? options.ScreenshotFrame : options.MaxFrames > 0 ? options.MaxFrames : 30;
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        game.ContributeShaders(contract);
        _modules.ContributeShaders(contract);
        _renderer = new Renderer(new RendererOptions(window.NativeHandle, window.Width, window.Height,
            options.DebugLayer || options.GpuValidation, options.ShaderRoot ?? Path.Combine(AppContext.BaseDirectory, "Shaders"),
            options.GpuValidation), contract);
        // Профайлер CPU — один на поток кадра: фазы кадра, тики симуляции, проходы графа, зоны игры. Рендеру —
        // до Initialize: игра заводит свои зоны в нём же.
        _zones = new FrameZones(Profiler);
        _renderer.Profiler = Profiler;
        simulation?.AttachProfiler(Profiler);
        if (options.Profile) Profiler.SetEnabled(true);
        _displayHz = window.RefreshHz;
        try
        {
            game.Initialize(_renderer, _clock?.Link);
            // Модули — после игры: их проходы идут после проходов игры и видят её камеру.
            _modules.Initialize(_renderer, ModuleCamera);
        }
        catch
        {
            // Игра и модули могли успеть создать часть ресурсов GPU: они освобождаются раньше рендера (он ждёт GPU).
            _renderer.WaitForIdle();
            _modules.Dispose();
            game.Dispose();
            _renderer.Dispose();
            throw;
        }
        _window.LiveResize += OnLiveResize;
        if (options.StallSeconds > 0)
            _watchdog = new FrameWatchdog(options.StallSeconds, () => _window.Minimized, message =>
            {
                Log.Error("client", message);
                options.Stalled?.Invoke(message);
            });
        Log.Info("input", "привязки: " + string.Join(", ", _bindings.Keys.Select(p => $"{p.Key}={p.Value}")));
        Log.Info("client", $"{_renderer.AdapterName}, {_renderer.AdapterVramMb} МБ, {window.Width}×{window.Height}, " +
            $"вертикальная синхронизация {(_vsync ? "вкл" : "выкл")}, монитор {(_displayHz > 0 ? $"{_displayHz} Гц" : "— Гц (неизвестно)")}" +
            (options.GpuValidation ? ", отладочный слой D3D12 с GPU-валидацией" : options.DebugLayer ? ", отладочный слой D3D12" : ""));
    }

    /// <summary>Главный цикл до закрытия окна, Escape или <see cref="ClientOptions.MaxFrames"/>.</summary>
    /// <remarks>Сразу оптимизированным: иначе OSR через ~4 с даёт разовый кадр 6–8 мс. Метод тонкий, PGO ему не нужен.</remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public ClientRunReport Run()
    {
        _running = true;
        _watchdog?.Restart();
        var total = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp(), lastSim = last;
        long lastAllocated = GC.GetAllocatedBytesForCurrentThread();
        int lastGen0 = GC.CollectionCount(0), lastResizes = _resizes;
        int frames = 0, serviceFrames = 0, cycle = 0, minimizedFrames = 0;
        double firstFrameMs = 0, frameMsSum = 0, frameMsMax = 0;
        long allocatedSum = 0;
        int gen0Sum = 0;
        string reason = "closed";
        var bench = _options.BenchSeconds > 0 ? new FrameBench(_options.BenchSeconds) : null;
        var profiler = Profiler;
        bool profileMeasuring = false;

        while (true)
        {
            // Кадр профайлера — от прокачки сообщений до конца кадра; включение профайлера вступает в силу здесь.
            profiler.BeginFrame(_frame + 1);
            profiler.Begin(_zones.Messages);
            bool alive = _window.PumpMessages();
            profiler.End(_zones.Messages);
            if (!alive) break;
            _liveResizeError?.Throw();
            // Escape у поля ввода панели — отмена правки, а не выход.
            if (_options.CloseOnEscape && _window.Input.WasPressed(Key.Escape) && _panels?.WantsKeyboard != true)
            {
                reason = "escape";
                break;
            }

            bool minimized = _window.Minimized;
            if (minimized && !_drawMinimized)
            {
                // Свёрнутое окно не рисуется: DXGI ответил бы «перекрыто», а цикл крутился бы впустую. Кадра нет — и у профайлера.
                // Проверочный прогон (кадры, бенч) рисует и свёрнутым — в прежнем размере, показ DXGI отбрасывает.
                profiler.AbandonFrame();
                Thread.Sleep(16);
                last = lastSim = Stopwatch.GetTimestamp();
                lastAllocated = GC.GetAllocatedBytesForCurrentThread();
                lastGen0 = GC.CollectionCount(0);
                continue;
            }

            if (_options.ResizeCycle && !minimized && frames > 0 && frames % _options.ResizeCycleFrames == 0)
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
            // Панели (DevTools) — до ввода игры: мышь и клавиатура интерфейса в этом же кадре не уходят игре.
            if (_panels is { } panels) input = PanelsFrame(panels, input);
            if (input.WasPressed(VsyncToggle)) Vsync = !Vsync;
            _game.Input(input);
            _edges.Note(input);   // фронты кадра — до ближайшего тика (раздел 7)
            profiler.End(_zones.Input);
            profiler.Begin(_zones.Simulate);
            // Кадр до первого сыгранного тика включительно — служебный: тик 0 строит мир.
            bool beforeFirstTick = _simulation is not null && _simulation.Tick == 0;
            _modules.BeginFrame(_simulation?.Tick ?? 0);
            int ticks = _clock?.RunFrame(seconds) ?? 0;
            profiler.End(_zones.Simulate);
            _view = new FrameView(_clock?.Alpha ?? 1, ticks, frameSeconds, _meter.Stats(ticks, _vsync));
            profiler.Begin(_zones.Extract);
            _game.Extract(_view);
            _modules.Extract(_view);
            profiler.End(_zones.Extract);
            double simTime = _clock is { } clock ? clock.Link.Tick * (double)clock.Link.Dt : 0;
            // Последний кадр замера — по игровому времени после тиков кадра.
            bool lastBenchFrame = bench?.Done(simTime) == true;
            string? shotPath = null;
            if (_options.ScreenshotPath is not null && (bench is not null ? lastBenchFrame : frames + 1 == _screenshotFrame))
                shotPath = _options.ScreenshotPath;
            else if (bench is null && input.WasPressed(Screenshot))   // в бенче F4 нет
                shotPath = ScreenshotFile();
            double preRenderMs = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
            profiler.Begin(_zones.Render);
            RenderFrame(shotPath, required: shotPath == _options.ScreenshotPath);
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
            // Служебный кадр: первый, до первого сыгранного тика включительно, со сменой размера с конца прошлого
            // кадра (и из цикла перетаскивания рамки), снимок.
            bool service = frames == 0 || beforeFirstTick || _resizes != lastResizes || shotPath is not null;
            _displayHz = _window.RefreshHz;   // окно могли перенести на монитор с другой частотой (поле окна — без вызовов ОС)
            // Бенч: итоги профайлера — кадры замера (первый кадр после прогрева — первый в итогах).
            if (bench is not null && !profileMeasuring && FrameBench.Measuring(simTime))
            {
                profiler.Reset();
                profileMeasuring = true;
            }
            profiler.EndFrame(_view.Alpha, service || lastBenchFrame);
            var gpu = _renderer.Gpu;   // GPU-время последнего разобранного кадра (на 3 раньше этого)
            _meter.Frame(ms, cpuMs, _renderer.LastFenceWaitMs, gpu.Valid ? gpu.FrameMs : 0, gpu.Valid ? gpu.PassesMs : 0);
            // Последний кадр замера (со снимком — ожидание GPU и PNG) в замер не входит: шаг кадра меряется в начале следующего.
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
            if (minimized) minimizedFrames++;
            _watchdog?.Frame();

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
        profiler.AbandonFrame();   // выход посреди кадра (окно закрыто, Escape): кадр не доделан — не в итогах
        var benchReport = bench?.Result();
        if (benchReport is not null)
        {
            Log.Info("bench", benchReport.ToString());
            // Проходы GPU — мс на кадр.
            foreach (var pass in benchReport.Passes ?? [])
                Log.Info("bench", $"  {pass.Name,-22} {pass.Ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} мс/кадр GPU");
        }

        _liveResizeError?.Throw();
        // GPU доделал отправленное — времена последнего кадра разобраны, проходы итога — с GPU-временем того же кадра.
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
            Gpu: _renderer.Gpu.Report(),
            DisplayHz: _displayHz,
            MinimizedFrames: minimizedFrames,
            Hdr: new ClientHdrReport(_renderer.Hdr, _renderer.Exposure, _renderer.Bloom, _screenshotHdr),
            Lights: ClientLightsReport.From(_renderer.Lights.Report()),
            Textures: [.. _renderer.Textures.Select(ClientTextureReport.From)],
            Modules: _modules.Report());
    }

    private long _benchGpu;

    /// <summary>Новые GPU-времена (раз на разобранный кадр) — в бенч: кадр целиком и проходы. 0 байт в установившемся режиме.</summary>
    private void BenchGpu(FrameBench bench, double simTime)
    {
        var gpu = _renderer.Gpu;
        if (!gpu.Valid || gpu.Sequence == _benchGpu) return;
        _benchGpu = gpu.Sequence;
        bench.GpuFrame(simTime, gpu.FrameMs);
        for (int i = 0; i < gpu.Count; i++) bench.GpuPass(gpu.NameOf(i), gpu.MillisecondsOf(i));
    }

    private int _resizes;

    /// <summary>Размер сверяется с задним буфером, а не по флагу Resized: SetClientSize присылает WM_SIZE прямо внутри
    /// вызова, вне прокачки сообщений.</summary>
    private void ResizeIfNeeded()
    {
        if (_window.Width == _renderer.Width && _window.Height == _renderer.Height) return;
        _renderer.Resize(_window.Width, _window.Height);
        _resizes++;
        Log.Info("client", $"размер {_window.Width}×{_window.Height}");
    }

    /// <summary>Ограничитель кадров. Сон в Windows идёт шагом таймера (~15,6 мс), поэтому спим, пока до конца кадра больше
    /// 20 мс, остаток — ожидание с уступкой процессора.</summary>
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

    /// <param name="required">Снимок заказан прогоном: не записался — ошибка прогона; снимок F4 — ошибка в журнал.</param>
    private void RenderFrame(string? screenshot = null, bool required = true)
    {
        _frame++;
        // Снимок прогона с HDR — ещё и карты рядом: HDR (<снимок>.hdr.png и сырой цвет .hdr.bin), bloom (.bloom.png, .bloom.bin) и слой ½
        // прозрачного (.transparent.png, .transparent.bin — если он есть в кадре); F4 — только цвет.
        bool maps = screenshot is not null && screenshot == _options.ScreenshotPath && _renderer.Hdr;
        var capture = screenshot is null ? null
            : new FrameCapture(screenshot)
            {
                HdrPath = maps ? Path.ChangeExtension(screenshot, ".hdr.png") : null,
                BloomPath = maps && _renderer.Bloom.Active ? Path.ChangeExtension(screenshot, ".bloom.png") : null,
                TransparentPath = maps ? Path.ChangeExtension(screenshot, ".transparent.png") : null,
            };
        bool captured = true;
        try
        {
            _renderer.Render(_buildFrame, _vsync, capture);
        }
        catch (CaptureException e) when (!required)
        {
            Log.Error("client", e.Message);
            captured = false;
        }
        if (screenshot is not null && captured)
        {
            _screenshots++;
            _screenshot = Path.GetFullPath(screenshot);
            if (screenshot == _options.ScreenshotPath) _screenshotHdr = _renderer.LastCapture?.Hdr;
            Log.Info("client", $"снимок {_screenshot}");
        }
    }

    /// <summary>Подключить панель (до <see cref="Run"/>); её действия должны быть в привязках. Первая панель заводит слой
    /// панелей (<see cref="Panels"/>).</summary>
    public void AddPanel(IClientPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (_running) throw new InvalidOperationException("панели подключаются до Run");
        foreach (var a in panel.Actions)
            if (!_bindings.Has(a.Name))
                throw new ArgumentException($"привязки не знают действия {a.Name} панели: разрешите их вместе с действиями игры", nameof(panel));
        if (_panels is null)
        {
            _panels = new ClientPanels(() => _renderer.Ui.Fonts);   // UI-проход — когда панель впервые видна
            _panels.Source.Input.GetClipboard = _window.GetClipboardText;
            _panels.Source.Input.SetClipboard = _window.SetClipboardText;
            _panelsZone = Profiler.Zone("frame.panels");
        }
        _panels.Add(panel);
    }

    /// <summary>Слой панелей (масштаб интерфейса — <see cref="ClientPanels.Scale"/>); null — панелей нет.</summary>
    public ClientPanels? Panels => _panels;

    private ClientPanels? _panels;
    private int _panelsZone;

    /// <summary>Часы интерфейса панелей (каретка, двойной щелчок, подсказки).</summary>
    private readonly long _uiStart = Stopwatch.GetTimestamp();

    /// <summary>Кадр панелей (шаг ввода кадра, до ввода игры) и курсор окна по интерфейсу.</summary>
    private FrameInput PanelsFrame(ClientPanels panels, in FrameInput input)
    {
        Profiler.Begin(_panelsZone);
        var framed = panels.Frame(input, _window.Input, _window.MouseInside, Stopwatch.GetElapsedTime(_uiStart).TotalSeconds);
        var cursor = panels.Cursor;
        if (!_window.CursorLock && _window.Cursor != cursor) _window.Cursor = cursor;
        Profiler.End(_panelsZone);
        return framed;
    }

    /// <summary>Проходы кадра: свет модулей (до игры — его видят константы игры), игра, клиентские модули, поверх — квады
    /// панелей.</summary>
    private void BuildFrame(RenderFrame frame)
    {
        _modules.AddLights(frame.Lights);
        _game.BuildFrame(frame);
        _modules.BuildFrame(frame);
        // Панели — в фазе Debug, последними: ручки F3 правят при включённых оверлеях, и оверлеи не должны закрывать панель.
        if (_panels is { Shown: true } panels) _renderer.Ui.Add(frame, panels.Ui.Output, "panels", phase: FramePhase.Debug);
    }

    /// <summary>Вертикальная синхронизация: меняется на ходу (<see cref="VsyncToggle"/>, панель) со следующего показа.
    /// В бенче выключена всегда.</summary>
    public bool Vsync
    {
        get => _vsync;
        set
        {
            if (value == _vsync || _options.BenchSeconds > 0) return;
            _vsync = value;
            Log.Info("client", $"вертикальная синхронизация {(value ? "вкл" : "выкл")}");
        }
    }

    /// <summary>Рендер клиента (граф проходов, снимки).</summary>
    public Renderer Renderer => _renderer;

    /// <summary>
    /// Профайлер CPU потока кадра: период <c>frame</c>, фазы <c>frame.messages</c>, <c>.resize</c>, <c>.input</c>
    /// (<c>frame.panels</c>), <c>.simulate</c> (тики), <c>.extract</c>, <c>.render</c>, <c>.wait</c>. Включается со
    /// следующего кадра.
    /// </summary>
    public CpuProfiler Profiler { get; } = new();

    private readonly FrameZones _zones;

    /// <summary>Номера зон фаз кадра — заведены при создании клиента.</summary>
    private sealed class FrameZones(CpuProfiler p)
    {
        public readonly int Messages = p.Zone("frame.messages"), Resize = p.Zone("frame.resize"), Input = p.Zone("frame.input"),
            Simulate = p.Zone("frame.simulate"), Extract = p.Zone("frame.extract"), Render = p.Zone("frame.render"),
            Wait = p.Zone("frame.wait", ProfileZoneKind.Idle);
    }

    /// <summary>Клиентская часть игры.</summary>
    public IClientGame Game => _game;

    /// <summary>Клиентские модули (включённые и выключенные, их оверлеи и ручки).</summary>
    public ClientModules Modules => _modules;

    /// <summary>Камера кадра игры для проходов модулей; игра не наблюдаема — нулевая.</summary>
    private CameraView ModuleCamera() => (_game as IClientObservable)?.Camera ?? default;

    /// <summary>Номер последнего нарисованного кадра (с 1).</summary>
    public ulong Frame => _frame;

    /// <summary>Доля тика последнего кадра (1 — кадр показывает ровно последний тик).</summary>
    public float Alpha => _view.Alpha;

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
    /// Край окна тянут: главный цикл стоит в системном цикле перетаскивания, поэтому кадр рисуется отсюда. Исключение через
    /// оконную процедуру не раскрутится — оно запоминается и бросается главным циклом; кадров после ошибки нет.
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
            _modules.BeginFrame(_simulation?.Tick ?? 0);   // и у модулей кадр без тиков: GPU-симуляции не шагают
            var view = _view with { Ticks = 0, Seconds = 0, Stats = _view.Stats with { Ticks = 0 } };
            _game.Extract(view);
            _modules.Extract(view);
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
        _watchdog?.Dispose();
        _window.LiveResize -= OnLiveResize;
        // Ресурсы кадра освобождаются только после того, как GPU их отпустил: сначала ожидание, затем модули и сцена игры,
        // затем рендер с устройством. Ожидание может упасть (потеря устройства) — освобождение всё равно идёт.
        try
        {
            _renderer.WaitForIdle();
        }
        finally
        {
            try
            {
                try
                {
                    _modules.Dispose();
                }
                finally
                {
                    _game.Dispose();
                }
            }
            finally
            {
                _renderer.Dispose();
            }
        }
    }
}
