namespace Sword.Engine.Ui.Tests;

/// <summary>
/// Виджеты без видеокарты и шрифтов: с пустыми подписями рисуют одни фигуры и шрифт не трогают; ввод — <see cref="UiInput"/>
/// от теста. Текст и поля ввода — <see cref="UiWidgetTextTests"/>.
/// </summary>
public sealed class UiContextTests
{
    private static UiContext Frame(UiContext ui, int width = 200, int height = 200)
    {
        ui.BeginFrame(new UiInput(), width, height, 1);
        return ui;
    }

    // ── кадр, прогресс, всплывающий список ─────────────────────────────

    [Fact]
    public void FrameBrokenByExceptionDoesNotBreakTheNext()
    {
        var ui = new UiContext();
        Frame(ui);
        // строку списка рисует вызывающий, и она может упасть: разбор параметров, чужие данные
        Assert.Throws<InvalidOperationException>(() =>
            ui.VirtualList(new UiRect(0, 0, 100, 100), "list", 10, 10, (_, _) => throw new InvalidOperationException("строка не нарисовалась")));

        Frame(ui);
        ui.EndFrame();   // незакрытый BeginScroll прошлого кадра не должен валить все следующие
    }

    [Fact]
    public void UnclosedScrollInOneFrameIsStillAnError()
    {
        var ui = new UiContext();
        Frame(ui);
        ui.BeginScroll(new UiRect(0, 0, 100, 100), "list", 500);
        Assert.Throws<InvalidOperationException>(ui.EndFrame);
    }

    [Fact]
    public void IndeterminateProgressAsksForTheNextFrame()
    {
        var ui = new UiContext();
        Frame(ui);
        ui.ProgressBar(new UiRect(0, 0, 100, 10), -1);
        ui.EndFrame();
        Assert.True(double.IsFinite(ui.WakeIn));
    }

    [Fact]
    public void DeterminateProgressLetsTheWindowSleep()
    {
        var ui = new UiContext();
        Frame(ui);
        ui.ProgressBar(new UiRect(0, 0, 100, 10), 0.5f);
        ui.EndFrame();
        Assert.True(double.IsInfinity(ui.WakeIn));
    }

    [Fact]
    public void ComboPopupStaysOnScreen()
    {
        var ui = new UiContext();
        Frame(ui);
        int index = 0;
        ui.OpenCombo("combo");
        // поле у самого правого края: список шире поля и вылез бы за экран, где его срежет scissor
        ui.Combo(new UiRect(160, 10, 195, 30), "combo", ref index, ["", "", ""]);
        ui.EndFrame();

        Assert.NotEmpty(ui.Output.ClipRuns);
        foreach (var run in ui.Output.ClipRuns)
            if (run.Clip != UiDrawList.NoClip)
                Assert.InRange(run.Clip.X1, run.Clip.X0, ui.Width);
    }

    [Fact]
    public void ComboPopupFlipsAboveTheFieldAtTheBottom()
    {
        var ui = new UiContext();
        Frame(ui);
        int index = 0;
        ui.OpenCombo("combo");
        ui.Combo(new UiRect(10, 170, 120, 190), "combo", ref index, ["", "", ""]);
        ui.EndFrame();

        Assert.NotEmpty(ui.Output.ClipRuns);
        foreach (var run in ui.Output.ClipRuns)
            if (run.Clip != UiDrawList.NoClip)
                Assert.InRange(run.Clip.Y1, run.Clip.Y0, ui.Height);
    }

    // ── кнопки и переключатели ─────────────────────────────────────────

    [Fact]
    public void Button_ClicksOnReleaseOverIt_NotWhenReleasedOutside()
    {
        var d = new UiDriver();
        var r = new UiRect(10, 10, 100, 30);
        bool clicked = false;
        void Widgets(UiContext ui) => clicked = ui.Button(r, "", id: "ok");

        d.Press(50, 20).Frame(Widgets);
        Assert.False(clicked);                          // нажатие захватывает мышь, щелчка ещё нет
        Assert.Equal(d.Ui.Id("ok"), d.Ui.ActiveId);
        d.Release(50, 20).Frame(Widgets);
        Assert.True(clicked);

        d.Press(50, 20).Frame(Widgets);
        d.Release(150, 150).Frame(Widgets);             // увёл мышь с кнопки — передумал
        Assert.False(clicked);
        Assert.Equal(0u, d.Ui.ActiveId);
    }

