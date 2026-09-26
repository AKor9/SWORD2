using System.Numerics;
using Sword.Game.Fire;

namespace Sword.Game.Tests;

/// <summary>
/// Размеры огня по полю (<see cref="FireMetrics"/>, вид <c>fire.metrics</c>): прямоугольники ρ на синтетическом поле дают высоту, ширину, края и
/// центроид в см по краям текселей; пороги ρ пламени и ядра — из k̄ формулы пламени; τ дыма по нормали = −ln T эталона марша дыма вдоль нормали
/// площадки; маска краёв гасит дым у края; пустое поле — пустые области.
/// </summary>
public sealed class FireMetricsTests
{
    private const int N = 64;
    private static readonly Vector2 Extents = new(3, 3);
    private const float Texel = 300f / N;

    private static Half[] Field(Func<int, int, float> rho)
    {
        var c = new Half[4 * N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                c[4 * (y * N + x) + 2] = (Half)rho(x, y);
        return c;
    }

    private static FireMetrics Compute(Half[] field) =>
        FireMetrics.Compute(field, N, Extents, FlameParams.Torch4, SmokeParams.Torch, FireMetricsParams.Default);

    [Fact]
    public void Rectangles_GiveEdgesInCentimetres_ThresholdsFromFlameFormula()
    {
        // Ядро ρ = 1 — ряды 28…31, столбцы 30…33; пламя ρ = 0.7 — ряды 24…33, столбцы 29…34; выше — ρ = 0.3 (только дым) до ряда 10.
        var field = Field((x, y) =>
            y is >= 28 and <= 31 && x is >= 30 and <= 33 ? 1f :
            y is >= 24 and <= 33 && x is >= 29 and <= 34 ? 0.7f :
            y is >= 10 and < 24 && x is >= 29 and <= 34 ? 0.3f : 0f);
        var m = Compute(field);

        float k = FlameParams.Torch4.DensityGain * MathF.Pow(0.5f, FlameParams.Torch4.FlowDensityPow);
        Assert.Equal(MathF.Pow(0.1f / k, 1f / FlameParams.Torch4.DensityPower), m.FlameRho, 5);
        Assert.Equal(MathF.Pow(1f / k, 1f / FlameParams.Torch4.DensityPower), m.CoreRho, 5);
        Assert.InRange(m.FlameRho, 0.3f, 0.7f);
        Assert.InRange(m.CoreRho, 0.7f, 1f);

        Assert.Equal(60, m.Flame.Texels);
        Assert.Equal(10 * Texel, m.Flame.HeightCm, 3);
        Assert.Equal(6 * Texel, m.Flame.WidthCm, 3);
        Assert.Equal((32 - 24) * Texel, m.Flame.TopCm, 3);           // верх ряда 24: v = 24/64, y = (0.5 − v)·300
        Assert.Equal((32 - 34) * Texel, m.Flame.BottomCm, 3);
        Assert.Equal((29 - 32) * Texel, m.Flame.LeftCm, 3);
        Assert.Equal((35 - 32) * Texel, m.Flame.RightCm, 3);
        Assert.Equal(0f, m.Flame.CentroidCm.X, 3);
        Assert.Equal((32 - 29) * Texel, m.Flame.CentroidCm.Y, 3);

        Assert.Equal(16, m.Core.Texels);
        Assert.Equal(4 * Texel, m.Core.HeightCm, 3);
        Assert.Equal(0.4f, m.CoreShare, 5);

        // Дым ρ = 0.3 по нормали — τ ≥ 0.1: столб от ряда 10 (вдали от края площадки маска краёв — 1).
        Assert.True(FireMetrics.NormalTau(0.3f, SmokeParams.Torch) >= FireMetricsParams.Default.SmokeTau);
        Assert.Equal((32 - 10) * Texel, m.Smoke.TopCm, 3);
        Assert.Equal((24 - 10) * Texel, m.SmokeAboveFlameCm, 3);
    }

    [Fact]
    public void EmptyField_EmptyRegions_EdgeMaskHidesSmokeAtBorder()
    {
        var empty = Compute(Field((_, _) => 0f));
        Assert.Equal(0, empty.Flame.Texels);
        Assert.Equal(0, empty.Smoke.Texels);
        Assert.Equal(0f, empty.CoreShare);
        Assert.Equal(0f, empty.SmokeAboveFlameCm);

        // Ряд 0 (верхний край): маска краёв (0.5 − |v − 0.5|)·6 ≈ 0.05 — τ ниже порога; тот же ρ в середине — дым.
        var border = Compute(Field((x, y) => y == 0 || y == 32 ? 0.3f : 0f));
        Assert.InRange(border.Smoke.Texels, N - 8, N - 2);   // ряд 32 без столбцов у левого и правого края
        Assert.Equal(0f, border.Smoke.TopCm, 3);
    }

    [Theory]
    [InlineData(0.02f)]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void NormalTau_EqualsSmokeMarchAlongNormal(float rho)
    {
        // Луч вдоль нормали площадки через её центр (объём — оси мира), поле постоянное: T эталона марша = exp(−τ).
        var p = SmokeParams.Torch;
        var sample = SmokeMath.March(new Vector3(0, 0, 5), -Vector3.UnitZ, float.MaxValue, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            _ => rho, Vector3.Zero, Vector3.Zero, [], p);
        float tau = -MathF.Log(sample.Transmittance);
        Assert.Equal(tau, FireMetrics.NormalTau(rho, p), tau * 0.05f + 1e-4f);
    }
}
