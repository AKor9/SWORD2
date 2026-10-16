using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Formats;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Xunit.Abstractions;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Точечный свет на устройстве: плоскость под источниками против <see cref="PointLightMath"/> по сырому HDR (half), оба режима
/// затухания, дальность показа, предел 64 источников, light function и её UV, 0 Б на кадр. Любое сообщение отладочного слоя —
/// провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class PointLightsGpuTests : IDisposable, ILogSink
{
    private const string Shader = "lights_tests.hlsl";
    private const int Side = 64;
    private const float Step = 0.25f;   // метров на пиксель: плоскость 16 × 16 м
    private static readonly GraphicsPipelineDesc Plane = new(Shader, "VsFull", "PsPlane");
    private static readonly GraphicsPipelineDesc Uv = new(Shader, "VsFull", "PsUv");
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-lights-{Guid.NewGuid():N}");
    private readonly Vector3 _eye = new(0, 10, 0);
    private readonly Action<RenderPassContext> _recordPlane;
    private int _lightsSrv;

    public PointLightsGpuTests(ITestOutputHelper output)
    {
        _output = output;
        _recordPlane = RecordPlane;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", Side, Side, Activate: false));
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
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Точка плоскости под пикселем (x, y) — как в <c>PsPlane</c>.</summary>
    private static Vector3 World(int x, int y) => new((x + 0.5f - Side / 2f) * Step, 0, (y + 0.5f - Side / 2f) * Step);

    // Делегат прохода — один на тест: кадр без выделений.
    private void PlanePass(RenderFrame f)
    {
        _lightsSrv = f.Lights.Srv;
        f.Graph.AddPass("test.plane").Color(f.SceneColor, _renderer.SceneColorClear).Execute(_recordPlane);
    }

    private void RecordPlane(RenderPassContext ctx)
    {
        ctx.SetPipeline(Plane);
        ctx.SetConstant(0, _lightsSrv);
        ctx.SetConstant(1, _eye.X);
        ctx.SetConstant(2, _eye.Y);
        ctx.SetConstant(3, _eye.Z);
        ctx.SetConstant(4, Step);
        ctx.SetConstant(5, ctx.Width);
        ctx.SetConstant(6, ctx.Height);
        ctx.Draw(3);
    }

    /// <summary>Кадр с источниками <paramref name="add"/> над плоскостью; вернёт сырой цвет сцены (RGB на пиксель, строки сверху вниз).</summary>
    private Vector3[] Scene(Action<PointLights> add, string name)
    {
        Directory.CreateDirectory(_dir);
        string hdr = Path.Combine(_dir, name + ".hdr.png");
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            f.Lights.ViewOrigin = _eye;
            add(f.Lights);
            PlanePass(f);
        }, vsync: false, new FrameCapture(Path.Combine(_dir, name + ".bmp")) { HdrPath = hdr });
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin")));
        var rgb = new Vector3[Side * Side];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = new((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2]);
        return rgb;
    }

    /// <summary>Каждый пиксель плоскости — сумма эталона по источникам (half: относительно 2·10⁻³).</summary>
    private static void AssertPlane(Vector3[] gpu, IReadOnlyList<(PointLight Light, float Fade, float Lf)> lights, string what)
    {
        float worst = 0;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                var expected = Vector3.Zero;
                foreach (var (light, fade, lf) in lights) expected += PointLightMath.Diffuse(light, World(x, y), Vector3.UnitY, fade, lf);
                var got = gpu[y * Side + x];
                for (int c = 0; c < 3; c++)
                {
                    float e = expected[c], g = got[c], err = MathF.Abs(g - e);
                    Assert.True(err <= 2e-3f * MathF.Abs(e) + 1e-5f, $"{what}: пиксель ({x}, {y}) канал {c}: GPU {g}, эталон {e}");
                    worst = MathF.Max(worst, err / MathF.Max(MathF.Abs(e), 1e-3f));
                }
            }
    }

    private static readonly PointLight Torch = PointLight.Torch(new Vector3(-2, 1.5f, -1));
    private static readonly PointLight Bulb = new(new Vector3(3, 2, 2), new Vector3(0.6f, 0.8f, 1f), 20, 6, LightFalloff.InverseSquared);

    [Fact]
    public void Plane_MatchesCpuReference_BothFalloffs()
    {
        var gpu = Scene(l =>
        {
            l.Add(Torch);
            l.Add(Bulb);
        }, "both");
        AssertPlane(gpu, [(Torch, 1, 1), (Bulb, 1, 1)], "факел и лампа");
        // Факел над (−2, −1): под ним на плоскости — 100·exp(−2.25)/π ≈ 3.3 в красном; за R лампы (6 м) — только факел.
        var under = gpu[(int)(-1 / Step + Side / 2) * Side + (int)(-2 / Step + Side / 2)];
        Assert.InRange(under.X, 3f, 3.6f);
        var report = _renderer.Lights.Report();
        Assert.Equal((2, 0, 0), (report.Shown, report.Dropped, report.Hidden));

        var none = Scene(_ => { }, "none");
        Assert.All(none, v => Assert.Equal(Vector3.Zero, v));
        Assert.Equal(0, _renderer.Lights.Report().Shown);
        AssertNoDebugMessages();
    }

    [Fact]
    public void DrawDistance_ZeroBeyond_LinearInBand()
    {
        // Глаз в 10 м над центром: факел в 8.8 м от глаза — предел 12, полоса 4, угасание (12 − 8.8)/4 ≈ 0.8; лампа в 8.8 м — предел 8, скрыта.
        var fading = Torch with { MaxDrawDistance = 12, FadeRange = 4 };
        var hidden = Bulb with { MaxDrawDistance = 8, FadeRange = 2 };
        float distance = Vector3.Distance(fading.Position, _eye);
        float fade = (12 - distance) / 4;
        var gpu = Scene(l =>
        {
            l.Add(fading);
            l.Add(hidden);
        }, "fade");
        AssertPlane(gpu, [(fading, fade, 1)], "угасание по дальности");
        var report = _renderer.Lights.Report();
        Assert.Equal((1, 0, 1), (report.Shown, report.Dropped, report.Hidden));
        Assert.Equal(fade, report.Lights[0].Fade, 1e-5f);
        AssertNoDebugMessages();
    }

    [Fact]
    public void Over64_ExtraDropped_AndCounted()
    {
        // 70 одинаковых видимых и 3 скрытых: в буфер — первые 64 видимых, итог — ровно 64 × один источник.
        var one = new PointLight(new Vector3(0, 3, 0), new Vector3(0.01f, 0.02f, 0.03f), 1, 8, LightFalloff.Exponent, 2);
        var gone = one with { MaxDrawDistance = 1 };
        var gpu = Scene(l =>
        {
            for (int i = 0; i < 70; i++)
            {
                if (i % 30 == 0) l.Add(gone);
                l.Add(one);
            }
        }, "many");
        AssertPlane(gpu, [(one with { Intensity = PointLights.MaxLights }, 1, 1)], "64 источника");
        var report = _renderer.Lights.Report();
        Assert.Equal((64, 6, 3), (report.Shown, report.Dropped, report.Hidden));
        Assert.Equal(64, report.Lights.Count);
        AssertNoDebugMessages();
    }

    private static byte[] Solid(int side, byte r)
    {
        var rgba = new byte[side * side * 4];
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, 0, 0, 255);
        return rgba;
    }

    [Fact]
    public void LightFunction_ClampedToOne_FadesToDisabled_MinusOneIsOne()
    {
        using var a60 = _renderer.CreateImage("test.lf.a60", 8, 8, Solid(8, 60), mips: false);
        using var b40 = _renderer.CreateImage("test.lf.b40", 8, 8, Solid(8, 40), mips: false);
        using var a200 = _renderer.CreateImage("test.lf.a200", 8, 8, Solid(8, 200), mips: false);
        using var b150 = _renderer.CreateImage("test.lf.b150", 8, 8, Solid(8, 150), mips: false);
        var dim = new LightFunction(a60, b40) { Time = 3.7 };
        var bright = new LightFunction(a200, b150) { Time = 12.1 };
        var far = new LightFunction(a60, b40) { FadeDistance = 10, DisabledBrightness = 0.8f };   // вся плоскость дальше 10 м от глаза
        var none = new LightFunction(a60, b40) { FadeDistance = 0, DisabledBrightness = 0.3f };   // дальность 0 — всюду яркость без LF
        var gpu = Scene(l =>
        {
            int d = l.AddFunction(dim), b = l.AddFunction(bright), f = l.AddFunction(far), z = l.AddFunction(none);
            l.Add(Torch with { Function = d });
            l.Add(Bulb with { Function = b });
            l.Add(Torch with { Position = new Vector3(3, 1, -3), Function = f });
            l.Add(Bulb with { Position = new Vector3(-3, 2, 4), Function = -1 });
            l.Add(Torch with { Position = new Vector3(5, 1, 5), Function = z });
        }, "lf");
        // Постоянные текстуры: сумма 100/255 — множитель; 350/255 — насыщение до 1; за дальностью угасания — 0.8; −1 — 1; дальность 0 — 0.3
        // (эталон — PointLightMath.LightFunctionValue).
        AssertPlane(gpu,
        [
            (Torch, 1, 100f / 255), (Bulb, 1, 1), (Torch with { Position = new Vector3(3, 1, -3) }, 1, 0.8f),
            (Bulb with { Position = new Vector3(-3, 2, 4) }, 1, 1),
            (Torch with { Position = new Vector3(5, 1, 5) }, 1, PointLightMath.LightFunctionValue(100f / 255, 10, 0, 0.3f)),
        ], "light function");
        Assert.Equal(4, _renderer.Lights.Report().Functions);

        // Сверх предела light functions — −1 и счётчик.
        _renderer.Render(f =>
        {
            for (int i = 0; i < PointLights.MaxFunctions; i++) Assert.Equal(i, f.Lights.AddFunction(dim));
            Assert.Equal(-1, f.Lights.AddFunction(dim));
            PlanePass(f);
        }, vsync: false);
        Assert.Equal((PointLights.MaxFunctions, 1), (_renderer.Lights.Report().Functions, _renderer.Lights.Report().DroppedFunctions));
        AssertNoDebugMessages();
    }

    [Fact]
    [Trait("Category", "Content")]
    public void LightFunction_OnPakTexture_MatchesSameImage()
    {
        // Шум крови пака (BC4) как узор LF: текстура пака против той же картинки (слой 0, мип 0, CPU-декод → RGBA8) через GpuImage.
        // Разница — декод BCn железом против CPU (допуск TextureDecode.Tolerance), округление картинки до байта и half цели сцены.
        // Вторая текстура — чёрная: сумма не насыщается, LF = узор.
        const string NoisePath = "textures/blood/noise.swtex";
        TextureData data;
        using (var pak = ContentLocator.Open())
        using (var s = pak.OpenRead(NoisePath)) data = TextureData.Read(s, NoisePath);
        using var texture = _renderer.LoadTexture(NoisePath);
        var texels = TextureDecode.Decode(data, 0, 0);
        var rgba = new byte[texels.Length * 4];
        float tolerance = 0;
        for (int i = 0; i < texels.Length; i++)
        {
            (rgba[i * 4], rgba[i * 4 + 3]) = ((byte)MathF.Round(texels[i].X * 255), 255);
            tolerance = MathF.Max(tolerance, TextureDecode.Tolerance(data, 0, 0, i % data.Width, i / data.Width, 0));
        }
        tolerance += 0.5f / 255 + 2e-3f;
        using var image = _renderer.CreateImage("test.lf.noise", data.Width, data.Height, rgba, mips: false);
        using var black = _renderer.CreateImage("test.lf.black", 8, 8, Solid(8, 0), mips: false);
        var light = Torch with { Position = new Vector3(0, 1.5f, 0), Exponent = 8 };
        var onTexture = new LightFunction(texture, texture) { TextureB = black.Srv, ScaleA = 1, Time = 0.3 };
        var onImage = onTexture with { TextureA = image.Srv };
        var fromTexture = Scene(l => l.Add(light with { Function = l.AddFunction(onTexture) }), "lf-pak");
        var fromImage = Scene(l => l.Add(light with { Function = l.AddFunction(onImage) }), "lf-image");
        float lo = 1, hi = 0, worst = 0;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                float unlit = PointLightMath.Diffuse(light, World(x, y), Vector3.UnitY).X;   // тот же пиксель с LF = 1
                if (unlit < 1e-2f) continue;
                float lfTexture = fromTexture[y * Side + x].X / unlit, lfImage = fromImage[y * Side + x].X / unlit;
                Assert.True(MathF.Abs(lfTexture - lfImage) <= tolerance,
                    $"пиксель ({x}, {y}): LF на текстуре пака {lfTexture}, на картинке {lfImage}, допуск {tolerance}");
                (lo, hi, worst) = (MathF.Min(lo, lfImage), MathF.Max(hi, lfImage), MathF.Max(worst, MathF.Abs(lfTexture - lfImage)));
            }
        _output.WriteLine($"{NoisePath} ({data.Format} {data.Width}×{data.Height}): LF {lo:F3}…{hi:F3}, расхождение до {worst:F4} при допуске {tolerance:F4}");
        Assert.True(hi - lo > 0.2f, $"узор не виден: LF {lo}…{hi}");
        AssertNoDebugMessages();
    }

    [Fact]
    public void LightFunctionUv_ShaderMatchesReference()
    {
        // Шесть осей (кроме шва −Y и полюсов по u) и случайные направления: UV шейдера = эталон; с поворотом осей LF (кватернион шейдера
        // TestRotation) — эталон по Vector3.Transform: тот же порядок поворота.
        var dirs = new List<Vector4>
        {
            new(1, 0, 0, 0), new(-1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(0, 0, -1, 0), new(0.3f, -0.8f, 0.5f, 0),
        };
        var rng = new Random(11);
        while (dirs.Count < Side)
            dirs.Add(new Vector4(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, 0));
        using var storage = _renderer.CreateStorage<Vector4>("test.dirs", dirs.ToArray());
        Directory.CreateDirectory(_dir);
        string hdr = Path.Combine(_dir, "uv.hdr.png");
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            var b = f.Graph.Import(storage);
            f.Graph.AddPass("test.uv").Color(f.SceneColor, _renderer.SceneColorClear).Read(b).Execute(ctx =>
            {
                ctx.SetPipeline(Uv);
                ctx.SetConstant(7, f.Graph.SrvOf(b));
                ctx.Draw(3);
            });
        }, vsync: false, new FrameCapture(Path.Combine(_dir, "uv.bmp")) { HdrPath = hdr });
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin")));
        for (int i = 0; i < dirs.Count; i++)
        {
            var d = Vector3.Normalize(new Vector3(dirs[i].X, dirs[i].Y, dirs[i].Z));
            var expected = PointLightMath.LightFunctionUv(d);
            float u = (float)halves[i * 4], v = (float)halves[i * 4 + 1];
            bool pole = MathF.Abs(d.Z) > 0.999f;   // на полюсе u не определено; у шва u = 0 | 1 — расстояние по кругу
            float du = MathF.Abs(u - expected.X);
            Assert.True((pole || MathF.Min(du, 1 - du) < 2e-3f) && MathF.Abs(v - expected.Y) < 2e-3f,
                $"направление {d}: GPU ({u}, {v}), эталон {expected}");
            var turned = Vector3.Transform(d, new Quaternion(0.2f, -0.5f, 0.3f, 0.78740079f));
            var expectedTurned = PointLightMath.LightFunctionUv(turned);
            float ut = (float)halves[i * 4 + 2], vt = (float)halves[i * 4 + 3];
            float dut = MathF.Abs(ut - expectedTurned.X);
            Assert.True((MathF.Abs(turned.Z) > 0.999f || MathF.Min(dut, 1 - dut) < 2e-3f) && MathF.Abs(vt - expectedTurned.Y) < 2e-3f,
                $"направление {d}, повёрнутое {turned}: GPU ({ut}, {vt}), эталон {expectedTurned}");
        }
        AssertNoDebugMessages();
    }

    [Fact]
    public void SteadyFrame_WithLights_AllocatesNothing()
    {
        using var a = _renderer.CreateImage("test.lf.a", 8, 8, Solid(8, 90), mips: false);
        var function = new LightFunction(a, a);
        void Build(RenderFrame f)
        {
            f.Lights.ViewOrigin = _eye;
            int lf = f.Lights.AddFunction(function with { Time = f.Number * 0.016 });
            // Больше предела буфера: отбор и отброс — тоже без выделений.
            for (int i = 0; i < 70; i++) f.Lights.Add(Torch with { Position = new Vector3(i % 8 - 4, 1, i / 8 - 4), Function = i % 2 == 0 ? lf : -1 });
            PlanePass(f);
        }
        Action<RenderFrame> build = Build;
        for (int i = 0; i < 30; i++)
        {
            _window.PumpMessages();
            _renderer.Render(build, vsync: false);
        }
        const int frames = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _renderer.Render(build, vsync: false);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр с источниками выделил {bytes} Б");
        Assert.Equal(PointLights.MaxLights, _renderer.Lights.Shown);
        AssertNoDebugMessages();
    }
}
