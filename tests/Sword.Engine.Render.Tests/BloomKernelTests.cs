namespace Sword.Engine.Render.Tests;

/// <summary>
/// Числа bloom без GPU (<see cref="BloomKernel"/>): ядро нормировано и симметрично, радиусы растут с экраном (гало в долях экрана
/// то же), усиления и порог — как у UE 5.8.
/// </summary>
public sealed class BloomKernelTests
{
    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(3.84f)]
    [InlineData(6.4f)]
    [InlineData(16f)]
    [InlineData(25.6f)]
    [InlineData(38.4f)]
    [InlineData(127f)]
    public void Taps_Normalized_TexelWeightsSymmetricGaussian(float radius)
    {
        var offsets = new float[BloomKernel.MaxRadius + 1];
        var weights = new float[BloomKernel.MaxRadius + 1];
        int count = BloomKernel.Taps(radius, offsets, weights);
        int n = BloomKernel.IntegerRadius(radius);
        Assert.Equal(n + 1, count);
        Assert.Equal(1.0, weights.Take(count).Sum(w => (double)w), 5);

        // Билинейная выборка со сдвигом i + f берёт тексель i с долей 1 − f и i + 1 с долей f: веса текселей −N…N.
        var texel = new double[2 * n + 2];
        for (int k = 0; k < count; k++)
        {
            int i = (int)MathF.Floor(offsets[k]);
            double f = offsets[k] - i;
            texel[i + n] += weights[k] * (1 - f);
            texel[i + n + 1] += weights[k] * f;
        }
        Assert.Equal(0, texel[2 * n + 1], 9);   // за последним текселем ничего
        for (int x = 0; x <= n; x++)
        {
            Assert.Equal(texel[n + x], texel[n - x], 6);
            double expected = texel[n] * BloomKernel.Weight(x, radius);
            Assert.True(Math.Abs(texel[n + x] - expected) <= 1e-6 + 1e-4 * expected, $"тексель {x}: {texel[n + x]} против {expected}");
        }
    }

    [Fact]
    public void Radius_ProportionalToScreen_UpTo4K()
    {
        // Радиус в долях ширины экрана: размер × SizeScale × 0.5 % — 0.6/2/4/20/60/128 %.
        float[] fractions = [0.006f, 0.02f, 0.04f, 0.2f, 0.6f, 1.28f];
        foreach (int width in (int[])[512, 640, 1280, 1920, 2560, 3840])
            for (int s = 0; s < BloomKernel.Stages; s++)
            {
                int level = BloomKernel.LevelSize(width, s);
                Assert.Equal(Math.Max(1, (int)Math.Ceiling(width / Math.Pow(2, s + 1))), level);
                float radius = BloomKernel.Radius(level, s);
                Assert.True(radius < BloomKernel.MaxRadius, $"{width}: ступень {s} упёрлась в предел");
                Assert.Equal(fractions[s], radius / level, 1e-5f);
            }
        // UE по умолчанию режет радиус на 31: на 1920 ступень 6 — 38.4 текселя, у нас не режется.
        Assert.Equal(38.4f, BloomKernel.Radius(BloomKernel.LevelSize(1920, 5), 5), 1e-3f);
    }

    [Fact]
    public void Gains_LikeUe_LinearInIntensity()
    {
        // Оттенки × интенсивность / 6 ступеней: сумма оттенков 0.7951 → при 10 энергия bloom в 1.325 раза больше прошедшего порог.
        Assert.Equal(0.7951f * 10 / 6, BloomKernel.TotalGain(10), 1e-5f);
        Assert.Equal(0.3465f * 10 / 6, BloomKernel.Gain(0, 10), 1e-6f);
        for (int s = 0; s < BloomKernel.Stages; s++) Assert.Equal(2 * BloomKernel.Gain(s, 10), BloomKernel.Gain(s, 20), 1e-6f);
    }

    [Fact]
    public void Amount_Threshold()
    {
        Assert.Equal(0f, BloomKernel.Amount(0.99f, 1f));
        Assert.Equal(0f, BloomKernel.Amount(1f, 1f));
        Assert.Equal(0.5f, BloomKernel.Amount(2f, 1f), 1e-6f);
        Assert.Equal(1f, BloomKernel.Amount(3f, 1f));
        Assert.Equal(1f, BloomKernel.Amount(1000f, 1f));
        Assert.Equal(1f, BloomKernel.Amount(0f, -1f));   // порог −1 — всё
        var defaults = BloomSettings.Default;
        Assert.Equal((true, 10f, 1f, true), (defaults.Enabled, defaults.Intensity, defaults.Threshold, defaults.Active));
        Assert.False(BloomSettings.Off.Active);
        Assert.False((defaults with { Intensity = 0 }).Active);
    }
}
