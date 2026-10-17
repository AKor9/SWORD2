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
/// Раскладка при масштабе 1, верхе 0 и высоте 600: панель x 8…518, меню вкладок x 18…122 (строки по 28), ручки справа x 134…508:
/// строки с y 16 (заголовок 22, строки
/// по 24), под ними — состояние в три строки и подвал 26 у низа панели (<see cref="TuningPanel.Bounds"/>: высота по содержимому).
/// </summary>
[Trait("Category", "Content")]
public sealed class TuningPanelTests(PakFonts pak) : IClassFixture<PakFonts>
{
    private const float HeaderY = 16, RowH = 24;

    /// <summary>Середина строки <paramref name="index"/> группы «Бой» (0 — заголовок).</summary>
    private static float RowY(int index) => index == 0 ? HeaderY + 11 : HeaderY + 22 + (index - 1) * RowH + RowH / 2;

    /// <summary>Середина подвала: 10 от низа панели, высота 26.</summary>
    private static float FooterY(TuningPanel panel) => panel.Bounds.Y1 - 10 - 13;

    /// <summary>Полоса строки состояния: над подвалом через зазор 6.</summary>
    private static (float Y0, float Y1) StatusBand(TuningPanel panel) => (HeaderY + 22 + 6 * RowH, panel.Bounds.Y1 - 10 - 26);

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
        float y = RowY(2);   // острота: справа от меню (x 134…508) подписи узкие — столбец 30 % (112), ползунок x 246…450, поле 450…508
        d.Press(300, y);
        Assert.True(d.Mouse);
        for (int i = 0; i <= 10; i++) d.Move(300 + i * 20, y).Frame();   // тянут за правый край дорожки
        Assert.True(d.Mouse);
        Assert.Equal(1, k.Sharpness);
        d.Release(500, y);
        Assert.Equal(1, k.Sharpness);

        d.Click(150, y, button: 1);   // ПКМ по подписи — к значению по умолчанию
        Assert.Equal(0.5f, k.Sharpness);

