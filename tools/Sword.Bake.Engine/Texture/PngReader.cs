using System.IO.Compression;

namespace Sword.Bake.Engine.Texture;

/// <summary>Картинка в плавающей точке: каналы 0…1 подряд, ряд за рядом сверху вниз.</summary>
internal sealed class FloatImage(int width, int height, int channels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Channels { get; } = channels;
    public float[] Pixels { get; } = new float[width * height * channels];

    public float this[int x, int y, int c]
    {
        get => Pixels[(y * Width + x) * Channels + c];
        set => Pixels[(y * Width + x) * Channels + c] = value;
    }
}

/// <summary>
/// Чтение PNG без зависимостей: серый, серый с альфой, RGB, RGBA (8 и 16 бит), палитра (8 бит); без чересстрочности.
/// Каналы выхода — как в файле, кроме палитры (RGBA). Экспорт текстур UE (tools/ue/export_textures.py) — ровно такие.
/// </summary>
internal static class PngReader
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static FloatImage Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return Decode(bytes, path);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"{path}: PNG не читается: {e.Message}", e);
        }
    }

    private static FloatImage Decode(byte[] bytes, string path)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException($"{path}: не PNG");
        int width = 0, height = 0, depth = 0, colour = -1;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= bytes.Length)
        {
            int length = BigEndian(bytes, pos);
            string type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            int data = pos + 8;
            if (length < 0 || data + length + 4 > bytes.Length) throw new InvalidDataException($"{path}: блок {type} обрезан");
            switch (type)
            {
                case "IHDR":
                    width = BigEndian(bytes, data);
                    height = BigEndian(bytes, data + 4);
                    depth = bytes[data + 8];
                    colour = bytes[data + 9];
                    if (bytes[data + 12] != 0) throw new InvalidDataException($"{path}: чересстрочный PNG не поддерживается");
                    break;
                case "PLTE": palette = bytes.AsSpan(data, length).ToArray(); break;
                case "tRNS": paletteAlpha = bytes.AsSpan(data, length).ToArray(); break;
                case "IDAT": idat.Write(bytes, data, length); break;
            }
            pos = data + length + 4;
            if (type == "IEND") break;
        }

        int samples = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"{path}: тип цвета {colour}") };
        if (!(depth == 8 || (depth == 16 && colour != 3))) throw new InvalidDataException($"{path}: глубина {depth} бит (нужны 8 или 16)");
        if (width <= 0 || height <= 0) throw new InvalidDataException($"{path}: нет IHDR");
        if (colour == 3 && palette is null) throw new InvalidDataException($"{path}: палитровый PNG без PLTE");

        int bpp = samples * depth / 8, stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var prev = new byte[stride];
        var cur = new byte[stride];
        var image = new FloatImage(width, height, colour == 3 ? 4 : samples);
        float scale = depth == 16 ? 1f / 65535 : 1f / 255;
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            byte filter = raw[row];
            Unfilter(filter, raw.AsSpan(row + 1, stride), prev, cur, bpp, path);
            int o = y * width * image.Channels;
            if (colour == 3)
                for (int x = 0; x < width; x++)
                {
                    int i = cur[x];
                    image.Pixels[o++] = palette![i * 3] / 255f;
                    image.Pixels[o++] = palette[i * 3 + 1] / 255f;
                    image.Pixels[o++] = palette[i * 3 + 2] / 255f;
                    image.Pixels[o++] = paletteAlpha is not null && i < paletteAlpha.Length ? paletteAlpha[i] / 255f : 1f;
                }
            else if (depth == 8)
                for (int i = 0; i < stride; i++) image.Pixels[o++] = cur[i] * scale;
            else
                for (int i = 0; i < stride; i += 2) image.Pixels[o++] = (cur[i] << 8 | cur[i + 1]) * scale;
            (prev, cur) = (cur, prev);
        }
        return image;
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> src, byte[] prev, byte[] cur, int bpp, string path)
    {
        switch (filter)
        {
            case 0: src.CopyTo(cur); break;
            case 1:
                for (int i = 0; i < src.Length; i++) cur[i] = (byte)(src[i] + (i >= bpp ? cur[i - bpp] : 0));
                break;
            case 2:
                for (int i = 0; i < src.Length; i++) cur[i] = (byte)(src[i] + prev[i]);
                break;
            case 3:
                for (int i = 0; i < src.Length; i++) cur[i] = (byte)(src[i] + ((i >= bpp ? cur[i - bpp] : 0) + prev[i]) / 2);
                break;
            case 4:
                for (int i = 0; i < src.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    cur[i] = (byte)(src[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                break;
            default: throw new InvalidDataException($"{path}: фильтр строки {filter}");
        }
    }

    private static int BigEndian(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];
}
