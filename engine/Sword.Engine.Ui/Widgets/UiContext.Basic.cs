using System.Numerics;

namespace Sword.Engine.Ui;

/// <summary>Виджеты. Прямоугольник задаёт вызывающий (нарезкой <see cref="UiRect"/>), размеры шрифта и отступы — тема.</summary>
public sealed partial class UiContext
{
    // ── оформление ─────────────────────────────────────────────────────

    /// <summary>Подложка панели (окна): скруглённый прямоугольник цвета темы.</summary>
    public void Panel(UiRect r, uint? colour = null, float? radius = null) =>
        Draw.Rect(r, colour ?? Theme.Panel, radius ?? S(Theme.Radius));

    /// <summary>Строка текста, по вертикали — по центру прямоугольника; не влезает — с «…». Возвращает ширину.</summary>
    public float Label(UiRect r, ReadOnlySpan<char> text, uint? colour = null, UiAlign align = UiAlign.Left,
        UiFace face = UiFace.Body, float? size = null) =>
        Draw.Label(r, face, S(size ?? Theme.FontSize), colour ?? Theme.Text, text, align);

    /// <summary>
    /// Строка-сведение «подпись — значение»: подпись слева приглушённая, значение справа цифровым начертанием, мелким кеглем;
    /// <paramref name="split"/> — доля ширины под подпись. Значение не влезло в остаток — подпись уступает ему всё, кроме
    /// своего текста. <paramref name="labelSize"/> — кегль подписи (null — мелкий).
    /// </summary>
    public void InfoRow(UiRect r, ReadOnlySpan<char> label, ReadOnlySpan<char> value, float split = 0.42f, uint? valueColour = null,
        float? labelSize = null) =>
        Draw.InfoRow(r, label, S(labelSize ?? Theme.SmallFontSize), Theme.TextDim, value, S(Theme.SmallFontSize),
            valueColour ?? Theme.Text, split, S(10));

    /// <summary>
    /// Текст с переносом по словам в <paramref name="r"/>: строк — сколько влезает по высоте, остаток последней — с «…».
    /// Возвращает число строк.
    /// </summary>
    public int Paragraph(UiRect r, ReadOnlySpan<char> text, uint? colour = null, UiFace face = UiFace.Body, float? size = null)
    {
        if (text.IsEmpty || r.Width <= 0) return 0;
        float fs = S(size ?? Theme.FontSize), lh = Draw.LineHeight(face, fs);
        int max = Math.Max(1, (int)(r.Height / lh));
        uint c = colour ?? Theme.Text;
        int lines = 0;
        float y = r.Y0;
        while (!text.IsEmpty && lines < max)
        {
            int n = lines == max - 1 ? text.Length : LineBreak(text, face, fs, r.Width);
            Draw.TextFit(face, fs, new Vector2(r.X0, y), c, text[..n], r.Width);
            text = text[n..].TrimStart(' ');
            y += lh;
            lines++;
        }
        return lines;
    }

    /// <summary>Длина начала <paramref name="text"/> до последнего пробела, с которым строка влезает в <paramref name="width"/>;
    /// слово длиннее строки — целиком (его режет «…»).</summary>
    private int LineBreak(ReadOnlySpan<char> text, UiFace face, float fs, float width)
    {
        if (Draw.Measure(face, fs, text) <= width) return text.Length;
        int fit = -1;
        for (int i = text.IndexOf(' '); i >= 0; i = text[(i + 1)..].IndexOf(' ') is var j && j >= 0 ? i + 1 + j : -1)
        {
            if (Draw.Measure(face, fs, text[..i]) > width) break;
            fit = i;
        }
        if (fit > 0) return fit;
        int word = text.IndexOf(' ');
        return word > 0 ? word : text.Length;
    }

    /// <summary>Заголовок секции: мелкая подпись и тонкая линия до края.</summary>
    public void SectionHeader(UiRect r, ReadOnlySpan<char> title) =>
        Draw.SectionHeader(r, title, S(Theme.SmallFontSize), Theme.TextDim, Theme.Border, S(8), MathF.Max(1, S(1)));

    public void Separator(UiRect r, bool vertical = false)
    {
        float t = MathF.Max(1, S(1));
        if (vertical) Draw.Rect(MathF.Round(r.Centre.X), r.Y0, MathF.Round(r.Centre.X) + t, r.Y1, Theme.Border);
        else Draw.Rect(r.X0, MathF.Round(r.Centre.Y), r.X1, MathF.Round(r.Centre.Y) + t, Theme.Border);
    }

    /// <summary>Залитый треугольник-стрелка размером <paramref name="size"/> с центром в <paramref name="c"/>.</summary>
    public void Arrow(Vector2 c, float size, UiDir dir, uint colour)
    {
        float h = size * 0.5f, n = size * 0.3f;
        switch (dir)
        {
            case UiDir.Down: Draw.Triangle(new(c.X - h, c.Y - n), new(c.X + h, c.Y - n), new(c.X, c.Y + n * 1.4f), colour); break;
            case UiDir.Up: Draw.Triangle(new(c.X - h, c.Y + n), new(c.X, c.Y - n * 1.4f), new(c.X + h, c.Y + n), colour); break;
            case UiDir.Right: Draw.Triangle(new(c.X - n, c.Y - h), new(c.X + n * 1.4f, c.Y), new(c.X - n, c.Y + h), colour); break;
            default: Draw.Triangle(new(c.X + n, c.Y - h), new(c.X + n, c.Y + h), new(c.X - n * 1.4f, c.Y), colour); break;
        }
    }

    public void StatusDot(Vector2 centre, uint colour, float radius = 4) => Draw.Circle(centre, S(radius), colour);
}
