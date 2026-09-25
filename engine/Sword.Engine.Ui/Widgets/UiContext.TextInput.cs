using System.Globalization;
using System.Numerics;

namespace Sword.Engine.Ui;

public sealed partial class UiContext
{
    // ── поля ввода ─────────────────────────────────────────────────────

    /// <summary>Отступ текста поля ввода от левого и правого края, логические пиксели: место под текст — ширина поля минус два отступа.</summary>
    public const float FieldPadding = 7;

    /// <summary>
    /// Однострочное поле ввода. Щелчок — фокус и каретка, протяжка и Shift — выделение, двойной щелчок — слово;
    /// Ctrl+A/C/X/V, Ctrl+←/→/Backspace/Delete — по словам; Enter и Escape снимают фокус (чем закончилась правка —
    /// <see cref="UiTextEdit.LastExit"/> в <c>State&lt;UiTextEdit&gt;(Id(label))</c>).
    /// </summary>
    /// <returns>Текст изменился в этом кадре.</returns>
    public bool TextField(UiRect r, ReadOnlySpan<char> label, ref string text, ReadOnlySpan<char> placeholder = default) =>
        TextField(r, Id(label), ref text, placeholder);

    private bool TextField(UiRect r, uint id, ref string text, ReadOnlySpan<char> placeholder)
    {
        var st = State<UiTextEdit>(id);
        bool focused = FocusId == id;
        bool hovered = IsHovered(r);
        if (hovered) Cursor = UiCursor.IBeam;

        float fs = S(Theme.FontSize);
        var inner = r.Shrink(S(FieldPadding), 0);
        int IndexAtMouse(string t) => Draw.IndexAtX(UiFace.Body, fs, t, Input.Mouse.X - inner.X0 + st.ScrollX);

        if (hovered && Input.Pressed(0) && (ActiveId == 0 || ActiveId == id))
        {
            if (!focused) st.LastExit = UiEditExit.None;
            Focus(id);
            focused = true;
            ActiveId = id;
            int at = IndexAtMouse(text);
            if (DoubleClicked) st.SelectWord(text, at);
            else st.MoveTo(text, at, Input.Shift);
            // кнопка отпускается кадры спустя: протяжка в них сжала бы слово до каретки у мыши
            st.WordPress = DoubleClicked;
            st.LastChange = Input.Time;
        }
        else if (focused && Input.Pressed(0) && !hovered)
        {
            st.LastExit = UiEditExit.Blur;   // фокус снимет EndFrame, если его не заберёт другой виджет
        }
        if (ActiveId == id)
        {
            if (Input.Down(0) && !st.WordPress) st.MoveTo(text, IndexAtMouse(text), extend: true);
            else if (!Input.Down(0)) ActiveId = 0;
        }

        bool changed = false;
        if (focused)
        {
            st.Clamp(text);
            string before = text;
            int caretBefore = st.Caret, anchorBefore = st.Anchor;
            // Ctrl+клавиша — сочетание, а не ввод; но Ctrl+Alt — это AltGr, и он ВВОДИТ символ
            // (Windows шлёт WM_CHAR только когда такой символ есть в раскладке).
            bool ctrl = Input.Ctrl && !Input.Alt;   // сочетание клавиш: правка по словам, Ctrl+A/C/X/V
            if (Input.Typed.Length > 0 && (!Input.Ctrl || Input.Alt)) text = st.Insert(text, Input.Typed.ToString());
            if (Input.TakeKey(UiKey.Backspace)) text = st.Backspace(text, ctrl);
            if (Input.TakeKey(UiKey.Delete)) text = st.Delete(text, ctrl);
            if (Input.TakeKey(UiKey.Left)) st.Left(text, ctrl, Input.Shift);
            if (Input.TakeKey(UiKey.Right)) st.Right(text, ctrl, Input.Shift);
            if (Input.TakeKey(UiKey.Home)) st.MoveTo(text, 0, Input.Shift);
            if (Input.TakeKey(UiKey.End)) st.MoveTo(text, text.Length, Input.Shift);
            if (ctrl && Input.TakeKey(UiKey.A)) st.SelectAll(text);
            if (ctrl && Input.TakeKey(UiKey.C) && st.HasSelection) Input.SetClipboard?.Invoke(st.Selected(text));
            if (ctrl && Input.TakeKey(UiKey.X) && st.HasSelection)
            {
                Input.SetClipboard?.Invoke(st.Selected(text));
                text = st.DeleteSelection(text);
            }
            if (ctrl && Input.TakeKey(UiKey.V) && Input.GetClipboard?.Invoke() is { Length: > 0 } paste)
                text = st.Insert(text, paste);
            if (Input.TakeKey(UiKey.Enter))
            {
                st.LastExit = UiEditExit.Commit;
                FocusId = 0;
            }
            else if (Input.TakeKey(UiKey.Escape))
            {
                st.LastExit = UiEditExit.Cancel;
                FocusId = 0;
            }
            changed = !ReferenceEquals(before, text) && before != text;
            if (changed || st.Caret != caretBefore || st.Anchor != anchorBefore) st.LastChange = Input.Time;
        }
        focused = FocusId == id;
        DrawField(r, text, placeholder, focused, hovered, st);
        return changed;
    }

