using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Formats;
using Sword.Engine.Platform;
using Sword.Engine.Ui;

namespace Sword.Engine.DevTools.Tests;

/// <summary>Шрифты пака контента на все тесты класса: чтение и склейка — один раз.</summary>
public sealed class PakFonts : IDisposable
{
    public PakFonts()
    {
        using var pak = ContentLocator.Open();
        var files = pak.List(UiVectorFontFormat.Dir, UiVectorFontFormat.Extension)
            .Select(f =>
            {
                using var s = pak.OpenRead(f);
                return UiFontFile.Read(s, pak.Describe(f));
            }).ToList();
        Fonts = new UiFonts(files);
    }

    public UiFonts Fonts { get; }

    public void Dispose() => Fonts.Dispose();
}

/// <summary>Настройки-заглушка группы: значения, которые панель читает и пишет делегатами.</summary>
internal sealed class Knobs
{
    public float Damage = 1;
    public float Sharpness = 0.5f;
    public int Enemies = 4;
    public bool Invulnerable;
    public int Clicks;
    public int Alive = 17;

    public TuningGroup Combat() => new TuningGroup("combat", "Бой")
        .Header("Урон")
        .Float("damage", "урон", () => Damage, v => Damage = v, 0.1f, 10, log: true, hint: "множитель урона по врагам")
        .Float("sharpness", "острота", () => Sharpness, v => Sharpness = v, 0, 1)
        .Int("enemies", "врагов", () => Enemies, v => Enemies = v, 0, 32)
        .Bool("invulnerable", "игрок неуязвим", () => Invulnerable, v => Invulnerable = v)
        .Button("сбросить арену", () => Clicks++)
        .Info("живых", d =>
        {
            Alive.TryFormat(d, out int n);
            return n;
        });

    public static TuningGroup Movement(Knobs k) => new TuningGroup("movement", "Движение")
        .Float("speed", "скорость", () => k.Damage, v => k.Damage = v, 0.1f, 10);
}

/// <summary>Панель ручек без шрифтов: закрытая и пустая ничего не стоят, клавиша открытия — действие панели.</summary>
public sealed class TuningPanelLogicTests
{
    [Fact]
    public void ClosedOrEmpty_DrawsNothing_AndIsNotShown()
    {
        var panel = new TuningPanel();
        var ui = new UiContext();
        var input = new UiInput { MouseInside = true, Mouse = new Vector2(50, 50) };

        ui.BeginFrame(input, 640, 600, 1);
        Assert.False(panel.Draw(ui, 0));   // закрыта
        panel.Open = true;
        Assert.False(panel.Draw(ui, 0));   // открыта, но групп нет
        ui.EndFrame();
        Assert.Equal(0, ui.Output.Count);
        Assert.False(panel.Update(default));   // открыта без групп — не видна (без действия ввод не читается)
    }

    [Fact]
    public void ToggleAction_IsPanelAction_AndOpensAndCloses()
    {
        var toggle = new InputAction("tuning", "F3");
        var panel = new TuningPanel(toggle);
        panel.Groups.Add(new Knobs().Combat());
        Assert.Equal([toggle], ((IClientPanel)panel).Actions);
        Assert.Empty(((IClientPanel)new TuningPanel()).Actions);

        var bindings = InputBindings.Resolve([toggle], null);
        var state = new InputState();
        var held = new HashSet<string>(["tuning"]);
        // Действие «нажато» — в первом кадре удержания заданием прогона (--hold tuning: так прогон без клавиатуры открывает панель).
        var pressed = new FrameInput(state, bindings, held, heldPressed: true, mouseInside: true, 640, 480, 1f / 60);
        var idle = new FrameInput(state, bindings, null, heldPressed: false, mouseInside: true, 640, 480, 1f / 60);

        Assert.False(panel.Update(idle));
        Assert.True(panel.Update(pressed));
        Assert.True(panel.Open);
        Assert.True(panel.Update(idle));
        Assert.False(panel.Update(pressed));
        Assert.False(panel.Open);
    }
}

