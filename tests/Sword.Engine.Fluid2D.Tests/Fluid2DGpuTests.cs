using System.Runtime.InteropServices;
using Sword.Engine.Render;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.Fluid2DTestKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Сим на настоящем устройстве (слой отладки и GPU-валидация, любое предупреждение D3D12 — провал) против CPU-эталона
/// <see cref="Fluid2DReference"/>: каждая стадия и шаг целиком на случайных полях 64² (факел и «все режимы») и на 368², экспорт,
/// статистика, детерминизм, долгий прогон, 0 Б на кадр и GPU-время шага.
/// </summary>
/// <remarks>
/// Допуск стадии — 2 ULP half: GPU и CPU считают одни и те же выражения float по одним и тем же half-входам (-Gis, без FMA); расходятся
/// только приближённые pow/exp/log/sqrt (доли ULP float), которые изредка перекидывают округление в half на 1 ULP (замер — 1 ULP).
/// Шумы эталону — значения, прочитанные с GPU: декод sRGB железом отличается от формулы до ~0.004 (замер), это забота текстур Render.
/// </remarks>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class Fluid2DGpuTests : IDisposable
{
    private const double StageUlps = 2;
    private readonly GpuDevice _gpu = new("Sword.Engine.Fluid2D.Tests");
    private readonly ITestOutputHelper _output;

    public Fluid2DGpuTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _gpu.Dispose();

    private sealed class Pair : IDisposable
    {
        public required Fluid2DSim Gpu;
        public required Fluid2DReference Cpu;
        public required GpuImage[] Images;
        public required Fluid2DNoiseImage DensityNoise, AmpNoise;
        public GpuStorage? BrushStorage;
        public Fluid2DBrushTexel[] Brush = [];

        public void Dispose()
        {
            Gpu.Dispose();
            BrushStorage?.Dispose();
            foreach (var i in Images) i.Dispose();
        }
    }

    private Pair Make(Fluid2DParams p, Fluid2DFields? fields, bool brush, int seed = 7)
    {
        var (dn, dnCpu, dImage) = _gpu.Noise("test.noise.density", 128, seed + 1);
        var (an, anCpu, aImage) = _gpu.Noise("test.noise.amp", 128, seed + 2);
        var pair = new Pair
        {
            Gpu = new Fluid2DSim(_gpu.Renderer, "test", p, dn, an),
            Cpu = new Fluid2DReference(p, dnCpu, anCpu, fields?.Clone()),
            Images = [dImage, aImage],
            DensityNoise = dnCpu,
            AmpNoise = anCpu,
        };
        if (fields is not null) pair.Gpu.Load(fields);
        if (brush)
        {
            pair.Brush = RandomBrush(p.BrushResolution, seed + 3);
            pair.BrushStorage = _gpu.Renderer.CreateStorage<Fluid2DBrushTexel>("test.brush", pair.Brush);
        }
        return pair;
    }

    private static Half[] Output(Fluid2DFields f, Fluid2DStage stage) => stage switch
    {
        Fluid2DStage.Composite => f.Composite,
        Fluid2DStage.Advection => f.Advected,
        Fluid2DStage.Divergence or Fluid2DStage.PressureY => f.Scratch,
        _ => f.Pressure,
    };

    private void Report(string what, (double Ulps, double Abs, int Index) e) =>
        _output.WriteLine($"{what}: {e.Ulps:F2} ULP half, {e.Abs:G4} абсолютно (элемент {e.Index})");

    [Theory]
    [InlineData(false, Fluid2DStage.Composite)]
    [InlineData(false, Fluid2DStage.Advection)]
    [InlineData(false, Fluid2DStage.Divergence)]
    [InlineData(false, Fluid2DStage.PressureX)]
    [InlineData(false, Fluid2DStage.PressureY)]
    [InlineData(true, Fluid2DStage.Composite)]
    [InlineData(true, Fluid2DStage.Advection)]
    [InlineData(true, Fluid2DStage.Divergence)]
    [InlineData(true, Fluid2DStage.PressureX)]
    [InlineData(true, Fluid2DStage.PressureY)]
    public void Stage_MatchesReference_OnRandomFields(bool everything, Fluid2DStage stage)
    {
        var p = everything ? Everything() : Torch();
        using var pair = Make(p, RandomFields(64, 11), brush: true);
        _gpu.Frame(f => pair.Gpu.AddStage(f, Input, pair.BrushStorage, stage));
        pair.Cpu.Stage(stage, Input, pair.Brush);
        var e = Compare(Output(pair.Gpu.ReadFields(), stage), Output(pair.Cpu.Fields, stage));
        Report($"{(everything ? "все режимы" : "факел")}, {stage}", e);
        _output.WriteLine($"декод sRGB железом против формулы: до {_gpu.SrgbDecodeError:G3}");
        Assert.True(e.Ulps <= StageUlps, $"{stage}: {e.Ulps} ULP half (элемент {e.Index})");
        _gpu.AssertNoDebugMessages();
    }

    /// <summary>Контроль допуска: те же поля, но A на GPU транспонирована (ошибка раскладки) — расхождение много больше допуска.</summary>
    [Fact]
    public void LayoutError_ExceedsTolerance()
    {
        var p = Torch();
        var fields = RandomFields(64, 11);
        var transposed = fields.Clone();
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                for (int k = 0; k < 4; k++) transposed.Advected[(y * 64 + x) * 4 + k] = fields.Advected[(x * 64 + y) * 4 + k];
        using var pair = Make(p, fields, brush: true);
        pair.Gpu.Load(transposed);
        _gpu.Frame(f => pair.Gpu.AddStage(f, Input, pair.BrushStorage, Fluid2DStage.Composite));
        pair.Cpu.Stage(Fluid2DStage.Composite, Input, pair.Brush);
        var e = Compare(pair.Gpu.ReadFields().Composite, pair.Cpu.Fields.Composite);
        Report("транспонированная A", e);
        Assert.True(e.Ulps > 1000 * StageUlps && e.Abs > 1, $"ошибка раскладки не видна: {e.Ulps} ULP, {e.Abs}");
    }

    /// <summary>
    /// Шаг целиком (проходы, роли буферов, обмен давления): каждый выход шага на GPU = эталон стадии на входах, которые эта стадия
    /// получила на GPU (C — из начальных полей, A — из C GPU, давление X — из дивергенции эталона по A GPU, давление Y — из X GPU).
    /// Сравнивать весь шаг с эталоном целиком нельзя в допуске half: 1 ULP скорости сдвигает точку адвекции на ~0.004·SimSpeed текселя,
    /// и на случайном поле значение уходит до 0.01 (замер); цепочка по стадиям держит 1–2 ULP и ловит любую ошибку проводки.
    /// </summary>
    [Theory]
    [InlineData(false, 64)]
    [InlineData(true, 64)]
    [InlineData(false, 368)]
    public void Step_MatchesReference_StageByStage(bool everything, int n)
    {
        var p = everything ? Everything(n) : Torch(n);
        var initial = RandomFields(n, 13);
        using var pair = Make(p, initial, brush: true);
        _gpu.Frame(f => pair.Gpu.Step(f, Input, pair.BrushStorage));
        var gpu = pair.Gpu.ReadFields();
        string what = $"{(everything ? "все режимы" : "факел")} {n}²";

        var cpu = Chain(p, pair, initial.Composite, initial.Advected, initial.Pressure, initial.Scratch);
        cpu.Stage(Fluid2DStage.Composite, Input, pair.Brush);
        AssertClose($"{what}, C", gpu.Composite, cpu.Fields.Composite, StageUlps);

        cpu = Chain(p, pair, gpu.Composite, initial.Advected, initial.Pressure, initial.Scratch);
        cpu.Stage(Fluid2DStage.Advection, Input, pair.Brush);
        AssertClose($"{what}, A", gpu.Advected, cpu.Fields.Advected, StageUlps);

        cpu = Chain(p, pair, gpu.Composite, gpu.Advected, initial.Pressure, initial.Scratch);
        cpu.Stage(Fluid2DStage.Divergence, Input, pair.Brush);
        var pass = (Input.Step & 1) != 0 ? Fluid2DStage.PressureY : Fluid2DStage.PressureX;
        if (p.CrudePressureSolver)
        {
            // Один проход по чётности шага: S — дивергенция, P — итог; буферы ролями не меняются.
            AssertClose($"{what}, S", gpu.Scratch, cpu.Fields.Scratch, StageUlps);
            cpu = Chain(p, pair, gpu.Composite, gpu.Advected, gpu.Pressure, gpu.Scratch);
            cpu.Stage(pass, Input, pair.Brush);
            AssertClose($"{what}, P ({pass})", gpu.Pressure, pass == Fluid2DStage.PressureX ? cpu.Fields.Pressure : cpu.Fields.Scratch, StageUlps);
        }
        else
        {
            // Два прохода и обмен: P GPU — итог Y (бывший S), S GPU — итог X (бывший P). Дивергенцию шаг затёр — её даёт та же стадия
            // отдельно на тех же входах (GPU детерминирован).
            var divergence = Chain(p, pair, gpu.Composite, gpu.Advected, initial.Pressure, initial.Scratch).Fields;
            pair.Gpu.Load(divergence);
            _gpu.Frame(f => pair.Gpu.AddStage(f, Input, pair.BrushStorage, Fluid2DStage.Divergence));
            var gpuDivergence = pair.Gpu.ReadFields().Scratch;
            AssertClose($"{what}, S", gpuDivergence, cpu.Fields.Scratch, StageUlps);
            cpu = Chain(p, pair, gpu.Composite, gpu.Advected, initial.Pressure, gpuDivergence);
            cpu.Stage(Fluid2DStage.PressureX, Input, pair.Brush);
            AssertClose($"{what}, давление X", gpu.Scratch, cpu.Fields.Pressure, StageUlps);
            cpu = Chain(p, pair, gpu.Composite, gpu.Advected, gpu.Scratch, gpu.Pressure);
            cpu.Stage(Fluid2DStage.PressureY, Input, pair.Brush);
            AssertClose($"{what}, давление Y", gpu.Pressure, cpu.Fields.Scratch, StageUlps);
        }
        _gpu.AssertNoDebugMessages();
    }

    private static Fluid2DReference Chain(Fluid2DParams p, Pair pair, Half[] composite, Half[] advected, Half[] pressure, Half[] scratch)
    {
        var f = new Fluid2DFields(p.Resolution);
        composite.CopyTo(f.Composite, 0);
        advected.CopyTo(f.Advected, 0);
        pressure.CopyTo(f.Pressure, 0);
        scratch.CopyTo(f.Scratch, 0);
        return new Fluid2DReference(p, pair.DensityNoise, pair.AmpNoise, f);
    }

    private void AssertClose(string what, Half[] gpu, Half[] cpu, double ulps)
    {
        var e = Compare(gpu, cpu);
        Report(what, e);
        Assert.True(e.Ulps <= ulps, $"{what}: {e.Ulps} ULP (элемент {e.Index})");
    }

    /// <summary>
    /// Горящий факел на 368², 30 шагов подряд с кистью: расхождения в ULP растут через адвекцию (выше), но поле в целом идёт за эталоном —
    /// Σρ и центроид в пределах 0.1 %, среднеквадратичное расхождение C — меньше 1 % от среднеквадратичного значения.
    /// </summary>
    [Fact]
    public void Steps_OnSmoothField_FollowReference()
    {
        var p = Torch(368);
        using var pair = Make(p, SmoothFields(368), brush: true);
        for (ulong s = 0; s < 30; s++)
        {
            var input = Input with { Step = 100 + s };
            _gpu.Frame(f => pair.Gpu.Step(f, input, pair.BrushStorage));
            pair.Cpu.Step(input, pair.Brush);
        }
        var gpu = pair.Gpu.ReadFields();
        var e = Compare(gpu.Composite, pair.Cpu.Fields.Composite);
        double diff = 0, norm = 0;
        for (int i = 0; i < gpu.Composite.Length; i++)
        {
            double g = (double)gpu.Composite[i], c = (double)pair.Cpu.Fields.Composite[i];
            diff += (g - c) * (g - c);
            norm += c * c;
        }
        double rms = Math.Sqrt(diff / norm);
        var sg = Fluid2DStats.Compute(gpu, 0);
        var sc = Fluid2DStats.Compute(pair.Cpu.Fields, 0);
        _output.WriteLine($"30 шагов: наибольшее {e.Abs:G3} ({e.Ulps:F0} ULP), RMS {rms:G3}; Σρ {sg.DensitySum} / {sc.DensitySum}, центроид {sg.Centroid} / {sc.Centroid}");
        Assert.True(rms < 0.01, $"RMS {rms}");
        Assert.True(Math.Abs(sg.DensitySum - sc.DensitySum) <= 1e-3 * sc.DensitySum, $"Σρ {sg.DensitySum} против {sc.DensitySum}");
        Assert.True((sg.Centroid - sc.Centroid).Length() <= 1e-3f, $"центроид {sg.Centroid} против {sc.Centroid}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Export_MatchesReference(bool everything)
    {
        var p = everything ? Everything() : Torch();
        using var pair = Make(p, RandomFields(64, 17), brush: false);
        var gpu = _gpu.ReadExport(pair.Gpu);
        var e = Compare(gpu, pair.Cpu.Export());
        Report($"экспорт, {(everything ? "все режимы" : "факел")}", e);
        Assert.True(e.Ulps <= StageUlps, $"экспорт: {e.Ulps} ULP (элемент {e.Index})");
        _gpu.AssertNoDebugMessages();
    }
}
