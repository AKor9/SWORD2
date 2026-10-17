using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// TAA на устройстве (<see cref="TemporalAa"/>) на аналитическом полу в полоску: в покое края полос сглаживаются (полутона), ровное —
/// как без TAA; первый кадр после сдвига камеры почти совпадает с TAA, сошедшимся на новом месте (двойных краёв нет); размытие движения
/// появляется только при движении камеры; выключенное — проходов нет. Любое сообщение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class TaaGpuTests : IDisposable, ILogSink
{
    private const int W = 160, H = 96;
    private const string Shader = "taa_tests.hlsl";
    private static readonly GraphicsPipelineDesc Stripes = new(Shader, "VsFull", "PsStripes", Depth: DepthMode.TestWrite);
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-taa-{Guid.NewGuid():N}");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct StripesFrame
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector3 Eye; public float Pad;
    }

    public TaaGpuTests()
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

    private static Matrix4x4 Camera(Vector3 eye)
    {
        var view = Matrix4x4.CreateLookAt(eye, eye + new Vector3(0.1f, -3, -4), Vector3.UnitY);
        float ys = 1f / MathF.Tan(0.45f), xs = ys / (W / (float)H);
        return view * new Matrix4x4(xs, 0, 0, 0, 0, ys, 0, 0, 0, 0, 0, -1, 0, 0, 0.1f, 0);
    }

    /// <summary>Кадр пола с глаза <paramref name="eye"/>; со снимком — красный канал по строкам сверху вниз.</summary>
    private byte[]? Frame(Vector3 eye, bool shot)
    {
        var taa = _renderer.Taa;
        var vp = Camera(eye);
        var jittered = taa.Jitter(vp, W, H);
        taa.SetView(vp, jittered);
        Matrix4x4.Invert(jittered, out var inv);
        var data = new StripesFrame { ViewProj = jittered, InvViewProj = inv, Eye = eye };
        string path = Path.Combine(_dir, $"t{Guid.NewGuid():N}.bmp");
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            f.SetFrameConstants(data);
            f.Graph.AddPass("test.stripes").Color(f.SceneColor, _renderer.SceneColorClear).Color(f.Scene.Normal, SceneTargets.NormalClear)
                .Depth(f.Scene.Depth, DepthAccess.Write, clear: 0f).Execute(ctx =>
                {
                    ctx.SetPipeline(Stripes);
                    ctx.SetConstant(0, (uint)W);
                    ctx.SetConstant(1, (uint)H);
                    ctx.Draw(3);
                });
        }, vsync: false, shot ? new FrameCapture(path) : null);
        if (!shot) return null;
        var file = File.ReadAllBytes(path);
        int stride = (W * 3 + 3) & ~3;
        var red = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                red[y * W + x] = file[54 + (H - 1 - y) * stride + x * 3 + 2];
        return red;
    }

    private static readonly Vector3 Eye = new(0, 3, 4);

    /// <summary>Пикселей между тёмной и светлой полосой (не ближе 6 байт к краям размаха кадра).</summary>
    private static int InBetween(byte[] red)
    {
        int lo = 255, hi = 0, n = 0;
        foreach (var v in red)
        {
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }
        foreach (var v in red)
            if (v > lo + 6 && v < hi - 6) n++;
        return n;
    }

    [Fact]
    public void Static_EdgesSmoothed_FlatUntouched_OffHasNoPasses()
    {
        var aliased = Frame(Eye, shot: true)!;
        Assert.DoesNotContain(_renderer.Graph.LastFrame, p => p.Name == "post.taa");
        _renderer.Taa.Enabled = true;
        for (int i = 0; i < 40; i++) Frame(Eye, shot: false);
        var smooth = Frame(Eye, shot: true)!;
        Assert.Contains(_renderer.Graph.LastFrame, p => p.Name == "post.taa");
        int before = InBetween(aliased), after = InBetween(smooth);
        // Ровное: пиксель, у которого вся окрестность 5×5 без TAA одного цвета, — тот же ±2.
        int flat = 0, moved = 0;
        for (int y = 2; y < H - 2; y++)
            for (int x = 2; x < W - 2; x++)
            {
                bool same = true;
                for (int dy = -2; dy <= 2 && same; dy++)
                    for (int dx = -2; dx <= 2 && same; dx++)
                        same = aliased[(y + dy) * W + x + dx] == aliased[y * W + x];
                if (!same) continue;
                flat++;
                if (Math.Abs(smooth[y * W + x] - aliased[y * W + x]) > 2) moved++;
            }
        Assert.True(after > 2 * before + 40, $"полутонов на краях: с TAA {after}, без {before}");
        Assert.True(flat > W * H / 8 && moved < flat / 100, $"ровных {flat}, изменилось {moved}");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    [Fact]
    public void CameraMove_NoDoubleEdges_MotionBlurOnlyWhenMoving()
    {
        var taa = _renderer.Taa;
        taa.Enabled = true;
        for (int i = 0; i < 30; i++) Frame(Eye, shot: false);
        // Сдвиг камеры на 0.2 м вбок за кадр: история — по глубине, первый кадр на новом месте почти тот же, что сошедшийся там.
        var moved = Eye + new Vector3(0.2f, 0, 0);
        var first = Frame(moved, shot: true)!;
        for (int i = 0; i < 40; i++) Frame(moved, shot: false);
        var settled = Frame(moved, shot: true)!;
        // След старого кадра — полосы на прежнем месте: по строкам первый кадр лучше всего совпадает с сошедшимся без сдвига, а средняя
        // разница мала (на краях первый кадр мягче, пока история снова не сойдётся).
        for (int row = 16; row < H - 8; row += 16)
        {
            int best = 0;
            long bestErr = long.MaxValue;
            for (int shift = -10; shift <= 10; shift++)
            {
                long err = 0;
                for (int x = 12; x < W - 12; x++) err += Math.Abs(first[row * W + x] - settled[row * W + x + shift]);
                if (err < bestErr) (bestErr, best) = (err, shift);
            }
            Assert.True(best == 0, $"строка {row}: первый кадр после сдвига совпадает с сошедшимся при сдвиге {best} px");
        }
        double mean = 0;
        for (int i = 0; i < settled.Length; i++) mean += Math.Abs(first[i] - settled[i]);
        mean /= settled.Length;
        Assert.True(mean < 8, $"средняя разница первого кадра после сдвига и сошедшегося — {mean:0.#}");

        // Размытие: в покое кадр тот же, что без него; при движении — полутонов больше.
        taa.Enabled = true;
        taa.MotionBlur = false;
        for (int i = 0; i < 30; i++) Frame(Eye, shot: false);
        var still = Frame(Eye, shot: true)!;
        taa.MotionBlur = true;
        for (int i = 1; i < TemporalAa.JitterPhases; i++) Frame(Eye, shot: false);
        var stillBlur = Frame(Eye, shot: true)!;
        // Та же фаза дрожания (через JitterPhases кадров): TAA сошёлся — допуск в несколько байт; размытия в покое нет.
        int worst = 0;
        for (int i = 0; i < still.Length; i++) worst = Math.Max(worst, Math.Abs(still[i] - stillBlur[i]));
        Assert.True(worst <= 6, $"в покое с размытием и без отличаются на {worst}");
        Assert.Contains(_renderer.Graph.LastFrame, p => p.Name == "post.motionBlur");
        // Выдержка 1 — размытие на всё перемещение за кадр: TAA и без него смягчает кадры в движении.
        taa.Shutter = 1;
        int sharp = 0, blurred = 0;
        for (int i = 1; i <= 4; i++)
        {
            taa.MotionBlur = false;
            sharp += InBetween(Frame(Eye + new Vector3(0.25f * i, 0, 0), shot: true)!);
        }
        for (int i = 5; i <= 8; i++)
        {
            taa.MotionBlur = true;
            blurred += InBetween(Frame(Eye + new Vector3(0.25f * i, 0, 0), shot: true)!);
        }
        Assert.True(blurred > sharp * 1.15, $"полутонов в движении: с размытием {blurred}, без {sharp}");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }
}
