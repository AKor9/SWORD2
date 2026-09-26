using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Texture;

/// <summary>Что берётся из исходника в слой массива.</summary>
internal enum TextureChannel { Luma, R, G, B, A, Normal, Rgb, Rg }

/// <summary>
/// Слои-картинки → массив <see cref="TextureData"/>: выбор канала, приведение к квадрату размера массива (площадной фильтр,
/// исходное соотношение сторон уходит в <see cref="TextureLayer.Aspect"/>), полная цепочка мипов, BC4 (один канал), BC5 (нормаль xy
/// или два канала как есть), BC1 sRGB (цвет). Без сжатия — R8 / RG8 / RGBA8 sRGB. Один канал с <c>srgb</c> — серый в BC1 sRGB /
/// RGBA8 sRGB (канал во все три цвета): у R8 и BC4 нет sRGB-формата, а выборка должна декодировать до фильтра, как в UE.
/// </summary>
internal static class TextureBake
{
    public static TextureData Build(IReadOnlyList<(string Name, FloatImage Image)> layers, TextureChannel channel, int size, bool compress,
        bool srgb = false)
    {
        if (layers.Count == 0) throw new ArgumentException("нет слоёв");
        if (size < 4 || size > 8192 || (size & (size - 1)) != 0) throw new ArgumentException($"size {size}: нужна степень двойки от 4 до 8192");
        if (srgb && channel is TextureChannel.Normal or TextureChannel.Rg)
            throw new ArgumentException($"srgb: только один канал или rgb, не {channel.ToString().ToLowerInvariant()}");
        int mips = 1 + (int)Math.Log2(size);
        // серый sRGB дальше идёт путём цвета: усреднение в линейном, в файле — sRGB
        bool colour = channel == TextureChannel.Rgb || srgb;
        var format = channel switch
        {
            _ when colour => compress ? TexturePixelFormat.Bc1Srgb : TexturePixelFormat.Rgba8Srgb,
            TextureChannel.Normal or TextureChannel.Rg => compress ? TexturePixelFormat.Bc5 : TexturePixelFormat.Rg8,
            _ => compress ? TexturePixelFormat.Bc4 : TexturePixelFormat.R8,
        };
        bool signed = channel == TextureChannel.Normal;

        var subs = new byte[layers.Count * mips][];
        var infos = new TextureLayer[layers.Count];
        Parallel.For(0, layers.Count, s =>
        {
            var (name, src) = layers[s];
            infos[s] = new TextureLayer(name, (float)src.Width / src.Height);
            var level = Resample(Extract(src, channel, srgb), size, size);
            for (int m = 0; m < mips; m++)
            {
                if (m > 0) level = Halve(level);
                if (channel == TextureChannel.Normal) Renormalize(level);
                subs[s * mips + m] = colour
                    ? compress ? BlockCompress.EncodeBc1(level) : QuantizeSrgb(level)
                    : compress ? BlockCompress.Encode(level, signed) : Quantize(level, signed);
            }
        });
        var data = new TextureData
        {
            Format = format, Width = size, Height = size, Mips = mips, Slices = layers.Count, Subresources = subs, Layers = infos,
        };
        data.Validate("запекание текстуры");
        return data;
    }

    public static TextureChannel ParseChannel(string text) => text.ToLowerInvariant() switch
    {
        "luma" => TextureChannel.Luma,
        "r" => TextureChannel.R,
        "g" => TextureChannel.G,
        "b" => TextureChannel.B,
        "a" => TextureChannel.A,
        "normal" => TextureChannel.Normal,
        "rgb" => TextureChannel.Rgb,
        "rg" => TextureChannel.Rg,
        _ => throw new ArgumentException($"channel «{text}»: luma, r, g, b, a, rg, normal или rgb"),
    };