        // Целое: ползунок даёт число, запись — целым.
        d.Press(348, RowY(3));   // середина хода ручки 256…440
        d.Release(348, RowY(3));
        Assert.InRange(k.Enemies, 15, 17);
    }

    [Fact]
    public void Toggle_Button_Info_Header()
    {
        var (_, k, d) = Open();
        Assert.True(d.TextIn(RowY(0) - 11, RowY(0) + 11) > 0);   // заголовок раздела
        Assert.True(d.TextIn(RowY(6) - 12, RowY(6) + 12) >= 6);  // «живых» и «17»

        d.Click(200, RowY(4));
        Assert.True(k.Invulnerable);
        d.Click(200, RowY(5));
        Assert.Equal(1, k.Clicks);

        k.Alive = 123456;
        int before = d.TextIn(RowY(6) - 12, RowY(6) + 12);
        d.Frame();
        Assert.Equal(before + 4, d.TextIn(RowY(6) - 12, RowY(6) + 12));   // строка состояния пересчитана в кадре
    }

    [Fact]
    public void HintOfHoveredRow_InStatusLine()
    {
        var (panel, _, d) = Open();
        var (y0, y1) = StatusBand(panel);
        Assert.Equal(0, d.TextIn(y0, y1));   // статуса ещё нет
        d.Move(160, RowY(1)).Frame();         // строка урона с подсказкой
        Assert.True(d.TextIn(y0, y1) > 10);
        d.Move(160, RowY(2)).Frame();         // у остроты подсказки нет
        Assert.Equal(0, d.TextIn(y0, y1));
    }

    [Fact]
    public void LongHint_WrapsByWords_InsideStatusLines()
    {
        var k = new Knobs();
        const string hint = "длинная подсказка строки, которая не влезает в одну строку панели и переносится по словам на следующие строки состояния";
        var panel = new TuningPanel { Open = true };
        panel.Groups.Add(new TuningGroup("long", "Длинная").Float("x", "икс", () => k.Damage, v => k.Damage = v, 0, 1, hint: hint));
        var d = new Driver(pak.Fonts, panel);
        d.Move(160, HeaderY + RowH / 2).Frame();
        float y0 = HeaderY + RowH, y1 = panel.Bounds.Y1 - 10 - 26;
        var glyphs = d.Ui.Output.Quads.ToArray().Where(q => q.Mode == UiMode.Text && q.Centre.Y >= y0 && q.Centre.Y < y1).ToArray();
        // строки — скопления центров glyph'ов по y: соседние строки дальше 8 px друг от друга (строка мелкого кегля ~15)
        var ys = glyphs.Select(q => q.Centre.Y).Order().ToArray();
        int lines = 1 + ys.Zip(ys.Skip(1), (a, b) => b - a > 8 ? 1 : 0).Sum();
        Assert.InRange(lines, 2, 3);   // перенесена, но не вылезла из трёх строк
        Assert.All(glyphs, q => Assert.InRange(q.Centre.X, panel.Bounds.X0, panel.Bounds.X1));
    }

    [Fact]
    public void PanelHeight_FollowsContent_ButStopsAtWindowBottom()
    {
        var k = new Knobs();
        var (panel, _, d) = Open();
        float shortTab = panel.Bounds.Y1;
        Assert.True(shortTab < 600 - 8 - 100, $"вкладка «Бой» из 7 строк — низ панели {shortTab}, не до низа окна");

        var tall = new TuningGroup("tall", "Длинная");
        for (int i = 0; i < 40; i++) tall.Float($"k{i}", "ручка", () => k.Damage, v => k.Damage = v, 0, 1);
        panel.Groups.Add(tall);
        panel.Tab = 1;
        d.Frame();
        Assert.Equal(600 - 8, panel.Bounds.Y1);   // 40 строк не влезают — до низа окна, дальше прокрутка
    }

    [Fact]
    public void Menu_ListsEveryTab_ClickSelects_PanelNotShorterThanMenu()
    {
        var k = new Knobs();
        var (panel, _, d) = Open();
        string[] titles = ["Рэгдолл", "Локомоция", "Свет", "Огонь", "Кровь", "Графика", "Звук", "Сеть", "Ввод"];
        foreach (string title in titles)
            panel.Groups.Add(new TuningGroup(title, title).Float("x", "икс", () => k.Damage, v => k.Damage = v, 0, 1));
        // каждая из десяти вкладок — строка меню слева (x 18…122, по 28 от y 16): щелчок выбирает её
        for (int i = panel.Groups.Count - 1; i >= 0; i--)
        {
            d.Click(60, HeaderY + 28 * i + 14);
            Assert.Equal(i, panel.Tab);
        }
        d.Click(60, HeaderY + 28 * 9 + 14);
        // у вкладки одна строка, но панель — не короче меню: последняя вкладка не обрезана
        Assert.True(panel.Bounds.Y1 >= HeaderY + 28 * 10 + 10, $"низ панели {panel.Bounds.Y1}");
    }

    [Fact]
    public void Footer_SaveLoadThroughHost_FailureInStatus_Reset()
    {
        var (panel, k, d) = Open();
        float footerY = FooterY(panel);   // подвал x 134…508: три кнопки по 122 через 4
        d.Click(195, footerY);   // хоста нет — «сохранить» выключена
        Assert.Equal("", panel.Status);

        Dictionary<string, double>? saved = null;
        panel.Save = g =>
        {
            saved = g.Values(changedOnly: true);
            return "сохранено: sword.user.json";
        };
        panel.Load = g => $"загружено значений: {g.Apply(new Dictionary<string, double> { ["damage"] = 4 })}";
        k.Sharpness = 0.25f;
        d.Click(195, footerY);
        Assert.Equal("сохранено: sword.user.json", panel.Status);
        Assert.Equal(0.25, saved!["sharpness"]);
        Assert.Single(saved);

        d.Click(321, footerY);
        Assert.Equal("загружено значений: 1", panel.Status);
        Assert.Equal(4, k.Damage);

        panel.Save = _ => throw new IOException("файл занят");
        d.Click(195, footerY);
        Assert.Equal("не сохранено: файл занят", panel.Status);

        d.Click(447, footerY);
        Assert.Equal((1f, 0.5f), (k.Damage, k.Sharpness));
        Assert.Equal("значения по умолчанию", panel.Status);
    }

    [Fact]
    public void Tabs_SwitchGroup()
    {
        var (panel, k, d) = Open();
        panel.Groups.Add(Knobs.Movement(k));
        d.Frame();
        d.Click(60, 16 + 28 + 14);   // меню слева: вторая строка — «Движение»
        Assert.Equal(1, panel.Tab);
        d.Click(340, HeaderY + RowH / 2);   // единственная строка вкладки «Движение» — первая, без заголовка
        Assert.NotEqual(1, k.Damage);
    }

    [Fact]
    public void MouseOverPanel_IsCaptured_OutsideIsNot()
    {
        var (panel, _, d) = Open();
        d.Move(300, 150).Frame();
        Assert.True(d.Mouse);
        d.Move(600, 100).Frame();
        Assert.False(d.Mouse);
        d.Move(300, panel.Bounds.Y1 + 20).Frame();   // под панелью высотой по содержимому — уже игра
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
        d.Press(300, y);
        for (int i = 0; i < 10; i++) d.Move(300 + i, y).Frame();   // прогрев: ползунок, поле числа, строка состояния
        d.Release(310, y);
        d.Move(160, y).Frame();

        float before = k.Damage;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        d.Press(300, y);
        for (int i = 0; i < 60; i++) d.Move(300 + i, y).Frame();   // тянут ползунок: поле рядом показывает число каждый кадр
        d.Release(360, y);
        for (int i = 0; i < 60; i++) d.Move(160, RowY(1 + i % 6)).Frame();   // подсказки строк, строка состояния, флаг, кнопка
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;

        Assert.Equal(0, allocated);
        Assert.NotEqual(before, k.Damage);
    }
}
