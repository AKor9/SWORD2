using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Render;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Свет факела без GPU: параметры модуля = пресет факела рендера (его освещённость на GPU сверяют тесты рендера), освещённость на 0.5 и
/// 1.5 м — формула «экспоненты» UE с числами LIVE-2, LF в [0, 1] и не ярче формулы; статистика LF по времени и направлениям на CPU-эталоне
/// с текстурами пака — среднее, доля насыщения и время мерцания FSR §3.9.
/// </summary>
public sealed class FireLightTests(ITestOutputHelper output)
{
    private static readonly FireLightParams P = FireLightParams.Torch;

    [Fact]
    public void Illuminance_AtHalfAndOneAndHalfMetres_IsExponentFormula()
    {
        var at = new Vector3(1, 2, 3);
        var light = P.Light(at, 1);
        // Тот же источник, что пресет факела рендера (GPU = PointLightMath — PointLightsGpuTests), и рассеяние в объёмах 0.15.
        Assert.Equal(PointLight.Torch(at), light);
        Assert.Equal(0.15f, light.VolumetricScattering);
        foreach (float d in new[] { 0.5f, 1.5f })
        {
            // Поверхность под источником, нормаль к нему: N·L = 1. Альбедо × цвет × 100 × (1 − d²/R²)^400 / π.
            var lit = PointLightMath.Diffuse(light, at - new Vector3(0, d, 0), Vector3.UnitY);
            float expected = 100f * (float)Math.Pow(1 - d * d / 400.0, 400) / MathF.PI;
            output.WriteLine($"d {d} м: ({lit.X:0.0000}, {lit.Y:0.0000}, {lit.Z}) против {expected:0.0000}·(1, 0.038204, 0)");
            Assert.Equal(expected, lit.X, expected * 1e-5f);
            Assert.Equal(expected * 0.038204f, lit.Y, expected * 1e-5f);
            Assert.Equal(0f, lit.Z);
            // ≈ exp(−(d/1 м)²): 0.779 на 0.5 м, 0.105 на 1.5 м (FSR §2.3).
            Assert.Equal(MathF.Exp(-d * d), expected * MathF.PI / 100f, 0.002f);
            // Под углом — × cos, за радиусом — 0.
            var slanted = PointLightMath.Diffuse(light, at - new Vector3(0, d, 0), Vector3.Normalize(new Vector3(1, 1, 0)));
            Assert.Equal(expected / MathF.Sqrt(2), slanted.X, expected * 1e-5f);
        }
        Assert.Equal(Vector3.Zero, PointLightMath.Diffuse(light, at - new Vector3(0, 20.01f, 0), Vector3.UnitY));
        // Гаснущий огонь: интенсивность × доля — освещённость тоже.
        var dim = PointLightMath.Diffuse(P.Light(at, 0.25f), at - new Vector3(0, 0.5f, 0), Vector3.UnitY);
        Assert.Equal(0.25f * PointLightMath.Diffuse(light, at - new Vector3(0, 0.5f, 0), Vector3.UnitY).X, dim.X, 1e-4f);
    }

    private static (TextureTexels A, TextureTexels B) PakTextures()
    {
        using var pak = ContentLocator.Open();
        using var a = pak.OpenRead(FireLight.NoisePathA);
        var texelsA = TextureTexels.Read(a, FireLight.NoisePathA);
        using var b = pak.OpenRead(FireLight.NoisePathB);
        return (texelsA, TextureTexels.Read(b, FireLight.NoisePathB));
    }

