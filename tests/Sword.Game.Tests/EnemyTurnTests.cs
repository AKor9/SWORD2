using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Contracts;
using Sword.Game.Data;
using Sword.Game.Movement;

namespace Sword.Game.Tests;

/// <summary>
/// Поворот врага на месте — слой действий (<see cref="ActionLayer"/>), ветка не-<c>Agile</c> (<see cref="RootSteerSystem.SteerSteady"/>),
/// команда врагу, инвариант <c>animation.actions</c>. Клипы поворота — 90° влево, вправо и разворот (<see cref="TestAssets"/>).
/// </summary>
public sealed class EnemyTurnTests
{
    private static Simulation Create(int enemies = 1, params string[] sets) =>
        TestAssets.Simulation(ConfigSet.Empty([$"arena.enemies={enemies}", .. sets]));

    private static Entity Enemy(Simulation sim, int slot = 1)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
        {
            var slots = chunk.Read<FighterSlot>();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Index == slot) return chunk.Entities[i];
        }
        throw new InvalidOperationException($"нет врага на месте {slot}");
    }

    private static void Step(Simulation sim, float goal, Vector2 velocity = default, int slot = 1)
    {
        sim.Enqueue(new EnemyCommand { Slot = slot, Velocity = velocity, FacingGoal = goal });
        sim.Step();
    }

    private static float Lag(float goal, float facing) => MathF.Abs(MathF.IEEERemainder(goal - facing, MathF.Tau));

    [Fact]
    public void TurnSlots_MapToTurnClips()
    {
        // Слоты поворота таблицы действий и клипы, которые им назначены по именам пака.
        Assert.Equal(("turn_left", "turn_right", "turn_180"),
            (ActionSlots.Names[ActionSlots.TurnLeft], ActionSlots.Names[ActionSlots.TurnRight], ActionSlots.Names[ActionSlots.Turn180]));
        var a = TestAssets.Assets;
        Assert.Equal(("sword_1h/1HM_TurnLeft90", "sword_1h/1HM_TurnRight90", "sword_1h/1HM_Turn180"),
            (a.Clips.Clips[a.Actions.TurnLeft].Name, a.Clips.Clips[a.Actions.TurnRight].Name, a.Clips.Clips[a.Actions.Turn180].Name));
    }

    [Theory]
    [InlineData(1.6f, "TurnLeft90")]      // 90° влево, растяжение 1,02
    [InlineData(-1.3f, "TurnRight90")]    // 90° вправо, 0,83
    [InlineData(2.2f, "TurnLeft90")]      // 1,40 (|ln| 0,34) ближе, чем разворот 0,70 (0,36)
    [InlineData(2.3f, "Turn180")]         // 1,46 (0,38) дальше, чем разворот 0,73 (0,31)
    [InlineData(2.6f, "Turn180")]         // 90° растянуть на 1,66 нельзя
    [InlineData(-3.0f, "Turn180")]        // разворот — в обе стороны
    [InlineData(0.79f, "TurnLeft90")]     // на грани 0,5
    [InlineData(0.7f, null)]              // 0,45 и 0,22 — клипа нет, курс доворачивает контроллер
    [InlineData(-0.7f, null)]
    public void StartTurn_PicksClipByScale(float delta, string? clip)
    {
        var assets = TestAssets.Assets;
        var a = AnimatorAction.None;
        bool started = ActionLayer.StartTurn(ref a, delta, assets.Clips, assets.Actions);
        Assert.Equal(clip is not null, started);
        if (clip is null)
        {
            Assert.Equal(AnimatorAction.None, a);
            return;
        }
        Assert.Equal(ActionKind.Turn, a.Kind);
        Assert.Equal("sword_1h/1HM_" + clip, assets.Clips.Clips[a.Clip].Name);
        Assert.Equal(delta / assets.Clips.RootYawAt(a.Clip, assets.Clips.Duration(a.Clip)), a.TurnScale);
        Assert.Equal((0f, 1f, 0f), (a.Time, a.Duration, a.Weight));
    }

    [Fact]
    public void Standing_GoalJumps90_TurnsByClip_YawDrivesFacing_ThenStandsOnGoal()
    {
        var sim = Create();
        var e = Enemy(sim);
        float goal = sim.World.Get<Transform>(e).Facing + 1.6f;
        var start = sim.World.Get<Transform>(e).Position;
        Step(sim, goal);
        var a = sim.World.Get<AnimatorAction>(e);
        Assert.Equal(ActionKind.Turn, a.Kind);
        Assert.Equal(TestAssets.Assets.Actions.TurnLeft, a.Clip);
        var layer = sim.World.Get<ActionPoses>(e).Layer;
        Assert.True(sim.World.View<IAnimationView>().Model(new PoseRef { Index = layer.Index, Generation = layer.Generation }).Length > 0);

        float weight = a.Weight, facing = sim.World.Get<Transform>(e).Facing;
        bool falling = false;
        int driven = 0;
        for (int t = 0; t < 200 && sim.World.Get<AnimatorAction>(e).Kind == ActionKind.Turn; t++)
        {
            Step(sim, goal);
            a = sim.World.Get<AnimatorAction>(e);
            float now = sim.World.Get<Transform>(e).Facing;
            if (a.Kind != ActionKind.Turn) break;
            // стоит — курс ведёт только yaw клипа: контроллер не крутит
            Assert.Equal(a.YawDelta, now - facing, 5);
            Assert.True(a.YawDelta >= 0);
            driven += a.YawDelta > 0 ? 1 : 0;
            // вес: вход до 1 за 0,15 с, выход — последние 0,2 с клипа
            if (a.Weight < weight) falling = true;
            else Assert.False(falling, "вес вырос после начала выхода");
            Assert.InRange(a.Weight, 0, 1);
            (weight, facing) = (a.Weight, now);
        }
        Assert.Equal(ActionKind.None, sim.World.Get<AnimatorAction>(e).Kind);
        Assert.True(driven > 100, $"yaw клипа вёл курс {driven} тиков");
        Assert.True(Lag(goal, sim.World.Get<Transform>(e).Facing) < 0.02f, "после клипа курс почти на цели");
        for (int t = 0; t < 30; t++) Step(sim, goal);
        Assert.True(Lag(goal, sim.World.Get<Transform>(e).Facing) < 1e-4f, "остаток доворачивает контроллер стоя");
        // сдвиг корня клипом (у тестового клипа 0,1 м вперёд) — с весом слоя, по курсу
        float moved = Vector3.Distance(start, sim.World.Get<Transform>(e).Position);
        Assert.InRange(moved, 0.01f, TestAssets.TurnDrift);
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Standing_GoalJumpsUnder45_TurnsAtIdleRate()
    {
        var sim = Create();
        var e = Enemy(sim);
        float goal = sim.World.Get<Transform>(e).Facing + 0.5f;
        for (int t = 0; t < 10; t++)
        {
            float before = sim.World.Get<Transform>(e).Facing;
            Step(sim, goal);
            Assert.Equal(MovementRules.IdleTurnRate / 120, sim.World.Get<Transform>(e).Facing - before, 5);
            Assert.Equal(ActionKind.None, sim.World.Get<AnimatorAction>(e).Kind);
        }
    }

    [Fact]
    public void Walking_GoalJumps90_TurnsByController_NoClip()
    {
        var sim = Create();
        var e = Enemy(sim);
        float facing0 = sim.World.Get<Transform>(e).Facing;
        var velocity = new Vector2(1.5f, 0);
        for (int t = 0; t < 60; t++) Step(sim, facing0, velocity);
        float goal = facing0 + 1.6f;
        for (int t = 0; t < 10; t++)
        {
            float before = sim.World.Get<Transform>(e).Facing;
            Step(sim, goal, velocity);
            Assert.Equal(MovementRules.TurnRate / 120, sim.World.Get<Transform>(e).Facing - before, 5);
            Assert.Equal(ActionKind.None, sim.World.Get<AnimatorAction>(e).Kind);
        }
    }

    [Fact]
    public void TurnInterruptedByWalking_ClipFadesWithoutYaw_ControllerTurns()
    {
        var sim = Create();
        var e = Enemy(sim);
        float goal = sim.World.Get<Transform>(e).Facing + 1.6f;
        for (int t = 0; t < 30; t++) Step(sim, goal);
        Assert.Equal(ActionKind.Turn, sim.World.Get<AnimatorAction>(e).Kind);
        float peak = sim.World.Get<AnimatorAction>(e).Weight;
        var velocity = new Vector2(-1.2f, -1.2f);
        int t2 = 0;
        while (!sim.World.Get<AnimatorMotion>(e).Moving && t2++ < 60) Step(sim, goal, velocity);
        Assert.True(sim.World.Get<AnimatorMotion>(e).Moving);
        int fading = 0;
        while (sim.World.Get<AnimatorAction>(e).Kind == ActionKind.Turn && fading < 60)
        {
            var a = sim.World.Get<AnimatorAction>(e);
            Assert.Equal(0f, a.YawDelta);          // пошёл — клип курс не ведёт
            Assert.True(a.Weight < peak);          // и гаснет за 0,2 с
            Step(sim, goal, velocity);
            fading++;
        }
        Assert.Equal(ActionKind.None, sim.World.Get<AnimatorAction>(e).Kind);
        Assert.InRange(fading, 1, 24);
        // курс доворачивает контроллер на ходу — 12 рад/с
        float before = sim.World.Get<Transform>(e).Facing;
        Step(sim, goal, velocity);
        float turned = sim.World.Get<Transform>(e).Facing - before;
        Assert.True(Lag(goal, before) < MovementRules.TurnRate / 120 || MathF.Abs(turned - MovementRules.TurnRate / 120) < 1e-5f, $"{turned}");
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Player_IsAgile_NoActionLayer_TurnsAtFullRateStanding()
    {
        var sim = Create();
        var p = GameCore.Player(sim.World);
        // Слой действий есть и у игрока, но без действий: поворот — контроллером, клипом — никогда.
        Assert.Equal(AnimatorAction.None, sim.World.Get<AnimatorAction>(p));
        Assert.True(sim.World.Get<ActionPoses>(p).Layer.IsNone);
        // взгляд назад стоя: 12 рад/с сразу, без клипа
        sim.Enqueue(new PlayerCommand { HasGaze = true, Gaze = new Vector3(0, 0, -10) });
        sim.Step();
        Assert.Equal(MovementRules.TurnRate / 120, MathF.Abs(sim.World.Get<Transform>(p).Facing), 5);
    }

    [Fact]
    public void EnemyCommand_OverridesAi_LastWins_NonFiniteFallsBack_AbsentSlotIgnored()
    {
        var sim = Create(2);
        var twin = Create(2);
        var e = Enemy(sim);
        sim.Enqueue(new EnemyCommand { Slot = 1, Velocity = new Vector2(1, 0), FacingGoal = 1 });
        sim.Enqueue(new EnemyCommand { Slot = 1, Velocity = new Vector2(0, 2), FacingGoal = 2 });
        sim.Enqueue(new EnemyCommand { Slot = 7, Velocity = new Vector2(5, 5), FacingGoal = 3 });
        sim.Step();
        twin.Step();
        var intent = sim.World.Get<FighterCommand>(e);
        Assert.Equal((new Vector3(0, 0, 2), 2f), (intent.DesiredVelocity, intent.FacingGoal));
        // второй враг — ИИ, как без команд
        Assert.Equal(twin.World.Get<FighterCommand>(Enemy(twin, 2)), sim.World.Get<FighterCommand>(Enemy(sim, 2)));

        sim.Enqueue(new EnemyCommand { Slot = 1, Velocity = new Vector2(float.NaN, 0), FacingGoal = float.PositiveInfinity });
        sim.Step();
        intent = sim.World.Get<FighterCommand>(e);
        Assert.Equal(Vector3.Zero, intent.DesiredVelocity);
        var ai = EnemyAi.Think(1, sim.World.Get<PrevTransform>(e).Position, sim.World.Get<FighterSeed>(e).Value,
            sim.World.Get<PrevTransform>(GameCore.Player(sim.World)).Position, sim.World.Resource<SimTime>().Time, [], [], out _);
        Assert.Equal(ai.FacingGoal, intent.FacingGoal);
    }

    [Fact]
    public void EnemyCommand_InArenaCommandTick_StillWins_AndIsRecordedInReplay()
    {
        var sim = Create(3);
        var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
        for (int t = 0; t < 200; t++)
        {
            if (t == 50) sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
            if (t == 80) sim.Enqueue(new ArenaCommand { Action = ArenaAction.ChangeEnemies, Enemies = 1 });
            sim.Enqueue(new EnemyCommand { Slot = 1 + t % 4, Velocity = t % 60 < 30 ? Vector2.Zero : new Vector2(0.5f, 0), FacingGoal = t / 40 * 1.7f });
            sim.Step();
            if (t is 50 or 80 && 1 + t % 4 is var slot && slot <= sim.World.Resource<ArenaState>().Enemies)
                Assert.Equal(t / 40 * 1.7f, sim.World.Get<FighterCommand>(Enemy(sim, slot)).FacingGoal);
        }
        Assert.Equal(0, sim.Invariants.Total);
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        Assert.Contains(replay.CommandTypes, c => c.Name.EndsWith(nameof(EnemyCommand), StringComparison.Ordinal));
        var again = TestAssets.Simulation(replay.CreateConfig());
        var played = again.Play(replay);
        while (again.Tick < replay.Ticks) again.Step();
        Assert.Null(played.Divergence);
        Assert.Equal(201, played.HashesChecked);
    }

    [Fact]
    public void Reset_MidTurn_ClearsAction_KeepsPose_Removal_ReturnsPose()
    {
        var sim = Create();
        var e = Enemy(sim);
        float goal = sim.World.Get<Transform>(e).Facing + 3f;
        for (int t = 0; t < 20; t++) Step(sim, goal);
        Assert.Equal(ActionKind.Turn, sim.World.Get<AnimatorAction>(e).Kind);
        var layer = sim.World.Get<ActionPoses>(e).Layer;
        var view = sim.World.View<IAnimationView>();
        Assert.Equal(3 + 4, view.PoseCount);   // игрок: поза, слой и снимок локомоции; враг — ещё поза слоя действий

        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        sim.Step();
        Assert.Equal(e, Enemy(sim));
        var a = sim.World.Get<AnimatorAction>(e);
        Assert.Equal((ActionKind.None, -1, 0f), (a.Kind, a.Clip, a.Weight));
        Assert.Equal(layer, sim.World.Get<ActionPoses>(e).Layer);
        Assert.Equal(7, view.PoseCount);

        sim.Enqueue(new ArenaCommand { Action = ArenaAction.SetEnemies, Enemies = 0 });
        sim.Step();
        Assert.Equal(3, view.PoseCount);
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void SteadyTick_32EnemiesTurning_DoesNotAllocate()
    {
        var sim = Create(32);
        int turns = 0;
        void Tick(int t)
        {
            sim.Export.BeginFrame();
            for (int slot = 1; slot <= 32; slot++)
                sim.Enqueue(new EnemyCommand { Slot = slot, FacingGoal = (t / 150 + slot) % 3 * 2.1f });
            sim.Step();
        }
        // прогрев — все три курса-цели: поза слоя действия берётся при первом повороте врага (пул растёт однократно)
        for (int t = 0; t < 450; t++) Tick(t);
        long allocated = 0;
        for (int t = 450; t < 1050; t++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Tick(t);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            if (t % 150 == 1) turns += Count(sim);   // запрос мира выделяет — вне замера
        }
        Assert.Equal(0, allocated);
        Assert.True(turns > 32, $"поворотов клипом {turns}");
        Assert.Equal(0, sim.Invariants.Total);

        static int Count(Simulation s)
        {
            int n = 0;
            foreach (var chunk in s.World.Iterate(s.World.Query().Read<AnimatorAction>()))
                foreach (var a in chunk.Read<AnimatorAction>())
                    if (a.Kind == ActionKind.Turn) n++;
            return n;
        }
    }

    // ── инвариант animation.actions ─────────────────────────────────────────────────────────────────────────────────

    public enum Fault { Weight, WrongClip, NoLayer }

    private sealed class FaultModule(Fault fault) : IModule
    {
        public string Id => "fault";

        public void Configure(SimulationBuilder b) => b.System(GameStages.Movement, new Break(fault));

        private sealed class Break(Fault fault) : IContinuousSystem
        {
            private Query _query = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("fault.action")).After(GameLabels.RootCollision).Writes<AnimatorAction>().Removes<AnimatorAction>();
                _query = s.Query().With<EnemyTag>().Write<AnimatorAction>();
            }

            public void Run(in SystemContext ctx)
            {
                if (ctx.World.Resource<SimTime>().Tick != 25) return;
                foreach (var chunk in ctx.Iterate(_query))
                {
                    var a = chunk.Write<AnimatorAction>();
                    for (int i = 0; i < a.Length; i++)
                        switch (fault)
                        {
                            case Fault.Weight: a[i].Weight = 2; break;
                            case Fault.WrongClip: (a[i].Kind, a[i].Clip, a[i].TurnScale) = (ActionKind.Turn, 0, 1); break;
                            case Fault.NoLayer: ctx.Commands.Remove<AnimatorAction>(chunk.Entities[i]); break;
                        }
                }
            }
        }
    }

    [Theory]
    [InlineData(Fault.Weight, "вес действия 2")]
    [InlineData(Fault.WrongClip, "не клип поворота")]
    [InlineData(Fault.NoLayer, "без слоя действий")]
    public void Invariant_CatchesBrokenActionLayer_OnItsTick(Fault fault, string expected)
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=1"), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new FaultModule(fault)]);
        for (int t = 0; t < 25; t++) sim.Step();
        Assert.Equal(0, sim.Invariants.Total);
        sim.Step();
        var first = sim.Invariants.Recent()[0];
        Assert.Equal(("animation.actions", 25ul), (first.Id, first.Tick));
        Assert.Contains(expected, first.Details);
    }
}
