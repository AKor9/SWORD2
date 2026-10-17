using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Автоэкспозиция на устройстве (<see cref="AutoExposure"/>) на однотонной HDR-сцене: яркость ключа — экспозиция ключ / яркость
/// (тонмап = эталон с ней), тёмная и яркая сцены — пределы 2^MaxEv и 2^MinEv от ручной; после смены сцены экспозиция идёт к новой
/// цели постепенно. Любое сообщение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class AutoExposureGpuTests : IDisposable, ILogSink
{
    private const string Shader = "graph_tests.hlsl";
    private static readonly GraphicsPipelineDesc Solid = new(Shader, "VsFull", "PsSolid");
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-exposure-{Guid.NewGuid():N}");

    public AutoExposureGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 96, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
        _renderer.AutoExposure.Enabled = true;
        Directory.CreateDirectory(_dir);
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
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Кадр однотонной сцены яркости <paramref name="luminance"/> (серый); вернёт красный байт центра или -1 без снимка.</summary>
    private int Frame(float luminance, bool shot)
    {
        string path = Path.Combine(_dir, $"e{Guid.NewGuid():N}.bmp");
        _window.PumpMessages();
        _renderer.Render(f => f.Graph.AddPass("test.solid").Color(f.SceneColor, _renderer.SceneColorClear).Execute(ctx =>
        {
            ctx.SetPipeline(Solid);
            ctx.SetConstant(0, luminance);
            ctx.SetConstant(1, luminance);
            ctx.SetConstant(2, luminance);
            ctx.Draw(3);
        }), vsync: false, shot ? new FrameCapture(path) : null);
        if (!shot) return -1;
        var file = File.ReadAllBytes(path);
        int w = _renderer.Width, h = _renderer.Height, stride = (w * 3 + 3) & ~3;
        return file[54 + (h - 1 - h / 2) * stride + w / 2 * 3 + 2];
    }

    /// <summary>Байт тонмапа серого <paramref name="luminance"/> с экспозицией <paramref name="exposure"/> (эталон).</summary>
    private static int Expected(float luminance, float exposure) =>
        FilmicTonemap.DisplayBytes(new Vector3((float)(Half)luminance), exposure).R;

    [Theory]
    [InlineData(0.405f)]
    [InlineData(0.002f)]
    [InlineData(40f)]
    public void Uniform_ExposureIsKeyOverLuminance_WithinLimits(float luminance)
    {
        var auto = _renderer.AutoExposure;
        float exposure = Math.Clamp(auto.Key / (float)(Half)luminance, Renderer.DefaultExposure * MathF.Pow(2, auto.MinEv),
            Renderer.DefaultExposure * MathF.Pow(2, auto.MaxEv));
        for (int i = 0; i < 6; i++) Frame(luminance, shot: false);
        int value = Frame(luminance, shot: true);
        Assert.InRange(value, Expected(luminance, exposure) - 1, Expected(luminance, exposure) + 1);
        Assert.Contains(_renderer.Graph.LastFrame, p => p.Name == "post.exposure.adapt");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    [Fact]
    public void SceneChange_ExposureMovesGradually()
    {
        var auto = _renderer.AutoExposure;
        for (int i = 0; i < 6; i++) Frame(0.405f, shot: false);
        // Сцена вчетверо ярче: следующий кадр (время кадра — миллисекунды) почти прежний, а не сразу у новой цели.
        int before = Expected(1.62f, Renderer.DefaultExposure), target = Expected(1.62f, auto.Key / 1.62f);
        int next = Frame(1.62f, shot: true);
        Assert.True(before > target + 5, $"сцена различима: прежняя {before}, цель {target}");
        Assert.InRange(next, target + 3, before + 1);
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }
}
