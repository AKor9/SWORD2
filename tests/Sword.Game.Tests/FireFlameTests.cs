using System.Numerics;
using Sword.Engine.Render;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Формула пламени без GPU (<see cref="FlameMath"/>, <c>MI_Torch4</c> LIVE-2): числа FSR §3.7, ядро ярче 1 и синий канал после тонмапа
/// при Color1.B = 0, альфа в [0, 1] и прозрачность вне ρ, непрерывность фаз flow-map, туман, параметры глубины камеры.
/// </summary>
public sealed class FireFlameTests(ITestOutputHelper output)
{
    private static readonly FlameParams P = FlameParams.Torch4;

    [Fact]
    public void Emission_MatchesLive2Numbers()
    {
        // FSR §3.7: ρ = 1, шум 0.5 — k = 2.5, (24.9, 1.2, 0); шум 1 — k 20, ≈(199, 9.2, 0); ρ = 0.5 — k ≤ 0.31, не больше ≈(3.2, 0.18, 0.03).
        var core = FlameMath.Emission(0.5f, 1, P);
        Assert.Equal(24.9f, core.X, 0.1f);
        Assert.Equal(1.2f, core.Y, 0.05f);
        Assert.Equal(0f, core.Z);
        var max = FlameMath.Emission(1, 1, P);
        Assert.Equal(199f, max.X, 0.5f);
        Assert.Equal(9.2f, max.Y, 0.1f);
        var half = FlameMath.Emission(1, 0.5f, P);
        Assert.True(half.X <= 3.2f && half.Y <= 0.19f && half.Z <= 0.04f, $"ρ 0.5: {half}");
        // ρ = 0.3 при шуме 0.5 — почти Color2.
        Assert.True(Vector3.Distance(FlameMath.Emission(0.5f, 0.3f, P), P.Colour2) < 0.02f);
    }

    [Fact]
    public void TailCut_TailTransparent_FlameUntouched_OffIsMiTorch4()
    {
        // Хвост (ρ 0.2: k ≈ 0) — прозрачен; пламя (k ≥ GlowCut) — альфа как без среза; срез 0 — альфа = ρ, как MI_Torch4.
        var off = P with { GlowCut = 0 };
        var uv = new Vector2(0.5f);
        var tail = new Vector4(0, 0, 0.2f, 1);
        Assert.True(FlameMath.K(0.5f, 0.2f, P) < 0.01f * P.GlowCut);
        Assert.True(FlameMath.Pixel(tail, 0.5f, 0.5f, 0, uv, 1, 0, Vector3.Zero, P).W < 0.01f * 0.2f);
        Assert.Equal(0.2f, FlameMath.Pixel(tail, 0.5f, 0.5f, 0, uv, 1, 0, Vector3.Zero, off).W, 1e-6f);
        var core = new Vector4(0, 0, 0.9f, 1);
        Assert.True(FlameMath.K(0.6f, 0.9f, P) >= P.GlowCut);
        Assert.Equal(FlameMath.Pixel(core, 0.6f, 0.6f, 0, uv, 1, 0, Vector3.Zero, off), FlameMath.Pixel(core, 0.6f, 0.6f, 0, uv, 1, 0, Vector3.Zero, P));
        // Между — линейно по k: половина среза — половина альфы.
        Assert.Equal(0.5f, FlameMath.Glow(0.5f * P.GlowCut, P), 1e-6f);
    }

    [Fact]
    public void Core_BrighterThanOne_BlueAfterFilmic()
    {
        // Ядро (ρ 1, шум 0.5 и 1) в HDR > 1 — даёт bloom; после экспозиции и Filmic синий канал > 0 при Color1.B = 0 (FSR §3.10а п.7).
        foreach (float n in new[] { 0.5f, 0.75f, 1f })
        {
            var hdr = FlameMath.Emission(n, 1, P);
            Assert.True(hdr.X > 1, $"шум {n}: {hdr}");
            var (r, g, b) = FilmicTonemap.DisplayBytes(hdr, Renderer.DefaultExposure);
            output.WriteLine($"шум {n}: HDR ({hdr.X:0.0}, {hdr.Y:0.00}, {hdr.Z:0.00}) → sRGB ({r}, {g}, {b})");
            Assert.True(b > 0, $"шум {n}: синий 0 после тонмапа ({r}, {g}, {b})");
        }
    }

    [Fact]
    public void Alpha_WithinUnit_TransparentWithoutDensity()
    {
        var random = new Random(15);
        for (int i = 0; i < 20000; i++)
        {
            var field = new Vector4(random.NextSingle() * 20 - 10, random.NextSingle() * 20 - 10, random.NextSingle() * 3 - 1, 1);
            var uv = new Vector2(random.NextSingle(), random.NextSingle());
            float fade = random.NextSingle() * 1.2f;
            var px = FlameMath.Pixel(field, random.NextSingle(), random.NextSingle(), random.NextSingle(), uv, Math.Min(fade, 1), random.NextSingle(),
                new Vector3(0.1f), P);
            Assert.True(float.IsFinite(px.X) && float.IsFinite(px.Y) && float.IsFinite(px.Z) && float.IsFinite(px.W), $"NaN: {px}");
            Assert.InRange(px.W, 0f, 1f);
            Assert.True(px.X >= 0 && px.Y >= 0 && px.Z >= 0, $"отрицательный цвет {px}");
            if (field.Z <= 0) Assert.Equal(Vector4.Zero, px);
        }
        // Край площадки — прозрачно (синусная маска), вне глубины — прозрачно.
        Assert.Equal(0f, FlameMath.Alpha(1, new Vector2(0, 0.5f), 1, P));
        Assert.Equal(0f, FlameMath.Alpha(1, new Vector2(0.5f, 0.5f), 0, P));
        Assert.Equal(1f, FlameMath.Alpha(1, new Vector2(0.5f, 0.5f), 1, P), 1e-6f);
    }

