using System.Numerics;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Детализирующий шум дыма на CPU (<see cref="SmokeMath.DetailDensity"/>, эталон шейдера): только уменьшает ρ и не трогает ρ у среза и ниже;
/// стоит в мире (окно сдвинуто целыми ячейками — та же ρ' в мировой точке, и за километр от начала координат); flow-map — фазы и вес по
/// времени, смена фазы без скачка, шум уносится скоростью газа.
/// </summary>
public sealed class FireSmokeDetailTests(ITestOutputHelper output)
{
    private static readonly SmokeParams P = SmokeParams.Torch with { Detail = 1.5f };

    private static SmokeGrid Grid(Vector3 min, Vector3 velocity)
    {
        const int n = 8;
        var v = new float[3 * n * n * n];
        for (int i = 0; i < n * n * n; i++) (v[3 * i], v[3 * i + 1], v[3 * i + 2]) = (velocity.X, velocity.Y, velocity.Z);
        return new SmokeGrid(new float[n * n * n], n, n, n, 0.03f, min) { Velocity = v };
    }

    [Fact]
    public void Detail_OnlyLowersDensity_ZeroAndCutUntouched()
    {
        var grid = Grid(new Vector3(0.3f, 1.2f, -0.6f), new Vector3(0.2f, 0.9f, -0.1f));
        var flow = SmokeMath.Flow(3.3, P);
        var rng = new Random(2);
        int lowered = 0;
        for (int i = 0; i < 5000; i++)
        {
            var q = new Vector3(rng.NextSingle() * 7, rng.NextSingle() * 7, rng.NextSingle() * 7);
            float rho = rng.NextSingle();
            float d = SmokeMath.DetailDensity(rho, q, grid, flow, P);
            Assert.InRange(d, 0, rho);
            if (d < rho) lowered++;
            Assert.Equal(rho, SmokeMath.DetailDensity(rho, q, grid, flow, P with { Detail = 0 }));
            Assert.Equal(P.DensityCut, SmokeMath.DetailDensity(P.DensityCut, q, grid, flow, P));
        }
        output.WriteLine($"шум уменьшил ρ в {lowered} точках из 5000");
        Assert.True(lowered > 4000);
    }

    [Fact]
    public void Detail_StandsInWorld_WindowShiftedByWholeCells()
    {
        // Окно за километр от начала координат и то же окно, сдвинутое на (3, −2, 5) ячеек: в одной мировой точке — та же ρ' (скорость газа 0 —
        // и с потоком: сдвиг шума зависит только от скорости в точке).
        const float cell = 0.03f;
        var a = Grid(new Vector3(40_000 * cell, -37 * cell, 12_345 * cell), new Vector3(0.4f, 1.1f, 0.2f));
        var b = Grid(a.Min + new Vector3(3, -2, 5) * cell, new Vector3(0.4f, 1.1f, 0.2f));
        var flow = SmokeMath.Flow(7.9, P);
        float worst = 0, spread = 0;
        for (int z = 5; z < 8; z++)
            for (int y = 0; y < 6; y++)
                for (int x = 3; x < 8; x++)
                {
                    var q = new Vector3(x + 0.37f, y + 0.61f, z - 0.19f);
                    float da = SmokeMath.DetailDensity(0.5f, q, a, flow, P);
                    float db = SmokeMath.DetailDensity(0.5f, q - new Vector3(3, -2, 5), b, flow, P);
                    worst = MathF.Max(worst, MathF.Abs(da - db));
                    spread = MathF.Max(spread, MathF.Abs(da - SmokeMath.DetailDensity(0.5f, q + Vector3.UnitX, a, flow, P)));
                }
        output.WriteLine($"сдвиг окна: наибольшее расхождение ρ' {worst:E2}; соседняя ячейка — {spread:F3}");
        Assert.True(worst < 1e-4f, $"шум не стоит в мире: {worst}");
        Assert.True(spread > 0.05f, "контроль: соседняя ячейка — другое значение");
    }