    [Fact]
    public void Button_DisabledNeverClicks_HoverAndHoldPaintTheme()
    {
        var d = new UiDriver();
        var r = new UiRect(10, 10, 100, 30);
        bool clicked = false;
        d.Click(50, 20, ui => clicked |= ui.Button(r, "", enabled: false, id: "off"));
        Assert.False(clicked);

        var theme = d.Ui.Theme;
        d.Move(50, 20).Frame(ui => ui.Button(r, "", id: "b"));
        Assert.Equal(theme.ButtonHot, d.Ui.Output.Quads[0].ColourA);
        d.Press(50, 20).Frame(ui => ui.Button(r, "", primary: true, id: "p"));
        Assert.Equal(theme.AccentActive, d.Ui.Output.Quads[0].ColourA);
    }

    [Fact]
    public void Toggle_FlipsValueOnClickAnywhereInRow()
    {
        var d = new UiDriver();
        bool value = false, changed = false;
        void Widgets(UiContext ui) => changed = ui.Toggle(new UiRect(10, 10, 150, 30), "", ref value, id: "flag");
        d.Click(120, 20, Widgets);
        Assert.True(changed);
        Assert.True(value);
        d.Click(12, 20, Widgets);
        Assert.False(value);
    }

    // ── ползунок и разделитель ─────────────────────────────────────────

    // Дорожка ползунка (0,0)–(112,20): ручка 12 px, центр ручки ходит по x ∈ [6, 106], размах 100.
    private static readonly UiRect SliderRect = new(0, 0, 112, 20);

    [Theory]
    [InlineData(false, 0f, 10f, 5f)]        // линейная шкала: середина — среднее
    [InlineData(true, 0.02f, 2f, 0.2f)]     // логарифмическая: середина — среднее геометрическое
    [InlineData(true, 0f, 10f, 5f)]         // лог. шкала с min ≤ 0 — линейная (иначе NaN)
    public void Slider_ClickOnTrack_SetsValue(bool log, float min, float max, float expected)
    {
        var d = new UiDriver();
        float value = min;
        bool changed = false;
        d.Press(56, 10).Frame(ui => changed = ui.Slider(SliderRect, "s", ref value, min, max, log));
        Assert.True(changed);
        Assert.InRange(value, expected - 1e-5f, expected + 1e-5f);
    }

    [Fact]
    public void Slider_GrabbedKnobDoesNotJump_UnmovedMouseKeepsExactValue_ReleaseEndsDrag()
    {
        var d = new UiDriver();
        float value = 1f;   // 1× на лог. шкале 0,02…2: центр ручки у x ≈ 90,95
        bool changed = false;
        void Widgets(UiContext ui) => changed = ui.Slider(SliderRect, "time", ref value, 0.02f, 2f, log: true);

        d.Press(91, 10).Frame(Widgets);
        d.Frame(Widgets);
        Assert.False(changed);
        Assert.Equal(1f, value);   // ровно 1, а не 0.99999994 от обратного пересчёта

        d.Move(101, 10).Frame(Widgets);
        Assert.True(changed);
        Assert.InRange(value, 1.5f, 1.7f);   // сдвиг на 10 px из 100 — 0,1 шкалы: ×100^0,1 ≈ ×1,585

        d.Release(101, 10).Frame(Widgets);
        float released = value;
        d.Move(30, 10).Frame(Widgets);
        Assert.False(changed);
        Assert.Equal(released, value);
        Assert.Equal(0u, d.Ui.ActiveId);
    }

