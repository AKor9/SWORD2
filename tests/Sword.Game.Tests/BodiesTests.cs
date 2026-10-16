using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Тела бойцов: 13 кинематических капсул за <see cref="BodyRef"/>, время жизни — хуками ECS; команды арены не оставляют лишних
/// и чужих тел; сброс сохраняет тело; участник хеша <c>physics</c>; постановка по позе тика, вид <see cref="IBodyView"/>.
/// </summary>
public sealed class BodiesTests
{
    /// <summary>Физику симуляции тест берёт модулем после сборки мира: снаружи она не видна.</summary>
    private sealed class Probe(Action<IPhysicsWorld> take, ISystem? system = null) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b)
        {
            b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
            if (system is not null) b.System(GameStages.Gameplay, system);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, IPhysicsWorld> Worlds = [];

    private static Simulation Create(string sets, ISystem? system = null)
    {
        IPhysicsWorld? physics = null;
        var sim = Simulation.Build(ConfigSet.Empty(sets.Split(';')), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe(p => physics = p, system)]);
        Worlds.Add(sim, physics!);
        return sim;
    }

    private static void Step(Simulation sim, params ArenaCommand[] commands)
    {
        foreach (var c in commands) sim.Enqueue(c);
        sim.Export.BeginFrame();
        sim.Step();
    }

    private static ArenaCommand Change(int n) => new() { Action = ArenaAction.ChangeEnemies, Enemies = n };
    private static ArenaCommand Set(int n) => new() { Action = ArenaAction.SetEnemies, Enemies = n };
    private static readonly ArenaCommand Reset = new() { Action = ArenaAction.Reset };

    private static IPhysicsWorld Physics(Simulation sim) => Worlds.TryGetValue(sim, out var p) ? p : throw new InvalidOperationException("симуляция не из Create");

    /// <summary>Бойцы по местам: сущность и её тело.</summary>
    private static SortedDictionary<int, (Entity Entity, ArticulationHandle Body)> Bodies(Simulation sim)
    {
        var w = sim.World;
        var rows = new SortedDictionary<int, (Entity, ArticulationHandle)>();
        foreach (var chunk in w.Iterate(w.Query().With<Fighter>().Read<FighterSlot>().Read<BodyRef>()))
        {
            var slots = chunk.Read<FighterSlot>();
            var refs = chunk.Read<BodyRef>();
            for (int i = 0; i < chunk.Count; i++) rows.Add(slots[i].Index, (chunk.Entities[i], FighterBodies.Handle(refs[i])));
        }
        return rows;
    }

    /// <summary>У каждого бойца живое сочленение из 13 тел, у каждого сочленения — ровно один боец, других тел нет.</summary>
    private static void AssertConsistent(Simulation sim)
    {
        var physics = Physics(sim);
        var bodies = Bodies(sim);
        Assert.Equal(sim.World.Resource<ArenaState>().Enemies + 1, bodies.Count);
        Assert.All(bodies.Values, b => Assert.True(physics.IsAlive(b.Body) && physics.BodyCount(b.Body) == BodySegs.Bodies, $"{b.Entity}: {b.Body}"));
        Assert.Equal(bodies.Count, bodies.Values.Select(b => b.Body).Distinct().Count());
        Assert.Equal((bodies.Count, bodies.Count * BodySegs.Bodies), (physics.ArticulationCount, physics.LiveBodyCount));
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent()));
    }

    /// <summary>
    /// Тело клинка бойца по позе <paramref name="model"/> в корне и курсе: центр и поворот — капсула от хвата (начало кисти) до
    /// кончика меча <paramref name="weapon"/> (<see cref="Combat.BladePlacement.BodyOf"/>).
    /// </summary>
    private static (Vector3 Position, Quaternion Rotation) Blade(ReadOnlySpan<Matrix4x4> model, Vector3 root, float facing, int weapon)
    {
        var weapons = TestAssets.Assets.Weapons;
        var hand = Combat.BladePlacement.Hand(model, TestAssets.Assets.Hand, root, facing);
        return Combat.BladePlacement.BodyOf(hand, weapons.Weapons[weapon], weapons.Bodies[weapon]);
    }

    [Fact]
    public void EveryFighter_GetsFourteenKinematicBodies_ShapeMassAndPlacementAtWorldBuild()
    {
        var sim = Create("arena.enemies=3");
        AssertConsistent(sim);   // сборка мира: немедленные операции — тоже барьеры, хуки сработали
        var physics = Physics(sim);
        var (_, player) = Bodies(sim)[0];
        Assert.Equal(new ArticulationHandle(0, 1), player);   // игрок строится первым
        for (int i = 0; i < BodySegs.Bodies; i++) Assert.Equal(player, physics.ArticulationOf(physics.Body(player, i)));

        // Тела бойцов сборки мира стоят до первого тика: форма — радиусы Fighter.Radii и полудлины процедурной позы постройки,
        // капсулы — по позе постройки без скорости. Тик 1 ставит их ещё раз теми же значениями — состояние то же. Масса —
        // FighterShape.Masses, тело клинка — по мечу в руке (радиус 0,03, полудлина — половина отрезка хват — кончик).
        var built = Snapshot(sim);
        Span<float> half = stackalloc float[BodySegs.Count];
        var model = StartModel(sim.World.Resource<StartPose>().Clip);
        var joints = new BodyJoints(TestAssets.Assets.Clips.Skeleton);
        var pos = new Vector3[BodySegs.Bodies];
        var rot = new Quaternion[BodySegs.Bodies];
        foreach (var (e, body) in Bodies(sim).Values)
        {
            var root = sim.World.Get<Transform>(e);   // корень постройки
            FighterShape.HalfLengths(root.Position, root.Facing, half);
            BodyPlacement.Capsules(model, joints, root.Position, root.Facing, pos, rot);
            int weapon = sim.World.Get<WeaponSlot>(e).Index;
            (pos[BodySegs.Weapon], rot[BodySegs.Weapon]) = Blade(model, root.Position, root.Facing, weapon);
            half[BodySegs.Weapon] = TestAssets.Assets.Weapons.Bodies[weapon].HalfLength;
            for (int i = 0; i < BodySegs.Bodies; i++)
            {
                var s = physics.Get(physics.Body(body, i));
                int seg = BodySegs.Segment(i);
                Assert.Equal((FighterShape.Radii[seg], half[seg], FighterShape.Masses[seg]), (s.Radius, s.HalfLength, s.Mass));
                Assert.Equal(Dynamics.CapsuleInvInertia(s.Mass, s.Radius, s.HalfLength), s.InvInertia);
                if (BodySegs.IsFlesh(seg)) Assert.True(s.HalfLength is > 0.02f and < 0.4f, $"{BodySegs.Name(seg)}: полудлина {s.HalfLength}");
                Assert.Equal((pos[i], rot[i], pos[i], rot[i]), (s.Position, s.Rotation, s.PrevPosition, s.PrevRotation));
                Assert.Equal((Vector3.Zero, Vector3.Zero, BodyMode.Kinematic, 0), (s.Velocity, s.AngularVelocity, s.Mode, (int)s.Profile));
            }
        }
        // Проверка «сейчас» до первого тика — капсулы сверяются с позой постройки, нарушений нет.
        Assert.All(sim.Invariants.Run(["physics.kinematicPose", "physics.finite"], 0, "—", sim.Dt), r => Assert.Equal(0, r.Violations));

        Step(sim);
        // Тик 1: постройка сборки мира поставлена ещё раз теми же значениями, затем постановка тика — прошлая постановка та же.
        foreach (var (e, body) in Bodies(sim).Values)
            for (int i = 0; i < BodySegs.Bodies; i++)
            {
                var s = physics.Get(physics.Body(body, i));
                var b = built[(e, i)];
                Assert.Equal((b.Position, b.Rotation, b.Radius, b.HalfLength), (s.PrevPosition, s.PrevRotation, s.Radius, s.HalfLength));
            }
        for (int t = 0; t < 60; t++) Step(sim);
        AssertConsistent(sim);   // и physics.kinematicPose, physics.kinematicSpeed — чисто
    }

    private static Dictionary<(Entity, int), BodyState> Snapshot(Simulation sim)
    {
        var physics = Physics(sim);
        var result = new Dictionary<(Entity, int), BodyState>();
        foreach (var (e, body) in Bodies(sim).Values)
            for (int i = 0; i < BodySegs.Bodies; i++) result[(e, i)] = physics.Get(physics.Body(body, i));
        return result;
    }

    /// <summary>Модельные матрицы позы постройки: клип <paramref name="clip"/> в момент 0 (−1 — поза привязки) — своим пулом.</summary>
    private static Matrix4x4[] StartModel(int clip)
    {
        var pool = new PosePool(TestAssets.Assets.Clips, 1);
        var h = pool.Alloc();
        if (clip >= 0) pool.Sample(h, clip, 0);
        pool.ToModel(h);
        pool.Tick();
        return pool.Model(h).ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_PlacesFromStartPose_WithoutVelocity_ThenTickVelocityIsTheDifference(bool locomotion)
    {
        // При постройке капсулы стоят по позе стойки с нулевого момента (без модуля Locomotion — поза привязки) без скорости;
        // в тике — цель по позе тика, скорость — разность за тик.
        var sim = Create(locomotion ? "arena.enemies=1" : "arena.enemies=1;modules.locomotion.enabled=false");
        var start = sim.World.Resource<StartPose>();
        Assert.Equal(locomotion ? TestAssets.Assets.Actions.Idle : -1, start.Clip);
        Assert.Equal(0f, start.Time);
        var model = StartModel(start.Clip);
        var joints = new BodyJoints(TestAssets.Assets.Clips.Skeleton);
        var physics = Physics(sim);
        var pos = new Vector3[BodySegs.Bodies];
        var rot = new Quaternion[BodySegs.Bodies];

        void Check(string when)
        {
            foreach (var (e, body) in Bodies(sim).Values)
            {
                var built = sim.World.Get<PrevTransform>(e);   // корень постройки в тике постройки
                BodyPlacement.Capsules(model, joints, built.Position, built.Facing, pos, rot);
                (pos[BodySegs.Weapon], rot[BodySegs.Weapon]) = Blade(model, built.Position, built.Facing, sim.World.Get<WeaponSlot>(e).Index);
                for (int i = 0; i < BodySegs.Bodies; i++)
                {
                    var s = physics.Get(physics.Body(body, i));
                    Assert.True(s.PrevPosition == pos[i] && s.PrevRotation == rot[i], $"{when}, {e}, {BodySegs.Name(BodySegs.Segment(i))}: прошлая постановка — не постройка");
                    Assert.Equal((s.Position - s.PrevPosition) / sim.Dt, s.Velocity);
                    Assert.Equal(PhysicsMath.RotationVector(s.PrevRotation, s.Rotation) / sim.Dt, s.AngularVelocity);
                }
            }
        }

        Step(sim);
        Check("сборка мира, тик 1");
        for (int t = 0; t < 40; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0) });
            Step(sim);
        }
        sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0) });
        Step(sim, Reset);   // игрок убежал — сброс строит его в центре: без скачка скорости капсул
        Check("сброс R");
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Capsules_FollowThePose_AndTheTerrain()
    {
        // Игрок бежит на склон (0, 18): капсулы идут за корнем и поднимаются с рельефом; таз — над землёй под корнем.
        var sim = Create("arena.enemies=0");
        var view = sim.World.View<IBodyView>();
        var (player, body) = Bodies(sim)[0];
        var bodyRef = sim.World.Get<BodyRef>(player);
        for (int t = 0; t < 600; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
            Step(sim);
        }
        var root = sim.World.Get<Transform>(player).Position;
        float ground = Arena.ArenaTerrain.Height(root.X, root.Z);
        Assert.True(ground > 0.5f, $"игрок не на склоне: корень {root}, земля {ground}");
        var pelvis = view.Body(bodyRef, BodySegs.Body(BodySegs.Pelvis));
        Assert.InRange(pelvis.Position.Y - ground, 0.5f, 1.3f);
        Assert.InRange(new Vector2(pelvis.Position.X - root.X, pelvis.Position.Z - root.Z).Length(), 0, 0.3f);
        Assert.InRange(pelvis.Velocity.Z, 2, 6);   // бежит вперёд по +Z
        Assert.Equal(BodySegs.Bodies, view.BodyCount(bodyRef));
        Assert.Equal(0, view.BodyCount(default));
        Assert.Equal(0, sim.Invariants.Total);
        _ = body;
    }


    [Fact]
    public void ArenaCommands_LeaveNoExtraOrForeignBodies()
    {
        var sim = Create("arena.enemies=5");
        ArenaCommand[][] script =
        [
            [Change(1)], [Change(1)], [Change(-1)], [Change(-3)], [Set(32)], [Set(0)], [Change(4)], [Reset],
            [Change(2), Change(-2)],          // прибавили и убавили в одном тике — места не строятся
            [Change(-2), Change(2)],          // убавили и прибавили — места строятся заново на новых сущностях
            [Change(3), Reset, Change(-1)],
            [Set(32), Reset], [Set(0), Change(1), Reset],
        ];
        foreach (var commands in script)
        {
            Step(sim, commands);
            AssertConsistent(sim);
            Step(sim);
            AssertConsistent(sim);
        }
    }

    [Fact]
    public void Reset_RebuildsOnTheSameEntity_WithTheSameBody_PageDownUp_NewEntityNewBody()
    {
        var sim = Create("arena.enemies=4");
        for (int t = 0; t < 30; t++) Step(sim);
        var before = Bodies(sim);
        Step(sim, Reset);
        var after = Bodies(sim);
        // Тот же боец места, те же тела (форма и постановка — заново).
        Assert.Equal(before, after);

        Step(sim, Change(-1));
        Assert.False(Physics(sim).IsAlive(before[4].Body));   // ушедшее место — тело освобождено хуком снятия
        Step(sim, Change(1));
        var back = Bodies(sim)[4];
        Assert.NotEqual(before[4].Entity, back.Entity);
        Assert.Equal((before[4].Body.Index, before[4].Body.Generation + 1), (back.Body.Index, back.Body.Generation));   // тот же слот, новое поколение
        AssertConsistent(sim);
    }

    /// <summary>На тике 5 создаёт сущность с <see cref="BodyRef"/> и удаляет её командами того же барьера.</summary>
    private sealed class Blink : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("blink")).Creates().Destroys().Adds<BodyRef>();

        public void Run(in SystemContext ctx)
        {
            if (ctx.World.Resource<SimTime>().Tick != 5) return;
            var d = ctx.Commands.Create();
            ctx.Commands.Add(d, new BodyRef());
            ctx.Commands.Destroy(d.Ref);
        }
    }

    [Fact]
    public void CreatedAndDestroyedOnOneBarrier_BothHooksRan_NoBodyLeft()
    {
        var sim = Create("arena.enemies=2", new Blink());
        var physics = Physics(sim);
        int created = 0;
        for (int t = 0; t < 10; t++)
        {
            int generationBefore = physics.ArticulationGenerationAt(3);
            Step(sim);
            if (physics.ArticulationGenerationAt(3) != generationBefore) created++;
            AssertConsistent(sim);
        }
        // Слот 3 (после игрока и двух врагов) выдан хуком добавления и возвращён хуком снятия на барьере Gameplay тика 5.
        Assert.Equal(1, created);
        Assert.Equal(-1, physics.ArticulationGenerationAt(3));
    }

    [Fact]
    public void PhysicsPart_HashesBodies()
    {
        var a = Create("arena.enemies=1");
        var b = Create("arena.enemies=1");
        Assert.Equal(a.HashState().Parts["physics"], b.HashState().Parts["physics"]);
        Step(a, Change(1));
        Step(b);
        Assert.NotEqual(a.HashState().Parts["physics"], b.HashState().Parts["physics"]);   // новое тело — другой хеш

        // Хешируемое — прямо из подсистемы: тела бойцов, поля динамики тела, профили мира, свободные слоты.
        var physics = Physics(a);
        Assert.Equal(3, physics.ArticulationCount);
        Assert.Equal(3 * 14, physics.LiveBodyCount);   // 14 тел на бойца — и клинок
        var player = FighterBodies.Handle(a.World.Get<BodyRef>(GameCore.Player(a.World)));
        Assert.Equal(14, physics.BodyCount(player));
        var pelvis = physics.Get(physics.Body(player, 0));
        Assert.Equal(BodyMode.Kinematic, pelvis.Mode);
        var foot = physics.Get(physics.Body(player, 13));
        Assert.Equal(FighterShape.Radii[BodySegs.FootR], foot.Radius);   // форма и постановка с тика 1
        Assert.True(foot.HalfLength > 0);
        Assert.Equal((12f, 1f / 12, 0), (pelvis.Mass, pelvis.InvMass, (int)pelvis.Profile));
        var blade = physics.Get(physics.Body(player, 11));
        Assert.Equal((BladeBody.Mass, BladeBody.Radius), (blade.Mass, blade.Radius));
        Assert.Equal((KinematicSource.Body, false), (blade.Source, blade.Grounded));
        // Профиль 0 — по умолчанию, 1 — трупа (потолки ручек «Рэгдолл»)
        Assert.Equal(2, physics.DynamicsCount);
        Assert.Equal(9.81f, physics.GetDynamics(0).Gravity);
        Assert.Equal(15f, physics.GetDynamics(1).MaxSpeed);
        Assert.Equal(ArenaRules.MaxFighters - 3, physics.FreeArticulations.Length);
    }

    [Fact]
    public void JointSegments_AsCharacterRig_ByNameThenParents()
    {
        // Сегмент сустава: ключевые кости, затем по имени кости UE Manny, затем вверх по родителям, иначе таз.
        // Имена — UE Manny (пальцы и ball_r в экспорте UE без родителя).
        string[] names = ["root", "pelvis", "spine_01", "spine_03", "clavicle_l", "neck_01", "head", "upperarm_l", "hand_r",
            "index_01_l", "thigh_r", "calf_l", "ball_r", "ik_foot_root"];
        int[] parents = [-1, 0, 1, 2, 3, 3, 5, 4, -1, -1, 1, 1, -1, 0];
        Assert.Equal([BodySegs.Pelvis, BodySegs.Pelvis, BodySegs.Torso, BodySegs.Torso, BodySegs.Torso, BodySegs.Head, BodySegs.Head,
            BodySegs.UArmL, BodySegs.FArmR, BodySegs.FArmL, BodySegs.ThighR, BodySegs.ShinL, BodySegs.FootR, BodySegs.Pelvis],
            JointSegments.Build(names, parents));
        var skeleton = TestAssets.Assets.Clips.Skeleton;
        var segments = JointSegments.Build(skeleton);
        Assert.All(segments, s => Assert.True(BodySegs.IsFlesh(s), $"сегмент {s} — не плоть"));   // скин — только на плоти (клинок — меч)
        // Синтетический скелет: неизвестная кость — по родителю, без ключевого родителя — таз.
        Assert.Equal([BodySegs.Pelvis, BodySegs.ThighL, BodySegs.ThighL, BodySegs.Pelvis],
            JointSegments.Build(["pelvis", "thigh_l", "strap", "prop"], [-1, 0, 1, -1]));
        // Вид тел отдаёт ту же таблицу клиенту.
        var view = Create("arena.enemies=0").World.View<IBodyView>();
        Assert.Equal(segments.Length, view.JointCount);
        Assert.All(Enumerable.Range(0, segments.Length), j => Assert.Equal(segments[j], view.JointSegment(j)));
    }

    [Fact]
    public void SegmentNumbering_BodyIsSegment_BladeIsBodyButNotFlesh()
    {
        // Номера сегментов: 0…10, клинок 11, стопы 12 и 13; тело i сочленения бойца — сегмент i (14 тел, клинок — тело 11).
        Assert.Equal(Enumerable.Range(0, 14), Enumerable.Range(0, BodySegs.Bodies).Select(BodySegs.Segment));
        Assert.All(Enumerable.Range(0, BodySegs.Bodies), i => Assert.Equal(i, BodySegs.Body(BodySegs.Segment(i))));
        Assert.Equal((11, -1, -1), (BodySegs.Body(BodySegs.Weapon), BodySegs.Body(14), BodySegs.Body(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BodySegs.Segment(14));
        Assert.Equal((12, 13, 14, 14, 13), (BodySegs.FootL, BodySegs.FootR, BodySegs.Count, BodySegs.Bodies, BodySegs.Flesh));
        // Плоть — все сегменты, кроме клинка: цели ударов и проб, раны.
        Assert.Equal(BodySegs.Flesh, Enumerable.Range(0, BodySegs.Count).Count(BodySegs.IsFlesh));
        Assert.False(BodySegs.IsFlesh(BodySegs.Weapon) || BodySegs.IsFlesh(-1) || BodySegs.IsFlesh(14));
        Assert.Equal("forearm.l", BodySegs.Name(BodySegs.FArmL));
        Assert.Equal(("левое предплечье", BodySegs.FArmL, BodySegs.Weapon), (BodySegs.Title(BodySegs.FArmL), BodySegs.Find("Forearm.L"), BodySegs.Find("blade")));
        Assert.Equal(-1, BodySegs.Find("tail"));
    }
}