/// <summary>
/// Панель ручек на шрифтах пака: числа, флаги, кнопки, вкладки, подвал, строка состояния, мышь у панели, кадр без выделений.
/// Раскладка при масштабе 1, верхе 0 и высоте 600: строки с y 46 (заголовок 22, строки по 24), состояние 538…556, подвал 556…582.
/// </summary>
[Trait("Category", "Content")]
public sealed class TuningPanelTests(PakFonts pak) : IClassFixture<PakFonts>
{
    private const float HeaderY = 46, RowH = 24;

    /// <summary>Середина строки <paramref name="index"/> группы «Бой» (0 — заголовок).</summary>
    private static float RowY(int index) => index == 0 ? HeaderY + 11 : HeaderY + 22 + (index - 1) * RowH + RowH / 2;

    private sealed class Driver(UiFonts fonts, TuningPanel panel)
    {
        public UiContext Ui { get; } = new(fonts);
        public UiInput Input { get; } = new() { MouseInside = true };
        public bool Mouse { get; private set; }

        public void Frame()
        {
            Ui.BeginFrame(Input, 640, 600, 1);
            Mouse = panel.Draw(Ui, 0);
            Ui.EndFrame();
            Input.BeginFrame();
            Input.Time += 1 / 60.0;
        }

        public Driver Move(float x, float y)
        {
            Input.Mouse = new Vector2(x, y);
            return this;
        }

        public void Press(float x, float y, int button = 0)
        {
            Move(x, y);
            Input.SetMouseButton(button, down: true, pressed: true, released: false);
            Frame();
        }

        public void Release(float x, float y, int button = 0)
        {
            Move(x, y);
            Input.SetMouseButton(button, down: false, pressed: false, released: true);
            Frame();
        }

        public void Click(float x, float y, int button = 0)
        {
            Press(x, y, button);
            Release(x, y, button);
        }

        /// <summary>Текст (квады glyph'ов) в полосе y [<paramref name="y0"/>, <paramref name="y1"/>).</summary>
        public int TextIn(float y0, float y1)
        {
            int n = 0;
            foreach (var q in Ui.Output.Quads)
                if (q.Mode == UiMode.Text && q.Centre.Y >= y0 && q.Centre.Y < y1) n++;
            return n;
        }
    }

    private (TuningPanel Panel, Knobs Knobs, Driver Driver) Open()
    {
        var knobs = new Knobs();
        var panel = new TuningPanel { Open = true };
        panel.Groups.Add(knobs.Combat());
        var driver = new Driver(pak.Fonts, panel);
        driver.Move(600, 300).Frame();
        return (panel, knobs, driver);
    }

    [Fact]
    public void Slider_DragSetsValue_NumberRowShowsIt_RightClickOnLabelResets()
    {
        var (_, k, d) = Open();
        float y = RowY(2);   // острота: ползунок x 156…276 (ручка ходит 162…270), поле 280…338
        d.Press(200, y);
        Assert.True(d.Mouse);
        for (int i = 0; i <= 10; i++) d.Move(200 + i * 10, y).Frame();   // тянут за правый край дорожки
        Assert.True(d.Mouse);
        Assert.Equal(1, k.Sharpness);
        d.Release(300, y);
        Assert.Equal(1, k.Sharpness);

        d.Click(40, y, button: 1);   // ПКМ по подписи — к значению по умолчанию
        Assert.Equal(0.5f, k.Sharpness);

        // Целое: ползунок даёт число, запись — целым.
        d.Press(216, RowY(3));
        d.Release(216, RowY(3));
        Assert.InRange(k.Enemies, 15, 17);
    }

    [Fact]
    public void Toggle_Button_Info_Header()
    {
        var (_, k, d) = Open();
        Assert.True(d.TextIn(RowY(0) - 11, RowY(0) + 11) > 0);   // заголовок раздела
        Assert.True(d.TextIn(RowY(6) - 12, RowY(6) + 12) >= 6);  // «живых» и «17»

        d.Click(100, RowY(4));
        Assert.True(k.Invulnerable);
        d.Click(100, RowY(5));
        Assert.Equal(1, k.Clicks);

        k.Alive = 123456;
        int before = d.TextIn(RowY(6) - 12, RowY(6) + 12);
        d.Frame();
        Assert.Equal(before + 4, d.TextIn(RowY(6) - 12, RowY(6) + 12));   // строка состояния пересчитана в кадре
    }

