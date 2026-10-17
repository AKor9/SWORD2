using System.Numerics;

namespace Sword.Engine.Ui;

/// <summary>
/// Один вызов рисования: квады [<see cref="Start"/>, Start + <see cref="Count"/>) списка (все режимы — один конвейер) с
/// обрезкой [<see cref="X0"/>, <see cref="X1"/>) × [<see cref="Y0"/>, <see cref="Y1"/>).
/// </summary>
public readonly record struct UiBatch(int Start, int Count, int X0, int Y0, int X1, int Y1);

/// <summary>
/// Список квадов интерфейса за кадр: аналитические фигуры (поле расстояний) и GPU-векторный текст; одна запись на квад.
/// Без выделений: строки — в <c>Span&lt;char&gt;</c>. Обрезка — стек прямоугольников, смены пишутся в <see cref="ClipRuns"/>;
/// квады целиком вне обрезки не записываются. Без шрифтов текст — <see cref="InvalidOperationException"/>.
/// </summary>
public sealed partial class UiDrawList
{
    private const float Fringe = 1f;

    /// <summary>Обрезка «нет обрезки» — заведомо больше любого экрана.</summary>
    public static readonly UiRect NoClip = new(-1e6f, -1e6f, 1e6f, 1e6f);

    /// <summary>С квада <see cref="Start"/> и до следующей записи действует обрезка <see cref="Clip"/>.</summary>
    public readonly record struct ClipRun(int Start, UiRect Clip);

    private UiQuad[] _quads = new UiQuad[4096];
    private int _count;
    private readonly List<ClipRun> _runs = [];
    private readonly Stack<UiRect> _clips = new();

    private readonly UiFonts? _fonts;

    /// <summary>Шрифты текста; список без шрифтов — ошибка при первом тексте.</summary>
    public UiFonts Fonts => _fonts ?? throw new InvalidOperationException("список квадов без шрифтов: текст не рисуется");

    public bool HasFonts => _fonts is not null;
    public float TextGamma { get; set; } = 1.6f;
    public int Count => _count;
    public ReadOnlySpan<UiQuad> Quads => _quads.AsSpan(0, _count);

    /// <summary>Смены обрезки по порядку; до первой записи — <see cref="NoClip"/>.</summary>
    public IReadOnlyList<ClipRun> ClipRuns => _runs;

    /// <summary>Текущая обрезка — пересечение всех вложенных.</summary>
    public UiRect ClipRect => _clips.Count > 0 ? _clips.Peek() : NoClip;

    public UiDrawList(UiFonts? fonts = null) => _fonts = fonts;

    public static uint Rgba(byte r, byte g, byte b, byte a = 255) =>
        r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    public static uint WithAlpha(uint colour, float alpha) =>
        (colour & 0x00FFFFFF) | ((uint)(Math.Clamp(alpha, 0f, 1f) * ((colour >> 24) & 0xFF)) << 24);

    public void Clear()
    {
        _count = 0;
        _runs.Clear();
        _clips.Clear();
    }

    // ── обрезка ────────────────────────────────────────────────────────

    /// <summary>Рисовать только внутри <paramref name="rect"/> (в пересечении с текущей обрезкой).</summary>
    public void PushClip(UiRect rect)
    {
        var clip = rect.Intersect(ClipRect);
        _clips.Push(clip);
        MarkClip(clip);
    }

    /// <summary>Рисовать только внутри <paramref name="rect"/>, не пересекая с текущей обрезкой (всплывающие окна).</summary>
    public void PushClipReplace(UiRect rect)
    {
        _clips.Push(rect);
        MarkClip(rect);
    }

    public void PopClip()
    {
        _clips.Pop();
        MarkClip(ClipRect);
    }

    private void MarkClip(UiRect clip) => SetRunAt(_count, clip);

