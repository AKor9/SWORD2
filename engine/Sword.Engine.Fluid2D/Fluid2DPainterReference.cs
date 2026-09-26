namespace Sword.Engine.Fluid2D;

/// <summary>
/// CPU-эталон Painter (float32): те же формулы и порядок операций, ручная выборка и хранение в half, что у <c>CsPainter</c>
/// (<c>fluid2d.hlsl</c>) — по нему тесты сверяют GPU. Грид кисти — <see cref="Grid"/> (слои PainterGrid 0–7, прошлый шаг читается в uv + WOD);
/// шум кисти и битмап — картинки <see cref="Fluid2DImage"/> (нужны, только если включены).
/// </summary>
public sealed class Fluid2DPainterReference
{
    private readonly Fluid2DImage? _noise, _bitmap;

    public Fluid2DPainterReference(Fluid2DParams parameters, Fluid2DImage? brushNoise, Fluid2DImage? bitmap, Fluid2DBrushTexel[]? grid = null)
    {
        parameters.Validate();
        Params = parameters;
        _noise = brushNoise;
        _bitmap = bitmap;
        int bn = parameters.BrushResolution;
        Grid = grid ?? new Fluid2DBrushTexel[bn * bn];
        if (Grid.Length != bn * bn) throw new ArgumentException($"грид кисти {Grid.Length} текселей, нужно {bn}²");
    }

    public Fluid2DParams Params { get; set; }

    /// <summary>Грид кисти после последнего <see cref="Paint"/> (BrushResolution², ряд 0 — v = 0).</summary>
    public Fluid2DBrushTexel[] Grid { get; private set; }

    /// <summary>
    /// Шаг Painter по точкам шага кисти (<see cref="Fluid2DBrush.End"/>). <paramref name="fullScan"/> — все точки каждому текселю без бининга
    /// и без отсечения по AABB штампа (проверка решения (d)).
    /// </summary>
    public void Paint(Fluid2DBrush brush, in Fluid2DStepInput input, bool fullScan = false)
    {
        if (!brush.Ready || brush.Step != input.Step) throw new InvalidOperationException($"кисть не готова к шагу {input.Step}");
        var c = Constants(input, brush);
        int bn = Params.BrushResolution;
        var output = new Fluid2DBrushTexel[Grid.Length];
        var records = brush.Records;
        var cells = brush.Cells;
        var entries = brush.Entries;
        uint[]? everything = null;
        if (fullScan)
        {
            everything = new uint[records.Length];
            for (int i = 0; i < everything.Length; i++) everything[i] = (uint)i;
        }
        Span<float> o = stackalloc float[7];
        int side = Fluid2DBrush.Tile * brush.CellGroups;
        for (int y = 0; y < bn; y++)
            for (int x = 0; x < bn; x++)
            {
                ReadOnlySpan<uint> list;
                if (everything is not null) list = everything;
                else
                {
                    uint word = cells[(y / side) * brush.CellsPerAxis + x / side];
                    list = entries.Slice((int)(word & 0xFFFF), (int)(word >> 16));
                }
                Texel(c, records, list, !fullScan, x, y, Grid, _noise, _bitmap, o);
                output[y * bn + x] = new Fluid2DBrushTexel(o[0], o[1], o[2], o[4], o[5], o[6]);
            }
        Grid = output;
    }

    internal Fluid2DConstants Constants(in Fluid2DStepInput input, Fluid2DBrush brush)
    {
        var p = Params;
        if (p.BrushNoiseUsed && _noise is null) throw new InvalidOperationException("шум кисти включён, картинки нет");
        var noise = _noise is null ? default : new Fluid2DNoise(0, 0, _noise.Width, _noise.Height);
        var bitmap = _bitmap is null ? default : new Fluid2DNoise(0, 0, _bitmap.Width, _bitmap.Height);
        var c = Fluid2DConstants.Build(p, input, true, default, default);
        c.SetPainter(p, input, brush, 0, noise, bitmap);
        return c;
    }

