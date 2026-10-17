using System.Numerics;

namespace Sword.Engine.Ui;

public sealed partial class UiContext
{
    private sealed class TabsState
    {
        public float Offset;
        /// <summary>Выбранная в прошлом кадре — её смена подводит полосу к новой вкладке.</summary>
        public int Shown = -1;
    }

    /// <summary>
    /// Вкладки в строку; выбранная подчёркнута. Не влезли в ширину — полоса прокручивается: колесом над ней,
    /// стрелками по краям и сама, когда выбрана вкладка за краем (клавишей или кодом).
    /// </summary>
    /// <returns>Выбрана другая вкладка.</returns>
    public bool Tabs(UiRect r, ReadOnlySpan<char> label, ref int index, IReadOnlyList<string> tabs)
    {
        PushId(label);
        Draw.Rect(r.X0, r.Y1 - MathF.Max(1, S(1)), r.X1, r.Y1, Theme.Border);
        bool changed = false;
        float fs = S(Theme.FontSize);
        float total = 0;
        Span<float> widths = tabs.Count <= 32 ? stackalloc float[tabs.Count] : new float[tabs.Count];
        for (int i = 0; i < tabs.Count; i++)
        {
            widths[i] = Draw.Measure(UiFace.Label, fs, tabs[i]) + S(24);
            total += widths[i];
        }

        var st = State<TabsState>(Id("tabs.scroll"));
        float arrow = S(14);
        bool scroll = total > r.Width;
        var strip = scroll ? new UiRect(r.X0 + arrow, r.Y0, r.X1 - arrow, r.Y1) : r;
        float max = MathF.Max(0, total - strip.Width);

        if (scroll)
        {
            st.Offset -= TakeWheel(r) * S(40);
            // выбор сменился (клавиша, код, ставшая невидимой вкладка) — подвести полосу к нему
            if (st.Shown != index)
            {
                float x0 = 0;
                for (int i = 0; i < index && i < tabs.Count; i++) x0 += widths[i];
                float x1 = x0 + (index >= 0 && index < tabs.Count ? widths[index] : 0);
                if (x0 < st.Offset) st.Offset = x0;
                else if (x1 > st.Offset + strip.Width) st.Offset = x1 - strip.Width;
            }
        }
        else st.Offset = 0;
        st.Offset = Math.Clamp(st.Offset, 0, max);
        st.Shown = index;

        Draw.PushClip(strip);
        float x = strip.X0 - st.Offset;
        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = new UiRect(x, r.Y0, x + widths[i], r.Y1);
            x += widths[i];
            if (tab.X1 < strip.X0 || tab.X0 > strip.X1) continue;   // за краем полосы: не рисовать и не ловить мышь
            if (Clickable(tab, Id(i), out bool hovered, out _) && index != i)
            {
                index = i;
                changed = true;
            }
            bool selected = i == index;
            if (hovered && !selected) Draw.Rect(tab.X0, tab.Y0, tab.X1, tab.Y1 - S(2), Theme.RowHover, S(3));
            Label(tab, tabs[i], selected || hovered ? Theme.Text : Theme.TextDim, UiAlign.Centre, UiFace.Label);
            if (selected) Draw.Rect(tab.X0 + S(6), r.Y1 - S(2), tab.X1 - S(6), r.Y1, Theme.Accent, S(1));
        }
        Draw.PopClip();

        if (scroll)
        {
            ScrollArrow(new UiRect(r.X0, r.Y0, r.X0 + arrow, r.Y1), "tabs.left", UiDir.Left, ref st.Offset, -strip.Width * 0.5f, max);
            ScrollArrow(new UiRect(r.X1 - arrow, r.Y0, r.X1, r.Y1), "tabs.right", UiDir.Right, ref st.Offset, strip.Width * 0.5f, max);
        }

        PopId();
        return changed;
    }

    /// <summary>
    /// Вкладки столбцом: строка высотой <paramref name="rowHeight"/> на вкладку от верха <paramref name="r"/>, выбранная —
    /// подсветка и полоса акцента слева; не влезшая подпись — «…». Не влезшие по высоте строки не рисуются.
    /// </summary>
    /// <returns>Выбрана другая вкладка.</returns>
    public bool TabList(UiRect r, ReadOnlySpan<char> label, ref int index, IReadOnlyList<string> tabs, float rowHeight)
    {
        PushId(label);
        bool changed = false;
        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = new UiRect(r.X0, r.Y0 + i * rowHeight, r.X1, r.Y0 + (i + 1) * rowHeight);
            if (tab.Y1 > r.Y1) break;
            if (Clickable(tab, Id(i), out bool hovered, out _) && index != i)
            {
                index = i;
                changed = true;
            }
            bool selected = i == index;
            RowBackground(tab, selected, hovered);
            if (selected) Draw.Rect(tab.X0, tab.Y0 + S(4), tab.X0 + S(3), tab.Y1 - S(4), Theme.Accent, S(1));
            Label(tab.Shrink(S(10), 0), tabs[i], selected || hovered ? Theme.Text : Theme.TextDim, UiAlign.Left, UiFace.Label);
        }
        PopId();
        return changed;
    }

    /// <summary>Стрелка у края полосы вкладок: щелчок двигает полосу на <paramref name="step"/>; у упора — притушена.</summary>
    private void ScrollArrow(UiRect r, ReadOnlySpan<char> label, UiDir dir, ref float offset, float step, float max)
    {
        bool can = step < 0 ? offset > 0 : offset < max;
        if (can && Clickable(r, Id(label), out _, out _)) offset = Math.Clamp(offset + step, 0, max);
        Arrow(new Vector2(r.Centre.X, r.Centre.Y - S(1)), S(7), dir, can ? Theme.TextDim : Theme.Border);
    }
}
