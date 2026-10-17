using System.Numerics;

namespace Sword.Engine.Ui;

/// <summary>Составные строки — общие для виджетов <see cref="UiContext"/> и панелей, рисующих списком напрямую (HUD, отладка).</summary>
public sealed partial class UiDrawList
{
    /// <summary>Строка в <paramref name="r"/>: по вертикали — по центру, по горизонтали — по <paramref name="align"/>; не влезает —
    /// от левого края с «…». Возвращает ширину.</summary>
    public float Label(UiRect r, UiFace face, float sizePx, uint colour, ReadOnlySpan<char> text, UiAlign align = UiAlign.Left)
    {
        if (text.IsEmpty || r.Width <= 0) return 0;
        float y = r.Y0 + (r.Height - LineHeight(face, sizePx)) * 0.5f;
        float w = Measure(face, sizePx, text);
        if (w > r.Width) return TextFit(face, sizePx, new Vector2(r.X0, y), colour, text, r.Width);
        float x = align switch
        {
            UiAlign.Centre => r.X0 + (r.Width - w) * 0.5f,
            UiAlign.Right => r.X1 - w,
            _ => r.X0,
        };
        return Text(face, sizePx, new Vector2(x, y), colour, text);
    }

    /// <summary>Заголовок секции в <paramref name="r"/>: подпись начертанием Label и линия толщиной <paramref name="rule"/>
    /// по центру строки — от подписи через <paramref name="gap"/> до правого края.</summary>
    public void SectionHeader(UiRect r, ReadOnlySpan<char> title, float sizePx, uint textColour, uint ruleColour, float gap, float rule)
    {
        float w = Label(r, UiFace.Label, sizePx, textColour, title);
        float y = MathF.Round(r.Centre.Y);
        Rect(r.X0 + w + gap, y, r.X1, y + rule, ruleColour);
    }

    /// <summary>
    /// Строка «подпись — значение»: подпись слева (Body), значение справа (Num), обе по центру по вертикали;
    /// <paramref name="split"/> — доля ширины под подпись. Значение не влезло в остаток — подпись уступает ему всё, кроме
    /// своего текста и <paramref name="gap"/>.
    /// </summary>
    public void InfoRow(UiRect r, ReadOnlySpan<char> label, float labelSize, uint labelColour, ReadOnlySpan<char> value, float valueSize,
        uint valueColour, float split, float gap)
    {
        float nameW = r.Width * split;
        if (Measure(UiFace.Num, valueSize, value) > r.Width - nameW)
            nameW = MathF.Min(nameW, Measure(UiFace.Body, labelSize, label) + gap);
        var name = r.SplitLeft(nameW, out var rest);
        Label(name, UiFace.Body, labelSize, labelColour, label);
        Label(rest, UiFace.Num, valueSize, valueColour, value, UiAlign.Right);
    }

    /// <summary>Размер строки на подложке: текст и поля <paramref name="pad"/> с каждой стороны.</summary>
    public Vector2 TextBoxSize(UiFace face, float sizePx, ReadOnlySpan<char> text, Vector2 pad) =>
        new(Measure(face, sizePx, text) + pad.X * 2, LineHeight(face, sizePx) + pad.Y * 2);

    /// <summary>Строка на подложке <paramref name="box"/> (заливка <paramref name="fill"/>, рамка — при <paramref name="borderWidth"/> &gt; 0):
    /// текст с полем <paramref name="padX"/> слева, по центру по вертикали, не влез — с «…»; текст — последним.</summary>
    public void TextBox(UiRect box, UiFace face, float sizePx, uint colour, ReadOnlySpan<char> text, uint fill, float padX, float radius,
        uint border = 0, float borderWidth = 0)
    {
        Rect(box, fill, radius);
        if (borderWidth > 0) RectStroke(box, border, borderWidth, radius);
        Label(box.Shrink(padX, 0), face, sizePx, colour, text);
    }
}
