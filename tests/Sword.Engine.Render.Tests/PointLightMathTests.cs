using System.Numerics;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// CPU-эталон точечного света (<see cref="PointLightMath"/>) против формул UE 5.8, записанных здесь заново: затухание обоих режимов на
/// d = 0, R/2, R, за R; угасание по дальности показа; UV light function — цепочка UE (оси UE, перестановка zyx, сферические углы) и
/// шесть осей; значение LF в пределах 0…1 и переход к яркости без LF; сдвиги по времени.
/// </summary>
public sealed class PointLightMathTests
{
    [Theory]
    [InlineData(8f)]
    [InlineData(400f)]
    public void Exponent_AtKeyDistances(float exponent)
    {
        const float R = 20;
        Assert.Equal(1f, PointLightMath.Attenuation(0, R, LightFalloff.Exponent, exponent));
        Assert.Equal(MathF.Pow(0.75f, exponent), PointLightMath.Attenuation(R / 2, R, LightFalloff.Exponent, exponent), 1e-6f);
        Assert.Equal(0f, PointLightMath.Attenuation(R, R, LightFalloff.Exponent, exponent));
        Assert.Equal(0f, PointLightMath.Attenuation(R * 1.5f, R, LightFalloff.Exponent, exponent));
        // Экспонента 400 при R 20 м — почти гауссиана exp(−(d/1 м)²) (исследование §3.9): на 1 м — 0.367.
        if (exponent == 400) Assert.Equal(MathF.Exp(-1), PointLightMath.Attenuation(1, R, LightFalloff.Exponent, exponent), 2e-3f);
        // Показатель 0 — ровный свет до края и 0 за ним (без pow(0, 0)).
        Assert.Equal(1f, PointLightMath.Attenuation(R * 0.99f, R, LightFalloff.Exponent, 0));
        Assert.Equal(0f, PointLightMath.Attenuation(R, R, LightFalloff.Exponent, 0));
    }

    [Fact]
    public void InverseSquared_AtKeyDistances()
    {
        const float R = 10, Bias = 1e-4f;
        Assert.Equal(1 / Bias, PointLightMath.Attenuation(0, R, LightFalloff.InverseSquared, 8), 1e-2f);
        float half = MathF.Pow(1 - 1f / 16, 2) / (R * R / 4 + Bias);
        Assert.Equal(half, PointLightMath.Attenuation(R / 2, R, LightFalloff.InverseSquared, 8), 1e-7f);
        Assert.Equal(0f, PointLightMath.Attenuation(R, R, LightFalloff.InverseSquared, 8));
        Assert.Equal(0f, PointLightMath.Attenuation(2 * R, R, LightFalloff.InverseSquared, 8));
        // Близко к источнику и далеко от края — чистый обратный квадрат.
        Assert.Equal(1 / (1 + Bias), PointLightMath.Attenuation(1, 1000, LightFalloff.InverseSquared, 8), 1e-5f);
    }

    [Fact]
    public void DistanceFade_ZeroBeyond_LinearInBand()
    {
        Assert.Equal(1f, PointLightMath.DistanceFade(1e6f, 0, 5));   // без предела
        Assert.Equal(1f, PointLightMath.DistanceFade(6.9f, 12, 5));
        Assert.Equal(1f, PointLightMath.DistanceFade(7, 12, 5));
        Assert.Equal(0.5f, PointLightMath.DistanceFade(9.5f, 12, 5), 1e-6f);
        Assert.Equal(0.2f, PointLightMath.DistanceFade(11, 12, 5), 1e-6f);
        Assert.Equal(0f, PointLightMath.DistanceFade(12, 12, 5));
        Assert.Equal(0f, PointLightMath.DistanceFade(13, 12, 5));
        // Без полосы — ступенька у предела.
        Assert.Equal(1f, PointLightMath.DistanceFade(11.99f, 12, 0));
        Assert.Equal(0f, PointLightMath.DistanceFade(12, 12, 0));
    }

    /// <summary>UV по UE: вектор в осях UE (x, y, z) = SWORD2 (z, −x, y), перестановка zyx, ((atan2(y, x) + π)/2π, acos(z)/π).</summary>
    private static Vector2 UeUv(Vector3 sword)
    {
        var ue = new Vector3(sword.Z, -sword.X, sword.Y);
        var s = new Vector3(ue.Z, ue.Y, ue.X);
        return new((MathF.Atan2(s.Y, s.X) + MathF.PI) / (2 * MathF.PI), MathF.Acos(s.Z) / MathF.PI);
    }

