using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// SMAA на устройстве: кадр GPU = CPU-эталон <see cref="SmaaMath.Apply"/> над тем же кадром без SMAA, лесенки смягчаются
/// только у краёв, прямые края во весь кадр не трогаются, выключенный — проходов нет. Любое сообщение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class SmaaGpuTests : IDisposable, ILogSink
{
    private const string Shader = "graph_tests.hlsl";
    private static readonly GraphicsPipelineDesc Shapes = new(Shader, "VsFull", "PsShapes");
    private static readonly GraphicsPipelineDesc Bands = new(Shader, "VsFull", "PsBands");
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-smaa-{Guid.NewGuid():N}");

    public SmaaGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 128, 96, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
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
        if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Кадр со снимком цвета в BMP: пиксели RGB по строкам сверху вниз.</summary>
    private byte[] Shot(Action<RenderFrame> build, string name)
    {
        string path = Path.Combine(_dir, name + ".bmp");
        _window.PumpMessages();
        _renderer.Render(build, vsync: false, new FrameCapture(path));
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

    private void ShapesScene(RenderFrame f) => f.Graph.AddPass("test.shapes").Color(f.SceneColor, _renderer.SceneColorClear).Execute(ctx =>
    {
        ctx.SetPipeline(Shapes);
        ctx.Draw(3);
    });

    private IEnumerable<string> LastPasses => _renderer.Graph.LastFrame.Select(p => p.Name);

    [Fact]
    public void Smaa_MatchesCpuReference_OnTonemappedFrame()
    {
        int w = _renderer.Width, h = _renderer.Height;
        _renderer.Smaa = false;
        var before = Shot(ShapesScene, "shapes.off");
        Assert.DoesNotContain("post.smaa.blend", LastPasses);
        _renderer.Smaa = true;
        var after = Shot(ShapesScene, "shapes.on");
        Assert.Equal(["post.tonemap", "post.smaa.edges", "post.smaa.weights", "post.smaa.blend"],
            LastPasses.Where(n => n.StartsWith("post.", StringComparison.Ordinal)));

        var expected = SmaaMath.Apply(before, w, h);
        int changed = 0, worst = 0, worstAt = -1;
        for (int i = 0; i < after.Length; i++)
        {
            if (expected[i] != before[i]) changed++;
            int d = Math.Abs(after[i] - expected[i]);
            if (d > worst) (worst, worstAt) = (d, i);
        }
        Assert.True(changed > 100, $"эталон сгладил лишь {changed} каналов — сцена без лесенок?");
        // Веса — байты цели, sRGB — степенью GPU: у тёмного конца кодирования ступень в 1/255 линейного — до пары байтов.
        Assert.True(worst <= 2, $"GPU и эталон расходятся на {worst} в канале {worstAt % 3} пикселя ({worstAt / 3 % w}, {worstAt / 3 / w})");
        AssertNoDebugMessages();
    }

    [Fact]
    public void Smaa_SoftensOnlyPixelsAtEdges_WithInBetweenColours()
    {
        int w = _renderer.Width, h = _renderer.Height;
        _renderer.Smaa = false;
        var before = Shot(ShapesScene, "edges.off");
        _renderer.Smaa = true;
        var after = Shot(ShapesScene, "edges.on");

        bool Same(int a, int b) => before[a * 3] == before[b * 3] && before[a * 3 + 1] == before[b * 3 + 1] && before[a * 3 + 2] == before[b * 3 + 2];
        int softened = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (after[p * 3] == before[p * 3] && after[p * 3 + 1] == before[p * 3 + 1] && after[p * 3 + 2] == before[p * 3 + 2]) continue;
                softened++;
                // Сменился — значит, у края: сосед другого цвета, а новый цвет — между своим и соседским.
                int[] near = [p - 1, p + 1, p - w, p + w];
                var other = near.Where(n => n >= 0 && n < w * h && Math.Abs(n % w - x) <= 1 && !Same(n, p)).ToArray();
                Assert.True(other.Length > 0, $"пиксель ({x}, {y}) вдали от края изменён");
                Assert.Contains(other, n => Enumerable.Range(0, 3).All(c =>
                    after[p * 3 + c] >= Math.Min(before[p * 3 + c], before[n * 3 + c]) && after[p * 3 + c] <= Math.Max(before[p * 3 + c], before[n * 3 + c])));
            }
        Assert.True(softened > 40, $"смягчено лишь {softened} пикселей");
        AssertNoDebugMessages();
    }

    [Fact]
    public void Smaa_StraightEdgesAcrossFrame_Untouched_AndOffHasNoPasses()
    {
        Vector4[] bands = [new(0, 0, 0, 1), new(1, 1, 1, 1), new(0.05f, 0.4f, 0.9f, 1), new(8, 0.5f, 0, 1), new(0.18f, 0.18f, 0.18f, 1)];
        using var storage = _renderer.CreateStorage<Vector4>("test.bands", bands);
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

        _renderer.Smaa = false;
        var off = Shot(Scene, "bands.off");
        Assert.DoesNotContain(LastPasses, n => n.StartsWith("post.smaa", StringComparison.Ordinal));
        _renderer.Smaa = true;
        var on = Shot(Scene, "bands.on");
        Assert.Contains("post.smaa.blend", LastPasses);
        Assert.Equal(off, on);
        AssertNoDebugMessages();
    }
}
