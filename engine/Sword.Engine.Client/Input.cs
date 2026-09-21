using Sword.Engine.Platform;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>
/// Действие ввода игры (раздел 7 плана: привязки клавиш к действиям — в конфиге): имя — ключ конфига
/// (<c>input.&lt;имя&gt;</c>, <c>SWORD_INPUT_&lt;ИМЯ&gt;</c>), клавиша по умолчанию — имя клавиши (<c>W</c>, <c>Shift</c>,
/// <c>Plus</c>) или кнопки мыши (<c>MouseLeft</c>, <c>MouseRight</c>, <c>MouseMiddle</c>), <c>None</c> — без привязки.
/// Игра знает только действия: клавиши и платформа ей не видны.
/// </summary>
public sealed record InputAction
{
    public InputAction(string name, string defaultKey)
    {
        if (!IModule.IsValidId(name)) throw new ArgumentException($"действие ввода «{name}»: нужно {IModule.IdRule}", nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultKey);
        Name = name;
        DefaultKey = defaultKey;
    }

    public string Name { get; }
    public string DefaultKey { get; }
}

/// <summary>
/// Привязки действий к клавишам: значения по умолчанию игры, поверх — секция конфига <see cref="Section"/> по общим
/// слоям (<see cref="ConfigSet.Strings"/>). Неизвестное действие в конфиге и неизвестная клавиша — ошибки композиции.
/// </summary>
public sealed class InputBindings
{
    /// <summary>Секция конфига привязок.</summary>
    public const string Section = ConfigSet.InputSection;

    // По имени действия: имя — ключ конфига и единственная его идентичность.
    private readonly Dictionary<string, Binding> _bindings;
    private readonly Dictionary<string, int> _index;

    private InputBindings(IReadOnlyList<InputAction> actions, Dictionary<string, Binding> bindings)
    {
        Actions = actions;
        _bindings = bindings;
        _index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < actions.Count; i++) _index[actions[i].Name] = i;
    }

    /// <summary>Действия игры по порядку объявления.</summary>
    public IReadOnlyList<InputAction> Actions { get; }

    /// <summary>Номер действия (для защёлки фронтов).</summary>
    internal int IndexOf(InputAction action) =>
        _index.TryGetValue(action.Name, out int i) ? i
            : throw new InvalidOperationException($"действие ввода {action.Name} не объявлено игрой (IClientGame.Actions)");

    /// <summary>Действие → клавиша (как в конфиге) — для справки, итога запуска и агента.</summary>
    public IReadOnlyDictionary<string, string> Keys => _bindings.ToDictionary(p => p.Key, p => p.Value.Name);

    /// <summary>Действия, привязанные к клавише <paramref name="key"/> (проверка конфликтов с клавишами клиента).</summary>
    internal IEnumerable<string> ActionsOn(Key key) => _bindings.Where(p => p.Value.Mouse is null && p.Value.Key == key).Select(p => p.Key);

    /// <summary>Привязки действий игры: по умолчанию, поверх — конфиг (<paramref name="config"/> null — только по умолчанию).</summary>
    public static InputBindings Resolve(IReadOnlyList<InputAction> actions, ConfigSet? config)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in actions)
            if (!names.Add(a.Name)) throw new ArgumentException($"действие ввода {a.Name} объявлено дважды");
        // Клавиша по умолчанию — ошибка игры, а не конфига: проверяется до слоёв.
        foreach (var a in actions)
            if (!Binding.TryParse(a.DefaultKey, out _))
                throw new ArgumentException($"действие ввода {a.Name}: клавиши по умолчанию «{a.DefaultKey}» нет — ошибка игры");
        var defaults = actions.ToDictionary(a => a.Name, a => a.DefaultKey, StringComparer.Ordinal);
        var keys = config?.Strings(Section, defaults) ?? defaults;
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var a in actions) bindings[a.Name] = Binding.Parse(a.Name, keys[a.Name]);
        return new InputBindings([.. actions], bindings);
    }

    /// <summary>Есть ли привязка у действия с этим именем.</summary>
    public bool Has(string action) => _bindings.ContainsKey(action);

    internal Binding Of(InputAction action) =>
        _bindings.TryGetValue(action.Name, out var b) ? b
            : throw new InvalidOperationException($"действие ввода {action.Name} не объявлено игрой (IClientGame.Actions)");

    /// <summary>Клавиша или кнопка мыши.</summary>
    internal readonly record struct Binding(string Name, Key Key, MouseButton? Mouse)
    {
        private const string MousePrefix = "Mouse";

        public static Binding Parse(string action, string text) =>
            TryParse(text, out var binding) ? binding
                : throw new InvalidDataException($"конфиг: {Section}.{action} = «{text}»: нет такой клавиши; есть: " +
                    string.Join(", ", Enum.GetNames<Key>().Concat(Enum.GetNames<MouseButton>().Select(b => MousePrefix + b))));

        /// <summary>
        /// Точное имя клавиши или кнопки без учёта регистра. <c>Enum.TryParse</c> не годится: «A, B» он сложил бы в
        /// третью клавишу по битам, а «Mouse0» принял бы по числу.
        /// </summary>
        public static bool TryParse(string text, out Binding binding)
        {
            foreach (var name in Enum.GetNames<MouseButton>())
                if (string.Equals(text, MousePrefix + name, StringComparison.OrdinalIgnoreCase))
                {
                    binding = new Binding(MousePrefix + name, Key.None, Enum.Parse<MouseButton>(name));
                    return true;
                }
            foreach (var name in Enum.GetNames<Key>())
                if (string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
                {
                    binding = new Binding(name, Enum.Parse<Key>(name), null);
                    return true;
                }
            binding = default;
            return false;
        }

        public bool IsDown(InputState s) => Mouse is { } m ? s.IsMouseDown(m) : Key != Key.None && s.IsDown(Key);

        public bool WasPressed(InputState s) => Mouse is { } m ? s.WasMousePressed(m) : Key != Key.None && s.WasPressed(Key);

        public bool WasReleased(InputState s) => Mouse is { } m ? s.WasMouseReleased(m) : Key != Key.None && s.WasReleased(Key);
    }
}

