using System.Numerics;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Кисть в тестах: синтетические текстуры по целочисленному хешу (те же, что у генератора золотых векторов
/// <c>artifacts/research/fluidninja/brush/painter/golden_vectors.py</c>), параметры, точки, сравнение слоёв кисти.
/// </summary>
internal static class PainterKit
{
    public static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352D;
        x ^= x >> 15;
        x *= 0x846CA68B;
        x ^= x >> 16;
        return x;
    }

    /// <summary>Шум кисти 32² (R — плотность, G — скорость): байты хеша / 255.</summary>
    public static Fluid2DImage Noise()
    {
        var v = new float[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int i = (y * 32 + x) * 4;
                v[i] = (Hash((uint)(0x1000 + y * 32 + x)) & 255) / 255f;
                v[i + 1] = (Hash((uint)(0x1000 + 0x100000 + y * 32 + x)) & 255) / 255f;
                v[i + 3] = 1;
            }
        return new Fluid2DImage(v, 32, 32);
    }

    /// <summary>Битмап 16² RGB: байты хеша / 255.</summary>
    public static Fluid2DImage Bitmap()
    {
        var v = new float[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4;
                for (int ch = 0; ch < 3; ch++) v[i + ch] = (Hash((uint)(0x2000 + ch * 0x100000 + y * 16 + x)) & 255) / 255f;
                v[i + 3] = 1;
            }
        return new Fluid2DImage(v, 16, 16);
    }

    /// <summary>Прошлая краска n²: скорость в [−1, 1], плотность в [0, 1], кратно 1/512 (точно в half).</summary>
    public static Fluid2DBrushTexel[] PreviousPaint(int n)
    {
        var g = new Fluid2DBrushTexel[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                g[y * n + x] = new Fluid2DBrushTexel(Value(x, y, 0), Value(x, y, 1), Value(x, y, 2));
        return g;
    }

    private static float Value(int x, int y, int layer)
    {
        int v = (int)(Hash((uint)(0x3000 + layer * 0x1000000 + y * 4096 + x)) % 1025) - 512;
        if (layer == 2) v = Math.Abs(v);
        return v / 512f;
    }

    /// <summary>Картинка шума кисти как RGBA8 для GPU (линейная).</summary>
    public static byte[] Rgba8(Fluid2DImage image)
    {
        var bytes = new byte[image.Rgba.Length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)MathF.Round(image.Rgba[i] * 255f);
        return bytes;
    }

    /// <summary>Площадка к камере 3 м без камеры, без поворота: мир (x, y) → UV (0.5 + x/3, 0.5 − y/3).</summary>
    public static Fluid2DStepInput At(ulong step, Vector2 wod = default, Vector2 wo = default) => new(step, wo, wod);

    public static Fluid2DBrushKey Key(uint id, ushort index = 0, Fluid2DBrushSource source = Fluid2DBrushSource.Array) => new(source, id, index);

    /// <summary>Наибольшее расхождение слоёв 0–2 и 4–6 двух гридов кисти в ULP half и абсолютно.</summary>
    public static (double Ulps, double Abs, int Index) Compare(Fluid2DBrushTexel[] gpu, Fluid2DBrushTexel[] cpu) =>
        Fluid2DTestKit.Compare(Halves(gpu), Halves(cpu));

    public static Half[] Halves(Fluid2DBrushTexel[] g)
    {
        var h = new Half[g.Length * 8];
        for (int i = 0; i < g.Length; i++)
        {
            h[i * 8] = g[i].VelocityX;
            h[i * 8 + 1] = g[i].VelocityY;
            h[i * 8 + 2] = g[i].Density;
            h[i * 8 + 3] = g[i].Capture;
            h[i * 8 + 4] = g[i].BitmapVelocityX;
            h[i * 8 + 5] = g[i].BitmapVelocityY;
            h[i * 8 + 6] = g[i].BitmapDensity;
            h[i * 8 + 7] = g[i].Unused;
        }
        return h;
    }
}
