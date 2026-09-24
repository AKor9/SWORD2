using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;
using Sword.Game.Movement;

namespace Sword.Game.Tests;

/// <summary>Неисправность, которую модуль вносит на тике <see cref="GameFaultModule.Tick"/>.</summary>
public enum GameFault
{
    NaNRoot, NaNPose, LatePoseJob, BrokenPoseRef, SharedLocomotionPose, RootInsideNeighbour, RootsPressed,
    NaNBody, BrokenBodyRef, SharedBodyRef, OrphanArticulation, FighterWithoutBody, BodyMovedAfterPlacement, BodyJumpBeforePlacement,
    SwappedBodyRefs,
}

/// <summary>
/// Модуль-неисправность в настоящей композиции игры (ядро и Locomotion): на тике <see cref="Tick"/> портит одно свойство,
/// которое держат базовые инварианты — корень игрока в NaN (Movement), поза бойца в NaN (смешивание с весом NaN,
/// Animation), задание пулу поз после его исполнения (Animation),
/// хэндл позы бойца (End), общая поза слоя и снимка локомоции (End); после столкновений (Movement) — корень врага 2 в
/// 0,1 м от врага 1 (в круге соседа) с тика <see cref="Tick"/> − 4 или прижат к нему на 0,65 м (перекрытие 0,05 м) с тика
/// <see cref="Tick"/> − 24: на тике <see cref="Tick"/> — на тик дольше, чем можно (<c>movement.rootOverlap</c>; нужны враги —
/// <c>arena.enemies = 2</c>).
/// </summary>
public sealed class GameFaultModule(GameFault fault) : IModule
{
    public const ulong Tick = 25;

    public string Id => "fault";

    public void Configure(SimulationBuilder b)
    {
        switch (fault)
        {
            case GameFault.NaNRoot: b.System(GameStages.Movement, new NaNRoot()); break;
            case GameFault.NaNPose: b.System(GameStages.Animation, new NaNPose()); break;
            case GameFault.LatePoseJob: b.System(GameStages.Animation, new LatePoseJob()); break;
            case GameFault.BrokenPoseRef: b.System(GameStages.End, new BrokenPoseRef()); break;
            case GameFault.SharedLocomotionPose: b.System(GameStages.End, new SharedLocomotionPose()); break;
            case GameFault.RootInsideNeighbour: b.System(GameStages.Movement, new MoveEnemy(0.1f, Tick - RootOverlapInvariant.HardMaxTicks)); break;
            case GameFault.RootsPressed: b.System(GameStages.Movement, new MoveEnemy(0.65f, Tick - RootOverlapInvariant.MaxTicks)); break;
            case GameFault.NaNBody: b.System(GameStages.Physics, new NaNBody()); break;
            case GameFault.BrokenBodyRef: b.System(GameStages.End, new BodyRefFault(shared: false)); break;
            case GameFault.SharedBodyRef: b.System(GameStages.End, new BodyRefFault(shared: true)); break;
            case GameFault.OrphanArticulation: b.System(GameStages.End, new OrphanArticulation()); break;
            case GameFault.FighterWithoutBody: b.System(GameStages.End, new FighterWithoutBody()); break;
            case GameFault.BodyMovedAfterPlacement:
                b.System(GameStages.PrePhysics, new ShiftPlayerBody(new Vector3(0, 0.1f, 0), after: GameLabels.KinematicPlacement, onlyPelvis: true)); break;
            case GameFault.BodyJumpBeforePlacement:
                b.System(GameStages.Gameplay, new ShiftPlayerBody(new Vector3(5, 0, 0), after: GameLabels.BodySetup, onlyPelvis: false)); break;
            case GameFault.SwappedBodyRefs: b.System(GameStages.End, new SwapBodyRefs()); break;
        }
    }

    private static bool Now(in SystemContext ctx) => ctx.World.Resource<SimTime>().Tick == Tick;