    /// <summary>
    /// Нужные каналы: один (маска), два (нормаль xy в −1…1 или rg как есть), три (цвет — линейный). Один канал с
    /// <paramref name="srgb"/> — значение исходника как sRGB: в линейный и во все три цвета.
    /// </summary>
    private static FloatImage Extract(FloatImage src, TextureChannel channel, bool srgb)
    {
        if (srgb && channel != TextureChannel.Rgb)
        {
            var one = Extract(src, channel, false);
            var grey = new FloatImage(src.Width, src.Height, 3);
            for (int i = 0; i < one.Pixels.Length; i++)
                grey.Pixels[i * 3] = grey.Pixels[i * 3 + 1] = grey.Pixels[i * 3 + 2] = BlockCompress.FromSrgb(one.Pixels[i]);
            return grey;
        }
        int n = src.Width * src.Height, c = src.Channels;
        var dst = new FloatImage(src.Width, src.Height, channel switch
        {
            TextureChannel.Normal or TextureChannel.Rg => 2, TextureChannel.Rgb => 3, _ => 1,
        });
        var p = src.Pixels;
        for (int i = 0; i < n; i++)
        {
            int o = i * c;
            switch (channel)
            {
                case TextureChannel.Luma:
                    dst.Pixels[i] = c >= 3 ? 0.299f * p[o] + 0.587f * p[o + 1] + 0.114f * p[o + 2] : p[o];
                    break;
                case TextureChannel.A:
                    dst.Pixels[i] = c is 2 or 4 ? p[o + c - 1] : 1f;
                    break;
                case TextureChannel.Rgb:
                    // цвет PNG — sRGB; мипы усредняются в линейном, в sRGB возвращает кодировщик
                    for (int ch = 0; ch < 3; ch++)
                        dst.Pixels[i * 3 + ch] = BlockCompress.FromSrgb(c >= 3 ? p[o + ch] : p[o]);
                    break;
                case TextureChannel.Normal:
                    if (c < 3) throw new InvalidDataException("channel normal: в исходнике нет RGB");
                    dst.Pixels[i * 2] = p[o] * 2 - 1;
                    dst.Pixels[i * 2 + 1] = p[o + 1] * 2 - 1;
                    break;
                case TextureChannel.Rg:
                    dst.Pixels[i * 2] = p[o];
                    dst.Pixels[i * 2 + 1] = c >= 3 ? p[o + 1] : p[o];   // серый (с альфой) — в оба
                    break;
                default:
                    int k = (int)channel - (int)TextureChannel.R;
                    dst.Pixels[i] = k < c ? p[o + k] : p[o];
                    break;
            }
        }
        return dst;
    }

    /// <summary>Площадной фильтр по каждой оси (уменьшение); увеличение — билинейно.</summary>
    public static FloatImage Resample(FloatImage src, int width, int height)
    {
        if (src.Width == width && src.Height == height) return src;
        var tmp = new FloatImage(width, src.Height, src.Channels);
        for (int y = 0; y < src.Height; y++)
            ResampleLine(width, src.Width, (x, ch) => src[x, y, ch], (x, ch, v) => tmp[x, y, ch] = v, src.Channels);
        var dst = new FloatImage(width, height, src.Channels);
        for (int x = 0; x < width; x++)
            ResampleLine(height, src.Height, (y, ch) => tmp[x, y, ch], (y, ch, v) => dst[x, y, ch] = v, src.Channels);
        return dst;
    }

    private static void ResampleLine(int dstLen, int srcLen, Func<int, int, float> get, Action<int, int, float> set, int channels)
    {
        double scale = (double)srcLen / dstLen;
        for (int i = 0; i < dstLen; i++)
            for (int ch = 0; ch < channels; ch++)
            {
                if (scale <= 1)
                {
                    double f = (i + 0.5) * scale - 0.5;
                    int a = Math.Clamp((int)Math.Floor(f), 0, srcLen - 1), b = Math.Min(a + 1, srcLen - 1);
                    float t = (float)Math.Clamp(f - Math.Floor(f), 0, 1);
                    set(i, ch, get(a, ch) * (1 - t) + get(b, ch) * t);
                    continue;
                }
                double x0 = i * scale, x1 = (i + 1) * scale, sum = 0;
                for (int s = (int)x0; s < Math.Min((int)Math.Ceiling(x1), srcLen); s++)
                    sum += get(s, ch) * (Math.Min(x1, s + 1) - Math.Max(x0, s));
                set(i, ch, (float)(sum / (x1 - x0)));
            }
    }

    private static FloatImage Halve(FloatImage src)
    {
        int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
        var dst = new FloatImage(w, h, src.Channels);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < src.Channels; c++)
                {
                    int x1 = Math.Min(2 * x + 1, src.Width - 1), y1 = Math.Min(2 * y + 1, src.Height - 1);
                    dst[x, y, c] = (src[2 * x, 2 * y, c] + src[x1, 2 * y, c] + src[2 * x, y1, c] + src[x1, y1, c]) * 0.25f;
                }
        return dst;
    }

    /// <summary>Нормаль xy после усреднения короче единицы: вытягивается обратно, z восстанавливает шейдер.</summary>
    private static void Renormalize(FloatImage img)
    {
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 2)
        {
            float len = MathF.Sqrt(p[i] * p[i] + p[i + 1] * p[i + 1]);
            if (len > 1) { p[i] /= len; p[i + 1] /= len; }
        }
    }

    /// <summary>Канал 0…1 или −1…1 (<paramref name="signed"/>: нормаль) → байт.</summary>
    internal static byte ToByte(float v, bool signed) =>
        (byte)Math.Clamp(MathF.Round((signed ? v * 0.5f + 0.5f : v) * 255), 0, 255);

    /// <summary>Линейный цвет → RGBA8 sRGB, альфа 255.</summary>
    private static byte[] QuantizeSrgb(FloatImage img)
    {
        int n = img.Width * img.Height;
        var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 3; k++) bytes[i * 4 + k] = ToByte(BlockCompress.ToSrgb(img.Pixels[i * 3 + k]), false);
            bytes[i * 4 + 3] = 255;
        }
        return bytes;
    }

    private static byte[] Quantize(FloatImage img, bool signed)
    {
        var bytes = new byte[img.Pixels.Length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = ToByte(img.Pixels[i], signed);
        return bytes;
    }
}
