using System.Numerics;
using System.Text;

namespace Sword.Engine.Ui;

/// <summary>
/// Клавиши, которые разбирают виджеты и редакторы на них; остальные — дело приложения. Набор — <c>UiKey</c> SWORD
/// 2eab13b (клавиши его редакторов — тоже: их сопоставит адаптер ввода клиента).
/// </summary>
public enum UiKey
{
    Tab, Enter, Escape, Backspace, Delete,
    Left, Right, Up, Down, Home, End, PageUp, PageDown, Space,
    A, C, S, V, X, Y, Z,
    D0, K, L, R, Plus, Minus,
    F2, F5, F7,
    Count,
}

/// <summary>
/// Снимок ввода кадра для <see cref="UiContext"/>, без привязки к платформе: заполняет клиент (адаптер окна — точка 6е,
/// в SWORD <c>UiInputWin32</c>), в тестах — сам тест. Клавиши — нажатие или автоповтор удержания за кадр.
/// Перенос <c>Widgets/UiInput.cs</c> SWORD 2eab13b.
/// </summary>
public sealed class UiInput
{
    private readonly bool[] _down = new bool[3], _pressed = new bool[3], _released = new bool[3];
    private readonly bool[] _keys = new bool[(int)UiKey.Count];

    /// <summary>Курсор в пикселях экрана (те же единицы, что прямоугольники виджетов).</summary>
    public Vector2 Mouse;
    public bool MouseInside;
    /// <summary>Щелчки колеса за кадр, вверх — положительные. Прокрутка забирает их себе (обнуляет).</summary>
    public float Wheel;
    /// <summary>Секунды с запуска — для мигания каретки, двойного щелчка и подсказок.</summary>
    public double Time;
    public bool Ctrl, Shift, Alt;
    /// <summary>Введённые символы кадра (раскладка уже применена).</summary>
    public readonly StringBuilder Typed = new();
    public Func<string?>? GetClipboard;
    public Action<string>? SetClipboard;

    /// <summary>
    /// В кадре был ввод: движение мыши, кнопка, колесо, клавиша или символ. Окно, которое рисует по событиям,
    /// после такого кадра рисует ещё один — немедленный режим видит последствия ввода кадром позже.
    /// </summary>
    public bool HadEvents;

    /// <summary>Сбросить события прошлого кадра (нажатия, отпускания, клавиши, символы, колесо); зажатые кнопки остаются.</summary>
    public void BeginFrame()
    {
        HadEvents = false;
        Array.Clear(_pressed);
        Array.Clear(_released);
        Array.Clear(_keys);
        Typed.Clear();
        Wheel = 0;
    }

    public void SetMouseButton(int button, bool down, bool pressed, bool released)
    {
        _down[button] = down;
        _pressed[button] = pressed;
        _released[button] = released;
    }

    public void PressKey(UiKey key) => _keys[(int)key] = true;

    /// <summary>Кнопка мыши зажата (0 — левая, 1 — правая, 2 — средняя).</summary>
    public bool Down(int button) => _down[button];
    public bool Pressed(int button) => _pressed[button];
    public bool Released(int button) => _released[button];

    public bool Key(UiKey key) => _keys[(int)key];

    /// <summary>Забрать нажатие: следующие проверки этого кадра его не увидят.</summary>
    public bool TakeKey(UiKey key)
    {
        bool v = _keys[(int)key];
        _keys[(int)key] = false;
        return v;
    }
}