    [Fact]
    public void Splitter_DragsFromGrabPoint_ClampsToRange_AsksForResizeCursor()
    {
        var d = new UiDriver(width: 400);
        float position = 100;
        void Widgets(UiContext ui) => ui.Splitter(new UiRect(95, 0, 105, 200), "split", ref position, vertical: true, 50, 150);

        d.Press(98, 50).Frame(Widgets);
        Assert.Equal(100f, position);    // схватили в 2 px левее середины — положение не прыгает
        d.Move(130, 50).Frame(Widgets);
        Assert.Equal(132f, position);
        d.Move(300, 50).Frame(Widgets);
        Assert.Equal(150f, position);
        Assert.Equal(UiCursor.SizeWE, d.Ui.Cursor);
    }

    // ── списки и прокрутка ─────────────────────────────────────────────

    [Fact]
    public void Selectable_PressSelectsAndCapturesMouse()
    {
        var d = new UiDriver();
        bool pressed = false;
        uint id = 0;
        d.Press(50, 15).Frame(ui =>
        {
            id = ui.Id(7);
            pressed = ui.Selectable(new UiRect(0, 10, 100, 20), id, selected: false);
        });
        Assert.True(pressed);
        Assert.Equal(id, d.Ui.ActiveId);
        Assert.Equal(d.Ui.Theme.RowHover, d.Ui.Output.Quads[0].ColourA);
    }

    [Fact]
    public void Scroll_WheelScrollsThreeRows_IsConsumed_AndClampsToContent()
    {
        var d = new UiDriver();
        var view = new UiRect(0, 0, 100, 100);
        float y0 = 0, wheelAfter = 0;
        void Widgets(UiContext ui)
        {
            y0 = ui.BeginScroll(view, "list", 1000).Y0;
            wheelAfter = ui.Input.Wheel;
            ui.EndScroll();
        }

        d.Move(50, 50);
        d.Input.Wheel = -1;
        d.Frame(Widgets);
        Assert.Equal(-3 * d.Ui.Theme.RowHeight, y0);
        Assert.Equal(0f, wheelAfter);   // колесо забрано: внешняя прокрутка его уже не увидит

        d.Input.Wheel = -100;
        d.Frame(Widgets);
        Assert.Equal(-900f, y0);        // содержимое 1000 в окне 100
    }

    [Fact]
    public void Scroll_ThumbDragMovesContent()
    {
        var d = new UiDriver();
        var view = new UiRect(0, 0, 100, 100);
        float y0 = 0;
        void Widgets(UiContext ui)
        {
            y0 = ui.BeginScroll(view, "list", 1000).Y0;
            ui.EndScroll();
        }
        // полоса x ∈ [92, 100), ползунок 24 px (не меньше 24), ход 76 px на 900 px прокрутки
        d.Press(96, 5).Frame(Widgets);
        d.Move(96, 43).Frame(Widgets);
        d.Frame(Widgets);
        Assert.Equal(-450f, y0);
    }

    [Fact]
    public void List_GivesOnlyVisibleRows_ShiftedByScroll()
    {
        var d = new UiDriver();
        var view = new UiRect(0, 0, 100, 100);
        UiListRows rows = default;
        void Widgets(UiContext ui)
        {
            rows = ui.BeginList(view, "rows", 1000, 10);
            ui.EndList();
        }

        d.Move(50, 50).Frame(Widgets);
        Assert.Equal((0, 10), (rows.First, rows.Last));

        d.Input.Wheel = -1;   // 66 px: строки 6…16, шестая — от y = −6
        d.Frame(Widgets);
        Assert.Equal((6, 17), (rows.First, rows.Last));
        Assert.Equal(new UiRect(0, -6, 89, 4), rows.Row(6));   // сужена под полосу прокрутки

        int drawn = 0;
        d.Frame(ui => ui.VirtualList(view, "virtual", 1000, 10, (_, _) => drawn++));
        Assert.Equal(10, drawn);
    }

    // ── вкладки ────────────────────────────────────────────────────────

