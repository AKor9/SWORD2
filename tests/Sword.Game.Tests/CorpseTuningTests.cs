using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Content;
using Sword.Engine.DevTools;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Formats;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Engine.Ui;
using Sword.Game.Arena;
using Sword.Game.Client;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Группа F3 «Рэгдолл» без окна и шрифтов: ручки секции <c>ragdoll</c> из регистрации, строки трупов кадра, ручка из группы —
/// команда на ближайший тик и в реплее; сброс, сохранение и загрузка <c>sword.user.json</c>.
/// </summary>
public sealed class CorpseTuningTests(ITestOutputHelper output)
{
    /// <summary>Модуль теста: берёт физику симуляции после сборки мира.</summary>
    private sealed class Probe(Action<IPhysicsWorld> take) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
    }

    private static (Simulation Sim, IPhysicsWorld Physics) Create(params string[] sets) => Create(ConfigSet.Empty(sets));

    private static (Simulation Sim, IPhysicsWorld Physics) Create(ConfigSet config)
    {
        IPhysicsWorld? physics = null;
        var sim = Simulation.Build(config, GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe(p => physics = p)]);
        return (sim, physics!);
    }

    private static SceneCommand Kill(int slot) => new() { Action = SceneAction.Kill, Slot = slot };

    /// <summary>Порядок ручек: «труп на земле», «сон трупа», «потолки трупа», «переход в труп», «толчок живого».</summary>
    private static readonly string[] Keys =
        ["friction", "pairFriction", "popSpeed", "jointCompliance", "jointFriction", "selfCollision", "jointPopSpeed", "sleepSpeed", "sleepBodySpeed",
            "sleepTime", "corpseMaxSpeed", "corpseMaxAngularSpeed", "deathMaxSpeed", "deathMaxAngularSpeed", "pushForce", "enemyRecoil", "playerRecoil",
            "recoilMaxSpeed"];

    /// <summary>
    /// Id группы — секция <c>ragdoll</c>; каждая строка — подпись, подсказка, пределы, шкала, формат и умолчание ручки
    /// (<see cref="RagdollKnobs"/>); разделы — заголовками, строки трупов кадра — первыми.
    /// </summary>
    [Fact]
    public void RagdollGroup_AllKnobsOfSection_FromRegistration()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
        var link = new SimulationLink(sim);
        var group = RagdollTuning.Build(new TuningBinding(link), new ArenaView(ArenaTerrain.Height));
        Assert.Equal(("ragdoll", "Рэгдолл", RagdollKnobs.Section), (group.Id, group.Title, RagdollTuning.Id));
        Assert.Equal(Keys, group.Values().Keys);
        var knobs = sim.Tuning.Where(k => k.Section == RagdollKnobs.Section).ToList();
        Assert.Equal(18, knobs.Count);
        Assert.Equal(knobs.Select(k => k.Key).Order(), Keys.Order());
        foreach (var k in knobs)
        {
            var row = Assert.IsType<TuningFloat>(group.Find(k.Key));
            Assert.Equal((k.Label, k.Hint, k.Min, k.Max, k.Log, k.Format, k.Default), (row.Label, row.Hint, row.Min, row.Max, row.Log, row.Format, row.Default));
            Assert.Equal(k.Default, row.Get());   // в мире — значение по умолчанию
        }
        Assert.True(group.Find("popSpeed") is TuningFloat { Log: true } && group.Find("jointCompliance") is TuningFloat { Log: true }
            && group.Find("jointPopSpeed") is TuningFloat { Log: true } && group.Find("sleepTime") is TuningFloat { Log: true });
        Assert.Equal(["ТРУПЫ В КАДРЕ", "ТРУП НА ЗЕМЛЕ", "СОН ТРУПА", "ПОТОЛКИ ТРУПА", "ПЕРЕХОД В ТРУП", "ТОЛЧОК ЖИВОГО"],
            group.Items.OfType<TuningHeader>().Select(h => h.Label));
        Assert.Equal(["трупов", "тел на физике", "быстрейшее тело"], group.Items.OfType<TuningInfo>().Select(i => i.Label));
        Assert.IsType<TuningInfo>(group.Items[1]);   // сразу под первым заголовком
        Assert.All(group.Items.OfType<TuningInfo>(), i => Assert.False(string.IsNullOrEmpty(i.Hint)));
    }

    /// <summary>Строки трупов кадра — тексты на стеке.</summary>
    [Fact]
    public void RagdollGroup_InfoTexts()
    {
        Span<char> d = stackalloc char[TuningPanel.InfoCapacity];
        var none = new CorpseFrameStats();
        Assert.Equal(("0", "0", "—"), (d[..RagdollTuning.Corpses(d, none)].ToString(), d[..RagdollTuning.Bodies(d, none)].ToString(),
            d[..RagdollTuning.Fastest(d, none)].ToString()));
        var s = new CorpseFrameStats(3, 2, 42, 17, 0.4213f, 1.26f, 1);
        Assert.Equal("3, в покое 2, спят 1", d[..RagdollTuning.Corpses(d, s)].ToString());   // спящие
        Assert.Equal("42, на земле 17", d[..RagdollTuning.Bodies(d, s)].ToString());
        Assert.Equal("0.42 м/с, 1.3 рад/с", d[..RagdollTuning.Fastest(d, s)].ToString());
    }

    /// <summary>
    /// Ручка из группы (как ползунок): ожидающее значение до тика, команда на ближайший тик, в том же тике — правила, физика и
    /// профиль трупа (потолок прижат к 40). Реплей с командами ручек воспроизводит те же хеши; без ручки итог другой.
    /// </summary>
    [Fact]
    public void RagdollGroup_KnobFromPanel_CommandInReplay_ReplayRepeats()
    {
        static (Replay Replay, ulong Hash, ulong KnobTick) Run(bool tune, ITestOutputHelper? output)
        {
            var (sim, physics) = Create("arena.enemies=3", "combat.enemiesAttack=false");
            var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
            var pending = new List<SceneCommand>();
            var clock = new SimulationClock(sim, (link, _) =>
            {
                foreach (var c in pending) link.Enqueue(c);
                pending.Clear();
            }, ticksPerFrame: 1);
            var view = new ArenaView(ArenaTerrain.Height);
            var binding = new TuningBinding(clock.Link);
            var group = RagdollTuning.Build(binding, view);
            void Frames(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    clock.RunFrame(0);
                    view.Extract(clock.Link, 1, allBodies: false);
                }
            }

            Frames(10);
            pending.AddRange([Kill(1), Kill(2), Kill(3)]);
            Frames(40);
            ulong knobTick = sim.Tick;
            if (tune)
            {
                ((TuningFloat)group.Find("friction")!).Set(1.6f);
                ((TuningFloat)group.Find("corpseMaxSpeed")!).Set(99);
                // Пауза: тиков нет — в мире прежнее, группа показывает поставленное.
                Assert.Equal(0.7f, sim.World.Resource<RagdollRules>().Friction);
                Assert.Equal((1.6f, 40f), (((TuningFloat)group.Find("friction")!).Get(), ((TuningFloat)group.Find("corpseMaxSpeed")!).Get()));
                Assert.True(binding.IsPending(RagdollKnobs.Friction));
                Frames(1);
                Assert.False(binding.IsPending(RagdollKnobs.Friction));
                var rules = sim.World.Resource<RagdollRules>();
                Assert.Equal((1.6f, 40f), (rules.Friction, rules.CorpseMaxSpeed));
                Assert.Equal((1.6f, 40f), (physics.Settings.StaticFriction, physics.GetDynamics(1).MaxSpeed));   // шаг этого тика — по новым
            }
            Frames(300);
            var stats = view.CorpseStats;
            output?.WriteLine($"трупы кадра: {stats}");
            Assert.Equal((3, 3 * BodySegs.Bodies), (stats.Corpses, stats.SimulatedBodies));
            Assert.InRange(stats.GroundedBodies, 1, 3 * BodySegs.Bodies);
            Assert.InRange(stats.AtRest, 0, 3);
            Assert.True(sim.Invariants.Total == 0, string.Join("; ", sim.Invariants.Recent().Take(3).Select(v => v.ToString())));
            return (Replay.Parse(recorder.ToReplay().ToJson()), sim.HashState().Total, knobTick);
        }

        var (replay, hash, tick) = Run(tune: true, output);
        var (_, plain, _) = Run(tune: false, null);
        Assert.NotEqual(plain, hash);   // ручка изменила падение

        int type = replay.CommandTypes.ToList().FindIndex(t => t.Name == typeof(TuningCommand).FullName);
        Assert.True(type >= 0, "команды ручек нет в реплее");
        var knobs = replay.CommandsAt(tick).Where(c => c.Type == type).Select(c => c.Leaves[0]).ToList();
        Assert.Equal([TuningKey.Of(RagdollKnobs.Friction).Hash, TuningKey.Of(RagdollKnobs.CorpseMaxSpeed).Hash], knobs);
        Assert.Equal("ragdoll.friction", new TuningKey(knobs[0]).Name);

        var (again, _) = Create(replay.CreateConfig());
        var player = again.Play(replay);
        while (again.Tick < replay.Ticks) again.Step();
        Assert.Null(player.Divergence);
        Assert.Equal((int)replay.Ticks + 1, player.HashesChecked);
        Assert.Equal(hash, again.HashState().Total);
        Assert.Equal(1.6f, again.World.Resource<RagdollRules>().Friction);
    }

    /// <summary>
    /// Сохранено — только отличия от умолчаний в секции <c>ragdoll</c> (прочее в файле на месте, CRLF); после перезапуска конфиг
    /// читает её; «сброс» — к значениям в коде командами; «загрузить» — из файла или умолчание.
    /// </summary>
    [Fact]
    public void RagdollGroup_ResetSaveLoad_UserFile_ReadByConfigAfterRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sword-ragdoll-tuning", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ConfigSet.FileName), "{ \"arena\": { \"enemies\": 0 } }");
            string file = UserConfig.PathFrom(dir);
            File.WriteAllText(file, "{\r\n  // своё\r\n  \"combat\": { \"playerDamage\": 3 },\r\n  \"ragdoll\": { \"deathMaxSpeed\": 2 }\r\n}\r\n");
            var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
            var link = new SimulationLink(sim);
            var binding = new TuningBinding(link);
            var group = RagdollTuning.Build(binding, new ArenaView(ArenaTerrain.Height));

            ((TuningFloat)group.Find("friction")!).Set(1.25f);
            ((TuningFloat)group.Find("jointCompliance")!).Set(1e-3f);
            sim.Step();
            Assert.Equal(new Dictionary<string, double> { ["friction"] = 1.25, ["jointCompliance"] = 0.001 }, group.Values(changedOnly: true));
            Assert.Equal("сохранено значений: 2 (sword.user.json)", binding.Save(group, file));
            string text = File.ReadAllText(file);
            Assert.Contains("своё", text);
            Assert.Contains("playerDamage", text);
            Assert.DoesNotContain("deathMaxSpeed", text);   // по умолчанию в игре — ключ убран: файл хранит только отличия
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));

            // «После перезапуска»: конфиг читает файл пользователя — правила трупа те же.
            var restarted = RagdollRules.From(ConfigSet.Load(dir, [], _ => null).Section<RagdollSettings>(RagdollKnobs.Section));
            Assert.Equal((1.25f, 1e-3f, new RagdollSettings().DeathMaxSpeed), (restarted.Friction, restarted.JointCompliance, restarted.DeathMaxSpeed));

            group.ResetAll();   // «сброс» — командами
            sim.Step();
            var rules = sim.World.Resource<RagdollRules>();
            var d = new RagdollSettings();
            Assert.Equal((d.Friction, d.JointCompliance), (rules.Friction, rules.JointCompliance));
            Assert.Equal(0, group.Changed());

            ((TuningFloat)group.Find("pushForce")!).Set(2000);
            sim.Step();
            Assert.StartsWith("загружено значений: 2", binding.Load(group, file));
            sim.Step();
            rules = sim.World.Resource<RagdollRules>();
            Assert.Equal((1.25f, 1e-3f, d.PushForce), (rules.Friction, rules.JointCompliance, rules.PushForce));   // нет в файле — по умолчанию
            Assert.Equal(0, sim.Invariants.Total);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// На шрифтах пака (категория Content): вкладка «Рэгдолл» панели F3 — тексты в шрифтах, ползунок ставит команду ручки, кадр без
