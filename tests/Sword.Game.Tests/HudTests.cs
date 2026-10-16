using Sword.Engine.Client;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Engine.Runtime;
using Sword.Engine.Ui;
using Sword.Game.Client;

namespace Sword.Game.Tests;

/// <summary>HUD арены без GPU: масштаб и цвета, справка по действующим привязкам.</summary>
public class HudTests
{
    [Theory]
    [InlineData(1080, 0.7f)]
    [InlineData(900, 900f / 1080 * 0.7f)]
    [InlineData(720, 0.75f * 0.7f)]    // ниже 810 пикселей не мельчает
    [InlineData(2160, 2f * 0.7f)]
    [InlineData(4320, 2f * 0.7f)]      // и не растёт больше двух
    public void Scale_ByWindowHeight(int height, float scale) => Assert.Equal(scale, Hud.Scale(height), 5);

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
        var config = ConfigSet.Empty("input.move_forward=Up", "input.hud_help=F2", "input.hud_profiler=F9", "input.arena_reset=Home",
            "input.debug_bodies=F8");
        var bindings = InputBindings.Resolve([.. ArenaInput.All, .. ClientHost.Actions], config);
        var lines = Hud.HelpLines(bindings.Keys);
        Assert.Contains("Up A S D — движение (Shift — шагом)", lines[0]);
        Assert.Contains("СКМ — камера", lines[1]);
        Assert.StartsWith("F2 — справка    F9 — профайлер    F4 — снимок", lines[2]);   // F2 по умолчанию — профайлер
        Assert.StartsWith("F1 — справка    F2 — профайлер    F4 — снимок", Hud.HelpLines(new Dictionary<string, string>())[2]);
        // Клавиши арены, меч (T) и капсулы тел с клинками (F7 по умолчанию, привязка — секция input).
        Assert.Equal("PgUp / PgDn — врагов ±1    Home — сброс арены    T — следующее оружие    F8 — капсулы и клинки", lines[3]);
        Assert.EndsWith("T — следующее оружие    F7 — капсулы и клинки", Hud.HelpLines(new Dictionary<string, string>())[3]);
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

    /// <summary>
    /// F2: секция профайлера — главные зоны кадра; буквы имён зон (шрифт Num) и строк секции (Body, Label) — в шрифте; секция внутри
    /// панели и окна; кадр HUD с ней не выделяет. Под зонами CPU — «GPU кадр» и проходы GPU (их имена — в шрифте Num), всё внутри панели.
    /// </summary>
    [Fact]
    public void ProfilerSection_F2_Zones_Glyphs_InsidePanel_NoAllocations()
    {
        var p = new Sword.Engine.Diagnostics.CpuProfiler(64);
        p.SetEnabled(true);
        string[] names = ["core.animation-tick@Animation", "invariant.animation.finite", "frame.fenceWait", "arena.build.skin",
            "invariant.movement.rootOverlap", "core.kinematic-placement@PrePhysics", "render.scene.opaque", "frame.record", "stage.Animation"];
        int[] zones = [.. names.Select(p.Zone)];
        // GPU-время кадра и проходов (зоны вида Gpu) — строки «GPU кадр» и проходов под зонами CPU.
        string[] gpuNames = ["scene.opaque", "hud", "debug.bodies"];
        int gpuFrame = p.Zone(Sword.Engine.Diagnostics.CpuProfiler.GpuFrameName, Sword.Engine.Diagnostics.ProfileZoneKind.Gpu);
        int[] gpuZones = [.. gpuNames.Select(n => p.Zone("gpu." + n, Sword.Engine.Diagnostics.ProfileZoneKind.Gpu))];
        var digest = new Sword.Engine.Diagnostics.ProfileDigest(p);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        void Frames(int n)
        {
            for (int f = 0; f < n; f++)
            {
                p.BeginFrame((ulong)f);
                foreach (int z in zones)
                {
                    p.Begin(z);
                    long until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 100_000;
                    while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
                    p.End(z);
                }
                p.NoteGpuFrame(3);
                p.RecordGpu(gpuFrame, 1.2);
                for (int i = 0; i < gpuZones.Length; i++) p.RecordGpu(gpuZones[i], 0.3 * (i + 1));
                p.EndFrame(1, false);
            }
        }
        Frames(40);
        digest.Update(now);
        Frames(40);
        digest.Update(now + System.Diagnostics.Stopwatch.Frequency);
        Assert.True(digest.Ready);
        Assert.Equal(3, digest.GpuCount);
        Assert.Equal("debug.bodies", digest.GpuName(0).ToString());

        // Буквы: имена зон — шрифт Num, строки секции — Body, заголовок — Label.
        foreach (string name in names.Append(Hud.FrameSelfName).Concat(gpuNames))
            foreach (var g in _fonts.Shape(UiFace.Num, name).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{name[(int)g.Cluster]}» в «{name}» (Num)");
        foreach (string text in (string[])["профайлер: собираю кадры…", "профайлер включается…", "GPU кадр", "профайлера нет"])
            foreach (var g in _fonts.Shape(UiFace.Body, text).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{text[(int)g.Cluster]}» в «{text}» (Body)");
        foreach (var g in _fonts.Shape(UiFace.Label, "ПРОФАЙЛЕР, мс").ToArray()) Assert.True(g.GlyphId != 0);

        var data = Data with { Profile = digest, Frame = Data.Frame with { GpuMs = 1.25, GpuPassesMs = 1.1 } };
        var hud = new Hud { ShowHelp = false };
        var d = new UiDrawList(_fonts);
        hud.Draw(d, 1600, 900, data, []);
        int without = d.Count;
        float bottomWithout = d.Quads[0].Centre.Y + d.Quads[0].HalfSize.Y;
        hud.ShowProfiler = true;
        d.Clear();
        hud.Draw(d, 1600, 900, data, []);
        Assert.True(d.Count > without + 100, $"квадов с F2 {d.Count}, без — {without}");
        var quads = d.Quads;
        float bottom = quads[0].Centre.Y + quads[0].HalfSize.Y;
        Assert.True(bottom > bottomWithout);
        foreach (ref readonly var q in quads[1..])
        {
            Assert.True(q.Centre.Y + q.HalfSize.Y <= bottom + 1, "содержимое F2 — внутри панели");
            Assert.InRange(q.Centre.X + q.HalfSize.X, 0, quads[0].Centre.X + quads[0].HalfSize.X + 1);
        }
        Assert.True(bottom < 900, "панель с F2 — в окне");

        for (int i = 0; i < 5; i++)
        {
            d.Clear();
            hud.Draw(d, 1600, 900, data, []);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            d.Clear();
            hud.Draw(d, 1600, 900, data with { Frame = data.Frame with { Fps = 100 + i } }, []);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
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