    /// <summary>Поле ввода: рамка, текст или подсказка-заполнитель; у поля с фокусом — выделение и мигающая каретка.</summary>
    private void DrawField(UiRect r, ReadOnlySpan<char> text, ReadOnlySpan<char> placeholder, bool focused, bool hovered, UiTextEdit st)
    {
        float fs = S(Theme.FontSize);
        var inner = r.Shrink(S(FieldPadding), 0);
        Draw.Rect(r, Theme.Field, S(Theme.Radius));
        Draw.RectStroke(r, focused ? Theme.Accent : hovered ? Theme.BorderStrong : Theme.Border, S(1), S(Theme.Radius));
        float lh = Draw.LineHeight(UiFace.Body, fs);
        float ty = r.Y0 + (r.Height - lh) * 0.5f;
        if (focused)
        {
            float caretX = Draw.CaretX(UiFace.Body, fs, text, st.Caret);
            if (caretX - st.ScrollX > inner.Width - S(1)) st.ScrollX = caretX - inner.Width + S(1);
            if (caretX - st.ScrollX < 0) st.ScrollX = caretX;
            st.ScrollX = MathF.Max(0, MathF.Min(st.ScrollX, MathF.Max(0, Draw.Measure(UiFace.Body, fs, text) - inner.Width + S(1))));
        }
        else st.ScrollX = 0;

        Draw.PushClip(inner.Expand(S(1)));
        float x0 = inner.X0 - st.ScrollX;
        if (text.IsEmpty)
        {
            if (!focused && !placeholder.IsEmpty) Draw.TextFit(UiFace.Body, fs, new Vector2(inner.X0, ty), Theme.TextFaint, placeholder, inner.Width);
        }
        else
        {
            if (focused && st.HasSelection)
            {
                float s0 = Draw.CaretX(UiFace.Body, fs, text, st.SelectionStart), s1 = Draw.CaretX(UiFace.Body, fs, text, st.SelectionEnd);
                Draw.Rect(x0 + s0, ty, x0 + s1, ty + lh, Theme.Selection, S(2));
            }
            Draw.Text(UiFace.Body, fs, new Vector2(x0, ty), Theme.Text, text);
        }
        if (focused)
        {
            const double blink = 0.53;
            double since = Input.Time - st.LastChange;
            if (since % (blink * 2) < blink)
            {
                float cx = MathF.Round(x0 + Draw.CaretX(UiFace.Body, fs, text, st.Caret));
                Draw.Rect(cx, ty, cx + MathF.Max(1, S(1.5f)), ty + lh, Theme.Accent);
            }
            RedrawAfter(blink - since % blink);
        }
        Draw.PopClip();
    }

    private sealed class NumberState
    {
        public string Text = "";
        public bool Editing;
    }

    /// <summary>
    /// Число в поле ввода: значение принимается по Enter или щелчку мимо (не число — остаётся прежнее, число вне
    /// [<paramref name="min"/>, <paramref name="max"/>] — прижимается), Escape — отмена.
    /// Вне правки число рисуется со стека: строка создаётся только щелчком, берущим фокус (в SWORD — каждый кадр).
    /// </summary>
    /// <returns>Значение изменилось.</returns>
    public bool NumberField(UiRect r, ReadOnlySpan<char> label, ref float value, string format = "0.###",
        float min = float.MinValue, float max = float.MaxValue)
    {
        uint editId = Id(label, "#edit");   // ID поля правки SWORD — Id(label + "#edit"), без склейки строк
        // Своё состояние числа — под своим ID («подпись#value»: та же подпись у ползунка рядом его не заденет). В SWORD
        // оно лежало под ID поля правки рядом с UiTextEdit, и State<T> каждый кадр заменял одно другим: правка числа
        // не доживала до следующего кадра.
        var st = State<NumberState>(Id(label, "#value"));
        bool idle = FocusId != editId && !st.Editing;
        if (idle && ActiveId != editId && !(IsHovered(r) && Input.Pressed(0) && ActiveId == 0))
        {
            // не правится и фокус в этом кадре не берёт: то же, что поле без фокуса, строка не нужна
            Span<char> buffer = stackalloc char[64];
            ReadOnlySpan<char> shown = value.TryFormat(buffer, out int n, format, CultureInfo.InvariantCulture)
                ? buffer[..n]
                : value.ToString(format, CultureInfo.InvariantCulture);
            bool hovered = IsHovered(r);
            if (hovered) Cursor = UiCursor.IBeam;
            DrawField(r, shown, default, focused: false, hovered, State<UiTextEdit>(editId));
            return false;
        }
        if (idle) st.Text = value.ToString(format, CultureInfo.InvariantCulture);   // вне правки строка — из значения (SWORD)

        TextField(r, editId, ref st.Text, default);
        var edit = State<UiTextEdit>(editId);
        bool editing = FocusId == editId && edit.LastExit == UiEditExit.None;
        bool changed = false;
        if (st.Editing && !editing)
        {
            if (edit.LastExit != UiEditExit.Cancel
                && float.TryParse(st.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                parsed = Math.Clamp(parsed, min, max);
                changed = parsed != value;
                value = parsed;
            }
            st.Text = value.ToString(format, CultureInfo.InvariantCulture);
        }
        st.Editing = editing;
        return changed;
    }
}