/// выделений.
/// </summary>
[Trait("Category", "Content")]
public sealed class CorpseTuningDrawTests : IDisposable
{
    private readonly UiFonts _fonts;
    private readonly ITestOutputHelper _output;

    public CorpseTuningDrawTests(ITestOutputHelper output)
    {
        _output = output;
        using var pak = ContentLocator.Open();
        var files = new List<UiFontFile>();
        foreach (var f in pak.List(UiVectorFontFormat.Dir, UiVectorFontFormat.Extension))
            using (var s = pak.OpenRead(f))
                files.Add(UiFontFile.Read(s, pak.Describe(f)));
        _fonts = new UiFonts(files);
    }

    public void Dispose() => _fonts.Dispose();

    [Fact]
    public void RagdollTexts_HaveEveryGlyph()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
        var group = RagdollTuning.Build(new TuningBinding(new SimulationLink(sim)), new ArenaView(ArenaTerrain.Height));
        Span<char> d = stackalloc char[TuningPanel.InfoCapacity];
        var s = new CorpseFrameStats(3, 2, 42, 17, 0.42f, 1.3f);
        void Check(UiFace face, string text)
        {
            foreach (var g in _fonts.Shape(face, text).ToArray())
                Assert.True(g.GlyphId != 0, $"нет glyph'а для «{text[(int)g.Cluster]}» в «{text}» ({face})");
        }
        Check(UiFace.Body, group.Title);
        foreach (var item in group.Items)
        {
            Check(item is TuningHeader ? UiFace.Label : UiFace.Body, item.Label);
            if (item.Hint is { } hint) Check(UiFace.Label, hint);
            if (item is TuningFloat f) Check(UiFace.Num, f.Max.ToString(f.Format, System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (string text in (string[])[d[..RagdollTuning.Corpses(d, s)].ToString(), d[..RagdollTuning.Bodies(d, s)].ToString(),
            d[..RagdollTuning.Fastest(d, s)].ToString(), d[..RagdollTuning.Fastest(d, default)].ToString()])
            Check(UiFace.Num, text);
    }

    /// <summary>
    /// Подпись каждой строки вкладок «Рэгдолл» и «Бой» (кегль <c>FontSize</c>) влезает в наибольший столбец подписей панели F3
    /// (<see cref="TuningPanel.MaxLabelShare"/> строки) при полосе прокрутки; полный смысл — в подсказке. Замер — в выводе.
    /// </summary>
    [Fact]
    public void KnobLabels_FitLabelColumn_WithScrollbar()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
        var link = new SimulationLink(sim);
        var binding = new TuningBinding(link);
        var groups = new[] { RagdollTuning.Build(binding, new ArenaView(ArenaTerrain.Height)), CombatTuning.Build(binding, link) };
        var draw = new UiDrawList(_fonts);
        var theme = UiTheme.Default;
        float room = (TuningPanel.Width - 2 * 10 - TuningPanel.MenuWidth - 12 - theme.ScrollbarWidth - 3) * TuningPanel.MaxLabelShare - 10;   // 12 — разделитель меню, 10 — зазор до ползунка
        float widest = 0;
        int rows = 0;
        foreach (var group in groups)
            foreach (var item in group.Items.Where(i => i is TuningFloat or TuningInfo))
            {
                float w = draw.Measure(UiFace.Body, theme.FontSize, item.Label);
                widest = MathF.Max(widest, w);
                rows++;
                _output.WriteLine($"{group.Title}: {item.Label} — {w:F1} из {room:F1} px");
                Assert.True(w <= room, $"{group.Title}: подпись «{item.Label}» — {w:F1} px, в строке {room:F1} px (панель режет «…»)");
            }
        Assert.Equal(18, groups[0].Items.OfType<TuningFloat>().Count());
        _output.WriteLine($"строк {rows}, самая широкая подпись — {widest:F1} из {room:F1} px");
    }

    /// <summary>
    /// У всех числовых ручек «Рэгдолл» и «Бой» умолчание, пределы и 25 значений логарифмической шкалы влезают в поле числа панели F3;
    /// малые значения — с порядком: «2e-6», «1e-8». Замер — в выводе.
    /// </summary>
    [Fact]
    public void KnobValues_FitNumberField_SmallValuesWithExponent()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=0"));
        var draw = new UiDrawList(_fonts);
        float room = TuningPanel.ValueWidth - 2 * UiContext.FieldPadding;
        var knobs = sim.Tuning.Where(k => k.Section is RagdollKnobs.Section or CombatKnobs.Section && k.Kind == TuningKind.Float).ToList();
        Assert.Equal(18, knobs.Count(k => k.Section == RagdollKnobs.Section));
        Assert.Contains(knobs, k => k.Section == CombatKnobs.Section);
        float widest = 0;
        foreach (var k in knobs)
        {
            var values = new List<float> { k.Default, k.Min, k.Max };
            if (k.Log && k.Min > 0)
                for (int i = 0; i <= 24; i++) values.Add(k.Min * MathF.Pow(k.Max / k.Min, i / 24f));
            foreach (float v in values)
            {
                string text = k.Text(v);
                float w = draw.Measure(UiFace.Body, UiTheme.Default.FontSize, text);
                widest = MathF.Max(widest, w);
                Assert.True(w <= room, $"{k.Name}: «{text}» ({v}) — {w:F1} px, в поле {room} px");
            }
            _output.WriteLine($"{k.Name}: по умолчанию «{k.Text(k.Default)}», пределы «{k.Text(k.Min)}» … «{k.Text(k.Max)}»");
        }
        _output.WriteLine($"самое широкое значение — {widest:F1} из {room} px");
        var compliance = sim.Knob(RagdollKnobs.JointCompliance)!;
        Assert.Equal(("2e-6", "1e-8", "1e-2", "2e-4"), (compliance.Text(2e-6f), compliance.Text(compliance.Min), compliance.Text(compliance.Max), compliance.Text(2e-4f)));
    }

