using System.Numerics;
using System.Text;
using StbTrueTypeSharp;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Font;

/// <summary>
/// Один файл <c>.swfont</c>, нарисованный на CPU — для предпросмотра пекарни: контуры берутся из запечённых кривых (те же,
/// что рисует шейдер интерфейса), покрытие пикселя — точная площадь под отрезками (накопление со знаком, как в font-rs);
/// кривые делятся на отрезки по размеру в пикселях. Перенос <c>UiFontRaster</c> SWORD 2eab13b, но <b>без шейпинга</b>:
/// SWORD расставлял знаки HarfBuzz, как игра, а плагин движка — сторона Neutral без ссылки на Ui, поэтому знак берётся по
/// cmap ttf из файла (stb), шаг — по hmtx, кернинг — только таблица kern (GPOS не читается). Для образца и набора знаков
/// этого хватает; лигатуры и сложные письменности предпросмотр показывает по знаку.
/// </summary>
internal sealed unsafe class UiFontRaster : IDisposable
{
    private readonly UiFontFile _file;
    private readonly StbTrueType.stbtt_fontinfo _info;
    private readonly float _toEm;
    /// <summary>[glyph] → номера его кривых (из полос по горизонтали, без повторов); null — ещё не собраны.</summary>
    private readonly int[]?[] _glyphCurves;
    private float[] _acc = [];

    public string Name => _file.Name;
    public IReadOnlyList<string> Roles => _file.Roles;
    public float Ascent => _file.Ascent;
    public float Descent => _file.Descent;
    public float LineGap => _file.LineGap;
    public int GlyphCount => _file.Glyphs.Length;
    public int CurveCount => _file.Curves.Length;
    public int TtfBytes => _file.Ttf.Length;

    private UiFontRaster(UiFontFile file)
    {
        _file = file;
        _info = StbTrueType.CreateFont(file.Ttf, 0) ?? throw new InvalidDataException($"{file.Path}: ttf внутри .swfont не разобрался");
        _toEm = StbTrueType.stbtt_ScaleForMappingEmToPixels(_info, 1f);
        _glyphCurves = new int[]?[file.Glyphs.Length];
    }

    /// <exception cref="InvalidDataException">Файл повреждён.</exception>
    public static UiFontRaster Read(Stream stream, string path) => new(UiFontFile.Read(stream, path));

    /// <summary>Кодовая точка есть в шрифте (cmap даёт glyph, а не .notdef).</summary>
    public bool Has(int codepoint) => StbTrueType.stbtt_FindGlyphIndex(_info, codepoint) != 0;

    /// <summary>Ширина строки, пиксели.</summary>
    public float Measure(float sizePx, string text)
    {
        float w = 0;
        foreach (var (_, advance) in Layout(text)) w += advance * sizePx;
        return w;
    }

    /// <summary>
    /// Строка в картинку RGBA8 (без премножения, поверх того, что там есть). <paramref name="y"/> — верх строки
    /// (базовая линия ниже на ascent). Возвращает ширину.
    /// </summary>
    public float Draw(Span<byte> rgba, int width, int height, float x, float y, float sizePx, string text, uint colour)
    {
        float pen = x, baseline = y + _file.Ascent * sizePx;
        foreach (var (glyph, advance) in Layout(text))
        {
            DrawGlyph(rgba, width, height, glyph, pen, baseline, sizePx, colour);
            pen += advance * sizePx;
        }
        return pen - x;
    }