/// <summary>
/// Ввод кадра для клиентской части игры (шаг Input кадра, раздел 7): действия по привязкам, мышь, колесо, размер кадра и
/// шаг кадра. Снимается раз в кадр, до тиков кадра.
/// </summary>
public readonly struct FrameInput
{
    private readonly InputState _state;
    private readonly InputBindings _bindings;
    private readonly IReadOnlySet<string>? _held;
    private readonly bool _heldPressed;

    /// <param name="heldPressed">Первый кадр удержания: заданное действие ещё и «нажато», как клавиша, зажатая с начала.</param>
    internal FrameInput(InputState state, InputBindings bindings, IReadOnlySet<string>? held, bool heldPressed, bool mouseInside,
        int width, int height, float seconds)
    {
        _state = state;
        _bindings = bindings;
        _held = held;
        _heldPressed = heldPressed;
        MouseInside = mouseInside;
        Width = width;
        Height = height;
        Seconds = seconds;
    }

    /// <summary>Действие удерживается (клавишей или заданием прогона — <see cref="ClientOptions.HeldActions"/>).</summary>
    public bool IsDown(InputAction action) => _bindings.Of(action).IsDown(_state) || _held?.Contains(action.Name) == true;

    /// <summary>
    /// Действие нажато в этом кадре (фронт); удерживаемое заданием прогона — в первом кадре. Для представления (камера,
    /// интерфейс); в команды симуляции фронты идут через <see cref="TickInput"/> — ровно одним тиком.
    /// </summary>
    public bool WasPressed(InputAction action) =>
        _bindings.Of(action).WasPressed(_state) || (_heldPressed && _held?.Contains(action.Name) == true);

    /// <summary>Действие отпущено в этом кадре (фронт).</summary>
    public bool WasReleased(InputAction action) => _bindings.Of(action).WasReleased(_state);

    public float MouseX => _state.MouseX;
    public float MouseY => _state.MouseY;
    public float MouseDeltaX => _state.MouseDeltaX;
    public float MouseDeltaY => _state.MouseDeltaY;

    /// <summary>Щелчки колеса за кадр (вперёд — плюс).</summary>
    public float Wheel => _state.Wheel;

    /// <summary>Курсор над клиентской областью окна.</summary>
    public bool MouseInside { get; }

    /// <summary>Размер кадра, пикселей.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Шаг кадра, с: по часам (не больше 0,25 с) или заданный (проверочные прогоны) — для сглаживания камеры.</summary>
    public float Seconds { get; }
}

/// <summary>Кадр после тиков (шаг Extract): доля тика для интерполяции и сколько тиков сделано.</summary>
/// <param name="Alpha">
/// Доля следующего тика, прошедшая по часам: 0 — состояние прошлого тика, 1 — последнего (<c>accumulator / dt</c> SWORD;
/// при заданном числе тиков на кадр — 1).
/// </param>
public readonly record struct FrameView(float Alpha, int Ticks, float Seconds);
