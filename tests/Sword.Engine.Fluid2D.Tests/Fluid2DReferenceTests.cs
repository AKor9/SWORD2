using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.Fluid2DTestKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Свойства шага на CPU-эталоне (GPU совпадает с ним по стадиям — <see cref="Fluid2DGpuTests"/>): ноль остаётся нулём, шум даёт скорость без
/// плотности, затухание капли ×0.9405 за шаг и быстрее у края, давление уменьшает дивергенцию, сдвиг площадки переносит каплю, ядра давления.
/// </summary>
public sealed class Fluid2DReferenceTests(ITestOutputHelper output)
{
    private static readonly Fluid2DStepInput Still = new(0, Vector2.Zero, Vector2.Zero);

    private static Fluid2DReference Run(Fluid2DParams p, Fluid2DFields? fields, int steps, ReadOnlySpan<Fluid2DBrushTexel> brush = default)
    {
        var noise = new Fluid2DNoiseImage(Enumerable.Range(0, 64 * 64).Select(i => (i * 37 % 101) / 100f).ToArray(), 64, 64);
        var r = new Fluid2DReference(p, noise, noise, fields);
        for (int s = 0; s < steps; s++) r.Step(Still with { Step = (ulong)s }, brush);
        return r;
    }

    [Fact]
    public void ZeroField_StaysZero_WithoutNoiseInputAndBrush()
    {
        var r = Run(Quiet(), null, 20);
        foreach (var g in new[] { r.Fields.Composite, r.Fields.Advected, r.Fields.Pressure, r.Fields.Scratch })
            Assert.All(g, h => Assert.Equal(0f, (float)h));
    }

    /// <summary>
    /// Факел без направленного шума и кисти: плотность стоит на полу L2 (1e-6 в half), скорость — только от постоянного ввода, пропорционального
    /// этому полу (установившаяся ~2e-5).
    /// </summary>
    [Fact]
    public void Torch_WithoutDirNoise_StaysAtDensityFloor()
    {
        var r = Run(Torch() with { VeloDirNoise = 0 }, null, 30);
        var c = r.Fields.Composite;
        for (int i = 0; i < c.Length; i += 4)
        {
            Assert.Equal((float)(Half)1e-6f, (float)c[i + 2]);
            Assert.True(MathF.Abs((float)c[i]) < 1e-4f && MathF.Abs((float)c[i + 1]) < 1e-4f, $"скорость {(float)c[i]}, {(float)c[i + 1]}");
        }
    }

    [Fact]
    public void DirNoise_MakesVelocity_WithoutDensity()
    {
        var r = Run(Quiet() with { VeloDirNoise = 10 }, null, 5);
        var stats = Fluid2DStats.Compute(r.Fields, 5);
        Assert.Equal(0f, stats.DensitySum);
        Assert.True(stats.MaxSpeed > 0.05f, $"max|v| {stats.MaxSpeed}");
    }

    [Fact]
    public void Drop_DecaysBy09405_PerStep_InTheInterior()
    {
        var r = new Fluid2DReference(Quiet(), null, null, Drop(64, 0.5f, 0.5f, 0.08f));
        r.Step(Still);
        float previous = Sum(r.Fields.Composite, 2, 4);
        for (int s = 1; s < 10; s++)
        {
            r.Step(Still with { Step = (ulong)s });
            float sum = Sum(r.Fields.Composite, 2, 4);
            Assert.InRange(sum / previous, 0.9405f - 1e-3f, 0.9405f + 1e-3f);
            previous = sum;
        }
    }

    [Fact]
    public void Drop_AtTheEdge_DecaysFaster()
    {
        var r = new Fluid2DReference(Quiet(), null, null, Drop(64, 0.985f, 0.5f, 0.02f));
        r.Step(Still);
        float previous = Sum(r.Fields.Composite, 2, 4);
        r.Step(Still with { Step = 1 });
        float ratio = Sum(r.Fields.Composite, 2, 4) / previous;
        output.WriteLine($"у края ×{ratio} за шаг");
        Assert.True(ratio < 0.9405f - 0.05f, $"у края ×{ratio} за шаг");
    }

