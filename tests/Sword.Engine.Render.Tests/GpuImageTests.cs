namespace Sword.Engine.Render.Tests;

/// <summary>Цепочка мипов картинки из пикселей (<see cref="GpuImage.BuildMips"/>) без GPU: размеры уровней и ящичный фильтр.</summary>
public sealed class GpuImageTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(5, 3, 3)]
    [InlineData(256, 64, 9)]
    [InlineData(1, 300, 9)]
    public void MipCount_IsFullChainToOnePixel(int width, int height, int mips) => Assert.Equal(mips, GpuImage.MipCount(width, height));

    [Fact]
    public void BuildMips_HalvesEachLevel_AveragesTwoByTwo()
    {
        // 5×3: уровни 5×3 → 2×1 → 1×1; пиксель — номер столбца и строки в R и G, B — 10·x, A — 255
        var rgba = new byte[5 * 3 * 4];
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 5; x++)
            {
                int o = (y * 5 + x) * 4;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = ((byte)x, (byte)y, (byte)(10 * x), 255);
            }
        var levels = GpuImage.BuildMips(5, 3, rgba, GpuImage.MipCount(5, 3));
        Assert.Equal([60, 8, 4], levels.Select(l => l.Length));
        Assert.Equal(rgba, levels[0]);
        // уровень 1, пиксель (1, 0) — среднее столбцов 2, 3 и строк 0, 1: R (2+3+2+3+2)/4 = 3, G (0+0+1+1+2)/4 = 1, B (20+30+20+30+2)/4 = 25
        Assert.Equal(new byte[] { 3, 1, 25, 255 }, levels[1][4..8]);
        // уровень 2 — среднее двух пикселей уровня 1 (края повторяются): R (1+3+1+3+2)/4 = 2
        Assert.Equal(2, levels[2][0]);
        Assert.Equal(255, levels[2][3]);
    }

    [Fact]
    public void BuildMips_RefusesWrongSizeAndTooManyLevels()
    {
        Assert.Throws<ArgumentException>(() => GpuImage.BuildMips(4, 4, new byte[4 * 4 * 4 - 1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuImage.BuildMips(4, 4, new byte[4 * 4 * 4], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuImage.MipCount(0, 4));
    }
}
