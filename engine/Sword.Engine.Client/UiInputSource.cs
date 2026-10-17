using System.Numerics;
using Sword.Engine.Platform;
using Sword.Engine.Ui;

namespace Sword.Engine.Client;

/// <summary>
/// Адаптер ввода интерфейса: <see cref="InputState"/> окна за кадр → <see cref="UiInput"/> виджетов, курсор виджетов →
/// курсор окна. Платформа UI не знает. Без выделений в кадре: символы выгребаются заранее созданным делегатом.
/// </summary>
public sealed class UiInputSource
{
    /// <summary>Клавиши виджетов и их клавиши платформы.</summary>
    private static readonly (Key Key, UiKey Ui)[] Keys =
    [
        (Key.Tab, UiKey.Tab), (Key.Enter, UiKey.Enter), (Key.Escape, UiKey.Escape),
        (Key.Backspace, UiKey.Backspace), (Key.Delete, UiKey.Delete),
        (Key.Left, UiKey.Left), (Key.Right, UiKey.Right), (Key.Up, UiKey.Up), (Key.Down, UiKey.Down),
        (Key.Home, UiKey.Home), (Key.End, UiKey.End), (Key.PageUp, UiKey.PageUp), (Key.PageDown, UiKey.PageDown),
        (Key.Space, UiKey.Space),
        (Key.A, UiKey.A), (Key.C, UiKey.C), (Key.S, UiKey.S), (Key.V, UiKey.V), (Key.X, UiKey.X), (Key.Y, UiKey.Y), (Key.Z, UiKey.Z),
        (Key.D0, UiKey.D0), (Key.K, UiKey.K), (Key.L, UiKey.L), (Key.R, UiKey.R), (Key.Plus, UiKey.Plus), (Key.Minus, UiKey.Minus),
        (Key.F2, UiKey.F2), (Key.F5, UiKey.F5), (Key.F7, UiKey.F7),
    ];

    private readonly Action<char> _typed;

    public UiInputSource()
    {
        _typed = c => Input.Typed.Append(c);
    }

    /// <summary>Снимок ввода интерфейса; заполняется <see cref="Fill"/> раз в кадр.</summary>
    public UiInput Input { get; } = new();

    /// <summary>Ввод кадра окна → <see cref="Input"/>, после прокачки сообщений; выгребает введённые символы.</summary>
    /// <param name="time">Секунды с запуска — каретка, двойной щелчок, подсказки.</param>
    public void Fill(InputState state, bool mouseInside, double time)
    {
        var ui = Input;
        var mouse = new Vector2(state.MouseX, state.MouseY);
        bool moved = mouse != ui.Mouse || mouseInside != ui.MouseInside;
        bool ctrl = state.IsDown(Key.Control), shift = state.IsDown(Key.Shift), alt = state.IsDown(Key.Alt);
        bool modifiers = ctrl != ui.Ctrl || shift != ui.Shift || alt != ui.Alt;
        ui.BeginFrame();
        ui.Time = time;
        ui.Mouse = mouse;
        ui.MouseInside = mouseInside;
        ui.Wheel = state.Wheel;
        ui.Ctrl = ctrl;
        ui.Shift = shift;
        ui.Alt = alt;
        bool events = moved || modifiers || state.Wheel != 0;
        for (int b = 0; b < 3; b++)
        {
            var button = (MouseButton)b;
            bool pressed = state.WasMousePressed(button), released = state.WasMouseReleased(button);
            ui.SetMouseButton(b, state.IsMouseDown(button), pressed, released);
            events |= pressed || released;
        }
        foreach (var (key, uiKey) in Keys)
            if (state.WasPressedOrRepeated(key))
            {
                ui.PressKey(uiKey);
                events = true;
            }
        state.DrainTypedChars(_typed);
        ui.HadEvents = events || ui.Typed.Length > 0;
    }

    /// <summary>Курсор интерфейса → курсор окна.</summary>
    public static CursorShape Cursor(UiCursor cursor) => cursor switch
    {
        UiCursor.IBeam => CursorShape.IBeam,
        UiCursor.SizeWE => CursorShape.SizeWE,
        UiCursor.SizeNS => CursorShape.SizeNS,
        UiCursor.SizeAll => CursorShape.SizeAll,
        _ => CursorShape.Arrow,
    };
}
