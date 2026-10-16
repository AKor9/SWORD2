using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Engine.Render;

/// <summary>
/// Один канал слоя текстуры на CPU, мип 0, линейные значения — для CPU-эталонов выборок шейдеров. Только несжатые форматы.
/// Декод sRGB и билинейка железа отличаются до ≈0.004: это эталон формулы, а не пикселя в пиксель.
/// </summary>
public sealed class TextureTexels
{
    private readonly float[] _values;

    private TextureTexels(int width, int height, float[] values)
    {
        Width = width;
        Height = height;
        _values = values;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Тексель (<paramref name="x"/>, <paramref name="y"/>) — от левого верхнего угла.</summary>
    public float Texel(int x, int y) => _values[y * Width + x];

    /// <summary>Билинейная выборка с повтором, как линейный сэмплер wrap на мипе 0: центры текселей — (i + 0.5)/размер.</summary>
    public float Sample(Vector2 uv)
    {
        float x = uv.X * Width - 0.5f, y = uv.Y * Height - 0.5f;
        float x0 = MathF.Floor(x), y0 = MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int ix = Wrap(x0, Width), iy = Wrap(y0, Height);
        int ix1 = ix + 1 == Width ? 0 : ix + 1, iy1 = iy + 1 == Height ? 0 : iy + 1;
        float top = Texel(ix, iy) + (Texel(ix1, iy) - Texel(ix, iy)) * fx;
        float bottom = Texel(ix, iy1) + (Texel(ix1, iy1) - Texel(ix, iy1)) * fx;
        return top + (bottom - top) * fy;
    }

    private static int Wrap(float i, int size)
    {
        double m = i - size * Math.Floor(i / (double)size);
        int w = (int)m;
        return w >= size ? 0 : w;
    }

    /// <summary>Канал (0 — r) слоя файла <c>.swtex</c> из потока; <paramref name="name"/> — для сообщений.</summary>
    /// <exception cref="InvalidDataException">Не текстура, обрезана или формат сжатый (BCn).</exception>
    /// <exception cref="ArgumentOutOfRangeException">Канала или слоя нет в формате или файле.</exception>
    public static TextureTexels Read(Stream file, string name, int channel = 0, int slice = 0)
    {
        var data = TextureData.Read(file, name);
        int bytes = data.Format switch
        {
            TexturePixelFormat.R8 => 1,
            TexturePixelFormat.Rg8 => 2,
            TexturePixelFormat.Rgba8 or TexturePixelFormat.Rgba8Srgb => 4,
            _ => throw new InvalidDataException($"{name}: формат {data.Format} сжатый — тексели на CPU читаются только у несжатых"),
        };
        if ((uint)channel >= (uint)bytes)
            throw new ArgumentOutOfRangeException(nameof(channel), $"{name}: канала {channel} нет в формате {data.Format}");
        if ((uint)slice >= (uint)data.Slices)
            throw new ArgumentOutOfRangeException(nameof(slice), $"{name}: слоя {slice} нет (слоёв {data.Slices})");
        bool srgb = data.Format == TexturePixelFormat.Rgba8Srgb && channel < 3;
        var source = data.Subresource(slice, 0);
        int row = data.RowBytes(0);
        var values = new float[data.Width * data.Height];
        for (int y = 0; y < data.Height; y++)
            for (int x = 0; x < data.Width; x++)
            {
                float v = source[y * row + x * bytes + channel] / 255f;
                values[y * data.Width + x] = srgb ? SrgbToLinear(v) : v;
            }
        return new TextureTexels(data.Width, data.Height, values);
    }

    /// <summary>Декод sRGB → линейное (IEC 61966-2-1).</summary>
    public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}
