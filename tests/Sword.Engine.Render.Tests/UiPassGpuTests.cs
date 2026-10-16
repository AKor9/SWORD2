using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Ui;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// UI-проход на устройстве (шрифты пака, категория Content): без сообщений слоя, вызовов — по числу отрезков, кольцо квадов
/// растёт посреди кадра, чужие шрифты и лишние проходы — отказ без порчи следующих кадров.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class UiPassGpuTests : IDisposable, ILogSink
{
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];

    public UiPassGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests.Ui", 320, 200, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
    }

    private void Frame(Action<RenderFrame> build)
    {
        _window.PumpMessages();
        _renderer.Render(build, vsync: false);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Фон, текст, фигуры под обрезкой, снова текст — четыре отрезка.</summary>
    private static void Panel(UiDrawList d, int rects)
    {
        d.Rect(0, 0, 320, 200, 0xC0101010, 8);
        d.Text(UiFace.Head, 26, new Vector2(10, 10), 0xFFFFFFFF, "143 FPS");
        d.PushClip(new UiRect(10, 50, 200, 150));
        for (int i = 0; i < rects; i++) d.Rect(10 + i % 190, 50 + i % 100, 20 + i % 190, 60 + i % 100, 0xFF00FF00, 2);
        d.PopClip();
        d.Text(UiFace.Body, 13, new Vector2(10, 170), 0xFFB0B0B0, "ожидание 0.30 мс");
    }

    [Fact]
    public void ShapesAndText_DrawPerBatch_RingGrowsMidFrame()
    {
        var ui = _renderer.Ui;
        var small = ui.CreateDrawList();
        var large = ui.CreateDrawList();
        Panel(small, 4);
        Panel(large, 5000);   // больше начального кольца (4096 квадов на кадр)
        var batches = new List<UiBatch>();
        large.Split(_renderer.Width, _renderer.Height, batches);
        Assert.Equal(4, batches.Count);

        uint before = ui.QuadCapacity;
        for (int frame = 0; frame < 4; frame++)   // кадров больше, чем кадров в полёте: кольцо каждого кадра — в деле
            Frame(f =>
            {
                ui.Add(f, small, "ui.small");
                ui.Add(f, large, "ui.large");   // второй проход кадра не влезает в кольцо — оно растёт
            });
        Assert.True(ui.QuadCapacity > before, $"кольцо не выросло: {ui.QuadCapacity}");

        var passes = _renderer.Graph.LastFrame;
        Assert.Equal(["ui.small", "ui.large"], passes.Select(p => p.Name));
        Assert.Equal([4, 4], passes.Select(p => p.Draws));
        Assert.Single(passes[1].Writes);   // только задний буфер
        Assert.Empty(passes[1].Reads);
        AssertNoDebugMessages();
    }

    [Fact]
    public void ForeignFontsAndTooManyPasses_AreRefused_NextFrameIsClean()
    {
        var ui = _renderer.Ui;
        var list = ui.CreateDrawList();
        Panel(list, 4);

        // Чужие шрифты: номера glyph'ов — других таблиц.
        using var other = new UiFonts(ui.Fonts.Tables.Fonts);
        var foreign = new UiDrawList(other);
        foreign.Text(UiFace.Body, 14, Vector2.Zero, 1, "x");
        Assert.Throws<ArgumentException>(() => Frame(f => ui.Add(f, foreign)));

        // Проходов в кадре — не больше MaxPassesPerFrame.
        Assert.Throws<InvalidOperationException>(() => Frame(f =>
        {
            for (int i = 0; i <= UiPass.MaxPassesPerFrame; i++) ui.Add(f, list, $"ui.{i}");
        }));

        // Брошенные кадры не оставили счётчиков: полный кадр из MaxPassesPerFrame проходов идёт чисто; список без
        // шрифтов (только фигуры) тоже принимается.
        var shapes = new UiDrawList();
        shapes.Rect(1, 1, 30, 30, 0xFFFFFFFF);
        Frame(f =>
        {
            for (int i = 0; i < UiPass.MaxPassesPerFrame - 1; i++) ui.Add(f, list, $"ui.{i}");
            ui.Add(f, shapes, "ui.shapes");
        });
        Assert.Equal(UiPass.MaxPassesPerFrame, _renderer.Graph.LastFrame.Count);
        Assert.Equal(1, _renderer.Graph.LastFrame[^1].Draws);
        AssertNoDebugMessages();
    }
}
