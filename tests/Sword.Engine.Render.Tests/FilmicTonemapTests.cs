using System.Numerics;
using Xunit.Abstractions;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// CPU-эталон тонмапа Filmic (<see cref="FilmicTonemap"/>): свойства кривой и цепочки и подбор экспозиции по умолчанию. Шейдер
/// сверяется с эталоном на устройстве — <see cref="HdrGpuTests"/>.
/// </summary>
public sealed class FilmicTonemapTests(ITestOutputHelper output)
{
    private static Vector3 Gray(float v) => new(v);

    /// <summary>Байт прежнего вывода сцены без HDR: <c>pow(saturate(c), 1/2.2)</c> в R8G8B8A8_UNorm.</summary>
    private static byte GammaByte(float v) => (byte)MathF.Round(MathF.Pow(Math.Clamp(v, 0, 1), 1 / 2.2f) * 255);

    [Fact]
    public void Black_StaysBlack()
    {
        var d = FilmicTonemap.Display(Vector3.Zero, Renderer.DefaultExposure);
        Assert.True(d.X < 1e-6f && d.Y < 1e-6f && d.Z < 1e-6f, $"чёрный → {d}");
        Assert.Equal((0, 0, 0), FilmicTonemap.DisplayBytes(Vector3.Zero, Renderer.DefaultExposure));
    }

    [Fact]
    public void Gray_MonotoneAndStaysGray()
    {
        float previous = -1;
        for (float stop = -14; stop <= 14; stop += 0.05f)
        {
            float x = MathF.Pow(2, stop);
            var d = FilmicTonemap.Display(Gray(x), Renderer.DefaultExposure);
            Assert.True(d.X >= previous, $"не монотонна: 2^{stop} → {d.X} после {previous}");
            // Серый остаётся серым: матрицы цепочки переводят белый в белый (синяя коррекция UE — в пределах её таблиц).
            Assert.True(MathF.Abs(d.X - d.Y) < 2e-3f && MathF.Abs(d.Y - d.Z) < 2e-3f, $"серый 2^{stop} окрасился: {d}");
            previous = d.X;
        }
        Assert.True(previous <= 1);
    }

    [Fact]
    public void MidGray_MatchesPreviousGammaByte_DefaultExposure()
    {
        // Средний серый 0.18: байт после «экспозиция → Filmic → sRGB» — тот же, что прежний вывод pow(0.18, 1/2.2) (117).
        byte before = GammaByte(0.18f);
        var (r, g, b) = FilmicTonemap.DisplayBytes(Gray(0.18f), Renderer.DefaultExposure);
        Assert.Equal(117, before);
        Assert.InRange(r - before, -2, 2);
        Assert.Equal((r, r), (g, b));

        // Подбор: экспозиция, при которой 0.18 выходит ровно в pow(0.18, 1/2.2) (бисекция по CPU-эталону).
        float target = MathF.Pow(0.18f, 1 / 2.2f);
        float lo = 0.5f, hi = 2f;
        for (int i = 0; i < 60; i++)
        {
            float mid = (lo + hi) / 2;
            if (FilmicTonemap.Display(Gray(0.18f), mid).X < target) lo = mid;
            else hi = mid;
        }
        output.WriteLine($"экспозиция для 0.18 → {target:F5}: {lo:F5}; по умолчанию {Renderer.DefaultExposure}, байт {r} (прежний {before}); " +
            $"при экспозиции 1 — {FilmicTonemap.DisplayBytes(Gray(0.18f), 1).R}; белый 1.0 → {FilmicTonemap.DisplayBytes(Gray(1), Renderer.DefaultExposure).R}");
        Assert.InRange(Renderer.DefaultExposure, lo - 0.01f, lo + 0.01f);
    }

    [Fact]
    public void VeryBright_StaysBelowOne()
    {
        foreach (var c in new[] { Gray(1e3f), new Vector3(1e3f, 10, 1), new Vector3(1e4f, 1e4f, 0), Gray(1e30f), new Vector3(float.PositiveInfinity) })
        {
            var d = FilmicTonemap.Display(c, Renderer.DefaultExposure);
            Assert.True(d.X <= 1 && d.Y <= 1 && d.Z <= 1 && float.IsFinite(d.X + d.Y + d.Z), $"{c} → {d}");
        }
        Assert.True(FilmicTonemap.Display(Gray(1e3f), Renderer.DefaultExposure).X > 0.99f);
    }

    [Fact]
    public void NaN_ShowsBlack()
    {
        var d = FilmicTonemap.Display(new Vector3(float.NaN), Renderer.DefaultExposure);
        Assert.True(d.X < 1e-6f && d.Y < 1e-6f && d.Z < 1e-6f, $"NaN → {d}");
    }

    [Fact]
    public void BrightOrange_WithZeroBlue_GetsBlue()
    {
        // Признак UE-подобного тонмапа (fire-smoke-research §3.10а п.7): ядро пламени с B = 0 на экране получает синий —
        // от десатураций и glow.
        foreach (var c in new[] { new Vector3(12, 0.6f, 0), new Vector3(40, 8, 0), new Vector3(4, 1, 0) })
        {
            var (r, g, b) = FilmicTonemap.DisplayBytes(c, Renderer.DefaultExposure);
            output.WriteLine($"{c} → ({r}, {g}, {b})");
            Assert.True(b > 0, $"{c}: синий 0");
            Assert.True(r >= g && g >= b, $"{c}: порядок каналов сломан ({r}, {g}, {b})");
        }
    }