    /// <summary>Виден ли прямоугольник в текущей обрезке.</summary>
    private bool Visible(float x0, float y0, float x1, float y1)
    {
        var c = ClipRect;
        return x0 < c.X1 && x1 > c.X0 && y0 < c.Y1 && y1 > c.Y0;
    }

    /// <summary>Дописать квады другого списка поверх своих (слой всплывающих окон) с его обрезками.</summary>
    /// <exception cref="ArgumentException">У <paramref name="other"/> другие шрифты: номера glyph'ов его текста — чужих таблиц.</exception>
    public void Append(UiDrawList other)
    {
        if (other._fonts is not null && !ReferenceEquals(other._fonts, _fonts))
            throw new ArgumentException("список квадов с другими шрифтами: номера glyph'ов — чужих таблиц", nameof(other));
        if (other._count == 0) return;
        int offset = _count;
        if (_quads.Length < _count + other._count) Array.Resize(ref _quads, Math.Max(_quads.Length * 2, _count + other._count));
        other.Quads.CopyTo(_quads.AsSpan(_count));
        _count += other._count;

        var restore = ClipRect;
        SetRunAt(offset, NoClip);
        foreach (var run in other._runs) SetRunAt(offset + run.Start, run.Clip);
        SetRunAt(_count, restore);
    }

    /// <summary>С квада <paramref name="start"/> — обрезка <paramref name="clip"/>; смена без квадов между — перезаписать.</summary>
    private void SetRunAt(int start, UiRect clip)
    {
        var current = _runs.Count > 0 ? _runs[^1].Clip : NoClip;
        if (_runs.Count > 0 && _runs[^1].Start == start) _runs[^1] = new ClipRun(start, clip);
        else if (clip != current) _runs.Add(new ClipRun(start, clip));
    }

    // ── фигуры ─────────────────────────────────────────────────────────

    private ref UiQuad Next()
    {
        if (_count == _quads.Length) Array.Resize(ref _quads, _quads.Length * 2);
        ref var q = ref _quads[_count++];
        q = default;
        q.Axis = new Vector2(1, 0);
        return ref q;
    }

    private ref UiQuad Shape(Vector2 centre, Vector2 half, float radius, float stroke)
    {
        radius = Math.Clamp(radius, 0f, MathF.Min(half.X, half.Y));
        float pad = Fringe + stroke * 0.5f;
        ref var q = ref Next();
        q.Mode = UiMode.Box;
        q.Centre = centre;
        q.HalfSize = half + new Vector2(pad);
        q.Uv0 = half;
        q.Shape = new Vector4(radius, stroke, 0, 0);
        return ref q;
    }

    private ref UiQuad Box(float x0, float y0, float x1, float y1, float radius, float stroke) =>
        ref Shape(new((x0 + x1) * 0.5f, (y0 + y1) * 0.5f), new((x1 - x0) * 0.5f, (y1 - y0) * 0.5f), radius, stroke);

    public void Rect(float x0, float y0, float x1, float y1, uint colour, float radius = 0f)
    {
        if (x1 <= x0 || y1 <= y0 || !Visible(x0, y0, x1, y1)) return;
        ref var q = ref Box(x0, y0, x1, y1, radius, 0f);
        q.ColourA = q.ColourB = colour;
    }

    public void Rect(UiRect r, uint colour, float radius = 0f) => Rect(r.X0, r.Y0, r.X1, r.Y1, colour, radius);

    /// <summary>Вертикальный градиент: <paramref name="top"/> сверху, <paramref name="bottom"/> снизу.</summary>
    public void RectGradient(float x0, float y0, float x1, float y1, uint top, uint bottom, float radius = 0f)
    {
        if (x1 <= x0 || y1 <= y0 || !Visible(x0, y0, x1, y1)) return;
        ref var q = ref Box(x0, y0, x1, y1, radius, 0f);
        q.ColourA = top;
        q.ColourB = bottom;
        q.Shape.W = 1f;
    }

