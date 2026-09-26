using System.Numerics;
using Sword.Engine.Render;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.Fluid2DTestKit;
using static Sword.Engine.Fluid2D.Tests.PainterKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Painter на устройстве (слой отладки и GPU-валидация) против CPU-эталона <see cref="Fluid2DPainterReference"/>: стадия на прошлой краске
/// и точках шага (факел, «всё сразу» с битмапом, painter-режим, сложение), контроль испорченным входом, шаг с кистью цепочкой стадий,
/// перестановки и два прогона — бит в бит, другое зерно — другое поле, снимок, 0 Б на кадр и имена проходов.
/// </summary>
/// <remarks>
/// Допуск — 2 ULP half, как у стадий сима: GPU и CPU считают одни выражения float по одним числам точек (их считает CPU); расходятся
/// приближённые exp2/pow и деление GPU (доли ULP float), которые изредка перекидывают округление в half на 1 ULP.
/// </remarks>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DPainterGpuTests : IDisposable
{
    private const double Ulps = 2;
    private readonly GpuDevice _gpu = new("Sword.Engine.Fluid2D.Tests.Painter");
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _dispose = [];

    public Fluid2DPainterGpuTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var d in _dispose) d.Dispose();
        _gpu.Dispose();
    }

    private sealed record Setup(Fluid2DSim Sim, Fluid2DImage Noise, Fluid2DImage? Bitmap, Fluid2DNoiseImage Density, Fluid2DNoiseImage Amp);

    private Setup Create(Fluid2DParams p, bool bitmap, string name = "painter")
    {
        var (dn, dnCpu, i1) = _gpu.Noise("test.noise.density", 128, 41);
        var (an, anCpu, i2) = _gpu.Noise("test.noise.amp", 128, 42);
        var (bn, bnCpu, i3) = _gpu.Image("test.noise.brush", 64, 43);
        var (bm, bmCpu, i4) = _gpu.Image("test.bitmap", 32, 44);
        _dispose.AddRange([i1, i2, i3, i4]);
        var sim = new Fluid2DSim(_gpu.Renderer, name, p, dn, an, bn, bitmap ? bm : default);
        _dispose.Add(sim);
        return new Setup(sim, bnCpu, bitmap ? bmCpu : null, dnCpu, anCpu);
    }

    public static TheoryData<string> Variants => ["torch", "everything", "painter mode", "additive"];

    private static (Fluid2DParams P, bool Bitmap) Variant(string name) => name switch
    {
        "torch" => (Fluid2DParams.Torch, false),
        "everything" => (Fluid2DParams.Torch with
        {
            PersistencyOfFieldAndPointData = 0.8f, SelectivelyKillBrushDensityKeepVelocity = 1, NegativePuncture = true, FadeBrushByZPos = true,
            FadeBrushByZPosMult = 3, BrushHardness = 0.3f, BrushHardnessScaledByObjSize = 0.2f, KillBrushBelowThisVelocity = 0.05f,
            BrushNoiseInWorldSpace = false, PainterEdgeMaskWidth = 0.2f, DensityTxtMult = 0.7f, DensityTxtOffsetX = 0.03f, DensityTxtScale = 1.5f,
            DensityTxtRandomOffset = 0.37f, BrushVelocityMultXY = 2, BrushVelocityMultZ = 2.5f, BrushPuncture = 0.12f, BrushStrength = 0.8f,
            ArrayBrushVelocity = 1.7f, ParticleBrushVelocity = 0.6f, CameraFacing = false, BrushRndScaledByVelocity = true,
        }, true),
        "painter mode" => (Fluid2DParams.Torch with
        {
            SimplePainterMode = true, PersistencyOfFieldAndPointData = 1, BrushStrength = 0.5f, BrushHardness = 0.6f,
            UseInputTextureOnlyAsDensity = true, DensityTxtMult = 0.9f,
        }, true),
        _ => (Fluid2DParams.Torch with { ForceAdditiveStrokeBlending = true }, false),
    };

    /// <summary>24 точки трёх классов по всей площадке (часть перекрывается), движутся; 20 шагов кисти на CPU до сверяемого.</summary>
    private static Fluid2DBrush WarmBrush(Fluid2DParams p, ulong seed, int steps, bool reverse = false)
    {
        var brush = new Fluid2DBrush(p, seed);
        var rng = new Random(5);
        var points = new (Fluid2DBrushKey, Vector3, Vector3, float)[24];
        for (int i = 0; i < points.Length; i++)
            points[i] = (Key((uint)(i / 4 + 1), (ushort)i, (Fluid2DBrushSource)(i % 3)),
                new Vector3(rng.NextSingle() * 2.6f - 1.3f, rng.NextSingle() * 2.6f - 1.3f, rng.NextSingle() * 0.1f),
                new Vector3(rng.NextSingle() * 4 - 2, rng.NextSingle() * 4 - 2, rng.NextSingle() - 0.5f), 0.2f + rng.NextSingle() * 1.5f);
        if (reverse) Array.Reverse(points);
        for (int s = 0; s < steps; s++)
        {
            brush.Begin(StepInput((ulong)s), (ulong)s, Vector3.Zero);
            foreach (var (key, position, velocity, scale) in points) brush.Add(key, position + velocity * (s / 60f), scale);
            brush.End();
        }
        return brush;
    }

    private static Fluid2DStepInput StepInput(ulong step) => new(1000 + step, new Vector2(0.37f + 0.002f * step, -1.25f), new Vector2(0.0021f, -0.0013f));

    private void Report(string what, (double Ulps, double Abs, int Index) e) =>
        _output.WriteLine($"{what}: {e.Ulps:F2} ULP half, {e.Abs:G4} абсолютно (элемент {e.Index})");

    [Theory]
    [MemberData(nameof(Variants))]
    public void Painter_MatchesReference(string variant)
    {
        var (p, bitmap) = Variant(variant);
        var s = Create(p, bitmap);
        var brush = WarmBrush(p, 3, 21);
        var input = StepInput(20);
        var old = PreviousPaint(p.BrushResolution);
        s.Sim.LoadBrush(old);
        _gpu.Frame(f => s.Sim.AddPainterStage(f, input, brush));
        var cpu = new Fluid2DPainterReference(p, s.Noise, s.Bitmap, (Fluid2DBrushTexel[])old.Clone());
        cpu.Paint(brush, input);
        var e = Compare(s.Sim.ReadBrush(), cpu.Grid);
        Report($"{variant} ({brush.Stats})", e);
        Assert.True(brush.Stats.Kept >= 20, $"точек {brush.Stats.Kept}");
        Assert.True(e.Ulps <= Ulps, $"{variant}: {e.Ulps} ULP (элемент {e.Index})");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Контроль допуска: прошлая краска на GPU транспонирована — расхождение много больше допуска.</summary>
    [Fact]
    public void TransposedPreviousPaint_ExceedsTolerance()
    {
        var p = Fluid2DParams.Torch;
        var s = Create(p, false);
        var brush = WarmBrush(p, 3, 21);
        int bn = p.BrushResolution;
        var old = PreviousPaint(bn);
        var transposed = new Fluid2DBrushTexel[old.Length];
        for (int y = 0; y < bn; y++)
            for (int x = 0; x < bn; x++) transposed[y * bn + x] = old[x * bn + y];
        s.Sim.LoadBrush(transposed);
        _gpu.Frame(f => s.Sim.AddPainterStage(f, StepInput(20), brush));
        var cpu = new Fluid2DPainterReference(p, s.Noise, null, old);
        cpu.Paint(brush, StepInput(20));
        var e = Compare(s.Sim.ReadBrush(), cpu.Grid);
        Report("транспонированная прошлая краска", e);
        Assert.True(e.Ulps > 1000 * Ulps && e.Abs > 0.1, $"ошибка не видна: {e.Ulps} ULP, {e.Abs}");
    }

    /// <summary>
    /// Шаг с кистью на полях факела 368²: грид кисти GPU = эталон Painter, C = эталон Composite на гриде кисти GPU (цепочка стадий, как в
    /// <see cref="Fluid2DGpuTests"/>); проходы — fluid.painter.clear, fluid.painter, затем стадии сима.
    /// </summary>
    [Fact]
    public void StepWithBrush_MatchesReference_StageByStage()
    {
        var p = Fluid2DParams.Torch;
        var s = Create(p, false);
        var fields = RandomFields(p.Resolution, 19);
        s.Sim.Load(fields);
        var brush = WarmBrush(p, 3, 21);
        var input = StepInput(20);
        _gpu.Frame(f => s.Sim.Step(f, input, brush));
        var names = _gpu.Renderer.Graph.LastFrame.Select(r => r.Name).ToArray();
        Assert.Equal(["fluid.painter.clear", "fluid.painter", "fluid.composite", "fluid.advection", "fluid.divergence", "fluid.pressure.x",
            "fluid.pressure.y", "fluid.stats.rows", "fluid.stats"], names);
        var gpuBrush = s.Sim.ReadBrush();
        var painter = new Fluid2DPainterReference(p, s.Noise, null);
        painter.Paint(brush, input);
        var e = Compare(gpuBrush, painter.Grid);
        Report("кисть шага", e);
        Assert.True(e.Ulps <= Ulps, $"кисть: {e.Ulps} ULP");
        var cpu = new Fluid2DReference(p, s.Density, s.Amp, fields.Clone());
        cpu.Stage(Fluid2DStage.Composite, input, gpuBrush);
        var c = Compare(s.Sim.ReadFields().Composite, cpu.Fields.Composite);
        Report("C на кисти GPU", c);
        Assert.True(c.Ulps <= Ulps, $"C: {c.Ulps} ULP");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Два сима, точки добавлены в разном порядке, 60 шагов (по 3 за кадр) — поля и кисть бит в бит; другое зерно — другие поля.</summary>
    [Fact]
    public void Runs_AreBitIdentical_AcrossPermutations_SeedChangesField()
    {
        var p = Fluid2DParams.Torch;
        var a = Run(p, Create(p, false, "a").Sim, 7, reverse: false);
        var b = Run(p, Create(p, false, "b").Sim, 7, reverse: true);
        var c = Run(p, Create(p, false, "c").Sim, 8, reverse: false);
        Assert.True(Halves(a.Brush).AsSpan().SequenceEqual(Halves(b.Brush)), "кисть различается");
        Assert.True(a.Fields.Composite.AsSpan().SequenceEqual(b.Fields.Composite), "C различается");
        Assert.True(a.Fields.Advected.AsSpan().SequenceEqual(b.Fields.Advected), "A различается");
        Assert.False(a.Fields.Composite.AsSpan().SequenceEqual(c.Fields.Composite), "зерно не меняет поле");
        _gpu.AssertNoDebugMessages();
    }

    private (Fluid2DBrushTexel[] Brush, Fluid2DFields Fields) Run(Fluid2DParams p, Fluid2DSim sim, ulong seed, bool reverse)
    {
        var brush = new Fluid2DBrush(p, seed);
        var rng = new Random(5);
        var points = new (Fluid2DBrushKey, Vector3, Vector3, float)[24];
        for (int i = 0; i < points.Length; i++)
            points[i] = (Key((uint)(i / 4 + 1), (ushort)i, (Fluid2DBrushSource)(i % 3)),
                new Vector3(rng.NextSingle() * 2.6f - 1.3f, rng.NextSingle() * 2.6f - 1.3f, rng.NextSingle() * 0.1f),
                new Vector3(rng.NextSingle() * 4 - 2, rng.NextSingle() * 4 - 2, rng.NextSingle() - 0.5f), 0.2f + rng.NextSingle() * 1.5f);
        if (reverse) Array.Reverse(points);
        for (int step = 0; step < 60; step += 3)
        {
            int first = step;
            _gpu.Frame(f =>
            {
                for (int k = first; k < first + 3; k++)
                {
                    var input = StepInput((ulong)k);
                    brush.Begin(input, (ulong)k, Vector3.Zero);
                    foreach (var (key, position, velocity, scale) in points) brush.Add(key, position + velocity * (k / 60f), scale);
                    brush.End();
                    sim.Step(f, input, brush);
                }
            });
        }
        return (sim.ReadBrush(), sim.ReadFields());
    }

    /// <summary>
    /// Шум кисти из пака (<c>effects/fire/noise_template2.swtex</c>, слой T_NoiseTemplate2, линейный RG): Painter факела = эталон на значениях,
    /// прочитанных с GPU.
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void PackBrushNoise_Painter_MatchesReference()
    {
        using var texture = _gpu.Renderer.LoadTexture("effects/fire/noise_template2.swtex");
        Assert.False(texture.Srgb, "шум кисти — линейный (BR §4.1)");
        var noise = Fluid2DNoise.FromLayer(texture, "T_NoiseTemplate2");
        var p = Fluid2DParams.Torch;
        var (dn, _, i1) = _gpu.Noise("test.noise.density", 128, 41);
        var (an, _, i2) = _gpu.Noise("test.noise.amp", 128, 42);
        _dispose.AddRange([i1, i2]);
        using var sim = new Fluid2DSim(_gpu.Renderer, "pack", p, dn, an, noise);
        var brush = WarmBrush(p, 3, 21);
        var old = PreviousPaint(p.BrushResolution);
        sim.LoadBrush(old);
        _gpu.Frame(f => sim.AddPainterStage(f, StepInput(20), brush));
        var cpu = new Fluid2DPainterReference(p, _gpu.ReadImage(noise), null, old);
        cpu.Paint(brush, StepInput(20));
        var e = Compare(sim.ReadBrush(), cpu.Grid);
        Report($"шум пака {texture}", e);
        Assert.True(e.Ulps <= Ulps, $"{e.Ulps} ULP (элемент {e.Index})");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// 30 шагов по одному за кадр и все 30 за один кадр (буфер точек кадра растёт посреди кадра) — поля и кисть бит в бит: результат не
    /// зависит от разбиения шагов по кадрам.
    /// </summary>
    [Fact]
    public void StepsPerFrame_DoNotChangeResult()
    {
        var p = Fluid2DParams.Torch;
        var one = Create(p, false, "one").Sim;
        var many = Create(p, false, "many").Sim;
        var b1 = new Fluid2DBrush(p, 5);
        var b2 = new Fluid2DBrush(p, 5);
        void Add(Fluid2DBrush b, int k)
        {
            var input = StepInput((ulong)k);
            b.Begin(input, (ulong)k, Vector3.Zero);
            for (uint i = 0; i < 64; i++) b.Add(Key(i + 1), new Vector3(0.04f * i - 1.2f + 0.02f * k, 0.5f * MathF.Sin(i + 0.1f * k), 0), 0.5f + 0.02f * i);
            b.End();
        }
        for (int k = 0; k < 30; k++)
            _gpu.Frame(f =>
            {
                Add(b1, k);
                one.Step(f, StepInput((ulong)k), b1);
            });
        _gpu.Frame(f =>
        {
            for (int k = 0; k < 30; k++)
            {
                Add(b2, k);
                many.Step(f, StepInput((ulong)k), b2);
            }
        });
        Assert.True(Halves(one.ReadBrush()).AsSpan().SequenceEqual(Halves(many.ReadBrush())), "кисть различается");
        Assert.True(one.ReadFields().Composite.AsSpan().SequenceEqual(many.ReadFields().Composite), "C различается");
        _output.WriteLine($"{b2.Stats}");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Снимок с кистью: файлы полей и кисти; <c>SWORD_TEST_KEEP=1</c> оставляет их.</summary>
    [Fact]
    public void Snapshot_WritesBrushFiles()
    {
        var p = Fluid2DParams.Torch;
        var s = Create(p, false);
        var brush = new Fluid2DBrush(p, 1);
        for (int k = 0; k < 40; k++)
        {
            var input = StepInput((ulong)k);
            var position = new Vector3(0.5f * MathF.Sin(k * 0.1f), -0.3f + k * 0.01f, 0);
            brush.Begin(input, (ulong)k, Vector3.Zero);
            brush.Add(Key(1), position, 0.34f);
            brush.End();
            _gpu.Frame(f => s.Sim.Step(f, input, brush));
        }
        var snapshot = s.Sim.Snapshot();
        Assert.NotNull(snapshot.Brush);
        string dir = Path.Combine(Path.GetTempPath(), "sword-fluid2d-brush-" + Environment.ProcessId);
        try
        {
            snapshot.Save(Path.Combine(dir, "torch"));
            Assert.True(new FileInfo(Path.Combine(dir, "torch.brush.density.png")).Length > 100);
            Assert.True(new FileInfo(Path.Combine(dir, "torch.brush.velocity.png")).Length > 100);
            Assert.Equal(184L * 184 * 16, new FileInfo(Path.Combine(dir, "torch.brush.bin")).Length);
            Assert.True(snapshot.Brush!.Max(t => (float)t.Density) > 0.5f);
            _output.WriteLine($"снимок: {snapshot.Stats}; {brush.Stats}");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1" && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Кадр со ступенью кисти (Begin/Add/End, шаг с Painter, экспорт, чтение статистики) — 0 Б.</summary>
    [Fact]
    public void Frame_WithBrush_AllocatesNothing()
    {
        var p = Fluid2DParams.Torch;
        var s = Create(p, false);
        var brush = new Fluid2DBrush(p, 1);
        using var readback = _gpu.Renderer.CreateReadback(s.Sim.StatsStorage, Fluid2DStats.Bytes, (_, _) => { });
        ulong step = 0;
        Action<RenderFrame> build = f =>
        {
            var input = StepInput(step);
            brush.Begin(input, step, Vector3.Zero);
            for (uint i = 0; i < 16; i++) brush.Add(Key(1, (ushort)i), new Vector3(0.1f * i - 0.8f + 0.01f * step, 0.05f * i, 0), 0.34f);
            brush.End();
            s.Sim.Step(f, input, brush);
            s.Sim.Export(f);
            step++;
        };
        for (int i = 0; i < 10; i++) _gpu.Frame(build);
        const int frames = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _gpu.Frame(build);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр сима с кистью выделил {bytes} Б");
        Assert.Contains("fluid.painter", _gpu.Renderer.Graph.LastFrame.Select(r => r.Name));
        _gpu.AssertNoDebugMessages();
    }
}

/// <summary>GPU-время Painter 184² (факел) для 1 и 64 точек без слоя отладки и валидации: зона <c>gpu.fluid.painter</c>.</summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DPainterTimingGpuTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void Painter184_GpuTime(int points)
    {
        using var gpu = new GpuDevice("Sword.Engine.Fluid2D.Tests.PainterTiming", validation: false);
        var p = Fluid2DParams.Torch;
        var (dn, _, i1) = gpu.Noise("test.noise.density", 128, 31);
        var (an, _, i2) = gpu.Noise("test.noise.amp", 128, 32);
        var (bn, _, i3) = gpu.Image("test.noise.brush", 256, 33);
        using var sim = new Fluid2DSim(gpu.Renderer, "timing", p, dn, an, bn);
        var brush = new Fluid2DBrush(p, 1);
        var rng = new Random(1);
        var start = new Vector3[points];
        for (int i = 0; i < points; i++)
            start[i] = points == 1 ? Vector3.Zero : new Vector3(rng.NextSingle() * 0.6f - 0.3f, rng.NextSingle() * 0.8f - 0.4f, 0);
        ulong step = 0;
        var painter = new List<double>();
        var all = new List<double>();
        for (int frame = 0; frame < 240; frame++)
        {
            gpu.Frame(f =>
            {
                var input = new Fluid2DStepInput(step, default, default);
                brush.Begin(input, step, Vector3.Zero);
                for (int i = 0; i < points; i++)
                    brush.Add(PainterKit.Key(1, (ushort)i), start[i] + new Vector3(0.3f * MathF.Sin(step * 0.05f + i), 0.2f * MathF.Cos(step * 0.04f + i), 0), 0.34f);
                brush.End();
                sim.Step(f, input, brush);
                sim.Export(f);
                step++;
            });
            var times = gpu.Renderer.Gpu;
            if (frame < 60 || !times.Valid) continue;
            double sum = 0;
            for (int i = 0; i < times.Count; i++)
            {
                string name = times.NameOf(i);
                if (!name.StartsWith("fluid.", StringComparison.Ordinal)) continue;
                sum += times.MillisecondsOf(i);
                if (name == "fluid.painter") painter.Add(times.MillisecondsOf(i));
            }
            all.Add(sum);
        }
        painter.Sort();
        all.Sort();
        double median = painter[painter.Count / 2];
        output.WriteLine($"gpu.fluid.painter, {points} точек ({brush.Stats}): медиана {median:F4} мс; весь шаг со статистикой и экспортом " +
            $"{all[all.Count / 2]:F4} мс; {gpu.Renderer.AdapterName}");
        Assert.True(median < 0.5, $"Painter {median} мс");
        i1.Dispose();
        i2.Dispose();
        i3.Dispose();
    }
}
