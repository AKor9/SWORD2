using Sword.Engine.Client;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Engine.Runtime;
using Sword.Engine.Ui;
using Sword.Game.Client;

namespace Sword.Game.Tests;

/// <summary>HUD арены без GPU: масштаб и цвета SWORD, справка по действующим привязкам.</summary>
public class HudTests
{
    [Theory]
    [InlineData(1080, 0.7f)]
    [InlineData(900, 900f / 1080 * 0.7f)]
    [InlineData(720, 0.75f * 0.7f)]    // ниже 810 пикселей не мельчает
    [InlineData(2160, 2f * 0.7f)]
    [InlineData(4320, 2f * 0.7f)]      // и не растёт больше двух
    public void Scale_ByWindowHeight_Like_SWORD(int height, float scale) => Assert.Equal(scale, Hud.Scale(height), 5);

    [Fact]
    public void FpsColour_Thresholds59And45()
    {
        var t = UiTheme.Default;
        Assert.Equal(t.Good, Hud.FpsColour(59));
        Assert.Equal(t.Accent, Hud.FpsColour(58.9));
        Assert.Equal(t.Accent, Hud.FpsColour(45));
        Assert.Equal(t.Bad, Hud.FpsColour(44.9));
    }

    [Fact]
    public void Help_ShowsActualBindings()
    {
        var config = ConfigSet.Empty("input.move_forward=Up", "input.hud_help=F2", "input.arena_reset=Home");
        var bindings = InputBindings.Resolve([.. ArenaInput.All, .. ClientHost.Actions], config);
        var lines = Hud.HelpLines(bindings.Keys);
        Assert.Contains("Up A S D — движение (Shift — шагом)", lines[0]);
        Assert.Contains("СКМ — камера", lines[1]);
        Assert.StartsWith("F2 — справка    F4 — снимок", lines[2]);
        Assert.Equal("PgUp / PgDn — врагов ±1    Home — сброс арены", lines[3]);   // клавиши арены (этап 4а)
    }
}

/// <summary>HUD на шрифтах пака: панель и справка в пределах окна, кадр HUD без выделений. Категория Content — нужен пак.</summary>
[Trait("Category", "Content")]
public sealed class HudDrawTests : IDisposable
{
    private readonly UiFonts _fonts;

    public HudDrawTests()
    {
        using var pak = ContentLocator.Open();
        var files = new List<UiFontFile>();
        foreach (var f in pak.List(UiVectorFontFormat.Dir, UiVectorFontFormat.Extension))
            using (var s = pak.OpenRead(f))
                files.Add(UiFontFile.Read(s, pak.Describe(f)));
        _fonts = new UiFonts(files);
    }

    public void Dispose() => _fonts.Dispose();

    private static readonly HudData Data = new(new FrameStats(143.6, 6.96, 9.1, 2.4, 0.3, 1, false), 14, 226, "NVIDIA GeForce RTX 5070");

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(640, 480)]
    [InlineData(3840, 2160)]
    public void Panels_StayInsideWindow_HelpHides(int w, int h)
    {
        var hud = new Hud();
        var d = new UiDrawList(_fonts);
        var help = Hud.HelpLines(new Dictionary<string, string>());
        hud.Draw(d, w, h, Data, help);
        int withHelp = d.Count;
        foreach (ref readonly var q in d.Quads)
        {
            Assert.InRange(q.Centre.X - q.HalfSize.X, -1, w + 1);
            Assert.InRange(q.Centre.X + q.HalfSize.X, -1, w + 1);
            Assert.InRange(q.Centre.Y - q.HalfSize.Y, -1, h * 0.5f);   // всё — в верхней половине окна
        }

        hud.ShowHelp = false;
        d.Clear();
        hud.Draw(d, w, h, Data, help);
        Assert.True(d.Count < withHelp - help.Sum(l => l.Count(c => !char.IsWhiteSpace(c))) + 2, "без справки квадов меньше на её буквы и панель");
    }

    [Fact]
    public void PerfPanel_EnclosesItsContent_WithPadding()
    {
        // Фон панели (первый квад) — вокруг всего её содержимого, снизу — отступ PerfPad·s (14 логических пикселей).
        var hud = new Hud { ShowHelp = false };
        var d = new UiDrawList(_fonts);
        hud.Draw(d, 1920, 1080, Data, []);
        var quads = d.Quads;
        float s = Hud.Scale(1080), bottom = quads[0].Centre.Y + quads[0].HalfSize.Y - 1;   // без каймы в пиксель
        float content = 0;
        foreach (ref readonly var q in quads[1..]) content = MathF.Max(content, q.Centre.Y + q.HalfSize.Y);
        Assert.InRange(bottom - content, 0, 14 * s + 4 * s);
    }

    [Fact]
    public void HelpText_HasEveryGlyph()
    {
        // «—», «−», «ЛКМ/СКМ», «+» — буквы справки есть в шрифте роли Body (не .notdef).
        var keys = new Dictionary<string, string> { ["camera_orbit"] = "MouseMiddle", ["walk"] = "MouseLeft", ["hud_help"] = "None" };
        foreach (var line in Hud.HelpLines(keys))
            foreach (var g in _fonts.Shape(UiFace.Body, line).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{line[(int)g.Cluster]}» в «{line}»");
    }

    [Fact]
    public void HudFrame_AllocatesNothing()
    {
        var hud = new Hud();
        var d = new UiDrawList(_fonts);
        var help = Hud.HelpLines(new Dictionary<string, string>());
        for (int i = 0; i < 5; i++)
        {
            d.Clear();
            hud.Draw(d, 1600, 900, Data, help);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            d.Clear();
            hud.Draw(d, 1600, 900, Data with { Frame = Data.Frame with { Fps = 100 + i, FrameMs = 1000.0 / (100 + i) } }, help);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
