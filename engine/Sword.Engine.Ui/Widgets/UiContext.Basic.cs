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
        UiFace face = UiFace.Body, float? size = null)
    {
        if (text.IsEmpty || r.Width <= 0) return 0;
        float fs = S(size ?? Theme.FontSize);
        float y = r.Y0 + (r.Height - Draw.LineHeight(face, fs)) * 0.5f;
        uint c = colour ?? Theme.Text;
        float w = Draw.Measure(face, fs, text);
        if (w > r.Width) return Draw.TextFit(face, fs, new Vector2(r.X0, y), c, text, r.Width);
        float x = align switch
        {
            UiAlign.Centre => r.X0 + (r.Width - w) * 0.5f,
            UiAlign.Right => r.X1 - w,
            _ => r.X0,
        };
        return Draw.Text(face, fs, new Vector2(x, y), c, text);
    }

    /// <summary>
    /// Строка-сведение «подпись — значение»: подпись слева приглушённая, значение справа цифровым начертанием, обе
    /// мелким кеглем; <paramref name="split"/> — доля ширины под подпись. Оформление строки <c>TuningInfo</c> панели
    /// ручек SWORD 2eab13b (<c>TuningPanel.DrawItem</c>).
    /// </summary>
    public void InfoRow(UiRect r, ReadOnlySpan<char> label, ReadOnlySpan<char> value, float split = 0.42f, uint? valueColour = null)
    {
        var name = r.SplitLeft(r.Width * split, out var rest);
        Label(name, label, Theme.TextDim, UiAlign.Left, UiFace.Body, Theme.SmallFontSize);
        Label(rest, value, valueColour ?? Theme.Text, UiAlign.Right, UiFace.Num, Theme.SmallFontSize);
    }

    /// <summary>Заголовок секции: мелкая подпись и тонкая линия до края.</summary>
    public void SectionHeader(UiRect r, ReadOnlySpan<char> title)
    {
        float w = Label(r, title, Theme.TextDim, UiAlign.Left, UiFace.Label, Theme.SmallFontSize);
        float y = MathF.Round(r.Centre.Y);
        Draw.Rect(r.X0 + w + S(8), y, r.X1, y + MathF.Max(1, S(1)), Theme.Border);
    }

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
