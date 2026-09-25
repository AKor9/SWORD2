using System.Numerics;
using StbTrueTypeSharp;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Font;

/// <summary>
/// Подготовка TrueType-контуров одного шрифта для аналитической отрисовки на GPU (<see cref="UiVectorFontFormat"/>).
/// Математика пиксельного шейдера основана на Slug Algorithm Эрика Ленгеля. Зовут обработчик шрифтов пекарни
/// и окна инструментов, которые печут себе системные шрифты на лету. Файл пишет писатель формата (<see cref="UiFontFile"/>).
/// </summary>
public static unsafe class UiFontBaker
{
    private const float BandEpsilon = 1f / 1024f;

    private readonly record struct Curve(Vector2 P1, Vector2 P2, Vector2 P3)
    {
        public float MinX => MathF.Min(P1.X, MathF.Min(P2.X, P3.X));
        public float MaxX => MathF.Max(P1.X, MathF.Max(P2.X, P3.X));
        public float MinY => MathF.Min(P1.Y, MathF.Min(P2.Y, P3.Y));
        public float MaxY => MathF.Max(P1.Y, MathF.Max(P2.Y, P3.Y));
    }

    private sealed class Glyph
    {
        public required int GlyphId;
        public readonly List<Curve> Curves = [];
        public Vector2 Min;
        public Vector2 Max;
        public uint HorizontalHeader;
        public uint VerticalHeader;
        public Vector2 BandScale;
        public Vector2 BandOffset;
    }

    private readonly record struct Header(uint Count, uint Offset);
    private readonly record struct FaceMetrics(float Ascent, float Descent, float LineGap);

    /// <param name="ttf">TrueType-шрифт (квадратичные контуры).</param>
    /// <param name="sourceName">Откуда шрифт — для сообщений об ошибках.</param>
    /// <param name="name">Имя шрифта — для сообщений игры.</param>
    /// <param name="roles">Роли интерфейса (<see cref="UiVectorFontFormat.RoleNames"/>), которые берёт шрифт.</param>
    /// <param name="report">Сводка одной строкой: глифы, кривые, полосы.</param>
    /// <returns>Содержимое файла <c>.swfont</c>.</returns>
    public static byte[] Bake(byte[] ttf, string sourceName, string name, IReadOnlyList<string> roles, out string report)
    {
        foreach (var role in roles)
            if (!UiVectorFontFormat.TryParseRole(role, out _))
                throw new ArgumentException($"неизвестная роль «{role}»; роли: {UiVectorFontFormat.RoleList}");

        var glyphs = new List<Glyph>();
        var metrics = BakeFace(ttf, sourceName, glyphs);
        BuildBands(glyphs, out var headers, out var indices);
        using var output = new MemoryStream();
        ToFile(sourceName, name, roles, metrics, glyphs, headers, indices, ttf).Write(output);

        int maxBand = headers.Count == 0 ? 0 : (int)headers.Max(h => h.Count);
        report = $"{name}: glyphs {glyphs.Count}, curves {glyphs.Sum(g => g.Curves.Count)}, " +
                 $"band indices {indices.Count}, max curves/band {maxBand}, роли: {(roles.Count == 0 ? "нет" : string.Join(", ", roles))}";
        return output.ToArray();
    }

    private static FaceMetrics BakeFace(byte[] data, string path, List<Glyph> output)
    {
        // stbtt_fontinfo хранит указатель на данные шрифта и читает по нему в каждом вызове ниже. Указатель
        // из fixed после блока ведёт в массив, который сборщик мусора вправе сдвинуть; CreateFont копирует
        // файл в неуправляемую память и освобождает её в Dispose.
        using var info = StbTrueType.CreateFont(data, 0)
            ?? throw new InvalidOperationException($"не разобрался шрифт {path}");

        float toEm = StbTrueType.stbtt_ScaleForMappingEmToPixels(info, 1f);
        int ascent, descent, lineGap;
        StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);

