using System.IO.Compression;
using Sword.Bake.Texture;
using Sword.Engine.Formats;

namespace Sword.Bake.Tests;

/// <summary>Текстуры пекарни: чтение PNG, BC4 и формат .swtex (перенесено из тестов SWORD 2eab13b); серый sRGB, два канала как есть.</summary>
public sealed class TextureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-texture-tests", Guid.NewGuid().ToString("N"));

    public TextureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>PNG из рядов байт: каждому ряду — свой фильтр (по кругу 0…4), чтобы проверить все.</summary>
    private string WritePng(string name, int width, int height, int colourType, int depth, Func<int, int, int, int> sample)
    {
        int samples = colourType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        int bpp = samples * depth / 8, stride = width * bpp;
        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            rows[y] = new byte[stride];
            for (int x = 0; x < width; x++)
                for (int c = 0; c < samples; c++)
                {
                    int v = sample(x, y, c), o = (x * samples + c) * depth / 8;
                    if (depth == 16) { rows[y][o] = (byte)(v >> 8); rows[y][o + 1] = (byte)v; }
                    else rows[y][o] = (byte)v;
                }
        }
        using var raw = new MemoryStream();
        var prev = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = y % 5;
            raw.WriteByte((byte)filter);
            var cur = rows[y];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int predicted = filter switch
                {
                    1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Math.Abs(a + b - c - a) <= Math.Abs(a + b - c - b) && Math.Abs(a + b - c - a) <= Math.Abs(a + b - c - c) ? a
                        : Math.Abs(a + b - c - b) <= Math.Abs(a + b - c - c) ? b : c,
                    _ => 0,
                };
                raw.WriteByte((byte)(cur[i] - predicted));
            }
            prev = cur;
        }
        using var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(z);

        string path = Path.Combine(_dir, name);
        using var f = File.Create(path);
        f.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, width);
        BigEndian(ihdr, 4, height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colourType;
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", zipped.ToArray());
        Chunk(f, "IEND", []);
        return path;
    }

    private static void BigEndian(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    /// <summary>CRC читатель не проверяет — пишутся нули.</summary>
    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, data.Length);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(0, 16)]
    [InlineData(4, 8)]
    [InlineData(2, 8)]
    [InlineData(6, 16)]
    public void PngReaderDecodesEveryFilterAndDepth(int colourType, int depth)
    {
        int max = depth == 16 ? 65535 : 255;
        int Sample(int x, int y, int c) => (x * 37 + y * 101 + c * 59) * (depth == 16 ? 257 : 1) % (max + 1);
        var img = PngReader.Read(WritePng("t.png", 13, 11, colourType, depth, Sample));
        Assert.Equal(13, img.Width);
        Assert.Equal(11, img.Height);
        for (int y = 0; y < 11; y++)
            for (int x = 0; x < 13; x++)
                for (int c = 0; c < img.Channels; c++)
                    Assert.Equal(Sample(x, y, c) / (float)max, img[x, y, c], 5);
    }

    [Fact]
    public void Bc4KeepsMaskEdgesAndExactZeros()
    {
        var rng = new Random(3);
        Span<float> block = stackalloc float[16];
        Span<byte> encoded = stackalloc byte[8];
        Span<byte> decoded = stackalloc byte[16];
        double sse = 0;
        for (int trial = 0; trial < 2000; trial++)
        {
            // край пятна: часть блока — полный ноль, часть — градиент
            float level = rng.Next(40, 256), edge = rng.Next(0, 16);
            for (int k = 0; k < 16; k++) block[k] = k < edge ? 0 : Math.Clamp(level - k * rng.Next(0, 6), 1, 255);
            BlockCompress.EncodeBc4(block, encoded);
            BlockCompress.DecodeBc4(encoded, decoded);
            for (int k = 0; k < 16; k++)
            {
                if (block[k] == 0) Assert.Equal(0, decoded[k]);
                sse += (block[k] - decoded[k]) * (block[k] - decoded[k]);
            }
        }
        double rms = Math.Sqrt(sse / (2000 * 16));
        Assert.True(rms < 3.0, $"BC4 RMS {rms:F2}");
    }

    [Fact]
    public void ArrayBakesMipsLayersAndRoundTrips()
    {
        var square = new FloatImage(64, 64, 1);
        var wide = new FloatImage(128, 64, 1);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++) square[x, y, 0] = (x - 32) * (x - 32) + (y - 32) * (y - 32) < 400 ? 1 : 0;
            for (int x = 0; x < 128; x++) wide[x, y, 0] = x / 127f;
        }
        var data = TextureBake.Build([("disc", square), ("ramp", wide)], TextureChannel.Luma, 32, compress: true);
        Assert.Equal(TexturePixelFormat.Bc4, data.Format);
        Assert.Equal(6, data.Mips);
        Assert.Equal(2f, data.Layers[1].Aspect);

        using var ms = new MemoryStream();
        data.Write(ms);
        ms.Position = 0;
        var back = TextureData.Read(ms, "память");
        Assert.Equal(1, back.FindLayer("RAMP"));
        Assert.Equal(data.Subresources.Length, back.Subresources.Length);
        for (int i = 0; i < data.Subresources.Length; i++) Assert.Equal(data.Subresources[i], back.Subresources[i]);

        // центр диска — единица, угол — ноль; рампа растёт слева направо
        Span<byte> texels = stackalloc byte[16];
        var mip0 = back.Subresource(0, 0);
        BlockCompress.DecodeBc4(mip0.AsSpan((4 * 8 + 4) * 8, 8), texels);
        Assert.True(texels[5] > 240);
        BlockCompress.DecodeBc4(mip0.AsSpan(0, 8), texels);
        Assert.Equal(0, texels[0]);
        var ramp = back.Subresource(1, 0);
        BlockCompress.DecodeBc4(ramp.AsSpan(0, 8), texels);
        int left = texels[0];
        BlockCompress.DecodeBc4(ramp.AsSpan(7 * 8, 8), texels);
        Assert.True(texels[3] > left + 180);
    }

    /// <summary>Исходник 8×8: шахматка 0/1 по r (усреднение в линейном и в sRGB расходится сильнее всего), g и b — другое.</summary>
    private static FloatImage Checker()
    {
        var img = new FloatImage(8, 8, 4);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                img[x, y, 0] = (x + y) % 2;
                img[x, y, 1] = 0.3f;
                img[x, y, 2] = x / 7f;
                img[x, y, 3] = 1;
            }
        return img;
    }

    [Fact]
    public void GreySrgb_ChannelInAllColours_MipsAveragedInLinear()
    {
        // Серый sRGB: байт исходника как есть во всех трёх цветах; мип 1 — среднее 0 и 1 в линейном (0.5 → sRGB 188), а не 128.
        var raw = TextureBake.Build([("n", Checker())], TextureChannel.R, 8, compress: false, srgb: true);
        Assert.Equal((TexturePixelFormat.Rgba8Srgb, 4), (raw.Format, raw.Mips));
        var mip0 = raw.Subresource(0, 0);
        for (int i = 0; i < 64; i++)
            Assert.Equal([(byte)((i % 8 + i / 8) % 2 * 255), (byte)((i % 8 + i / 8) % 2 * 255), (byte)((i % 8 + i / 8) % 2 * 255), 255],
                mip0.AsSpan(i * 4, 4).ToArray());
        byte half = TextureBake.ToByte(BlockCompress.ToSrgb(0.5f), false);
        Assert.Equal(188, half);
        Assert.All(raw.Subresource(0, 1).Chunk(4), t => Assert.Equal([half, half, half, 255], t));

        // сжатый — BC1 sRGB; кодировщик втягивает опоры на 1/16 размаха блока внутрь: 0 и 255 выходят 16 и 239
        var bc1 = TextureBake.Build([("n", Checker())], TextureChannel.R, 8, compress: true, srgb: true);
        Assert.Equal(TexturePixelFormat.Bc1Srgb, bc1.Format);
        Span<byte> rgba = stackalloc byte[64];
        BlockCompress.DecodeBc1(bc1.Subresource(0, 0).AsSpan(0, 8), rgba);
        for (int k = 0; k < 16; k++)
            if ((k % 4 + k / 4) % 2 == 0) Assert.InRange(rgba[k * 4], 0, 20);
            else Assert.InRange(rgba[k * 4], 235, 255);
        BlockCompress.DecodeBc1(bc1.Subresource(0, 1).AsSpan(0, 8), rgba);
        Assert.InRange(rgba[0], half - 3, half + 3);

        // без srgb тот же канал — BC4/R8 с усреднением как есть
        var linear = TextureBake.Build([("n", Checker())], TextureChannel.R, 8, compress: false);
        Assert.Equal(TexturePixelFormat.R8, linear.Format);
        Assert.All(linear.Subresource(0, 1), b => Assert.Equal(128, b));

        Assert.Throws<ArgumentException>(() => TextureBake.Build([("n", Checker())], TextureChannel.Normal, 8, false, srgb: true));
        Assert.Throws<ArgumentException>(() => TextureBake.Build([("n", Checker())], TextureChannel.Rg, 8, false, srgb: true));
    }

    [Fact]
    public void Rg_TwoChannelsAsIs()
    {
        // r = g = 1 нормаль вытянула бы до длины 1 (0.707); rg хранит как есть. Второй канал — g, не b.
        var img = new FloatImage(8, 8, 4);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                (img[x, y, 0], img[x, y, 1], img[x, y, 2]) = x < 4 ? (1f, 1f, 0f) : (0.25f, 0.75f, 0.5f);
        Assert.Equal(TextureChannel.Rg, TextureBake.ParseChannel("RG"));
        var raw = TextureBake.Build([("n", img)], TextureChannel.Rg, 8, compress: false);
        Assert.Equal(TexturePixelFormat.Rg8, raw.Format);
        var mip0 = raw.Subresource(0, 0);
        Assert.Equal([255, 255], mip0.AsSpan(0, 2).ToArray());
        Assert.Equal([64, 191], mip0.AsSpan(5 * 2, 2).ToArray());

        var bc5 = TextureBake.Build([("n", img)], TextureChannel.Rg, 8, compress: true);
        Assert.Equal(TexturePixelFormat.Bc5, bc5.Format);
        Span<byte> r = stackalloc byte[16], g = stackalloc byte[16];
        var block = bc5.Subresource(0, 0).AsSpan(16, 16);   // блок (1, 0): x 4…7
        BlockCompress.DecodeBc4(block[..8], r);
        BlockCompress.DecodeBc4(block[8..], g);
        Assert.All(r.ToArray(), v => Assert.InRange(v, 63, 65));
        Assert.All(g.ToArray(), v => Assert.InRange(v, 190, 192));
    }
}