    private sealed class NaNRoot : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.nan-root")).After(GameLabels.RootCollision).Writes<Transform>();
            _query = s.Query().With<PlayerTag>().Write<Transform>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var t in chunk.Write<Transform>()) t.Position.X = float.NaN;
        }
    }

    private sealed class NaNPose : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.nan-pose")).Before(GameLabels.AnimatorPose).Reads<PoseRef>();
            _query = s.Query().Read<PoseRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            var pool = ctx.World.Resource<PosePool>();
            foreach (var chunk in ctx.Iterate(_query))
                foreach (var p in chunk.Read<PoseRef>())
                {
                    var h = new PoseHandle(p.Index, p.Generation);
                    pool.Blend(h, h, float.NaN);
                }
        }
    }

    private sealed class LatePoseJob : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.late-pose-job")).After(GameLabels.AnimationTick).Reads<PoseRef>();
            _query = s.Query().Read<PoseRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            var pool = ctx.World.Resource<PosePool>();
            foreach (var chunk in ctx.Iterate(_query))
                foreach (var p in chunk.Read<PoseRef>()) pool.ToModel(new PoseHandle(p.Index, p.Generation));
        }
    }

    private sealed class BrokenPoseRef : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.broken-pose-ref")).Writes<PoseRef>();
            _query = s.Query().With<PlayerTag>().Write<PoseRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var p in chunk.Write<PoseRef>()) p.Generation += 100;
        }
    }

    /// <summary>После столкновений: враг 2 — в <paramref name="distance"/> м по +X от врага 1, с тика <paramref name="from"/> до <see cref="Tick"/>.</summary>
    private sealed class MoveEnemy(float distance, ulong from) : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.move-enemy")).After(GameLabels.RootCollision).Reads<FighterSlot>().Writes<Transform>();
            _query = s.Query().With<EnemyTag>().Read<FighterSlot>().Write<Transform>();
        }

        public void Run(in SystemContext ctx)
        {
            ulong tick = ctx.World.Resource<SimTime>().Tick;
            if (tick < from || tick > Tick) return;
            Vector3 first = default;
            foreach (var chunk in ctx.Iterate(_query))
            {
                var slots = chunk.Read<FighterSlot>();
                var t = chunk.Write<Transform>();
                for (int i = 0; i < t.Length; i++)
                    if (slots[i].Index == 1) first = t[i].Position;
            }
            foreach (var chunk in ctx.Iterate(_query))
            {
                var slots = chunk.Read<FighterSlot>();
                var t = chunk.Write<Transform>();
                for (int i = 0; i < t.Length; i++)
                    if (slots[i].Index == 2) t[i].Position = first + new Vector3(distance, 0, 0);
            }
        }
    }

    /// <summary>Physics: таз игрока в NaN мимо постановки (этап 5а — постановки ещё нет, 5б).</summary>
    private sealed class NaNBody : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.nan-body")).Reads<BodyRef>();
            _query = s.Query().With<PlayerTag>().Read<BodyRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            var physics = ctx.World.Resource<IPhysicsWorld>();
            foreach (var chunk in ctx.Iterate(_query))
                foreach (var r in chunk.Read<BodyRef>())
                    physics.Place(physics.Body(new ArticulationHandle(r.Index, r.Generation), 0), new Vector3(float.NaN, 1, 0), Quaternion.Identity);
        }
    }

    /// <summary>
    /// Тело игрока сдвинуто на <paramref name="offset"/> без скорости (<see cref="IPhysicsWorld.Place"/>) на тике
    /// <see cref="Tick"/> после <paramref name="after"/>: в PrePhysics после постановки — капсула таза не на суставах
    /// (<c>physics.kinematicPose</c>); в Gameplay до постановки — все капсулы «прилетают» из стороны (как постройка без постановки,
    /// <c>physics.kinematicSpeed</c>).
    /// </summary>
    private sealed class ShiftPlayerBody(Vector3 offset, SystemLabel after, bool onlyPelvis) : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.shift-player-body")).After(after).Reads<BodyRef>();
            _query = s.Query().With<PlayerTag>().Read<BodyRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            var physics = ctx.World.Resource<IPhysicsWorld>();
            foreach (var chunk in ctx.Iterate(_query))
                foreach (var r in chunk.Read<BodyRef>())
                    for (int k = 0; k < (onlyPelvis ? 1 : BodySegs.Bodies); k++)
                    {
                        var b = physics.Body(new ArticulationHandle(r.Index, r.Generation), k);
                        var st = physics.Get(b);
                        physics.Place(b, st.Position + offset, st.Rotation);
                    }
        }
    }

    /// <summary>End: хэндлы тел игрока и врага 1 обменялись — у каждого по одному телу, но чужому (<c>physics.bodyRefs</c> не видит).</summary>
    private sealed class SwapBodyRefs : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.swap-body-refs")).Writes<BodyRef>().Reads<FighterSlot>();
            _query = s.Query().Read<FighterSlot>().Write<BodyRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (ctx.World.Resource<SimTime>().Tick != Tick - 1) return;   // обмен в конце тика 24 — постановка тика 25 его увидит
            BodyRef player = default, enemy = default;
            foreach (var chunk in ctx.Iterate(_query))
            {
                var slots = chunk.Read<FighterSlot>();
                var refs = chunk.Write<BodyRef>();
                for (int i = 0; i < refs.Length; i++)
                    if (slots[i].Index == 0) player = refs[i];
                    else if (slots[i].Index == 1) enemy = refs[i];
            }
            foreach (var chunk in ctx.Iterate(_query))
            {
                var slots = chunk.Read<FighterSlot>();
                var refs = chunk.Write<BodyRef>();
                for (int i = 0; i < refs.Length; i++)
                    if (slots[i].Index == 0) refs[i] = enemy;
                    else if (slots[i].Index == 1) refs[i] = player;
            }
        }
    }

    /// <summary>End: хэндл тела игрока испорчен (поколение +100) или указывает на тело врага 1.</summary>
    private sealed class BodyRefFault(bool shared) : IContinuousSystem
    {
        private Query _player = null!, _enemies = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.body-ref")).Writes<BodyRef>().Reads<FighterSlot>();
            _player = s.Query().With<PlayerTag>().Write<BodyRef>();
            _enemies = s.Query().With<EnemyTag>().Read<FighterSlot>().Read<BodyRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            BodyRef first = default;
            foreach (var chunk in ctx.Iterate(_enemies))
            {
                var slots = chunk.Read<FighterSlot>();
                var refs = chunk.Read<BodyRef>();
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i].Index == 1) first = refs[i];
            }
            foreach (var chunk in ctx.Iterate(_player))
                foreach (ref var r in chunk.Write<BodyRef>())
                    if (shared) r = first;
                    else r.Generation += 100;
        }
    }

    /// <summary>End: сочленение мимо хука — его никто не освободит.</summary>
    private sealed class OrphanArticulation : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("fault.orphan-articulation"));

        public void Run(in SystemContext ctx)
        {
            if (Now(ctx)) ctx.World.Resource<IPhysicsWorld>().CreateArticulation(2);
        }
    }

    /// <summary>End: у игрока снят <see cref="BodyRef"/> (хук освобождает тело) — боец без тела.</summary>
    private sealed class FighterWithoutBody : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.fighter-without-body")).Removes<BodyRef>();
            _query = s.Query().With<PlayerTag>().With<BodyRef>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (var e in chunk.Entities) ctx.Commands.Remove<BodyRef>(e);
        }
    }

    private sealed class SharedLocomotionPose : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.shared-locomotion-pose")).Writes<LegacyLocomotionState>();
            _query = s.Query().Write<LegacyLocomotionState>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var st in chunk.Write<LegacyLocomotionState>()) st.From = st.Layer;
        }
    }
}

