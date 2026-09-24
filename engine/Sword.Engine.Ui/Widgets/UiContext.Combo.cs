using System.Numerics;

namespace Sword.Engine.Ui;

public sealed partial class UiContext
{
    private sealed class ComboState
    {
        public bool Open;
    }

    /// <summary>Раскрыть выпадающий список программно (с тем же стеком ID, что у вызова <see cref="Combo"/>).</summary>
    public void OpenCombo(ReadOnlySpan<char> label) => State<ComboState>(Id(label)).Open = true;

    /// <summary>
    /// Выпадающий список. Раскрытый список — всплывающее окно под полем (не влезает — над ним, и там не влезает —
    /// прижат к низу экрана); закрывают выбор, щелчок мимо и Escape.
    /// </summary>
    /// <returns>Выбран другой элемент.</returns>
    public bool Combo(UiRect r, ReadOnlySpan<char> label, ref int index, IReadOnlyList<string> items)
    {
        uint id = Id(label);
        var st = State<ComboState>(id);
        if (Clickable(r, id, out bool hovered, out bool held)) st.Open = !st.Open;
        Draw.Rect(r, held ? Theme.ButtonActive : hovered || st.Open ? Theme.ButtonHot : Theme.Button, S(Theme.Radius));
        ReadOnlySpan<char> current = index >= 0 && index < items.Count ? items[index] : "";
        Label(new UiRect(r.X0 + S(8), r.Y0, r.X1 - S(22), r.Y1), current, Theme.Text);
        Arrow(new Vector2(r.X1 - S(12), r.Centre.Y), S(8), st.Open ? UiDir.Up : UiDir.Down, Theme.TextDim);
        if (!st.Open) return false;

        float rowH = S(Theme.RowHeight);
        float h = rowH * Math.Clamp(items.Count, 1, 12) + S(8);
        // Список держится в экране: за краем его срезал бы scissor, и щёлкать было бы не по чему.
        float w = MathF.Min(MathF.Max(r.Width, S(160)), Width);
        var popup = UiRect.FromSize(Math.Clamp(r.X0, 0, MathF.Max(0, Width - w)), r.Y1 + S(2), w, h);
        if (popup.Y1 > Height)
            popup = r.Y0 - S(2) - h >= 0
                ? popup with { Y0 = r.Y0 - S(2) - h, Y1 = r.Y0 - S(2) }   // не влезает снизу — над полем
                : popup.Offset(0, Height - popup.Y1);                     // и там не влезает — прижать к низу экрана

        int picked = -1;
        BeginPopup(popup);
        PushId(label);
        var rows = BeginList(popup.Shrink(S(4)), "items", items.Count, rowH);
        for (int i = rows.First; i < rows.Last; i++)
        {
            var row = rows.Row(i);
            if (Selectable(row, Id(i), i == index)) picked = i;
            Label(row.Shrink(S(6), 0), items[i], i == index ? Theme.Text : Theme.TextDim);
        }
        EndList();
        PopId();
        EndPopup();

        if (picked >= 0)
        {
            bool changed = picked != index;
            index = picked;
            st.Open = false;
            return changed;
        }
        if (Input.Pressed(0) && !popup.Contains(Input.Mouse) && !r.Contains(Input.Mouse) || Input.TakeKey(UiKey.Escape))
            st.Open = false;
        return false;
    }
}