    public void RectStroke(float x0, float y0, float x1, float y1, uint colour, float thickness, float radius = 0f)
    {
        if (x1 <= x0 || y1 <= y0 || !Visible(x0 - thickness, y0 - thickness, x1 + thickness, y1 + thickness)) return;
        ref var q = ref Box(x0, y0, x1, y1, radius, thickness);
        q.ColourA = q.ColourB = colour;
    }

    public void RectStroke(UiRect r, uint colour, float thickness, float radius = 0f) =>
        RectStroke(r.X0, r.Y0, r.X1, r.Y1, colour, thickness, radius);

    public void Circle(Vector2 centre, float radius, uint colour, float thickness = 0f)
    {
        if (radius <= 0f || !Visible(centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius)) return;
        ref var q = ref Shape(centre, new Vector2(radius), radius, thickness);
        q.ColourA = q.ColourB = colour;
    }

    public void Line(Vector2 a, Vector2 b, float thickness, uint colour)
    {
        var d = b - a;
        float length = d.Length();
        if (length < 1e-4f || thickness <= 0f) return;
        var min = Vector2.Min(a, b) - new Vector2(thickness);
        var max = Vector2.Max(a, b) + new Vector2(thickness);
        if (!Visible(min.X, min.Y, max.X, max.Y)) return;
        float r = thickness * 0.5f;
        ref var q = ref Shape((a + b) * 0.5f, new Vector2(length * 0.5f + r, r), r, 0f);
        q.Axis = d / length;
        q.ColourA = q.ColourB = colour;
    }

    /// <summary>Залитый треугольник со сглаженным краем (стрелки дерева и списков).</summary>
    public void Triangle(Vector2 a, Vector2 b, Vector2 c, uint colour)
    {
        var min = Vector2.Min(a, Vector2.Min(b, c));
        var max = Vector2.Max(a, Vector2.Max(b, c));
        if (!Visible(min.X, min.Y, max.X, max.Y)) return;
        var centre = (min + max) * 0.5f;
        ref var q = ref Next();
        q.Mode = UiMode.Triangle;
        q.Centre = centre;
        q.HalfSize = (max - min) * 0.5f + new Vector2(Fringe);
        // вершины — в системе квада; третья едет в Shape.zw (см. таблицу полей в ui.hlsl)
        q.Uv0 = a - centre;
        q.Uv1 = b - centre;
        q.Shape = new Vector4(0, 0, c.X - centre.X, c.Y - centre.Y);
        q.ColourA = q.ColourB = colour;
    }

    /// <summary>
    /// Картинка — слой <paramref name="slice"/> массива текстур (bindless <paramref name="srv"/>), мип, участок uv;
    /// <paramref name="nearest"/> — тексели квадратами. Цвет умножается на <paramref name="tint"/>.
    /// </summary>
    public void Image(UiRect r, int srv, Vector2 uv0, Vector2 uv1, uint tint, float mip = 0, int slice = 0, bool nearest = false)
    {
        if (r.IsEmpty || srv < 0 || !Visible(r.X0, r.Y0, r.X1, r.Y1)) return;
        ref var q = ref Next();
        q.Mode = UiMode.Sprite;
        q.Centre = r.Centre;
        q.HalfSize = r.Size * 0.5f;
        q.Uv0 = uv0;
        q.Uv1 = uv1;
        // индекс, мип и слой — значениями (см. UiMode.Sprite)
        q.Shape = new Vector4(srv, mip, nearest ? 1 : 0, slice);
        q.ColourA = q.ColourB = tint;
    }

    // ── текст ──────────────────────────────────────────────────────────

    public float Measure(UiFace face, float sizePx, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0f;
        return Measure(sizePx, Fonts.Shape(face, text));
    }

    private static float Measure(float sizePx, ReadOnlySpan<UiShapedGlyph> glyphs)
    {
        float width = 0f;
        foreach (ref readonly var g in glyphs) width += g.XAdvance * sizePx;
        return width;
    }