    /// <summary>Источник (радиальный разлёт): после 8 шагов дивергенция поля C с давлением меньше, чем без него (Feedback 0 — давления нет).</summary>
    [Fact]
    public void Pressure_ReducesDivergence()
    {
        static Fluid2DFields Source()
        {
            var f = new Fluid2DFields(64);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float u = (x + 0.5f) / 64 - 0.5f, v = (y + 0.5f) / 64 - 0.5f;
                    float g = 0.5f * MathF.Exp(-(u * u + v * v) / 0.02f);
                    f.Advected[(y * 64 + x) * 4] = (Half)(u * 8 * g);
                    f.Advected[(y * 64 + x) * 4 + 1] = (Half)(v * 8 * g);
                }
            return f;
        }

        static double Divergence(Fluid2DFields f)
        {
            double sum = 0;
            for (int y = 4; y < 60; y++)
                for (int x = 4; x < 60; x++)
                {
                    double d = f.Velocity(x + 1, y).X - f.Velocity(x - 1, y).X + f.Velocity(x, y + 1).Y - f.Velocity(x, y - 1).Y;
                    sum += d * d;
                }
            return sum;
        }

        double with = Divergence(Run(Quiet(), Source(), 8).Fields);
        double without = Divergence(Run(Quiet() with { PressureFeedback = 0, DivergenceFeedback = 0 }, Source(), 8).Fields);
        output.WriteLine($"Σdiv² после 8 шагов: с давлением {with:G4}, без {without:G4}");
        Assert.True(with < 0.7 * without, $"Σdiv²: с давлением {with}, без {without}");
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(2.5f)]
    [InlineData(-3f)]
    public void WorldOffsetDelta_MovesDrop_ByMinusATexels(float texels)
    {
        var drop = Drop(64, 0.5f, 0.45f, 0.1f);
        var before = Centroid(drop.Advected);
        var r = new Fluid2DReference(Quiet(), null, null, drop);
        r.Stage(Fluid2DStage.Composite, Still with { WorldOffsetDelta = new Vector2(texels / 64, 0) });
        var after = Centroid(r.Fields.Composite);
        Assert.InRange((after.X - before.X) * 64, -texels - 0.02f, -texels + 0.02f);
        Assert.InRange((after.Y - before.Y) * 64, -0.02f, 0.02f);
    }

    /// <summary>Сдвиг на полплощадки: правая половина читает за краем — последний столбец A (clamp), ×0.95 в глубине площадки.</summary>
    [Fact]
    public void WorldOffsetDelta_ClampsAtTheEdge()
    {
        var f = new Fluid2DFields(64);
        for (int y = 0; y < 64; y++) f.Advected[(y * 64 + 63) * 4 + 2] = (Half)(0.3f + 0.005f * y);
        var r = new Fluid2DReference(Quiet(), null, null, f);
        r.Stage(Fluid2DStage.Composite, Still with { WorldOffsetDelta = new Vector2(0.5f, 0) });
        for (int y = 8; y < 56; y++)
            for (int x = 32; x < 58; x++)
                Assert.InRange(r.Fields.Density(x, y), 0.95f * (float)(Half)(0.3f + 0.005f * y) * 0.999f, 0.95f * (float)(Half)(0.3f + 0.005f * y) * 1.001f);
        Assert.Equal(0f, r.Fields.Density(10, 30));
    }

    /// <summary>
    /// Ветер факела в мировых осях SWORD2 (подъём +Y 0.2, снос −X 0.1 = UE (0, 0.1, 0.2)) в осях площадки: вверх — к v = 0 (v растёт вниз);
    /// площадка развёрнута на 180° — снос к u = 1; наклон на θ — подъём ×cos θ. Капля в покое всплывает к v = 0 и сносится к u = 0.
    /// </summary>
    [Fact]
    public void DirectVelocity_Torch_LiftsTowardVZero_InPlaneAxes()
    {
        static Vector2 Base(Quaternion q) => Fluid2DConstants.Build(Fluid2DParams.Torch, Still with { PlaneRotation = q }, false, default, default).VelocityBase;
        var facing = Base(Quaternion.Identity);
        var back = Base(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));
        Assert.Equal((-0.1f, -0.2f), (facing.X, facing.Y));
        Assert.Equal(0.1f, back.X, 5);
        Assert.Equal(-0.2f, back.Y, 5);
        Assert.Equal(-0.2f * MathF.Cos(1f), Base(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f)).Y, 5);

        var drop = Drop(64, 0.5f, 0.5f, 0.1f);
        var before = Centroid(drop.Advected);
        var r = new Fluid2DReference(Quiet() with { DirectVelocity = Fluid2DParams.Torch.DirectVelocity }, null, null, drop);
        for (int s = 0; s < 10; s++) r.Step(Still with { Step = (ulong)s });
        var moved = (Centroid(r.Fields.Composite) - before) * 64;
        output.WriteLine($"капля за 10 шагов: {moved} текселей");
        Assert.True(moved.Y < -0.5f && moved.X < -0.1f && moved.X > 0.8f * moved.Y, $"сдвиг {moved}");
    }

    /// <summary>Упругость края: на внешнем кольце (≈1.9 текселя при 368²) скорость ×(1 − √0.1) = 0.684, внутри — без потерь.</summary>
    [Fact]
    public void EdgeRing_DampsVelocity_ByBounciness()
    {
        var f = new Fluid2DFields(368);
        for (int i = 0; i < 368 * 368; i++) f.Advected[i * 4] = (Half)1f;
        var r = new Fluid2DReference(Quiet(368), null, null, f);
        r.Stage(Fluid2DStage.Composite, Still);
        Assert.Equal((float)(Half)(1f - MathF.Sqrt(0.1f)), r.Fields.Velocity(0, 100).X);
        Assert.Equal((float)(Half)(1f - MathF.Sqrt(0.1f)), r.Fields.Velocity(100, 1).X);
        Assert.Equal(1f, r.Fields.Velocity(2, 100).X);
    }

    private static Vector2 Centroid(Half[] g)
    {
        double sum = 0, sx = 0, sy = 0;
        for (int i = 0; i < g.Length / 4; i++)
        {
            double rho = (float)g[i * 4 + 2];
            sum += rho;
            sx += rho * (i % 64 + 0.5) / 64;
            sy += rho * (i / 64 + 0.5) / 64;
        }
        return new Vector2((float)(sx / sum), (float)(sy / sum));
    }

    [Fact]
    public void PressureKernels_AreL2Rows()
    {
        Assert.Equal((15, 0, 0), Fluid2DConstants.Kernel(1, 0));
        Assert.Equal((7, 64, 64), Fluid2DConstants.Kernel(0.5f, 0));   // песок: KM 0.5 → G7/C7
        Assert.Equal((9, 33, 48), Fluid2DConstants.Kernel(0.6f, -1));
        var c = Fluid2DConstants.Build(Fluid2DParams.Torch, Still, false, default, default);
        float sumP = 0, sumD = 0;
        for (int j = 0; j < 16; j++)
        {
            sumP += c.WeightP[j];
            sumD += c.WeightD[j];
        }
        Assert.Equal(15u, c.Taps);
        Assert.Equal(0.9998f, sumP, 4);
        Assert.Equal(1.7506f, sumD, 4);
        Assert.Equal(0f, c.WeightP[15]);
        // Ряд P за таблицей — нули (кровь BVR: «веса гаусса вне массива → 0»).
        var shifted = Fluid2DConstants.Build(Fluid2DParams.Torch with { KernelIndexOffset = -1 }, Still, false, default, default);
        Assert.Equal(0.00117f, shifted.WeightP[15]);
        for (int j = 0; j < 15; j++) Assert.Equal(0f, shifted.WeightP[j]);
    }

    [Fact]
    public void Simplex_IsBounded_Continuous_AndCentred()
    {
        double mean = 0;
        float max = 0;
        var rng = new Random(3);
        for (int i = 0; i < 20000; i++)
        {
            float x = rng.NextSingle() * 200 - 100, y = rng.NextSingle() * 200 - 100, z = rng.NextSingle() * 10;
            var (a, b) = SimplexNoise.Sample(x, y, z);
            max = MathF.Max(max, MathF.Max(MathF.Abs(a), MathF.Abs(b)));
            mean += a + b;
            var (a2, b2) = SimplexNoise.Sample(x + 1e-3f, y, z);
            Assert.True(MathF.Abs(a2 - a) < 0.02f && MathF.Abs(b2 - b) < 0.02f, $"разрыв в ({x}, {y}, {z})");
        }
        Assert.InRange(max, 0.5f, 1f);
        Assert.InRange(mean / 40000, -0.02, 0.02);
    }
}
