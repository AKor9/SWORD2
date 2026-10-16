using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Фазы кадра и HDR на устройстве: порядок фаз, запрет записи заднего буфера до Post при HDR, тонмап против
/// <see cref="FilmicTonemap"/>, кадр без HDR — бит в бит. Любое сообщение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class HdrGpuTests : IDisposable, ILogSink
{
    private const string Shader = "graph_tests.hlsl";
    private static readonly GraphicsPipelineDesc Bands = new(Shader, "VsFull", "PsBands");
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-hdr-{Guid.NewGuid():N}");

    public HdrGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 64, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        // Тонмап против CPU-эталона пиксель в пиксель — без bloom (его проверяет BloomGpuTests).
        _renderer.Bloom = BloomSettings.Off;
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

    private void Frame(Action<RenderFrame> build, FrameCapture? capture = null)
    {
        _window.PumpMessages();
        _renderer.Render(build, vsync: false, capture);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Кадр со снимком цвета в BMP: пиксели RGB по строкам сверху вниз.</summary>
    private byte[] Shot(Action<RenderFrame> build, string name, string? hdrPath = null)
    {
        string path = Path.Combine(_dir, name + ".bmp");
        Frame(build, new FrameCapture(path) { HdrPath = hdrPath });
        var file = File.ReadAllBytes(path);
        int w = _renderer.Width, h = _renderer.Height, stride = (w * 3 + 3) & ~3;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int src = 54 + (h - 1 - y) * stride + x * 3, dst = (y * w + x) * 3;
                (rgb[dst], rgb[dst + 1], rgb[dst + 2]) = (file[src + 2], file[src + 1], file[src]);
            }
        return rgb;
    }

    private static readonly Vector4 LdrClear = new(0.2f, 0.4f, 0.6f, 1);

    private static void LdrScene(RenderFrame f) => f.Graph.AddPass("test.scene").Color(f.Graph.BackBuffer, LdrClear);

    [Fact]
    public void Phases_ExecuteByPhaseThenRegistration_ReportInSameOrder()
    {
        var desc = new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget, 8, 8);
        Frame(f =>
        {
            var g = f.Graph;
            var t = g.CreateTexture("test.t", desc);
            var u = g.CreateTexture("test.u", desc);
            g.AddPass("test.debug", FramePhase.Debug).Color(g.BackBuffer);
            g.AddPass("test.ui", FramePhase.Ui).Color(g.BackBuffer);
            // Читает t раньше по регистрации, чем t пишут, но исполняется позже по фазе: инвариант «прочитано записанное» — по исполнению.
            g.AddPass("test.composite", FramePhase.Composite).Color(u).Read(t);
            g.AddPass("test.opaque").Color(t, Vector4.Zero);
            g.AddPass("test.post", FramePhase.Post).Color(g.BackBuffer, Vector4.Zero);
            g.AddPass("test.opaque2", FramePhase.Opaque).Color(u, Vector4.Zero);
            g.AddPass("test.ui2", FramePhase.Ui).Color(g.BackBuffer);
        });
        var passes = _renderer.Graph.LastFrame;
        Assert.Equal(["test.opaque", "test.opaque2", "test.composite", "test.post", "test.ui", "test.ui2", "test.debug"],
            passes.Select(p => p.Name));
        Assert.Equal([FramePhase.Opaque, FramePhase.Opaque, FramePhase.Composite, FramePhase.Post, FramePhase.Ui, FramePhase.Ui, FramePhase.Debug],
            passes.Select(p => p.Phase));
        // Барьеры — по исполнению: test.composite переводит t в чтение после записи test.opaque.
        Assert.True(passes[2].Barriers >= 1);
        Assert.Equal(passes.Select(p => p.Name), _renderer.Gpu.Join(passes).Select(p => p.Name));
        AssertNoDebugMessages();
    }

    [Fact]
    public void HdrOff_FrameAsBefore_AndAfterHdrFramesTheSame()
    {
        Assert.False(_renderer.Hdr);
        var before = Shot(LdrScene, "before");
        // Прежний кадр: очистка заднего буфера цветом — байты по формуле UNORM, без тонмапа.
        for (int i = 0; i < before.Length; i += 3) Assert.Equal((51, 102, 153), (before[i], before[i + 1], before[i + 2]));
        Assert.Null(_renderer.LastCapture!.Hdr);

        _renderer.Hdr = true;
        for (int i = 0; i < 3; i++) Frame(f => f.Graph.AddPass("test.hdr").Color(f.SceneColor, _renderer.SceneColorClear));
        Assert.Contains("post.tonemap", _renderer.Graph.LastFrame.Select(p => p.Name));
        _renderer.Hdr = false;
        var after = Shot(LdrScene, "after");
        Assert.Equal(before, after);
        Assert.DoesNotContain("post.tonemap", _renderer.Graph.LastFrame.Select(p => p.Name));
        Assert.Throws<InvalidOperationException>(() => Frame(f => _ = f.SceneColor));
        AssertNoDebugMessages();
    }

    [Fact]
    public void Tonemap_MatchesCpuReference_AndCaptureReportsHdr()
    {
        Vector4[] bands =
        [
            new(0, 0, 0, 1), new(0.18f, 0.18f, 0.18f, 1), new(0.02f, 0.05f, 0.3f, 1), new(1, 1, 1, 1),
            new(12, 0.6f, 0, 1), new(0.8f, 0.3f, 0.1f, 1), new(60, 60, 60, 1), new(1000, 900, 800, 1),
        ];
        using var storage = _renderer.CreateStorage<Vector4>("test.bands", bands);
        _renderer.Hdr = true;
        void Scene(RenderFrame f)
        {
            var g = f.Graph;
            var b = g.Import(storage);
            g.AddPass("test.bands").Color(f.SceneColor, _renderer.SceneColorClear).Read(b).Execute(ctx =>
            {
                ctx.SetPipeline(Bands);
                ctx.SetConstant(0, bands.Length);
                ctx.SetConstant(1, ctx.Width);
                ctx.SetConstant(4, g.SrvOf(b));
                ctx.Draw(3);
            });
        }

        string hdrPath = Path.Combine(_dir, "bands.hdr.png");
        var rgb = Shot(Scene, "bands", hdrPath);
        int w = _renderer.Width, band = w / bands.Length;
        var sceneValues = new List<Vector3>();
        for (int i = 0; i < bands.Length; i++)
        {
            // Цель — RGBA16F: эталон считает от значения, округлённого до half.
            var v = new Vector3((float)(Half)bands[i].X, (float)(Half)bands[i].Y, (float)(Half)bands[i].Z);
            sceneValues.Add(v);
            var (r, g, b) = FilmicTonemap.DisplayBytes(v, _renderer.Exposure);
            int p = ((_renderer.Height / 2) * w + i * band + band / 2) * 3;
            Assert.True(Math.Abs(rgb[p] - r) <= 1 && Math.Abs(rgb[p + 1] - g) <= 1 && Math.Abs(rgb[p + 2] - b) <= 1,
                $"полоса {i} {bands[i]}: GPU ({rgb[p]}, {rgb[p + 1]}, {rgb[p + 2]}), эталон ({r}, {g}, {b})");
        }

        var hdr = _renderer.LastCapture!.Hdr!;
        float Luma(Vector3 c) => (0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z) * _renderer.Exposure;
        Assert.Equal(_renderer.Exposure, hdr.Exposure);
        Assert.Equal(Luma(sceneValues[^1]), hdr.MaxLuminance, 1e-3f);
        // Ярче 1 — полосы 4, 6, 7 (по 8 пикселей из 64 в строке).
        Assert.Equal(3f / bands.Length, hdr.OverOne, 1e-4f);
        Assert.Equal(sceneValues.Average(Luma), hdr.MeanLuminance, 1e-2f);
        Assert.Equal(0, hdr.NonFinite);
        Assert.Equal(hdrPath, hdr.Path);
        Assert.True(File.Exists(hdr.Path) && new FileInfo(hdr.RawPath!).Length == (long)w * _renderer.Height * 8);
        Assert.Contains(_renderer.Graph.LastFrame, p => p is { Name: "post.tonemap", Phase: FramePhase.Post });
        AssertNoDebugMessages();
    }

    [Fact]
    public void Tonemap_NonFiniteAndNegative_LikeCpuReference()
    {
        // NaN и −∞ в канале — 0, +∞ — предел half (белый), отрицательные — 0: GPU так же, как эталон; снимок считает NaN и ∞.
        Vector4[] bands =
        [
            new(float.NaN, float.NaN, float.NaN, 1), new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 1),
            new(float.NegativeInfinity, 0.5f, 0.2f, 1), new(float.NaN, 0.5f, 0.2f, 1), new(-1, -0.5f, 0.3f, 1), new(0.18f, 0.18f, 0.18f, 1),
            new(-1, -1, -1, 1), new(0.5f, float.PositiveInfinity, 0, 1),
        ];
        using var storage = _renderer.CreateStorage<Vector4>("test.bands", bands);
        _renderer.Hdr = true;
        var rgb = Shot(f =>
        {
            var b = f.Graph.Import(storage);
            f.Graph.AddPass("test.bands").Color(f.SceneColor, _renderer.SceneColorClear).Read(b).Execute(ctx =>
            {
                ctx.SetPipeline(Bands);
                ctx.SetConstant(0, bands.Length);
                ctx.SetConstant(1, ctx.Width);
                ctx.SetConstant(4, f.Graph.SrvOf(b));
                ctx.Draw(3);
            });
        }, "nonfinite");
        int w = _renderer.Width, band = w / bands.Length;
        for (int i = 0; i < bands.Length; i++)
        {
            var (r, g, b) = FilmicTonemap.DisplayBytes(new Vector3((float)(Half)bands[i].X, (float)(Half)bands[i].Y, (float)(Half)bands[i].Z), _renderer.Exposure);
            int p = ((_renderer.Height / 2) * w + i * band + band / 2) * 3;
            Assert.True(Math.Abs(rgb[p] - r) <= 1 && Math.Abs(rgb[p + 1] - g) <= 1 && Math.Abs(rgb[p + 2] - b) <= 1,
                $"полоса {i} {bands[i]}: GPU ({rgb[p]}, {rgb[p + 1]}, {rgb[p + 2]}), эталон ({r}, {g}, {b})");
        }
        Assert.Equal(5 * band * _renderer.Height, _renderer.LastCapture!.Hdr!.NonFinite);
        AssertNoDebugMessages();
    }

    [Fact]
    public void Hdr_BackBufferBeforePost_RejectedAndRendererRecovers()
    {
        _renderer.Hdr = true;
        var error = Assert.Throws<InvalidOperationException>(() => Frame(f =>
        {
            f.Graph.AddPass("test.hdr").Color(f.SceneColor, _renderer.SceneColorClear);
            f.Graph.AddPass("test.early", FramePhase.Transparent).Color(f.Graph.BackBuffer);
        }));
        Assert.Contains("test.early", error.Message);
        Assert.Contains("задний буфер", error.Message);

        // Интерфейс и отладка — после тонмапа, поверх него: разрешено.
        Frame(f =>
        {
            f.Graph.AddPass("test.debug", FramePhase.Debug).Color(f.Graph.BackBuffer);
            f.Graph.AddPass("test.hdr").Color(f.SceneColor, _renderer.SceneColorClear);
            f.Graph.AddPass("test.ui", FramePhase.Ui).Color(f.Graph.BackBuffer);
        });
        Assert.Equal(["test.hdr", "post.tonemap", "test.ui", "test.debug"], _renderer.Graph.LastFrame.Select(p => p.Name));
        AssertNoDebugMessages();
    }

    [Fact]
    public void SceneColorClear_Change_RecreatesTarget_WrongClearRejected()
    {
        _renderer.Hdr = true;
        var clear = new Vector4(0.1f, 0.2f, 0.3f, 1);
        var wrong = Assert.Throws<InvalidOperationException>(() => Frame(f => f.Graph.AddPass("test.hdr").Color(f.SceneColor, clear)));
        Assert.Contains("SceneColorClear", wrong.Message);
        _renderer.SceneColorClear = clear;
        var rgb = Shot(f => f.Graph.AddPass("test.hdr").Color(f.SceneColor, clear), "clear");
        var (r, g, b) = FilmicTonemap.DisplayBytes(new Vector3((float)(Half)0.1f, (float)(Half)0.2f, (float)(Half)0.3f), _renderer.Exposure);
        Assert.True(Math.Abs(rgb[0] - r) <= 1 && Math.Abs(rgb[1] - g) <= 1 && Math.Abs(rgb[2] - b) <= 1, $"({rgb[0]}, {rgb[1]}, {rgb[2]}) против ({r}, {g}, {b})");
        AssertNoDebugMessages();
    }
}
