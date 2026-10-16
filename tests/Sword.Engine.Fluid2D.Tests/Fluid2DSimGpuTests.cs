using System.Numerics;
using Sword.Engine.Render;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.Fluid2DTestKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Свойства сима на устройстве: шум скорости = эталон, ноль остаётся нулём, 10 000 шагов с сильным впрыском конечны и в пределах, статистика
/// GPU = подсчёт по полю, два прогона — бит в бит (и после сброса), статистика асинхронно, снимок, 0 Б на кадр, GPU-время шага.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DSimGpuTests(ITestOutputHelper output) : IDisposable
{
    private readonly GpuDevice _gpu = new("Sword.Engine.Fluid2D.Tests.Sim");

    public void Dispose() => _gpu.Dispose();

    /// <summary>Сильная кисть: диск плотности 1 со скоростью (0.1, −0.08) — |bv| = 3.8, ввод кисти глушит ветер.</summary>
    private static Fluid2DBrushTexel[] StrongBrush(int n)
    {
        var b = new Fluid2DBrushTexel[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.6f;
                if (u * u + v * v < 0.01f) b[y * n + x] = new Fluid2DBrushTexel(0.1f, -0.08f, 1);
            }
        return b;
    }

    private (Fluid2DSim Sim, Fluid2DNoiseImage Density, Fluid2DNoiseImage Amp, GpuImage[] Images) Create(Fluid2DParams p, string name = "test")
    {
        var (dn, dnCpu, i1) = _gpu.Noise("test.noise.density", 128, 21);
        var (an, anCpu, i2) = _gpu.Noise("test.noise.amp", 128, 22);
        return (new Fluid2DSim(_gpu.Renderer, name, p, dn, an), dnCpu, anCpu, [i1, i2]);
    }

    private static Fluid2DStepInput At(ulong step) => new(step, new Vector2(0.001f * step, -0.0005f * step), new Vector2(0.002f, -0.001f),
        Quaternion.CreateFromYawPitchRoll(0.3f, -0.9f, 0));

    /// <summary>
    /// Шумы из пака (<c>textures/fire/sim_noise.swtex</c>: слой T_Noise3 — плотность, T_NoiseTemplate1 — amp-шум, sRGB): Composite факела 368²
    /// с кистью = эталон на шумах, прочитанных с GPU.
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void PackNoise_TorchComposite_MatchesReference()
    {
        using var texture = _gpu.Renderer.LoadTexture("textures/fire/sim_noise.swtex");
        Assert.True(texture.Srgb, "шумы сима — sRGB (FSR §3.11)");
        var density = Fluid2DNoise.FromLayer(texture, "T_Noise3");
        var amp = Fluid2DNoise.FromLayer(texture, "T_NoiseTemplate1");
        Assert.NotEqual(density.Layer, amp.Layer);
        var p = Torch(368);
        using var sim = new Fluid2DSim(_gpu.Renderer, "pack", p, density, amp);
        var fields = RandomFields(368, 5);
        var brush = RandomBrush(p.BrushResolution, 6);
        using var brushStorage = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", brush);
        sim.Load(fields);
        _gpu.Frame(f => sim.AddStage(f, Input, brushStorage, Fluid2DStage.Composite));
        var cpu = new Fluid2DReference(p, _gpu.ReadNoise(density), _gpu.ReadNoise(amp), fields.Clone());
        cpu.Stage(Fluid2DStage.Composite, Input, brush);
        var e = Compare(sim.ReadFields().Composite, cpu.Fields.Composite);
        output.WriteLine($"шумы пака {texture}: {e.Ulps:F2} ULP half");
        Assert.True(e.Ulps <= 2, $"{e.Ulps} ULP (элемент {e.Index})");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Направленный шум на пустом поле: C.v = 0.005·VeloDirNoise·симплекс — GPU = эталон (симплекс, хеш, заворот).</summary>
    [Fact]
    public void DirNoise_OnEmptyField_MatchesReference()
    {
        var p = Quiet(128) with { VeloDirNoise = 10 };
        var (sim, dn, an, images) = Create(p);
        using var _ = sim;
        var input = new Fluid2DStepInput(98765, new Vector2(123.4f, -56.7f), Vector2.Zero);
        _gpu.Frame(f => sim.Step(f, input));
        var cpu = new Fluid2DReference(p, dn, an);
        cpu.Step(input);
        var e = Compare(sim.ReadFields().Composite, cpu.Fields.Composite);
        output.WriteLine($"шум скорости: {e.Ulps:F2} ULP half, {e.Abs:G3}; max|v| {Fluid2DStats.Compute(cpu.Fields, 0).MaxSpeed}");
        Assert.True(e.Ulps <= 2, $"{e.Ulps} ULP (элемент {e.Index})");
        Assert.True(Fluid2DStats.Compute(cpu.Fields, 0).MaxSpeed > 0.03f);
        foreach (var i in images) i.Dispose();
        _gpu.AssertNoDebugMessages();
    }

    [Fact]
    public void ZeroField_StaysZero_OnGpu()
    {
        var (sim, _, _, images) = Create(Quiet(128));
        using var _s = sim;
        for (int frame = 0; frame < 5; frame++) _gpu.Frame(f =>
        {
            for (int k = 0; k < 4; k++) sim.Step(f, At((ulong)(frame * 4 + k)));
        });
        var fields = sim.ReadFields();
        foreach (var g in new[] { fields.Composite, fields.Advected, fields.Pressure, fields.Scratch })
            Assert.All(g, h => Assert.Equal(0f, (float)h));
        Assert.Equal(20ul, sim.Steps);
        foreach (var i in images) i.Dispose();
    }

    /// <summary>
    /// 10 000 шагов факела 368² с сильной кистью (50 шагов за кадр): ни одного Inf/NaN, |vx|, |vy| ≤ VelocityClamp, ρ ∈ [0, 1]; статистика
    /// GPU-редукцией = подсчёт по прочитанному полю.
    /// </summary>
    [Fact]
    public void LongRun_StrongInjection_StaysFiniteAndBounded_StatsMatchField()
    {
        var p = Torch(368);
        var (sim, _, _, images) = Create(p);
        using var _s = sim;
        using var brush = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", StrongBrush(p.BrushResolution));
        const int perFrame = 50, frames = 200;
        for (int frame = 0; frame < frames; frame++) _gpu.Frame(f =>
        {
            for (int k = 0; k < perFrame; k++) sim.Step(f, At((ulong)(frame * perFrame + k)), brush);
        });
        var fields = sim.ReadFields();
        var gpu = sim.ReadStats();
        var cpu = Fluid2DStats.Compute(fields, gpu.Step);
        output.WriteLine($"GPU: {gpu}");
        output.WriteLine($"CPU: {cpu}");
        Assert.Equal((ulong)(perFrame * frames - 1), gpu.Step);
        Assert.Equal(368, gpu.Resolution);
        Assert.Equal(0, gpu.NonFinite);
        Assert.Equal(0, cpu.NonFinite);
        var c = fields.Composite;
        for (int i = 0; i < c.Length; i += 4)
        {
            Assert.InRange((float)c[i], -p.VelocityClamp, p.VelocityClamp);
            Assert.InRange((float)c[i + 1], -p.VelocityClamp, p.VelocityClamp);
            Assert.InRange((float)c[i + 2], 0f, 1f);
        }
        Assert.True(cpu.DensitySum > 100, $"впрыск не дал плотности: {cpu.DensitySum}");
        Assert.Equal(cpu.DensitySum, gpu.DensitySum, cpu.DensitySum * 1e-5f);
        Assert.Equal(cpu.WetSum, gpu.WetSum, cpu.WetSum * 1e-5f);
        Assert.Equal(cpu.MaxSpeed, gpu.MaxSpeed, 1e-6f);
        Assert.Equal(cpu.MaxDensity, gpu.MaxDensity);
        Assert.True((cpu.Centroid - gpu.Centroid).Length() < 1e-5f, $"центроид {gpu.Centroid} против {cpu.Centroid}");
        foreach (var i in images) i.Dispose();
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Два сима с одними входами — поля и статистика бит в бит (атомиков нет); сброс и повтор — те же биты.</summary>
    [Fact]
    public void TwoRuns_AreBitIdentical_AndResetRepeats()
    {
        var p = Torch(368);
        var (a, _, _, ia) = Create(p, "a");
        var (b, _, _, ib) = Create(p, "b");
        using var _a = a;
        using var _b = b;
        using var brush = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", StrongBrush(p.BrushResolution));
        void Run(Fluid2DSim sim, int steps)
        {
            for (int s = 0; s < steps; s += 3)
            {
                int first = s;
                _gpu.Frame(f =>
                {
                    for (int k = first; k < Math.Min(first + 3, steps); k++) sim.Step(f, At((ulong)k), brush);
                });
            }
        }
        Run(a, 300);
        Run(b, 300);
        var fa = a.ReadFields();
        var fb = b.ReadFields();
        AssertBits(fa, fb);
        Assert.Equal(a.ReadStats(), b.ReadStats());

        b.Reset();
        Run(b, 300);
        AssertBits(fa, b.ReadFields());
        foreach (var i in ia.Concat(ib)) i.Dispose();
        _gpu.AssertNoDebugMessages();
    }

    private static void AssertBits(Fluid2DFields x, Fluid2DFields y)
    {
        Assert.True(x.Composite.AsSpan().SequenceEqual(y.Composite), "C различается");
        Assert.True(x.Advected.AsSpan().SequenceEqual(y.Advected), "A различается");
        Assert.True(x.Pressure.AsSpan().SequenceEqual(y.Pressure), "P различается");
        Assert.True(x.Scratch.AsSpan().SequenceEqual(y.Scratch), "S различается");
    }

    /// <summary>Статистика асинхронно (<c>Renderer.CreateReadback</c>): данные кадра N — в кадре N + 3, шаг кадра, по порядку.</summary>
    [Fact]
    public void Stats_ArriveAsync_WithTheirStep()
    {
        var p = Torch(128);
        var (sim, _, _, images) = Create(p);
        using var _s = sim;
        using var brush = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", StrongBrush(p.BrushResolution));
        var got = new List<Fluid2DStats>();
        using var readback = _gpu.Renderer.CreateReadback(sim.StatsStorage, Fluid2DStats.Bytes, (data, _) => got.Add(Fluid2DStats.Parse(data)));
        for (int frame = 0; frame < 12; frame++) _gpu.Frame(f => sim.Step(f, At((ulong)(1000 + frame)), brush));
        Assert.Equal(12 - GpuReadback.Latency, got.Count);
        for (int i = 0; i < got.Count; i++) Assert.Equal((ulong)(1000 + i), got[i].Step);
        Assert.True(got[^1].DensitySum > got[0].DensitySum);
        foreach (var i in images) i.Dispose();
    }

    [Fact]
    public void Snapshot_WritesDensityVelocityAndRaw()
    {
        var p = Torch(128);
        var (sim, _, _, images) = Create(p);
        using var _s = sim;
        using var brush = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", StrongBrush(p.BrushResolution));
        for (int frame = 0; frame < 20; frame++) _gpu.Frame(f => sim.Step(f, At((ulong)frame), brush));
        var snapshot = sim.Snapshot();
        string dir = Path.Combine(Path.GetTempPath(), "sword-fluid2d-" + Environment.ProcessId);
        try
        {
            var (density, velocity, raw) = snapshot.Save(Path.Combine(dir, "torch"));
            Assert.True(new FileInfo(density).Length > 100);
            Assert.True(new FileInfo(velocity).Length > 100);
            Assert.Equal(128L * 128 * 8, new FileInfo(raw).Length);
            Assert.Equal(19ul, snapshot.Stats.Step);
            Assert.Equal(sim.ReadStats().DensitySum, snapshot.Stats.DensitySum, snapshot.Stats.DensitySum * 1e-5f);
            output.WriteLine($"снимок: {snapshot.Stats}");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1" && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        foreach (var i in images) i.Dispose();
    }

    /// <summary>Кадр с шагом, экспортом и чтением статистики — 0 Б; проходы графа названы по стадиям.</summary>
    [Fact]
    public void Frame_WithStepExportAndReadback_AllocatesNothing()
    {
        var p = Torch(368);
        var (sim, _, _, images) = Create(p);
        using var _s = sim;
        using var brush = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", StrongBrush(p.BrushResolution));
        using var readback = _gpu.Renderer.CreateReadback(sim.StatsStorage, Fluid2DStats.Bytes, (_, _) => { });
        ulong step = 0;
        Action<RenderFrame> build = f =>
        {
            sim.Step(f, At(step++), brush);
            sim.Export(f);
        };
        for (int i = 0; i < 10; i++) _gpu.Frame(build);
        const int frames = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _gpu.Frame(build);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр сима выделил {bytes} Б");
        var names = _gpu.Renderer.Graph.LastFrame.Select(r => r.Name).ToArray();
        Assert.Equal(["fluid.composite", "fluid.advection", "fluid.divergence", "fluid.pressure.x", "fluid.pressure.y", "fluid.stats.rows",
            "fluid.stats", "fluid.export"], names);
        Assert.All(_gpu.Renderer.Graph.LastFrame, r => Assert.Equal(FramePhase.Simulation, r.Phase));
        foreach (var i in images) i.Dispose();
        _gpu.AssertNoDebugMessages();
    }
}

/// <summary>GPU-время шага факела 368² без слоя отладки и валидации (она инструментирует шейдеры): зоны проходов <c>gpu.fluid.*</c>.</summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DTimingGpuTests(ITestOutputHelper output)
{
    [Fact]
    public void Step368_GpuTime()
    {
        using var gpu = new GpuDevice("Sword.Engine.Fluid2D.Tests.Timing", validation: false);
        var p = Fluid2DParams.Torch;
        var (dn, _, i1) = gpu.Noise("test.noise.density", 128, 31);
        var (an, _, i2) = gpu.Noise("test.noise.amp", 128, 32);
        using var sim = new Fluid2DSim(gpu.Renderer, "timing", p, dn, an);
        var brush = new Fluid2DBrushTexel[p.BrushResolution * p.BrushResolution];
        for (int i = 0; i < brush.Length; i++) brush[i] = new Fluid2DBrushTexel(0.01f, -0.02f, (i * 7 % 13) / 13f);
        using var brushStorage = gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", brush);
        ulong step = 0;
        var passes = new Dictionary<string, List<double>>();
        for (int frame = 0; frame < 240; frame++)
        {
            gpu.Frame(f =>
            {
                sim.Step(f, new Fluid2DStepInput(step++, new Vector2(0.01f * step, 0), new Vector2(0.001f, 0)), brushStorage);
                sim.Export(f);
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
        double total = 0, stepOnly = 0;
        foreach (var (name, list) in passes.OrderBy(kv => kv.Key))
        {
            list.Sort();
            double median = list[list.Count / 2];
            total += median;
            if (name is not ("fluid.export" or "fluid.stats" or "fluid.stats.rows")) stepOnly += median;
            output.WriteLine($"gpu.{name}: медиана {median:F4} мс ({list.Count} кадров)");
        }
        output.WriteLine($"шаг (5 стадий) {stepOnly:F4} мс; со статистикой и экспортом {total:F4} мс; {gpu.Renderer.AdapterName}");
        Assert.True(total < 1.0, $"шаг 368² — {total} мс GPU");
        i1.Dispose();
        i2.Dispose();
    }
}
