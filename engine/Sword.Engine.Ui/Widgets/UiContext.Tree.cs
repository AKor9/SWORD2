using System.Numerics;

namespace Sword.Engine.Ui;

public sealed partial class UiContext
{
    private sealed class TreeState
    {
        public bool Open, Known;
    }

    /// <summary>
    /// Строка дерева: отступ по глубине, стрелка (щелчок или двойной щелчок по строке — свернуть/развернуть),
    /// необязательная цветная точка статуса и подпись.
    /// </summary>
    /// <returns>Узел раскрыт (у листа — всегда false).</returns>
    public bool TreeNode(UiRect row, ReadOnlySpan<char> label, ReadOnlySpan<char> text, int depth, bool leaf, bool selected,
        out bool clicked, bool defaultOpen = false, uint? dot = null, uint? textColour = null)
    {
        uint id = Id(label);
        var st = State<TreeState>(id);
        if (!st.Known)
        {
            st.Open = defaultOpen;
            st.Known = true;
        }
        float indent = S(14) * depth;
        var arrow = new UiRect(row.X0 + indent, row.Y0, row.X0 + indent + S(16), row.Y1);
        bool arrowHovered = !leaf && IsHovered(arrow);
        bool arrowPressed = arrowHovered && Input.Pressed(0);
        clicked = Selectable(row, id, selected) && !arrowPressed;
        if (arrowPressed || clicked && DoubleClicked && !leaf) st.Open = !st.Open;

        if (!leaf) Arrow(arrow.Centre, S(8), st.Open ? UiDir.Down : UiDir.Right, arrowHovered ? Theme.Text : Theme.TextDim);
        float x = arrow.X1 + S(2);
        if (dot is { } d)
        {
            Draw.Circle(new Vector2(x + S(4), row.Centre.Y), S(4), d);
            x += S(14);
        }
        Label(new UiRect(x, row.Y0, row.X1 - S(4), row.Y1), text, textColour ?? (selected ? Theme.Text : Theme.TextDim));
        return !leaf && st.Open;
    }

    /// <summary>Свернуть или развернуть узел дерева программно.</summary>
    public void SetTreeOpen(ReadOnlySpan<char> label, bool open)
    {
        var st = State<TreeState>(Id(label));
        st.Open = open;
        st.Known = true;
    }
}