/// <summary>
/// Инварианты игры (этап 3г): базовые инварианты движка и анимации и инвариант модуля Locomotion ловят
/// модуль-неисправность на её тике, после нужной стадии; честная игра их не нарушает.
/// </summary>
public sealed class GameInvariantTests
{
    private static Simulation Build(GameFault fault) =>
        Simulation.Build(fault is GameFault.RootInsideNeighbour or GameFault.RootsPressed or GameFault.SharedBodyRef or GameFault.SwappedBodyRefs
                ? ConfigSet.Empty("arena.enemies=2") : ConfigSet.Empty(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. SwordSimulation.Modules(), new GameFaultModule(fault)]);

    [Theory]
    [InlineData(GameFault.NaNRoot, "ecs.finite", Simulation.ExportStage, "Transform.Position.X = NaN")]
    [InlineData(GameFault.NaNPose, "animation.finite", GameStages.Animation, "поворот (NaN")]
    [InlineData(GameFault.LatePoseJob, "animation.settled", GameStages.Animation, "в очереди пула 1 заданий")]
    [InlineData(GameFault.BrokenPoseRef, "animation.owners", GameStages.End, "поза не жива")]
    [InlineData(GameFault.SharedLocomotionPose, "locomotion.poses", GameStages.End, "ещё и у 0v1")]
    [InlineData(GameFault.RootInsideNeighbour, "movement.rootOverlap", GameStages.Movement, "в 0.1 м: перекрытие 0.6 м — корень в круге соседа (ближе 0.35 м) 5 тиков подряд (можно 4)")]
    [InlineData(GameFault.RootsPressed, "movement.rootOverlap", GameStages.Movement, "в 0.65 м: перекрытие 0.05 м больше 0.02 м 25 тиков подряд (можно 24)")]
    [InlineData(GameFault.NaNBody, "physics.finite", GameStages.Physics, "положение (NaN, 1, 0)")]
    [InlineData(GameFault.BrokenBodyRef, "physics.bodyRefs", GameStages.End, "сочленение не живо")]
    [InlineData(GameFault.SharedBodyRef, "physics.bodyRefs", GameStages.End, "— ещё и у")]
    [InlineData(GameFault.OrphanArticulation, "physics.bodyRefs", GameStages.End, "(2 тел) без владельца")]
    [InlineData(GameFault.FighterWithoutBody, "physics.bodyRefs", GameStages.End, "боец без тела (BodyRef)")]
    [InlineData(GameFault.BodyMovedAfterPlacement, "physics.kinematicPose", GameStages.PrePhysics, "капсула pelvis (таз) не на суставах позы: центр в 0.1 м")]
    [InlineData(GameFault.BodyJumpBeforePlacement, "physics.kinematicSpeed", GameStages.PrePhysics, "капсула pelvis (таз): скорость относительно бойца 600")]
    [InlineData(GameFault.SwappedBodyRefs, "physics.kinematicSpeed", GameStages.PrePhysics, "скорость относительно бойца")]
    public void Fault_CaughtOnItsTick_AfterItsStage(GameFault fault, string id, string stage, string details)
    {
        var sim = Build(fault);
        Assert.Contains(sim.Invariants.List(), i => i.Id == id && i.Enabled);
        for (int t = 0; t < (int)GameFaultModule.Tick; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
            sim.Step();
        }
        Assert.Equal(0, sim.Invariants.Total);                           // до неисправности — чисто
        sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
        sim.Step();
        // Нарушение этого инварианта — на тике неисправности после своей стадии (раньше него в том же тике может сработать
        // другой: NaN корня с 5б доходит до капсул и physics.finite видит его после Physics, до ecs.finite в конце тика).
        var first = sim.Invariants.Recent().First(v => v.Id == id);
        Assert.Equal((id, GameFaultModule.Tick, stage), (first.Id, first.Tick, first.Stage));
        // Подробность — у одного из нарушений этой проверки (NaN корня с 6б уходит и в клинок: ecs.finite называет и Blade).
        Assert.Contains(sim.Invariants.Recent(), v => v.Id == id && v.Tick == first.Tick && v.Details.Contains(details));
    }