    [Fact]
    public void DetailPixels_NearOnFarOff()
    {
        // 1280×720, вертикальный угол 0.9 рад: шаг 8 см у точки в 3 м — ≈13 пикселей ½, в 24 м — ≈1.6 (шум выключен).
        var eye = new Vector3(0, 1, 0);
        foreach (var (distance, on) in new[] { (3f, true), (6f, true), (24f, false) })
        {
            var at = eye + new Vector3(0, 0, -distance);
            var view = Matrix4x4.CreateLookAt(eye, at, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1280 / 720f, 0.1f, 1000);
            float px = SmokeMath.DetailPixels(view, -Vector3.UnitZ, at, 0.08f, 1280);
            output.WriteLine($"{distance} м: {px:F2} пикселя ½");
            Assert.Equal(on, px >= SmokeMath.MinDetailPixels);
        }
        var behind = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 16 / 9f, 0.1f, 1000);
        Assert.Equal(float.PositiveInfinity, SmokeMath.DetailPixels(behind, -Vector3.UnitZ, eye + Vector3.UnitZ * 2, 0.08f, 1280));
    }

    [Fact]
    public void FlowMap_PhasesBlend_WrapIsContinuous_NoiseMovesWithGas()
    {
        var p = P with { FlowSpeed = 2, FlowStrength = 1, DetailScale = 0.05f };
        // τ = 1 (t = 0.5 с): первая фаза на сбросе (сдвиг −½ периода, вес второй 1), вторая свежая; τ = 1.5: наоборот.
        var reset = SmokeMath.Flow(0.5, p);
        var fresh = SmokeMath.Flow(0.75, p);
        float s = 1 / (2f * 0.05f);
        Assert.Equal(-0.5f * s, reset.Shift0, 4);
        Assert.Equal(0f, reset.Shift1, 4);
        Assert.Equal(1f, reset.Blend, 4);
        Assert.Equal(0f, fresh.Shift0, 4);
        Assert.Equal(1f, MathF.Abs(fresh.Shift1 / (0.5f * s)), 4);
        Assert.Equal(0f, fresh.Blend, 4);

        // Смена фазы (τ через целое): ρ' непрерывна — фаза на сбросе имеет вес 0.
        var grid = Grid(Vector3.Zero, new Vector3(0.3f, 1.2f, -0.4f));
        float jump = 0;
        for (int i = 0; i < 200; i++)
        {
            var q = new Vector3(1 + i * 0.031f, 2 + i * 0.017f, 3 + i * 0.023f);
            float before = SmokeMath.DetailDensity(0.5f, q, grid, SmokeMath.Flow(0.5 - 1e-6, p), p);
            float after = SmokeMath.DetailDensity(0.5f, q, grid, SmokeMath.Flow(0.5 + 1e-6, p), p);
            jump = MathF.Max(jump, MathF.Abs(before - after));
        }
        output.WriteLine($"смена фазы: наибольший скачок ρ' {jump:E2}");
        Assert.True(jump < 1e-3f, $"скачок на смене фазы {jump}");

        // Перенос: за 0.1 с внутри фазы шум в точке q — тот, что был в точке, откуда газ пришёл (v·0.1 с назад), пока вес не сменился
        // заметно — сверка по одной фазе (вес 0 у τ = 1.5).
        var gas = new Vector3(0, 0.6f, 0);
        var moving = Grid(Vector3.Zero, gas);
        var t0 = SmokeMath.Flow(0.75, p);
        var t1 = t0 with { Shift0 = t0.Shift0 + 0.1f / p.DetailScale };
        float carried = 0;
        for (int i = 0; i < 200; i++)
        {
            var q = new Vector3(1 + i * 0.031f, 2 + i * 0.017f, 3 + i * 0.023f);
            float now = SmokeMath.DetailDensity(0.5f, q + gas / 0.03f * 0.1f, moving, t1, p);
            float then = SmokeMath.DetailDensity(0.5f, q, moving, t0, p);
            carried = MathF.Max(carried, MathF.Abs(now - then));
        }
        output.WriteLine($"перенос за 0.1 с: наибольшее расхождение {carried:E2}");
        Assert.True(carried < 1e-3f, $"шум не уносится газом: {carried}");
    }
}
