using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Постобработка на устройстве (<see cref="PostEffects"/>) над полосами HDR-сцены: нейтральные настройки — прежний кадр бит в бит;
/// виньетка не трогает центр и темнит углы; насыщенность 0 — серое; теплее — красный выше, синий ниже; резкость не трогает ровные
/// полосы и усиливает перепад на их стыке. Любое сообщение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class PostEffectsGpuTests : IDisposable, ILogSink
{
    private const int W = 128, H = 64;
    private const string Shader = "graph_tests.hlsl";
    private static readonly GraphicsPipelineDesc Bands = new(Shader, "VsFull", "PsBands");
    private static readonly Vector4[] Colours = [new(0.12f, 0.12f, 0.12f, 1), new(0.3f, 0.12f, 0.05f, 1), new(0.05f, 0.2f, 0.4f, 1), new(0.22f, 0.22f, 0.22f, 1)];
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly GpuStorage _bands;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-post-{Guid.NewGuid():N}");

    public PostEffectsGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", W, H, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
        _bands = _renderer.CreateStorage<Vector4>("test.bands", Colours);
        Directory.CreateDirectory(_dir);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _bands.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Кадр полос со снимком: RGB по строкам сверху вниз.</summary>
    private byte[] Shot()
    {
        string path = Path.Combine(_dir, $"p{Guid.NewGuid():N}.bmp");
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            var g = f.Graph;
            var b = g.Import(_bands);
            g.AddPass("test.bands").Color(f.SceneColor, _renderer.SceneColorClear).Read(b).Execute(ctx =>
            {
                ctx.SetPipeline(Bands);
                ctx.SetConstant(0, Colours.Length);
                ctx.SetConstant(1, ctx.Width);
                ctx.SetConstant(4, g.SrvOf(b));
                ctx.Draw(3);
            });
        }, vsync: false, new FrameCapture(path));
        var file = File.ReadAllBytes(path);
        int stride = (W * 3 + 3) & ~3;
        var rgb = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int src = 54 + (H - 1 - y) * stride + x * 3, dst = (y * W + x) * 3;
                (rgb[dst], rgb[dst + 1], rgb[dst + 2]) = (file[src + 2], file[src + 1], file[src]);
            }
        return rgb;
    }

    private static (int R, int G, int B) At(byte[] rgb, int x, int y) => (rgb[(y * W + x) * 3], rgb[(y * W + x) * 3 + 1], rgb[(y * W + x) * 3 + 2]);

    [Fact]
    public void NeutralIsBitExact_VignetteCornersOnly_SaturationZeroGrey_WarmerRedder()
    {
        var post = _renderer.Post;
        var plain = Shot();
        post.Vignette = 0.6f;
        var vignette = Shot();
        post.Vignette = 0;
        Assert.Equal(plain, Shot());
        // Центр кадра (r = 0) — как без виньетки; угол — темнее.
        Assert.Equal(At(plain, W / 2, H / 2), At(vignette, W / 2, H / 2));
        Assert.True(At(vignette, 0, 0).R < At(plain, 0, 0).R - 5, $"угол {At(vignette, 0, 0)} против {At(plain, 0, 0)}");

        post.Saturation = 0;
        var grey = Shot();
        post.Saturation = 1;
        for (int band = 0; band < Colours.Length; band++)
        {
            var (r, g, b) = At(grey, band * W / Colours.Length + 8, H / 2);
            Assert.True(Math.Abs(r - g) <= 1 && Math.Abs(g - b) <= 1, $"полоса {band}: {(r, g, b)}");
        }

        post.Temperature = 1;
        var warm = Shot();
        post.Temperature = 0;
        var (pr, _, pb) = At(plain, 3 * W / 4 + 8, H / 2);   // серая полоса
        var (wr, _, wb) = At(warm, 3 * W / 4 + 8, H / 2);
        Assert.True(wr > pr && wb < pb, $"теплее: {(wr, wb)} против {(pr, pb)}");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    [Fact]
    public void Sharpen_FlatUntouched_EdgeStronger()
    {
        var post = _renderer.Post;
        var plain = Shot();
        post.Sharpen = 1;
        var sharp = Shot();
        Assert.Contains(_renderer.Graph.LastFrame, p => p.Name == "post.sharpen");
        post.Sharpen = 0;
        Assert.Equal(plain, Shot());
        Assert.DoesNotContain(_renderer.Graph.LastFrame, p => p.Name == "post.sharpen");
        int edge = 3 * W / 4;   // стык тёмно-синей и серой полос
        Assert.Equal(At(plain, edge - 8, H / 2), At(sharp, edge - 8, H / 2));
        Assert.Equal(At(plain, edge + 8, H / 2), At(sharp, edge + 8, H / 2));
        int before = Math.Abs(At(plain, edge, H / 2).R - At(plain, edge - 1, H / 2).R);
        int after = Math.Abs(At(sharp, edge, H / 2).R - At(sharp, edge - 1, H / 2).R);
        Assert.True(after > before, $"перепад на стыке: {after} против {before}");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }
}
