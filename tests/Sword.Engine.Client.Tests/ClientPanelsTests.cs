using Sword.Engine.Platform;
using Sword.Engine.Ui;

namespace Sword.Engine.Client.Tests;

/// <summary>
/// Слой панелей без окна и GPU: закрытые панели ничего не стоят; мышь или клавиатура у панели — ввод не достаётся игре,
/// отпускания доходят всегда; закрытая панель теряет фокус; кадр без выделений.
/// </summary>
public sealed class ClientPanelsTests
{
    private static readonly InputAction Attack = new("attack", "MouseLeft");
    private static readonly InputAction Jump = new("jump", "Space");
    private static readonly InputAction Toggle = new("panel", "F3");
    private static readonly InputBindings Bindings = InputBindings.Resolve([Attack, Jump, Toggle], null);

    /// <summary>Панель-заглушка: кнопка без подписи (фигуры без шрифтов) в своём прямоугольнике, по желанию — фокус поля.</summary>
    private sealed class FakePanel(UiRect rect) : IClientPanel
    {
        public bool Open = true;
        public bool TakeFocus;
        public int Draws;

        public IReadOnlyList<InputAction> Actions => [Toggle];

        public bool Update(in FrameInput input)
        {
            if (input.WasPressed(Toggle)) Open = !Open;
            return Open;
        }

        public bool Draw(UiContext ui)
        {
            Draws++;
            ui.Button(rect, "");
            if (TakeFocus) ui.Focus(ui.Id("field"));
            return ui.IsHovered(rect) || ui.ActiveId != 0;
        }
    }

    private readonly InputState _state = new();
    private readonly FakePanel _panel = new(new UiRect(0, 0, 100, 100));
    private readonly ClientPanels _panels;
    private int _fontRequests;
    private IReadOnlySet<string>? _held;
    private double _time;

    public ClientPanelsTests()
    {
        _panels = new ClientPanels(() =>
        {
            _fontRequests++;
            return null;
        });
        _panels.Add(_panel);
    }

    private FrameInput Frame(Action<InputState>? events = null)
    {
        _state.BeginFrame();
        events?.Invoke(_state);
        var input = new FrameInput(_state, Bindings, _held, heldPressed: false, mouseInside: true, 640, 480, 1f / 60);
        return _panels.Frame(input, _state, mouseInside: true, _time += 1 / 60.0);
    }

    [Fact]
    public void ClosedPanels_NoUiFrame_NoFonts_InputUntouched()
    {
        _panel.Open = false;
        var input = Frame(s =>
        {
            s.SetMousePosition(50, 50);   // над панелью, но она закрыта
            s.SetMouseButton(MouseButton.Left, true);
            s.AddWheel(1);
        });
        Assert.False(_panels.Shown);
        Assert.Equal(0, _panel.Draws);
        Assert.Equal(0, _fontRequests);             // интерфейс не заведён — UI-проход не нужен
        Assert.Equal(0, _panels.Source.Input.Time); // ввод интерфейса не заполнялся
        Assert.True(input.WasPressed(Attack));
        Assert.Equal(1, input.Wheel);
        Assert.False(input.UiMouse || input.UiKeyboard);
    }

    [Fact]
    public void ToggleAction_OpensPanel_ThenItDraws()
    {
        _panel.Open = false;
        Frame(s => s.SetKey((int)Key.F3, true));
        Assert.True(_panel.Open);
        Assert.True(_panels.Shown);
        Assert.Equal(1, _panel.Draws);
        Assert.Equal(1, _fontRequests);
    }

    [Fact]
    public void MouseOverPanel_MouseActionsAndWheel_DoNotReachGame_KeysDo()
    {
        var input = Frame(s =>
        {
            s.SetMousePosition(50, 50);
            s.SetMouseButton(MouseButton.Left, true);
            s.SetKey((int)Key.Space, true);
            s.AddWheel(1);
        });
        Assert.True(input.UiMouse);
        Assert.False(input.UiKeyboard);
        Assert.False(input.WasPressed(Attack));
        Assert.False(input.IsDown(Attack));
        Assert.Equal(0, input.Wheel);
        Assert.True(input.WasPressed(Jump));
        Assert.Equal(50, input.MouseX);   // курсор виден как есть

        // И в тик: защёлка фронтов берёт ввод кадра игры — удара нет, прыжок есть.
        var edges = new InputEdges(Bindings);
        edges.Note(input);
        var tick = edges.ForTick();
        Assert.False(tick.WasPressed(Attack));
        Assert.False(tick.IsDown(Attack));
        Assert.True(tick.WasPressed(Jump));

        // Нажатие — на панели, отпускание — над игрой: отпускание игре доходит всегда (у действия нет нажатия без пары).
        input = Frame(s =>
        {
            s.SetMousePosition(300, 300);
            s.SetMouseButton(MouseButton.Left, false);
        });
        Assert.True(input.WasReleased(Attack));

        // Мышь над игрой — игра снова видит мышь.
        input = Frame(s => s.SetMouseButton(MouseButton.Left, true));
        Assert.False(input.UiMouse);
        Assert.True(input.WasPressed(Attack));
    }

    [Fact]
    public void DragStartedOnPanel_KeepsMouseWhileHeldOutside()
    {
        Frame(s =>
        {
            s.SetMousePosition(50, 50);
            s.SetMouseButton(MouseButton.Left, true);
        });
        var input = Frame(s => s.SetMousePosition(400, 300));   // тянут кнопку панели за её край
        Assert.True(input.UiMouse);
        Assert.False(input.IsDown(Attack));
    }

    [Fact]
    public void FieldFocus_KeysDoNotReachGame_EscapeGuard_ClosingPanelDropsFocus()
    {
        _panel.TakeFocus = true;
        var input = Frame(s => s.SetKey((int)Key.Space, true));
        Assert.True(input.UiKeyboard);
        Assert.True(_panels.WantsKeyboard);   // хост по нему не закрывает окно Escape
        Assert.False(input.WasPressed(Jump));
        Assert.False(input.IsDown(Jump));

        _panel.TakeFocus = false;
        input = Frame(s => s.SetKey((int)Key.Space, false));
        Assert.True(input.WasReleased(Jump));   // отпускание доходит и при клавиатуре у поля

        input = Frame(s => s.SetKey((int)Key.F3, true));   // панель закрыта — её поле клавиатуру не держит
        Assert.False(_panel.Open);
        Assert.False(_panels.Shown);
        Assert.False(_panels.WantsKeyboard);
        Assert.Equal(0u, _panels.Ui.FocusId);
        Assert.False(input.UiKeyboard);
    }

    [Fact]
    public void HeldActionsOfRun_AreNotMasked()
    {
        _held = new HashSet<string>(["attack"]);
        var input = Frame(s => s.SetMousePosition(50, 50));
        Assert.True(input.UiMouse);
        Assert.True(input.IsDown(Attack));   // удержание заданием прогона (--hold) — не мышь
    }

    [Fact]
    public void PanelFrame_AllocatesNothing()
    {
        void Events(InputState s)
        {
            s.SetMousePosition(s.MouseX > 150 ? 20 : s.MouseX + 7, 50);
            s.SetMouseButton(MouseButton.Left, !s.IsMouseDown(MouseButton.Left));
            s.AddWheel(1);
        }
        Action<InputState> events = Events;
        for (int i = 0; i < 8; i++) Frame(events);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++) Frame(events);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(128, _panel.Draws);
    }

    [Fact]
    public void Add_RejectsDuplicate()
    {
        Assert.Throws<ArgumentException>(() => _panels.Add(_panel));
        Assert.Single(_panels.Panels);
    }
}