    [Fact]
    public void LightFunctionUv_MatchesUeChain_AndSixAxes()
    {
        var rng = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1));
            var uv = PointLightMath.LightFunctionUv(d);
            var ue = UeUv(d);
            Assert.True(Vector2.Distance(uv, ue) < 1e-5f, $"{d}: {uv} против UE {ue}");
        }
        // Экватор (плоскость XY SWORD2): +X — u ¼, −X — ¾, +Y (вверх) — ½, −Y (вниз) — шов 0 | 1; v — ½.
        Assert.Equal(new Vector2(0.25f, 0.5f), PointLightMath.LightFunctionUv(Vector3.UnitX));
        Assert.Equal(new Vector2(0.75f, 0.5f), PointLightMath.LightFunctionUv(-Vector3.UnitX));
        Assert.Equal(new Vector2(0.5f, 0.5f), PointLightMath.LightFunctionUv(Vector3.UnitY));
        var down = PointLightMath.LightFunctionUv(-Vector3.UnitY);
        Assert.True(down.X is 0 or 1 && down.Y == 0.5f, $"−Y: {down}");
        // Полюса — ±Z SWORD2 (вперёд UE): v 0 и 1.
        Assert.Equal(0f, PointLightMath.LightFunctionUv(Vector3.UnitZ).Y);
        Assert.Equal(1f, PointLightMath.LightFunctionUv(-Vector3.UnitZ).Y);
    }

    [Fact]
    public void LightFunctionValue_InZeroOne_FadesToDisabled()
    {
        for (float sum = -0.5f; sum <= 2.5f; sum += 0.125f)
            for (float view = 0; view <= 1200; view += 50)
            {
                float v = PointLightMath.LightFunctionValue(sum, view, 1000, 0.5f);
                Assert.InRange(v, 0f, 1f);
            }
        Assert.Equal(1f, PointLightMath.LightFunctionValue(1.37f, 10, 1000, 0.5f));    // сумма двух текстур насыщается
        Assert.Equal(0.4f, PointLightMath.LightFunctionValue(0.4f, 10, 1000, 0.5f), 1e-6f);
        Assert.Equal(0.4f, PointLightMath.LightFunctionValue(0.4f, 800, 1000, 0.5f), 1e-6f);   // полоса — последние 20 %
        Assert.Equal(0.45f, PointLightMath.LightFunctionValue(0.4f, 900, 1000, 0.5f), 1e-6f);
        Assert.Equal(0.5f, PointLightMath.LightFunctionValue(0.4f, 1000, 1000, 0.5f));
        Assert.Equal(0.5f, PointLightMath.LightFunctionValue(1.8f, 5000, 1000, 0.5f));
        // Дальность 0 — всюду яркость без LF, как у шейдера (saturate из NaN — 0), и в том числе в самой камере.
        Assert.Equal(0.3f, PointLightMath.LightFunctionValue(0.9f, 0, 0, 0.3f));
        Assert.Equal(0.3f, PointLightMath.LightFunctionValue(0.9f, 7, 0, 0.3f));
    }

    [Fact]
    public void LightFunctionOffsets_WobbleAndWrap()
    {
        var f = new LightFunction(0, 0) { Time = 0.25 };   // sin(2π·¼) = 1
        Assert.Equal(f.WobbleA.X, f.OffsetA.X, 1e-6f);
        Assert.Equal(f.WobbleA.Y, f.OffsetA.Y, 1e-6f);
        Assert.Equal(new Vector2(0.0075f, 0.0025f).X, f.OffsetB.X, 1e-6f);
        var late = f with { Time = 1e6 + 0.5 };   // долгая игра: сдвиг B — по модулю 1, без потери точности
        Assert.InRange(late.OffsetB.X, 0f, 1f);
        Assert.Equal((float)((1e6 + 0.5) * f.VelocityB.X % 1.0), late.OffsetB.X, 1e-4f);
        Assert.Equal(0f, late.OffsetA.X, 1e-4f);
        // Числа M_LightFunction1 LIVE-2 — умолчания.
        Assert.Equal((0.2f, 0.001f, 1000f, 0.5f), (f.ScaleA, f.ScaleB, f.FadeDistance, f.DisabledBrightness));
    }

    [Fact]
    public void Diffuse_LambertOverPi_ZeroBehindAndBeyond()
    {
        var light = new PointLight(new Vector3(0, 2, 0), new Vector3(1, 0.5f, 0.25f), 10, 5, LightFalloff.Exponent, 8);
        var up = Vector3.UnitY;
        // Прямо под источником: N·L = 1.
        var below = PointLightMath.Diffuse(light, Vector3.Zero, up);
        float att = MathF.Pow(1 - 4f / 25, 8);
        Assert.Equal(10 * att / MathF.PI, below.X, 1e-5f);
        Assert.Equal(below.X * 0.5f, below.Y, 1e-6f);
        // В стороне: N·L = cos угла.
        var p = new Vector3(2, 0, 0);
        float d = MathF.Sqrt(8);
        Assert.Equal(10 * MathF.Pow(1 - 8f / 25, 8) * (2 / d) / MathF.PI, PointLightMath.Diffuse(light, p, up).X, 1e-5f);
        Assert.Equal(Vector3.Zero, PointLightMath.Diffuse(light, Vector3.Zero, -up));   // свет со спины
        Assert.Equal(Vector3.Zero, PointLightMath.Diffuse(light, new Vector3(6, 0, 0), up));   // за радиусом
        Assert.Equal(Vector3.Zero, PointLightMath.Diffuse(light, light.Position, up));   // в самом источнике
        // Угасание и LF — множители.
        Assert.Equal(below * 0.25f, PointLightMath.Diffuse(light, Vector3.Zero, up, fade: 0.5f, lightFunction: 0.5f));
    }
}
