using System.Numerics;

namespace Sword.Engine.Ui;

/// <summary>Курсор, который просят виджеты кадра; ставит его окну клиент.</summary>
public enum UiCursor { Arrow, IBeam, Hand, SizeWE, SizeNS, SizeAll }

public enum UiAlign { Left, Centre, Right }

public enum UiDir { Up, Down, Left, Right }

/// <summary>
/// Интерфейс в немедленном режиме: каждый кадр приложение заново вызывает виджеты, виджет рисует себя и возвращает, что
/// с ним сделали; состояние виджетов — здесь по ID. Кадр: <see cref="BeginFrame"/> → виджеты → <see cref="EndFrame"/> →
/// <see cref="Output"/>. Всплывающие окна — слоями поверх основного, мышь нижних слоёв под ними не видит.
/// Шрифты не обязательны: без них виджеты с пустыми подписями рисуют одни фигуры (тесты без пака).
/// </summary>
public sealed partial class UiContext
{
    private const uint HashSeed = 2166136261;

    private readonly UiDrawList _main, _overlay, _top;
    private readonly Stack<uint> _ids = new();
    private readonly Dictionary<uint, object> _state = [];
    private UiRect _popupBlock, _nextPopupBlock, _topBlock, _nextTopBlock;
    /// <summary>Слой, в который рисуют сейчас: 0 — основной, 1 — всплывающие окна, 2 — всплывающее внутри всплывающего (список в диалоге).</summary>
    private int _layer;
    private readonly Stack<int> _layers = new();
    private bool _focusClaimed;
    private double _wakeAt = double.PositiveInfinity;
    private double _lastClickTime = double.NegativeInfinity;
    private Vector2 _lastClickPos;

    /// <param name="fonts">Шрифты текста; без них текст виджетов — <see cref="InvalidOperationException"/> (пустые подписи шрифт не трогают).</param>
    public UiContext(UiFonts? fonts = null)
    {
        _main = new UiDrawList(fonts);
        _overlay = new UiDrawList(fonts);
        _top = new UiDrawList(fonts);
        Draw = _main;
    }

    /// <summary>Куда рисуют виджеты сейчас: основной слой или слой всплывающих окон.</summary>
    public UiDrawList Draw { get; private set; }

    /// <summary>Готовый кадр (после <see cref="EndFrame"/>): основной слой, поверх — всплывающие окна и подсказка.</summary>
    public UiDrawList Output => _main;

    public UiTheme Theme { get; set; } = UiTheme.Default;
    public UiInput Input { get; private set; } = new();
    public float Scale { get; private set; } = 1;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public UiRect Screen => new(0, 0, Width, Height);

    /// <summary>Курсор, который просят виджеты этого кадра; приложение ставит его окну.</summary>
    public UiCursor Cursor { get; set; }

    /// <summary>Виджет, захвативший мышь (нажатие на нём ещё не отпущено); 0 — никто.</summary>
    public uint ActiveId { get; set; }

    /// <summary>Виджет с фокусом клавиатуры (поле ввода); 0 — никто.</summary>
    public uint FocusId { get; set; }

    /// <summary>Нажатие левой кнопки этого кадра — второе щелчка за короткое время в том же месте.</summary>
    public bool DoubleClicked { get; private set; }

    /// <summary>Клавиатура занята полем ввода: горячие клавиши приложения не срабатывают.</summary>
    public bool WantsKeyboard => FocusId != 0;

    /// <summary>Через сколько секунд нужен следующий кадр без всякого ввода; ∞ — не нужен.</summary>
    public double WakeIn => Math.Max(0, _wakeAt - Input.Time);

    /// <summary>Логические пиксели темы → пиксели экрана.</summary>
    public float S(float value) => value * Scale;

    public void BeginFrame(UiInput input, int width, int height, float scale)
    {
        Input = input;
        Width = width;
        Height = height;
        Scale = scale;
        _main.Clear();
        _overlay.Clear();
        _top.Clear();
        Draw = _main;
        _layer = 0;
        _layers.Clear();
        _focusClaimed = false;
        _pendingTooltip = null;
        _popupBlock = _nextPopupBlock;
        _nextPopupBlock = default;
        _topBlock = _nextTopBlock;
        _nextTopBlock = default;
        _wakeAt = double.PositiveInfinity;
        // Стеки чистятся в начале кадра: исключение из виджета оставило бы незакрытый BeginScroll, и падал бы каждый EndFrame.
        _ids.Clear();
        _scrolls.Clear();
        Cursor = UiCursor.Arrow;

        DoubleClicked = false;
        if (input.Pressed(0))
        {
            DoubleClicked = input.Time - _lastClickTime < 0.4 && Vector2.Distance(input.Mouse, _lastClickPos) < S(5);
            _lastClickTime = DoubleClicked ? double.NegativeInfinity : input.Time;
            _lastClickPos = input.Mouse;
        }
    }

