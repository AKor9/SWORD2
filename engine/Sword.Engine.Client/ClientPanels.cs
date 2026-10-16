using Sword.Engine.Platform;
using Sword.Engine.Ui;

namespace Sword.Engine.Client;

/// <summary>
/// Панель интерфейса поверх кадра (DevTools). Кадр панели — в шаге ввода, до ввода игры: <see cref="Update"/> →
/// <see cref="Draw"/> в общем <see cref="UiContext"/> → квады UI-проходом поверх игры. Мышь и клавиатура у панели в игру не уходят.
/// </summary>
public interface IClientPanel
{
    /// <summary>Действия ввода панели (клавиша открытия) — привязываются вместе с действиями игры (секция <c>input</c>).</summary>
    IReadOnlyList<InputAction> Actions => [];

    /// <summary>Открыть или закрыть по фронтам действий панели. Истина — панель видна и рисуется; закрытая ничего не стоит.</summary>
    bool Update(in FrameInput input);

    /// <summary>Кадр видимой панели между <c>BeginFrame</c> и <c>EndFrame</c>. Истина — мышь у панели и игре не уходит.</summary>
    bool Draw(UiContext ui);
}

/// <summary>
/// Слой панелей клиента: общий <see cref="UiContext"/>, ввод интерфейса и кадр панелей — до ввода игры, чтобы игра в том
/// же кадре знала, что мышь или клавиатура у интерфейса. Все закрыты — 0 работы, кроме опроса <see cref="IClientPanel.Update"/>.
/// </summary>
public sealed class ClientPanels
{
    /// <summary>Панелей не больше: видимые в кадре — битовая маска.</summary>
    public const int MaxPanels = 64;

    private readonly List<IClientPanel> _panels = [];
    private readonly Func<UiFonts?> _fonts;
    private UiContext? _ui;
    private ulong _shown;

    /// <param name="fonts">Шрифты интерфейса — берутся, когда панель впервые видна (пока все закрыты, шрифтов в видеопамяти
    /// нет); null — только фигуры (тесты логики).</param>
    public ClientPanels(Func<UiFonts?>? fonts = null)
    {
        _fonts = fonts ?? (() => null);
    }

    /// <summary>Общий интерфейс панелей: его квады после кадра — <see cref="UiContext.Output"/>. Заводится при первом обращении.</summary>
    public UiContext Ui => _ui ??= new UiContext(_fonts());

    /// <summary>Ввод интерфейса (буфер обмена окна ставит хост).</summary>
    public UiInputSource Source { get; } = new();

    public IReadOnlyList<IClientPanel> Panels => _panels;

    /// <summary>Масштаб интерфейса по высоте кадра; по умолчанию <c>clamp(h / 1080, 0,75, 2)</c> — игра ставит масштаб своего HUD.</summary>
    public Func<int, float> Scale { get; set; } = DefaultScale;

    /// <summary>Хоть одна панель видна в последнем кадре: его квады — в <see cref="UiContext.Output"/>.</summary>
    public bool Shown => _shown != 0;

    /// <summary>Мышь у панелей в последнем кадре.</summary>
    public bool WantsMouse { get; private set; }

    /// <summary>Клавиатура у поля ввода панели (после последнего кадра): горячие клавиши и Escape окна молчат.</summary>
    public bool WantsKeyboard => _shown != 0 && Ui.WantsKeyboard;

    public static float DefaultScale(int height) => Math.Clamp(height / 1080f, 0.75f, 2f);

    public void Add(IClientPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (_panels.Count == MaxPanels) throw new InvalidOperationException($"панелей больше {MaxPanels}");
        if (_panels.Contains(panel)) throw new ArgumentException("панель уже подключена", nameof(panel));
        _panels.Add(panel);
    }

    /// <summary>Кадр панелей: <see cref="IClientPanel.Update"/> у всех, затем, если хоть одна видна, — ввод, <c>BeginFrame</c>,
    /// <see cref="IClientPanel.Draw"/> видимых, <c>EndFrame</c>. Закрытая панель отпускает фокус клавиатуры и захват мыши.</summary>
    /// <returns>Ввод кадра для игры с отметками «мышь у интерфейса» и «клавиатура у интерфейса».</returns>
    public FrameInput Frame(in FrameInput input, InputState state, bool mouseInside, double time)
    {
        ulong shown = 0;
        for (int i = 0; i < _panels.Count; i++)
            if (_panels[i].Update(input)) shown |= 1UL << i;
        if ((_shown & ~shown) != 0 && _ui is { } ui)
        {
            ui.FocusId = 0;
            ui.ActiveId = 0;
        }
        _shown = shown;
        if (shown == 0)
        {
            WantsMouse = false;
            return input;
        }

        Source.Fill(state, mouseInside, time);
        Ui.BeginFrame(Source.Input, input.Width, input.Height, Scale(input.Height));
        bool mouse = false;
        for (int i = 0; i < _panels.Count; i++)
            if ((shown >> i & 1) != 0) mouse |= _panels[i].Draw(Ui);
        Ui.EndFrame();
        WantsMouse = mouse;
        return input.WithUi(mouse, Ui.WantsKeyboard);
    }

    /// <summary>Курсор окна по интерфейсу: над панелями — что просят виджеты, иначе — стрелка.</summary>
    public CursorShape Cursor => Shown ? UiInputSource.Cursor(Ui.Cursor) : CursorShape.Arrow;
}
