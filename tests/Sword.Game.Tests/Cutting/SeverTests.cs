using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Cutting;
using Sword.Game.Ragdoll;

namespace Sword.Game.Tests.Cutting;

/// <summary>
/// Расчленение клинком: решение по энергии против сечения (модуль Cutting) и отделение в ядре — культя и кусок с массой по объёму, кусок
/// и отделённая ветвь на физике, живой без ноги умирает, без руки — нет, меч из отрубленной руки выпадает, постройка заново убирает
/// куски; без модуля отрубов нет. Контент синтетический (<see cref="TestAssets"/>: профили ткани — капсулы).
/// </summary>
public sealed class SeverTests
{
    /// <summary>Физику симуляции тест берёт модулем после сборки мира: снаружи она не видна.</summary>
    private sealed class Take(Action<IPhysicsWorld> take) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, IPhysicsWorld> Worlds = [];

    private static Simulation Create(params string[] sets)
    {
        IPhysicsWorld? physics = null;
        var config = ConfigSet.Empty(["arena.enemies=1", "combat.enemiesAttack=false", .. sets]);
        var sim = Simulation.Build(config, GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Take(p => physics = p)], Bootstrap.SwordSimulation.ClientModuleIds);
        Worlds.Add(sim, physics!);
        return sim;
    }

    private static void Step(Simulation sim, int ticks)
    {
        for (int t = 0; t < ticks; t++) sim.Step();
    }

    private static void Probe(Simulation sim, int part, float damage, HitKind kind = HitKind.Slash) =>
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = part, Damage = damage, Kind = kind });

    private static Entity Enemy(Simulation sim)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == 1) return chunk.Entities[i];
        throw new InvalidOperationException("нет врага");
    }

    private static Entity Player(Simulation sim)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<PlayerTag>().Read<FighterSlot>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("нет игрока");
    }

    private static ArticulationHandle Body(Simulation sim, Entity e) => FighterBodies.Handle(sim.World.Get<BodyRef>(e));

    private static IPhysicsWorld Physics(Simulation sim) => Worlds.TryGetValue(sim, out var p) ? p : throw new InvalidOperationException("симуляция не из Create");

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    /// <summary>
    /// Сильный удар по плечу живого врага: отруб — кусок (тело 14) на физике, предплечье на нём; масса культи и куска — масса плеча;
    /// враг жив и стоит; кусок падает; инварианты чисты.
    /// </summary>
    [Fact]
    public void StrongSlash_SeversUpperArm_PieceFallsOwnerLives()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        var physics = Physics(sim);
        var body = Body(sim, enemy);
        float mass = physics.Get(physics.Body(body, BodySegs.UArmL)).Mass;
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.UArmL, 60);
        Step(sim, 1);

        var detached = Assert.Single(sim.Export.Frame<Event<LimbDetached>>().ToArray()).Record.Value;
        Assert.Equal((enemy, BodySegs.UArmL, SeverMode.Cut), (detached.Victim, detached.Segment, detached.Mode));
        var severance = sim.World.Get<Severance>(enemy);
        Assert.Equal(1, severance.Pieces);
        Assert.True(severance.IsCut(BodySegs.UArmL) && severance.IsDetached(BodySegs.FArmL) && !severance.IsDetached(BodySegs.UArmL));
        Assert.Equal(BodySegs.Bodies + 1, physics.BodyCount(body));
        var stump = physics.Get(physics.Body(body, BodySegs.UArmL));
        var piece = physics.Get(physics.Body(body, BodySegs.Bodies));
        Assert.Equal(mass, stump.Mass + piece.Mass, 3);
        Assert.Equal(BodyMode.Kinematic, stump.Mode);
        Assert.Equal(BodyMode.Simulated, piece.Mode);
        Assert.Equal(BodyMode.Simulated, physics.Get(physics.Body(body, BodySegs.FArmL)).Mode);
        Assert.False(sim.World.Has<Dead>(enemy));
        // предплечье висит на куске: сустав предплечья — к телу куска
        bool hangs = false;
        for (int j = 0; j < physics.JointCount(body); j++)
            if (physics.Joint(body, j).Child == BodySegs.FArmL) hangs = physics.Joint(body, j).Parent == BodySegs.Bodies;
        Assert.True(hangs);

        float y0 = piece.Position.Y;
        Step(sim, 60);
        var fallen = physics.Get(physics.Body(body, BodySegs.Bodies));
        Assert.True(fallen.Position.Y < y0 - 0.05f, $"кусок не упал: {y0} → {fallen.Position.Y}");
        Assert.True(fallen.IsFinite);
        Assert.False(sim.World.Has<Dead>(enemy));
        Clean(sim);
    }

    /// <summary>Слабый удар: энергии не хватило на сечение — неполный разрез с долей меньше 1, тело целое.</summary>
    [Fact]
    public void WeakSlash_IsPartialCut()
    {
        var sim = Create("cutting.boneToughness=1000000", "cutting.softToughness=50000");
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.ThighL, 20);
        Step(sim, 1);
        Assert.Empty(sim.Export.Frame<Event<LimbDetached>>().ToArray());
        var partial = Assert.Single(sim.Export.Frame<Event<PartialCut>>().ToArray()).Record.Value;
        Assert.True(partial.Fraction is > 0 and < 1 && partial.Depth > 0, $"{partial.Fraction} {partial.Depth}");
        Assert.Equal(BodySegs.Bodies, Physics(sim).BodyCount(Body(sim, enemy)));
        Clean(sim);
    }

    /// <summary>Отруб голени у живого — смерть с причиной «отделение», труп по дереву после отделения; инварианты чисты.</summary>
    [Fact]
    public void LegSevered_KillsLiving_CorpseKeepsPiece()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        Probe(sim, BodySegs.ShinR, 60);
        Step(sim, 1);
        Assert.True(sim.World.TryGet<Dead>(enemy, out var dead));
        Assert.Equal(DeathCause.Severed, dead.Cause);
        var physics = Physics(sim);
        var body = Body(sim, enemy);
        Assert.True(Corpse.IsCorpse(physics, body));
        for (int k = 0; k < physics.BodyCount(body); k++)
            if (k != BodySegs.Weapon) Assert.Equal(BodyMode.Simulated, physics.Get(physics.Body(body, k)).Mode);
        Step(sim, 240);
        for (int k = 0; k < physics.BodyCount(body); k++) Assert.True(physics.Get(physics.Body(body, k)).IsFinite);
        Clean(sim);
    }

    /// <summary>Отруб предплечья руки с мечом: меч выпадает (тело клинка на физике), враг жив и не бьёт.</summary>
    [Fact]
    public void SwordArmSevered_WeaponFalls()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        Probe(sim, BodySegs.FArmR, 60);
        Step(sim, 1);
        var severance = sim.World.Get<Severance>(enemy);
        Assert.True(severance.IsDetached(BodySegs.Weapon));
        var physics = Physics(sim);
        Assert.Equal(BodyMode.Simulated, physics.Get(physics.Body(Body(sim, enemy), BodySegs.Weapon)).Mode);
        Step(sim, 60);
        Assert.False(sim.World.Has<Dead>(enemy));
        Clean(sim);
    }

    /// <summary>
    /// Игрок без руки с мечом: меч на физике падает и лежит, игрок уходит — меч за ним не едет (ни тело клинка, ни клинок удара).
    /// </summary>
    [Theory]
    [InlineData(BodySegs.FArmR, false)]
    [InlineData(BodySegs.UArmR, false)]
    [InlineData(BodySegs.FArmR, true)]
    public void PlayerSwordArmSevered_SwordStaysBehind(int part, bool swap)
    {
        var sim = Create();
        var clock = new Sword.Engine.Client.SimulationClock(sim, (_, _) => { }, 1);
        var view = new Sword.Game.Client.ArenaView(Sword.Game.Arena.ArenaTerrain.Height);
        for (int t = 0; t < 5; t++)
        {
            clock.RunFrame(0);
            view.Extract(clock.Link, 1, allBodies: false);
        }
        var player = Player(sim);
        sim.Enqueue(new StrikeProbe { Attacker = 1, Victim = 0, Part = part, Damage = 90, Kind = HitKind.Slash });
        clock.RunFrame(0);
        view.Extract(clock.Link, 1, allBodies: false);
        Assert.True(sim.World.Get<Severance>(player).IsDetached(BodySegs.Weapon), "меч не отделён");
        var physics = Physics(sim);
        var blade = physics.Body(Body(sim, player), BodySegs.Weapon);
        var start = physics.Get(blade).Position;
        for (int t = 0; t < 180; t++)
        {
            if (t == 30 && swap) sim.Enqueue(new ArenaCommand { Action = ArenaAction.EquipWeapon, Weapon = 1 });
            sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0), Attack = t % 40 < 20 });
            clock.RunFrame(0);
            // арена рисует меч на его теле
            view.Extract(clock.Link, 1, allBodies: false);
            var shown = view.Fighters.Single(f => f.IsPlayer);
            Assert.True(shown.Weapon >= 0, "меч не рисуется");
            var drawn = shown.WeaponWorld.Translation;
            Assert.True(Vector3.Distance(drawn, physics.Get(blade).Position) < 0.6f, $"тик {t}: меч рисуется в {drawn}, тело клинка в {physics.Get(blade).Position}");
        }
        var s = physics.Get(blade);
        var root = sim.World.Get<Transform>(player).Position;
        Assert.Equal(BodyMode.Simulated, s.Mode);
        Assert.True(s.Position.Y < 0.3f, $"меч не упал: {s.Position}");
        Assert.True(Vector2.Distance(new(s.Position.X, s.Position.Z), new(start.X, start.Z)) < 1.5f, $"меч уехал: {start} → {s.Position}");
        Assert.True(Vector2.Distance(new(root.X, root.Z), new(start.X, start.Z)) > 1.5f, $"игрок не ушёл: {root}");
                Clean(sim);
    }

    /// <summary>
    /// Меч из отрубленной кисти врага падает и остаётся, хотя игрок рядом машет своим мечом и уходит: живой клинок не уносит выпавший.
    /// </summary>
    [Fact]
    public void DroppedEnemySword_NotCarriedByPlayer()
    {
        var sim = Create("combat.playerInvulnerable=true");
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.1f), Facing = MathF.PI });
        Step(sim, 5);
        var enemy = Enemy(sim);
        Probe(sim, BodySegs.FArmR, 60);
        Step(sim, 1);
        Assert.True(sim.World.Get<Severance>(enemy).IsDetached(BodySegs.Weapon));
        var physics = Physics(sim);
        var blade = physics.Body(Body(sim, enemy), BodySegs.Weapon);
        float worst = 0;
        Vector3 rest = default;
        for (int t = 0; t < 420; t++)
        {
            sim.Enqueue(new PlayerCommand { Attack = t % 50 < 25, Move = t >= 120 ? new Vector2(1, 0) : default, HasGaze = true, Gaze = new Vector3(0, 0, 1) });
            sim.Step();
            if (t == 110) rest = physics.Get(blade).Position;
            if (t > 110) worst = MathF.Max(worst, Vector3.Distance(physics.Get(blade).Position, rest));
        }
        var player = sim.World.Get<Transform>(Player(sim)).Position;
        Assert.True(worst < 0.5f, $"выпавший меч уехал на {worst:F2} м (игрок в {player}, меч в {physics.Get(blade).Position})");
        Clean(sim);
    }

    /// <summary>Труп режется тоже; повторный отруб той же части в тике отклоняется; постройка заново убирает куски и отделения.</summary>
    [Fact]
    public void CorpseCut_ThenRebuild_RemovesPieces()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        Step(sim, 30);
        Probe(sim, BodySegs.ThighL, 60);
        Probe(sim, BodySegs.ThighL, 60);
        Step(sim, 1);
        var physics = Physics(sim);
        var body = Body(sim, enemy);
        Assert.Equal(BodySegs.Bodies + 1, physics.BodyCount(body));
        Step(sim, 120);
        Clean(sim);
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(3, 3) });
        Step(sim, 2);
        Assert.Equal(BodySegs.Bodies, physics.BodyCount(body));
        Assert.False(sim.World.Has<Severance>(enemy));
        Step(sim, 30);
        Clean(sim);
    }

    /// <summary>Без модуля Cutting: заявки есть, отрубов нет, тело — 14 капсул.</summary>
    [Fact]
    public void WithoutModule_NoSever()
    {
        var sim = Create("modules.cutting.enabled=false");
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.UArmL, 60);
        Step(sim, 1);
        Assert.Single(sim.Export.Frame<Event<SeverRequested>>().ToArray());
        Assert.Empty(sim.Export.Frame<Event<LimbDetached>>().ToArray());
        Assert.Equal(BodySegs.Bodies, Physics(sim).BodyCount(Body(sim, enemy)));
        Assert.False(sim.World.Has<Severance>(enemy));
    }

    /// <summary>Детерминизм: два прогона с отрубами — один хеш.</summary>
    [Fact]
    public void TwoRuns_SameHash()
    {
        ulong Run()
        {
            var sim = Create();
            Step(sim, 5);
            Probe(sim, BodySegs.UArmL, 60);
            Step(sim, 3);
            Probe(sim, BodySegs.ShinL, 60);
            Step(sim, 60);
            return sim.HashState().Total;
        }
        Assert.Equal(Run(), Run());
    }

    // ── тупое и взрыв ────────────────────────────────────────────────────────────────────────────────────────────────

    private static LimbDetached[] Detached(Simulation sim) => [.. sim.Export.Frame<Event<LimbDetached>>().ToArray().Select(r => r.Record.Value)];

    /// <summary>Молот средней силы по предплечью: перелом — кусок висит на слабом суставе у культи; живой жив.</summary>
    [Fact]
    public void Blunt_Fractures_PieceHangsOnWeakJoint()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.FArmL, 40, HitKind.Blunt);
        Step(sim, 1);
        var ev = Assert.Single(Detached(sim));
        Assert.Equal(SeverMode.Fracture, ev.Mode);
        var physics = Physics(sim);
        var body = Body(sim, enemy);
        Assert.Equal(BodySegs.Bodies + 1, physics.BodyCount(body));
        bool hinge = false;
        for (int j = 0; j < physics.JointCount(body); j++)
            hinge |= physics.Joint(body, j) is { Parent: BodySegs.FArmL, Child: BodySegs.Bodies };
        Assert.True(hinge, "нет сустава перелома");
        Step(sim, 120);
        Assert.False(sim.World.Has<Dead>(enemy));
        Clean(sim);
    }

    /// <summary>
    /// Молот и клинок вперемешку по 8 врагам (живым и трупам, сила — от слабой до раздробления) 20 с: суставы держат, куски не
    /// раскручиваются — инварианты чисты.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(23)]
    public void MixedHits_Crowd_InvariantsClean(int seed)
    {
        var sim = Create("arena.enemies=8");
        var rng = new Random(seed);
        for (int t = 0; t < 2400; t++)
        {
            if (t % 9 == 0)
            {
                var probe = new StrikeProbe
                {
                    Attacker = 0, Victim = 1 + rng.Next(8), Part = rng.Next(BodySegs.Count - 1) is var p && p >= BodySegs.Weapon ? p + 1 : p,
                    Damage = 20 + rng.NextSingle() * 80, Kind = rng.Next(3) == 0 ? HitKind.Slash : HitKind.Blunt,
                };
                sim.Enqueue(probe);
            }
            sim.Step();
        }
        Clean(sim);
    }

    /// <summary>
    /// Сильный молот по голени: кость ломается, но не отрывается — кусок висит на слабом суставе (меш не режется), живой без ноги
    /// падает; второй сильный удар по сломанной голени отрывает её по суставу.
    /// </summary>
    [Fact]
    public void Blunt_BreaksLeg_SecondHitTearsBroken()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.ShinL, 80, HitKind.Blunt);
        Step(sim, 1);
        var first = Assert.Single(Detached(sim));
        Assert.Equal(SeverMode.Fracture, first.Mode);
        Assert.False(sim.World.Get<Severance>(enemy).IsTorn(BodySegs.ShinL));
        Step(sim, 60);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.ShinL, 80, HitKind.Blunt);
        Step(sim, 1);
        var second = Assert.Single(Detached(sim));
        Assert.Equal(SeverMode.Tear, second.Mode);
        var severance = sim.World.Get<Severance>(enemy);
        Assert.True(severance.IsTorn(BodySegs.ShinL) && severance.IsDetached(BodySegs.FootL));
        Assert.True(sim.World.Has<Dead>(enemy));
        Step(sim, 120);
        Clean(sim);
    }

    /// <summary>
    /// Молот по голове стоящего — голова цела (удар уводит голову; урон решает здоровье); по голове трупа — в клочья: пять кусков на физике,
    /// культя шеи; куски числовые и падают.
    /// </summary>
    [Fact]
    public void Blunt_ShattersHeadOnlyLying()
    {
        var sim = Create();
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.Head, 30, HitKind.Blunt);
        Step(sim, 1);
        Assert.Empty(Detached(sim));
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        Step(sim, 60);
        sim.Export.BeginFrame();
        Probe(sim, BodySegs.Head, 80, HitKind.Blunt);
        Step(sim, 1);
        var ev = Assert.Single(Detached(sim));
        Assert.Equal((SeverMode.Shatter, 0), (ev.Mode, ev.Piece));
        var physics = Physics(sim);
        var body = Body(sim, enemy);
        Assert.Equal(BodySegs.Bodies + ev.Count, physics.BodyCount(body));
        Assert.Equal(ev.Count, sim.World.Get<Severance>(enemy).Pieces);
        Step(sim, 180);
        for (int k = BodySegs.Bodies; k < physics.BodyCount(body); k++) Assert.True(physics.Get(physics.Body(body, k)).IsFinite);
        Clean(sim);
    }

    /// <summary>
    /// Взрыв у врага: мёртв, тела разлетаются от центра; вблизи части рвутся (отрыв или клочья). Два прогона — один хеш.
    /// </summary>
    [Fact]
    public void Blast_KillsTearsAndPushes()
    {
        (ulong Hash, int Detached, float Away, bool Dead) Run()
        {
            var sim = Create();
            var enemy = Enemy(sim);
            Step(sim, 5);
            var physics = Physics(sim);
            var body = Body(sim, enemy);
            var center = physics.Get(physics.Body(body, BodySegs.Torso)).Position + new Vector3(0.3f, -0.2f, 0);
            sim.Export.BeginFrame();
            sim.Enqueue(new BlastCommand { Position = center, Energy = 40000, Radius = 5 });
            Step(sim, 1);
            int detached = Detached(sim).Length;
            var pelvis = physics.Get(physics.Body(body, BodySegs.Pelvis));
            float away = Vector3.Dot(pelvis.Velocity, Vector3.Normalize(pelvis.Position - center));
            Step(sim, 120);
            Clean(sim);
            return (sim.HashState().Total, detached, away, sim.World.Has<Dead>(enemy));
        }
        var a = Run();
        Assert.True(a.Dead);
        Assert.True(a.Detached > 0, "взрыв ничего не оторвал");
        Assert.True(a.Away > 1, $"таз не толкнуло от взрыва: {a.Away} м/с");
        Assert.Equal(a.Hash, Run().Hash);
    }

    // ── физика разреза ───────────────────────────────────────────────────────────────────────────────────────────────

    private static TissueProfiles Cylinder(float half, float radius, float bone)
    {
        var t = new TissueProfiles();
        for (int s = 0; s < BodySegs.Count; s++)
        {
            t.Start[s] = -half;
            t.End[s] = half;
            t.Half[s] = half;
            t.Bone[s] = bone;
            for (int i = 0; i < TissueProfiles.Samples; i++) t.Radius[s * TissueProfiles.Samples + i] = radius;
        }
        return t;
    }

    private static BodyState Limb(float mass, float half, float radius)
    {
        var b = BodyState.New(BodyMode.Simulated);
        b.Mass = mass;
        b.HalfLength = half;
        b.Radius = radius;
        b.Rotation = Quaternion.Identity;
        return b;
    }

    private static readonly CuttingRules Rules = CuttingRules.From(new CuttingSettings());

    /// <summary>
    /// Отруб поперёк цилиндра: плоскость поперёк оси, масса делится по объёму (сумма — масса части), культя — от проксимального конца до
    /// разреза, импульс — работа разреза / скорость вдоль хода клинка. Слабее — неполный; укол и клинок вдоль хода — ничего.
    /// </summary>
    [Fact]
    public void Decide_EnergyAgainstSection()
    {
        var tissue = Cylinder(0.2f, 0.05f, 0.01f);
        var limb = Limb(2, 0.2f, 0.05f);
        var request = new SeverRequested
        {
            Part = BodySegs.FArmL, Point = new Vector3(0.05f, 0.05f, 0), Blade = Vector3.UnitZ, Velocity = new Vector3(-10, 0, 0), Sharpness = 1,
        };
        Assert.Equal(SeverOutcome.Cut, SeverPhysics.Decide(request, BodySegs.FArmL, limb, 0, 0.2f, tissue, Rules, out var cut, out _));
        Assert.True(Vector3.Distance(cut.Normal, Vector3.UnitY) < 1e-4f, $"{cut.Normal}");
        Assert.Equal(0.05f, cut.Offset, 4);
        Assert.Equal(2f, cut.StumpMass + cut.PieceMass, 4);
        Assert.Equal(2f * 0.15f / 0.4f, cut.PieceMass, 3);
        Assert.Equal((-0.2f + 0.05f) * 0.5f, cut.StumpShift, 4);
        float need = Rules.SoftToughness * MathF.PI * (0.05f * 0.05f - 0.01f * 0.01f) + Rules.BoneToughness * MathF.PI * 0.01f * 0.01f;
        Assert.True(Vector3.Distance(cut.Impulse, new Vector3(-need / 10, 0, 0)) < 1e-3f, $"{cut.Impulse}");

        var weak = request with { Velocity = new Vector3(-1, 0, 0) };
        Assert.Equal(SeverOutcome.Partial, SeverPhysics.Decide(weak, BodySegs.FArmL, limb, 0, 0.2f, tissue, Rules, out _, out var partial));
        Assert.True(partial.Fraction is > 0 and < 1);
        // укол с ходом поперёк клинка режет, как рубящий; прямой укол вдоль клинка — прокол
        Assert.Equal(SeverOutcome.Cut, SeverPhysics.Decide(request with { Kind = HitKind.Thrust }, BodySegs.FArmL, limb, 0, 0.2f, tissue, Rules, out _, out _));
        Assert.Equal(SeverOutcome.Partial,
            SeverPhysics.Decide(request with { Kind = HitKind.Thrust, Blade = Vector3.UnitX }, BodySegs.FArmL, limb, 0, 0.2f, tissue, Rules, out _, out _));
        Assert.Equal(SeverOutcome.None, SeverPhysics.Decide(request with { Blade = Vector3.UnitX }, BodySegs.FArmL, limb, 0, 0.2f, tissue, Rules, out _, out _));
    }

    /// <summary>Продольный удар доворачивается: плоскость не ближе 30° к оси, нормаль — к дистальной стороне.</summary>
    [Fact]
    public void Tilt_KeepsPlaneAtLeast30DegreesFromAxis()
    {
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var n = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
            var t = SeverPhysics.Tilt(n);
            Assert.True(t.Y >= SeverPhysics.MinAxisCos - 1e-5f, $"{n} → {t}");
            Assert.Equal(1f, t.Length(), 4);
        }
    }

    /// <summary>
    /// С куском торса уходят дети, чей сустав по его сторону: срез по груди уносит голову и обе руки, косой срез у плеч — голову и верхнее
    /// плечо (нижнее остаётся на культе), срез у шеи — только голову. Клинок уходит с куском предплечья всегда.
    /// </summary>
    [Fact]
    public void Carried_ChildrenOnPieceSide()
    {
        // торс стоит по Y (центр кости 1,3 м, полудлина 0,25); плечи — на 0,2 выше центра и 0,18 в стороны, шея — на 0,25
        var positions = new Vector3[BodySegs.Count];
        var rotations = new Quaternion[BodySegs.Count];
        var halves = new float[BodySegs.Count];
        Array.Fill(rotations, Quaternion.Identity);
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);   // руки висят: ось Y вниз
        positions[BodySegs.Head] = new(0, 1.55f + 0.1f, 0);
        halves[BodySegs.Head] = 0.1f;
        positions[BodySegs.UArmL] = new(0.18f, 1.5f - 0.14f, 0);
        positions[BodySegs.UArmR] = new(-0.18f, 1.5f - 0.14f, 0);
        rotations[BodySegs.UArmL] = rotations[BodySegs.UArmR] = down;
        halves[BodySegs.UArmL] = halves[BodySegs.UArmR] = 0.14f;
        var center = new Vector3(0, 1.3f, 0);
        ushort all = (ushort)(1 << BodySegs.Head | 1 << BodySegs.UArmL | 1 << BodySegs.UArmR);
        ushort Carried(Vector3 n, float offset) =>
            SeverGeometry.Carried(BodySegs.Torso, Vector3.Normalize(n), offset, center, Quaternion.Identity, positions, rotations, halves, all);
        Assert.Equal(BodySegs.Branch(BodySegs.Head) | BodySegs.Branch(BodySegs.UArmL) | BodySegs.Branch(BodySegs.UArmR), Carried(Vector3.UnitY, 0.05f));
        Assert.Equal(BodySegs.Branch(BodySegs.Head), Carried(Vector3.UnitY, 0.22f));
        // косо: плоскость поднимается к правому плечу (−X)
        Assert.Equal(BodySegs.Branch(BodySegs.Head) | BodySegs.Branch(BodySegs.UArmL), Carried(new Vector3(0.5f, 1, 0), 0.15f));
        Assert.Equal(0, Carried(Vector3.UnitY, 0.3f));
        // клинок держит кисть: с куском предплечья, где бы ни лежало тело клинка
        positions[BodySegs.Weapon] = new(5, -5, 5);
        ushort blade = SeverGeometry.Carried(BodySegs.FArmR, Vector3.UnitY, 0.5f, Vector3.Zero, Quaternion.Identity, positions, rotations, halves,
            1 << BodySegs.Weapon);
        Assert.Equal(1 << BodySegs.Weapon, blade);
    }

    /// <summary>Энергия растёт с квадратом скорости: быстрый удар перерубает бедро, медленный — нет (кость бедра толще кости предплечья).</summary>
    [Fact]
    public void ThickerSection_NeedsMoreEnergy()
    {
        var thin = Cylinder(0.15f, 0.045f, 0.01f);
        var thick = Cylinder(0.22f, 0.08f, 0.014f);
        var request = new SeverRequested { Point = Vector3.Zero, Blade = Vector3.UnitZ, Velocity = new Vector3(-6, 0, 0), Sharpness = 1 };
        Assert.Equal(SeverOutcome.Cut, SeverPhysics.Decide(request, BodySegs.FArmL, Limb(2, 0.15f, 0.045f), 0, 0.15f, thin, Rules, out _, out _));
        Assert.Equal(SeverOutcome.Partial, SeverPhysics.Decide(request, BodySegs.ThighL, Limb(8, 0.22f, 0.08f), 0, 0.22f, thick, Rules, out _, out _));
        var fast = request with { Velocity = new Vector3(-16, 0, 0) };
        Assert.Equal(SeverOutcome.Cut, SeverPhysics.Decide(fast, BodySegs.ThighL, Limb(8, 0.22f, 0.08f), 0, 0.22f, thick, Rules, out _, out _));
    }
}