    /// <summary>Высота строки от верха до низа выносных элементов: столько занимает <see cref="Text"/> по вертикали.</summary>
    public float LineHeight(UiFace face, float sizePx)
    {
        var m = Fonts.Metrics(face);
        return (m.Ascent - m.Descent) * sizePx;
    }

    /// <summary>Строка от верхнего левого угла. Возвращает ширину.</summary>
    public float Text(UiFace face, float sizePx, Vector2 pos, uint colour, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0f;
        return EmitText(face, sizePx, pos, colour, Fonts.Shape(face, text));
    }

    private float EmitText(UiFace face, float sizePx, Vector2 pos, uint colour,
        ReadOnlySpan<UiShapedGlyph> glyphs)
    {
        UiFontMetrics font = Fonts.Metrics(face);
        float penX = MathF.Round(pos.X);
        float baseline = MathF.Round(pos.Y + font.Ascent * sizePx);
        float start = penX;

        foreach (ref readonly var shaped in glyphs)
        {
            Vector4 bounds = Fonts.Bounds(face, shaped.GlyphId);
            if (bounds.Z > bounds.X && bounds.W > bounds.Y)
            {
                float emPad = Fringe / sizePx;
                float x0 = bounds.X - emPad, y0 = bounds.Y - emPad;
                float x1 = bounds.Z + emPad, y1 = bounds.W + emPad;
                float w = (x1 - x0) * sizePx, h = (y1 - y0) * sizePx;
                float gx = penX + (shaped.XOffset + x0) * sizePx;
                float gy = baseline + (shaped.YOffset + y0) * sizePx;

                if (Visible(gx, gy, gx + w, gy + h))
                {
                    ref var q = ref Next();
                    q.Mode = UiMode.Text;
                    q.Centre = new Vector2(gx + w * 0.5f, gy + h * 0.5f);
                    q.HalfSize = new Vector2(w * 0.5f, h * 0.5f);
                    q.Uv0 = new Vector2(x0, y0);
                    q.Uv1 = new Vector2(x1, y1);
                    q.Shape.X = Fonts.VectorIndex(face, shaped.GlyphId);
                    q.ColourA = q.ColourB = colour;
                }
            }
            penX += shaped.XAdvance * sizePx;
        }
        return MathF.Max(penX - start, 0f);
    }

    public float TextRight(UiFace face, float sizePx, Vector2 rightTop, uint colour, ReadOnlySpan<char> text) =>
        TextAnchored(face, sizePx, rightTop, 1f, colour, text);

    public float TextCentered(UiFace face, float sizePx, Vector2 centreTop, uint colour, ReadOnlySpan<char> text) =>
        TextAnchored(face, sizePx, centreTop, 0.5f, colour, text);

    /// <summary>Строка, у которой точка <paramref name="anchor"/> её ширины (0 — левый край, 1 — правый) стоит в <paramref name="pos"/>.</summary>
    private float TextAnchored(UiFace face, float sizePx, Vector2 pos, float anchor, uint colour, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0f;
        ReadOnlySpan<UiShapedGlyph> glyphs = Fonts.Shape(face, text);
        float w = Measure(sizePx, glyphs);
        EmitText(face, sizePx, pos with { X = pos.X - w * anchor }, colour, glyphs);
        return w;
    }