    /// <summary>
    /// Живая игра (исправления после Пд, решение пользователя): тяжёлые по цене в тике толпы — <c>animation.finite</c>,
    /// <c>ecs.finite</c>, <c>physics.finite</c>, <c>physics.kinematicPose</c>, <c>physics.kinematicSpeed</c> — раз в 4 тика, остальные
    /// (и <c>movement.rootOverlap</c> со счётом тиков подряд) — каждый тик. Неисправность, которая держится (NaN корня с тика 25),
    /// поймана не позже чем через 3 тика — на тике 28; в тестах (не живая игра) — на тике 25 (<see cref="Fault_CaughtOnItsTick_AfterItsStage"/>).
    /// </summary>
    [Fact]
    public void LiveGame_HeavyEveryFourTicks_PersistentFaultCaughtWithinPeriod()
    {
        string[] heavy = ["ecs.finite", "animation.finite", "physics.finite", "physics.kinematicPose", "physics.kinematicSpeed"];
        var sim = Build(GameFault.NaNRoot);
        Assert.All(sim.Invariants.List().Where(i => i.Enabled), i => Assert.Equal(1, i.Every));   // не живая игра — каждый тик
        sim.Invariants.LivePeriods = true;
        foreach (var i in sim.Invariants.List().Where(i => i.Enabled))
            Assert.True(i.Every == (heavy.Contains(i.Id) ? InvariantCost.HeavyPeriod : 1), $"{i.Id}: раз в {i.Every}");
        for (int t = 0; t <= (int)GameFaultModule.Tick + 3; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
            sim.Step();
        }
        var first = sim.Invariants.Recent().First(v => v.Id == "ecs.finite");
        Assert.Equal(GameFaultModule.Tick + 3, first.Tick);   // 28 — ближайшая проверка раз в 4 после тика 25
        // К тику 28 NaN корня разошёлся по клинку (6б) и прошлому корню — подробностей больше 8, корня среди них может не быть.
        Assert.Contains("= NaN", first.Details);
        Assert.Equal(8, sim.Invariants.Find("ecs.finite")!.Evaluations);              // тики 0, 4, …, 28
        Assert.Equal(29, sim.Invariants.Find("movement.rootOverlap")!.Evaluations);   // каждый тик: счёт тиков подряд
    }