    /// <summary>Направления, равномерные по сфере (спираль Фибоначчи).</summary>
    private static Vector3[] Directions(int n)
    {
        var dirs = new Vector3[n];
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float y = 1 - 2 * (i + 0.5f) / n, r = MathF.Sqrt(1 - y * y);
            dirs[i] = new Vector3(MathF.Cos(golden * i) * r, y, MathF.Sin(golden * i) * r);
        }
        return dirs;
    }

    [Fact]
    [Trait("Category", "Content")]
    public void LightFunction_OnPakTextures_MeanSaturationFlicker()
    {
        // LF = min(A + B, 1): A — T_Noise3 (sRGB) по UV·0.2 с качанием 1 Гц, B — T_NoiseTemplate2 по UV·0.001, бегущий t·(0.03, 0.01).
        // Числа FSR §3.9 (lf_stats) — для независимых A и B по всей текстуре: среднее A + B 0.668, P(A + B > 1) 0.171. Та же статистика по
        // текстурам пака — точно (A по всем текселям, B — доля текселей выше 1 − a): совпадение ±0.01 значит «те же текстуры».
        var (a, b) = PakTextures();
        var sortedB = new float[b.Width * b.Height];
        for (int i = 0; i < sortedB.Length; i++) sortedB[i] = b.Texel(i % b.Width, i / b.Width);
        Array.Sort(sortedB);
        double meanA = 0, whole = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                float av = a.Texel(x, y);
                meanA += av;
                int k = Array.BinarySearch(sortedB, 1 - av + 1e-7f);
                whole += sortedB.Length - (k < 0 ? ~k : k);
            }
        meanA /= a.Width * a.Height;
        whole /= (double)a.Width * a.Height * sortedB.Length;
        double wholeMean = meanA + sortedB.Average();
        output.WriteLine($"по всей текстуре: среднее A {meanA:0.000}, A + B {wholeMean:0.000}, P(A + B > 1) {whole:P1}");
        Assert.Equal(0.668, wholeMean, 0.01);
        Assert.Equal(0.171, whole, 0.01);

        // Лампа: 256 направлений по сфере × 100 с (период узора — заворот B по v) шагом 0.05 с. A у лампы — лишь квадрат 0.2 × 0.2 текстуры
        // (≈51² текселей у угла, с качанием ±5 текселей), B — одна линия по текстуре: среднее — то же ±0.05, насыщение — свойство этого
        // квадрата (его верхний хвост), по всей текстуре не сверяется: только 0 < доля < доли всей текстуры.
        var dirs = Directions(256);
        const double Period = 100, Dt = 0.05;
        int times = (int)(Period / Dt);
        double sum = 0, lfSum = 0;
        long saturated = 0, n = 0;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int t = 0; t < times; t++)
        {
            var f = P.Function(0, 0, t * Dt);
            foreach (var dir in dirs)
            {
                float s = PointLightMath.LightFunctionSum(f, a, b, dir);
                float lf = PointLightMath.LightFunctionValue(s, 0, f.FadeDistance, f.DisabledBrightness);
                Assert.InRange(lf, 0f, 1f);
                Assert.Equal(Math.Clamp(s, 0f, 1f), lf, 1e-6f);
                sum += s;
                lfSum += lf;
                if (s > 1) saturated++;
                (lo, hi) = (MathF.Min(lo, s), MathF.Max(hi, s));
                n++;
            }
        }
        double mean = sum / n, lfMean = lfSum / n, share = (double)saturated / n;
        output.WriteLine($"лампа: A + B среднее {mean:0.000}, {lo:0.000}…{hi:0.000}, насыщение {share:P1}; LF (≤ 1) среднее {lfMean:0.000}");
        Assert.InRange(mean, 0.668 - 0.05, 0.668 + 0.05);
        Assert.InRange(share, 1e-3, whole);
        Assert.True(lfMean < mean, "зажим до 1 не снизил среднее");

        // Время мерцания: автокорреляция LF в точке по времени (шаг 0.01 с, 100 с) — у B 0.96 / 0.42 / −0.01 на 0.02 / 0.1 / 0.2 с, ≈4
        // пересечения среднего в секунду (FSR §3.9). A качается с 1 Гц и добавляет медленную часть, зажим — срезает пики: допуски шире.
        const double Fine = 0.01;
        int steps = (int)(Period / Fine);
        double r02 = 0, r10 = 0, r20 = 0, crossings = 0;
        var probe = new[] { -Vector3.UnitY, Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f)), Vector3.UnitX, Vector3.Normalize(new Vector3(-0.6f, 0.2f, -0.7f)) };
        var series = new double[steps];
        foreach (var dir in probe)
        {
            for (int t = 0; t < steps; t++)
            {
                var f = P.Function(0, 0, t * Fine);
                series[t] = Math.Clamp(PointLightMath.LightFunctionSum(f, a, b, dir), 0f, 1f);
            }
            r02 += Autocorrelation(series, 2) / probe.Length;
            r10 += Autocorrelation(series, 10) / probe.Length;
            r20 += Autocorrelation(series, 20) / probe.Length;
            double m = series.Average();
            int c = 0;
            for (int t = 1; t < steps; t++) if ((series[t] - m) * (series[t - 1] - m) < 0) c++;
            crossings += c / Period / probe.Length;
        }
        output.WriteLine($"LF в точке: автокорреляция 0.02 с {r02:0.00}, 0.1 с {r10:0.00}, 0.2 с {r20:0.00}; пересечений среднего {crossings:0.0}/с");
        Assert.True(r02 > 0.85, $"0.02 с: {r02}");
        Assert.InRange(r10, 0.2, 0.7);
        Assert.True(r20 < 0.35, $"0.2 с: {r20}");
        Assert.InRange(crossings, 2.5, 6);
    }

    private static double Autocorrelation(double[] x, int lag)
    {
        double m = x.Average(), v = 0, c = 0;
        for (int i = 0; i < x.Length; i++) v += (x[i] - m) * (x[i] - m);
        for (int i = 0; i + lag < x.Length; i++) c += (x[i] - m) * (x[i + lag] - m);
        return c / (x.Length - lag) / (v / x.Length);
    }
}