    /// <summary>Строка не шире <paramref name="maxWidth"/>: не влезает — обрезается по символам с «…» в конце. Возвращает ширину.</summary>
    /// <remarks>
    /// Строка shaping'уется один раз: обрезка — по ширинам её glyph'ов на границе кластера (пара UTF-16 и combining mark —
    /// один кластер, половинки не бывает), а не перебором префиксов: каждый префикс — отдельный вызов HarfBuzz.
    /// </remarks>
    public float TextFit(UiFace face, float sizePx, Vector2 pos, uint colour, ReadOnlySpan<char> text, float maxWidth)
    {
        if (text.IsEmpty || maxWidth <= 0) return 0f;
        // «…» — первым: результат Shape живёт до следующего вызова
        Span<UiShapedGlyph> dots = stackalloc UiShapedGlyph[4];
        var shapedDots = Fonts.Shape(face, "…");
        dots = dots[..Math.Min(shapedDots.Length, dots.Length)];
        shapedDots[..dots.Length].CopyTo(dots);
        float ellipsis = Measure(sizePx, dots);

        var glyphs = Fonts.Shape(face, text);
        if (Measure(sizePx, glyphs) <= maxWidth) return EmitText(face, sizePx, pos, colour, glyphs);
        float pen = 0;
        int keep = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            pen += glyphs[i].XAdvance * sizePx;
            if (pen + ellipsis > maxWidth) break;
            if (i + 1 == glyphs.Length || glyphs[i + 1].Cluster != glyphs[i].Cluster) keep = i + 1;
        }
        while (keep > 0 && char.IsWhiteSpace(text[(int)glyphs[keep - 1].Cluster])) keep--;
        float w = EmitText(face, sizePx, pos, colour, glyphs[..keep]);
        return w + EmitText(face, sizePx, pos with { X = MathF.Round(pos.X) + w }, colour, dots);
    }

    // ── вызовы рисования ───────────────────────────────────────────────

    /// <summary>
    /// Вызовы рисования: подряд квады под одной обрезкой (соседние обрезки с тем же scissor — один вызов); обрезка —
    /// целочисленный scissor в пределах цели, дробные края наружу; вне цели — вызова нет. <paramref name="batches"/> очищается.
    /// </summary>
    public void Split(int width, int height, List<UiBatch> batches)
    {
        batches.Clear();
        int count = _count, nextRun = 0, runStart = 0;
        var clip = NoClip;
        while (runStart < count)
        {
            while (nextRun < _runs.Count && _runs[nextRun].Start <= runStart) clip = _runs[nextRun++].Clip;
            int runEnd = nextRun < _runs.Count ? Math.Min(_runs[nextRun].Start, count) : count;

            int x0 = (int)Math.Clamp(MathF.Floor(clip.X0), 0, width), y0 = (int)Math.Clamp(MathF.Floor(clip.Y0), 0, height);
            int x1 = (int)Math.Clamp(MathF.Ceiling(clip.X1), 0, width), y1 = (int)Math.Clamp(MathF.Ceiling(clip.Y1), 0, height);
            if (x1 > x0 && y1 > y0)
            {
                if (batches.Count > 0 && batches[^1] is var last && last.Start + last.Count == runStart
                    && (last.X0, last.Y0, last.X1, last.Y1) == (x0, y0, x1, y1))
                    batches[^1] = last with { Count = last.Count + runEnd - runStart };
                else batches.Add(new UiBatch(runStart, runEnd - runStart, x0, y0, x1, y1));
            }
            runStart = runEnd;
        }
    }

    /// <summary>Смещение каретки перед символом <paramref name="index"/> от начала строки.</summary>
    public float CaretX(UiFace face, float sizePx, ReadOnlySpan<char> text, int index)
    {
        if (index <= 0 || text.IsEmpty) return 0f;
        int end = Math.Min(index, text.Length);
        if (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1])) end--;   // внутри пары — перед ней
        return Measure(face, sizePx, text[..end]);
    }

    /// <summary>Индекс символа, перед которым встанет каретка при щелчке на расстоянии <paramref name="x"/> от начала строки.</summary>
    public int IndexAtX(UiFace face, float sizePx, ReadOnlySpan<char> text, float x)
    {
        if (text.IsEmpty || x <= 0) return 0;
        var glyphs = Fonts.Shape(face, text);
        float pen = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            float advance = glyphs[i].XAdvance * sizePx;
            if (x < pen + advance * 0.5f) return (int)glyphs[i].Cluster;
            pen += advance;
        }
        return text.Length;
    }
}