    public void EndFrame()
    {
        // щелчок мимо всех полей снимает фокус; кнопки мыши отпущены — захват снят, чей бы он ни был
        if (Input.Pressed(0) && !_focusClaimed) FocusId = 0;
        if (!Input.Down(0) && !Input.Down(1) && !Input.Down(2)) ActiveId = 0;
        if (_ids.Count != 0) throw new InvalidOperationException($"PushId без PopId: {_ids.Count}");
        if (_scrolls.Count != 0) throw new InvalidOperationException($"BeginScroll без EndScroll: {_scrolls.Count}");
        _main.Append(_overlay);
        _main.Append(_top);
        DrawTooltip();
    }

    /// <summary>Нужен кадр не позже чем через <paramref name="seconds"/>, даже без ввода.</summary>
    public void RedrawAfter(double seconds) => _wakeAt = Math.Min(_wakeAt, Input.Time + Math.Max(0, seconds));

    // ── идентификаторы и состояние ─────────────────────────────────────

    private uint Seed => _ids.Count > 0 ? _ids.Peek() : HashSeed;

    /// <summary>ID подписи в текущем стеке ID (FNV-1a по символам; 0 не бывает).</summary>
    public uint Id(ReadOnlySpan<char> label) => NonZero(Hash(Seed, label));

    /// <summary>ID склейки двух подписей — тот же, что <c>Id(a + b)</c>, без строки.</summary>
    private uint Id(ReadOnlySpan<char> a, ReadOnlySpan<char> b) => NonZero(Hash(Hash(Seed, a), b));

