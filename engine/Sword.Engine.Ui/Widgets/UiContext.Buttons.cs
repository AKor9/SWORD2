namespace Sword.Engine.Ui;

public sealed partial class UiContext
{
    // ── кнопки и переключатели ─────────────────────────────────────────

    /// <summary>Кнопка; щелчок — отпускание над ней после нажатия на ней. <paramref name="id"/> — ID вместо подписи.</summary>
    public bool Button(UiRect r, ReadOnlySpan<char> label, bool primary = false, bool enabled = true, string? id = null)
    {
        bool hovered = false, held = false;
        bool clicked = enabled && Clickable(r, id is null ? Id(label) : Id(id), out hovered, out held);
        uint bg = primary
            ? held ? Theme.AccentActive : hovered ? Theme.AccentHot : Theme.Accent
            : held ? Theme.ButtonActive : hovered ? Theme.ButtonHot : Theme.Button;
        uint fg = primary ? Theme.AccentText : Theme.Text;
        if (!enabled)
        {
            bg = UiDrawList.WithAlpha(bg, 0.45f);
            fg = UiDrawList.WithAlpha(fg, 0.5f);
        }
        Draw.Rect(r, bg, S(Theme.Radius));
        Label(r.Shrink(S(6), 0), label, fg, UiAlign.Centre, UiFace.Label);
        return clicked;
    }

    /// <summary>Флажок с подписью; щелчок — по всей строке.</summary>
    /// <returns>Значение переключено в этом кадре.</returns>
    public bool Toggle(UiRect r, ReadOnlySpan<char> label, ref bool value, string? id = null)
    {
        bool clicked = Clickable(r, id is null ? Id(label) : Id(id), out bool hovered, out _);
        if (clicked) value = !value;
        float box = S(16), by = MathF.Round(r.Y0 + (r.Height - box) * 0.5f);
        var br = new UiRect(r.X0, by, r.X0 + box, by + box);
        if (value)
        {
            Draw.Rect(br, hovered ? Theme.AccentHot : Theme.Accent, S(3));
            Draw.Line(new(br.X0 + box * 0.24f, br.Y0 + box * 0.52f), new(br.X0 + box * 0.42f, br.Y0 + box * 0.72f), S(2), Theme.AccentText);
            Draw.Line(new(br.X0 + box * 0.42f, br.Y0 + box * 0.72f), new(br.X0 + box * 0.78f, br.Y0 + box * 0.30f), S(2), Theme.AccentText);
        }
        else
        {
            Draw.Rect(br, hovered ? Theme.ButtonHot : Theme.Field, S(3));
            Draw.RectStroke(br, Theme.BorderStrong, S(1), S(3));
        }
        Label(new UiRect(br.X1 + S(8), r.Y0, r.X1, r.Y1), label, hovered ? Theme.Text : Theme.TextDim);
        return clicked;
    }
}