    [Fact]
    public void Tabs_ClickSelectsTab()
    {
        var d = new UiDriver();
        int index = 0;
        bool changed = false;
        string[] tabs = ["", "", ""];   // пустая подпись: вкладка шириной в поля, 24 px
        void Widgets(UiContext ui) => changed = ui.Tabs(new UiRect(0, 0, 200, 24), "tabs", ref index, tabs);

        d.Click(30, 12, Widgets);
        Assert.True(changed);
        Assert.Equal(1, index);
    }

    [Fact]
    public void Tabs_OverflowScrollsSelectedIntoView()
    {
        var d = new UiDriver();
        int index = 15;
        string[] tabs = new string[20];
        Array.Fill(tabs, "");
        void Widgets(UiContext ui) => ui.Tabs(new UiRect(0, 0, 200, 24), "tabs", ref index, tabs);

        // 20 × 24 = 480 px в полосе 172 px между стрелками: выбранная 15-я подведена к правому краю, 14-я — у x 138…162
        d.Frame(Widgets);
        d.Click(150, 12, Widgets);
        Assert.Equal(14, index);
    }

    // ── всплывающие окна и подсказка ───────────────────────────────────

    [Fact]
    public void Popup_TakesMouseFromWidgetsBelow_FromTheNextFrame()
    {
        var d = new UiDriver();
        uint belowActive = 1, insideActive = 0;
        void Widgets(UiContext ui)
        {
            ui.Button(new UiRect(10, 10, 50, 30), "", id: "below");
            belowActive = ui.ActiveId;
            ui.BeginPopup(new UiRect(0, 0, 100, 100));
            ui.Button(new UiRect(20, 40, 80, 60), "", id: "inside");
            insideActive = ui.ActiveId;
            ui.EndPopup();
        }

        d.Frame(Widgets);                // окно открылось: заслоняет со следующего кадра
        d.Press(20, 20).Frame(Widgets);
        Assert.Equal(0u, belowActive);   // кнопка под окном мышь не видит
        d.Release(20, 20).Frame(Widgets);

        d.Press(50, 50).Frame(Widgets);
        Assert.Equal(d.Ui.Id("inside"), insideActive);
    }

    [Fact]
    public void Modal_BlocksWholeScreen()
    {
        var d = new UiDriver();
        uint active = 1;
        void Widgets(UiContext ui)
        {
            ui.Button(new UiRect(0, 0, 40, 40), "", id: "far");
            active = ui.ActiveId;
            ui.BeginModal(new UiRect(100, 100, 150, 150));
            ui.EndModal();
        }
        d.Frame(Widgets);
        d.Press(10, 10).Frame(Widgets);
        Assert.Equal(0u, active);
    }

    [Fact]
    public void Tooltip_WaitsHalfSecondUnderMouse_AsksForRedraw()
    {
        var d = new UiDriver();
        var r = new UiRect(0, 0, 50, 50);
        double wake = 0;
        void Widgets(UiContext ui)
        {
            ui.Tooltip(r, "подсказка");
            wake = ui.WakeIn;
        }

        d.Move(10, 10).Frame(Widgets);
        Assert.Equal(0.5, wake, 1e-9);
        d.Frame(Widgets);
        Assert.Equal(0.5 - 1 / 60.0, wake, 1e-9);

        d.Move(100, 100).Frame(Widgets);   // мышь ушла — подсказка не нужна, кадр по таймеру тоже
        Assert.True(double.IsInfinity(wake));
    }

    // ── дерево, выпадающий список, двойной щелчок ──────────────────────

    [Fact]
    public void TreeNode_ArrowToggles_RowSelects_DoubleClickToggles()
    {
        var d = new UiDriver();
        bool open = false, clicked = false;
        void Widgets(UiContext ui) =>
            open = ui.TreeNode(new UiRect(0, 0, 200, 20), "node", "", depth: 0, leaf: false, selected: false, out clicked);

        d.Click(8, 10, Widgets);          // стрелка — x ∈ [0, 16)
        Assert.True(open);
        Assert.False(clicked);

        d.Press(100, 10).Frame(Widgets);  // строка: выбор, узел не трогается
        Assert.True(clicked);
        Assert.True(open);
        d.Release(100, 10).Frame(Widgets);
        d.Press(100, 10).Frame(Widgets);  // второе нажатие рядом по времени и месту — двойной щелчок
        Assert.True(d.Ui.DoubleClicked);
        Assert.False(open);
    }

