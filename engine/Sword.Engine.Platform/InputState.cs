namespace Sword.Engine.Platform;

/// <summary>
/// Состояние клавиатуры и мыши за кадр. Платформа кормит его из своих сообщений (методы <c>internal</c> открыты
/// реализациям платформы через InternalsVisibleTo), остальные только читают.
/// </summary>
public sealed class InputState
{
    private const int MouseButtons = 3;

    private readonly bool[] _down = new bool[256];
    private readonly bool[] _mouse = new bool[MouseButtons];

    // Фронты нажатия за кадр: не «уровень сейчас и уровень тогда», а «нажатие было». Пара down+up внутри одного
    // кадра при сравнении уровней не видна.
    private readonly bool[] _pressed = new bool[256];

    // Фронт отпускания клавиши — как у мыши: короткий тап (нажата и отпущена в одном кадре) по уровням не виден.
    private readonly bool[] _released = new bool[256];
    private readonly bool[] _mousePressed = new bool[MouseButtons];

    // Автоповтор удерживаемой клавиши. Отдельно от фронта: переключателям повтор не нужен — удержание щёлкало бы
    // ими тридцать раз в секунду, — а полю ввода и списку нужен: удержанный Backspace стирает, стрелка листает.
    private readonly bool[] _repeated = new bool[256];

    // Фронт отпускания — по той же причине, что и фронт нажатия. Он отличает ЩЕЛЧОК правой кнопкой (приказ) от её
    // ПЕРЕТАСКИВАНИЯ (панорама): короткий щелчок целиком укладывается в один кадр, и по уровням его не видно.
    private readonly bool[] _mouseReleased = new bool[MouseButtons];

    public float MouseX { get; private set; }
    public float MouseY { get; private set; }
    public float MouseDeltaX { get; private set; }
    public float MouseDeltaY { get; private set; }
    public float Wheel { get; private set; }

    private float _lastX, _lastY;
    private bool _hasLast;

    // Введённые символы. Отдельно от кодов клавиш: раскладку, модификаторы и мёртвые клавиши разбирает система,
    // а нужен результат.
    private readonly Queue<char> _typed = new();

    /// <summary>Начало кадра: фронты и дельты обнуляются.</summary>
    internal void BeginFrame()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
        Array.Clear(_repeated);
        Array.Clear(_mousePressed);
        Array.Clear(_mouseReleased);
        MouseDeltaX = 0;
        MouseDeltaY = 0;
        Wheel = 0;

        // Символы, которые никто не выгреб за прошлый кадр, выбрасываются: иначе без потребителя очередь росла бы
        // до конца запуска.
        _typed.Clear();
    }

    /// <summary>
    /// Фокус потерян: настоящие отпускания уйдут другому окну, поэтому всё удерживаемое считается отпущенным — с
    /// фронтом отпускания, иначе у действия было бы нажатие без пары. Фронты этого кадра — факты (как в
    /// <see cref="ForgetMouse"/>): нажатие, случившееся до потери фокуса, остаётся.
    /// </summary>
    internal void ForgetAll()
    {
        for (int i = 0; i < _down.Length; i++)
            if (_down[i]) _released[i] = true;
        for (int b = 0; b < MouseButtons; b++)
            if (_mouse[b]) _mouseReleased[b] = true;
        Array.Clear(_down);
        Array.Clear(_repeated);
        ForgetMouse();
        _typed.Clear();

        // Иначе первое же движение после возврата даст дельту через пол-экрана, и камера прыгнет.
        _hasLast = false;
    }

    /// <summary>
    /// Забыть, что кнопки мыши УДЕРЖИВАЮТСЯ. Фронты нажатия при этом остаются.
    ///
    /// Уровень — «кнопка зажата прямо сейчас», и он врёт, когда отпускание ушло другому окну. Фронт — «нажатие
    /// в этом кадре БЫЛО», и это факт, который потерей захвата не отменяется. Окно отпускает захват, когда отжата
    /// последняя кнопка, и система отвечает на это сообщением о смене захвата в той же прокачке очереди: сотри мы
    /// здесь фронт — щелчок, целиком уместившийся в один кадр, пропадал бы молча.
    /// </summary>
    internal void ForgetMouse() => Array.Clear(_mouse);

    /// <summary>Зажата ли кнопка мыши — любая. По этому платформа держит захват мыши.</summary>
    internal bool AnyMouseDown() => _mouse[0] || _mouse[1] || _mouse[2];

    internal void AddTypedChar(char c)
    {
        // Управляющие символы интерфейсу не нужны: забой и перевод строки приходят отдельно кодами клавиш.
        if (c >= ' ' && c != 0x7F) _typed.Enqueue(c);
    }

    /// <summary>Отдаёт накопленные символы потребителю и очищает очередь.</summary>
    public void DrainTypedChars(Action<char> consume)
    {
        while (_typed.Count > 0) consume(_typed.Dequeue());
    }

    internal void SetKey(int code, bool down)
    {
        int i = code & 0xFF;
        // Фронт запоминается ОТДЕЛЬНО от уровня: сравнение уровней теряло нажатие, случившееся и отпущенное внутри
        // одного кадра — на просевшем кадре короткий тап пропадал молча.
        if (down && !_down[i]) _pressed[i] = true;
        if (!down && _down[i]) _released[i] = true;
        _down[i] = down;
    }

    /// <summary>Система повторила нажатие удерживаемой клавиши.</summary>
    internal void RepeatKey(int code) => _repeated[code & 0xFF] = true;

    internal void SetMouseButton(MouseButton button, bool down)
    {
        int b = (int)button;
        if (down && !_mouse[b]) _mousePressed[b] = true;

        // Отпускание засчитывается только с ЗАЖАТОЙ кнопки. Если уровень уже стёрт потерей захвата
        // (<see cref="ForgetMouse"/>), то отпускание, пришедшее следом, — хвост чужого жеста, и щелчком оно не будет.
        if (!down && _mouse[b]) _mouseReleased[b] = true;
        _mouse[b] = down;
    }

    internal void SetMousePosition(float x, float y)
    {
        if (_hasLast)
        {
            MouseDeltaX += x - _lastX;
            MouseDeltaY += y - _lastY;
        }
        _lastX = x;
        _lastY = y;
        _hasLast = true;
        MouseX = x;
        MouseY = y;
    }

    internal void AddWheel(float delta) => Wheel += delta;

    /// <summary>Курсор принудительно перенесён: следующее движение считается от этой точки.</summary>
    internal void AnchorMouse(float x, float y)
    {
        _lastX = x;
        _lastY = y;
        _hasLast = true;
        MouseX = x;
        MouseY = y;
    }

    public bool IsDown(Key key) => _down[(int)key & 0xFF];

    /// <summary>Клавиша нажата именно в этом кадре — для переключателей.</summary>
    public bool WasPressed(Key key) => _pressed[(int)key & 0xFF];

    /// <summary>Клавиша отпущена именно в этом кадре.</summary>
    public bool WasReleased(Key key) => _released[(int)key & 0xFF];

    /// <summary>Нажата в этом кадре или повторена удержанием — для полей ввода и списков.</summary>
    public bool WasPressedOrRepeated(Key key) => _pressed[(int)key & 0xFF] || _repeated[(int)key & 0xFF];

    public bool IsMouseDown(MouseButton button) => _mouse[(int)button];

    public bool WasMousePressed(MouseButton button) => _mousePressed[(int)button];

    /// <summary>Кнопка мыши отпущена именно в этом кадре.</summary>
    public bool WasMouseReleased(MouseButton button) => _mouseReleased[(int)button];
}
