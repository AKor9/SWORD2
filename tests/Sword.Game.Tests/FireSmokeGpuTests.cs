using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Game.Contracts;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Дым огня на настоящем устройстве (слой отладки D3D12) без игры: перспективная камера на объём, повёрнутый к ней под углом,
/// синтетический выход поля, источник «факел», солнце и небо, сцена с известной глубиной. Слой ½ (сырой снимок цели ½) = CPU-эталон
/// <see cref="SmokeMath.March"/> вдоль лучей; глубина сцены обрезает марш (за стеной дыма нет); на силуэте ближе объёма дыма нет, на
/// дальнем — есть; σ = 0 — кадр бит в бит без дыма; без света, солнца и неба — излучения нет; шагов по сетке = эталон; кадр — 0 Б.
/// Категория Gpu.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
public sealed class FireSmokeGpuTests : IDisposable, ILogSink
{
    private const int W = 161, H = 121, FieldSize = 96;
    private const float Near = 0.1f, FovY = 0.9f;
    private const string Shader = "fire_smoke_tests.hlsl";
    private static readonly GraphicsPipelineDesc ScenePipeline = new(Shader, "VsFull", "PsScene", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc FieldPipeline = new(Shader, "VsFull", "PsField");
    private static readonly Vector2 K = new(0, Near);

    private static readonly Vector3 Eye = new(0.3f, 0.25f, 3.2f), Target = new(0, 0.1f, 0);
    private static readonly Vector3 Forward = Vector3.Normalize(Target - Eye);
    private static readonly Matrix4x4 ViewProj = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY) *
        new Matrix4x4(1 / MathF.Tan(FovY / 2) / (W / (float)H), 0, 0, 0, 0, 1 / MathF.Tan(FovY / 2), 0, 0, 0, 0, 0, -1, 0, 0, Near, 0);

    private readonly ITestOutputHelper _output;
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly FireSmoke _smoke;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-smoke-{Guid.NewGuid():N}");
    private readonly SmokeVolume[] _volumes = new SmokeVolume[1];
    private readonly SmokeLight[] _lights = new SmokeLight[1];
    private readonly SmokeVolume _volume;
    private long _smokeBytes;

