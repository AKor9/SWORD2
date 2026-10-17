using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Blood;
using Sword.Game.Client;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Сервисы крови и скелет модуля <c>blood</c> без окна и GPU (контент синтетический, <see cref="TestAssets"/>): каждое попадание видно в
/// наблюдателе тиков ровно раз со своим тиком при 1, 3 и 8 тиках за кадр; точка попадания в осях тела сегмента того же тика возвращается
/// сама в себя; позы тика — вход палитры скина кадра; рельеф — <see cref="ArenaTerrain"/> бит в бит; сброс R, смерти и постройки; модуль
/// выключен — не настраивается; кадр модуля и сервисов — 0 Б.
/// </summary>
public sealed class BloodModuleTests
{
    private sealed class Scene
    {
        public readonly Simulation Sim;
        public readonly ClientModules Modules;
        public readonly SimulationClock Clock;
        public readonly BloodModule Blood = new();
        public readonly ArenaBloodFeed Feed;
        public readonly ArenaFighterPoses Poses;
        public readonly List<SceneCommand> Pending = [];
        public readonly List<ArenaCommand> PendingArena = [];
        public readonly List<StrikeProbe> PendingProbes = [];

        /// <summary>Команда игрока на каждый тик (null — без команды).</summary>
        public Func<ulong, PlayerCommand>? Command;
        public readonly ConfigSet Config;

        /// <summary>Мир представления кадра клиента арены — снимается каждым кадром (бойцы приходят очередью кадра).</summary>
        public readonly ArenaView View = new(ArenaTerrain.Height);

        /// <summary>Своя проверка в наблюдателе тиков — после наблюдателей модулей.</summary>
        public Action<ulong>? AfterTick;

        public Scene(int ticksPerFrame, params string[] sets) : this(ticksPerFrame, null, sets)
        {
        }

        /// <param name="assets">Ассеты бойца; null — синтетические теста (в них клинок врага не попадает — только пробы).</param>
        public Scene(int ticksPerFrame, CharacterAssets? assets, params string[] sets)
        {
            Config = ConfigSet.Empty(["arena.enemies=3", .. sets]);
            Sim = assets is null ? TestAssets.Simulation(Config) : Sword.Game.Bootstrap.SwordSimulation.Create(Config, assets);
            ArenaBloodFeed? feed = null;
            ArenaFighterPoses? poses = null;
            var services = new ClientServices()
                .Add<IBloodFeed>(link => feed = new ArenaBloodFeed(link))
                .Add<IFighterPoses>(link => poses = new ArenaFighterPoses(link))
                .Add<ITerrainView>(new ArenaTerrainView());
            Modules = ClientModules.Compose(Config, Sim, [Blood], services);
            Feed = feed!;
            Poses = poses!;
            Clock = new SimulationClock(Sim, (link, _) =>
            {
                foreach (var c in Pending) link.Enqueue(c);
                foreach (var c in PendingArena) link.Enqueue(c);
                foreach (var c in PendingProbes) link.Enqueue(c);
                if (Command is { } command) link.Enqueue(command(link.Tick));
                Pending.Clear();
                PendingArena.Clear();
                PendingProbes.Clear();
            }, ticksPerFrame, afterTick: tick =>
            {
                Modules.AfterTick(tick);
                AfterTick?.Invoke(tick);
            });
        }

        public int Frame(float alpha = 1)
        {
            Modules.BeginFrame(Sim.Tick);
            int ticks = Clock.RunFrame(0);
            Modules.Extract(new FrameView(alpha, ticks, 0, default));
            View.Extract(Clock.Link, alpha, allBodies: false);
            return ticks;
        }

        public void Frames(int n)
        {
            for (int i = 0; i < n; i++) Frame();
        }

        public Entity Slot(int slot)
        {
            foreach (var chunk in Sim.World.Iterate(Sim.World.Query().With<Fighter>().Read<FighterSlot>()))
                for (int i = 0; i < chunk.Count; i++)
                    if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
            throw new InvalidOperationException($"нет бойца места {slot}");
        }
    }

