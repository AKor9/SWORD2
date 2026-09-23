using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;
using Sword.Game.Movement;

namespace Sword.Game.Tests;

/// <summary>Неисправность, которую модуль вносит на тике <see cref="GameFaultModule.Tick"/>.</summary>
public enum GameFault { NaNRoot, NaNPose, LatePoseJob, BrokenPoseRef, SharedLocomotionPose, RootInsideNeighbour, RootsPressed }

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
        Simulation.Build(fault is GameFault.RootInsideNeighbour or GameFault.RootsPressed ? ConfigSet.Empty("arena.enemies=2") : ConfigSet.Empty(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. SwordSimulation.Modules(), new GameFaultModule(fault)]);

    [Theory]
    [InlineData(GameFault.NaNRoot, "ecs.finite", Simulation.ExportStage, "Transform.Position.X = NaN")]
    [InlineData(GameFault.NaNPose, "animation.finite", GameStages.Animation, "поворот (NaN")]
    [InlineData(GameFault.LatePoseJob, "animation.settled", GameStages.Animation, "в очереди пула 1 заданий")]
    [InlineData(GameFault.BrokenPoseRef, "animation.owners", GameStages.End, "поза не жива")]
    [InlineData(GameFault.SharedLocomotionPose, "locomotion.poses", GameStages.End, "ещё и у 0v1")]
    [InlineData(GameFault.RootInsideNeighbour, "movement.rootOverlap", GameStages.Movement, "в 0.1 м: перекрытие 0.6 м — корень в круге соседа (ближе 0.35 м) 5 тиков подряд (можно 4)")]
    [InlineData(GameFault.RootsPressed, "movement.rootOverlap", GameStages.Movement, "в 0.65 м: перекрытие 0.05 м больше 0.02 м 25 тиков подряд (можно 24)")]
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
        var first = sim.Invariants.Recent()[0];
        Assert.Equal((id, GameFaultModule.Tick, stage), (first.Id, first.Tick, first.Stage));
        Assert.Contains(details, first.Details);
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
        Assert.Equal(["ecs.finite", "ecs.entityRefs", "animation.finite", "animation.settled", "animation.pool", "animation.owners", "animation.actions", "arena.slots", "movement.rootOverlap", "locomotion.poses"],
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
}
