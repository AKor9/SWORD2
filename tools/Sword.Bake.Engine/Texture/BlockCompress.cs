using System.Numerics;

namespace Sword.Bake.Engine.Texture;

/// <summary>
/// BC4 (один канал) и BC5 (два канала — два блока BC4 подряд). Блок 4×4: два опорных байта и 16 индексов по 3 бита.
/// Кодировщик пробует оба режима: восемь градаций между опорами и шесть градаций плюс точные 0 и 255 — второй
/// выигрывает у масок, где у края пятна есть полный ноль. Опоры уточняются наименьшими квадратами.
/// </summary>
internal static class BlockCompress
{
    public static byte[] Encode(FloatImage img)
    {
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4, channels = img.Channels;
        if (channels is not (1 or 2)) throw new ArgumentException($"BC4/BC5: каналов {channels}");
        bool signed = channels == 2;
        var output = new byte[bw * bh * 8 * channels];
        Span<float> block = stackalloc float[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
                for (int c = 0; c < channels; c++)
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int sx = Math.Min(bx * 4 + x, img.Width - 1), sy = Math.Min(by * 4 + y, img.Height - 1);
                            block[y * 4 + x] = TextureBake.ToByte(img[sx, sy, c], signed);
                        }
                    EncodeBc4(block, output.AsSpan((by * bw + bx) * 8 * channels + c * 8, 8));
                }
        return output;
    }

    /// <summary>Значения 0…255 → 8 байт блока.</summary>
    public static void EncodeBc4(ReadOnlySpan<float> v, Span<byte> dst)
    {
        float lo = 255, hi = 0, loInner = 255, hiInner = 0;
        bool extremes = false;
        foreach (float x in v)
        {
            lo = Math.Min(lo, x);
            hi = Math.Max(hi, x);
            if (x <= 0 || x >= 255) { extremes = true; continue; }
            loInner = Math.Min(loInner, x);
            hiInner = Math.Max(hiInner, x);
        }
        if (hi - lo < 0.5f)
        {
            dst.Clear();
            dst[0] = dst[1] = (byte)lo;   // e0 == e1 — шестиградационный режим, все индексы 0
            return;
        }

        Span<byte> best = stackalloc byte[8];
        Span<byte> trial = stackalloc byte[8];
        float bestErr = Eight(v, Math.Clamp((int)MathF.Round(hi), 0, 255), Math.Clamp((int)MathF.Round(lo), 0, 255), best);
        if (extremes)
        {
            int a = loInner > hiInner ? 0 : (int)MathF.Round(loInner), b = loInner > hiInner ? 255 : (int)MathF.Round(hiInner);
            float err = Six(v, a, b, trial);
            if (err < bestErr) { bestErr = err; trial.CopyTo(best); }
        }
        best.CopyTo(dst);
    }

    /// <summary>Режим e0 &gt; e1: восемь градаций. Две итерации уточнения опор.</summary>
    private static float Eight(ReadOnlySpan<float> v, int e0, int e1, Span<byte> dst)
    {
        Span<float> t = stackalloc float[16];
        float err = float.MaxValue;
        Span<byte> tmp = stackalloc byte[8];
        for (int iter = 0; iter < 3; iter++)
        {
            if (e0 <= e1) { if (e0 > 0) e1 = e0 - 1; else e0 = e1 + 1; }
            float e = Assign8(v, e0, e1, tmp, t);
            if (e < err) { err = e; tmp.CopyTo(dst); } else break;
            if (!Fit(v, t, out float a, out float b)) break;
            int n0 = Math.Clamp((int)MathF.Round(a), 0, 255), n1 = Math.Clamp((int)MathF.Round(b), 0, 255);
            if (e1 == 0) n1 = 0;   // полный ноль у края маски остаётся точным: по нему шейдер режет пятно
            if (n0 == e0 && n1 == e1) break;
            e0 = n0; e1 = n1;
        }
        return err;
    }

    private static float Assign8(ReadOnlySpan<float> v, int e0, int e1, Span<byte> dst, Span<float> t)
    {
        Span<float> pal = stackalloc float[8];
        Span<float> pt = stackalloc float[8];
        pal[0] = e0; pt[0] = 0;
        pal[1] = e1; pt[1] = 1;
        for (int i = 2; i < 8; i++) { pt[i] = (i - 1) / 7f; pal[i] = ((8 - i) * e0 + (i - 1) * e1) / 7f; }
        ulong bits = 0;
        float err = 0;
        for (int k = 0; k < 16; k++)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                float d = (v[k] - pal[i]) * (v[k] - pal[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            err += bestD;
            t[k] = pt[best];
            bits |= (ulong)best << (3 * k);
        }
        Write(dst, e0, e1, bits);
        return err;
    }

    /// <summary>Режим e0 ≤ e1: шесть градаций, индекс 6 — ноль, 7 — 255.</summary>
    private static float Six(ReadOnlySpan<float> v, int e0, int e1, Span<byte> dst)
    {
        Span<float> pal = stackalloc float[8];
        pal[0] = e0; pal[1] = e1;
        for (int i = 2; i < 6; i++) pal[i] = ((6 - i) * e0 + (i - 1) * e1) / 5f;
        pal[6] = 0; pal[7] = 255;
        ulong bits = 0;
        float err = 0;
        for (int k = 0; k < 16; k++)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                float d = (v[k] - pal[i]) * (v[k] - pal[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            err += bestD;
            bits |= (ulong)best << (3 * k);
        }
        Write(dst, e0, e1, bits);
        return err;
    }

    /// <summary>Опоры a, b, при которых (1 − t)·a + t·b ближе всего к значениям.</summary>
    private static bool Fit(ReadOnlySpan<float> v, ReadOnlySpan<float> t, out float a, out float b)
    {
        double aa = 0, ab = 0, bb = 0, av = 0, bv = 0;
        for (int k = 0; k < 16; k++)
        {
            double wa = 1 - t[k], wb = t[k];
            aa += wa * wa; ab += wa * wb; bb += wb * wb;
            av += wa * v[k]; bv += wb * v[k];
        }
        double det = aa * bb - ab * ab;
        if (Math.Abs(det) < 1e-9) { a = b = 0; return false; }
        a = (float)((av * bb - bv * ab) / det);
        b = (float)((bv * aa - av * ab) / det);
        return true;
    }

    private static void Write(Span<byte> dst, int e0, int e1, ulong bits)
    {
        dst[0] = (byte)e0;
        dst[1] = (byte)e1;
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }

    /// <summary>
    /// BC1 цвета (три канала, линейные 0…1 → sRGB): блок 8 Б. Опоры — концы главной оси цветов блока
    /// (ковариация, степенной метод), чуть втянутые внутрь; индексы — ближайшая из четырёх градаций.
    /// Всегда четырёхцветный режим (c0 &gt; c1): прозрачности у альбедо нет.
    /// </summary>
    public static byte[] EncodeBc1(FloatImage img)
    {
        if (img.Channels != 3) throw new ArgumentException($"BC1: каналов {img.Channels}");
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4;
        var output = new byte[bw * bh * 8];
        Parallel.For(0, bh, by =>
        {
            Span<Vector3> block = stackalloc Vector3[16];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, img.Width - 1), sy = Math.Min(by * 4 + y, img.Height - 1);
                        block[y * 4 + x] = new Vector3(ToSrgb(img[sx, sy, 0]), ToSrgb(img[sx, sy, 1]), ToSrgb(img[sx, sy, 2])) * 255;
                    }
                EncodeBc1Block(block, output.AsSpan((by * bw + bx) * 8, 8));
            }
        });
        return output;
    }

    /// <summary>Линейный 0…1 → sRGB 0…1.</summary>
    public static float ToSrgb(float v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
    }

    /// <summary>sRGB 0…1 → линейный 0…1.</summary>
    public static float FromSrgb(float v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static void EncodeBc1Block(ReadOnlySpan<Vector3> c, Span<byte> dst)
    {
        var mean = Vector3.Zero;
        foreach (var v in c) mean += v;
        mean /= 16;
        // ковариация и главная ось
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var v in c)
        {
            var d = v - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        var axis = new Vector3(1, 1, 1);
        for (int it = 0; it < 6; it++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float len = axis.Length();
            if (len < 1e-6f) { axis = Vector3.Zero; break; }
            axis /= len;
        }
        float lo = 0, hi = 0;
        foreach (var v in c)
        {
            float t = Vector3.Dot(v - mean, axis);
            lo = MathF.Min(lo, t);
            hi = MathF.Max(hi, t);
        }
        float inset = (hi - lo) / 16;
        ushort e0 = To565(mean + axis * (hi - inset)), e1 = To565(mean + axis * (lo + inset));
        if (e0 < e1) (e0, e1) = (e1, e0);
        uint indices = 0;
        if (e0 != e1)
        {
            Span<Vector3> pal = stackalloc Vector3[4];
            pal[0] = From565(e0);
            pal[1] = From565(e1);
            pal[2] = (pal[0] * 2 + pal[1]) / 3;
            pal[3] = (pal[0] + pal[1] * 2) / 3;
            for (int k = 0; k < 16; k++)
            {
                int best = 0;
                float bestD = float.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    float d = Vector3.DistanceSquared(c[k], pal[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
                indices |= (uint)best << (2 * k);
            }
        }
        dst[0] = (byte)e0; dst[1] = (byte)(e0 >> 8);
        dst[2] = (byte)e1; dst[3] = (byte)(e1 >> 8);
        for (int i = 0; i < 4; i++) dst[4 + i] = (byte)(indices >> (8 * i));
    }

    private static ushort To565(Vector3 c)
    {
        int r = Math.Clamp((int)MathF.Round(c.X * 31 / 255), 0, 31);
        int g = Math.Clamp((int)MathF.Round(c.Y * 63 / 255), 0, 63);
        int b = Math.Clamp((int)MathF.Round(c.Z * 31 / 255), 0, 31);
        return (ushort)(r << 11 | g << 5 | b);
    }

    private static Vector3 From565(ushort v) =>
        new(((v >> 11) & 31) * 255f / 31, ((v >> 5) & 63) * 255f / 63, (v & 31) * 255f / 31);

    /// <summary>Распаковка блока BC1 (четырёхцветный режим) в RGBA — для предпросмотра.</summary>
    public static void DecodeBc1(ReadOnlySpan<byte> src, Span<byte> rgba64)
    {
        ushort e0 = (ushort)(src[0] | src[1] << 8), e1 = (ushort)(src[2] | src[3] << 8);
        Span<Vector3> pal = stackalloc Vector3[4];
        pal[0] = From565(e0);
        pal[1] = From565(e1);
        pal[2] = e0 > e1 ? (pal[0] * 2 + pal[1]) / 3 : (pal[0] + pal[1]) / 2;
        pal[3] = e0 > e1 ? (pal[0] + pal[1] * 2) / 3 : Vector3.Zero;
        uint bits = (uint)(src[4] | src[5] << 8 | src[6] << 16 | src[7] << 24);
        for (int k = 0; k < 16; k++)
        {
            var c = pal[(int)((bits >> (2 * k)) & 3)];
            rgba64[k * 4] = (byte)c.X;
            rgba64[k * 4 + 1] = (byte)c.Y;
            rgba64[k * 4 + 2] = (byte)c.Z;
            rgba64[k * 4 + 3] = 255;
        }
    }

    /// <summary>Распаковка блока BC4 — для проверок.</summary>
    public static void DecodeBc4(ReadOnlySpan<byte> src, Span<byte> dst16)
    {
        int e0 = src[0], e1 = src[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = e0; pal[1] = e1;
        if (e0 > e1)
            for (int i = 2; i < 8; i++) pal[i] = ((8 - i) * e0 + (i - 1) * e1) / 7;
        else
        {
            for (int i = 2; i < 6; i++) pal[i] = ((6 - i) * e0 + (i - 1) * e1) / 5;
            pal[6] = 0; pal[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (int k = 0; k < 16; k++) dst16[k] = (byte)pal[(int)((bits >> (3 * k)) & 7)];
    }
}