    public FireSmokeGpuTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.FireSmoke", W, H, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        FireSmoke.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders")), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
        _smoke = new FireSmoke(_renderer, 2, 2);
        // Объём — оси камеры, повёрнутые на 0.25 рад вокруг вертикали: лучи идут через слой наискось (в игре — к камере ровно).
        var basis = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
        Matrix4x4.Invert(basis, out var toWorld);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.25f);
        _volume = new SmokeVolume
        {
            Center = Vector3.Zero,
            AxisU = Vector3.Transform(Vector3.Normalize(new Vector3(toWorld.M11, toWorld.M12, toWorld.M13)), turn),
            AxisV = Vector3.Transform(Vector3.Normalize(new Vector3(toWorld.M21, toWorld.M22, toWorld.M23)), turn),
            AxisW = Vector3.Transform(Vector3.Normalize(new Vector3(toWorld.M31, toWorld.M32, toWorld.M33)), turn),
        };
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.WaitForIdle();
        _smoke.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
        if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
    }

    private static readonly SmokeLight Torch = SmokeLight.From(PointLight.Torch(new Vector3(0.1f, 0.2f, 0.05f)));
    private static readonly SceneSky Sky = new(new Vector3(0.2f, 0.25f, 0.3f), new Vector3(0.3f, 0.3f, 0.4f));

    /// <summary>Сцена: фон цвета <paramref name="background"/> с глубиной-рампой z0…z1 по столбцам, прямоугольник (пиксели, глубина, цвет).</summary>
    private readonly record struct Scene(Vector3 Background, float Z0, float Z1, (int X0, int Y0, int X1, int Y1) Rect = default, float RectZ = 1,
        Vector3 RectColour = default)
    {
        public float Depth(int x, int y) =>
            x >= Rect.X0 && y >= Rect.Y0 && x < Rect.X1 && y < Rect.Y1 ? RectZ : Z0 + (Z1 - Z0) * (x + 0.5f) / W;
    }

    private void Build(RenderFrame f, Scene scene, SmokeParams? smoke, SmokeLight light, SceneSky sky)
    {
        f.Graph.AddPass("test.scene").Color(f.SceneColor, _renderer.SceneColorClear).Depth(f.Scene.Depth, DepthAccess.Write, 0).Execute(ctx =>
        {
            ctx.SetPipeline(ScenePipeline);
            ctx.SetConstant(1, scene.Background.X);
            ctx.SetConstant(2, scene.Background.Y);
            ctx.SetConstant(3, scene.Background.Z);
            ctx.SetConstant(4, Near);
            ctx.SetConstant(5, scene.Z0);
            ctx.SetConstant(6, scene.Z1);
            ctx.SetConstant(7, ctx.Width);
            ctx.SetConstant(8, scene.Rect.X0);
            ctx.SetConstant(9, scene.Rect.Y0);
            ctx.SetConstant(10, scene.Rect.X1);
            ctx.SetConstant(11, scene.Rect.Y1);
            ctx.SetConstant(12, scene.RectZ);
            ctx.SetConstant(13, scene.RectColour.X);
            ctx.SetConstant(14, scene.RectColour.Y);
            ctx.SetConstant(15, scene.RectColour.Z);
            ctx.Draw(3);
        });
        if (smoke is null) return;
        var field = f.Graph.CreateTexture("test.field", new TextureDesc(TextureFormat.Rgba16Float, TextureUsage.ColorTarget, FieldSize, FieldSize));
        f.Graph.AddPass("test.field").Color(field, Vector4.Zero).Execute(ctx =>
        {
            ctx.SetPipeline(FieldPipeline);
            ctx.SetConstant(1, FieldSize);
            ctx.SetConstant(2, FieldSize);
            ctx.Draw(3);
        });
        _smoke.Params = smoke;
        _volumes[0] = _volume with { Field = field };
        _lights[0] = light;
        var half = f.HalfRes.Begin(K);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _smoke.Add(f, half, new SmokeView(ViewProj, Eye, Forward, SceneFog.None, sky), _volumes, _lights);
        _smokeBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        f.HalfRes.Composite();
    }

    /// <summary>Кадр со снимком: HDR сцены (полный размер) и сырая цель ½ (null — без дыма).</summary>
    private (Vector4[] Hdr, Vector4[]? Half) Shot(Scene scene, SmokeParams? smoke, string name, SmokeLight? light = null, SceneSky? sky = null)
    {
        Directory.CreateDirectory(_dir);
        string hdr = Path.Combine(_dir, name + ".hdr.png");
        string transparent = Path.Combine(_dir, name + ".transparent.png");
        _window.PumpMessages();
        _renderer.Render(f => Build(f, scene, smoke, light ?? Torch, sky ?? Sky), vsync: false,
            new FrameCapture(Path.Combine(_dir, name + ".png")) { HdrPath = hdr, TransparentPath = smoke is null ? null : transparent });
        var image = ReadHalf(Path.ChangeExtension(hdr, ".bin"));
        Assert.Equal(W * H, image.Length);
        var half = smoke is null ? null : ReadHalf(Path.ChangeExtension(transparent, ".bin"));
        if (half is not null) Assert.Equal(HalfW * HalfH, half.Length);
        return (image, half);
    }

    private const int HalfW = (W + 1) / 2, HalfH = (H + 1) / 2;

    private static Vector4[] ReadHalf(string path)
    {
        var halves = MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(path));
        var rgba = new Vector4[halves.Length / 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = new((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2], (float)halves[i * 4 + 3]);
        return rgba;
    }

    // ── CPU-эталон ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ρ поля — билинейно по текселям (значения — в half, как в цели RGBA16F), за краем — крайний тексель.</summary>
    private static float Rho(Vector2 uv)
    {
        float fx = uv.X * FieldSize - 0.5f, fy = uv.Y * FieldSize - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float wx = fx - x0, wy = fy - y0;
        float T(int x, int y)
        {
            x = Math.Clamp(x, 0, FieldSize - 1);
            y = Math.Clamp(y, 0, FieldSize - 1);
            float u = (x + 0.5f) / FieldSize, v = (y + 0.5f) / FieldSize;
            return (float)(Half)((1.5f * u - 0.25f) * (0.3f + 0.7f * v));
        }
        float top = T(x0, y0) + (T(x0 + 1, y0) - T(x0, y0)) * wx;
        float bottom = T(x0, y0 + 1) + (T(x0 + 1, y0 + 1) - T(x0, y0 + 1)) * wx;
        return top + (bottom - top) * wy;
    }

    /// <summary>Луч через полный пиксель (px, py) — как <c>SmokeRay</c>: NDC точки на ближней плоскости через обратную вид-проекцию.</summary>
    private static Vector3 Ray(float px, float py)
    {
        Matrix4x4.Invert(ViewProj, out var inv);
        var p = Vector4.Transform(new Vector4(px / W * 2 - 1, 1 - py / H * 2, 1, 1), inv);
        return Vector3.Normalize(new Vector3(p.X, p.Y, p.Z) / p.W - Eye);
    }

    /// <summary>Глубина ½ (ближайшая в блоке 2×2, край нечётного кадра — край) и параметр луча сцены.</summary>
    private static float SceneT(Scene scene, int hx, int hy, Vector3 dir)
    {
        int x0 = 2 * hx, y0 = 2 * hy, x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float z = MathF.Min(MathF.Min(scene.Depth(x0, y0), scene.Depth(x1, y0)), MathF.Min(scene.Depth(x0, y1), scene.Depth(x1, y1)));
        return z / MathF.Max(Vector3.Dot(dir, Forward), 1e-6f);
    }

    private SmokeSample Cpu(Scene scene, int hx, int hy, SmokeParams p, SmokeLight light, SceneSky sky)
    {
        var dir = Ray(2 * hx + 1, 2 * hy + 1);
        return SmokeMath.March(Eye, dir, SceneT(scene, hx, hy, dir), _volume.Center, _volume.AxisU, _volume.AxisV, _volume.AxisW, Rho,
            SmokeMath.SunRadiance(sky.Sun), SmokeMath.Ambient(sky.Sky, p), [light], p);
    }

    /// <summary>Отрезок луча близко к целому числу шагов: GPU и CPU могут округлить по-разному — такие лучи не сравниваются.</summary>
    private static bool NearStepBoundary(Scene scene, int hx, int hy, SmokeParams p, Vector3 center, Vector3 u, Vector3 v, Vector3 w)
    {
        var dir = Ray(2 * hx + 1, 2 * hy + 1);
        var (t0, t1, n) = SmokeMath.Segment(Eye, dir, SceneT(scene, hx, hy, dir), center, u, v, w, p);
        if (n == 0) return MathF.Abs(t1 - t0) < 1e-3f;
        float steps = (t1 - t0) / p.StepLength;
        return MathF.Abs(steps - MathF.Round(steps)) < 1e-3f;
    }

    /// <summary>Сверка цели ½ с эталоном по сетке: вернёт (сверено, с дымом, без дыма, худшее в долях допуска).</summary>
    private (int Compared, int Smoke, int Clear, float Worst) Compare(Vector4[] half, Scene scene, SmokeParams p, SmokeLight light, SceneSky sky)
    {
        int compared = 0, smoke = 0, clear = 0;
        float worst = 0;
        for (int hy = 1; hy < HalfH; hy += 2)
            for (int hx = 0; hx < HalfW; hx += 2)
            {
                if (NearStepBoundary(scene, hx, hy, p, _volume.Center, _volume.AxisU, _volume.AxisV, _volume.AxisW)) continue;
                var want = SmokeMath.Composite(Cpu(scene, hx, hy, p, light, sky), 0, Vector3.Zero);
                var got = half[hy * HalfW + hx];
                for (int c = 0; c < 4; c++)
                {
                    // Допуск: округление half (2⁻¹¹ относительно) и порядок операций float на GPU.
                    float tol = 2e-3f * MathF.Abs(want[c]) + 2e-4f;
                    float err = MathF.Abs(got[c] - want[c]);
                    worst = MathF.Max(worst, err / tol);
                    Assert.True(err <= tol, $"½ ({hx}, {hy}) канал {c}: GPU {got[c]}, CPU {want[c]}, допуск {tol}");
                }
                compared++;
                if (want.W > 0.01f) smoke++;
                if (want.W == 0) clear++;
            }
        return (compared, smoke, clear, worst);
    }

    [Fact]
    public void March_EqualsCpuReference_AlongRays_StepsOnGridToo()
    {
        var p = SmokeParams.Torch with { Density = 1 };
        var scene = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 1000, 1000);
        var (_, half) = Shot(scene, p, "march");
        var (compared, smoke, clear, worst) = Compare(half!, scene, p, Torch, Sky);
        _output.WriteLine($"сверено лучей {compared}: с дымом {smoke}, без {clear}; худшее — {worst:0.00} допуска");
        Assert.True(compared > 600 && smoke > 200 && clear > 30, $"мало лучей: {compared}, с дымом {smoke}, без {clear}");

        // Статистика марша по сетке 64×36 (асинхронно, через кадры в полёте): шагов на луч с дымом = эталон отрезков.
        for (int i = 0; i < GpuReadback.Latency + 1; i++) Shot(scene, p, $"stats{i}");
        var stats = Assert.IsType<SmokeStats>(_smoke.Stats);
        int hit = 0, maxSteps = 0;
        long steps = 0;
        for (int gy = 0; gy < FireSmoke.GridY; gy++)
            for (int gx = 0; gx < FireSmoke.GridX; gx++)
            {
                int hx = (int)((gx + 0.5f) * HalfW / FireSmoke.GridX), hy = (int)((gy + 0.5f) * HalfH / FireSmoke.GridY);
                var dir = Ray(2 * hx + 1, 2 * hy + 1);
                int n = SmokeMath.Segment(Eye, dir, SceneT(scene, hx, hy, dir), _volume.Center, _volume.AxisU, _volume.AxisV, _volume.AxisW, p).Steps;
                if (n == 0) continue;
                hit++;
                steps += n;
                maxSteps = Math.Max(maxSteps, n);
            }
        _output.WriteLine($"сетка: GPU лучей с дымом {stats.Hit}, шагов средн. {stats.MeanSteps:0.00} макс. {stats.MaxSteps}; CPU {hit}, " +
            $"{(double)steps / hit:0.00}, {maxSteps}; покрытие {stats.Coverage:0.000}");
        Assert.Equal(FireSmoke.GridX * FireSmoke.GridY, stats.Rays);
        Assert.InRange(stats.Hit, hit - 4, hit + 4);
        Assert.InRange(stats.MaxSteps, maxSteps - 1, maxSteps + 1);
        Assert.Equal((double)steps / hit, stats.MeanSteps, 0.05);
        Assert.True(stats.Coverage > 0 && stats.MaxSteps <= p.MaxSteps);
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void SceneDepth_ClipsMarch_BehindWallNothing_NoHaloAtSilhouette()
    {
        var p = SmokeParams.Torch with { Density = 1 };
        // Стена-рампа по столбцам поперёк слоя (центр объёма в 3.2 м от камеры, слой ±0.3 м): справа (там ρ больше) стена перед дымом,
        // слева за ним. Лучи, у которых стена ближе входа в слой, а без стены дым был бы, — ровно ноль: дым за стеной не виден.
        var wall = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 3.8f, 2.6f);
        var (_, half) = Shot(wall, p, "wall");
        var (compared, smoke, clear, worst) = Compare(half!, wall, p, Torch, Sky);
        int hidden = 0;
        for (int hy = 0; hy < HalfH; hy++)
            for (int hx = 0; hx < HalfW; hx++)
            {
                var dir = Ray(2 * hx + 1, 2 * hy + 1);
                var (t0, _, _) = SmokeMath.Segment(Eye, dir, float.MaxValue, _volume.Center, _volume.AxisU, _volume.AxisV, _volume.AxisW, p);
                if (SceneT(wall, hx, hy, dir) > t0 - 1e-3f) continue;
                if (Cpu(new Scene(Vector3.Zero, 1000, 1000), hx, hy, p, Torch, Sky).Alpha < 0.01f) continue;
                Assert.Equal(Vector4.Zero, half![hy * HalfW + hx]);
                hidden++;
            }
        _output.WriteLine($"стена: сверено {compared}, с дымом {smoke}, без {clear}; худшее — {worst:0.00} допуска; за стеной (дым был бы) {hidden}");
        Assert.True(compared > 600 && smoke > 100 && clear > 600 && hidden > 300, $"мало лучей: {compared}, с дымом {smoke}, без {clear}, за стеной {hidden}");

        // Силуэты: прямоугольник ближе объёма (2 м) — на нём дыма нет ни в одном пикселе, включая край (ореола нет); прямоугольник за
        // объёмом (5 м) — дым поверх него. Сравнение с тем же кадром без дыма — HDR бит в бит там, где дыма нет.
        var rect = (60, 30, 110, 90);
        foreach (var (z, name) in new[] { (2f, "near"), (5f, "far") })
        {
            var scene = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 1000, 1000, rect, z, new Vector3(0.6f, 0.5f, 0.4f));
            var (with, _) = Shot(scene, p, name);
            var (without, _) = Shot(scene, null, name + "-clear");
            int same = 0, changed = 0, outsideChanged = 0;
            for (int y = rect.Item2; y < rect.Item4; y++)
                for (int x = rect.Item1; x < rect.Item3; x++)
                {
                    int i = y * W + x;
                    if (with[i] == without[i]) same++;
                    else changed++;
                }
            // Фон сразу за краем ближнего прямоугольника (1–2 пикселя) — дым есть: слой ½ не «съедает» дым у силуэта с обеих сторон.
            for (int y = rect.Item2 + 5; y < rect.Item4 - 5; y++)
                for (int x = rect.Item3 + 1; x <= rect.Item3 + 2; x++)
                    if (with[y * W + x] != without[y * W + x]) outsideChanged++;
            _output.WriteLine($"{name} (z {z} м): на прямоугольнике без изменений {same}, с дымом {changed}; фон за краем с дымом {outsideChanged}");
            if (z < 3)
            {
                Assert.Equal(0, changed);
                Assert.True(outsideChanged > 0, "за краем ближнего прямоугольника дыма нет");
            }
            else Assert.True(changed > 1000, $"дым поверх дальнего прямоугольника — {changed} пикселей");
        }
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void ZeroSigma_FrameUnchanged_NoLightNoSkyNoSun_NoRadiance()
    {
        var scene = new Scene(new Vector3(0.3f, 0.2f, 0.1f), 1000, 1000);
        // σ = 0 (плотность 0): слой ½ прозрачен, кадр — бит в бит кадр без дыма.
        var (zero, zeroHalf) = Shot(scene, SmokeParams.Torch with { Density = 0 }, "zero");
        var (clear, _) = Shot(scene, null, "clear");
        Assert.True(MemoryMarshal.AsBytes(zero.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(clear.AsSpan())), "σ = 0 — кадр изменился");
        Assert.All(zeroHalf!, t => Assert.Equal(Vector4.Zero, t));

        // Рассеяние в объёмах 0, солнца и неба нет: дым только поглощает — излучения нет нигде, непрозрачность есть.
        var dark = SmokeLight.From(PointLight.Torch(new Vector3(0.1f, 0.2f, 0.05f)) with { VolumetricScattering = 0 });
        Assert.Equal(Vector3.Zero, dark.Radiance);
        var (_, half) = Shot(scene, SmokeParams.Torch, "dark", dark, SceneSky.None);
        Assert.All(half!, t => Assert.Equal(Vector3.Zero, new Vector3(t.X, t.Y, t.Z)));
        Assert.Contains(half!, t => t.W > 0.05f);

        // Light function источника в дым не идёт: тот же источник с LF и без — те же данные рассеяния.
        var torch = PointLight.Torch(Vector3.One);
        Assert.Equal(SmokeLight.From(torch), SmokeLight.From(torch with { Function = 3 }));
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void Frame_AllocatesNothing()
    {
        var scene = new Scene(Vector3.Zero, 1000, 1000);
        var p = SmokeParams.Torch;
        for (int frame = 0; frame < 40; frame++)
        {
            if (frame == 10) _smokeBytes = 0;
            _window.PumpMessages();
            _renderer.Render(f => Build(f, scene, p, Torch, Sky), vsync: false);
        }
        _output.WriteLine($"байт дыма за 30 кадров: {_smokeBytes}; статистика: {_smoke.Stats}");
        Assert.Equal(1, _smoke.LastVolumes);
        Assert.True(_smokeBytes == 0, $"кадр дыма выделил {_smokeBytes} Б за 30 кадров");
    }
}
