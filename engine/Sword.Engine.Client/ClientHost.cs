using System.Diagnostics;
using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Render;
using Sword.Engine.Rhi;

namespace Sword.Engine.Client;

/// <summary>Как вести клиент.</summary>
public sealed record ClientOptions
{
    public bool Vsync { get; init; }

    /// <summary>Отладочный слой D3D12 (сообщения — в журнал, категория «d3d12»).</summary>
    public bool DebugLayer { get; init; }

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

    /// <summary>Цвет очистки кадра (RGBA, 0…1).</summary>
    public Vector4 ClearColor { get; init; } = new(0.33f, 0.29f, 0.27f, 1f);
}

/// <summary>Итог работы клиента: чем кончилось, сколько кадров и как быстро. Для людей и агента (JSON).</summary>
public sealed record ClientRunReport(
    string ExitReason,
    string Adapter,
    ulong AdapterVramMb,
    int Frames,
    int Resizes,
    double Seconds,
    double AverageFrameMs,
    double MaxFrameMs,
    int Width,
    int Height);

/// <summary>
/// Клиент движка (раздел 7 ARCHITECTURE_PLAN.md): окно → кадр → показ. На точке 1а — окно, устройство, очистка кадра,
/// закрытие и изменение размера; симуляция, Extract, рендер-граф и UI добавляются на следующих точках.
/// Окно создаёт exe и отдаёт сюда: клиент знает только <see cref="IWindow"/>.
/// </summary>
public sealed class ClientHost : IDisposable
{
    private readonly IWindow _window;
    private readonly ClientOptions _options;
    private readonly GraphicsDevice _device;
    private readonly FrameRenderer _renderer;

    public ClientHost(IWindow window, ClientOptions options)
    {
        _window = window;
        _options = options;
        _device = new GraphicsDevice(window.NativeHandle, window.Width, window.Height, options.DebugLayer);
        _renderer = new FrameRenderer(_device);
        _window.LiveResize += OnLiveResize;
        Log.Info("client", $"{_device.AdapterName}, {_device.AdapterVramBytes / 1048576} МБ, {window.Width}×{window.Height}, " +
            $"вертикальная синхронизация {(options.Vsync ? "вкл" : "выкл")}{(options.DebugLayer ? ", отладочный слой D3D12" : "")}");
    }

    /// <summary>Главный цикл до закрытия окна, Escape или <see cref="ClientOptions.MaxFrames"/>.</summary>
    public ClientRunReport Run()
    {
        var total = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        int frames = 0, cycle = 0;
        double frameMsSum = 0, frameMsMax = 0;
        string reason = "closed";

        while (_window.PumpMessages())
        {
            if (_options.CloseOnEscape && _window.Input.WasPressed(Key.Escape))
            {
                reason = "escape";
                break;
            }

            if (_window.Minimized)
            {
                // Свёрнутое окно не рисуется: DXGI ответил бы «перекрыто», а цикл крутился бы впустую.
                Thread.Sleep(16);
                last = Stopwatch.GetTimestamp();
                continue;
            }

            if (_options.ResizeCycle && frames > 0 && frames % _options.ResizeCycleFrames == 0)
            {
                var (w, h) = _options.ResizeCycleSizes[cycle++ % _options.ResizeCycleSizes.Count];
                _window.SetClientSize(w, h);
            }

            ResizeIfNeeded();
            RenderFrame();

            long now = Stopwatch.GetTimestamp();
            double ms = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            last = now;
            frames++;
            frameMsSum += ms;
            frameMsMax = Math.Max(frameMsMax, ms);

            if (_options.MaxFrames > 0 && frames >= _options.MaxFrames)
            {
                reason = "max-frames";
                break;
            }
        }

        return new ClientRunReport(
            ExitReason: reason,
            Adapter: _device.AdapterName,
            AdapterVramMb: _device.AdapterVramBytes / 1048576,
            Frames: frames,
            Resizes: _resizes,
            Seconds: Math.Round(total.Elapsed.TotalSeconds, 3),
            AverageFrameMs: frames > 0 ? Math.Round(frameMsSum / frames, 3) : 0,
            MaxFrameMs: Math.Round(frameMsMax, 3),
            Width: _device.BackBufferWidth,
            Height: _device.BackBufferHeight);
    }

    private int _resizes;

    /// <summary>
    /// Размер сверяется с задним буфером, а не по флагу Resized: флаг живёт до следующей прокачки сообщений, а размер
    /// могли поменять и вне её (SetClientSize присылает WM_SIZE прямо внутри вызова).
    /// </summary>
    private void ResizeIfNeeded()
    {
        if (_window.Width == _device.BackBufferWidth && _window.Height == _device.BackBufferHeight) return;
        _device.Resize(_window.Width, _window.Height);
        _resizes++;
        Log.Info("client", $"размер {_window.Width}×{_window.Height}");
    }

    private void RenderFrame()
    {
        _renderer.Render(_options.ClearColor, _options.Vsync);
        _device.DrainDebugMessages();
    }

    /// <summary>
    /// Край окна тянут прямо сейчас: главный цикл стоит внутри системного цикла перетаскивания, поэтому кадр под новый
    /// размер рисуется отсюда — иначе до отпускания мыши картинка замерла бы.
    /// </summary>
    private void OnLiveResize()
    {
        if (_window.Minimized) return;
        ResizeIfNeeded();
        RenderFrame();
    }

    public void Dispose()
    {
        _window.LiveResize -= OnLiveResize;
        // Ресурсы кадра освобождаются только после того, как GPU их отпустил: Dispose устройства ждёт простоя.
        _device.Dispose();
    }
}