    /// <summary>Знаки строки по cmap и их шаг в em (с кернингом к следующему по таблице kern).</summary>
    private List<(int Glyph, float Advance)> Layout(string text)
    {
        var list = new List<(int, float)>(text.Length);
        int prev = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            int glyph = StbTrueType.stbtt_FindGlyphIndex(_info, rune.Value);
            int advance, lsb;
            StbTrueType.stbtt_GetGlyphHMetrics(_info, glyph, &advance, &lsb);
            if (prev >= 0)
            {
                var (g, a) = list[^1];
                list[^1] = (g, a + StbTrueType.stbtt_GetGlyphKernAdvance(_info, prev, glyph) * _toEm);
            }
            list.Add((glyph, advance * _toEm));
            prev = glyph;
        }
        return list;
    }

    /// <summary>Один glyph: начало координат glyph'а (на базовой линии) в точке <paramref name="ox"/>, <paramref name="oy"/>.</summary>
    public void DrawGlyph(Span<byte> rgba, int width, int height, int glyphId, float ox, float oy, float sizePx, uint colour)
    {
        if ((uint)glyphId >= (uint)_file.Glyphs.Length) glyphId = 0;
        var b = _file.Glyphs[glyphId].Bounds;
        if (b.Z <= b.X || b.W <= b.Y) return;
        int x0 = Math.Max(0, (int)MathF.Floor(ox + b.X * sizePx)), y0 = Math.Max(0, (int)MathF.Floor(oy + b.Y * sizePx));
        int x1 = Math.Min(width, (int)MathF.Ceiling(ox + b.Z * sizePx) + 1), y1 = Math.Min(height, (int)MathF.Ceiling(oy + b.W * sizePx) + 1);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return;
        if (_acc.Length < (w + 2) * h) _acc = new float[(w + 2) * h];
        var acc = _acc.AsSpan(0, (w + 2) * h);
        acc.Clear();

        var origin = new Vector2(ox - x0, oy - y0);
        foreach (int c in Curves(glyphId)) Curve(acc, w, h, _file.Curves[c], origin, sizePx);

        byte cr = (byte)colour, cg = (byte)(colour >> 8), cb = (byte)(colour >> 16);
        float ca = (colour >> 24) / 255f;
        for (int yy = 0; yy < h; yy++) Resolve(rgba, width, acc, w, x0, y0, yy, cr, cg, cb, ca);
    }

    /// <summary>Кривая — отрезками в буфер накопления; отклонение контрольной точки от хорды → число отрезков (~0,1 пикселя).</summary>
    private static void Curve(Span<float> acc, int w, int h, VectorCurve curve, Vector2 origin, float sizePx)
    {
        var p1 = new Vector2(curve.P12.X, curve.P12.Y) * sizePx + origin;
        var p2 = new Vector2(curve.P12.Z, curve.P12.W) * sizePx + origin;
        var p3 = new Vector2(curve.P3Pad.X, curve.P3Pad.Y) * sizePx + origin;
        float dev = (p1 - 2 * p2 + p3).Length();
        int n = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(dev * 2.5f)), 1, 64);
        var prev = p1;
        for (int i = 1; i <= n; i++)
        {
            float t = i / (float)n, u = 1 - t;
            var p = u * u * p1 + 2 * u * t * p2 + t * t * p3;
            Line(acc, w + 2, h, prev, p);
            prev = p;
        }
    }

    /// <summary>Строка буфера накопления → покрытие → цвет поверх картинки.</summary>
    private static void Resolve(Span<byte> rgba, int width, Span<float> acc, int w, int x0, int y0, int yy, byte cr, byte cg, byte cb, float ca)
    {
        float sum = 0;
        int row = yy * (w + 2);
        for (int xx = 0; xx < w; xx++)
        {
            sum += acc[row + xx];
            float cov = MathF.Min(1, MathF.Abs(sum)) * ca;
            if (cov <= 0.002f) continue;
            int o = ((y0 + yy) * width + x0 + xx) * 4;
            Over(rgba.Slice(o, 4), cr, cg, cb, cov);
        }
    }

    private static void Over(Span<byte> dst, byte r, byte g, byte b, float a)
    {
        float da = dst[3] / 255f;
        float oa = a + da * (1 - a);
        if (oa <= 0) return;
        dst[0] = (byte)((r * a + dst[0] * da * (1 - a)) / oa + 0.5f);
        dst[1] = (byte)((g * a + dst[1] * da * (1 - a)) / oa + 0.5f);
        dst[2] = (byte)((b * a + dst[2] * da * (1 - a)) / oa + 0.5f);
        dst[3] = (byte)(oa * 255 + 0.5f);
    }

    private int[] Curves(int glyphId)
    {
        if (_glyphCurves[glyphId] is { } known) return known;
        var g = _file.Glyphs[glyphId];
        var set = new SortedSet<int>();
        for (int band = 0; band < UiVectorFontFormat.BandsPerAxis; band++)
        {
            var hb = _file.Bands[g.HorizontalHeader + band];
            for (uint i = 0; i < hb.Count; i++) set.Add((int)_file.Indices[hb.Offset + i]);
        }
        return _glyphCurves[glyphId] = [.. set];
    }

    /// <summary>Отрезок в буфер накопления: в каждой клетке — доля площади слева от отрезка со знаком направления по Y.</summary>
    private static void Line(Span<float> acc, int stride, int height, Vector2 p0, Vector2 p1)
    {
        if (MathF.Abs(p0.Y - p1.Y) < 1e-6f) return;
        float dir = 1;
        if (p0.Y > p1.Y)
        {
            dir = -1;
            (p0, p1) = (p1, p0);
        }
        float dxdy = (p1.X - p0.X) / (p1.Y - p0.Y);
        float x = p0.X;
        int yStart = Math.Max(0, (int)p0.Y);
        if (p0.Y < 0) x -= p0.Y * dxdy;
        int yEnd = Math.Min(height, (int)MathF.Ceiling(p1.Y));
        for (int y = yStart; y < yEnd; y++)
        {
            float dy = MathF.Min(y + 1, p1.Y) - MathF.Max(y, p0.Y);
            float xNext = x + dxdy * dy;
            Row(acc, y * stride, stride, x, xNext, dy * dir);
            x = xNext;
        }
    }

    /// <summary>Одна строка пикселей отрезка от <paramref name="x"/> до <paramref name="xNext"/>, площадь со знаком <paramref name="d"/>.</summary>
    private static void Row(Span<float> acc, int line, int stride, float x, float xNext, float d)
    {
        float xa = MathF.Min(x, xNext), xb = MathF.Max(x, xNext);
        float x0Floor = MathF.Floor(xa);
        int x0i = (int)x0Floor;
        float x1Ceil = MathF.Ceiling(xb);
        int x1i = (int)x1Ceil;
        if (x1i <= x0i + 1)
        {
            float xm = 0.5f * (x + xNext) - x0Floor;
            Add(acc, line, stride, x0i, d - d * xm);
            Add(acc, line, stride, x0i + 1, d * xm);
            return;
        }
        float s = 1 / (xb - xa);
        float x0f = xa - x0Floor;
        float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
        float x1f = xb - x1Ceil + 1;
        float am = 0.5f * s * x1f * x1f;
        Add(acc, line, stride, x0i, d * a0);
        if (x1i == x0i + 2) Add(acc, line, stride, x0i + 1, d * (1 - a0 - am));
        else
        {
            float a1 = s * (1.5f - x0f);
            Add(acc, line, stride, x0i + 1, d * (a1 - a0));
            for (int xi = x0i + 2; xi < x1i - 1; xi++) Add(acc, line, stride, xi, d * s);
            float a2 = a1 + (x1i - x0i - 3) * s;
            Add(acc, line, stride, x1i - 1, d * (1 - a2 - am));
        }
        Add(acc, line, stride, x1i, d * am);
    }

    private static void Add(Span<float> acc, int line, int stride, int x, float v)
    {
        if (x < 0) x = 0;   // левее картинки — вклад в первую клетку (заливка строки не теряется)
        if (x >= stride) return;
        acc[line + x] += v;
    }

    public void Dispose() => _info.Dispose();
}