    [Fact]
    public void SceneFor_InvertsDisplay()
    {
        foreach (var d in new[] { new Vector3(0.33f, 0.29f, 0.27f), new Vector3(0.5f), new Vector3(0.1f, 0.4f, 0.8f), new Vector3(0.9f, 0.2f, 0.1f) })
        {
            var scene = FilmicTonemap.SceneFor(d, Renderer.DefaultExposure);
            var back = FilmicTonemap.Display(scene, Renderer.DefaultExposure);
            Assert.True(Vector3.Distance(back, d) < 1e-4f, $"{d} → {scene} → {back}");
        }
    }

    [Fact]
    public void SceneFor_ZeroChannel_ReachesZero()
    {
        // Канал 0 на экране (фон ночи арены — sRGB (1, 0, 2)/255): сцена в нём — малое положительное число, экран — тот же цвет.
        var d = new Vector3(1 / 255f, 0, 2 / 255f);
        var scene = FilmicTonemap.SceneFor(d, Renderer.DefaultExposure);
        var back = FilmicTonemap.Display(scene, Renderer.DefaultExposure);
        output.WriteLine($"{d} → {scene} → {back}");
        Assert.True(scene.X > 0 && scene.Y >= 0 && scene.Z > 0 && float.IsFinite(scene.X + scene.Y + scene.Z), $"{d} → {scene}");
        Assert.True(Vector3.Distance(back, d) < 1e-4f, $"{d} → {scene} → {back}");
        Assert.Equal((1, 0, 2), FilmicTonemap.DisplayBytes(scene, Renderer.DefaultExposure));
    }

    public static TheoryData<float, float, float, string> OtherCurves => new()
    {
        // Носок > 0.8: 0.18 — на прямом участке; стыки (log10): носок −0.836, плечо −0.654 — плечо правее носка (InvSpan > 0).
        { 0.9f, 0.26f, 0.0f, "носок 0.9" },
        // Носок 0.55 (0.18 на носке), плечо 0.9: плечо −0.606 → 0.121, правее носка −0.390 — InvSpan > 0, доля склейки не перевёрнута.
        { 0.55f, 0.9f, 0.0f, "плечо 0.9" },
        // Обрезка чёрного: носок проводит 0.18 в 0.18 и при BlackClip > 0.
        { 0.55f, 0.26f, 0.1f, "BlackClip 0.1" },
    };

    [Theory]
    [MemberData(nameof(OtherCurves))]
    public void OtherCurves_MidGrayStays_Monotone(float toe, float shoulder, float black, string what)
    {
        // Ветки вывода стыков, которых нет у умолчаний UE: средний серый кривая оставляет на месте (на носке — стыком, на прямой —
        // её сдвигом), серый растёт монотонно и не выходит за 1. Перевёрнутая по ошибке доля склейки дала бы 0.18 → прямую (≈ 0.138).
        var curve = new FilmicCurve(0.88f, toe, shoulder, black, 0.04f);
        Assert.Equal(FilmicTonemap.EncodeSrgb(0.18f), FilmicTonemap.Display(Gray(0.18f), 1, curve).X, 4);
        float previous = -1;
        for (float stop = -14; stop <= 14; stop += 0.05f)
        {
            float v = FilmicTonemap.Display(Gray(MathF.Pow(2, stop)), 1, curve).X;
            Assert.True(float.IsFinite(v) && v >= previous && v <= 1, $"{what}: 2^{stop} → {v} после {previous}");
            previous = v;
        }
    }

    [Theory]
    [InlineData(0f, 0.55f, 0.26f, 0f)]                  // наклон 0
    [InlineData(0.88f, 1.0f, 0.26f, 0f)]                // масштаб носка 1 + BlackClip − Toe = 0
    [InlineData(0.88f, 0.55f, 1.1f, 0f)]                // масштаб плеча 1 + WhiteClip − Shoulder < 0
    [InlineData(0.88f, 0.7f, 0.26f, -0.18f)]            // носок не проводит 0.18: (0.18 + BlackClip) / масштаб = 0
    [InlineData(0.88f, 0.5f, 0.26f, -0.3f)]             // …и < 0
    [InlineData(float.NaN, 0.55f, 0.26f, 0f)]
    [InlineData(0.88f, float.PositiveInfinity, 0.26f, 0f)]
    public void DegenerateCurve_Throws(float slope, float toe, float shoulder, float black)
    {
        var curve = new FilmicCurve(slope, toe, shoulder, black, 0.04f);
        Assert.Throws<ArgumentException>(() => FilmicTonemap.Display(Gray(0.18f), 1, curve));
    }

    [Fact]
    public void DefaultCurve_UeJoints()
    {
        // Умолчания UE: 0.18 — на носке (Toe 0.55 < 0.8), плечо левее носка — доля склейки перевёрнута. Средний серый кривая
        // оставляет на месте; белый 1.0 — на плече: 1.04 − 1.56 / (1 + e^(2·0.88/0.78·(0 − ShoulderMatch))) = 0.7232 (выписано по формуле).
        var d = FilmicTonemap.Display(Gray(0.18f), 1);
        Assert.Equal(FilmicTonemap.EncodeSrgb(0.18f), d.X, 4);
        Assert.Equal(FilmicTonemap.EncodeSrgb(0.7232f), FilmicTonemap.Display(Gray(1), 1).X, 3);
        output.WriteLine($"1.0 → {FilmicTonemap.Display(Gray(1), 1).X:F4} (sRGB), 4 → {FilmicTonemap.Display(Gray(4), 1).X:F4}, " +
            $"16 → {FilmicTonemap.Display(Gray(16), 1).X:F4}");
    }
}