    [Fact]
    public void Combo_ClickOpens_PickChangesIndex_EscapeCloses()
    {
        var d = new UiDriver();
        int index = 0;
        bool changed = false;
        string[] items = ["", "", ""];
        void Widgets(UiContext ui) => changed = ui.Combo(new UiRect(10, 10, 120, 30), "combo", ref index, items);

        d.Click(50, 20, Widgets);
        Assert.NotEmpty(d.Ui.Output.ClipRuns);   // раскрыт: окно списка со своей обрезкой
        // список: окно (10,32)–(170,106), строки по 22 px от y 36; третья — y 80…102
        d.Press(50, 90).Frame(Widgets);
        Assert.True(changed);
        Assert.Equal(2, index);
        d.Release(50, 90).Frame(Widgets);
        Assert.Empty(d.Ui.Output.ClipRuns);      // выбор закрыл список

        d.Click(50, 20, Widgets);
        d.Input.PressKey(UiKey.Escape);
        d.Frame(Widgets);
        d.Frame(Widgets);
        Assert.Empty(d.Ui.Output.ClipRuns);
        Assert.Equal(2, index);
    }

    // ── кадр без выделений ─────────────────────────────────────────────

    [Fact]
    public void ShapeWidgetFrame_AllocatesNothing()
    {
        // Кадр панели: ползунок тянут, список крутят, выпадающий список раскрыт — без выделений (состояние виджетов
        // заводится в первые кадры). Текст — в UiWidgetTextTests.
        var d = new UiDriver(width: 300, height: 300);
        float slider = 0.02f, split = 260;
        bool flag = false;
        int tab = 0, combo = 0;
        string[] tabs = ["", "", ""], items = ["", "", "", "", ""];
        void Widgets(UiContext ui)
        {
            ui.Panel(new UiRect(0, 0, 240, 300));
            ui.Button(new UiRect(10, 10, 100, 30), "", id: "b");
            ui.Toggle(new UiRect(110, 10, 230, 30), "", ref flag, id: "t");
            ui.Slider(new UiRect(10, 40, 230, 60), "s", ref slider, 0.02f, 2f, log: true);
            ui.ProgressBar(new UiRect(10, 64, 230, 72), -1);
            ui.Tabs(new UiRect(10, 76, 230, 100), "tabs", ref tab, tabs);
            var rows = ui.BeginList(new UiRect(10, 104, 230, 180), "list", 100, 10);
            for (int i = rows.First; i < rows.Last; i++) ui.Selectable(rows.Row(i), ui.Id(i), i == 3);
            ui.EndList();
            ui.TreeNode(new UiRect(10, 184, 230, 204), "node", "", 1, leaf: false, selected: true, out _);
            ui.Separator(new UiRect(10, 206, 230, 210));
            ui.Splitter(new UiRect(255, 0, 265, 300), "split", ref split, vertical: true, 240, 290);
            ui.OpenCombo("combo");
            ui.Combo(new UiRect(10, 214, 120, 234), "combo", ref combo, items);
        }

        Action<UiContext> frame = Widgets;   // делегат один: преобразование группы методов в цикле — выделение теста
        d.Frame(frame);
        d.Press(20, 50).Frame(frame);   // схватить ползунок
        for (int i = 0; i < 10; i++) d.Move(20 + i, i % 2 == 0 ? 50 : 150).Frame(frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            // ползунок следит только за X: на кадрах с колесом мышь — над списком, он прокручивается
            bool wheel = i % 10 == 0;
            d.Move(30 + i * 2, wheel ? 150 : 50);
            if (wheel) d.Input.Wheel = -1;
            d.Frame(frame);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(slider > 0.02f);
    }
}