    [Fact]
    public void RootOverlap_OnDemandRuns_DoNotAdvanceTicksInARow()
    {
        // Пара прижата с тика 1: после тика 24 — 24 тика подряд (можно). Проверки по запросу между тиками тот же мир
        // не считают новым тиком; нарушение — ровно на плановой проверке тика 25.
        var sim = Build(GameFault.RootsPressed);
        for (int t = 0; t < (int)GameFaultModule.Tick; t++) sim.Step();
        for (int k = 0; k < 5; k++)
            Assert.All(sim.Invariants.Run(["movement.rootOverlap"], sim.Tick, "между тиками", sim.Dt), r => Assert.Equal(0, r.Violations));
        Assert.Equal(0, sim.Invariants.Total);
        sim.Step();
        var first = sim.Invariants.Recent()[0];
        Assert.Equal(("movement.rootOverlap", GameFaultModule.Tick), (first.Id, first.Tick));
        Assert.Contains("25 тиков подряд", first.Details);
        // По запросу после нарушения — то же нарушение (счёт не сдвинулся до 26).
        var again = sim.Invariants.Run(["movement.rootOverlap"], sim.Tick, "между тиками", sim.Dt);
        Assert.Contains("25 тиков подряд", Assert.Single(again).Details[0].Details);
    }