    [Fact]
    public void TuningPanel_RagdollTab_SliderSetsKnobCommand_PendingShown_InfoRows_NoAllocations()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=2", "combat.enemiesAttack=false"));
        var link = new SimulationLink(sim);
        var binding = new TuningBinding(link);
        var view = new ArenaView(ArenaTerrain.Height);
        var panel = new TuningPanel { Open = true };
        panel.Groups.Add(RagdollTuning.Build(binding, view));
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

        // Трупы в кадре: два врага убиты, падают.
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 2 });
        for (int t = 0; t < 30; t++) sim.Step();
        view.Extract(link, 1, allBodies: false);
        Assert.Equal((2, 2 * BodySegs.Bodies), (view.CorpseStats.Corpses, view.CorpseStats.SimulatedBodies));

        Frame();
        // «трение о землю» — первая строка раздела «труп на земле»: заголовок, три строки трупов, заголовок (y 16 + 22 + 3·24 + 22 + 12),
        // ручки справа от меню, ползунок — от столбца подписей до поля числа (x 450): протяжка за правый край — к пределу 2.
        const float y = 16 + 22 + 3 * 24 + 22 + 12;
        input.Mouse = new Vector2(350, y);
        input.SetMouseButton(0, down: true, pressed: true, released: false);
        Assert.True(Frame());
        input.SetMouseButton(0, down: true, pressed: false, released: false);
        for (int i = 0; i <= 10; i++)
        {
            input.Mouse = new Vector2(350 + i * 20, y);
            Frame();
        }
        input.SetMouseButton(0, down: false, pressed: false, released: true);
        Frame();
        // Пауза: тиков нет — в мире прежнее, панель показывает поставленное (ползунок не отскакивает).
        Assert.Equal(0.7f, sim.World.Resource<RagdollRules>().Friction);
        Assert.Equal(2f, binding.Value(RagdollKnobs.Friction));
        sim.Step();
        Assert.Equal(2f, sim.World.Resource<RagdollRules>().Friction);
        Assert.False(binding.IsPending(RagdollKnobs.Friction));

        input.Mouse = new Vector2(600, 300);
        for (int i = 0; i < 5; i++) Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
