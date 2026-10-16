using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Content;
using Sword.Engine.DevTools;
using Sword.Engine.Diagnostics;
using Sword.Engine.Formats;
using Sword.Engine.Platform;
using Sword.Engine.Runtime;
using Sword.Engine.Ui;
using Sword.Game.Client;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// На шрифтах пака (категория Content): HUD боя, экран «ПАЛ», панель инвентаря (щелчок — «взять меч»), вкладка «Бой» панели F3
/// (ползунок ставит команду ручки и показывает ожидающее значение); кадры без выделений.
/// </summary>
[Trait("Category", "Content")]
public sealed class CombatUiDrawTests : IDisposable
{
    private readonly UiFonts _fonts;

    public CombatUiDrawTests()
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
    private static readonly CombatHud Alive = new(62, false, 7, 10, 3, 12, BodySegs.Head, 60.8f, "sword_1h");

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(640, 480)]
    [InlineData(3840, 2160)]
    public void CombatHud_InsideWindow_BarAtBottom_SectionInPanel(int w, int h)
    {
        var hud = new Hud();
        var d = new UiDrawList(_fonts);
        var help = Hud.HelpLines(new Dictionary<string, string>());
        hud.Draw(d, w, h, Data, help);
        int plain = d.Count;
        float plainBottom = hud.PerfBottom;
        d.Clear();
        hud.Draw(d, w, h, Data with { Combat = Alive }, help);
        Assert.True(d.Count > plain + 40, $"квадов с боем {d.Count}, без — {plain}");
        Assert.True(hud.PerfBottom > plainBottom, "секция «ФИЗИКА И БОЙ» — в панели производительности");
        float lowest = 0;
        foreach (ref readonly var q in d.Quads)
        {
            Assert.InRange(q.Centre.X - q.HalfSize.X, -1, w + 1);
            Assert.InRange(q.Centre.X + q.HalfSize.X, -1, w + 1);
            Assert.InRange(q.Centre.Y - q.HalfSize.Y, -1, h + 1);
            Assert.InRange(q.Centre.Y + q.HalfSize.Y, -1, h + 1);
            lowest = MathF.Max(lowest, q.Centre.Y + q.HalfSize.Y);
        }
        Assert.True(lowest > h * 0.9f, "полоса здоровья и подпись меча — внизу");
        // Инвентарь открыт — подписи меча в углу нет.
        int withLabel = d.Count;
        d.Clear();
        hud.Draw(d, w, h, Data with { Combat = Alive with { InventoryOpen = true } }, help);
        Assert.True(d.Count < withLabel - 20);
        // Мёртвый игрок — затемнение на весь экран и «ПАЛ».
        d.Clear();
        hud.Draw(d, w, h, Data with { Combat = Alive with { PlayerDead = true, PlayerHp = -4 } }, help);
        int dead = d.Count;
        Assert.Contains(d.Quads.ToArray(), q => q.Mode == UiMode.Box && q.HalfSize.X >= w / 2f - 1 && q.HalfSize.Y >= h / 2f - 1);
        d.Clear();
        hud.Draw(d, w, h, Data with { Combat = Alive with { PlayerHp = -4 } }, help);
        Assert.True(dead > d.Count + 10, "экран «ПАЛ» — затемнение и две строки");
    }

    [Fact]
    public void CombatTexts_HaveEveryGlyph()
    {
        Span<char> buf = stackalloc char[96];
        string[] body = ["враги", "убито", "попаданий", Hud.FallenText(new Line(buf), 12, "R").ToString(), .. Hud.HelpLines(
            new Dictionary<string, string> { [Hud.TuningAction] = "F3" })];
        foreach (string text in body)
            foreach (var g in _fonts.Shape(UiFace.Body, text).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{text[(int)g.Cluster]}» в «{text}» (Body)");
        string[] label = ["ФИЗИКА И БОЙ", Hud.LastHitText(new Line(buf), Alive).ToString(), Hud.WeaponText(new Line(buf), "sword_1h", "I").ToString(),
            "ИНВЕНТАРЬ   оружие 6", "1–9, щелчок — взять    I — закрыть", "УДАР ИГРОКА", "ВРАГИ И ИГРОК", "РЫВОК", .. Enumerable.Range(0, 14).Select(Hud.PartName)];
        foreach (string text in label)
            foreach (var g in _fonts.Shape(UiFace.Label, text).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{text[(int)g.Cluster]}» в «{text}» (Label)");
        foreach (string text in (string[])["HP 62", "7 / 10", "61 > 27 — отрубил бы", "20 ≤ 27 — нет", "рубящий 61, укол 62", "27, 18, 25, 26, 37, 75"])
            foreach (var g in _fonts.Shape(UiFace.Num, text).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{text[(int)g.Cluster]}» в «{text}» (Num)");
        foreach (var g in _fonts.Shape(UiFace.Head, "ПАЛ").ToArray()) Assert.True(g.GlyphId != 0);
    }

    [Fact]
    public void CombatHudFrame_AllocatesNothing()
    {
        var hud = new Hud();
        var d = new UiDrawList(_fonts);
        var help = Hud.HelpLines(new Dictionary<string, string>());
        for (int i = 0; i < 5; i++)
        {
            d.Clear();
            hud.Draw(d, 1600, 900, Data with { Combat = Alive with { PlayerDead = i % 2 == 0 } }, help);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            d.Clear();
            hud.Draw(d, 1600, 900, Data with { Combat = Alive with { PlayerHp = 100 - i, Kills = i, LastDamage = i * 1.5f, PlayerDead = i % 2 == 0 } }, help);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void InventoryPanel_RowPerWeapon_ClickTakesWeapon_MouseOverPanel_NoAllocations()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
        var link = new SimulationLink(sim);
        var panel = new InventoryPanel();
        panel.Attach(link, ["katana", "sword_1h"]);
        var ui = new UiContext(_fonts);
        var input = new UiInput { MouseInside = true, Mouse = new Vector2(10, 10) };
        bool Frame()
        {
            ui.BeginFrame(input, 1280, 720, 1);
            bool mouse = panel.Draw(ui);
            ui.EndFrame();
            input.BeginFrame();
            input.Time += 1 / 60.0;
            return mouse;
        }

        Assert.False(panel.Update(default));   // закрыта — не видна и не рисуется
        Assert.False(Frame());
        Assert.Equal(0, ui.Output.Count);
        panel.Open = true;
        Assert.True(panel.Update(default));
        Assert.False(Frame());   // мышь не над панелью
        var rect = panel.LastRect;
        Assert.Equal((1280 - 16f, 340f), (rect.X1, rect.Width));   // справа, ширина 340
        Assert.Equal(22 + 12 + 24 + 26 * 2, rect.Height, 3);        // две строки — два меча
        // Строка 2 — sword_1h (выбран); щелчок по строке 1 — katana.
        float row0 = rect.Y0 + 8 + 22 + 6 + 13;
        input.Mouse = new Vector2(rect.Centre.X, row0);
        input.SetMouseButton(0, down: true, pressed: true, released: false);
        Assert.True(Frame());   // курсор над панелью — мышь в бой не уходит
        Assert.Equal(0, panel.TakeClicked());
        Assert.Equal(-1, panel.TakeClicked());   // забрал первый тик — больше нет
        input.SetMouseButton(0, down: false, pressed: false, released: true);
        Frame();

        for (int i = 0; i < 5; i++) Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void TuningPanel_CombatTab_SliderSetsKnobCommand_PendingShown_NoAllocations()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
        var link = new SimulationLink(sim);
        var binding = new TuningBinding(link);
        var panel = new TuningPanel { Open = true };
        panel.Groups.Add(CombatTuning.Build(binding, link));
        var ui = new UiContext(_fonts);
        var input = new UiInput { MouseInside = true, Mouse = new Vector2(600, 300) };
        bool Frame()
        {
            ui.BeginFrame(input, 640, 600, 1);
            bool mouse = panel.Draw(ui, 0);
            ui.EndFrame();
            input.BeginFrame();
            input.Time += 1 / 60.0;
            return mouse;
        }

        Frame();
        // «урон» — первая строка после заголовка (y 46 + 22 + 12), ползунок x 156…276: протяжка к правому краю — к пределу 5.
        const float y = 46 + 22 + 12;
        input.Mouse = new Vector2(200, y);
        input.SetMouseButton(0, down: true, pressed: true, released: false);
        Assert.True(Frame());
        input.SetMouseButton(0, down: true, pressed: false, released: false);
        for (int i = 0; i <= 10; i++)
        {
            input.Mouse = new Vector2(200 + i * 10, y);
            Frame();
        }
        input.SetMouseButton(0, down: false, pressed: false, released: true);
        Frame();
        // Пауза: тиков нет — в мире прежнее, панель показывает поставленное (ползунок не отскакивает).
        Assert.Equal(2f, sim.World.Resource<CombatRules>().PlayerDamage);
        Assert.Equal(5f, binding.Value(CombatKnobs.PlayerDamage));
        for (int i = 0; i < 5; i++) Frame();
        Assert.Equal(5f, ((TuningFloat)panel.Groups[0].Find("playerDamage")!).Get());
        sim.Step();
        Assert.Equal(5f, sim.World.Resource<CombatRules>().PlayerDamage);
        Assert.False(binding.IsPending(CombatKnobs.PlayerDamage));

        input.Mouse = new Vector2(600, 300);
        for (int i = 0; i < 5; i++) Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

/// <summary>
/// Без видеокарты и шрифтов: справка F1, тексты HUD боя, кольцо игрока, инвентарь (1–9 и щелчок — команда «взять меч», в
/// реплее), ручки «Бой» (команда в том же тике, реплей — те же хеши, сохранение в <c>sword.user.json</c>).
/// </summary>
public class CombatUiTests
{
    private static Simulation Create(params string[] sets) => TestAssets.Simulation(ConfigSet.Empty(sets));

    private static readonly ReplayInfo Info = new("test", "tests", "none");

    private static TuningCommand Tune(string knob, float value) => new() { Knob = TuningKey.Of(knob), Value = value };

    // ── справка и тексты HUD ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Help_NamesEveryCombatKey()
    {
        var bindings = InputBindings.Resolve([.. ArenaInput.All, .. ClientHost.Actions, new InputAction(Hud.TuningAction, "F3")],
            ConfigSet.Empty("input.inventory=Tab"));
        var lines = Hud.HelpLines(bindings.Keys);
        Assert.Equal(6, lines.Count);
        Assert.Contains("ЛКМ — удар", lines[4]);
        Assert.Contains("Q — укол", lines[4]);
        Assert.Contains("Пробел — рывок", lines[4]);
        Assert.Contains("T — следующее оружие", lines[3]);
        Assert.Equal("Tab — инвентарь (1–9, щелчок — взять меч)    F3 — ручки (бой, рэгдолл)", lines[5]);
        // Без панели ручек (сборка без DevTools) — F3 в справке нет.
        Assert.Equal("I — инвентарь (1–9, щелчок — взять меч)", Hud.HelpLines(new Dictionary<string, string>())[5]);
        Assert.Equal(("1", "Пробел", "ЛКМ", "Tab"), (Hud.Show("D1"), Hud.Show("Space"), Hud.Show("MouseLeft"), Hud.Show("Tab")));
    }

    [Fact]
    public void Texts_LastHit_Weapon_Fallen_PartNames_RingColour()
    {
        Span<char> buf = stackalloc char[96];
        var hit = new CombatHud(62, false, 3, 5, 2, 7, BodySegs.FArmL, 60.8f, "katana");
        Assert.Equal("предплечье 61", Hud.LastHitText(new Line(buf), hit).ToString());
        Assert.True(Hud.LastHitText(new Line(buf), hit with { LastPart = -1 }).IsEmpty);   // удара не было (и после R)
        Assert.Equal("оружие: katana    I — инвентарь", Hud.WeaponText(new Line(buf), "katana", "I").ToString());
        Assert.Equal("убито 4  ·  R — снова в бой", Hud.FallenText(new Line(buf), 4, "R").ToString());
        Assert.Equal(["голова", "торс", "таз", "плечо", "предплечье", "бедро", "голень", "стопа", "клинок"],
            new[] { BodySegs.Head, BodySegs.Torso, BodySegs.Pelvis, BodySegs.UArmR, BodySegs.FArmR, BodySegs.ThighL, BodySegs.ShinR, BodySegs.FootL, BodySegs.Weapon }
                .Select(Hud.PartName));
        Assert.Equal((1f, 0.5f, 0f, 1f), (ArenaClient.PlayerHpFrac(100), ArenaClient.PlayerHpFrac(50), ArenaClient.PlayerHpFrac(-3), ArenaClient.PlayerHpFrac(130)));
    }

    // ── инвентарь ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Кадр с удержанием действий (в первом кадре удержания они ещё и нажаты) и тиками кадра.</summary>
    private static void Frame(SimulationClock clock, InputEdges edges, InputBindings bindings, params InputAction[] pressed)
    {
        edges.Note(new FrameInput(new InputState(), bindings, pressed.Select(a => a.Name).ToHashSet(), heldPressed: pressed.Length > 0,
            true, 640, 480, 1f / 60));
        clock.RunFrame(0);
    }

    [Fact]
    public void Inventory_DigitsWhenOpen_AfterT_EquipCommand_RecordedAndReplayed()
    {
        var bindings = InputBindings.Resolve(ArenaInput.All, null);
        var edges = new InputEdges(bindings);
        var client = new ArenaClient(scripted: new PlayerCommand());
        var sim = Create("arena.enemies=1");
        var link = new SimulationLink(sim);
        client.Inventory.Attach(link, ["katana", "sword_1h"]);
        var recorder = sim.Record(Info);
        var clock = new SimulationClock(sim, (l, input) => client.BeforeTick(l, input), ticksPerFrame: 1, edges);
        int Equipped() => sim.World.Resource<PlayerInventory>().Equipped;
        Assert.Equal(1, Equipped());   // меч по умолчанию — sword_1h

        Frame(clock, edges, bindings, ArenaInput.Weapons[0]);   // инвентарь закрыт — цифры не действуют
        Assert.Equal(1, Equipped());
        client.Inventory.Open = true;
        Frame(clock, edges, bindings, ArenaInput.Weapons[0]);   // 1 — первый по порядку получения (katana)
        Assert.Equal(0, Equipped());
        Assert.Equal(0, sim.World.Get<WeaponSlot>(GameCore.Player(sim.World)).Index);
        Frame(clock, edges, bindings, ArenaInput.Weapons[8]);   // 9 — такого меча нет: ничего
        Assert.Equal(0, Equipped());
        // T и 1 в одном кадре: сначала T (katana → sword_1h), затем 1 (→ katana).
        Frame(clock, edges, bindings, ArenaInput.NextWeapon, ArenaInput.Weapons[0]);
        Assert.Equal(0, Equipped());
        Frame(clock, edges, bindings);
        Assert.Equal(0, sim.Invariants.Total);

        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        int arenaType = replay.CommandTypes.ToList().FindIndex(t => t.Name == typeof(ArenaCommand).FullName);
        var last = replay.CommandsAt(3).Where(c => c.Type == arenaType).Select(c => ((ArenaAction)c.Leaves[0], (int)c.Leaves[2])).ToList();
        Assert.Equal([(ArenaAction.NextWeapon, 0), (ArenaAction.EquipWeapon, 0)], last);
        var b = TestAssets.Simulation(replay.CreateConfig());
        var player = b.Play(replay);
        while (b.Tick < replay.Ticks) b.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(0, b.World.Resource<PlayerInventory>().Equipped);
    }

    [Fact]
    public void Inventory_ReplayMode_SendsNothing()
    {
        var bindings = InputBindings.Resolve(ArenaInput.All, null);
        var edges = new InputEdges(bindings);
        var client = new ArenaClient(playerCommands: false);
        var sim = Create("arena.enemies=1");
        client.Inventory.Attach(new SimulationLink(sim), ["katana", "sword_1h"]);
        client.Inventory.Open = true;
        var clock = new SimulationClock(sim, (l, input) => client.BeforeTick(l, input), ticksPerFrame: 1, edges);
        Frame(clock, edges, bindings, ArenaInput.Weapons[0]);
        Assert.Equal(1, sim.World.Resource<PlayerInventory>().Equipped);
    }

    [Fact]
    public void OwnedAt_OrderOfAcquisition()
    {
        var inventory = new PlayerInventory { Owned = 0b1011_0100 };
        Assert.Equal([2, 4, 5, 7, -1], Enumerable.Range(0, 5).Select(inventory.OwnedAt));
        Assert.Equal(-1, default(PlayerInventory).OwnedAt(0));
    }

    // ── ручки «Бой» ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Knobs_CombatSection_RangesDefaults_SystemInBegin()
    {
        var sim = Create();
        Assert.Equal([CombatKnobs.PlayerDamage, CombatKnobs.PlayerSharpness, CombatKnobs.EnemiesAttack, CombatKnobs.PlayerInvulnerable,
            CombatKnobs.DashSpeed, CombatKnobs.DashTime, CombatKnobs.DashCooldown, CombatKnobs.DashInvulnerable],
            sim.Tuning.Where(k => k.Section == CombatKnobs.Section).Select(k => k.Name));   // и ручки «Рэгдолл» — своей секцией
        var d = new CombatSettings();
        Assert.Equal(new float[] { d.PlayerDamage, d.PlayerSharpness, 1, 0, d.DashSpeed, d.DashTime, d.DashCooldown, d.DashInvulnerable },
            sim.Tuning.Where(k => k.Section == CombatKnobs.Section).Select(k => k.Default));
        var damage = sim.Knob(CombatKnobs.PlayerDamage)!;
        Assert.Equal((0.2f, 5f, true, "PlayerDamage", typeof(CombatRules)), (damage.Min, damage.Max, damage.Log, damage.Field, damage.Resource));
        Assert.All(sim.Tuning, k => Assert.InRange(k.Default, k.Min, k.Max));
        Assert.True(sim.Knob(CombatKnobs.DashTime)!.Min > 0);   // время рывка — больше 0, как требует конфиг
        var system = sim.World.Systems.Single(s => s.Label == "tuning.combat");
        Assert.Equal((GameStages.Begin, (string?)null), (system.Stage, system.Module));   // ядро: без токена модуля
        Assert.Contains(sim.World.Systems, s => s.Label == GameLabels.CombatRules.Name && s.Stage == GameStages.Intent);
    }

    [Fact]
    public void KnobCommand_ChangesRulesSameTick_ReplayRepeats()
    {
        static (Replay Replay, CombatRules Rules, ulong Hash) Run(bool tune)
        {
            var sim = Create("arena.enemies=3");
            var recorder = sim.Record(Info);
            for (int t = 0; t < 400; t++)
            {
                if (tune && t == 50) sim.Enqueue(Tune(CombatKnobs.PlayerDamage, 4));
                if (tune && t == 120) sim.Enqueue(Tune(CombatKnobs.EnemiesAttack, 0));
                if (tune && t == 200) sim.Enqueue(Tune(CombatKnobs.PlayerInvulnerable, 1));
                if (tune && t == 260) sim.Enqueue(Tune(CombatKnobs.DashSpeed, 30));   // выше предела — 20
                sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1), Attack = t % 90 == 10, Dash = t % 130 == 70 });
                sim.Step();
                if (tune && t == 50) Assert.Equal(4f, sim.World.Resource<CombatRules>().PlayerDamage);   // в том же тике
            }
            Assert.Equal(0, sim.Invariants.Total);
            return (recorder.ToReplay(), sim.World.Resource<CombatRules>(), sim.HashState().Total);
        }

        var (tuned, rules, hash) = Run(tune: true);
        Assert.Equal((4f, false, true, 20f), (rules.PlayerDamage, rules.EnemiesAttack, rules.PlayerInvulnerable, rules.DashSpeed));
        var (_, plain, plainHash) = Run(tune: false);
        Assert.NotEqual(plainHash, hash);
        Assert.Equal(2f, plain.PlayerDamage);

        var replay = Replay.Parse(tuned.ToJson());
        Assert.Contains(replay.CommandTypes, t => t.Name == typeof(TuningCommand).FullName);
        var sim = TestAssets.Simulation(replay.CreateConfig());
        var player = sim.Play(replay);
        while (sim.Tick < replay.Ticks) sim.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(401, player.HashesChecked);
        Assert.Equal(hash, sim.HashState().Total);
    }

    [Fact]
    public void RulesChanged_PlayerTimersClampedToNewRules_NoViolation()
    {
        var sim = Create("arena.enemies=0");
        sim.Enqueue(new PlayerCommand { Dash = true });
        sim.Step();
        var player = GameCore.Player(sim.World);
        var control = sim.World.Get<PlayerControl>(player);
        Assert.True(control.Dashing && control.DashCooldown > 0.5f && control.Invulnerable > 0.25f);
        sim.Enqueue(Tune(CombatKnobs.DashCooldown, 0.1f));
        sim.Enqueue(Tune(CombatKnobs.DashInvulnerable, 0.05f));
        sim.Step();
        control = sim.World.Get<PlayerControl>(player);
        Assert.InRange(control.DashCooldown, 0, 0.1f);
        Assert.InRange(control.Invulnerable, 0, 0.05f);
        for (int t = 0; t < 60; t++) sim.Step();
        Assert.Equal(0, sim.Invariants.Total);   // combat.control: таймеры — в пределах новых правил
    }

    [Fact]
    public void Binding_PendingValueUntilItsTick_ThenWorld_NoDuplicateCommands()
    {
        var sim = Create("arena.enemies=0");
        var recorder = sim.Record(Info);
        var link = new SimulationLink(sim);
        var binding = new TuningBinding(link);
        binding.Set(CombatKnobs.PlayerDamage, 3);
        binding.Set(CombatKnobs.PlayerDamage, 3);   // то же значение — второй команды нет
        // Пауза (тиков нет): панель показывает поставленное, в мире — прежнее.
        Assert.Equal(3f, binding.Value(CombatKnobs.PlayerDamage));
        Assert.True(binding.IsPending(CombatKnobs.PlayerDamage));
        Assert.Equal(2f, sim.World.Resource<CombatRules>().PlayerDamage);
        sim.Step();
        Assert.False(binding.IsPending(CombatKnobs.PlayerDamage));
        Assert.Equal(3f, sim.World.Resource<CombatRules>().PlayerDamage);
        Assert.Equal(3f, binding.Value(CombatKnobs.PlayerDamage));
        binding.Set(CombatKnobs.PlayerDamage, 99);   // прижато к пределу 5
        binding.Set(CombatKnobs.PlayerSharpness, float.NaN);   // нечисло — ничего
        sim.Step();
        Assert.Equal(5f, sim.World.Resource<CombatRules>().PlayerDamage);
        Assert.Equal(2, recorder.ToReplay().CommandCount);
        Assert.Throws<ArgumentException>(() => binding.Value("combat.nope"));
    }

    [Fact]
    public void CombatGroup_ResetSaveLoad_UserFile_ReadByConfigAfterRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sword-combat-tuning", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ConfigSet.FileName), "{ \"arena\": { \"enemies\": 0 } }");
            string file = UserConfig.PathFrom(dir);
            File.WriteAllText(file, "{\r\n  // своё\r\n  \"input\": { \"attack\": \"MouseRight\" },\r\n  \"combat\": { \"playerWeapon\": \"katana\", \"dashTime\": 0.5 }\r\n}\r\n");
            var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
            var link = new SimulationLink(sim);
            var binding = new TuningBinding(link);
            var group = CombatTuning.Build(binding, link);
            Assert.Equal(("combat", "Бой"), (group.Id, group.Title));
            Assert.Equal(["playerDamage", "playerSharpness", "enemiesAttack", "playerInvulnerable", "dashSpeed", "dashTime", "dashCooldown", "dashInvulnerable"],
                group.Values().Keys);

            ((TuningFloat)group.Find("playerDamage")!).Set(3.5f);
            ((TuningBool)group.Find("enemiesAttack")!).Set(false);
            sim.Step();
            Assert.Equal(new Dictionary<string, double> { ["playerDamage"] = 3.5, ["enemiesAttack"] = 0 }, group.Values(changedOnly: true));
            Assert.Equal("сохранено значений: 2 (sword.user.json)", binding.Save(group, file));
            string text = File.ReadAllText(file);
            Assert.Contains("своё", text);
            Assert.Contains("MouseRight", text);
            Assert.Contains("katana", text);
            Assert.DoesNotContain("dashTime", text);   // по умолчанию в игре — ключ убран: файл хранит только отличия
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));

            // «После перезапуска»: конфиг читает файл пользователя — ручки на месте.
            var restarted = ConfigSet.Load(dir, [], _ => null).Section<CombatSettings>("combat");
            Assert.Equal((3.5f, false, "katana"), (restarted.PlayerDamage, restarted.EnemiesAttack, restarted.PlayerWeapon));

            group.ResetAll();   // «сброс» — командами
            sim.Step();
            var rules = sim.World.Resource<CombatRules>();
            Assert.Equal((2f, true), (rules.PlayerDamage, rules.EnemiesAttack));
            Assert.Equal(0, group.Changed());

            ((TuningFloat)group.Find("dashSpeed")!).Set(12);
            sim.Step();
            Assert.StartsWith("загружено значений: 2", binding.Load(group, file));
            sim.Step();
            rules = sim.World.Resource<CombatRules>();
            Assert.Equal((3.5f, false, new CombatSettings().DashSpeed), (rules.PlayerDamage, rules.EnemiesAttack, rules.DashSpeed));   // нет в файле — по умолчанию
            Assert.Equal(0, sim.Invariants.Total);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CombatGroup_InfoRows()
    {
        Span<char> d = stackalloc char[128];
        Assert.Equal("—", d[..CombatTuning.LastStrike(d, ArenaStats.Start)].ToString());
        var stats = new ArenaStats { LastPart = BodySegs.Head, LastDamage = 60.8f, LastSeverAt = 27 };
        Assert.Equal("61 > 27 — отрубил бы", d[..CombatTuning.LastStrike(d, stats)].ToString());
        Assert.Equal("20 ≤ 27 — нет", d[..CombatTuning.LastStrike(d, stats with { LastDamage = 20 })].ToString());
        Assert.Equal("рубящий 61, укол 62", d[..CombatTuning.MaxStrike(d, 2)].ToString());   // 2·(8 + 16·1,4), 2·(12 + 16·1,2)
        Assert.Equal("27, 18, 25, 26, 37, 75", d[..CombatTuning.SeverThresholds(d, 1)].ToString());
        Assert.Equal("14, 9, 13, 13, 19, 38", d[..CombatTuning.SeverThresholds(d, 2)].ToString());
    }
}