    /// <summary>Пробы удара по врагам — несколько в одном тике (две по одному врагу, одна по другому), раз в 10 кадров, иногда смертельная.</summary>
    private static void Probes(Scene scene, int frame)
    {
        if (frame % 10 != 5) return;
        scene.PendingProbes.Add(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.Torso, Damage = 3, Kind = HitKind.Slash });
        scene.PendingProbes.Add(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.Head, Damage = 3, Kind = HitKind.Thrust });
        scene.PendingProbes.Add(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.ThighL, Damage = frame % 60 == 5 ? 500 : 4 });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void EachHit_SeenOnce_WithItsTick(int ticksPerFrame)
    {
        var scene = new Scene(ticksPerFrame);
        var seen = new List<(ulong Tick, ulong Victim, uint Contact, Vector3 Blade, float Speed)>();
        scene.AfterTick = tick =>
        {
            Assert.Equal(tick, scene.Feed.Tick);
            foreach (var h in scene.Feed.Hits)
            {
                Assert.Equal(tick, h.Tick);
                seen.Add((h.Tick, h.Fighter, h.ContactIndex, h.Blade, h.Speed));
            }
        };
        var truth = new List<(ulong Tick, ulong Victim, uint Contact, Vector3 Blade, float Speed)>();
        int frames = 960 / ticksPerFrame;
        for (int f = 0; f < frames; f++)
        {
            Probes(scene, f);
            scene.Frame();
            // Правда — очередь кадра целиком (все тики кадра) и касания тех же тиков.
            var contacts = scene.Clock.Link.Frame<Event<StrikeContact>>();
            foreach (var (tick, record) in scene.Clock.Link.Frame<Event<Hit>>())
            {
                var hit = record.Value;
                var contact = Assert.Single(contacts.ToArray(), c => c.Tick == tick && c.Record.Value.Id == hit.Contact).Record.Value;
                truth.Add((tick, FighterKey.Of(hit.Victim), (uint)hit.Contact.Index, contact.Blade, contact.Speed));
            }
        }
        Assert.True(truth.Count >= 20, $"попаданий {truth.Count} — мало для проверки");
        Assert.Contains(truth, t => truth.Count(u => u.Tick == t.Tick) > 1);   // несколько попаданий в одном тике
        Assert.Equal(truth, seen);
        Assert.Equal(truth.Count, scene.Blood.Hits);
        Assert.Equal(960, scene.Blood.ObservedTicks);
    }

    /// <summary>Ассеты бойца из пака игры — бой клинками (синтетические клипы не попадают).</summary>
    private static readonly Lazy<CharacterAssets> Pak = new(CharacterAssets.LoadFromPak);

    [Fact]
    [Trait("Category", "Content")]
    public void HitPoint_InSegmentAxesOfSameTick_RoundTrips()
    {
        // Игрок рубит без перерыва, взгляд поворачивается на 45° каждые 300 тиков (сценарий crowd); враги бьют его — касания клинком.
        // без расчленения: якорь сверяется с капсулами тела, а у культи и куска они другие
        var scene = new Scene(ticksPerFrame: 3, Pak.Value, "arena.enemies=8", "combat.playerInvulnerable=true", "modules.cutting.enabled=false")
        {
            Command = tick =>
            {
                float angle = tick / 300 * MathF.PI / 4;
                return new PlayerCommand { Attack = true, HasGaze = true, Gaze = new Vector3(3 * MathF.Cos(angle), 0, 3 * MathF.Sin(angle)) };
            },
        };
        int checkedHits = 0, bladeHits = 0;
        float worst = 0, surface = 0;
        scene.AfterTick = tick =>
        {
            Assert.Equal(tick, scene.Poses.Tick);
            foreach (var h in scene.Feed.Hits)
            {
                int index = scene.Poses.Find(h.Fighter);
                Assert.True(index >= 0);
                var body = scene.Poses.Segments(index)[h.Segment];
                Assert.Equal(h.Segment, body.Segment);
                Assert.True(Matrix4x4.Invert(body.World, out var back));
                var local = Vector3.Transform(h.Point, back);
                worst = MathF.Max(worst, Vector3.Distance(Vector3.Transform(local, body.World), h.Point));
                checkedHits++;
                if (h.Probe) continue;   // у пробы точка — центр капсулы
                // Касание клинка — точка на поверхности капсулы своего тика: расстояние до оси минус радиус.
                var onAxis = new Vector3(0, Math.Clamp(local.Y, -body.HalfLength, body.HalfLength), 0);
                surface = MathF.Max(surface, MathF.Abs(Vector3.Distance(local, onAxis) - body.Radius));
                bladeHits++;
            }
        };
        for (int f = 0; f < 600; f++)
        {
            Probes(scene, f);
            scene.Frame();
        }
        Assert.True(checkedHits >= 20 && bladeHits >= 3, $"попаданий {checkedHits}, из них клинком {bladeHits}");
        Assert.True(worst <= 1e-5f, $"точка туда и обратно: {worst} м");
        Assert.True(surface <= 1e-3f, $"точка от поверхности капсулы тика: {surface} м");
    }

    [Fact]
    public void Poses_LastTick_AreSkinPaletteInput_LivingAndCorpse()
    {
        var scene = new Scene(ticksPerFrame: 2, "combat.enemiesAttack=false");
        scene.Frames(5);
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        scene.Frames(40);
        var view = scene.View;
        var poses = scene.Poses;
        Assert.Equal(scene.Sim.Tick - 1, poses.Tick);
        Assert.Equal(view.Fighters.Count, poses.Count);
        int corpses = 0;
        float worst = 0;
        foreach (var f in view.Fighters)
        {
            int index = poses.Find(FighterKey.Of(f.Entity));
            Assert.True(index >= 0);
            var posed = poses.Fighter(index);
            Assert.Equal((f.Corpse, f.Dead, f.IsPlayer, f.Slot), (posed.Corpse, posed.Dead, posed.IsPlayer, posed.Slot));
            var pose = view.Pose(f);
            var joints = poses.Joints(index);
            Assert.Equal(pose.Length, joints.Length);
            // Вход палитры кадра (ArenaClient.BuildFrame): живой — модельная · мир показа, труп — уже мир (привязка · тело показа).
            for (int j = 0; j < joints.Length; j++) worst = MathF.Max(worst, MaxDiff(f.Corpse ? pose[j] : pose[j] * f.World, joints[j]));
            if (f.Corpse) corpses++;
            Assert.Equal(BodySegs.Bodies, poses.Segments(index).Length);
        }
        Assert.Equal(1, corpses);
        Assert.True(worst <= 1e-5f, $"позы тика и вход палитры при доле 1: {worst}");
    }

    private static float MaxDiff(in Matrix4x4 a, in Matrix4x4 b)
    {
        float d = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++) d = MathF.Max(d, MathF.Abs(a[r, c] - b[r, c]));
        return d;
    }

    [Fact]
    public void Terrain_Height_BitExact_TenThousandPoints()
    {
        ITerrainView terrain = new ArenaTerrainView();
        var rng = new Random(12);
        for (int i = 0; i < 10_000; i++)
        {
            float x = (float)(rng.NextDouble() * 80 - 40), z = (float)(rng.NextDouble() * 80 - 40);
            Assert.Equal(BitConverter.SingleToInt32Bits(ArenaTerrain.Height(x, z)), BitConverter.SingleToInt32Bits(terrain.Height(x, z)));
            float smooth = terrain.SmoothHeight(x, z, out var g);
            Assert.Equal(ArenaTerrain.SmoothHeightAt(x, z, out var expected), smooth);
            Assert.Equal(expected, g);
            Assert.Equal(ArenaTerrain.StepHeightAt(x, z), terrain.StepHeight(x, z));
        }
        // Вне участков — пол 0; сетка GPU — сетка рисования рельефа.
        Assert.Equal(0f, terrain.Height(30, 30));
        var field = ArenaTerrain.BuildField();
        Assert.Equal((field.Cells, field.Half, field.Step, field.Nodes), (terrain.Grid.Cells, terrain.Grid.Half, terrain.Grid.Step, terrain.Grid.Nodes));
        Assert.Equal(-1, terrain.HeightsSrv);   // рендера нет
    }

    [Fact]
    public void Deaths_Built_ArenaReset_Bleed()
    {
        var scene = new Scene(ticksPerFrame: 3, "combat.enemiesAttack=false");
        var deaths = new List<(ulong Tick, ulong Key)>();
        var resets = new List<ulong>();
        var built = new List<(ulong Tick, ulong Key)>();
        scene.AfterTick = tick =>
        {
            foreach (ulong k in scene.Feed.Deaths) deaths.Add((tick, k));
            foreach (ulong k in scene.Feed.Built) built.Add((tick, k));
            if (scene.Feed.ArenaReset) resets.Add(tick);
        };
        scene.Frames(5);
        ulong enemy = FighterKey.Of(scene.Slot(2));
        Assert.True(scene.Feed.TryBleed(enemy, out float bleed, out bool dead));
        Assert.Equal((0f, false), (bleed, dead));
        scene.PendingProbes.Add(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.ThighL, Damage = 20 });
        scene.Frames(1);
        Assert.True(scene.Feed.TryBleed(enemy, out bleed, out dead) && bleed > 0 && !dead);
        ulong kill = scene.Sim.Tick;
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        scene.Frames(1);
        Assert.Equal([(kill, FighterKey.Of(scene.Slot(1)))], deaths);
        Assert.True(scene.Feed.TryBleed(FighterKey.Of(scene.Slot(1)), out _, out dead) && dead);
        // Игрок — сущность с номером 0 (не Entity.Null): его труп тоже мёртв, иначе нет лужи трупа игрока.
        Assert.Equal(0, scene.Slot(0).Index);
        Assert.True(scene.Feed.TryBleed(FighterKey.Of(scene.Slot(0)), out _, out dead) && !dead);
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        scene.Frames(1);
        Assert.True(scene.Feed.TryBleed(FighterKey.Of(scene.Slot(0)), out _, out dead) && dead);
        Assert.Empty(resets);

        built.Clear();
        ulong reset = scene.Sim.Tick;
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.Reset });
        scene.Frames(2);
        Assert.Equal([reset], resets);
        Assert.Equal(1, scene.Blood.Resets);
        // Сброс строит заново всех: игрока и трёх врагов — в тике сброса, ключи прежние.
        Assert.Equal(4, built.Count(b => b.Tick == reset));
        Assert.Contains((reset, FighterKey.Of(scene.Slot(0))), built);
        Assert.Contains((reset, enemy), built);
        Assert.False(scene.Feed.TryBleed(ulong.MaxValue, out _, out _));
    }

    [Fact]
    public void DisabledModule_NotConfigured_SectionSkipped()
    {
        var off = new Scene(ticksPerFrame: 2, "modules.blood.enabled=false", "blood.body=false");
        off.Frames(5);
        Assert.Null(off.Blood.Settings);
        Assert.Null(off.Blood.Overlay);
        Assert.Empty(off.Modules.Active);
        Assert.Equal(["blood"], off.Modules.Inactive);
        Assert.Empty(off.Modules.Overlays);
        Assert.Empty(off.Modules.TuningGroups);
        Assert.Empty(off.Modules.Sections());
        Assert.Equal(0, off.Blood.ObservedTicks);
        off.Config.SkipClientSections();
        off.Config.Complete();   // секция blood выключенного модуля — не «неизвестная»

        var on = new Scene(ticksPerFrame: 2);
        on.Frames(5);
        Assert.Equal(["blood"], on.Modules.Active);
        Assert.Equal(["blood", "blood.wounds", "blood.drips"], on.Modules.Overlays.Select(o => o.Name));
        Assert.Equal(["Кровь"], on.Modules.TuningGroups.Select(g => g.Title));
        Assert.Single(on.Modules.Sections());
        Assert.Equal(10, on.Blood.ObservedTicks);
    }

    /// <summary>Часы крови в долгой игре: 600 шагов — 10 с и через 5 ч, и через 73 ч, и через неделю (сумма float там шла вбок и вставала).</summary>
    [Fact]
    public void Clock_LongPlay_KeepsPace()
    {
        const float dt = 1f / 60;
        foreach (long hours in new long[] { 0, 5, 73, 7 * 24 })
        {
            long n = hours * 3600 * 60;
            float c = BloodModule.ClockAt(n, dt), later = BloodModule.ClockAt(n + 600, dt);
            Assert.InRange(c, n * (double)dt * (1 - 1e-6), n * (double)dt * (1 + 1e-6) + 1e-6);
            Assert.InRange(later - c, 9.9, 10.1);
        }
    }

    [Fact]
    public void ModuleAndServices_SteadyFrame_DoNotAllocate()
    {
        var scene = new Scene(ticksPerFrame: 3);
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        int posed = 0;
        Span<char> text = stackalloc char[256];
        var status = scene.Modules.TuningGroups.Single().Items.OfType<Sword.Engine.Diagnostics.TuningInfo>().First().Text;
        // Каждый тик — события и позы тика (снимок всех бойцов), как их будут читать системы крови.
        scene.AfterTick = _ =>
        {
            posed += scene.Poses.Count;
            for (int i = 0; i < scene.Poses.Count; i++) posed += scene.Poses.Joints(i).Length + scene.Poses.Segments(i).Length;
            posed += scene.Feed.Hits.Length + scene.Feed.Deaths.Length + scene.Feed.Built.Length + scene.Feed.Removed.Length;
        };
        for (int f = 0; f < 200; f++)
        {
            Probes(scene, f);
            scene.Frame();
        }
        long bytes = 0, extract = 0, observe = 0;
        scene.AfterTick = tick =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            posed += scene.Poses.Count;
            for (int i = 0; i < scene.Poses.Count; i++) posed += scene.Poses.Joints(i).Length + scene.Poses.Segments(i).Length;
            posed += scene.Feed.Hits.Length + scene.Feed.Deaths.Length + scene.Feed.Built.Length + scene.Feed.Removed.Length;
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        };
        for (int f = 0; f < 200; f++)
        {
            Probes(scene, f);
            scene.Modules.BeginFrame(scene.Sim.Tick);
            scene.Clock.RunFrame(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            scene.Modules.Extract(new FrameView(1, 3, 0, default));
            status(text);
            extract += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        // Наблюдатель модуля — внутри тиков часов: тот же кадр, вызов напрямую по последнему тику.
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            scene.Modules.BeginFrame(scene.Sim.Tick - 1);
            scene.Modules.AfterTick(scene.Sim.Tick - 1);
        }
        observe += GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(posed > 0);
        Assert.True(scene.Blood.Hits > 0);
        Assert.True(bytes == 0 && extract == 0 && observe == 0, $"сервисы в тике {bytes} Б, кадр модуля {extract} Б, наблюдатель {observe} Б");
    }
}
