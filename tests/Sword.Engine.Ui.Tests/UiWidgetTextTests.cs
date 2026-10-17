using System.Globalization;
using System.Numerics;
using Sword.Engine.Content;
using Sword.Tests;

namespace Sword.Engine.Ui.Tests;

/// <summary>
/// Виджеты с текстом на шрифтах пака: поле ввода, числовое поле, подсказка, кадр панели без выделений. Категория Content:
/// нужен пак (<see cref="ContentLocator"/>); <c>verify.ps1 -SkipReference</c> их не запускает.
/// </summary>
[Trait("Category", "Content")]
public sealed class UiWidgetTextTests(PakFonts pak) : IClassFixture<PakFonts>
{
    private UiDriver Driver(int width = 300, int height = 200) => new(pak.Fonts, width, height);

    // ── поле ввода ─────────────────────────────────────────────────────

    [Fact]
    public void TextField_TypingEdits_CtrlBackspaceDeletesWord_EnterCommits()
    {
        var d = Driver();
        string text = "";
        bool changed = false;
        void Widgets(UiContext ui) => changed = ui.TextField(new UiRect(10, 10, 200, 34), "name", ref text, "имя");

        d.Frame(Widgets);   // без фокуса и текста — заполнитель
        Assert.Contains(d.Ui.Output.Quads.ToArray(), q => q.Mode == UiMode.Text && q.ColourA == d.Ui.Theme.TextFaint);

        d.Click(50, 20, Widgets);
        Assert.True(d.Ui.WantsKeyboard);
        d.Input.Typed.Append("меч 1hm");
        d.Frame(Widgets);
        Assert.True(changed);
        Assert.Equal("меч 1hm", text);

        d.Input.Ctrl = true;
        d.Input.PressKey(UiKey.Backspace);
        d.Frame(Widgets);
        d.Input.Ctrl = false;
        Assert.Equal("меч ", text);

        d.Input.PressKey(UiKey.Enter);
        d.Frame(Widgets);
        Assert.False(d.Ui.WantsKeyboard);
        Assert.Equal(UiEditExit.Commit, d.Ui.State<UiTextEdit>(d.Ui.Id("name")).LastExit);
    }

    [Fact]
    public void TextField_CopyPasteThroughClientClipboard()
    {
        var d = Driver();
        string text = "abc";
        string? clipboard = null;
        d.Input.SetClipboard = s => clipboard = s;
        d.Input.GetClipboard = () => "вставка";
        void Widgets(UiContext ui) => ui.TextField(new UiRect(10, 10, 200, 34), "name", ref text);

        d.Click(50, 20, Widgets);
        d.Input.Ctrl = true;
        d.Input.PressKey(UiKey.A);
        d.Frame(Widgets);
        d.Input.PressKey(UiKey.C);
        d.Frame(Widgets);
        Assert.Equal("abc", clipboard);
        d.Input.PressKey(UiKey.V);
        d.Frame(Widgets);
        Assert.Equal("вставка", text);   // вставка — вместо выделенного
    }

    [Fact]
    public void TextField_DoubleClickSelectsWordUnderMouse()
    {
        var d = Driver();
        string text = "load sword now";
        void Widgets(UiContext ui) => ui.TextField(new UiRect(10, 10, 280, 34), "name", ref text);
        // текст поля — от x 10 + 7 (поле), середина «sword» — перед седьмым символом
        float x = 17 + new UiDrawList(pak.Fonts).CaretX(UiFace.Body, d.Ui.Theme.FontSize, text, 7);

        d.Click(x, 20, Widgets);
        d.Press(x, 20).Frame(Widgets);
        Assert.True(d.Ui.DoubleClicked);
        Assert.Equal("sword", d.Ui.State<UiTextEdit>(d.Ui.Id("name")).Selected(text));
    }

    // ── числовое поле ──────────────────────────────────────────────────

    private static void SelectAllAndType(UiDriver d, Action<UiContext> widgets, string typed)
    {
        d.Input.Ctrl = true;
        d.Input.PressKey(UiKey.A);
        d.Frame(widgets);
        d.Input.Ctrl = false;
        d.Input.Typed.Append(typed);
        d.Frame(widgets);
    }

    [Fact]
    public void NumberField_EnterCommitsParsedValue_CommaAccepted_ClampedToRange()
    {
        var d = Driver();
        float value = 1.5f;
        bool changed = false;
        void Widgets(UiContext ui) => changed = ui.NumberField(new UiRect(10, 10, 110, 34), "dmg", ref value, "0.###", 0, 10);

        d.Frame(Widgets);
        d.Click(50, 20, Widgets);
        SelectAllAndType(d, Widgets, "12,5");
        Assert.Equal(1.5f, value);   // до Enter значение прежнее

        d.Input.PressKey(UiKey.Enter);
        d.Frame(Widgets);
        Assert.True(changed);
        Assert.Equal(10f, value);
        // поле правки — ID «подпись#edit», без склейки строк в кадре
        Assert.Equal(UiEditExit.Commit, d.Ui.State<UiTextEdit>(d.Ui.Id("dmg#edit")).LastExit);
    }

