namespace Sword.Engine.Render.Tests;

/// <summary>Числа SMAA без устройства: площади узоров и эталон над синтетическими кадрами.</summary>
public sealed class SmaaMathTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    public void Area_LineWithoutTurn_BlendsNothing(int pattern)
    {
        for (int left = 0; left < 20; left++)
            for (int right = 0; right < 20; right++)
                Assert.Equal(default, SmaaMath.Area(pattern, left, right));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(6)]
    public void Area_StepZ_HalvesBalance_AndStayBelowHalfPixel(int pattern)
    {
        for (int length = 1; length < 40; length++)
        {
            float below = 0, above = 0;
            for (int left = 0; left < length; left++)
            {
                var a = SmaaMath.Area(pattern, left, length - 1 - left);
                Assert.InRange(a.X, 0f, 0.5f);
                Assert.InRange(a.Y, 0f, 0.5f);
                below += a.X;
                above += a.Y;
            }
            // Линия ступени идёт от −½ до +½ через середину: площади по обе стороны края равны, сумма — длина / 4.
            Assert.Equal(below, above, 4);
            Assert.Equal(length / 4f, below + above, 3);
        }
    }

    [Fact]
    public void Apply_FlatAndFullFrameStraightEdges_ReturnFrameUnchanged()
    {
        const int w = 24, h = 16;
        var flat = new byte[w * h * 3];
        Array.Fill(flat, (byte)90);
        Assert.Equal(flat, SmaaMath.Apply(flat, w, h));

        var stripes = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
            stripes[i * 3] = stripes[i * 3 + 1] = stripes[i * 3 + 2] = (byte)((i % w) / 6 % 2 == 0 ? 20 : 230);
        Assert.Equal(stripes, SmaaMath.Apply(stripes, w, h));
    }

    [Fact]
    public void Apply_Staircase_GetsInBetweenValues_SymmetricAcrossStep()
    {
        // Ступени по 8 пикселей: белое ниже ломаной y = 4 + x / 8, чёрное выше.
        const int w = 48, h = 16;
        var stairs = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (y >= 4 + x / 8) stairs[(y * w + x) * 3] = stairs[(y * w + x) * 3 + 1] = stairs[(y * w + x) * 3 + 2] = 255;
        var smooth = SmaaMath.Apply(stairs, w, h);
        int between = 0;
        for (int i = 0; i < smooth.Length; i += 3)
            if (smooth[i] is > 0 and < 255) between++;
        Assert.True(between >= 20, $"полутонов лишь {between}");
        // Ступень в середине (x = 16…23, край над y = 6): у её концов меняется и чёрный сверху, и белый снизу.
        Assert.InRange(smooth[(5 * w + 16) * 3], (byte)1, (byte)254);
        Assert.InRange(smooth[(6 * w + 23) * 3], (byte)1, (byte)254);
    }

    /// <summary>
    /// Края у 45° — диагонали: средняя ошибка линейного цвета против точного покрытия пикселя (белая полуплоскость на чёрном,
    /// шесть сдвигов) — не больше 0.06. Ортогональные узоры давали тут 0.07…0.10: каждую ступень 1:1 они ведут отдельной линией.
    /// </summary>
    [Theory]
    [InlineData(40.37)]
    [InlineData(50.37)]
    [InlineData(55.37)]
    public void Apply_StraightEdgeNear45_CloseToExactCoverage(double degrees)
    {
        const int w = 96, h = 96, s = 16;
        double a = degrees * Math.PI / 180, nx = -Math.Sin(a), ny = Math.Cos(a), error = 0;
        int count = 0;
        foreach (double offset in new[] { 0.0, 0.17, 0.33, 0.5, 0.71, 0.9 })
        {
            double c = nx * w / 2 + ny * h / 2 + offset;
            var image = new byte[w * h * 3];
            var coverage = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = nx * (x + 0.5) + ny * (y + 0.5) > c ? (byte)255 : (byte)0;
                    image[(y * w + x) * 3] = image[(y * w + x) * 3 + 1] = image[(y * w + x) * 3 + 2] = v;
                    int k = 0;
                    for (int sy = 0; sy < s; sy++)
                        for (int sx = 0; sx < s; sx++)
                            if (nx * (x + (sx + 0.5) / s) + ny * (y + (sy + 0.5) / s) > c) k++;
                    coverage[y * w + x] = k / (double)(s * s);
                }
            var smooth = SmaaMath.Apply(image, w, h);
            for (int y = 3; y < h - 3; y++)
                for (int x = 3; x < w - 3; x++)
                {
                    int p = y * w + x;
                    if (coverage[p] is <= 0 or >= 1) continue;
                    error += Math.Abs(FilmicTonemap.DecodeSrgb(smooth[p * 3] / 255f) - coverage[p]);
                    count++;
                }
        }
        Assert.True(error / count <= 0.06, $"средняя ошибка {error / count:F3}");
    }
}