    /// <summary>
    /// Тексель (x, y) грида кисти: шумы текселя, мазки точек <paramref name="list"/> (ветка i = 0 L2 у каждой), слияние (плотность — наибольший
    /// |ρ| со знаком, при равенстве — раньше по ключу; скорость — средняя с весом s_h; или сумма при сложении), память краски, маска края,
    /// битмап. Результат — семь слоёв 0–6 в float (до хранения в half).
    /// </summary>
    internal static void Texel(in Fluid2DConstants c, ReadOnlySpan<Fluid2DBrushPoint> points, ReadOnlySpan<uint> list, bool cutoff, int x, int y,
        ReadOnlySpan<Fluid2DBrushTexel> old, Fluid2DImage? noise, Fluid2DImage? bitmap, Span<float> result)
    {
        float u = (x + 0.5f) * c.BrushInvN, v = (y + 0.5f) * c.BrushInvN;
        float vnx = 0, vny = 0, dn = 1;
        if ((c.PainterFlags & Fluid2DConstants.PainterNoise) != 0)
        {
            var rg = SampleWrap(noise!, u * c.VeloNoiseScale + c.VeloNoiseOffset.X, v * c.VeloNoiseScale + c.VeloNoiseOffset.Y);
            vnx = rg.X * c.VeloNoiseAmp - c.VeloNoiseBias;
            vny = rg.Y * c.VeloNoiseAmp - c.VeloNoiseBias;
            float r = SampleWrap(noise!, u * c.BrushDensityScale + c.BrushDensityOffset.X, v * c.BrushDensityScale + c.BrushDensityOffset.Y).X;
            dn = MathF.Max(c.BrushDensityFloor, r > 0 ? MathF.Pow(r, c.BrushDensityPow) : 0f);
        }

        float clampV = c.PainterVeloClamp;
        float best = 0, weight = 0, wx = 0, wy = 0, sx = 0, sy = 0, sz = 0;
        foreach (uint index in list)
        {
            ref readonly var p = ref points[(int)index];
            if (cutoff && (u < p.Min.X || u > p.Max.X || v < p.Min.Y || v > p.Max.Y)) continue;
            float dx = u - p.Pos.X, dy = v - p.Pos.Y;
            float f = MathF.Pow(2f, p.C2 * (dx * dx + dy * dy));
            float ax = u - p.Prev.X, ay = v - p.Prev.Y;
            if (p.SegLen >= 0)
            {
                float t = Math.Clamp(ax * p.Dir.X + ay * p.Dir.Y, 0f, p.SegLen);
                float fs = MathF.Pow(2f, p.C2 * (ax * ax + ay * ay - t * t));
                f = MathF.Max(fs, f);
            }
            f = MathF.Min(f, 1f) * p.Zf;
            float w1 = (1f - f) * (1f - f);
            float velX = p.Vb.X + (vnx + ax) * 100f * p.Pz * w1 * p.KVel;
            float velY = p.Vb.Y + (vny + ay) * 100f * p.Pz * w1 * p.KVel;
            float xs = Saturate((dn * f - 0.5f * p.Hs) / (1f - p.Hs));
            float sh = xs * xs * (3f - 2f * xs);
            float sv = sh;
            if (c.PainterQ > 0)
            {
                float base_ = MathF.Max(sv - 0.1f * c.PainterQ, 0f);
                sv = base_ > 0 ? MathF.Pow(base_, c.PainterSharpen) : 0f;
            }
            velX = Math.Clamp(sv * velX * p.Mv, -clampV, clampV) * c.PainterVeloMult;
            velY = Math.Clamp(sv * velY * p.Mv, -clampV, clampV) * c.PainterVeloMult;
            float dens = sh * p.Strp;
            if (MathF.Abs(dens) > MathF.Abs(best)) best = dens;
            weight += sh;
            wx += sh * velX;
            wy += sh * velY;
            sx += velX;
            sy += velY;
            sz += Math.Clamp(dens, -1f, 1f);
        }

        float a = Saturate(best + c.PainterBase);
        int bn = (int)c.BrushN;
        Fluid2DReference.BrushBilinear(old, bn, u + c.Wod.X, v + c.Wod.Y, out var prev, out _);
        float keep = 1f - a;
        float px, py, pz;
        if ((c.PainterFlags & Fluid2DConstants.PainterAdditive) != 0)
        {
            float a4 = (c.PainterFlags & Fluid2DConstants.PainterPainterMode) != 0 ? (a > 0 ? MathF.Pow(a, 0.25f) : 0f) : a;
            px = sx * a4 + prev.X * keep;
            py = sy * a4 + prev.Y * keep;
            pz = sz * a4 + prev.Z * keep;
        }
        else
        {
            float inv = MathF.Max(weight, 1e-6f);
            px = wx / inv * a * a + prev.X * keep;
            py = wy / inv * a * a + prev.Y * keep;
            pz = Math.Clamp(best, -1f, 1f) * a * a + prev.Z * keep;
        }

        float iw = c.PainterEdgeInv;
        float m = Saturate(iw - 0.01f - MathF.Max(MathF.Abs(2f * u * iw - iw), MathF.Abs(2f * v * iw - iw)));
        float e = (c.PainterFlags & Fluid2DConstants.PainterEdgeStep) != 0 ? (m >= 1f ? 1f : 0f) : (m > 0 ? MathF.Pow(m, 0.1f) : 0f);

        float l4 = 0, l5 = 0, l6 = 0;
        if ((c.PainterFlags & Fluid2DConstants.PainterBitmap) != 0)
        {
            var t = SampleWrap(bitmap!, u * c.BitmapScale + c.BitmapOffset.X, v * c.BitmapScale + c.BitmapOffset.Y);
            bool densityOnly = (c.PainterFlags & Fluid2DConstants.PainterBitmapDensityOnly) != 0;
            if (!densityOnly)
            {
                l4 = 2f * t.X - 1f;
                l5 = 2f * t.Y - 1f;
            }
            float td = densityOnly ? t.X : t.Z;
            l6 = td * c.DensityTxtMult * (1f - Saturate(pz)) * 0.5f;
        }
        result[0] = e * px;
        result[1] = e * py;
        result[2] = e * pz;
        result[3] = 0;
        result[4] = l4;
        result[5] = l5;
        result[6] = l6;
    }