        for (int glyphIndex = 0; glyphIndex < info.numGlyphs; glyphIndex++)
        {
            var glyph = new Glyph { GlyphId = glyphIndex };
            Extract(info, glyphIndex, toEm, glyph.Curves, path);
            FinishBounds(glyph);
            ValidateGlyph(glyph, path);
            output.Add(glyph);
        }
        return new FaceMetrics(ascent * toEm, descent * toEm, lineGap * toEm);
    }

    private static void Extract(StbTrueType.stbtt_fontinfo info, int glyphIndex,
        float toEm, List<Curve> curves, string path)
    {
        StbTrueType.stbtt_vertex* vertices;
        int count = StbTrueType.stbtt_GetGlyphShape(info, glyphIndex, &vertices);
        if (count <= 0 || vertices == null) return;

        try
        {
            Vector2 start = default;
            Vector2 current = default;
            bool open = false;
            int contourStart = 0;

            for (int i = 0; i < count; i++)
            {
                ref var v = ref vertices[i];
                Vector2 end = Point(v.x, v.y, toEm);

                if (v.type == StbTrueType.STBTT_vmove)
                {
                    Close(curves, ref current, start, open);
                    if (open) ValidateContour(curves, contourStart, current, start, path, glyphIndex);
                    contourStart = curves.Count;
                    start = current = end;
                    open = true;
                }
                else if (v.type == StbTrueType.STBTT_vline)
                {
                    curves.Add(new Curve(current, end, end));
                    current = end;
                }
                else if (v.type == StbTrueType.STBTT_vcurve)
                {
                    curves.Add(new Curve(current, Point(v.cx, v.cy, toEm), end));
                    current = end;
                }
                else if (v.type == StbTrueType.STBTT_vcubic)
                {
                    throw new InvalidDataException(
                        $"{path}: glyph ID {glyphIndex} содержит cubic Bézier. " +
                        "GPU-vector font поддерживает только TrueType quadratic outlines");
                }
            }

            Close(curves, ref current, start, open);
            if (open) ValidateContour(curves, contourStart, current, start, path, glyphIndex);
        }
        finally
        {
            StbTrueType.stbtt_FreeShape(info, vertices);
        }
    }

    private static Vector2 Point(short x, short y, float toEm) => new(x * toEm, -y * toEm);

    private static void Close(List<Curve> curves, ref Vector2 current, Vector2 start, bool open)
    {
        if (open && Vector2.DistanceSquared(current, start) > 1e-12f)
        {
            curves.Add(new Curve(current, start, start));
            current = start;
        }
    }

    private static void ValidateContour(List<Curve> curves, int first, Vector2 current,
        Vector2 start, string path, int glyphId)
    {
        const float epsilonSquared = 1e-10f;
        if (first == curves.Count)
            throw new InvalidDataException($"{path}: glyph ID {glyphId} содержит пустой контур");
        if (Vector2.DistanceSquared(current, start) > epsilonSquared)
            throw new InvalidDataException($"{path}: glyph ID {glyphId} содержит незамкнутый контур");

        Vector2 expected = curves[first].P1;
        for (int i = first; i < curves.Count; i++)
        {
            if (Vector2.DistanceSquared(curves[i].P1, expected) > epsilonSquared)
                throw new InvalidDataException(
                    $"{path}: glyph ID {glyphId}, контур разорван перед curve {i - first}");
            expected = curves[i].P3;
        }
        if (Vector2.DistanceSquared(expected, curves[first].P1) > epsilonSquared)
            throw new InvalidDataException($"{path}: glyph ID {glyphId} содержит незамкнутый контур");
    }

    private static void FinishBounds(Glyph glyph)
    {
        if (glyph.Curves.Count == 0)
        {
            glyph.Min = glyph.Max = Vector2.Zero;
            glyph.BandScale = Vector2.One;
            return;
        }

        glyph.Min = new Vector2(glyph.Curves.Min(c => c.MinX), glyph.Curves.Min(c => c.MinY));
        glyph.Max = new Vector2(glyph.Curves.Max(c => c.MaxX), glyph.Curves.Max(c => c.MaxY));
        Vector2 extent = Vector2.Max(glyph.Max - glyph.Min, new Vector2(1e-5f));
        glyph.BandScale = new Vector2(UiVectorFontFormat.BandsPerAxis / extent.X,
                                      UiVectorFontFormat.BandsPerAxis / extent.Y);
        glyph.BandOffset = -glyph.Min * glyph.BandScale;
    }

    private static void ValidateGlyph(Glyph glyph, string path)
    {
        static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);

        foreach (var c in glyph.Curves)
        {
            if (!Finite(c.P1) || !Finite(c.P2) || !Finite(c.P3))
                throw new InvalidDataException($"{path}: glyph ID {glyph.GlyphId} содержит NaN/Infinity");
            if (c.MinX < glyph.Min.X - 1e-6f || c.MaxX > glyph.Max.X + 1e-6f ||
                c.MinY < glyph.Min.Y - 1e-6f || c.MaxY > glyph.Max.Y + 1e-6f)
                throw new InvalidDataException($"{path}: bounds glyph ID {glyph.GlyphId} не содержат контур");
        }

        if (!Finite(glyph.Min) || !Finite(glyph.Max) ||
            glyph.Min.X > glyph.Max.X || glyph.Min.Y > glyph.Max.Y)
            throw new InvalidDataException($"{path}: неверные bounds glyph ID {glyph.GlyphId}");
    }

    private static void BuildBands(List<Glyph> glyphs, out List<Header> headers, out List<uint> indices)
    {
        headers = [];
        indices = [];

        uint curveBase = 0;
        foreach (var glyph in glyphs)
        {
            glyph.HorizontalHeader = (uint)headers.Count;
            AddAxisBands(glyph, horizontal: true, curveBase, headers, indices);
            glyph.VerticalHeader = (uint)headers.Count;
            AddAxisBands(glyph, horizontal: false, curveBase, headers, indices);
            curveBase += (uint)glyph.Curves.Count;
        }

        ValidateBands(glyphs, headers, indices);
    }

    private static void ValidateBands(List<Glyph> glyphs, List<Header> headers, List<uint> indices)
    {
        uint curveCount = (uint)glyphs.Sum(g => g.Curves.Count);
        foreach (var h in headers)
        {
            if ((ulong)h.Offset + h.Count > (ulong)indices.Count)
                throw new InvalidDataException("vector font: band ссылается за границу index buffer");
            for (uint i = 0; i < h.Count; i++)
                if (indices[(int)(h.Offset + i)] >= curveCount)
                    throw new InvalidDataException("vector font: band содержит неверный curve index");
        }

        uint glyphCurveBase = 0;
        foreach (var g in glyphs)
        {
            if (g.HorizontalHeader + UiVectorFontFormat.BandsPerAxis > headers.Count ||
                g.VerticalHeader + UiVectorFontFormat.BandsPerAxis > headers.Count)
                throw new InvalidDataException($"vector font: glyph ID {g.GlyphId} имеет неверные band headers");

            ValidateAxisBands(g, horizontal: true, glyphCurveBase, headers, indices);
            ValidateAxisBands(g, horizontal: false, glyphCurveBase, headers, indices);
            glyphCurveBase += (uint)g.Curves.Count;
        }
    }

    private static void ValidateAxisBands(Glyph glyph, bool horizontal, uint curveBase,
        List<Header> headers, List<uint> indices)
    {
        float min = horizontal ? glyph.Min.Y : glyph.Min.X;
        float max = horizontal ? glyph.Max.Y : glyph.Max.X;
        float step = (max - min) / UiVectorFontFormat.BandsPerAxis;
        uint headerBase = horizontal ? glyph.HorizontalHeader : glyph.VerticalHeader;

        for (int band = 0; band < UiVectorFontFormat.BandsPerAxis; band++)
        {
            float lo = min + step * band - BandEpsilon;
            float hi = min + step * (band + 1) + BandEpsilon;
            uint[] expected = Enumerable.Range(0, glyph.Curves.Count)
                .Where(i => horizontal
                    ? glyph.Curves[i].MaxY >= lo && glyph.Curves[i].MinY <= hi &&
                      MathF.Abs(glyph.Curves[i].MaxY - glyph.Curves[i].MinY) > 1e-8f
                    : glyph.Curves[i].MaxX >= lo && glyph.Curves[i].MinX <= hi &&
                      MathF.Abs(glyph.Curves[i].MaxX - glyph.Curves[i].MinX) > 1e-8f)
                .OrderByDescending(i => horizontal ? glyph.Curves[i].MaxX : glyph.Curves[i].MaxY)
                .Select(i => curveBase + (uint)i)
                .ToArray();
            Header actual = headers[(int)headerBase + band];
            if (actual.Count != expected.Length)
                throw new InvalidDataException(
                    $"vector font: glyph ID {glyph.GlyphId}, band {band} имеет неполное покрытие");
            for (int i = 0; i < expected.Length; i++)
                if (indices[(int)actual.Offset + i] != expected[i])
                    throw new InvalidDataException(
                        $"vector font: glyph ID {glyph.GlyphId}, band {band} имеет неверный порядок curves");
        }
    }

    private static void AddAxisBands(Glyph glyph, bool horizontal, uint curveBase,
        List<Header> headers, List<uint> indices)
    {
        float min = horizontal ? glyph.Min.Y : glyph.Min.X;
        float max = horizontal ? glyph.Max.Y : glyph.Max.X;
        float step = (max - min) / UiVectorFontFormat.BandsPerAxis;

        for (int band = 0; band < UiVectorFontFormat.BandsPerAxis; band++)
        {
            float lo = min + step * band - BandEpsilon;
            float hi = min + step * (band + 1) + BandEpsilon;
            var selected = Enumerable.Range(0, glyph.Curves.Count)
                .Where(i => horizontal
                    ? glyph.Curves[i].MaxY >= lo && glyph.Curves[i].MinY <= hi &&
                      MathF.Abs(glyph.Curves[i].MaxY - glyph.Curves[i].MinY) > 1e-8f
                    : glyph.Curves[i].MaxX >= lo && glyph.Curves[i].MinX <= hi &&
                      MathF.Abs(glyph.Curves[i].MaxX - glyph.Curves[i].MinX) > 1e-8f)
                .OrderByDescending(i => horizontal ? glyph.Curves[i].MaxX : glyph.Curves[i].MaxY)
                .ToArray();

            uint offset = (uint)indices.Count;
            foreach (int i in selected) indices.Add(curveBase + (uint)i);
            headers.Add(new Header((uint)selected.Length, offset));
        }
    }

    private static UiFontFile ToFile(string sourceName, string name, IReadOnlyList<string> roles, FaceMetrics metrics,
        List<Glyph> glyphs, List<Header> headers, List<uint> indices, byte[] ttf) => new()
    {
        Name = name,
        Path = sourceName,
        Roles = [.. roles],
        Ascent = metrics.Ascent,
        Descent = metrics.Descent,
        LineGap = metrics.LineGap,
        Glyphs = [.. glyphs.Select(g => new VectorGlyph
        {
            Bounds = new Vector4(g.Min.X, g.Min.Y, g.Max.X, g.Max.Y),
            HorizontalHeader = g.HorizontalHeader,
            VerticalHeader = g.VerticalHeader,
            BandScale = g.BandScale,
            BandOffset = g.BandOffset,
        })],
        // 32-байтный шаг StructuredBuffer: p3 и два нуля
        Curves = [.. glyphs.SelectMany(g => g.Curves).Select(c => new VectorCurve
        {
            P12 = new Vector4(c.P1.X, c.P1.Y, c.P2.X, c.P2.Y),
            P3Pad = new Vector4(c.P3.X, c.P3.Y, 0f, 0f),
        })],
        Bands = [.. headers.Select(h => new VectorBand { Count = h.Count, Offset = h.Offset })],
        Indices = [.. indices],
        Ttf = ttf,
    };
}
