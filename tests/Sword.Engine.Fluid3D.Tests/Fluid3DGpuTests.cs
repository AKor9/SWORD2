using System.Numerics;
using Sword.Engine.Render;
using Xunit.Abstractions;
using static Sword.Engine.Fluid3D.Tests.Fluid3DTestKit;

namespace Sword.Engine.Fluid3D.Tests;

/// <summary>
/// Газ на настоящем устройстве (слой отладки и GPU-валидация, любое предупреждение D3D12 — провал) против CPU-эталона
/// <see cref="Fluid3DReference"/>: каждая стадия на случайных полях 16³ и 32×24×20 (MacCormack с тёплым стартом и полулагранжева с холодным),
/// контроль с испорченным входом, сдвиг окна бит в бит, 10 000 шагов, детерминизм, статистика, срезы, 0 Б на кадр.
/// </summary>
/// <remarks>
/// Допуск стадии — 2 ULP half: GPU и CPU считают одни и те же выражения float по одним и тем же half-входам (строгий IEEE, без FMA);
/// расходятся только sqrt и деление (доли ULP float), которые изредка перекидывают округление в half на 1 ULP.
/// </remarks>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid3DGpuTests(ITestOutputHelper output) : IDisposable
{
    private const double StageUlps = 2;
    private readonly GpuDevice _gpu = new("Sword.Engine.Fluid3D.Tests");

    public void Dispose() => _gpu.Dispose();

    /// <summary>Шар, капсула с широким краем через край окна, узкая капсула с быстрым подтягиванием скорости.</summary>
    private static Fluid3DSource[] Sources(Fluid3DWindow w) =>
    [
        new(w.ToWorld(new Vector3(5.3f, 6.7f, 4.2f)), 2.5f * w.CellSize, 40, 70, new Vector3(0.5f, 1.5f, -0.7f), 20),
        new(w.ToWorld(new Vector3(-1.2f, 3.1f, 8.4f)), 3.2f * w.CellSize, 25, 10, new Vector3(-1, 0.2f, 0), 90)
        {
            From = w.ToWorld(new Vector3(4.6f, 2.2f, 11.9f)),
            Edge = 2.3f * w.CellSize,
        },
        new(w.ToWorld(new Vector3(10.4f, 12.3f, 7.7f)), 1.4f * w.CellSize, 60, 30, new Vector3(2, 0, 1), 200) { From = w.ToWorld(new Vector3(9.1f, 7.2f, 6.5f)) },
    ];

    private static IEnumerable<(string Name, Half[] Gpu, Half[] Cpu)> Buffers(Fluid3DFields g, Fluid3DFields c) =>
    [
        ("V", g.Velocity, c.Velocity), ("V1", g.VelocityScratch, c.VelocityScratch), ("S", g.Scalars, c.Scalars),
        ("S1", g.ScalarScratch, c.ScalarScratch), ("S2", g.ScalarForward, c.ScalarForward), ("Aux", g.Aux, c.Aux), ("AuxPrev", g.AuxPrevious, c.AuxPrevious),
    ];

    [Theory]
    [InlineData(16, 16, 16, false)]
    [InlineData(32, 24, 20, false)]
    [InlineData(16, 16, 16, true)]
    [InlineData(32, 24, 20, true)]
    public void EveryStage_MatchesReference_OnRandomFields(int nx, int ny, int nz, bool semiLagrangian)
    {
        // Вихревой шум с маской плотностью: случайные ρ в [0, 1] — и ячейки с частью силы, и с полной; ветер — в обратном следе адвекции.
        var p = Small(nx, ny, nz) with
        {
            CurlNoise = 4, CurlNoiseScale = 4 * Fluid3DParams.Smoke.CellSize, CurlNoiseRise = 0.7f, CurlNoiseDensity = 0.5f, Wind = new Vector3(0.9f, -0.3f, 0.5f),
        };
        if (semiLagrangian) p = p with { ScalarAdvection = Fluid3DAdvection.SemiLagrangian, PressureWarmStart = false, SorWeight = null };
        var w0 = WindowAt(p);
        var target = w0.Center + new Vector3(2, -1, 3) * p.CellSize;
        var sources = Sources(w0);
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        foreach (var stage in Enum.GetValues<Fluid3DStage>())
        {
            var fields = RandomFields(nx, ny, nz, 11 + (int)stage);
            sim.Load(fields, w0);
            var cpu = new Fluid3DReference(p, fields.Clone(), w0);
            _gpu.Frame(f => sim.AddStage(f, 77, target, sources, stage));
            var k = cpu.Constants(77, target, sources, out _);
            cpu.Stage(stage, k);
            var gpu = sim.ReadFields();
            double worst = 0;
            foreach (var (name, g, c) in Buffers(gpu, cpu.Fields))
            {
                var e = Compare(g, c);
                worst = Math.Max(worst, e.Ulps);
                Assert.True(e.Ulps <= StageUlps, $"{stage}, {name}: {e.Ulps} ULP half (элемент {e.Index}, {e.Abs:G4})");
            }
            output.WriteLine($"{nx}×{ny}×{nz} {(semiLagrangian ? "полулагранжева" : "MacCormack")}, {stage}: до {worst:F2} ULP half");
        }
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Контроль допуска: скорость на GPU с переставленными осями x и z — расхождение сил и адвекции много больше допуска.</summary>
    [Fact]
    public void AxisSwap_ExceedsTolerance()
    {
        var p = Small(16, 16, 16);
        var w0 = WindowAt(p);
        var fields = RandomFields(16, 16, 16, 5);
        var swapped = fields.Clone();
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        swapped.Velocity[4 * fields.Index(x, y, z) + c] = fields.Velocity[4 * fields.Index(z, y, x) + c];
                        swapped.VelocityScratch[4 * fields.Index(x, y, z) + c] = fields.VelocityScratch[4 * fields.Index(z, y, x) + c];
                    }
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        foreach (var stage in new[] { Fluid3DStage.Forces, Fluid3DStage.AdvectVelocity })
        {
            sim.Load(swapped, w0);
            var cpu = new Fluid3DReference(p, fields.Clone(), w0);
            _gpu.Frame(f => sim.AddStage(f, 1, w0.Center, [], stage));
            cpu.Stage(stage, cpu.Constants(1, w0.Center, [], out _));
            var e = Compare(sim.ReadFields().Velocity, cpu.Fields.Velocity);
            var e1 = Compare(sim.ReadFields().VelocityScratch, cpu.Fields.VelocityScratch);
            double ulps = Math.Max(e.Ulps, e1.Ulps);
            output.WriteLine($"{stage} на переставленной скорости: {ulps:G4} ULP half");
            Assert.True(ulps > 100 * StageUlps);
        }
    }

    /// <summary>
    /// Впрыск источников по траектории на GPU = эталон (стадия Sources, случайные поля): новый ключ (шар), взмах 10 м/с (капсула), капсула
    /// с широким краем и уносом — 2 ULP half. Контроль: GPU получает шары в концах вместо капсул — расхождение много больше допуска.
    /// </summary>
    [Fact]
    public void EmitterInjection_MatchesReference_AndSpheresInsteadOfCapsulesFail()
    {
        var p = Small(32, 24, 20);
        var w0 = WindowAt(p);
        var torch = Fluid3DEmitterParams.Torch with { Radius = 2 * p.CellSize };
        var wide = torch with { Edge = 3 * p.CellSize, VelocityScale = 1, VelocityRate = 200 };
        var emitters = new Fluid3DEmitters(5);
        for (ulong s = 0; s < 2; s++)
        {
            emitters.Begin(s, p.StepSeconds);
            emitters.Add(3, w0.ToWorld(new Vector3(6, 8, 10)) + new Vector3(10 * p.StepSeconds * s, 0, 0), torch);
            emitters.Add(9, w0.ToWorld(new Vector3(20, 14, 6)) + new Vector3(0, 0.1f, -0.08f) * s, wide);
            if (s == 1) emitters.Add(1, w0.ToWorld(new Vector3(12, 4, 14)), torch);
            emitters.End();
        }
        Assert.Equal(new Fluid3DEmitterStats(1, 3, 3, 0, 2), emitters.Stats);
        var sources = emitters.Sources.ToArray();
        var spheres = sources.Select(s => s with { From = null }).ToArray();
        var fields = RandomFields(p.Nx, p.Ny, p.Nz, 21);
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        double Ulps(Fluid3DSource[] gpuSources)
        {
            sim.Load(fields, w0);
            var cpu = new Fluid3DReference(p, fields.Clone(), w0);
            _gpu.Frame(f => sim.AddStage(f, 1, w0.Center, gpuSources, Fluid3DStage.Sources));
            cpu.Stage(Fluid3DStage.Sources, cpu.Constants(1, w0.Center, sources, out _));
            var gpu = sim.ReadFields();
            return Math.Max(Compare(gpu.ScalarScratch, cpu.Fields.ScalarScratch).Ulps, Compare(gpu.VelocityScratch, cpu.Fields.VelocityScratch).Ulps);
        }
        double same = Ulps(sources), control = Ulps(spheres);
        output.WriteLine($"впрыск капсул GPU против эталона: {same:F2} ULP half; шары вместо капсул: {control:G4}");
        Assert.True(same <= StageUlps, $"{same} ULP");
        Assert.True(control > 100 * StageUlps, $"контроль {control} ULP");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Сдвиг окна шагом целиком (тихий газ в покое): содержимое в мире — бит в бит, пришедшее из-за края — пусто; туда и обратно.</summary>
    [Fact]
    public void WindowShift_IsBitExactOnGpu()
    {
        var p = Quiet(16, 20, 12);
        var start = RandomFields(p.Nx, p.Ny, p.Nz, 3);
        Array.Clear(start.Velocity);
        Array.Clear(start.Aux);
        var w0 = WindowAt(p);
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        sim.Load(start, w0);
        var cpu = new Fluid3DReference(p, start.Clone(), w0);
        var shift = (X: -4, Y: 3, Z: 2);
        var there = w0.Center + new Vector3(shift.X, shift.Y, shift.Z) * p.CellSize;
        _gpu.Frame(f => sim.Step(f, 1, there, []));
        cpu.Step(1, there, []);
        var gpu = sim.ReadFields();
        Fluid3DReferenceTests.AssertShifted(start.Scalars, gpu.Scalars, p, shift);
        Assert.True(SameBits(gpu.Velocity, cpu.Fields.Velocity));
        Assert.Equal(w0.X + shift.X, sim.Window!.Value.X);
        _gpu.Frame(f => sim.Step(f, 2, w0.Center, []));
        cpu.Step(2, w0.Center, []);
        Assert.True(SameBits(sim.ReadFields().Scalars, cpu.Fields.Scalars));
        Assert.Equal(w0.Z, sim.Window!.Value.Z);
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>
    /// 10 000 шагов с источником, окно ходит кругом: нет Inf/NaN, |v| ≤ предела (статистика асинхронно каждый кадр); два сима с одними
    /// входами — бит в бит; статистика GPU = подсчёт по полю.
    /// </summary>
    [Fact]
    public void TenThousandSteps_FiniteBoundedAndDeterministic()
    {
        var p = Fluid3DParams.Smoke with { Nx = 24, Ny = 32, Nz = 24, CurlNoise = 3, CurlNoiseScale = 0.15f, CurlNoiseRise = 0.3f, CurlNoiseDensity = 0.05f };
        using var a = new Fluid3DSim(_gpu.Renderer, "a", p);
        using var b = new Fluid3DSim(_gpu.Renderer, "b", p);
        var seen = new List<Fluid3DStats>();
        using var readback = _gpu.Renderer.CreateReadback(a.StatsStorage, Fluid3DStats.Bytes, (data, _) => seen.Add(Fluid3DStats.Parse(data)));
        const int steps = 10_000, perFrame = 50;
        ulong step = 0;
        var sources = new Fluid3DSource[1];
        for (int frame = 0; frame < steps / perFrame; frame++)
            _gpu.Frame(f =>
            {
                for (int i = 0; i < perFrame; i++, step++)
                {
                    float t = step / 60f;
                    var target = new Vector3(MathF.Cos(t) * 0.8f, 0.3f, MathF.Sin(t) * 0.8f);
                    sources[0] = new Fluid3DSource(target - new Vector3(0, 0.25f, 0), 0.06f, 30, 50, new Vector3(-MathF.Sin(t), 0.5f, MathF.Cos(t)) * 0.8f, 20);
                    a.Step(f, step, target, sources);
                    b.Step(f, step, target, sources);
                }
            });
        var fa = a.ReadFields();
        var fb = b.ReadFields();
        Assert.True(SameBits(fa.Velocity, fb.Velocity) && SameBits(fa.Scalars, fb.Scalars) && SameBits(fa.Aux, fb.Aux), "два прогона разошлись");
        var gpuStats = a.ReadStats();
        var cpuStats = Fluid3DStats.Compute(fa, a.Window!.Value, step - 1);
        output.WriteLine($"10 000 шагов: {gpuStats}");
        output.WriteLine($"кадров со статистикой {seen.Count}, max|v| за прогон {seen.Max(s => s.MaxSpeed):F3} м/с");
        Assert.Equal(step - 1, gpuStats.Step);
        Assert.All(seen, s => Assert.Equal(0, s.NonFinite));
        Assert.All(seen, s => Assert.True(s.MaxSpeed <= p.MaxSpeed * 1.001f, $"шаг {s.Step}: |v| {s.MaxSpeed}"));
        Assert.True(gpuStats.DensitySum > 1, "дым есть");
        Assert.Equal(cpuStats.DensitySum, gpuStats.DensitySum, cpuStats.DensitySum * 1e-4f);
        Assert.Equal(cpuStats.TemperatureSum, gpuStats.TemperatureSum, cpuStats.TemperatureSum * 1e-4f);
        Assert.Equal(cpuStats.MaxSpeed, gpuStats.MaxSpeed, 1e-5f);
        Assert.Equal(cpuStats.DivergenceBefore, gpuStats.DivergenceBefore, cpuStats.DivergenceBefore * 1e-4f);
        Assert.Equal(cpuStats.DivergenceAfter, gpuStats.DivergenceAfter, cpuStats.DivergenceAfter * 1e-3f);
        Assert.Equal(cpuStats.Enstrophy, gpuStats.Enstrophy, cpuStats.Enstrophy * 1e-3f);
        Assert.True(Vector3.Distance(cpuStats.Centroid, gpuStats.Centroid) < 1e-4f);
        Assert.Equal(a.Window!.Value.X, gpuStats.Window.X);

        a.Reset();
        var small = new[] { new Fluid3DSource(new Vector3(0, -0.2f, 0), 0.06f, 30, 50) };
        void Run(Fluid3DSim sim)
        {
            ulong n = 0;
            for (int frame = 0; frame < 4; frame++)
                _gpu.Frame(f =>
                {
                    for (int i = 0; i < perFrame; i++) sim.Step(f, n++, new Vector3(0.1f, 0, 0), small);
                });
        }
        Run(a);
        Assert.False(SameBits(a.ReadFields().Scalars, b.ReadFields().Scalars));
        b.Reset();
        Run(b);
        var ra = a.ReadFields();
        var rb = b.ReadFields();
        Assert.True(SameBits(ra.Velocity, rb.Velocity) && SameBits(ra.Aux, rb.Aux), "после сброса прогоны разошлись");
        Assert.True(SameBits(a.ReadFields().Scalars, b.ReadFields().Scalars), "после сброса прогоны разошлись");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Тёплый дым от источника поднимается: центроид ρ в мире (статистика GPU) выше источника; срезы пишутся.</summary>
    [Fact]
    public void WarmSmoke_Rises_AndSlicesAreWritten()
    {
        var p = Fluid3DParams.Smoke with { Nx = 32, Ny = 48, Nz = 32 };
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        var target = new Vector3(10.2f, 1.4f, -3.3f);
        var source = new Fluid3DSource(target - new Vector3(0, 0.4f, 0), 0.08f, 3, 5);
        ulong step = 0;
        for (int frame = 0; frame < 3; frame++)
            _gpu.Frame(f =>
            {
                for (int i = 0; i < 20; i++) sim.Step(f, step++, target, [source]);
            });
        var s = sim.ReadStats();
        output.WriteLine($"через 60 шагов: {s}; центроид над источником на {(s.Centroid.Y - source.Center.Y) * 100:F1} см");
        Assert.True(s.Centroid.Y > source.Center.Y + 0.1f);
        Assert.True(MathF.Abs(s.Centroid.X - source.Center.X) < 0.05f && MathF.Abs(s.Centroid.Z - source.Center.Z) < 0.05f);
        Assert.True(MathF.Abs(sim.Window!.Value.Offset.X) <= p.CellSize * 0.5f + 1e-5f);

        var snapshot = sim.Snapshot();
        Assert.Equal(s.DensitySum, snapshot.Stats.DensitySum, s.DensitySum * 1e-4f);
        string dir = Path.Combine(Path.GetTempPath(), "sword-fluid3d-" + Environment.ProcessId);
        try
        {
            var files = snapshot.SaveSlices(Path.Combine(dir, "smoke"), source.Center);
            Assert.Equal(8, files.Count);
            Assert.All(files, f => Assert.True(new FileInfo(f).Length > 100, f));
            Assert.Equal(32L * 48 * 6 * 2, new FileInfo(files.Single(f => f.EndsWith(".xy.bin", StringComparison.Ordinal))).Length);
            Assert.Equal(32L * 32 * 6 * 2, new FileInfo(files.Single(f => f.EndsWith(".xz.bin", StringComparison.Ordinal))).Length);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1" && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        _gpu.AssertNoDebugMessages();
    }

    [Fact]
    public void Stats_ArriveAsync_WithTheirStep()
    {
        var p = Fluid3DParams.Smoke with { Nx = 16, Ny = 16, Nz = 16 };
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        var got = new List<Fluid3DStats>();
        using var readback = _gpu.Renderer.CreateReadback(sim.StatsStorage, Fluid3DStats.Bytes, (data, _) => got.Add(Fluid3DStats.Parse(data)));
        for (int frame = 0; frame < 12; frame++)
            _gpu.Frame(f => sim.Step(f, (ulong)(1000 + frame), Vector3.Zero, [new Fluid3DSource(Vector3.Zero, 0.05f, 10, 10)]));
        Assert.Equal(12 - GpuReadback.Latency, got.Count);
        for (int i = 0; i < got.Count; i++) Assert.Equal((ulong)(1000 + i), got[i].Step);
        Assert.True(got[^1].DensitySum > got[0].DensitySum);
    }

    /// <summary>Кадр с источниками по траектории, шагом и чтением статистики — 0 Б; проходы графа названы по стадиям, все — фазы Simulation.</summary>
    [Fact]
    public void Frame_WithStepAndReadback_AllocatesNothing()
    {
        var p = Fluid3DParams.Smoke with { Nx = 32, Ny = 32, Nz = 32 };
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        using var readback = _gpu.Renderer.CreateReadback(sim.StatsStorage, Fluid3DStats.Bytes, (_, _) => { });
        var emitters = new Fluid3DEmitters(7);
        var torch = Fluid3DEmitterParams.Torch;
        ulong step = 0;
        Action<RenderFrame> build = f =>
        {
            float t = step * p.StepSeconds;
            emitters.Begin(step, p.StepSeconds);
            emitters.Add(2, new Vector3(0.3f * MathF.Cos(8 * t), -0.3f, 0.3f * MathF.Sin(8 * t)), torch);
            if (step % 3 != 0) emitters.Add(1, new Vector3(0, -0.2f, 0.01f * step), torch, 0.5f);
            emitters.End();
            sim.Step(f, new Vector3(0.01f * step, 0, 0), emitters);
            step++;
        };
        for (int i = 0; i < 10; i++) _gpu.Frame(build);
        const int frames = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _gpu.Frame(build);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр газа выделил {bytes} Б");
        var names = _gpu.Renderer.Graph.LastFrame.Select(r => r.Name).ToArray();
        string[] pressure = [.. Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? "gas.pressure.red" : "gas.pressure.black")];
        Assert.Equal(["gas.sources", "gas.curl", "gas.forces", "gas.advect.velocity", "gas.divergence", .. pressure, "gas.project", "gas.advect.forward",
            "gas.advect.scalars", "gas.stats.slices", "gas.stats"], names);
        Assert.All(_gpu.Renderer.Graph.LastFrame, r => Assert.Equal(FramePhase.Simulation, r.Phase));
        _gpu.AssertNoDebugMessages();
    }
    /// <summary>Плоскость наискось через окно 32×24×20: карта 12² по 1.3 ячейки, слой ±1.5 ячейки с краем 2 ячейки; карта — с клетки 7 буфера.</summary>
    /// <summary>Буфер карты короче смещения + Side², с отрицательным смещением или не того шага — исключение до регистрации проходов.</summary>
    [Fact]
    public void PlaneSource_RejectsShortOrWrongMapBuffer()
    {
        var p = Small(32, 24, 20);
        var w0 = WindowAt(p);
        var plane = Plane(w0);
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        using var exact = _gpu.Renderer.CreateStorage<Fluid3DMapTexel>("test.exact", (uint)(3 + plane.Texels));
        using var wide = _gpu.Renderer.CreateStorage<Matrix4x4>("test.wide", (uint)(3 + plane.Texels));
        void Bad(GpuStorage map, int offset) => _gpu.Frame(f =>
        {
            var error = Assert.Throws<ArgumentException>(() => sim.AddStage(f, 1, w0.Center, [], Fluid3DStage.Sources, plane, map, offset));
            output.WriteLine(error.Message);
        });
        Bad(exact, 4);
        Bad(exact, -1);
        Bad(wide, 3);
        sim.Load(RandomFields(p.Nx, p.Ny, p.Nz, 5), w0);
        _gpu.Frame(f => sim.AddStage(f, 1, w0.Center, [], Fluid3DStage.Sources, plane, exact, 3));
        _gpu.AssertNoDebugMessages();
    }

    private static Fluid3DPlane Plane(Fluid3DWindow w, bool swapped = false)
    {
        var ax = Vector3.Normalize(new Vector3(0.8f, 0.1f, 0.6f));
        var ay = Vector3.Normalize(Vector3.Cross(new Vector3(0.2f, 0.3f, -0.9f), ax));
        return new Fluid3DPlane(w.ToWorld(new Vector3(15.3f, 11.6f, 9.2f)), swapped ? ay : ax, swapped ? ax : ay, 12, 1.3f * w.CellSize,
            1.5f * w.CellSize, 2 * w.CellSize);
    }

    private static Fluid3DMapTexel[] RandomMap(int offset, int side, int seed)
    {
        var rng = new Random(seed);
        var map = new Fluid3DMapTexel[offset + side * side];
        for (int i = offset; i < map.Length; i++)
            map[i] = Fluid3DMapTexel.Pack((float)rng.NextDouble() * 0.5f, (float)rng.NextDouble(), (float)rng.NextDouble(),
                new Vector3((float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2));
        return map;
    }

    /// <summary>
    /// Источник-карта на GPU = эталон (стадия Sources, случайные поля и карта, шар-источник рядом): 2 ULP half; нулевая карта — выход как без
    /// карты (бит в бит). Контроль: GPU получает плоскость с переставленными осями карты — расхождение много больше допуска.
    /// </summary>
    [Fact]
    public void PlaneSource_MatchesReference_ZeroMapIsNoSource_SwappedAxesFail()
    {
        var p = Small(32, 24, 20);
        var w0 = WindowAt(p);
        var plane = Plane(w0);
        const int offset = 7;
        var map = RandomMap(offset, plane.Side, 31);
        var zero = new Fluid3DMapTexel[map.Length];
        Fluid3DSource[] sources = [new(w0.ToWorld(new Vector3(14, 10, 8)), 2 * p.CellSize, 20, 40, new Vector3(0, 0.5f, 0), 30)];
        var fields = RandomFields(p.Nx, p.Ny, p.Nz, 41);
        using var sim = new Fluid3DSim(_gpu.Renderer, "test", p);
        using var gpuMap = _gpu.Renderer.CreateStorage<Fluid3DMapTexel>("test.map", map);
        using var gpuZero = _gpu.Renderer.CreateStorage<Fluid3DMapTexel>("test.zero", zero);
        Fluid3DFields Gpu(Fluid3DPlane q, GpuStorage m)
        {
            sim.Load(fields, w0);
            _gpu.Frame(f => sim.AddStage(f, 1, w0.Center, sources, Fluid3DStage.Sources, q, m, offset));
            return sim.ReadFields();
        }
        Fluid3DFields Cpu(Fluid3DPlane? q, Fluid3DMapTexel[]? m)
        {
            var cpu = new Fluid3DReference(p, fields.Clone(), w0) { Map = m };
            cpu.Stage(Fluid3DStage.Sources, cpu.Constants(1, w0.Center, sources, out _, q, offset));
            return cpu.Fields;
        }
        double Ulps(Fluid3DFields g, Fluid3DFields c) =>
            Math.Max(Compare(g.ScalarScratch, c.ScalarScratch).Ulps, Compare(g.VelocityScratch, c.VelocityScratch).Ulps);
        var reference = Cpu(plane, map);
        double same = Ulps(Gpu(plane, gpuMap), reference), control = Ulps(Gpu(Plane(w0, swapped: true), gpuMap), reference);
        var none = Cpu(null, null);
        var zeroGpu = Gpu(plane, gpuZero);
        sim.Load(fields, w0);
        _gpu.Frame(f => sim.AddStage(f, 1, w0.Center, sources, Fluid3DStage.Sources));
        var noneGpu = sim.ReadFields();
        int touched = 0;
        for (int i = 0; i < none.ScalarScratch.Length; i += 2)
            if (none.ScalarScratch[i] != reference.ScalarScratch[i]) touched++;
        output.WriteLine($"источник-карта GPU против эталона: {same:F2} ULP half, ячеек с впрыском {touched}; оси карты переставлены: {control:G4}");
        Assert.True(touched > 200, $"ячеек с впрыском {touched}");
        Assert.True(same <= StageUlps, $"{same} ULP");
        Assert.True(control > 100 * StageUlps, $"контроль {control} ULP");
        Assert.Equal(noneGpu.ScalarScratch, zeroGpu.ScalarScratch);
        Assert.Equal(noneGpu.VelocityScratch, zeroGpu.VelocityScratch);
        _gpu.AssertNoDebugMessages();
    }
}

/// <summary>GPU-время шага без слоя отладки и валидации (она инструментирует шейдеры): зоны проходов <c>gas.*</c> на нескольких сетках.</summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid3DTimingGpuTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(64, 96, 64, Fluid3DAdvection.MacCormack)]
    [InlineData(80, 112, 80, Fluid3DAdvection.MacCormack)]
    [InlineData(96, 128, 96, Fluid3DAdvection.MacCormack)]
    [InlineData(96, 128, 96, Fluid3DAdvection.SemiLagrangian)]
    [InlineData(96, 128, 96, Fluid3DAdvection.MacCormack, 0.02f)]
    [InlineData(96, 128, 96, Fluid3DAdvection.MacCormack, 0f)]
    public void Step_GpuTime(int nx, int ny, int nz, Fluid3DAdvection advection, float noiseDensity = -1)
    {
        using var gpu = new GpuDevice("Sword.Engine.Fluid3D.Tests.Timing", validation: false);
        // noiseDensity ≥ 0 — с вихревым шумом (маска этой плотностью; 0 — шум во всех ячейках, худший случай).
        var p = Fluid3DParams.Smoke with { Nx = nx, Ny = ny, Nz = nz, ScalarAdvection = advection };
        if (noiseDensity >= 0) p = p with { CurlNoise = 2, CurlNoiseScale = 0.2f, CurlNoiseRise = 0.3f, CurlNoiseDensity = noiseDensity };
        using var sim = new Fluid3DSim(gpu.Renderer, "timing", p);
        // Источник взмахом: 12 м/с по кругу радиусом 0.4 м — капсула ≈20 см за шаг.
        var emitters = new Fluid3DEmitters(1);
        ulong step = 0;
        var passes = new Dictionary<string, List<double>>();
        var frameSums = new Dictionary<string, double>();
        for (int frame = 0; frame < 240; frame++)
        {
            gpu.Frame(f =>
            {
                float t = step * p.StepSeconds;
                emitters.Begin(step, p.StepSeconds);
                emitters.Add(1, new Vector3(0.4f * MathF.Cos(30 * t), -0.8f, 0.4f * MathF.Sin(30 * t)), Fluid3DEmitterParams.Torch);
                emitters.End();
                sim.Step(f, new Vector3(0.005f * step, 0, 0), emitters);
                step++;
            });
            var times = gpu.Renderer.Gpu;
            if (frame < 60 || !times.Valid) continue;
            frameSums.Clear();
            for (int i = 0; i < times.Count; i++)
            {
                string name = times.NameOf(i);
                if (!name.StartsWith("gas.", StringComparison.Ordinal)) continue;
                frameSums[name] = frameSums.GetValueOrDefault(name) + times.MillisecondsOf(i);
            }
            foreach (var (name, ms) in frameSums)
            {
                if (!passes.TryGetValue(name, out var list)) passes[name] = list = [];
                list.Add(ms);
            }
        }
        double total = 0, stepOnly = 0;
        foreach (var (name, list) in passes.OrderBy(kv => kv.Key))
        {
            list.Sort();
            double median = list[list.Count / 2];
            total += median;
            if (!name.StartsWith("gas.stats", StringComparison.Ordinal)) stepOnly += median;
            output.WriteLine($"gpu.{name}: медиана {median:F4} мс ({list.Count} кадров)");
        }
        output.WriteLine($"{nx}×{ny}×{nz} ({p.Cells / 1e6:F2} М ячеек, {advection}, шум {(noiseDensity >= 0 ? $"маска {noiseDensity}" : "выкл.")}): шаг {stepOnly:F3} мс, со статистикой {total:F3} мс; {gpu.Renderer.AdapterName}");
        Assert.True(total < 5.0, $"шаг {nx}×{ny}×{nz} — {total} мс GPU");
    }
}
