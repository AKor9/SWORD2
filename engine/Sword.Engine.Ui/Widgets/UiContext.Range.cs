using System.Numerics;

namespace Sword.Engine.Ui;

public sealed partial class UiContext
{
    // ── перетаскивание ─────────────────────────────────────────────────

    /// <summary>Состояние перетаскивания: смещение точки захвата от ручки (разделитель, ползунок).</summary>
    private sealed class DragState
    {
        public float Grab;
    }

    /// <summary>
    /// Разделитель панелей: перетаскивание меняет <paramref name="position"/> (X для вертикальной полосы, Y — для горизонтальной).
    /// </summary>
    /// <returns>Положение изменилось.</returns>
    public bool Splitter(UiRect bar, ReadOnlySpan<char> label, ref float position, bool vertical, float min, float max)
    {
        uint id = Id(label);
        var st = State<DragState>(id);
        bool hovered = IsHovered(bar) && (ActiveId == 0 || ActiveId == id);
        float mouse = vertical ? Input.Mouse.X : Input.Mouse.Y;
        if (hovered && Input.Pressed(0))
        {
            ActiveId = id;
            st.Grab = mouse - position;
        }
        bool changed = false;
        if (ActiveId == id)
        {
            if (Input.Down(0))
            {
                float p = Math.Clamp(mouse - st.Grab, min, MathF.Max(min, max));
                changed = p != position;
                position = p;
            }
            else ActiveId = 0;
        }
        if (hovered || ActiveId == id)
        {
            Cursor = vertical ? UiCursor.SizeWE : UiCursor.SizeNS;
            uint c = ActiveId == id ? Theme.Accent : Theme.BorderStrong;
            if (vertical) Draw.Rect(bar.Centre.X - S(1), bar.Y0, bar.Centre.X + S(1), bar.Y1, c);
            else Draw.Rect(bar.X0, bar.Centre.Y - S(1), bar.X1, bar.Centre.Y + S(1), c);
        }
        return changed;
    }

    /// <summary>
    /// Горизонтальный ползунок: протяжка или щелчок по дорожке ставит значение в [<paramref name="min"/>, <paramref name="max"/>].
    /// <paramref name="log"/> — логарифмическая шкала (при min ≤ 0 или max ≤ min — линейная). Схватили за ручку — тянется
    /// от точки захвата; мышь не сдвинулась на полпикселя — значение не трогается (1× так и остаётся ровно 1).
    /// </summary>
    /// <returns>Значение изменилось.</returns>
    public bool Slider(UiRect r, ReadOnlySpan<char> label, ref float value, float min, float max, bool log = false)
    {
        uint id = Id(label);
        var st = State<DragState>(id);
        log &= min > 0 && max > min;   // на лог. шкале с min ≤ 0 выходил NaN — тогда линейная
        float knob = S(12), x0 = r.X0 + knob * 0.5f, x1 = r.X1 - knob * 0.5f, span = MathF.Max(1, x1 - x0);

        bool hovered = IsHovered(r) && (ActiveId == 0 || ActiveId == id);
        if (hovered && Input.Pressed(0))
        {
            ActiveId = id;
            // схватили за ручку — тянуть от точки захвата, а не прыгать центром ручки под курсор
            float kx0 = x0 + span * SliderT(value, min, max, log);
            st.Grab = MathF.Abs(Input.Mouse.X - kx0) <= knob * 0.5f ? Input.Mouse.X - kx0 : 0;
        }
        bool changed = false;
        if (ActiveId == id)
        {
            if (Input.Down(0) || Input.Pressed(0))
            {
                float t = Math.Clamp((Input.Mouse.X - st.Grab - x0) / span, 0, 1);
                float current = SliderT(value, min, max, log);
                if (MathF.Abs(t - current) * span >= 0.5f)   // мышь не сдвинулась — значение не трогать
                {
                    float v = log ? min * MathF.Pow(max / min, t) : min + (max - min) * t;
                    v = Math.Clamp(v, MathF.Min(min, max), MathF.Max(min, max));   // Pow на краю даёт 2.0000002
                    changed = v != value;
                    value = v;
                }
            }
            else ActiveId = 0;
        }
        float kx = x0 + span * SliderT(value, min, max, log), cy = r.Centre.Y, th = S(2);
        Draw.Rect(x0, cy - th, x1, cy + th, Theme.Field, th);
        Draw.Rect(x0, cy - th, kx, cy + th, Theme.Accent, th);
        Draw.Circle(new Vector2(kx, cy), knob * 0.5f, ActiveId == id ? Theme.AccentActive : hovered ? Theme.AccentHot : Theme.Accent);
        return changed;
    }

    /// <summary>Доля дорожки ползунка под значением, [0, 1]; пустой диапазон или не число — 0.</summary>
    private static float SliderT(float v, float min, float max, bool log) => max <= min || !float.IsFinite(v) ? 0
        : Math.Clamp(log ? MathF.Log(v / min) / MathF.Log(max / min) : (v - min) / (max - min), 0, 1);

    /// <summary>Полоса прогресса; <paramref name="fraction"/> &lt; 0 — неопределённый (бегущий отрезок, просит следующий кадр).</summary>
    public void ProgressBar(UiRect r, float fraction, ReadOnlySpan<char> text = default)
    {
        float radius = r.Height * 0.5f;
        Draw.Rect(r, Theme.Field, radius);
        if (fraction < 0)
        {
            const double period = 1.4;
            float t = (float)(Input.Time % period / period);
            float segment = r.Width * 0.3f;
            float x0 = r.X0 - segment + t * (r.Width + segment);
            Draw.PushClip(r);
            Draw.Rect(x0, r.Y0, x0 + segment, r.Y1, Theme.Accent, radius);
            Draw.PopClip();
            RedrawAfter(1 / 60.0);
            Label(r, text, Theme.Text, UiAlign.Centre, UiFace.Label, Theme.SmallFontSize);
            return;
        }

        float f = Math.Clamp(fraction, 0, 1);
        float fill = f > 0 ? MathF.Min(r.X0 + MathF.Max(r.Height, r.Width * f), r.X1) : r.X0;
        if (f > 0) Draw.Rect(r.X0, r.Y0, fill, r.Y1, Theme.Accent, radius);
        if (text.IsEmpty) return;
        // подпись двумя цветами по границе заливки: тёмная на заливке, светлая на фоне
        Draw.PushClip(r with { X1 = fill });
        Label(r, text, Theme.AccentText, UiAlign.Centre, UiFace.Label, Theme.SmallFontSize);
        Draw.PopClip();
        Draw.PushClip(r with { X0 = fill });
        Label(r, text, Theme.Text, UiAlign.Centre, UiFace.Label, Theme.SmallFontSize);
        Draw.PopClip();
    }
}