    [Fact]
    public void Flow_TwoPhases_ContinuousAcrossWrap()
    {
        var uv = new Vector2(0.4f, 0.6f);
        var wo = new Vector2(0.2f, -0.3f);
        var vel = new Vector2(3, -8);   // y зажат в −5
        // Фаза 0.5 — только A без сдвига; 0 и почти 1 — только B без сдвига: переход через заворот фазы без скачка.
        var (a, _, w) = FlameMath.Flow(uv, wo, vel, 0.5f, P);
        var still = (P.FlowTile * (uv + wo) + Vector2.One) * 0.5f;
        Assert.Equal(0f, w);
        Assert.True(Vector2.Distance(a, still) < 1e-6f);
        var (_, b0, w0) = FlameMath.Flow(uv, wo, vel, 0f, P);
        var (_, b1, w1) = FlameMath.Flow(uv, wo, vel, 0.9999f, P);
        Assert.Equal(1f, w0);
        Assert.True(w1 > 0.999f);
        Assert.True(Vector2.Distance(b0, still) < 1e-6f && Vector2.Distance(b1, still) < 1e-3f, $"B: {b0} / {b1}, без сдвига {still}");
        // Сдвиг A на фазе 1/4: (ph − 0.5)·vel, vel = −clamp(s.rg)·0.5 — (0.25·1.5, −0.25·2.5)/2.
        var (aq, _, _) = FlameMath.Flow(uv, wo, vel, 0.25f, P);
        Assert.True(Vector2.Distance(aq - still, new Vector2(0.25f * 1.5f, -0.25f * 2.5f) * 0.5f) < 1e-6f, $"{aq - still}");
    }

    [Fact]
    public void Phase_ByFrameTime_StableForLongRuns()
    {
        Assert.Equal(0.3f, FlameMath.Phase(0.1, 3), 1e-6f);
        Assert.Equal(0.5f, FlameMath.Phase(3600.5 / 3, 3), 1e-6f);
        Assert.Equal(FlameMath.Phase(12.345, 3), FlameMath.Phase(12.345, 3));
        Assert.InRange(FlameMath.Phase(1e7 + 1 / 3.0, 3), 0f, 1f);
    }

    [Fact]
    public void Fog_LinearLikeShade_HeightFogThickerLow()
    {
        var fog = new SceneFog(new Vector3(0.1f), SceneEnvironment.FogStart, SceneEnvironment.FogRange);
        var eye = new Vector3(0, 6, 0);
        Assert.Equal(0f, fog.Factor(eye, eye + new Vector3(10, 0, 0)));
        Assert.Equal(0.5f, fog.Factor(eye, eye + new Vector3(0, 0, 95)), 1e-6f);
        Assert.Equal(1f, fog.Factor(eye, eye + new Vector3(500, 0, 0)));
        Assert.Equal(0f, SceneFog.None.Factor(eye, new Vector3(1e6f, 0, 0)));

        // Высотный: прибавка к линейному, у земли гуще, чем выше на том же расстоянии; горизонтальный луч на высоте y — ровно
        // 1 − e^(−плотность·e^(−спад·y)·d); вверх от камеры — реже, чем вниз.
        var day = SceneEnvironment.Day;
        var height = fog with { HeightDensity = day.FogDensity, HeightFalloff = day.FogFalloff };
        var low = new Vector3(60, 0, 0);
        Assert.True(height.Factor(eye, low) > fog.Factor(eye, low));
        Assert.True(height.Factor(eye, low) > height.Factor(eye, low with { Y = 12 }));
        Assert.True(height.Factor(eye, new Vector3(0, 0, 20)) is > 0f and < 0.1f);
        var flat = new Vector3(20, 1, 0);
        Assert.Equal(1 - MathF.Exp(-day.FogDensity * MathF.Exp(-day.FogFalloff) * 20), height.Factor(flat with { X = 0 }, flat), 1e-5f);
        Assert.True(height.Factor(eye, eye + new Vector3(0, 10, 10)) < height.Factor(eye, eye + new Vector3(0, -6, 10)));
        // Луч вдоль пола из камеры у земли в 60 м — дымка заметна, но не стена.
        Assert.InRange(height.Factor(eye, low) - fog.Factor(eye, low), 0.05f, 0.25f);
        // Полный туман — цвет тумана с альфой пламени (вклад поверх туманной сцены тот же, что у непрозрачного).
        var px = FlameMath.Pixel(new Vector4(0, 0, 1, 1), 1, 1, 0.5f, new Vector2(0.5f), 1, 1, fog.Colour, P);
        Assert.True(Vector3.Distance(new Vector3(px.X, px.Y, px.Z), fog.Colour * px.W) < 1e-6f);
    }

    [Fact]
    public void ViewDepthParams_FromCamera_EqualProjection()
    {
        var camera = new RtsCamera { Yaw = 0.6f, Pitch = 0.95f, Distance = 3 };
        camera.Rebuild(1280, 720);
        var forward = -Vector3.Transform(Vector3.UnitZ, camera.Rotation);
        var k = FlameMath.ViewDepthParams(camera.ViewProj, camera.Eye, forward);
        var expected = TransparencyMath.ViewDepthParams(camera.Projection);
        Assert.Equal(expected.X, k.X, 1e-5f);
        Assert.Equal(expected.Y, k.Y, 1e-5f);
    }
}
