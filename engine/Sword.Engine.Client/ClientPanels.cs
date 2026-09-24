using Sword.Engine.Platform;
using Sword.Engine.Ui;

namespace Sword.Engine.Client;

/// <summary>
/// Панель интерфейса поверх кадра (DevTools: ручки F3; позже — панели игры): её ведёт клиент движка (<see cref="ClientPanels"/>,
/// <see cref="ClientHost.AddPanel"/>). Кадр панели — в шаге ввода кадра, до ввода игры: <see cref="Update"/> (клавиша
/// открытия) → <see cref="Draw"/> в общем <see cref="UiContext"/> → квады идут UI-проходом поверх проходов игры. Мышь у
/// панели и клавиатура у её поля ввода в игру не уходят (<see cref="FrameInput.UiMouse"/>, <see cref="FrameInput.UiKeyboard"/>).
/// </summary>
public interface IClientPanel
{
    /// <summary>Действия ввода панели (клавиша открытия) — привязываются вместе с действиями игры (секция <c>input</c>).</summary>
    IReadOnlyList<InputAction> Actions => [];

    /// <summary>
    /// Ввод кадра — до интерфейса и до ввода игры (фронты действий панели): открыть, закрыть. Истина — панель видна в этом
    /// кадре и рисуется (<see cref="Draw"/>); закрытая панель больше ничего не стоит.
    /// </summary>
    bool Update(in FrameInput input);

    /// <summary>
    /// Кадр видимой панели — между <c>BeginFrame</c> и <c>EndFrame</c> общего <see cref="UiContext"/>. Истина — мышь у панели
    /// (курсор над ней или тянут её виджет): мышь в этом кадре не уходит игре.
    /// </summary>
    bool Draw(UiContext ui);
}

/// <summary>
/// Слой панелей клиента: общий <see cref="UiContext"/> панелей, ввод интерфейса из ввода окна (<see cref="UiInputSource"/>) и
/// кадр панелей (<see cref="Frame"/>) — до ввода игры, чтобы игра в том же кадре знала, что мышь или клавиатура у интерфейса.
/// Все панели закрыты — ни заполнения ввода, ни кадра интерфейса, ни прохода (0 работы, кроме опроса <see cref="IClientPanel.Update"/>).
/// Без окна и GPU: квады — <see cref="UiContext.Output"/>, в UI-проход их отдаёт <see cref="ClientHost"/>.
/// </summary>
public sealed class ClientPanels
{
    /// <summary>Панелей не больше: видимые в кадре — битовая маска.</summary>
    public const int MaxPanels = 64;

    private readonly List<IClientPanel> _panels = [];
    private readonly Func<UiFonts?> _fonts;
    private UiContext? _ui;
    private ulong _shown;

    /// <param name="fonts">
    /// Шрифты интерфейса (<c>Renderer.Ui.Fonts</c>) — берутся, когда панель впервые видна: пока все закрыты, UI-проход не
    /// заводится (Sword.exe <c>--no-hud</c> без открытых панелей — без шрифтов в видеопамяти); null или шрифтов нет —
    /// только фигуры (тесты логики).
    /// </param>
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

    /// <summary>
    /// Кадр панелей: <see cref="IClientPanel.Update"/> у всех, затем — если хоть одна видна — ввод интерфейса из
    /// <paramref name="state"/>, <c>BeginFrame</c>, <see cref="IClientPanel.Draw"/> видимых, <c>EndFrame</c>. Панель закрылась —
    /// фокус клавиатуры и захват мыши снимаются (её поле не держит клавиатуру закрытым — <c>ToolsModule.Keys</c> SWORD).
    /// </summary>
    /// <returns>Ввод кадра для игры: с отметками «мышь у интерфейса» и «клавиатура у интерфейса».</returns>
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
