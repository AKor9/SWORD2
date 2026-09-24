namespace Sword.Engine.Ui;

/// <summary>
/// Видимые строки списка <see cref="UiContext.BeginList"/>: номера [<see cref="First"/>, <see cref="Last"/>),
/// прямоугольник строки — <see cref="Row"/>. Список без делегата — без замыкания и выделений в кадре.
/// </summary>
public readonly struct UiListRows
{
    private readonly UiRect _content;
    private readonly float _rowHeight;

    public UiListRows(int first, int last, UiRect content, float rowHeight)
    {
        First = first;
        Last = last;
        _content = content;
        _rowHeight = rowHeight;
    }

    public int First { get; }
    public int Last { get; }

    /// <summary>Прямоугольник строки <paramref name="index"/> (сдвинут прокруткой).</summary>
    public UiRect Row(int index) =>
        new(_content.X0, _content.Y0 + index * _rowHeight, _content.X1, _content.Y0 + (index + 1) * _rowHeight);
}

public sealed partial class UiContext
{
    // ── прокрутка и списки ─────────────────────────────────────────────

    private sealed class ScrollState
    {
        public float Offset;
        public float Grab;
    }

    private readonly Stack<(uint Id, UiRect View, float Content, ScrollState State)> _scrolls = new();

    /// <summary>
    /// Прокручиваемая область высоты <paramref name="contentHeight"/> в окне <paramref name="view"/>:
    /// возвращает прямоугольник содержимого (сдвинут прокруткой, сужен под полосу), рисовать в нём до <see cref="EndScroll"/>.
    /// Колесо над окном прокручивает на три строки темы и забирается (<see cref="UiInput.Wheel"/> = 0).
    /// </summary>
    public UiRect BeginScroll(UiRect view, ReadOnlySpan<char> label, float contentHeight)
    {
        uint id = Id(label);
        var st = State<ScrollState>(id);
        float max = MathF.Max(0, contentHeight - view.Height);
        if (Input.Wheel != 0 && IsHovered(view))
        {
            st.Offset -= Input.Wheel * S(Theme.RowHeight) * 3;
            Input.Wheel = 0;
        }
        st.Offset = Math.Clamp(st.Offset, 0, max);
        _scrolls.Push((id, view, contentHeight, st));
        Draw.PushClip(view);
        float right = max > 0 ? view.X1 - S(Theme.ScrollbarWidth) - S(3) : view.X1;
        return new UiRect(view.X0, view.Y0 - st.Offset, right, view.Y0 - st.Offset + contentHeight);
    }

    /// <summary>Закрыть область прокрутки: снять обрезку, нарисовать полосу и ловить её протяжку.</summary>
    public void EndScroll()
    {
        var (id, view, content, st) = _scrolls.Pop();
        Draw.PopClip();
        float max = MathF.Max(0, content - view.Height);
        if (max <= 0) return;

        float bw = S(Theme.ScrollbarWidth);
        var track = new UiRect(view.X1 - bw, view.Y0, view.X1, view.Y1);
        float thumbH = MathF.Min(track.Height, MathF.Max(S(24), track.Height * view.Height / content));
        float travel = MathF.Max(1, track.Height - thumbH);
        uint thumbId = id ^ 0x5CB0115u;
        bool hovered = IsHovered(track) && (ActiveId == 0 || ActiveId == thumbId);
        float ty = track.Y0 + travel * st.Offset / max;
        if (hovered && Input.Pressed(0))
        {
            ActiveId = thumbId;
            st.Grab = Input.Mouse.Y >= ty && Input.Mouse.Y < ty + thumbH ? Input.Mouse.Y - ty : thumbH * 0.5f;
        }
        if (ActiveId == thumbId)
        {
            if (Input.Down(0) || Input.Pressed(0))
            {
                st.Offset = Math.Clamp((Input.Mouse.Y - st.Grab - track.Y0) / travel * max, 0, max);
                ty = track.Y0 + travel * st.Offset / max;
            }
            else ActiveId = 0;
        }
        uint colour = ActiveId == thumbId ? Theme.ScrollbarActive : hovered ? Theme.ScrollbarHot : Theme.Scrollbar;
        Draw.Rect(track.X0, ty, track.X1, ty + thumbH, colour, bw * 0.5f);
    }

    /// <summary>Прокрутить область так, чтобы полоса содержимого [y0, y1) была видна в окне высоты <paramref name="viewHeight"/>.</summary>
    public void ScrollIntoView(ReadOnlySpan<char> label, float y0, float y1, float viewHeight)
    {
        var st = State<ScrollState>(Id(label));
        if (y0 < st.Offset) st.Offset = y0;
        else if (y1 > st.Offset + viewHeight) st.Offset = y1 - viewHeight;
    }

    /// <summary>
    /// Список из <paramref name="count"/> строк одной высоты в прокручиваемом окне: возвращает видимые строки, рисовать
    /// только их (<c>for (int i = rows.First; i &lt; rows.Last; i++) … rows.Row(i)</c>), закрывать — <see cref="EndList"/>.
    /// </summary>
    public UiListRows BeginList(UiRect view, ReadOnlySpan<char> label, int count, float rowHeight)
    {
        var content = BeginScroll(view, label, count * rowHeight);
        int first = 0, last = 0;
        if (count > 0 && rowHeight > 0)
        {
            first = Math.Max(0, (int)((view.Y0 - content.Y0) / rowHeight));
            last = Math.Min(count, (int)MathF.Ceiling((view.Y1 - content.Y0) / rowHeight));
        }
        return new UiListRows(first, last, content, rowHeight);
    }

    public void EndList() => EndScroll();

    /// <summary>
    /// Список из <paramref name="count"/> строк одной высоты: рисуются только видимые. Делегат с замыканием — выделение
    /// на вызов; в кадре без выделений — <see cref="BeginList"/>.
    /// </summary>
    public void VirtualList(UiRect view, ReadOnlySpan<char> label, int count, float rowHeight, Action<int, UiRect> row)
    {
        var rows = BeginList(view, label, count, rowHeight);
        for (int i = rows.First; i < rows.Last; i++) row(i, rows.Row(i));
        EndList();
    }

    /// <summary>Строка списка: подсветка наведения и выбора; выбор — по нажатию.</summary>
    /// <returns>Строку нажали в этом кадре.</returns>
    public bool Selectable(UiRect r, uint id, bool selected)
    {
        bool hovered = IsHovered(r) && (ActiveId == 0 || ActiveId == id);
        bool pressed = hovered && Input.Pressed(0);
        if (pressed) ActiveId = id;
        if (selected) Draw.Rect(r, Theme.RowSelected, S(3));
        else if (hovered) Draw.Rect(r, Theme.RowHover, S(3));
        return pressed;
    }
}
