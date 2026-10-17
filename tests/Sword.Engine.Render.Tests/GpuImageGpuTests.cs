using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Ui;
using Sword.Tests;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Картинки из пикселей (<see cref="Renderer.CreateImage"/>) рисуются <see cref="UiDrawList.Image"/> с нужным мипом, UI-проход
/// берёт шрифты вызывающего (<see cref="RendererOptions.UiFonts"/>), а не пака. Шрифты читаются из пака — категория Content.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class GpuImageGpuTests : IDisposable, ILogSink
{
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly UiFonts _fonts;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-image-{Guid.NewGuid():N}");

    public GpuImageGpuTests()
    {
        Log.AddSink(this);
        _fonts = PakFonts.Read();   // шрифты читает вызывающий, как окно инструмента
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests.Image", 320, 200, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true) { UiFonts = _fonts }, contract);
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
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    [Fact]
    public void UiPass_TakesCallerFonts()
    {
        Assert.Same(_fonts, _renderer.Ui.Fonts);
        var list = _renderer.Ui.CreateDrawList();
        list.Text(UiFace.Body, 14, new Vector2(4, 4), 0xFFFFFFFF, "шрифт вызывающего");
        _window.PumpMessages();
        _renderer.Render(f => _renderer.Ui.Add(f, list), vsync: false);
        AssertNoDebugMessages();
    }

    [Fact]
    public void Image_MipsUploaded_DrawnWithChosenMip()
    {
        // 4×4: левая половина красная, правая синяя. Мип 0 слева — красный; мип 2 (1×1) — среднее, фиолетовый.
        var rgba = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int o = (y * 4 + x) * 4;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = x < 2 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255);
            }
        using var image = _renderer.CreateImage("test.image", 4, 4, rgba);
        Assert.Equal((4, 4, 3, false), (image.Width, image.Height, image.Mips, image.Srgb));
        Assert.True(image.Srv >= 0);
        using var flat = _renderer.CreateImage("test.flat", 4, 4, rgba, mips: false);
        Assert.Equal(1, flat.Mips);

        var list = new UiDrawList();
        list.Image(new UiRect(20, 20, 100, 100), image.Srv, Vector2.Zero, new Vector2(0.5f, 1), 0xFFFFFFFF, mip: 0, nearest: true);
        list.Image(new UiRect(140, 20, 220, 100), image.Srv, Vector2.Zero, Vector2.One, 0xFFFFFFFF, mip: 2);
        string shot = Path.Combine(_dir, "image.bmp");
        Directory.CreateDirectory(_dir);
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            f.Graph.AddPass("test.clear").Color(f.Graph.BackBuffer, new Vector4(0, 0, 0, 1));
            _renderer.Ui.Add(f, list);
        }, vsync: false, shot);
        AssertNoDebugMessages();

        var bmp = File.ReadAllBytes(shot);
        int width = BitConverter.ToInt32(bmp, 18), height = BitConverter.ToInt32(bmp, 22), stride = (width * 3 + 3) & ~3;
        (int R, int G, int B) Pixel(int x, int y)
        {
            int o = 54 + (height - 1 - y) * stride + x * 3;
            return (bmp[o + 2], bmp[o + 1], bmp[o]);
        }
        Assert.Equal((255, 0, 0), Pixel(60, 60));
        var mixed = Pixel(180, 60);
        Assert.True(Math.Abs(mixed.R - 128) <= 2 && mixed.G == 0 && Math.Abs(mixed.B - 128) <= 2, $"мип 2: {mixed}");
        Assert.Equal((0, 0, 0), Pixel(10, 150));
    }

    [Fact]
    public void Image_GivenMips_ShownAsIs()
    {
        // мип 0 2×2 — красный, мип 1 (1×1) — зелёный, а не среднее красного: предпросмотр пекарни показывает мипы текстуры как есть
        byte[] red = [.. Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 255, 0, 0, 255 })];
        byte[] green = [0, 255, 0, 255];
        Assert.Throws<ArgumentException>(() => _renderer.CreateImage("test.bad", 2, 2, [red, new byte[8]]));
        Assert.Throws<ArgumentException>(() => _renderer.CreateImage("test.many", 2, 2, [red, green, green]));
        using var image = _renderer.CreateImage("test.given", 2, 2, [red, green]);
        Assert.Equal(2, image.Mips);

        var list = new UiDrawList();
        list.Image(new UiRect(20, 20, 100, 100), image.Srv, Vector2.Zero, Vector2.One, 0xFFFFFFFF, mip: 0);
        list.Image(new UiRect(140, 20, 220, 100), image.Srv, Vector2.Zero, Vector2.One, 0xFFFFFFFF, mip: 1);
        string shot = Path.Combine(_dir, "given.bmp");
        Directory.CreateDirectory(_dir);
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            f.Graph.AddPass("test.clear").Color(f.Graph.BackBuffer, new Vector4(0, 0, 0, 1));
            _renderer.Ui.Add(f, list);
        }, vsync: false, shot);
        AssertNoDebugMessages();

        var bmp = File.ReadAllBytes(shot);
        int width = BitConverter.ToInt32(bmp, 18), height = BitConverter.ToInt32(bmp, 22), stride = (width * 3 + 3) & ~3;
        (int R, int G, int B) Pixel(int x, int y)
        {
            int o = 54 + (height - 1 - y) * stride + x * 3;
            return (bmp[o + 2], bmp[o + 1], bmp[o]);
        }
        Assert.Equal((255, 0, 0), Pixel(60, 60));
        Assert.Equal((0, 255, 0), Pixel(180, 60));
    }
}
