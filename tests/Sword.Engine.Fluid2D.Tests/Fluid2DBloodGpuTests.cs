using System.Numerics;
using Sword.Engine.Render;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.Fluid2DTestKit;
using static Sword.Engine.Fluid2D.Tests.PainterKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Режим крови на устройстве (слой отладки и GPU-валидация): стадии пресета <see cref="Fluid2DParams.Blood"/> со сдвигом окна, возрастом и
/// штампами = эталон; лужа в покое; баланс массы в half; затухание скорости; сдвиг туда и обратно; штампы — масса и перестановки; возраст;
/// 0 Б на кадр.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DBloodGpuTests : IDisposable
{
    private const double StageUlps = 2;
    private readonly GpuDevice _gpu = new("Sword.Engine.Fluid2D.Tests.Blood");
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _dispose = [];

    public Fluid2DBloodGpuTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var d in _dispose) d.Dispose();
        _gpu.Dispose();
    }

    /// <summary>Кровь на n² с тем же текселем 3.125 см (площадка n·3.125 см).</summary>
    public static Fluid2DParams Blood(int n) => Fluid2DParams.Blood with { Resolution = n, Extents = new Vector3(n * 0.03125f, n * 0.03125f, 0.65f) };

    private static readonly Fluid2DStepInput Shifted = new(4321, new Vector2(1.7f, -2.3f), Vector2.Zero) { ShiftTexels = (3, -2) };

    private sealed record Setup(Fluid2DSim Sim, Fluid2DNoiseImage Density, Fluid2DImage BrushNoise, Fluid2DImage Shape);

    private Setup Create(Fluid2DParams p, string name = "blood")
    {
        var (dn, dnCpu, i1) = _gpu.Noise("test.noise.density", 128, 51);
        var (bn, bnCpu, i2) = _gpu.Image("test.noise.brush", 64, 52);
        var (sh, shCpu, i3) = _gpu.Image("test.stamp.shape", 32, 53);
        _dispose.AddRange([i1, i2, i3]);
        var sim = new Fluid2DSim(_gpu.Renderer, name, p, dn, default, bn, default, sh);
        _dispose.Add(sim);
        return new Setup(sim, dnCpu, bnCpu, shCpu);
    }

    private Fluid2DReference Reference(Fluid2DParams p, Setup s, Fluid2DFields fields) => new(p, s.Density, null, fields, [s.Shape]);

    private static float ShapeMean(Fluid2DImage img)
    {
        double sum = 0;
        for (int i = 0; i < img.Width * img.Height; i++) sum += img.Rgba[i * 4];
        return (float)(sum / (img.Width * img.Height));
    }

    /// <summary>Случайные поля и возраст в [0, 100) с.</summary>
    private static Fluid2DFields RandomBloodFields(int n, int seed)
    {
        var f = RandomFields(n, seed);
        var rng = new Random(seed + 100);
        for (int i = 0; i < n * n; i++)
        {
            f.AgeAdvected[i] = rng.NextSingle() * 100;
            f.AgeComposite[i] = rng.NextSingle() * 100;
        }
        return f;
    }

    /// <summary>Штампы шага: гауссы разных размеров и поворотов (часть — за краем, часть мельче текселя) и формы из текстуры.</summary>
    private static Fluid2DStamps RandomStamps(Fluid2DParams p, ulong step, int count, int seed, float[]? shapeMeans, bool reverse = false)
    {
        var rng = new Random(seed);
        var list = new (Vector2 C, float A, Vector2 H, float V, int S)[count];
        for (int i = 0; i < count; i++)
        {
            float h = 0.005f + rng.NextSingle() * 0.2f;
            list[i] = (new Vector2(rng.NextSingle() * 1.2f - 0.1f, rng.NextSingle() * 1.2f - 0.1f), rng.NextSingle() * 6.3f,
                new Vector2(h, h * (0.3f + rng.NextSingle())), 1e-4f + rng.NextSingle() * 3e-3f, shapeMeans is not null && i % 4 == 3 ? 1 : 0);
        }
        if (reverse) Array.Reverse(list);
        var stamps = new Fluid2DStamps(p, shapeMeans: shapeMeans);
        stamps.Begin(step);
        foreach (var (c, a, h, v, s) in list) stamps.AddUv(c, a, h, v, s);
        stamps.End();
        return stamps;
    }

    private static Half[] Output(Fluid2DFields f, Fluid2DStage stage) => stage switch
    {
        Fluid2DStage.Composite => f.Composite,
        Fluid2DStage.Advection => f.Advected,
        Fluid2DStage.Divergence or Fluid2DStage.PressureY => f.Scratch,
        _ => f.Pressure,
    };

    private static (double Rel, int Index) CompareAge(float[] gpu, float[] cpu)
    {
        double worst = 0;
        int index = -1;
        for (int i = 0; i < cpu.Length; i++)
        {
            double d = Math.Abs(gpu[i] - cpu[i]) / Math.Max(Math.Abs(cpu[i]), 1);
            if (d > worst) (worst, index) = (d, i);
        }
        return (worst, index);
    }

    /// <summary>Стадии пресета крови 64² со сдвигом окна (3, −2), возрастом, штампами (гаусс и форма) и кистью = эталон в 2 ULP half.</summary>
    [Theory]
    [InlineData(Fluid2DStage.Composite)]
    [InlineData(Fluid2DStage.Advection)]
    [InlineData(Fluid2DStage.Divergence)]
    [InlineData(Fluid2DStage.PressureX)]
    [InlineData(Fluid2DStage.PressureY)]
    public void BloodStage_MatchesReference(Fluid2DStage stage)
    {
        var p = Blood(64);
        var s = Create(p);
        var fields = RandomBloodFields(64, 61);
        var brush = RandomBrush(p.BrushResolution, 62);
        using var brushStorage = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", brush);
        var stamps = RandomStamps(p, Shifted.Step, 24, 63, [ShapeMean(s.Shape)]);
        s.Sim.Load(fields);
        _gpu.Frame(f => s.Sim.AddStage(f, Shifted, brushStorage, stage, stamps));
        var cpu = Reference(p, s, fields.Clone());
        cpu.Stage(stage, Shifted, brush, stamps);
        var gpu = s.Sim.ReadFields();
        var e = Compare(Output(gpu, stage), Output(cpu.Fields, stage));
        _output.WriteLine($"кровь, {stage}: {e.Ulps:F2} ULP half, {e.Abs:G4} (элемент {e.Index}); штампов {stamps.Stats}");
        Assert.True(e.Ulps <= StageUlps, $"{stage}: {e.Ulps} ULP (элемент {e.Index})");
        if (stage is Fluid2DStage.Composite or Fluid2DStage.Advection)
        {
            var age = stage == Fluid2DStage.Composite ? (gpu.AgeComposite, cpu.Fields.AgeComposite) : (gpu.AgeAdvected, cpu.Fields.AgeAdvected);
            var (rel, index) = CompareAge(age.Item1, age.Item2);
            _output.WriteLine($"возраст: {rel:G3} относительно (элемент {index})");
            Assert.True(rel <= 1e-5, $"возраст {stage}: {rel} (элемент {index}: {age.Item1[index]} против {age.Item2[index]})");
        }
        if (stage == Fluid2DStage.Composite)
        {
            var grid = s.Sim.ReadStampGrid()!;
            double worst = 0;
            for (int i = 0; i < grid.Length; i++) worst = Math.Max(worst, Math.Abs(grid[i] - cpu.StampGrid![i]) / Math.Max(Math.Abs(cpu.StampGrid[i]), 1e-3));
            _output.WriteLine($"грид штампов: {worst:G3} относительно");
            Assert.True(worst <= 1e-5, $"грид штампов: {worst}");
        }
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Painter крови 64² со сдвигом окна: мировая площадка, жёсткость, Kill, шум плотности 0.4 — GPU = эталон.</summary>
    [Fact]
    public void BloodPainter_WithShift_MatchesReference()
    {
        var p = Blood(64);
        var s = Create(p);
        var brush = new Fluid2DBrush(p, 9);
        var rng = new Random(7);
        var points = new (Vector3 P, Vector3 V, float S)[16];
        for (int i = 0; i < points.Length; i++)
            points[i] = (new Vector3(rng.NextSingle() * 1.6f - 0.8f, rng.NextSingle() * 1.6f - 0.8f, 0), new Vector3(rng.NextSingle() * 8 - 4, rng.NextSingle() * 8 - 4, 0),
                0.5f + rng.NextSingle() * 2);
        Fluid2DStepInput input = default;
        for (int step = 0; step < 21; step++)
        {
            input = Shifted with { Step = (ulong)(100 + step) };
            brush.Begin(input, (ulong)step, Vector3.Zero);
            for (int i = 0; i < points.Length; i++) brush.Add(Key((uint)i + 1), points[i].P + points[i].V * (step / 60f), points[i].S);
            brush.End();
        }
        var old = PreviousPaint(p.BrushResolution);
        s.Sim.LoadBrush(old);
        _gpu.Frame(f => s.Sim.AddPainterStage(f, input, brush));
        var cpu = new Fluid2DPainterReference(p, s.BrushNoise, null, (Fluid2DBrushTexel[])old.Clone());
        cpu.Paint(brush, input);
        var e = Compare(s.Sim.ReadBrush(), cpu.Grid);
        _output.WriteLine($"Painter крови: {e.Ulps:F2} ULP half ({brush.Stats})");
        Assert.True(brush.Stats.Kept >= 8, $"точек {brush.Stats.Kept}");
        Assert.True(e.Ulps <= StageUlps, $"{e.Ulps} ULP (элемент {e.Index})");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Лужа: диск ρ 0.3–0.9 с мягким краем, v = 0, в A и C, возраст 0.</summary>
    private static Fluid2DFields Puddle(int n, float radius = 0.2f)
    {
        var f = new Fluid2DFields(n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.47f;
                float r = MathF.Sqrt(u * u + v * v) / radius;
                if (r >= 1) continue;
                float rho = (0.3f + 0.55f * (1 - r * r)) * (0.9f + 0.1f * MathF.Sin(17 * u) * MathF.Cos(11 * v));
                f.Advected[(y * n + x) * 4 + 2] = (Half)rho;
                f.Composite[(y * n + x) * 4 + 2] = (Half)rho;
            }
        return f;
    }

    private void Run(Fluid2DSim sim, ulong first, int steps, Func<ulong, Fluid2DStepInput>? input = null, int perFrame = 50)
    {
        for (int s = 0; s < steps; s += perFrame)
        {
            int begin = s, end = Math.Min(s + perFrame, steps);
            _gpu.Frame(f =>
            {
                for (int k = begin; k < end; k++) sim.Step(f, input?.Invoke(first + (ulong)k) ?? new Fluid2DStepInput(first + (ulong)k, default, default));
            });
        }
    }

    /// <summary>
    /// Лужа в покое 256², 10 000 шагов: v остаётся ровно 0, центроид на месте, ни один тексель не уходит дальше 1 ULP half от начального
    /// (размытия нет), Σρ — в 0.1 % (без притока за 600 шагов — тоже).
    /// </summary>
    [Fact]
    public void Puddle_AtRest_10000Steps_DoesNotMoveOrBlur()
    {
        var p = Blood(256);
        var s = Create(p);
        var initial = Puddle(256);
        s.Sim.Load(initial);
        _gpu.Frame(f => s.Sim.Step(f, new Fluid2DStepInput(0, default, default)));
        var start = s.Sim.ReadStats();
        double mass0 = Sum(initial.Advected, 2, 4);
        Run(s.Sim, 1, 599);
        double mass600 = Sum(s.Sim.ReadFields().Advected, 2, 4);
        Run(s.Sim, 600, 9400);
        var fields = s.Sim.ReadFields();
        var end = s.Sim.ReadStats();
        double mass = Sum(fields.Advected, 2, 4);
        double drift600 = (mass600 - mass0) / mass0, drift = (mass - mass0) / mass0;
        double worstUlps = 0;
        for (int i = 2; i < fields.Advected.Length; i += 4)
        {
            double a = (double)initial.Advected[i], b = (double)fields.Advected[i];
            if (a == 0 && b == 0) continue;
            worstUlps = Math.Max(worstUlps, Math.Abs(a - b) / HalfUlp(Math.Max(a, b)));
        }
        _output.WriteLine($"лужа 10 000 шагов: ΣρA {mass0} → {mass600} (600, {drift600:P4}) → {mass} ({drift:P4}); ΣρC {start.DensitySum} → {end.DensitySum}; " +
            $"центроид {start.Centroid} → {end.Centroid}; max|v| {end.MaxSpeed}; тексель A — до {worstUlps} ULP half");
        Assert.Equal(0f, end.MaxSpeed);
        Assert.True((end.Centroid - start.Centroid).Length() < 1e-4f, $"центроид {end.Centroid}");
        Assert.True(worstUlps <= 1, $"тексель ушёл на {worstUlps} ULP");
        Assert.True(Math.Abs(drift600) <= 1e-3, $"масса за 600 шагов {drift600:P4}");
        Assert.True(Math.Abs(drift) <= 1e-3, $"масса за 10 000 шагов {drift:P4}");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// Без притока, лужа с толчком (вся площадка — vx 1.5, vy −0.7 ед.: лужа уезжает на 1.5 м и тормозится, шум скорости по маске |v|
    /// работает): масса C за 600 шагов — в 0.5 % (замер +0.13 %: полулагранжева адвекция при неоднородной скорости не сохраняет массу;
    /// баланс half — тест лужи в покое, 0.0002 %).
    /// </summary>
    [Fact]
    public void Mass_PushedPuddle_600Steps_Within0p5Percent()
    {
        var p = Blood(256);
        var s = Create(p);
        var initial = Puddle(256, 0.15f);
        for (int i = 0; i < 256 * 256; i++)
        {
            initial.Advected[i * 4] = (Half)1.5f;
            initial.Advected[i * 4 + 1] = (Half)(-0.7f);
        }
        s.Sim.Load(initial);
        _gpu.Frame(f => s.Sim.Step(f, new Fluid2DStepInput(0, default, default)));
        var first = s.Sim.ReadStats();
        Run(s.Sim, 1, 599);
        var end = s.Sim.ReadStats();
        double drift = (end.DensitySum - first.DensitySum) / first.DensitySum;
        _output.WriteLine($"толчок: Σρ {first.DensitySum} → {end.DensitySum} ({drift:P4}), центроид {first.Centroid} → {end.Centroid}, max|v| {end.MaxSpeed}");
        Assert.True((end.Centroid - first.Centroid).X > 1e-3f, "лужа не сдвинулась");
        Assert.True(Math.Abs(drift) <= 5e-3, $"масса {drift:P4}");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Однородная скорость 1 ед. по луже ρ 0.5 на всей площадке 128²: средняя vx в центре гаснет e-кратно за 9.5 ± 1 шага (VF 0.9).</summary>
    [Fact]
    public void Velocity_DecaysEFold_In9p5Steps()
    {
        var p = Blood(128);
        var s = Create(p);
        var f0 = new Fluid2DFields(128);
        for (int i = 0; i < 128 * 128; i++)
        {
            f0.Advected[i * 4] = (Half)1f;
            f0.Advected[i * 4 + 2] = (Half)0.5f;
        }
        s.Sim.Load(f0);
        double Mean(Fluid2DFields f)
        {
            double sum = 0;
            for (int y = 32; y < 96; y++)
                for (int x = 32; x < 96; x++) sum += (float)f.Composite[(y * 128 + x) * 4];
            return sum / (64 * 64);
        }
        _gpu.Frame(f => s.Sim.Step(f, new Fluid2DStepInput(0, default, default)));
        double v0 = Mean(s.Sim.ReadFields());
        Run(s.Sim, 1, 20);
        double v20 = Mean(s.Sim.ReadFields());
        double eFold = 20 / Math.Log(v0 / v20);
        _output.WriteLine($"vx {v0:F4} → {v20:F4} за 20 шагов: e-кратно за {eFold:F2} шага");
        Assert.InRange(eFold, 8.5, 10.5);
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// Сдвиг окна на (k, m) и обратно = два шага без сдвига, байт в байт в пределах окна: лужа в покое с возрастом, C, A, P, S и возраст.
    /// Плюс один шаг со сдвигом — точная копия текселей (x + сдвиг) шага без сдвига.
    /// </summary>
    [Fact]
    public void Shift_ThereAndBack_SameBytes()
    {
        var p = Blood(128);
        var a = Create(p, "a");
        var b = Create(p, "b");
        var initial = Puddle(128);
        for (int i = 0; i < 128 * 128; i++) initial.AgeAdvected[i] = (float)initial.Advected[i * 4 + 2] > 0 ? 3 + (i % 7) : 0;
        a.Sim.Load(initial);
        b.Sim.Load(initial);
        var there = new Fluid2DStepInput(10, new Vector2(7, -5) / 128f, default) { ShiftTexels = (7, -5) };
        var back = new Fluid2DStepInput(11, default, default) { ShiftTexels = (-7, 5) };
        _gpu.Frame(f => a.Sim.Step(f, there));
        _gpu.Frame(f => b.Sim.Step(f, new Fluid2DStepInput(10, default, default)));
        var shifted = a.Sim.ReadFields();
        var plain = b.Sim.ReadFields();
        int copied = 0;
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                int sx = x + 7, sy = y - 5;
                if (sx < 0 || sx >= 128 || sy < 0 || sy >= 128) continue;
                for (int k = 0; k < 3; k++)
                    Assert.Equal(plain.Composite[(sy * 128 + sx) * 4 + k], shifted.Composite[(y * 128 + x) * 4 + k]);
                Assert.Equal(plain.AgeComposite[sy * 128 + sx], shifted.AgeComposite[y * 128 + x]);
                if ((float)plain.Composite[(sy * 128 + sx) * 4 + 2] > 0) copied++;
            }
        _gpu.Frame(f => a.Sim.Step(f, back));
        _gpu.Frame(f => b.Sim.Step(f, back with { ShiftTexels = (0, 0) }));
        var fa = a.Sim.ReadFields();
        var fb = b.Sim.ReadFields();
        Assert.True(fa.Composite.AsSpan().SequenceEqual(fb.Composite), "C различается");
        Assert.True(fa.Advected.AsSpan().SequenceEqual(fb.Advected), "A различается");
        Assert.True(fa.Pressure.AsSpan().SequenceEqual(fb.Pressure), "P различается");
        Assert.True(fa.Scratch.AsSpan().SequenceEqual(fb.Scratch), "S различается");
        Assert.True(fa.AgeComposite.AsSpan().SequenceEqual(fb.AgeComposite), "возраст C различается");
        Assert.True(fa.AgeAdvected.AsSpan().SequenceEqual(fb.AgeAdvected), "возраст A различается");
        _output.WriteLine($"сдвиг (7, −5): {copied} текселей лужи скопированы точно; туда и обратно — байт в байт");
        Assert.True(copied > 1000);
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// Штампы 1024² (пресет как есть): Σρ C·площадь текселя = Σ объёмов ± 2 % (гауссы от мельче текселя до 40 см, поворот, вытянутые);
    /// те же штампы в обратном порядке — C и грид штампов бит в бит; грид = эталон.
    /// </summary>
    [Fact]
    public void Stamps_MassEqualsVolume_PermutationsBitIdentical()
    {
        var p = Fluid2DParams.Blood;
        var a = Create(p, "a");
        var b = Create(p, "b");
        Fluid2DStamps Make(bool reverse)
        {
            var rng = new Random(71);
            var list = new (Vector3 W, float A, Vector2 H, float V)[60];
            for (int i = 0; i < list.Length; i++)
            {
                float h = i < 10 ? 0.005f + 0.002f * i : 0.02f + rng.NextSingle() * 0.4f;
                list[i] = (new Vector3(rng.NextSingle() * 24 - 12, rng.NextSingle() * 24 - 12, 0), rng.NextSingle() * 6.3f,
                    new Vector2(h, h * (0.4f + 0.6f * rng.NextSingle())), 1e-4f + rng.NextSingle() * 2e-3f);
            }
            if (reverse) Array.Reverse(list);
            var stamps = new Fluid2DStamps(p);
            stamps.Begin(5, new Vector3(0, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2));
            foreach (var (w, ang, h, v) in list) stamps.Add(new Vector3(w.X, 0, w.Y), ang, h, v);
            stamps.End();
            return stamps;
        }
        var forward = Make(false);
        var reversed = Make(true);
        var input = new Fluid2DStepInput(5, default, default);
        _gpu.Frame(f => a.Sim.AddStage(f, input, null, Fluid2DStage.Composite, forward));
        _gpu.Frame(f => b.Sim.AddStage(f, input, null, Fluid2DStage.Composite, reversed));
        var fa = a.Sim.ReadFields();
        var fb = b.Sim.ReadFields();
        Assert.True(fa.Composite.AsSpan().SequenceEqual(fb.Composite), "C различается при перестановке");
        Assert.True(a.Sim.ReadStampGrid().AsSpan().SequenceEqual(b.Sim.ReadStampGrid()), "грид штампов различается при перестановке");
        double texelArea = (double)p.Extents.X / p.Resolution * (p.Extents.Y / p.Resolution);
        double mass = Sum(fa.Composite, 2, 4) * texelArea;
        double volume = forward.Stats.Volume;
        _output.WriteLine($"штампы {forward.Stats}: Σρ·S {mass:G6} против объёма {volume:G6} ({(mass - volume) / volume:P3})");
        Assert.Equal(60, forward.Stats.Kept);
        Assert.InRange(mass, volume * 0.98, volume * 1.02);

        // По одному: каждый штамп отдельно — в ±2 % (мельче текселя — после расширения до 0.75 текселя).
        var cpu = new Fluid2DReference(p, a.Density, null);
        var rng = new Random(72);
        double worst = 0;
        for (int i = 0; i < 12; i++)
        {
            var one = new Fluid2DStamps(p);
            one.Begin(5);
            float h = 0.003f * MathF.Pow(1.6f, i);
            one.AddUv(new Vector2(0.3f + 0.4f * rng.NextSingle(), 0.3f + 0.4f * rng.NextSingle()), rng.NextSingle() * 3, new Vector2(h, 0.7f * h), 1e-3f);
            one.End();
            cpu.Stage(Fluid2DStage.Composite, input, default, one);
            double m = 0;
            foreach (float v in cpu.StampGrid!) m += v;
            m *= texelArea;
            worst = Math.Max(worst, Math.Abs(m - 1e-3) / 1e-3);
            _output.WriteLine($"полуось {h * 100:F1} см: масса {m / 1e-3:F4} объёма");
        }
        Assert.True(worst <= 0.02, $"одиночный штамп: {worst:P2}");

        // Грид штампов GPU = эталон (те же формулы, exp2 GPU — доли ULP float).
        cpu.Stage(Fluid2DStage.Composite, input, default, forward);
        var grid = a.Sim.ReadStampGrid()!;
        double rel = 0;
        for (int i = 0; i < grid.Length; i++) rel = Math.Max(rel, Math.Abs(grid[i] - cpu.StampGrid![i]) / Math.Max(Math.Abs(cpu.StampGrid[i]), 1e-3));
        _output.WriteLine($"грид штампов 1024²: {rel:G3} относительно");
        Assert.True(rel <= 1e-5, $"грид штампов: {rel}");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Возраст: без притока растёт ровно на шаг за шаг (монотонно, 600 шагов — 10 с), вне лужи — 0; штамп в центр омолаживает.</summary>
    [Fact]
    public void Age_GrowsWithoutInflow_InflowRejuvenates()
    {
        var p = Blood(128);
        var s = Create(p);
        s.Sim.Load(Puddle(128));
        int center = (int)(0.47f * 128) * 128 + 64;
        float last = -1;
        for (int k = 0; k < 12; k++)
        {
            Run(s.Sim, (ulong)(k * 50), 50);
            var f = s.Sim.ReadFields();
            float age = f.AgeComposite[center];
            Assert.True(age > last, $"возраст не растёт: {last} → {age}");
            last = age;
            Assert.Equal(0f, f.AgeComposite[5]);
        }
        _output.WriteLine($"600 шагов: возраст {last} с");
        Assert.InRange(last, 600 / 60f - 1e-3f, 600 / 60f + 1e-3f);

        var before = s.Sim.ReadFields();
        float rho0 = (float)before.Advected[center * 4 + 2];
        var stamps = new Fluid2DStamps(p);
        stamps.Begin(600);
        stamps.AddUv(new Vector2((64 + 0.5f) / 128, (center / 128 + 0.5f) / 128), 0, new Vector2(0.3f, 0.3f), 0.05f);
        stamps.End();
        _gpu.Frame(f => s.Sim.Step(f, new Fluid2DStepInput(600, default, default), (GpuStorage?)null, stamps));
        var after = s.Sim.ReadFields();
        float rho1 = (float)after.Composite[center * 4 + 2], age1 = after.AgeComposite[center];
        float expected = (last + (float)p.StepSeconds) * (rho0 * 1.1111112f / rho1);
        _output.WriteLine($"штамп: ρ {rho0 * 1.1111f:F4} → {rho1:F4}, возраст {last:F4} → {age1:F4} (ожидание {expected:F4})");
        Assert.True(age1 < 0.8f * last, $"приток не омолодил: {age1}");
        Assert.InRange(age1, expected * 0.995f, expected * 1.005f);
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Кадр крови со штампами, кистью, экспортом и экспортом возраста — 0 Б; проходы по порядку.</summary>
    [Fact]
    public void BloodFrame_AllocatesNothing()
    {
        var p = Blood(128);
        var s = Create(p);
        var brush = new Fluid2DBrush(p, 3);
        var stamps = new Fluid2DStamps(p);
        ulong step = 0;
        Action<RenderFrame> build = f =>
        {
            var input = new Fluid2DStepInput(step, default, default) { ShiftTexels = ((int)(step % 3) - 1, 0) };
            brush.Begin(input, step, Vector3.Zero);
            brush.Add(Key(1), new Vector3(0.01f * step, 0.3f, 0), 1.5f);
            brush.End();
            stamps.Begin(step);
            stamps.AddUv(new Vector2(0.5f, 0.4f + 0.001f * step), 0.3f, new Vector2(0.1f, 0.05f), 1e-3f);
            stamps.AddUv(new Vector2(0.45f, 0.6f), 0, new Vector2(0.02f, 0.02f), 1e-4f);
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
        Assert.True(bytes == 0, $"кадр крови выделил {bytes} Б");
        var names = _gpu.Renderer.Graph.LastFrame.Select(r => r.Name).ToArray();
        Assert.Equal(["fluid.painter", "fluid.stamp", "fluid.composite", "fluid.advection", "fluid.divergence", "fluid.pressure.x", "fluid.pressure.y",
            "fluid.stats.rows", "fluid.stats", "fluid.export", "fluid.export.age"], names);
        Assert.True(s.Sim.ReadStats().DensitySum > 0);
        _gpu.AssertNoDebugMessages();
    }
}

/// <summary>
/// GPU-время шага крови 1024² без слоя отладки и валидации: Painter (16 точек), штампы (32), стадии, статистика, экспорт и возраст; у
/// <see cref="Fluid2DParams.BloodSpread"/> — с высотами рельефа (сетка 256 ячеек на ±32 м).
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DBloodTimingGpuTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BloodStep1024_GpuTime(bool spread)
    {
        using var gpu = new GpuDevice("Sword.Engine.Fluid2D.Tests.BloodTiming", validation: false);
        var p = spread ? Fluid2DParams.BloodSpread : Fluid2DParams.Blood;
        var (dn, _, i1) = gpu.Noise("test.noise.density", 128, 31);
        var (bn, _, i2) = gpu.Image("test.noise.brush", 64, 32);
        using var sim = new Fluid2DSim(gpu.Renderer, "timing", p, dn, default, bn);
        var heights = new float[257 * 257];
        for (int i = 0; i < heights.Length; i++) heights[i] = 0.1f * MathF.Sin(0.05f * (i % 257)) + 0.002f * (i / 257);
        using var heightStorage = gpu.Renderer.CreateStorage<float>("test.heights", heights);
        if (spread)
        {
            int srv = -1;
            gpu.Frame(f => srv = f.Graph.SrvOf(f.Graph.Import(heightStorage)));
            sim.Heights = new Fluid2DHeightField(srv, 256, 32);
        }
        var brush = new Fluid2DBrush(p, 1);
        var stamps = new Fluid2DStamps(p);
        var rng = new Random(3);
        var drops = new Vector2[32];
        for (int i = 0; i < drops.Length; i++) drops[i] = new Vector2(0.3f + 0.4f * rng.NextSingle(), 0.3f + 0.4f * rng.NextSingle());
        var passes = new Dictionary<string, List<double>>();
        for (int frame = 0; frame < 240; frame++)
        {
            ulong step = (ulong)frame;
            gpu.Frame(f =>
            {
                var input = new Fluid2DStepInput(step, default, default);
                brush.Begin(input, step, Vector3.Zero);
                for (int k = 0; k < 16; k++)
                    brush.Add(PainterKit.Key((uint)k + 1), new Vector3(-4 + k * 0.5f + 3 * MathF.Sin(0.05f * step + k), 2 * MathF.Cos(0.04f * step + k), 0), 1.5f);
                brush.End();
                stamps.Begin(step);
                foreach (var d in drops) stamps.AddUv(d, 0.5f, new Vector2(0.05f, 0.03f), 2e-4f);
                stamps.End();
                sim.Step(f, input, brush, stamps);
                sim.Export(f);
                sim.ExportAge(f);
            });
            var times = gpu.Renderer.Gpu;
            if (frame < 60 || !times.Valid) continue;
            for (int i = 0; i < times.Count; i++)
            {
                string name = times.NameOf(i);
                if (!name.StartsWith("fluid.", StringComparison.Ordinal)) continue;
                if (!passes.TryGetValue(name, out var list)) passes[name] = list = [];
                list.Add(times.MillisecondsOf(i));
            }
        }
        double total = 0, stages = 0;
        foreach (var (name, list) in passes.OrderBy(kv => kv.Key))
        {
            list.Sort();
            double median = list[list.Count / 2];
            total += median;
            if (name is "fluid.composite" or "fluid.advection" or "fluid.divergence" or "fluid.pressure.x" or "fluid.pressure.y") stages += median;
            output.WriteLine($"gpu.{name}: медиана {median:F4} мс ({list.Count} кадров)");
        }
        output.WriteLine($"{(spread ? "кровь A′" : "кровь")} 1024²: 5 стадий {stages:F4} мс; всё (Painter, штампы, статистика, экспорт, возраст) {total:F4} мс; {gpu.Renderer.AdapterName}");
        Assert.True(total < 5.0, $"шаг крови 1024² — {total} мс GPU");
        i1.Dispose();
        i2.Dispose();
    }
}