    /// <summary>Билинейная выборка RGBA с заворотом, мип 0 (как <c>ImageBilinear</c> в <c>fluid2d.hlsl</c>).</summary>
    internal static Fluid2DReference.Vec4 SampleWrap(Fluid2DImage img, float u, float v)
    {
        float px = u * img.Width - 0.5f, py = v * img.Height - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        var a = Texel(img, x, y);
        var b = Texel(img, x + 1, y);
        var c = Texel(img, x, y + 1);
        var d = Texel(img, x + 1, y + 1);
        var ab = Lerp(a, b, tx);
        var cd = Lerp(c, d, tx);
        return Lerp(ab, cd, ty);
    }

    private static Fluid2DReference.Vec4 Texel(Fluid2DImage img, int x, int y)
    {
        int i = (Wrap(y, img.Height) * img.Width + Wrap(x, img.Width)) * 4;
        var v = img.Rgba;
        return new Fluid2DReference.Vec4(v[i], v[i + 1], v[i + 2], v[i + 3]);
    }

    private static Fluid2DReference.Vec4 Lerp(in Fluid2DReference.Vec4 a, in Fluid2DReference.Vec4 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);

    private static int Wrap(int i, int size) => ((i % size) + size) % size;

    private static float Saturate(float x) => x >= 0 ? MathF.Min(x, 1f) : 0f;
}
