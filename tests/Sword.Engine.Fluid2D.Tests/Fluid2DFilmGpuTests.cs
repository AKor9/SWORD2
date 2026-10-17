using System.Numerics;
using Sword.Engine.Render;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.Fluid2DTestKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Плёнка крови (A′, пресет <see cref="Fluid2DParams.BloodSpread"/>) на устройстве: стадии с консервативным переносом, толщиной без потолка,
/// трением и высотами рельефа = эталон; масса за 10 000 шагов с движением; постоянный источник — площадь ∝ t; лужа встаёт при толщине H;
/// на склоне 17° полоса 2h течёт в 4 раза быстрее полосы h, мазок тоньше h_min стоит; на ровном лужа без притока не движется.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DFilmGpuTests : IDisposable
{
    private const double StageUlps = 2;
    private const float Texel = 0.03125f;
    private readonly GpuDevice _gpu = new("Sword.Engine.Fluid2D.Tests.Film");
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _dispose = [];

    public Fluid2DFilmGpuTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var d in _dispose) d.Dispose();
        _gpu.Dispose();
    }

    /// <summary>Плёнка на n² с тем же текселем 3.125 см.</summary>
    public static Fluid2DParams Film(int n) =>
        Fluid2DParams.BloodSpread with { Resolution = n, Extents = new Vector3(n * Texel, n * Texel, 0.65f) };

    private sealed record Setup(Fluid2DSim Sim, Fluid2DNoiseImage Density, Fluid2DImage BrushNoise, Fluid2DImage Shape);

    private Setup Create(Fluid2DParams p, string name = "film")
    {
        var (dn, dnCpu, i1) = _gpu.Noise("test.noise.density", 128, 51);
        var (bn, bnCpu, i2) = _gpu.Image("test.noise.brush", 64, 52);
        var (sh, shCpu, i3) = _gpu.Image("test.stamp.shape", 32, 53);
        _dispose.AddRange([i1, i2, i3]);
        var sim = new Fluid2DSim(_gpu.Renderer, name, p, dn, default, bn, default, sh);
        _dispose.Add(sim);
        return new Setup(sim, dnCpu, bnCpu, shCpu);
    }

    /// <summary>Буфер высот на GPU (узлы (cells + 1)² по рядам z на ±half м) и его SRV.</summary>
    private Fluid2DHeightField Heights(Fluid2DHeightImage image)
    {
        var storage = _gpu.Renderer.CreateStorage<float>("test.heights", image.Values);
        _dispose.Add(storage);
        int srv = -1;
        _gpu.Frame(f => srv = f.Graph.SrvOf(f.Graph.Import(storage)));
        return new Fluid2DHeightField(srv, image.Cells, image.Half);
    }

    private static Fluid2DHeightImage HeightImage(int cells, float half, Func<float, float, float> height)
    {
        var values = new float[(cells + 1) * (cells + 1)];
        float step = 2 * half / cells;
        for (int k = 0; k <= cells; k++)
            for (int i = 0; i <= cells; i++) values[k * (cells + 1) + i] = height(-half + i * step, -half + k * step);
        return new Fluid2DHeightImage(values, cells, half);
    }

    /// <summary>Случайные поля с ρ до 3 (толщина без потолка), скоростями в пределах CFL и возрастом.</summary>
    private static Fluid2DFields RandomFilmFields(int n, int seed)
    {
        var f = new Fluid2DFields(n);
        var rng = new Random(seed);
        for (int i = 0; i < n * n; i++)
        {
            bool dry = rng.NextSingle() < 0.2f;
            for (int g = 0; g < 2; g++)
            {
                var grid = g == 0 ? f.Composite : f.Advected;
                grid[i * 4] = (Half)((rng.NextSingle() - 0.5f) * 0.15f);
                grid[i * 4 + 1] = (Half)((rng.NextSingle() - 0.5f) * 0.15f);
                grid[i * 4 + 2] = (Half)(dry ? 0f : rng.NextSingle() * 3f);
                grid[i * 4 + 3] = (Half)rng.NextSingle();
            }
            f.Pressure[i * 2] = (Half)((rng.NextSingle() - 0.5f) * 0.01f);
            f.AgeAdvected[i] = rng.NextSingle() * 100;
            f.AgeComposite[i] = rng.NextSingle() * 100;
        }
        return f.WithFloatDensity();
    }

    private static Half[] Output(Fluid2DFields f, Fluid2DStage stage) => stage == Fluid2DStage.Composite ? f.Composite : f.Advected;

    /// <summary>
    /// Composite и Advection плёнки 64² со сдвигом окна, возрастом, штампами, кистью и высотами рельефа (волнистый склон со ступенью) = эталон
    /// в 2 ULP half; возраст — 1e-5 относительно.
    /// </summary>
    [Theory]
    [InlineData(Fluid2DStage.Composite)]
    [InlineData(Fluid2DStage.Advection)]
    public void FilmStage_MatchesReference(Fluid2DStage stage)
    {
        var p = Film(64);
        var s = Create(p);
        var image = HeightImage(16, 2f, (x, z) => 0.3f * z + 0.1f * MathF.Sin(3 * x) + (x > 0.4f ? 0.15f : 0));
        s.Sim.Heights = Heights(image);
        var fields = RandomFilmFields(64, 81);
        var brush = RandomBrush(p.BrushResolution, 82);
        using var brushStorage = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", brush);
        var input = new Fluid2DStepInput(777, new Vector2(1.1f, 0.4f), Vector2.Zero) { ShiftTexels = (2, -3), WindowCenter = new Vector2(0.3f, -0.2f) };
        var stamps = new Fluid2DStamps(p);
        stamps.Begin(input.Step);
        stamps.AddUv(new Vector2(0.4f, 0.6f), 0.3f, new Vector2(0.1f, 0.06f), 2e-3f);
        stamps.AddUv(new Vector2(0.7f, 0.2f), 0, new Vector2(0.02f, 0.02f), 1e-4f);
        stamps.End();
        s.Sim.Load(fields);
        _gpu.Frame(f => s.Sim.AddStage(f, input, brushStorage, stage, stamps));
        var cpu = new Fluid2DReference(p, s.Density, null, fields.Clone(), [s.Shape]) { Heights = image };
        cpu.Stage(stage, input, brush, stamps);
        var gpu = s.Sim.ReadFields();
        var e = Compare(Output(gpu, stage), Output(cpu.Fields, stage));
        _output.WriteLine($"плёнка, {stage}: {e.Ulps:F2} ULP half, {e.Abs:G4} (элемент {e.Index})");
        Assert.True(e.Ulps <= StageUlps, $"{stage}: {e.Ulps} ULP (элемент {e.Index})");
        var age = stage == Fluid2DStage.Composite ? (gpu.AgeComposite, cpu.Fields.AgeComposite) : (gpu.AgeAdvected, cpu.Fields.AgeAdvected);
        double worst = 0;
        for (int i = 0; i < age.Item1.Length; i++) worst = Math.Max(worst, Math.Abs(age.Item1[i] - age.Item2[i]) / Math.Max(Math.Abs(age.Item2[i]), 1));
        var rho = stage == Fluid2DStage.Composite ? (gpu.DensityComposite, cpu.Fields.DensityComposite) : (gpu.DensityAdvected, cpu.Fields.DensityAdvected);
        double worstRho = 0;
        for (int i = 0; i < rho.Item1.Length; i++) worstRho = Math.Max(worstRho, Math.Abs(rho.Item1[i] - rho.Item2[i]) / Math.Max(Math.Abs(rho.Item2[i]), 1e-3));
        _output.WriteLine($"возраст: {worst:G3}, ρ float: {worstRho:G3} относительно");
        Assert.True(worst <= 1e-5, $"возраст {stage}: {worst}");
        Assert.True(worstRho <= 1e-6, $"ρ float {stage}: {worstRho}");
        _gpu.AssertNoDebugMessages();
    }

    private void Run(Fluid2DSim sim, ulong first, int steps, Func<ulong, Fluid2DStamps?>? stamps = null, int perFrame = 50)
    {
        for (int s = 0; s < steps; s += perFrame)
        {
            int begin = s, end = Math.Min(s + perFrame, steps);
            _gpu.Frame(f =>
            {
                for (int k = begin; k < end; k++)
                {
                    ulong step = first + (ulong)k;
                    sim.Step(f, new Fluid2DStepInput(step, default, default), (GpuStorage?)null, stamps?.Invoke(step));
                }
            });
        }
    }

    /// <summary>Лужа объёмом <paramref name="liters"/> — гаусс σ = <paramref name="sigma"/> м в (u, v), ρ = h/h_max, в A и C.</summary>
    private static Fluid2DFields Puddle(Fluid2DParams p, float liters, float sigma, float u0 = 0.5f, float v0 = 0.5f)
    {
        int n = p.Resolution;
        var f = new Fluid2DFields(n);
        float peak = liters * 1e-3f / (2 * MathF.PI * sigma * sigma) / p.FilmThicknessPerDensity;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = ((x + 0.5f) / n - u0) * p.Extents.X, dy = ((y + 0.5f) / n - v0) * p.Extents.Y;
                float rho = peak * MathF.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                if (rho < 1e-3f) continue;
                f.Advected[(y * n + x) * 4 + 2] = (Half)rho;
                f.Composite[(y * n + x) * 4 + 2] = (Half)rho;
            }
        return f.WithFloatDensity();
    }

    /// <summary>Σρ C — по float-каналу (состояние консервативного переноса).</summary>
    private static double Mass(Fluid2DFields f)
    {
        double s = 0;
        foreach (float r in f.DensityComposite) s += r;
        return s;
    }

    /// <summary>Тексели толще <paramref name="h"/> м — площадь, м².</summary>
    private static double Area(Fluid2DFields f, Fluid2DParams p, float h)
    {
        int count = 0;
        foreach (float r in f.DensityComposite)
            if (r * p.FilmThicknessPerDensity > h) count++;
        return count * (double)(p.Extents.X / p.Resolution) * (p.Extents.Y / p.Resolution);
    }

    private static (double U, double V) Centroid(Fluid2DFields f)
    {
        int n = f.N;
        double m = 0, su = 0, sv = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double r = f.DensityComposite[y * n + x];
                m += r;
                su += r * x;
                sv += r * y;
            }
        return (su / m, sv / m);
    }

    private static double MaxSpeed(Fluid2DFields f)
    {
        double v = 0;
        for (int i = 0; i < f.Composite.Length; i += 4) v = Math.Max(v, Math.Max(Math.Abs((float)f.Composite[i]), Math.Abs((float)f.Composite[i + 1])));
        return v;
    }

    /// <summary>
    /// Масса за 10 000 шагов с движением: лужа 0.2 л сбоку от дна чаши (рельеф 0.15·r²) стекает на дно, растекается и встаёт — Σρ в 0.5 %.
    /// </summary>
    [Fact]
    public void Mass_10000StepsWithMotion_Within0p5Percent()
    {
        var p = Film(256);
        var s = Create(p);
        s.Sim.Heights = Heights(HeightImage(64, 8, (x, z) => 0.15f * (x * x + z * z)));
        var initial = Puddle(p, 0.2f, 0.06f, 0.62f, 0.45f);
        s.Sim.Load(initial);
        double m0 = Mass(initial);
        var c0 = Centroid(initial);
        Run(s.Sim, 0, 300);
        var f300 = s.Sim.ReadFields();
        var c300 = Centroid(f300);
        Run(s.Sim, 300, 9700);
        var f = s.Sim.ReadFields();
        double drift300 = (Mass(f300) - m0) / m0, drift = (Mass(f) - m0) / m0;
        var c = Centroid(f);
        _output.WriteLine($"чаша: Σρ {m0:F3} → {Mass(f300):F3} (300, {drift300:P4}) → {Mass(f):F3} ({drift:P4}); центр масс, текселей: {c0} → {c300} → {c}; " +
            $"max|v| {MaxSpeed(f300):G3} → {MaxSpeed(f):G3}; площадь > 0.3 мм {Area(f, p, 3e-4f):F3} м²");
        Assert.True(Math.Abs(c300.U - c0.U) > 2, "лужа не стекла к дну");
        Assert.True(Math.Abs(drift) <= 5e-3, $"масса {drift:P4}");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// Постоянный источник на ровном полу (штамп каждый шаг, 25 мл/с): площадь толще H/2 растёт ∝ t (за вдвое большее время — вдвое, ± 15 %),
    /// масса = притоку; источник перекрыт — лужа встаёт при толщине H: площадь толще H/4 = объём/H ± 10 %, скорость гаснет.
    /// </summary>
    [Fact]
    public void ConstantSource_AreaGrowsLinearly_PuddleStopsAtH()
    {
        var p = Film(256);
        var s = Create(p);
        float h = p.FilmPuddleThickness;
        float perStep = 25e-6f / 60 / p.FilmThicknessPerDensity;
        var stamps = new Fluid2DStamps(p);
        Fluid2DStamps Source(ulong step)
        {
            stamps.Begin(step);
            stamps.AddUv(new Vector2(0.5f, 0.5f), 0, new Vector2(0.03f, 0.03f), perStep);
            stamps.End();
            return stamps;
        }
        var areas = new List<(double T, double Area, double Mass)>();
        int steps = 0;
        foreach (int t in new[] { 240, 480, 960 })
        {
            Run(s.Sim, (ulong)steps, t - steps, Source);
            steps = t;
            var f = s.Sim.ReadFields();
            areas.Add((t / 60.0, Area(f, p, 0.5f * h), Mass(f) * Texel * Texel));
        }
        foreach (var (t, a, m) in areas)
            _output.WriteLine($"источник {t:F0} с: площадь > H/2 {a:F4} м², масса {m:G5} ρ·м² (приток {perStep * t * 60:G5}), объём {m * p.FilmThicknessPerDensity * 1e3:F3} л");
        for (int i = 1; i < areas.Count; i++)
        {
            double ratio = areas[i].Area / areas[i - 1].Area;
            _output.WriteLine($"площадь ×{ratio:F3} за ×2 времени");
            Assert.InRange(ratio, 2 * 0.85, 2 * 1.15);
        }
        double inflow = perStep * steps;
        Assert.InRange(areas[^1].Mass, inflow * 0.995, inflow * 1.005);

        Run(s.Sim, (ulong)steps, 3600);
        var end = s.Sim.ReadFields();
        double volume = Mass(end) * Texel * Texel * p.FilmThicknessPerDensity;
        double area = Area(end, p, 0.25f * h), expected = volume / h;
        _output.WriteLine($"встала через 60 с: площадь > H/4 {area:F4} м², объём/H {expected:F4} м² ({area / expected - 1:P1}); max|v| {MaxSpeed(end):G3}");
        Assert.InRange(area, expected * 0.9, expected * 1.1);
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Лужа 0.2 л растекается за несколько секунд до 0.3–0.6 м и встаёт: площадь ≈ объём/H ± 10 %, центр на месте.</summary>
    [Fact]
    public void Puddle_SpreadsInSeconds_StopsAtH()
    {
        var p = Film(256);
        var s = Create(p);
        var initial = Puddle(p, 0.2f, 0.04f);
        s.Sim.Load(initial);
        var c0 = Centroid(initial);
        double volume = Mass(initial) * Texel * Texel * p.FilmThicknessPerDensity;
        double expected = volume / p.FilmPuddleThickness;
        double Diameter(Fluid2DFields f) => 2 * Math.Sqrt(Area(f, p, 0.25f * p.FilmPuddleThickness) / Math.PI);
        var log = new List<string>();
        int steps = 0;
        double d3 = 0;
        foreach (int t in new[] { 30, 60, 120, 180, 300, 600, 1200, 2400 })
        {
            Run(s.Sim, (ulong)steps, t - steps);
            steps = t;
            var f = s.Sim.ReadFields();
            if (t == 180) d3 = Diameter(f);
            log.Add($"{t / 60.0:F1} с: ⌀ {Diameter(f):F3} м, max|v| {MaxSpeed(f):G3}");
        }
        var end = s.Sim.ReadFields();
        double area = Area(end, p, 0.25f * p.FilmPuddleThickness);
        var c = Centroid(end);
        _output.WriteLine(string.Join("; ", log));
        _output.WriteLine($"лужа {volume * 1e3:F3} л: площадь {area:F4} м², объём/H {expected:F4} м² ({area / expected - 1:P1}); центр {c0} → {c}");
        Assert.InRange(d3, 0.3, 0.6);
        Assert.InRange(area, expected * 0.9, expected * 1.1);
        Assert.True(Math.Abs(c.U - c0.U) < 0.05 && Math.Abs(c.V - c0.V) < 0.05, $"центр {c}");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// Склон 17° (высоты вдоль +Z): полосы толщины h = 0.5 мм и 2h поперёк склона — скорость центра масс 2h ≈ 4× скорости h (± 30 %); мазок
    /// тоньше h_min стоит бит в бит.
    /// </summary>
    [Fact]
    public void Slope17_ThickStripFlowsFourTimesFaster_ThinSmearStands()
    {
        var p = Film(256);
        float tan = MathF.Tan(17 * MathF.PI / 180);
        var image = HeightImage(64, 8, (x, z) => -tan * z);
        double Speed(float thickness, out Fluid2DFields after)
        {
            var s = Create(p, $"slope{thickness}");
            s.Sim.Heights = Heights(image);
            var f0 = new Fluid2DFields(256);
            float rho = thickness / p.FilmThicknessPerDensity;
            for (int y = 100; y < 116; y++)
                for (int x = 64; x < 192; x++)
                {
                    f0.Advected[(y * 256 + x) * 4 + 2] = (Half)rho;
                    f0.Composite[(y * 256 + x) * 4 + 2] = (Half)rho;
                }
            s.Sim.Load(f0.WithFloatDensity());
            Run(s.Sim, 0, 30);
            var a = Centroid(s.Sim.ReadFields());
            Run(s.Sim, 30, 120);
            after = s.Sim.ReadFields();
            var b = Centroid(after);
            return (b.V - a.V) * Texel / 2.0;
        }
        double v1 = Speed(0.5e-3f, out _), v2 = Speed(1e-3f, out _);
        double expected1 = 9.81 * tan * 0.25e-6 / p.FilmFriction;
        _output.WriteLine($"склон 17°: h 0.5 мм — {v1 * 100:F3} см/с (формула {expected1 * 100:F3}), 1 мм — {v2 * 100:F3} см/с; отношение {v2 / v1:F2}");
        Assert.True(v1 > 0, "полоса h стоит");
        Assert.InRange(v2 / v1, 4 * 0.7, 4 * 1.3);

        Speed(0.2e-3f, out var smear);
        double moved = 0;
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float expect = y is >= 100 and < 116 && x is >= 64 and < 192 ? (float)(Half)(0.2e-3f / p.FilmThicknessPerDensity) : 0f;
                moved = Math.Max(moved, Math.Abs((float)smear.Composite[(y * 256 + x) * 4 + 2] - expect));
            }
        _output.WriteLine($"мазок 0.2 мм: наибольшее изменение ρ {moved}");
        Assert.Equal(0, moved);
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>На ровном полу лужа толщиной ≤ H без притока не движется: 2000 шагов — C бит в бит, v = 0.</summary>
    [Fact]
    public void Flat_PuddleAtH_WithoutInflow_DoesNotMove()
    {
        var p = Film(128);
        var s = Create(p);
        s.Sim.Heights = Heights(HeightImage(16, 2, (_, _) => 0.7f));
        var f0 = new Fluid2DFields(128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float u = (x + 0.5f) / 128 - 0.5f, v = (y + 0.5f) / 128 - 0.5f;
                float r = MathF.Sqrt(u * u + v * v) / 0.2f;
                if (r >= 1) continue;
                float rho = (0.6f + 0.4f * MathF.Cos(9 * u)) * p.FilmPuddleThickness / p.FilmThicknessPerDensity;
                f0.Advected[(y * 128 + x) * 4 + 2] = (Half)rho;
            }
        s.Sim.Load(f0.WithFloatDensity());
        _gpu.Frame(f => s.Sim.Step(f, new Fluid2DStepInput(0, default, default)));
        var first = s.Sim.ReadFields();
        Run(s.Sim, 1, 2000);
        var end = s.Sim.ReadFields();
        int changed = 0;
        for (int i = 2; i < end.Composite.Length; i += 4)
            if (end.Composite[i] != first.Composite[i]) changed++;
        _output.WriteLine($"лужа ≤ H: изменилось {changed} текселей, max|v| {MaxSpeed(end)}");
        Assert.Equal(0, changed);
        Assert.Equal(0, MaxSpeed(end));
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Кадр плёнки со штампами, кистью, высотами, экспортом и возрастом — 0 Б; проходы по порядку; сброс обнуляет float-канал ρ.</summary>
    [Fact]
    public void FilmFrame_AllocatesNothing_ResetClears()
    {
        var p = Film(128);
        var s = Create(p);
        s.Sim.Heights = Heights(HeightImage(16, 4, (x, z) => 0.2f * z));
        var brush = new Fluid2DBrush(p, 3);
        var stamps = new Fluid2DStamps(p);
        ulong step = 0;
        Action<RenderFrame> build = f =>
        {
            var input = new Fluid2DStepInput(step, default, default) { ShiftTexels = ((int)(step % 3) - 1, 0), WindowCenter = new Vector2(0.01f * step, 0) };
            brush.Begin(input, step, Vector3.Zero);
            brush.Add(PainterKit.Key(1), new Vector3(0.01f * step, 0.3f, 0), 1.5f);
            brush.End();
            stamps.Begin(step);
            stamps.AddUv(new Vector2(0.5f, 0.4f), 0.3f, new Vector2(0.1f, 0.05f), 1e-3f);
            stamps.End();
            s.Sim.Step(f, input, brush, stamps);
            s.Sim.Export(f);
            s.Sim.ExportAge(f);
            step++;
        };
        for (int i = 0; i < 10; i++) _gpu.Frame(build);
        const int frames = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _gpu.Frame(build);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр плёнки выделил {bytes} Б");
        var names = _gpu.Renderer.Graph.LastFrame.Select(r => r.Name).ToArray();
        Assert.Equal(["fluid.painter", "fluid.stamp", "fluid.composite", "fluid.advection", "fluid.divergence", "fluid.pressure.x", "fluid.pressure.y",
            "fluid.stats.rows", "fluid.stats", "fluid.export", "fluid.export.age"], names);
        Assert.True(Mass(s.Sim.ReadFields()) > 0);
        s.Sim.Reset();
        _gpu.Frame(f => s.Sim.Step(f, new Fluid2DStepInput(0, default, default)));
        Assert.Equal(0, Mass(s.Sim.ReadFields()));
        _gpu.AssertNoDebugMessages();
    }
}
