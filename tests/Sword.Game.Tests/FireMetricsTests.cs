using System.Numerics;
using Sword.Game.Fire;

namespace Sword.Game.Tests;

/// <summary>
/// Размеры огня по полю (<see cref="FireMetrics"/>, вид <c>fire.metrics</c>): прямоугольники ρ на синтетическом поле дают высоту, ширину, края и
/// центроид в см по краям текселей; пороги ρ пламени и ядра — из k̄ формулы пламени; столб дыма по 3D-полю газа — τ вдоль мировой z = −ln T
/// эталона марша дыма по тому же лучу, края в см от шишки по краям ячеек; пустое поле — пустые области.
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
        FireMetrics.Compute(field, N, Extents, FlameParams.Torch4, FireMetricsParams.Default);

    [Fact]
    public void Rectangles_GiveEdgesInCentimetres_ThresholdsFromFlameFormula()
    {
        // Ядро ρ = 1 — ряды 28…31, столбцы 30…33; пламя ρ = 0.7 — ряды 24…33, столбцы 29…34; выше — ρ = 0.3 (ни пламя, ни ядро) до ряда 10.
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
        Assert.Equal(0, m.Smoke.Texels);   // дым — по газу (SmokeColumn)

        // Столб газа над пламенем: верх дыма над верхом пламени — разность краёв (обе — от центра шишки).
        var column = new FireRegion(10, 60, 5, -4, 4, Vector2.Zero);
        Assert.Equal(60 - (32 - 24) * Texel, (m with { Smoke = column }).SmokeAboveFlameCm, 3);
    }

    [Fact]
    public void EmptyField_EmptyRegions()
    {
        var empty = Compute(Field((_, _) => 0f));
        Assert.Equal(0, empty.Flame.Texels);
        Assert.Equal(0, empty.Smoke.Texels);
        Assert.Equal(0f, empty.CoreShare);
        Assert.Equal(0f, empty.SmokeAboveFlameCm);
        var grid = new SmokeGrid(new float[8 * 8 * 8], 8, 8, 8, 0.03f, Vector3.Zero);
        Assert.Equal(default, FireMetrics.SmokeColumn(grid, Vector3.Zero, SmokeParams.Torch, 0.1f));
    }

    [Fact]
    public void SmokeColumn_EdgesFromKnobInCentimetres_TauAlongZ()
    {
        // Сетка 10×20×6 по 3 см с углом (1, 2, 3): столбец ρ = 0.3 в ячейках x 4…5, y 3…14, z 1…4 — τ вдоль z = 4 ячейки σ(0.3); выше, в
        // ячейках y 15…16, ρ = 10⁻⁴ (ниже среза — σ = 0). Шишка — (1.1, 2.1, 3.05).
        const float cell = 0.03f;
        var rho = new float[10 * 20 * 6];
        for (int z = 1; z <= 4; z++)
            for (int y = 3; y <= 16; y++)
                for (int x = 4; x <= 5; x++)
                    rho[x + 10 * (y + 20 * z)] = y <= 14 ? 0.3f : 1e-4f;
        var grid = new SmokeGrid(rho, 10, 20, 6, cell, new Vector3(1, 2, 3));
        var knob = new Vector3(1.1f, 2.1f, 3.05f);
        var p = SmokeParams.Torch;
        float tau = 4 * cell * p.UnitsPerMetre * SmokeMath.Sigma(0.3f, p);
        Assert.True(tau >= 0.1f, $"τ столба {tau}");
        var r = FireMetrics.SmokeColumn(grid, knob, p, 0.1f);
        Assert.Equal(2 * 12, r.Texels);
        Assert.Equal((2 + 15 * cell - 2.1f) * 100, r.TopCm, 3);
        Assert.Equal((2 + 3 * cell - 2.1f) * 100, r.BottomCm, 3);
        Assert.Equal((1 + 4 * cell - 1.1f) * 100, r.LeftCm, 3);
        Assert.Equal((1 + 6 * cell - 1.1f) * 100, r.RightCm, 3);
        Assert.Equal(12 * cell * 100, r.HeightCm, 3);
        Assert.Equal((1 + 5 * cell - 1.1f) * 100, r.CentroidCm.X, 3);
        Assert.Equal((2 + 9 * cell - 2.1f) * 100, r.CentroidCm.Y, 3);
        // Порог выше τ столба — пусто.
        Assert.Equal(0, FireMetrics.SmokeColumn(grid, knob, p, tau * 1.01f).Texels);

        // τ столба ≈ −ln T эталона марша вдоль z через его середину (без света и неба): у марша трилинейные края столба — до 15 % больше.
        var eye = grid.Min + new Vector3(4.5f * cell + cell * 0.5f, 8.5f * cell, 10);
        var sample = SmokeMath.March(eye, -Vector3.UnitZ, float.MaxValue, grid, Vector3.Zero, Vector3.Zero, [], p with { MinTransmittance = 0 });
        Assert.InRange(-MathF.Log(sample.Transmittance), tau, tau * 1.15f);
    }

    /// <summary>
    /// Стык: пламя — столбик ρ 1 (|x| ≤ 3 см, y −5…20 см), дым — наклонная на 20° струя ρ 0.5 толщиной 12 см вдоль взгляда от высоты
    /// <paramref name="startCm"/>. Зазор — от верха пламени до первого ряда с τ ≥ 0.5 (ряды по 3 см от шишки), углы — пламени 0, струи 20°
    /// (у струи от верха пламени).
    /// </summary>
    [Theory]
    [InlineData(32f, 10f, 15f, false)]
    [InlineData(18f, 0f, 0f, true)]
    public void Joint_GapAndAngles_OfSyntheticFlameAndSmoke(float startCm, float gapMin, float gapMax, bool jet)
    {
        const int n = 368;
        var composite = new Half[4 * n * n];
        float texelCm = 300f / n;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float px = (x + 0.5f - n * 0.5f) * texelCm, py = (n * 0.5f - y - 0.5f) * texelCm;
                if (MathF.Abs(px) <= 3 && py >= -5 && py <= 20) composite[4 * (y * n + x) + 2] = (Half)1f;
            }
        var knob = new Vector3(0.2f, 1f, -0.1f);
        const int nx = 40, ny = 60, nz = 10;
        var rho = new float[nx * ny * nz];
        var min = knob - new Vector3(0.6f, 0.3f, 0.15f);
        float tan = MathF.Tan(20f * MathF.PI / 180f);
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    var c = (min + (new Vector3(x, y, z) + new Vector3(0.5f)) * 0.03f - knob) * 100f;
                    if (c.Y >= startCm && MathF.Abs(c.X - (c.Y - 20f) * tan) <= 3f && MathF.Abs(c.Z) <= 6f) rho[x + nx * (y + ny * z)] = 0.5f;
                }
        var grid = new SmokeGrid(rho, nx, ny, nz, 0.03f, min);
        var j = FireMetrics.Joint(composite, n, new Vector2(3, 3), FlameParams.Torch4, FireMetricsParams.Default, grid, knob, Quaternion.Identity,
            SmokeParams.Torch);
        Assert.True(j.Valid);
        Assert.InRange(j.FlameTopCm, 20f, 21f);
        Assert.InRange(j.FlameAngleDeg, -0.5f, 0.5f);
        Assert.InRange(j.GapCm, gapMin, gapMax);
        if (!jet) return;
        // Клетки 3 см: центры струи на +10 и +30 см — по ячейкам, угол с точностью до клетки.
        Assert.InRange(j.SmokeAngleDeg, 12f, 29f);
        Assert.InRange(j.AngleDifferenceDeg, 12f, 29f);
    }

    /// <summary>Дыма у оси нет — стык не найден (Valid false), профиль τ у оси есть и нулевой.</summary>
    [Fact]
    public void Joint_NoSmokeNearAxis_NotValid()
    {
        const int n = 64;
        var composite = new Half[4 * n * n];
        for (int y = n / 2 - 4; y < n / 2; y++) composite[4 * (y * n + n / 2) + 2] = (Half)1f;
        var grid = new SmokeGrid(new float[8 * 8 * 8], 8, 8, 8, 0.03f, Vector3.Zero);
        var j = FireMetrics.Joint(composite, n, new Vector2(3, 3), FlameParams.Torch4, FireMetricsParams.Default, grid, new Vector3(0.12f), Quaternion.Identity,
            SmokeParams.Torch);
        Assert.False(j.Valid);
        Assert.All(j.TauNearAxis!, t => Assert.Equal(0f, t));
    }
}