    private static uint Hash(uint h, ReadOnlySpan<char> label)
    {
        foreach (char c in label)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    private static uint NonZero(uint h) => h == 0 ? 1 : h;

    /// <summary>ID номера (строка списка, вкладка) в текущем стеке ID.</summary>
    public uint Id(int index)
    {
        uint h = (Seed ^ (uint)index) * 2654435761u;
        h ^= h >> 15;
        return h == 0 ? 1 : h;
    }

    public void PushId(ReadOnlySpan<char> label) => _ids.Push(Id(label));
    public void PushId(int index) => _ids.Push(Id(index));
    public void PopId() => _ids.Pop();

    /// <summary>Состояние виджета по ID; нет — создаётся (выделение — только в первый кадр виджета).</summary>
    public T State<T>(uint id) where T : class, new()
    {
        if (_state.TryGetValue(id, out var o) && o is T t) return t;
        t = new T();
        _state[id] = t;
        return t;
    }

    // ── мышь ───────────────────────────────────────────────────────────

    /// <summary>Курсор над прямоугольником, внутри текущей обрезки и не под всплывающим окном.</summary>
    public bool IsHovered(UiRect r) =>
        Input.MouseInside && r.Contains(Input.Mouse) && Draw.ClipRect.Contains(Input.Mouse)
        && _layer switch
        {
            0 => !_popupBlock.Contains(Input.Mouse) && !_topBlock.Contains(Input.Mouse),
            1 => !_topBlock.Contains(Input.Mouse),
            _ => true,
        };

    /// <summary>
    /// Щелчок по прямоугольнику: нажатие захватывает мышь за <paramref name="id"/>, отпускание над ним — щелчок.
    /// </summary>
    public bool Clickable(UiRect r, uint id, out bool hovered, out bool held, int button = 0)
    {
        hovered = IsHovered(r) && (ActiveId == 0 || ActiveId == id);
        if (hovered && Input.Pressed(button)) ActiveId = id;
        bool clicked = false;
        if (ActiveId == id && Input.Released(button))
        {
            clicked = r.Contains(Input.Mouse);
            ActiveId = 0;
        }
        held = ActiveId == id;
        return clicked;
    }

    /// <summary>Отдать фокус клавиатуры виджету (и не снять его щелчком этого кадра).</summary>
    public void Focus(uint id)
    {
        FocusId = id;
        _focusClaimed = true;
    }

    // ── всплывающие окна ───────────────────────────────────────────────

    /// <summary>
    /// Всплывающее окно поверх всего с обрезкой по <paramref name="r"/> (обрезка родителя не действует). Открытое внутри
    /// другого всплывающего ложится слоем выше и забирает мышь у окна под собой.
    /// </summary>
    public void BeginPopup(UiRect r)
    {
        var layer = EnterLayer();
        if (_layer == 1) _nextPopupBlock = _nextPopupBlock.Union(r);
        else _nextTopBlock = _nextTopBlock.Union(r);
        float radius = S(Theme.Radius);
        layer.Rect(r.X0 + S(2), r.Y0 + S(4), r.X1 + S(2), r.Y1 + S(6), Theme.Shadow, radius + S(2));
        layer.Rect(r, Theme.Popup, radius);
        layer.RectStroke(r, Theme.BorderStrong, S(1), radius);
        layer.PushClipReplace(r);
    }

    /// <summary>Закрыть окно: рисование и мышь возвращаются слою, из которого оно открыто.</summary>
    public void EndPopup()
    {
        Draw.PopClip();
        _layer = _layers.Count > 0 ? _layers.Pop() : 0;
        Draw = LayerList(_layer);
    }

    private UiDrawList EnterLayer()
    {
        _layers.Push(_layer);
        _layer = Math.Min(_layer + 1, 2);
        Draw = LayerList(_layer);
        return Draw;
    }

    private UiDrawList LayerList(int layer) => layer switch { 0 => _main, 1 => _overlay, _ => _top };

    /// <summary>Окно вопроса поверх всего: фон затемняется, мышь под ним не проходит. Закрывать — <see cref="EndModal"/>.</summary>
    public void BeginModal(UiRect dialog)
    {
        var layer = EnterLayer();
        if (_layer == 1) _nextPopupBlock = Screen;
        else _nextTopBlock = Screen;
        // Сначала затемнение, потом окно: затемнение поверх окна проступало бы тёмной рамкой на скруглённых углах.
        float radius = S(Theme.Radius + 2);
        layer.Rect(Screen, Theme.Shadow);
        layer.Rect(dialog.Offset(S(2), S(5)).Expand(S(2)), Theme.Shadow, radius + S(2));
        layer.Rect(dialog, Theme.Popup, radius);
        layer.RectStroke(dialog, Theme.BorderStrong, S(1), radius);
        layer.PushClipReplace(dialog);
    }

    public void EndModal() => EndPopup();

    // ── подсказка ──────────────────────────────────────────────────────

    private uint _tooltipId;
    private double _tooltipSince;
    private string? _pendingTooltip;

    /// <summary>Подсказка у курсора, если он полсекунды над <paramref name="r"/>. Строка, а не отрезок: рисуется в конце кадра.</summary>
    public void Tooltip(UiRect r, string text)
    {
        if (text.Length == 0) return;
        // ID — текст и место без склейки строк. Место нужно, когда одну подсказку просят два соседних виджета: иначе
        // не наведённый сбрасывал бы таймер наведённого.
        uint id = NonZero((Hash(Hash(Seed, "tooltip:"), text) ^ (uint)((int)r.X0 * 7919 ^ (int)r.Y0)) * 2654435761u);
        if (!IsHovered(r) || ActiveId != 0)
        {
            if (_tooltipId == id) _tooltipId = 0;
            return;
        }
        if (_tooltipId != id)
        {
            _tooltipId = id;
            _tooltipSince = Input.Time;
        }
        double wait = 0.5 - (Input.Time - _tooltipSince);
        if (wait > 0)
        {
            RedrawAfter(wait);
            return;
        }
        // Рисуется в конце кадра (DrawTooltip): иначе позже нарисованные виджеты слоя ложились бы поверх, а обрезка резала бы.
        _pendingTooltip = text;
    }

    /// <summary>Подсказка кадра — последней, поверх всего и без обрезки.</summary>
    private void DrawTooltip()
    {
        if (_pendingTooltip is not { } text) return;
        _pendingTooltip = null;
        float fs = S(Theme.SmallFontSize), pad = S(6);
        float w = MathF.Min(_main.Measure(UiFace.Body, fs, text) + pad * 2, Width);
        float h = _main.LineHeight(UiFace.Body, fs) + pad * 2;
        float x = Math.Clamp(Input.Mouse.X + S(12), 0, MathF.Max(0, Width - w));
        float y = Input.Mouse.Y + S(20);
        if (y + h > Height) y = Input.Mouse.Y - h - S(6);
        var tip = new UiRect(x, y, x + w, y + h);
        _main.Rect(tip, Theme.Popup, S(3));
        _main.RectStroke(tip, Theme.BorderStrong, S(1), S(3));
        _main.TextFit(UiFace.Body, fs, new Vector2(x + pad, y + pad), Theme.Text, text, w - pad * 2);
    }
}
