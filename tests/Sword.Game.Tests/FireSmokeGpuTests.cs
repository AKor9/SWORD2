using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid3D;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Game.Contracts;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Дым огня на настоящем устройстве (слой отладки D3D12 с GPU-валидацией: дескрипторы и состояния буферов из bindless-кучи) без игры: перспективная камера на окно газа в мире, синтетическая плотность (два
/// гауссовых облака, пустые кирпичи между ними и вокруг), источник «факел», солнце и небо, сцена с известной глубиной. Слой ½ (сырой снимок
/// цели ½) = CPU-эталон <see cref="SmokeMath.March"/> вдоль лучей (контроль — эталон по полю, сдвинутому на ячейку, не совпадает); пропуск
/// пустоты не меняет результат и выборки (с полным маршем — бит в бит), сетка занятости = CPU; глубина сцены обрезает марш (за стеной дыма
/// нет); на силуэте ближе окна дыма нет, на дальнем — есть; ρ = 0 и плотность 0 — кадр бит в бит без дыма; без света, солнца и неба —
/// излучения нет; детализирующий шум (умолчания факела, скорость газа — вихрь с подъёмом, время газа с обеими фазами flow-map) — в эталоне тоже,
/// а при a = 0 скорость и время газа ни на что не влияют (бит в бит); кадр — 0 Б. Категория Gpu.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
public sealed class FireSmokeGpuTests : IDisposable, ILogSink
{
    private const int W = 161, H = 121;
    private const float Near = 0.1f, FovY = 0.9f, Cell = 0.05f;
    private const string Shader = "fire_smoke_tests.hlsl";
    private static readonly GraphicsPipelineDesc ScenePipeline = new(Shader, "VsFull", "PsScene", Depth: DepthMode.TestWrite);
    private static readonly Vector2 K = new(0, Near);

    private static readonly Vector3 Eye = new(0, 0.2f, 2.2f), Target = new(0, 0.1f, 0);
    private static readonly Vector3 Forward = Vector3.Normalize(Target - Eye);
    private static readonly Matrix4x4 ViewProj = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY) *
        new Matrix4x4(1 / MathF.Tan(FovY / 2) / (W / (float)H), 0, 0, 0, 0, 1 / MathF.Tan(FovY / 2), 0, 0, 0, 0, 0, -1, 0, 0, Near, 0);

    /// <summary>Окно 24×32×24 по 5 см: 1.2×1.6×1.2 м с нижним углом (−0.6, −0.8, −0.6).</summary>
    private static readonly Fluid3DWindow Window = new(-12, -16, -12, 24, 32, 24, Cell, Vector3.Zero);

    private readonly ITestOutputHelper _output;
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly FireSmoke _smoke;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-smoke-{Guid.NewGuid():N}");
    private readonly SmokeVolume[] _volumes = new SmokeVolume[1];
    private readonly SmokeLight[] _lights = new SmokeLight[1];
    private readonly GpuStorage _gas, _empty, _velocity, _still;
    private readonly SmokeGrid _grid, _shifted;
    private long _smokeBytes;

    /// <summary>Время газа кадров: τ = 2.74 при 2 фазах в секунду — обе фазы flow-map с весами 0.52 и 0.48.</summary>
    private const double GasTime = 1.37;

    /// <summary>Скорость и время газа следующих кадров (<see cref="Build"/>): по умолчанию — вихрь и <see cref="GasTime"/>.</summary>
    private bool _stillGas;
    private double _time = GasTime;

    public FireSmokeGpuTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.FireSmoke", W, H, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        FireSmoke.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
        _smoke = new FireSmoke(_renderer, 2, 2, (Window.Nx, Window.Ny, Window.Nz));
        var (bits, grid) = Field(0);
        var (velocityBits, velocity) = Velocity();
        _grid = grid with { Velocity = velocity };
        _shifted = Field(1).Grid with { Velocity = velocity };
        _gas = _renderer.CreateStorage<uint>("test.gas", bits);
        _empty = _renderer.CreateStorage<uint>("test.gas.empty", (uint)bits.Length);
        _velocity = _renderer.CreateStorage<ulong>("test.gas.velocity", velocityBits);
        _still = _renderer.CreateStorage<ulong>("test.gas.still", (uint)velocityBits.Length);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.WaitForIdle();
        _gas.Dispose();
        _empty.Dispose();
        _velocity.Dispose();
        _still.Dispose();
        _smoke.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
        if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Плотность — два гауссовых облака (ρ 0.8 и 0.3, σ 10 и 8 см) в мире, ниже 0.01 — ноль (пустые кирпичи); T — 0.3. Ячейка x окна —
    /// облака, сдвинутые на <paramref name="shift"/> ячеек по x (контроль). Биты — как у газа: half ρ в младших 16 битах, T — в старших.
    /// </summary>
    private static (uint[] Bits, SmokeGrid Grid) Field(int shift)
    {
        var bits = new uint[Window.Nx * Window.Ny * Window.Nz];
        var rho = new float[bits.Length];
        var a = new Vector3(-0.4f, 0f, 0f);
        var b = new Vector3(0.35f, 0.3f, -0.1f);
        for (int z = 0; z < Window.Nz; z++)
            for (int y = 0; y < Window.Ny; y++)
                for (int x = 0; x < Window.Nx; x++)
                {
                    var p = Window.ToWorld(new Vector3(x - shift, y, z));
                    float v = 0.8f * MathF.Exp(-Vector3.DistanceSquared(p, a) / (2 * 0.1f * 0.1f)) +
                        0.3f * MathF.Exp(-Vector3.DistanceSquared(p, b) / (2 * 0.08f * 0.08f));
                    var h = v < 0.01f ? (Half)0 : (Half)v;
                    int i = x + Window.Nx * (y + Window.Ny * z);
                    rho[i] = (float)h;
                    bits[i] = BitConverter.HalfToUInt16Bits(h) | ((uint)BitConverter.HalfToUInt16Bits((Half)0.3f) << 16);
                }
        return (bits, new SmokeGrid(rho, Window.Nx, Window.Ny, Window.Nz, Cell, Window.Min));
    }

    /// <summary>Скорость газа: вихрь вокруг вертикали через центр окна (3 рад/с) с подъёмом 0.8 м/с; биты — как у газа (half vx, vy | vz).</summary>
    private static (ulong[] Bits, float[] Velocity) Velocity()
    {
        var bits = new ulong[Window.Nx * Window.Ny * Window.Nz];
        var velocity = new float[3 * bits.Length];
        for (int z = 0; z < Window.Nz; z++)
            for (int y = 0; y < Window.Ny; y++)
                for (int x = 0; x < Window.Nx; x++)
                {
                    var p = Window.ToWorld(new Vector3(x, y, z));
                    Half vx = (Half)(-3 * p.Z), vy = (Half)0.8f, vz = (Half)(3 * p.X);
                    int i = x + Window.Nx * (y + Window.Ny * z);
                    velocity[3 * i] = (float)vx;
                    velocity[3 * i + 1] = (float)vy;
                    velocity[3 * i + 2] = (float)vz;
                    bits[i] = BitConverter.HalfToUInt16Bits(vx) | ((ulong)BitConverter.HalfToUInt16Bits(vy) << 16) |
                        ((ulong)BitConverter.HalfToUInt16Bits(vz) << 32);
                }
        return (bits, velocity);
    }

    private static readonly SmokeLight Torch = SmokeLight.From(PointLight.Torch(new Vector3(0.1f, 0.2f, 0.05f)));

    /// <summary>
    /// Числа дыма факела с детализирующим шумом крупнее (15 см): в кадре 161×121 решётка 8 см у окна — меньше
    /// <see cref="SmokeMath.MinDetailPixels"/> пикселей ½, и шум был бы выключен.
    /// </summary>
    private static readonly SmokeParams Smoke = SmokeParams.Torch with { DetailScale = 0.15f };
    private static readonly SceneSky Sky = new(new Vector3(0.2f, 0.25f, 0.3f), new Vector3(0.3f, 0.3f, 0.4f));

    /// <summary>Сцена: фон цвета <paramref name="background"/> с глубиной-рампой z0…z1 по столбцам, прямоугольник (пиксели, глубина, цвет).</summary>
    private readonly record struct Scene(Vector3 Background, float Z0, float Z1, (int X0, int Y0, int X1, int Y1) Rect = default, float RectZ = 1,
        Vector3 RectColour = default)
    {
        public float Depth(int x, int y) =>
            x >= Rect.X0 && y >= Rect.Y0 && x < Rect.X1 && y < Rect.Y1 ? RectZ : Z0 + (Z1 - Z0) * (x + 0.5f) / W;
    }

    private void Build(RenderFrame f, Scene scene, SmokeParams? smoke, SmokeLight light, SceneSky sky, bool empty)
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
        _smoke.Params = smoke;
        // Номер содержимого: другое поле — сетка занятости заново (у газа — номер шага).
        _volumes[0] = new SmokeVolume
        {
            Slot = 0, Scalars = empty ? _empty : _gas, Velocity = _stillGas ? _still : _velocity, Window = Window, Version = empty ? 1UL : 2UL,
            Time = _time,
        };
        _lights[0] = light;
        var half = f.HalfRes.Begin(K);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _smoke.Add(f, half, new SmokeView(ViewProj, Eye, Forward, SceneFog.None, sky), _volumes, _lights);
        _smokeBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        f.HalfRes.Composite();
    }

    /// <summary>Кадр со снимком: HDR сцены (полный размер) и сырая цель ½ (null — без дыма).</summary>
    private (Vector4[] Hdr, Vector4[]? Half) Shot(Scene scene, SmokeParams? smoke, string name, SmokeLight? light = null, SceneSky? sky = null,
        bool empty = false)
    {
        Directory.CreateDirectory(_dir);
        string hdr = Path.Combine(_dir, name + ".hdr.png");
        string transparent = Path.Combine(_dir, name + ".transparent.png");
        _window.PumpMessages();
        _renderer.Render(f => Build(f, scene, smoke, light ?? Torch, sky ?? Sky, empty), vsync: false,
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

    private static SmokeSample Cpu(Scene scene, int hx, int hy, SmokeParams p, SmokeLight light, SceneSky sky, SmokeGrid grid)
    {
        var dir = Ray(2 * hx + 1, 2 * hy + 1);
        return SmokeMath.March(Eye, dir, SceneT(scene, hx, hy, dir), grid, SmokeMath.SunRadiance(sky.Sun), SmokeMath.Ambient(sky.Sky, p), [light], p,
            SmokeMath.Flow(GasTime, p));
    }

    /// <summary>Отрезок луча близко к целому числу шагов: GPU и CPU могут округлить по-разному — такие лучи не сравниваются.</summary>
    private bool NearStepBoundary(Scene scene, int hx, int hy, SmokeParams p)
    {
        var dir = Ray(2 * hx + 1, 2 * hy + 1);
        var (t0, t1, n) = SmokeMath.Segment(Eye, dir, SceneT(scene, hx, hy, dir), _grid, p);
        if (n == 0) return MathF.Abs(t1 - t0) < 1e-3f;
        float steps = (t1 - t0) / (p.StepCells * Cell);
        return MathF.Abs(steps - MathF.Round(steps)) < 1e-3f;
    }

    /// <summary>Допуск канала: округление half (2⁻¹¹ относительно) и порядок операций float на GPU.</summary>
    private static float Tolerance(float want) => 2e-3f * MathF.Abs(want) + 2e-4f;

    /// <summary>Сверка цели ½ с эталоном по сетке: вернёт (сверено, с дымом, без дыма, худшее в долях допуска).</summary>
    private (int Compared, int Smoke, int Clear, float Worst) Compare(Vector4[] half, Scene scene, SmokeParams p, SmokeLight light, SceneSky sky)
    {
        int compared = 0, smoke = 0, clear = 0;
        float worst = 0;
        for (int hy = 1; hy < HalfH; hy += 2)
            for (int hx = 0; hx < HalfW; hx += 2)
            {
                if (NearStepBoundary(scene, hx, hy, p)) continue;
                var want = SmokeMath.Composite(Cpu(scene, hx, hy, p, light, sky, _grid), 0, Vector3.Zero);
                var got = half[hy * HalfW + hx];
                for (int c = 0; c < 4; c++)
                {
                    float err = MathF.Abs(got[c] - want[c]);
                    worst = MathF.Max(worst, err / Tolerance(want[c]));
                    Assert.True(err <= Tolerance(want[c]), $"½ ({hx}, {hy}) канал {c}: GPU {got[c]}, CPU {want[c]}, допуск {Tolerance(want[c])}");
                }
                compared++;
                if (want.W > 0.01f) smoke++;
                if (want.W == 0) clear++;
            }
        return (compared, smoke, clear, worst);
    }

    /// <summary>Сетка занятости на CPU: max ρ кирпича 4³ с ячейкой запаса снизу и двумя сверху; непустые (&gt; среза) — число и рамка.</summary>
    private static (int Count, int Total, (int, int, int) Min, (int, int, int) Max) Occupancy(SmokeGrid g, float cut)
    {
        int bx = SmokeMath.Bricks(g.Nx), by = SmokeMath.Bricks(g.Ny), bz = SmokeMath.Bricks(g.Nz);
        int count = 0;
        (int, int, int) lo = (int.MaxValue, int.MaxValue, int.MaxValue), hi = (-1, -1, -1);
        for (int z = 0; z < bz; z++)
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    float max = 0;
                    for (int k = -1; k <= 5; k++)
                        for (int j = -1; j <= 5; j++)
                            for (int i = -1; i <= 5; i++)
                                max = MathF.Max(max, g.Cell(4 * x + i, 4 * y + j, 4 * z + k));
                    if (!(max > cut)) continue;
                    count++;
                    lo = (Math.Min(lo.Item1, x), Math.Min(lo.Item2, y), Math.Min(lo.Item3, z));
                    hi = (Math.Max(hi.Item1, x), Math.Max(hi.Item2, y), Math.Max(hi.Item3, z));
                }
        return (count, bx * by * bz, lo, hi);
    }

    [Fact]
    public void March_EqualsCpuReference_AlongRays_ShiftedFieldIsNot_OccupancyAndStepsOnGridToo()
    {
        var p = Smoke with { Density = 1.5f };
        var scene = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 1000, 1000);
        var (_, half) = Shot(scene, p, "march");
        var (compared, smoke, clear, worst) = Compare(half!, scene, p, Torch, Sky);
        _output.WriteLine($"сверено лучей {compared}: с дымом {smoke}, без {clear}; худшее — {worst:0.00} допуска");
        Assert.True(compared > 600 && smoke > 60 && clear > 100, $"мало лучей: {compared}, с дымом {smoke}, без {clear}");

        // Контроль: эталон по полю, сдвинутому на ячейку, — лучи с дымом расходятся с GPU больше допуска.
        int differ = 0;
        for (int hy = 1; hy < HalfH; hy += 2)
            for (int hx = 0; hx < HalfW; hx += 2)
            {
                var want = SmokeMath.Composite(Cpu(scene, hx, hy, p, Torch, Sky, _shifted), 0, Vector3.Zero);
                var got = half![hy * HalfW + hx];
                if (MathF.Abs(got.W - want.W) > Tolerance(want.W)) differ++;
            }
        _output.WriteLine($"поле сдвинуто на ячейку: лучей вне допуска {differ}");
        Assert.True(differ > smoke / 4, $"сдвинутое поле почти совпало: {differ} лучей вне допуска из {smoke} с дымом");

        // Статистика марша по сетке 64×36 (асинхронно, через кадры в полёте): шагов на луч = эталон отрезков; сетка занятости = CPU.
        for (int i = 0; i < GpuReadback.Latency + 1; i++) Shot(scene, p, $"stats{i}");
        var stats = Assert.IsType<SmokeStats>(_smoke.Stats);
        int hit = 0, maxSteps = 0;
        long steps = 0;
        for (int gy = 0; gy < FireSmoke.GridY; gy++)
            for (int gx = 0; gx < FireSmoke.GridX; gx++)
            {
                int hx = (int)((gx + 0.5f) * HalfW / FireSmoke.GridX), hy = (int)((gy + 0.5f) * HalfH / FireSmoke.GridY);
                var dir = Ray(2 * hx + 1, 2 * hy + 1);
                int n = SmokeMath.Segment(Eye, dir, SceneT(scene, hx, hy, dir), _grid, p).Steps;
                if (n == 0) continue;
                hit++;
                steps += n;
                maxSteps = Math.Max(maxSteps, n);
            }
        var occupancy = Occupancy(_grid, p.DensityCut);
        _output.WriteLine($"сетка: GPU лучей в окне {stats.Hit}, шагов средн. {stats.MeanSteps:0.00} макс. {stats.MaxSteps}, выборок средн. " +
            $"{stats.MeanSamples:0.00} макс. {stats.MaxSamples}; CPU {hit}, {(double)steps / hit:0.00}, {maxSteps}; покрытие {stats.Coverage:0.000}; " +
            $"кирпичей GPU {stats.Bricks}/{stats.TotalBricks} {stats.BrickMin}…{stats.BrickMax}, CPU {occupancy.Count}/{occupancy.Total} " +
            $"{occupancy.Min}…{occupancy.Max}");
        Assert.Equal(FireSmoke.GridX * FireSmoke.GridY, stats.Rays);
        Assert.InRange(stats.Hit, hit - 4, hit + 4);
        Assert.InRange(stats.MaxSteps, maxSteps - 1, maxSteps + 1);
        Assert.Equal((double)steps / hit, stats.MeanSteps, 0.05);
        Assert.True(stats.Coverage > 0 && stats.MaxSamples > 0 && stats.MeanSamples < stats.MeanSteps);
        Assert.Equal((occupancy.Count, occupancy.Total, occupancy.Min, occupancy.Max), (stats.Bricks, stats.TotalBricks, stats.BrickMin, stats.BrickMax));
        Assert.True(occupancy.Count < occupancy.Total / 2, $"непустых кирпичей {occupancy.Count} из {occupancy.Total} — пропускать нечего");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void SkipEmpty_SameResultAsFullMarch_SameSamples()
    {
        // Пропуск пустоты (рамка непустых кирпичей и прыжки через пустые) убирает только выборки с σ = 0: цель ½ — бит в бит полный марш,
        // выборок с σ > 0 по сетке лучей — столько же. Сцена со стеной-рампой: лучи обрезаны глубиной на разной длине.
        var scene = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 1.2f, 3.2f);
        var skip = Smoke with { Density = 3 };
        var full = skip with { SkipEmpty = false };
        (Vector4[] Half, SmokeStats Stats) Run(SmokeParams p, string name)
        {
            Vector4[]? half = null;
            for (int i = 0; i < GpuReadback.Latency + 1; i++) half = Shot(scene, p, $"{name}{i}").Half;
            return (half!, Assert.IsType<SmokeStats>(_smoke.Stats));
        }
        var a = Run(skip, "skip");
        var b = Run(full, "full");
        int smoke = a.Half.Count(t => t.W > 0.01f);
        _output.WriteLine($"с пропуском: выборок средн. {a.Stats.MeanSamples:0.00}, шагов {a.Stats.MeanSteps:0.00}; полный: {b.Stats.MeanSamples:0.00}, " +
            $"{b.Stats.MeanSteps:0.00}; текселей ½ с дымом {smoke}");
        Assert.True(smoke > 100, $"мало дыма: {smoke}");
        Assert.True(MemoryMarshal.AsBytes(a.Half.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Half.AsSpan())), "пропуск пустоты изменил слой ½");
        Assert.Equal(b.Stats.MeanSamples, a.Stats.MeanSamples);
        Assert.Equal(b.Stats.MeanSteps, a.Stats.MeanSteps);
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void SceneDepth_ClipsMarch_BehindWallNothing_NoHaloAtSilhouette()
    {
        var p = Smoke with { Density = 1.5f };
        // Стена-рампа по столбцам поперёк окна (центр окна в 2.2 м от камеры, вход в окно ≈1.6 м): слева стена перед левым облаком, справа
        // за окном. Лучи, у которых стена ближе входа в окно, а без стены дым был бы, — ровно ноль: дым за стеной не виден.
        var wall = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 0.8f, 3.0f);
        var (_, half) = Shot(wall, p, "wall");
        var (compared, smoke, clear, worst) = Compare(half!, wall, p, Torch, Sky);
        int hidden = 0;
        for (int hy = 0; hy < HalfH; hy++)
            for (int hx = 0; hx < HalfW; hx++)
            {
                var dir = Ray(2 * hx + 1, 2 * hy + 1);
                var (t0, _, _) = SmokeMath.Segment(Eye, dir, float.MaxValue, _grid, p);
                if (SceneT(wall, hx, hy, dir) > t0 - 1e-3f) continue;
                if (Cpu(new Scene(Vector3.Zero, 1000, 1000), hx, hy, p, Torch, Sky, _grid).Alpha < 0.01f) continue;
                Assert.Equal(Vector4.Zero, half![hy * HalfW + hx]);
                hidden++;
            }
        _output.WriteLine($"стена: сверено {compared}, с дымом {smoke}, без {clear}; худшее — {worst:0.00} допуска; за стеной (дым был бы) {hidden}");
        Assert.True(compared > 600 && smoke > 10 && clear > 300 && hidden > 50, $"мало лучей: {compared}, с дымом {smoke}, без {clear}, за стеной {hidden}");

        // Силуэты: прямоугольник ближе окна (1.2 м) — на нём дыма нет ни в одном пикселе, включая край (ореола нет); прямоугольник за окном
        // (4 м) — дым поверх него. Сравнение с тем же кадром без дыма — HDR бит в бит там, где дыма нет.
        var rect = (60, 30, 110, 90);
        foreach (var (z, name) in new[] { (1.2f, "near"), (4f, "far") })
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
                for (int x = rect.Item1 - 2; x < rect.Item1; x++)
                    if (with[y * W + x] != without[y * W + x]) outsideChanged++;
            _output.WriteLine($"{name} (z {z} м): на прямоугольнике без изменений {same}, с дымом {changed}; фон за краем с дымом {outsideChanged}");
            if (z < 3)
            {
                Assert.Equal(0, changed);
                Assert.True(outsideChanged > 0, "за краем ближнего прямоугольника дыма нет");
            }
            else Assert.True(changed > 300, $"дым поверх дальнего прямоугольника — {changed} пикселей");
        }
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void ZeroDensity_FrameUnchanged_NoLightNoSkyNoSun_NoRadiance()
    {
        var scene = new Scene(new Vector3(0.3f, 0.2f, 0.1f), 1000, 1000);
        var (clear, _) = Shot(scene, null, "clear");
        // ρ = 0 во всём окне и множитель плотности 0: слой ½ прозрачен, кадр — бит в бит кадр без дыма.
        var (empty, emptyHalf) = Shot(scene, Smoke, "empty", empty: true);
        var (zero, zeroHalf) = Shot(scene, Smoke with { Density = 0 }, "zero");
        foreach (var (image, half, name) in new[] { (empty, emptyHalf, "ρ = 0"), (zero, zeroHalf, "плотность 0") })
        {
            Assert.True(MemoryMarshal.AsBytes(image.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(clear.AsSpan())), $"{name} — кадр изменился");
            Assert.All(half!, t => Assert.Equal(Vector4.Zero, t));
        }

        // Рассеяние в объёмах 0, солнца и неба нет: дым только поглощает — излучения нет нигде, непрозрачность есть.
        var dark = SmokeLight.From(PointLight.Torch(new Vector3(0.1f, 0.2f, 0.05f)) with { VolumetricScattering = 0 });
        Assert.Equal(Vector3.Zero, dark.Radiance);
        var (_, half2) = Shot(scene, Smoke, "dark", dark, SceneSky.None);
        Assert.All(half2!, t => Assert.Equal(Vector3.Zero, new Vector3(t.X, t.Y, t.Z)));
        Assert.Contains(half2!, t => t.W > 0.05f);

        // Только свет огня (без солнца и неба): рассеяние есть — красное, как его цвет.
        var (_, lit) = Shot(scene, Smoke, "lit", Torch, SceneSky.None);
        var brightest = lit!.MaxBy(t => t.X);
        Assert.True(brightest.X > 0 && brightest.Y < 0.1f * brightest.X && brightest.Z == 0, $"рассеяние света огня не красное: {brightest}");

        // Light function источника в дым не идёт: тот же источник с LF и без — те же данные рассеяния.
        var torch = PointLight.Torch(Vector3.One);
        Assert.Equal(SmokeLight.From(torch), SmokeLight.From(torch with { Function = 3 }));
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void DetailNoise_ChangesSmoke_AtZeroVelocityAndTimeChangeNothing()
    {
        // Умолчания факела — шум включён: слой ½ = эталон (первый тест), и он другой, чем без шума. При a = 0 шум не считается: слой ½ бит в бит
        // при другой скорости газа (ноль) и другом времени газа.
        var scene = new Scene(new Vector3(0.05f, 0.04f, 0.03f), 1000, 1000);
        var on = Smoke with { Density = 1.5f };
        var off = on with { Detail = 0 };
        Assert.True(on.Detail > 0);
        var (_, noisy) = Shot(scene, on, "detail-on");
        Assert.Equal(1, _smoke.LastDetailed);
        var (_, plain) = Shot(scene, off, "detail-off");
        _stillGas = true;
        _time = 5.9;
        var (_, plainStill) = Shot(scene, off, "detail-off-still");
        var (_, noisyStill) = Shot(scene, on, "detail-on-still");
        _stillGas = false;
        _time = GasTime;
        int smoke = 0, changed = 0, flowChanged = 0;
        for (int i = 0; i < plain!.Length; i++)
        {
            if (plain[i].W < 0.01f) continue;
            smoke++;
            if (MathF.Abs(noisy![i].W - plain[i].W) > Tolerance(plain[i].W)) changed++;
            if (MathF.Abs(noisy[i].W - noisyStill![i].W) > Tolerance(noisy[i].W)) flowChanged++;
        }
        _output.WriteLine($"текселей ½ с дымом {smoke}: шум изменил α у {changed}; другая скорость и время газа — у {flowChanged}");
        Assert.True(MemoryMarshal.AsBytes(plain.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(plainStill!.AsSpan())),
            "a = 0: скорость или время газа изменили кадр");
        Assert.True(smoke > 300 && changed > smoke / 3, $"шум почти ничего не изменил: {changed} из {smoke}");
        Assert.True(flowChanged > smoke / 10, $"flow-map: скорость и время газа не сдвинули шум ({flowChanged} из {smoke})");

        // Решётка умолчания (8 см) у окна в этом кадре — меньше 3 пикселей ½: шум у объёма выключен, слой ½ — бит в бит без шума.
        var (_, small) = Shot(scene, on with { DetailScale = SmokeParams.Torch.DetailScale }, "detail-small");
        Assert.Equal(0, _smoke.LastDetailed);
        Assert.True(MemoryMarshal.AsBytes(small!.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(plain.AsSpan())), "мелкий шум не выключен");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void Frame_AllocatesNothing()
    {
        var scene = new Scene(Vector3.Zero, 1000, 1000);
        var p = Smoke;
        // Сетка занятости перестраивается в кадрах 3, 4, 10, 11, … (смена поля): к кадру 20 каждый слот кольца кадров её видел.
        for (int frame = 0; frame < 60; frame++)
        {
            if (frame == 20) _smokeBytes = 0;
            _window.PumpMessages();
            _renderer.Render(f => Build(f, scene, p, Torch, Sky, frame % 7 == 3), vsync: false);
        }
        _output.WriteLine($"байт дыма за 40 кадров: {_smokeBytes}; статистика: {_smoke.Stats}");
        Assert.Equal(1, _smoke.LastVolumes);
        Assert.True(_smokeBytes == 0, $"кадр дыма выделил {_smokeBytes} Б за 40 кадров");
    }
}
