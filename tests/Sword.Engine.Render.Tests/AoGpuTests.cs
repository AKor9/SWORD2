using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// GTAO на устройстве (<see cref="AmbientOcclusion"/>): аналитический угол «пол + стена» — у стыка видимость заметно меньше 1,
/// открытые пол и стена дальше радиуса — около 1; больше радиус — темнее у стыка. Любое сообщение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class AoGpuTests : IDisposable, ILogSink
{
    private const int W = 256, H = 192;
    private const string Shader = "ao_tests.hlsl";
    private static readonly GraphicsPipelineDesc Corner = new(Shader, "VsFull", "PsCorner", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Show = new(Shader, "VsFull", "PsShowAo");
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-ao-{Guid.NewGuid():N}");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct CornerFrame
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector3 Eye; public float Pad;
    }

    public AoGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", W, H, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        _renderer.Ao.Enabled = true;
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
        if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Камера как у арены: обратный Z с бесконечной дальней плоскостью.</summary>
    private static (Matrix4x4 ViewProj, Matrix4x4 View) Camera(Vector3 eye, Vector3 target)
    {
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        float ys = 1f / MathF.Tan(0.45f), xs = ys / (W / (float)H);
        var proj = new Matrix4x4(xs, 0, 0, 0, 0, ys, 0, 0, 0, 0, 0, -1, 0, 0, 0.1f, 0);
        return (view * proj, view);
    }

    /// <summary>Видимость AO в точках мира (серый вывода карты, 0…1).</summary>
    private float[] Visibility(Vector3 eye, Vector3 target, params Vector3[] points)
    {
        var (viewProj, view) = Camera(eye, target);
        Matrix4x4.Invert(viewProj, out var inv);
        var frameData = new CornerFrame { ViewProj = viewProj, InvViewProj = inv, Eye = eye };
        string path = Path.Combine(_dir, $"ao{Guid.NewGuid():N}.bmp");
        for (int i = 0; i < 2; i++)
        {
            _window.PumpMessages();
            _renderer.Render(f =>
            {
                var g = f.Graph;
                var ao = f.Ao;
                Assert.True(ao.Prepare(f));
                f.SetFrameConstants(frameData);
                g.AddPass("test.corner").Color(f.Scene.Normal, SceneTargets.NormalClear).Depth(f.Scene.Depth, DepthAccess.Write, clear: 0f)
                    .Execute(ctx =>
                    {
                        ctx.SetPipeline(Corner);
                        ctx.SetConstant(0, (uint)W);
                        ctx.SetConstant(1, (uint)H);
                        ctx.Draw(3);
                    });
                ao.AddPasses(f, viewProj, eye, new Vector3(view.M11, view.M21, view.M31), new Vector3(view.M12, view.M22, view.M32));
                g.AddPass("test.show").Color(g.BackBuffer).Read(ao.Map).Execute(ctx =>
                {
                    ctx.SetPipeline(Show);
                    ctx.SetConstant(0, ao.Srv);
                    ctx.Draw(3);
                });
            }, vsync: false, i == 1 ? new FrameCapture(path) : null);
        }
        var file = File.ReadAllBytes(path);
        int stride = (W * 3 + 3) & ~3;
        var result = new float[points.Length];
        for (int k = 0; k < points.Length; k++)
        {
            var c = Vector4.Transform(new Vector4(points[k], 1), viewProj);
            int x = (int)((c.X / c.W * 0.5f + 0.5f) * W), y = (int)((0.5f - c.Y / c.W * 0.5f) * H);
            Assert.InRange(x, 0, W - 1);
            Assert.InRange(y, 0, H - 1);
            result[k] = file[54 + (H - 1 - y) * stride + x * 3 + 1] / 255f;
        }
        return result;
    }

    [Fact]
    public void Corner_DarkAtJoint_OpenSurfacesNearOne_WiderRadiusDarker()
    {
        var eye = new Vector3(0.6f, 1.8f, 3.2f);
        var target = new Vector3(0, 0.3f, 0);
        Vector3[] points = [new(0.3f, 0, 0.06f), new(0.3f, 0, 1.25f), new(-0.6f, 1.4f, 0)];
        _renderer.Ao.Radius = 0.8f;
        _renderer.Ao.Intensity = 1f;
        var v = Visibility(eye, target, points);
        _renderer.Ao.Radius = 1.6f;
        var wide = Visibility(eye, target, points);
        Assert.True(v[0] < 0.75f, $"у стыка {v[0]}");
        Assert.True(v[1] > 0.95f, $"открытый пол {v[1]}");
        Assert.True(v[2] > 0.95f, $"открытая стена {v[2]}");
        Assert.True(wide[0] < v[0], $"радиус 1.6 у стыка {wide[0]}, 0.8 — {v[0]}");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }
}