    [Fact]
    public void NumberField_WithSliderOfSameLabel_EditSurvivesFrames()
    {
        // Состояния ползунка, числа и его поля правки — под разными ID: с общим ID State<T> каждый кадр подменял бы одно другим.
        var d = Driver();
        float value = 1f;
        void Widgets(UiContext ui)
        {
            ui.Slider(new UiRect(10, 40, 200, 60), "speed", ref value, 0, 10);
            ui.NumberField(new UiRect(10, 10, 110, 34), "speed", ref value, "0.###", 0, 10);
        }

        d.Click(50, 20, Widgets);
        SelectAllAndType(d, Widgets, "2");
        d.Frame(Widgets);
        d.Input.Typed.Append("5");
        d.Frame(Widgets);
        d.Input.PressKey(UiKey.Enter);
        d.Frame(Widgets);
        Assert.Equal(10f, value);   // «25» прижато к 10
    }

    [Theory]
    [InlineData("7", UiKey.Escape, 3f)]    // Escape — отмена
    [InlineData("abc", UiKey.Enter, 3f)]   // не число — значение прежнее
    public void NumberField_CancelOrGarbage_KeepsValue(string typed, UiKey key, float expected)
    {
        var d = Driver();
        float value = 3f;
        bool changed = false;
        void Widgets(UiContext ui) => changed = ui.NumberField(new UiRect(10, 10, 110, 34), "n", ref value, "0.###");

        d.Click(50, 20, Widgets);
        SelectAllAndType(d, Widgets, typed);
        d.Input.PressKey(key);
        d.Frame(Widgets);
        Assert.False(changed);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void NumberField_ClickElsewhereCommits()
    {
        var d = Driver();
        float value = 3f;
        void Widgets(UiContext ui) => ui.NumberField(new UiRect(10, 10, 110, 34), "n", ref value, "0.###");

        d.Click(50, 20, Widgets);
        SelectAllAndType(d, Widgets, "4");
        d.Press(250, 150).Frame(Widgets);
        Assert.Equal(4f, value);
        Assert.False(d.Ui.WantsKeyboard);
    }

    // ── подсказка и строка-сведение ────────────────────────────────────

    [Fact]
    public void Tooltip_DrawnOnTopBelowCursor_AfterHalfSecond()
    {
        var d = Driver();
        var r = new UiRect(0, 0, 50, 50);
        void Widgets(UiContext ui) => ui.Tooltip(r, "подсказка");

        d.Move(10, 10).Frame(Widgets);
        Assert.Equal(0, d.Ui.Output.Count);
        for (int i = 0; i < 32; i++) d.Frame(Widgets);   // больше 0,5 с при 60 кадрах в секунду
        var quads = d.Ui.Output.Quads;
        Assert.Equal(d.Ui.Theme.Popup, quads[0].ColourA);
        Assert.True(quads[0].Centre.Y - quads[0].HalfSize.Y >= 10 + 20 - 1);   // под курсором (кайма — пиксель)
        Assert.Equal(UiMode.Text, quads[^1].Mode);
    }

    [Fact]
    public void InfoRow_ValueRightAlignedInNumFace()
    {
        var d = Driver();
        d.Frame(ui => ui.InfoRow(new UiRect(0, 0, 200, 20), "живых", "12"));
        var tables = pak.Fonts.Tables;
        int numFrom = tables.FaceOffsets[(int)UiFace.Num], numTo = numFrom + tables.FaceGlyphCounts[(int)UiFace.Num];
        float rightmost = 0;
        int numGlyphs = 0;
        foreach (ref readonly var q in d.Ui.Output.Quads)
        {
            rightmost = MathF.Max(rightmost, q.Centre.X + q.HalfSize.X);
            if (q.Shape.X >= numFrom && q.Shape.X < numTo && q.ColourA == d.Ui.Theme.Text) numGlyphs++;
        }
        Assert.Equal(2, numGlyphs);
        Assert.InRange(rightmost, 190, 202);   // значение кончается у правого края (кайма — пиксель)
    }

    [Fact]
    public void TextBox_FillFirst_TextLastInsideBox_LongTextEllipsized()
    {
        var list = new UiDrawList(pak.Fonts);
        var size = list.TextBoxSize(UiFace.Body, 14, "подсказка", new Vector2(6, 4));
        var box = UiRect.FromSize(20, 30, size.X * 0.5f, size.Y);   // вдвое уже текста
        list.TextBox(box, UiFace.Body, 14, 0xffffffff, "подсказка", 0x80000000, 6, 3);
        var quads = list.Quads;
        Assert.Equal(0x80000000u, quads[0].ColourA);
        Assert.Equal(UiMode.Text, quads[^1].Mode);
        foreach (ref readonly var q in quads[1..])
        {
            Assert.InRange(q.Centre.X, box.X0 + 6, box.X1 - 6);   // текст — в поле подложки
            Assert.InRange(q.Centre.Y, box.Y0, box.Y1);
        }
    }

    // ── кадр без выделений ─────────────────────────────────────────────

    [Fact]
    public void PanelFrameWithText_AllocatesNothing()
    {
        // Панель ручек: вкладки, ползунок тянут — числовое поле рядом показывает новое значение каждый кадр (со стека,
        // без строки), поле ввода с фокусом мигает кареткой, строки списка с подписями, раскрытый выпадающий список,
        // затем мышь отпущена и над строкой появляется подсказка.
        var d = Driver(400, 400);
        float speed = 1f;
        bool flag = true;
        int tab = 1, combo = 1;
        string name = "sword_1h";
        string[] tabs = ["Бой", "Движение", "Камера"], items = ["sword_1h", "sword_2h", "katana"];
        void Widgets(UiContext ui)
        {
            var t = ui.Theme;
            ui.Panel(new UiRect(0, 0, 360, 400), t.OverlayStrong, 8);
            ui.SectionHeader(new UiRect(10, 4, 350, 20), "РУЧКИ");
            ui.Tabs(new UiRect(10, 22, 350, 46), "tabs", ref tab, tabs);
            ui.Slider(new UiRect(10, 50, 260, 70), "speed", ref speed, 0.02f, 2f, log: true);
            ui.NumberField(new UiRect(266, 50, 350, 70), "speed.num", ref speed, "0.###", 0.02f, 2f);
            ui.Toggle(new UiRect(10, 74, 350, 94), "враги нападают", ref flag, id: "flag");
            ui.Button(new UiRect(10, 98, 170, 122), "Сохранить", primary: true);
            Span<char> buffer = stackalloc char[32];
            speed.TryFormat(buffer, out int n, "0.00", CultureInfo.InvariantCulture);
            ui.InfoRow(new UiRect(10, 126, 350, 144), "скорость", buffer[..n]);
            ui.Label(new UiRect(10, 146, 120, 164), "очень длинная подпись, которая не влезет", t.TextDim);
            ui.TextField(new UiRect(10, 168, 350, 192), "name", ref name, "имя");
            var rows = ui.BeginList(new UiRect(10, 196, 350, 280), "list", 50, 20);
            for (int i = rows.First; i < rows.Last; i++)
            {
                var row = rows.Row(i);
                ui.Selectable(row, ui.Id(i), i == 2);
                ui.Label(row.Shrink(6, 0), items[i % items.Length]);
            }
            ui.EndList();
            ui.TreeNode(new UiRect(10, 284, 350, 304), "node", "Скелет", 0, leaf: false, selected: false, out _, dot: t.Good);
            ui.ProgressBar(new UiRect(10, 308, 350, 318), 0.4f, "40%");
            ui.Tooltip(new UiRect(10, 322, 350, 342), "подсказка строки");
            ui.Combo(new UiRect(10, 346, 200, 370), "combo", ref combo, items);
        }

        // Раскрытый список не влезает под поле и встаёт над ним — (10,270)–(200,344): подсказку ждут правее, у x 300.
        Action<UiContext> frame = Widgets;
        d.Frame(frame);
        d.Press(20, 60).Frame(frame);            // схватить ползунок (щелчок снял бы фокус поля — фокус ставится после)
        d.Ui.FocusId = d.Ui.Id("name");
        for (int i = 0; i < 10; i++) d.Move(30 + i, 60).Frame(frame);
        d.Release(40, 60).Frame(frame);
        d.Click(100, 358, frame);                // раскрыть список
        d.Ui.FocusId = d.Ui.Id("name");
        for (int i = 0; i < 40; i++) d.Move(300, 330).Frame(frame);   // подсказка показана хотя бы раз
        Assert.Equal(UiMode.Text, d.Ui.Output.Quads[^1].Mode);
        d.Press(20, 60).Frame(frame);
        d.Ui.FocusId = d.Ui.Id("name");

        float speedBefore = speed;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++) d.Move(30 + i * 3, 60).Frame(frame);
        d.Release(210, 60).Frame(frame);
        d.Click(100, 358, frame);                // нажатие на ползунок закрыло список — раскрыть снова
        d.Ui.FocusId = d.Ui.Id("name");
        for (int i = 0; i < 60; i++) d.Move(300, 330).Frame(frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.NotEqual(speedBefore, speed);
        Assert.True(d.Ui.WantsKeyboard);
        Assert.Equal(UiMode.Text, d.Ui.Output.Quads[^1].Mode);   // подсказка — последней
        bool popup = false;
        foreach (ref readonly var q in d.Ui.Output.Quads) popup |= q.ColourA == d.Ui.Theme.Shadow;   // тень — только у всплывающего окна
        Assert.True(popup);   // список раскрыт и в замере
    }
}
