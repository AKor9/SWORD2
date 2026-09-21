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

    /// <summary>Снять кадр в PNG: путь файла; снимается кадр <see cref="ScreenshotFrame"/>.</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>Номер снимаемого кадра (с 1); 0 — последний кадр прогона (<see cref="MaxFrames"/>), без него — 30-й.</summary>
    public int ScreenshotFrame { get; init; }

    /// <summary>Каталог шейдеров; по умолчанию — Shaders рядом с exe.</summary>
    public string? ShaderRoot { get; init; }

    /// <summary>
    /// Тиков симуляции на кадр ровно столько (проверочные прогоны, снимки: результат не зависит от скорости машины);
    /// 0 — по часам, накопителем (<c>Program.cs</c> SWORD: шаг кадра до 0,25 с, не больше
    /// <see cref="SimSettings.MaxTicksPerFrame"/> тиков, после упора остаток срезается до одного тика).
    /// </summary>
    public int TicksPerFrame { get; init; }
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
/// останавливает весь процесс, поэтому считается любая).
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
    string? Screenshot,
    IReadOnlyList<RenderPassReport> Passes);

/// <summary>
/// Клиент движка (раздел 7 ARCHITECTURE_PLAN.md): окно → тики симуляции (<see cref="SimulationClock"/>: очередь кадра,
/// накопитель, команды тиков от игры) → кадр → показ. Ввод, Extract с интерполяцией и UI — следующие точки.
/// Окно и симуляцию создаёт exe и отдаёт сюда: клиент знает <see cref="IWindow"/>, API рендера (устройство D3D12 —
/// внутри рендера) и симуляцию; клиентской части игры он отдаёт только <see cref="SimulationLink"/>.
/// </summary>
public sealed class ClientHost : IDisposable
{
    private readonly IWindow _window;
    private readonly ClientOptions _options;
    private readonly Renderer _renderer;
    private readonly IClientGame _game;
    private readonly Action<RenderFrame> _buildFrame;
    private readonly int _screenshotFrame;
    private string? _screenshot;
    private readonly Simulation? _simulation;
    private readonly SimulationClock? _clock;

    /// <summary>Исключение кадра, нарисованного из системного цикла перетаскивания; бросается в главном цикле.</summary>
    private ExceptionDispatchInfo? _liveResizeError;

    public ClientHost(IWindow window, ClientOptions options, IClientGame game, Simulation? simulation = null)
    {
        if (options.ResizeCycle && (options.ResizeCycleFrames < 1 || options.ResizeCycleSizes.Count == 0))
            throw new ArgumentException("ResizeCycle: нужны ResizeCycleFrames ≥ 1 и хотя бы один размер", nameof(options));
        _window = window;
        _options = options;
        _game = game;
        _simulation = simulation;
        _clock = simulation is null ? null : new SimulationClock(simulation, game.BeforeTick, options.TicksPerFrame);
        _buildFrame = game.BuildFrame;
        _screenshotFrame = options.ScreenshotFrame > 0 ? options.ScreenshotFrame : options.MaxFrames > 0 ? options.MaxFrames : 30;
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        game.ContributeShaders(contract);
        _renderer = new Renderer(new RendererOptions(window.NativeHandle, window.Width, window.Height,
            options.DebugLayer || options.GpuValidation, options.ShaderRoot ?? Path.Combine(AppContext.BaseDirectory, "Shaders"),
            options.GpuValidation), contract);
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
        Log.Info("client", $"{_renderer.AdapterName}, {_renderer.AdapterVramMb} МБ, {window.Width}×{window.Height}, " +
            $"вертикальная синхронизация {(options.Vsync ? "вкл" : "выкл")}" +
            (options.GpuValidation ? ", отладочный слой D3D12 с GPU-валидацией" : options.DebugLayer ? ", отладочный слой D3D12" : ""));
    }

    /// <summary>Главный цикл до закрытия окна, Escape или <see cref="ClientOptions.MaxFrames"/>.</summary>
    public ClientRunReport Run()
    {
        var total = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp(), lastSim = last;
        long lastAllocated = GC.GetAllocatedBytesForCurrentThread();
        int lastGen0 = GC.CollectionCount(0), lastResizes = _resizes;
        int frames = 0, serviceFrames = 0, cycle = 0;
        double firstFrameMs = 0, frameMsSum = 0, frameMsMax = 0;
        long allocatedSum = 0;
        int gen0Sum = 0;
        string reason = "closed";

        while (_window.PumpMessages())
        {
            _liveResizeError?.Throw();
            if (_options.CloseOnEscape && _window.Input.WasPressed(Key.Escape))
            {
                reason = "escape";
                break;
            }

            if (_window.Minimized)
            {
                // Свёрнутое окно не рисуется: DXGI ответил бы «перекрыто», а цикл крутился бы впустую.
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

            ResizeIfNeeded();
            long simNow = Stopwatch.GetTimestamp();
            _clock?.RunFrame(Stopwatch.GetElapsedTime(lastSim, simNow).TotalSeconds);
            lastSim = simNow;
            bool shot = frames + 1 == _screenshotFrame && _options.ScreenshotPath is not null;
            RenderFrame(shot ? _options.ScreenshotPath : null);

            long now = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0);
            double ms = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            // Служебный кадр: первый, со сменой размера с конца прошлого кадра (и из цикла перетаскивания рамки), снимок.
            bool service = frames == 0 || _resizes != lastResizes || shot;
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

            if (_options.MaxFrames > 0 && frames >= _options.MaxFrames)
            {
                reason = "max-frames";
                break;
            }
        }

        _liveResizeError?.Throw();
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
            Screenshot: _screenshot,
            Passes: _renderer.Graph.LastFrame);
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

    private void RenderFrame(string? screenshot = null)
    {
        _renderer.Render(_buildFrame, _options.Vsync, screenshot);
        if (screenshot is not null)
        {
            _screenshot = Path.GetFullPath(screenshot);
            Log.Info("client", $"снимок {_screenshot}");
        }
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
        try
        {
            ResizeIfNeeded();
            // Кадр без тиков: очередь кадра пуста — события прошлого кадра не обрабатываются второй раз.
            _clock?.EmptyFrame();
            RenderFrame();
        }
        catch (Exception ex)
        {
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