    [Fact]
    public void FairGame_NoViolations_AllCoreInvariantsChecked()
    {
        var sim = TestAssets.Simulation();
        Assert.Equal(["ecs.finite", "ecs.entityRefs", "animation.finite", "animation.settled", "animation.pool", "animation.owners", "animation.actions", "arena.slots", "movement.rootOverlap", "physics.finite", "physics.kinematicPose", "physics.kinematicSpeed", "physics.bodyRefs", "combat.blade", "combat.weapons", "locomotion.poses"],
            sim.Invariants.List().Select(i => i.Id));
        for (int t = 0; t < 400; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = t < 200 ? new Vector2(0, 1) : new Vector2(-1, 0), Walk = t > 300 });
            sim.Step();
        }
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent()));
        Assert.All(sim.Invariants.List().Where(i => i.Enabled), i => Assert.Equal(400, i.Evaluations));
        Assert.DoesNotContain(sim.Invariants.Run(["ecs.entityRefs"], sim.Tick, "между тиками", sim.Dt), r => r.Violations > 0);
    }

    /// <summary>Модуль, который после постановки тел сборки мира сдвигает таз игрока на 10 см (до первого тика).</summary>
    private sealed class ShiftBuiltBody : IModule
    {
        public string Id => "shift_built";

        public void Configure(SimulationBuilder b) => b.OnWorldReady(w =>
        {
            var physics = w.Resource<Sword.Engine.Physics.IPhysicsWorld>();
            var body = physics.Body(Sword.Game.Bodies.FighterBodies.Handle(w.Get<BodyRef>(GameCore.Player(w.Reader))), BodySegs.Body(BodySegs.Pelvis));
            var s = physics.Get(body);
            physics.Place(body, s.Position + new Vector3(0.1f, 0, 0), s.Rotation);
        });
    }

    /// <summary>
    /// 5в: до первого тика тела бойцов сборки мира уже стоят (по позе постройки), и проверка «сейчас» их сверяет, а не
    /// пропускает: тело, сдвинутое после постановки сборки мира, — нарушение <c>physics.kinematicPose</c> на тике 0.
    /// </summary>
    [Fact]
    public void RunNow_BeforeFirstTick_ChecksWorldBuiltBodies()
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=2"), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new ShiftBuiltBody()]);
        var result = Assert.Single(sim.Invariants.Run(["physics.kinematicPose"], sim.Tick, "между тиками", sim.Dt));
        var violation = Assert.Single(result.Details);
        Assert.Equal(GameCore.Player(sim.World), violation.Entity);
        Assert.Contains("капсула pelvis (таз) не на суставах позы: центр в 0.1 м", violation.Details);
    }

    /// <summary>
    /// Проверка «сейчас» (<c>invariant.run</c> агента) до первого тика (свежий сеанс, <c>time.seek 0</c>) и посреди тика после
    /// каждой стадии (<c>time.break</c>) — все инварианты чисты: тела бойцов сборки мира стоят по позе постройки (5в) и
    /// сверяются с ней, а посреди тика до PrePhysics (корень, курс и поза уже новые, капсулы — ещё прошлого тика)
    /// <c>physics.kinematicPose</c> капсулы с позой не сверяет.
    /// </summary>
    [Fact]
    public void RunNow_BeforeFirstTickAndMidTick_NoViolations()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=12"));
        var results = sim.Invariants.Run(null, sim.Tick, "между тиками", sim.Dt);
        Assert.Contains(results, r => r.Id == "physics.kinematicPose");
        Assert.True(results.All(r => r.Violations == 0), string.Join("\n", results.SelectMany(r => r.Details)));
        var probe = new RunNowProbe();
        sim.Observe(probe);
        for (int t = 0; t < 30; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0.5f) });
            sim.Step();
            results = sim.Invariants.Run(null, sim.Tick, "между тиками", sim.Dt);
            Assert.True(results.All(r => r.Violations == 0), string.Join("\n", results.SelectMany(r => r.Details)));
        }
        Assert.Equal(30 * GameStages.All.Count, probe.Runs);
        Assert.True(probe.Violations.Count == 0, string.Join("\n", probe.Violations));
    }

    /// <summary>После барьера каждой стадии — все инварианты «сейчас», как <c>invariant.run</c> после <c>time.break</c>.</summary>
    private sealed class RunNowProbe : ISimulationObserver
    {
        public int Runs;
        public readonly List<InvariantViolation> Violations = [];

        public void AfterStage(Simulation simulation, int stage)
        {
            string name = simulation.World.Stages[stage];
            if (!GameStages.All.Contains(name)) return;
            Runs++;
            foreach (var r in simulation.Invariants.Run(null, simulation.Tick, name, simulation.Dt)) Violations.AddRange(r.Details);
        }

        public void AfterTick(Simulation simulation)
        {
        }
    }
}