    [Fact]
    public void HintOfHoveredRow_InStatusLine()
    {
        var (_, _, d) = Open();
        Assert.Equal(0, d.TextIn(538, 556));   // статуса ещё нет
        d.Move(60, RowY(1)).Frame();           // строка урона с подсказкой
        Assert.True(d.TextIn(538, 556) > 10);
        d.Move(60, RowY(2)).Frame();           // у остроты подсказки нет
        Assert.Equal(0, d.TextIn(538, 556));
    }

    [Fact]
    public void Footer_SaveLoadThroughHost_FailureInStatus_Reset()
    {
        var (panel, k, d) = Open();
        const float footerY = 569;
        d.Click(70, footerY);   // хоста нет — «сохранить» выключена
        Assert.Equal("", panel.Status);

        Dictionary<string, double>? saved = null;
        panel.Save = g =>
        {
            saved = g.Values(changedOnly: true);
            return "сохранено: sword.user.json";
        };
        panel.Load = g => $"загружено значений: {g.Apply(new Dictionary<string, double> { ["damage"] = 4 })}";
        k.Sharpness = 0.25f;
        d.Click(70, footerY);
        Assert.Equal("сохранено: sword.user.json", panel.Status);
        Assert.Equal(0.25, saved!["sharpness"]);
        Assert.Single(saved);

        d.Click(178, footerY);
        Assert.Equal("загружено значений: 1", panel.Status);
        Assert.Equal(4, k.Damage);

        panel.Save = _ => throw new IOException("файл занят");
        d.Click(70, footerY);
        Assert.Equal("не сохранено: файл занят", panel.Status);

        d.Click(286, footerY);
        Assert.Equal((1f, 0.5f), (k.Damage, k.Sharpness));
        Assert.Equal("значения по умолчанию", panel.Status);
    }

    [Fact]
    public void Tabs_SwitchGroup()
    {
        var (panel, k, d) = Open();
        panel.Groups.Add(Knobs.Movement(k));
        d.Frame();
        float first = new UiDrawList(pak.Fonts).Measure(UiFace.Label, d.Ui.Theme.FontSize, "Бой") + 24;
        d.Click(18 + first + 10, 28);
        Assert.Equal(1, panel.Tab);
        d.Click(250, HeaderY + RowH / 2);   // единственная строка вкладки «Движение» — первая, без заголовка
        Assert.NotEqual(1, k.Damage);
    }

    [Fact]
    public void MouseOverPanel_IsCaptured_OutsideIsNot()
    {
        var (_, _, d) = Open();
        d.Move(100, 300).Frame();
        Assert.True(d.Mouse);
        d.Move(400, 300).Frame();
        Assert.False(d.Mouse);
        d.Input.MouseInside = false;
        d.Move(100, 300).Frame();
        Assert.False(d.Mouse);
    }

    [Fact]
    public void OpenPanelFrame_AllocatesNothing()
    {
        var (_, k, d) = Open();
        float y = RowY(1);
        d.Press(200, y);
        for (int i = 0; i < 10; i++) d.Move(200 + i, y).Frame();   // прогрев: ползунок, поле числа, строка состояния
        d.Release(210, y);
        d.Move(60, y).Frame();

        float before = k.Damage;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        d.Press(200, y);
        for (int i = 0; i < 60; i++) d.Move(200 + i, y).Frame();   // тянут ползунок: поле рядом показывает число каждый кадр
        d.Release(260, y);
        for (int i = 0; i < 60; i++) d.Move(60, RowY(1 + i % 6)).Frame();   // подсказки строк, строка состояния, флаг, кнопка
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;

        Assert.Equal(0, allocated);
        Assert.NotEqual(before, k.Damage);
    }
}
