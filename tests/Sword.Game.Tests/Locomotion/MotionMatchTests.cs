using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Motion Matching игрока в тике (8е; категория Content — клипы GASP есть только в паке; <c>locomotion.player = mm</c>): игрок на MM, враги на
/// клипах; поиск по расписанию (целое число тиков) и по событиям (скачок цели, конец действия всем телом); предсказание траектории = N тиков
/// настоящего контроллера ядра (одна правда — <see cref="SteerModel"/>); R, смерть и постройка заново — состояние с нуля, мёртвый не
/// трогается; ошибка данных — ошибка старта; <c>clips</c> — ничего нового (побитово — <see cref="ReferenceReplaysTests"/>); повтор реплея — те
/// же хеши; 0 Б в тике, пул не растёт на переходах; первый тик — поза постройки; сценарий «бег по кругу 8 направлений» — числа в вывод.
/// </summary>
[Trait("Category", "Content")]
public sealed class MotionMatchTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Animation.CharacterAssets Assets => FeatureManifestsTests.Reference.Value.Assets;

    private static Simulation Create(params string[] sets) =>
        SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm", .. sets]), Assets);

    private static GaspMotionBase Base => GaspMotionBase.For(Assets.Clips);

    private static void Step(Simulation sim, in PlayerCommand command)
    {
        sim.Export.BeginFrame();
        sim.Enqueue(command);
        sim.Step();
    }

    private static void Step(Simulation sim, int ticks, PlayerCommand command = default)
    {
        for (int t = 0; t < ticks; t++) Step(sim, command);
    }

    private static MotionMatchState State(Simulation sim) => sim.World.Get<MotionMatchState>(GameCore.Player(sim.World));

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    private static readonly PlayerCommand RunForward = new() { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 200) };

    /// <summary>Бег по кругу 8 направлений: направление хода меняется через 0,5 с (0°, 45°, … по часовой), взгляд — вперёд (+Z) издалека.</summary>
    private static PlayerCommand Circle(int tick)
    {
        float a = tick / 60 % 8 * MathF.PI / 4;
        return new PlayerCommand { Move = new Vector2(MathF.Sin(a), MathF.Cos(a)), HasGaze = true, Gaze = new Vector3(0, 0, 60) };
    }

    [Fact]
    public void Player_OnMotionMatching_EnemiesOnClips()
    {
        var sim = Create("arena.enemies=3", "combat.enemiesAttack=false");
        Step(sim, 120, RunForward);
        var player = GameCore.Player(sim.World);
        Assert.True(sim.World.Has<MotionMatchState>(player));
        Assert.False(sim.World.Has<LegacyLocomotionState>(player));   // одну позу бойца пишет одна система
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>()))
            foreach (var e in chunk.Entities)
            {
                Assert.True(sim.World.Has<LegacyLocomotionState>(e));
                Assert.False(sim.World.Has<MotionMatchState>(e));
                Assert.False(sim.World.Has<FighterStartPose>(e));   // враги строятся по общей позе постройки
            }
        Assert.Contains("locomotion", sim.StateParts);
        Assert.Equal(5 + GaspMotion.WeightKeys.Count, sim.Tuning.Count(k => k.Section == LocomotionKnobs.Section));   // 8ж: + вес руки
        var s = State(sim);
        var view = sim.World.View<IAnimationView>();
        string clip = view.ClipName(s.Clip);
        output.WriteLine(string.Create(Inv, $"после 1 с бега: клип {clip}, кадр в клипе {s.ClipFrame}, поисков {s.Searches}, смен {s.Switches}, переходов {s.Transitions}, темп {s.Rate:F3}"));
        Assert.StartsWith("locomotion/", clip);
        Assert.Contains("Run", clip);
        Assert.True(s.Searches >= 9 && s.Switches >= 1, $"поисков {s.Searches}, смен {s.Switches}");
        Clean(sim);
    }

    [Fact]
    public void ClipsMode_RegistersNothingOfMotionMatching()
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty(), Assets);
        Step(sim, 2, RunForward);
        var player = GameCore.Player(sim.World);
        Assert.Equal(["animation", "ecs", "physics", "skin"], sim.StateParts);
        Assert.False(sim.World.Knows<MotionMatchState>());
        Assert.DoesNotContain(sim.Tuning, k => k.Section == LocomotionKnobs.Section);
        Assert.True(sim.World.Has<LegacyLocomotionState>(player));
        Assert.False(sim.World.Has<FighterStartPose>(player));
        Assert.False(sim.World.Has<SteerModel>(player));
        // явное clips — тот же мир, что без секции
        var explicitClips = SwordSimulation.Create(ConfigSet.Empty("locomotion.player=clips"), Assets);
        Step(explicitClips, 2, RunForward);
        Assert.Equal(sim.HashState().Total, explicitClips.HashState().Total);
    }

    /// <summary>
    /// Первый тик после постройки — ровно поза постройки игрока (<see cref="FighterStartPose"/> — кадр стойки базы): по ней сборка мира
    /// поставила капсулы и клинок, поэтому <c>physics.kinematicSpeed</c> и <c>physics.kinematicPose</c> не срабатывают ни до первого тика, ни в
    /// нём; враги построены по общей позе. То же после R посреди бега.
    /// </summary>
    [Fact]
    public void FirstTick_IsBuildPose_NoKinematicJump()
    {
        var sim = Create("arena.enemies=2", "combat.enemiesAttack=false");
        var player = GameCore.Player(sim.World);
        var start = sim.World.Get<FighterStartPose>(player);
        Assert.Equal(0, start.Time);
        Assert.Contains("Stand_Idle", sim.World.View<IAnimationView>().ClipName(start.Clip));
        Assert.All(sim.Invariants.Run(["physics.kinematicPose", "combat.blade"], 0, "—", sim.Dt), r => Assert.Equal(0, r.Violations));
        Step(sim, 1);
        Assert.True(MaxJointOffset(sim, player, start) < 1e-5f, $"поза тика 1 отошла от позы постройки на {MaxJointOffset(sim, player, start)} м");
        Clean(sim);
        Step(sim, 200, RunForward);
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim, 1, RunForward);
        Assert.True(MaxJointOffset(sim, player, start) < 1e-5f, "после R поза тика — не поза постройки");
        Step(sim, 60, RunForward);
        Clean(sim);
    }

    /// <summary>
    /// Наибольшее расстояние суставов итоговой позы игрока от позы постройки <paramref name="start"/> (модельное пространство; 8ж — со слоем
    /// руки с мечом: матрицы — те же, что у постройки тела в ядре).
    /// </summary>
    private static float MaxJointOffset(Simulation sim, Entity player, FighterStartPose start)
    {
        var built = new Bodies.StartPoseModel(Assets.Clips).Of(start);
        var pose = sim.World.View<IAnimationView>().Model(sim.World.Get<PoseRef>(player));
        float max = 0;
        for (int j = 0; j < built.Length; j++) max = MathF.Max(max, Vector3.Distance(built[j].Translation, pose[j].Translation));
        return max;
    }

    /// <summary>
    /// Поиск — через целое число тиков (0,1 с × 120 Гц = 12), считая с тика после постройки; ручка периода меняет его на ходу (0,05 с — 6
    /// тиков); скачок цели скорости — поиск в этом же тике; под действием всем телом (рывок) — без поиска, в первом тике после — поиск.
    /// </summary>
    [Fact]
    public void Search_OnSchedule_AndOnEvents()
    {
        var sim = Create();
        var searchTicks = new List<ulong>();
        int before = 0;
        void Tick(in PlayerCommand c)
        {
            Step(sim, c);
            var s = State(sim);
            if (s.Searches != before) searchTicks.Add(sim.Tick);
            before = s.Searches;
        }
        for (int t = 0; t < 240; t++) Tick(RunForward);
        // расписание: первый поиск — в тике 2 (тик 1 ставит позу постройки), дальше ровно через 12
        Assert.Equal(2ul, searchTicks[0]);
        for (int i = 1; i < searchTicks.Count; i++) Assert.Equal(12ul, searchTicks[i] - searchTicks[i - 1]);

        // ручка периода: 0,05 с — 6 тиков
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of("locomotion.searchPeriod"), Value = 0.05f });
        searchTicks.Clear();
        for (int t = 0; t < 60; t++) Tick(RunForward);
        for (int i = 2; i < searchTicks.Count; i++) Assert.Equal(6ul, searchTicks[i] - searchTicks[i - 1]);
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of("locomotion.searchPeriod"), Value = GaspMotion.SearchPeriod });
        for (int t = 0; t < 30; t++) Tick(RunForward);

        // скачок цели: разворот назад в тике, когда поиск не по расписанию
        while (State(sim).SinceSearch != 3) Tick(RunForward);
        searchTicks.Clear();
        Tick(new PlayerCommand { Move = new Vector2(0, -1), HasGaze = true, Gaze = new Vector3(0, 0, 200) });
        Assert.Equal(new[] { sim.Tick }, searchTicks);
        Assert.Equal(0, State(sim).SinceSearch);

        // рывок: пока действие ведёт всё тело (AnimatorMotion.Search = false) — поисков нет; первый тик после — поиск
        for (int t = 0; t < 120; t++) Tick(RunForward);
        var player = GameCore.Player(sim.World);
        searchTicks.Clear();
        Tick(RunForward with { Dash = true });
        var blocked = new List<ulong>();
        ulong resumed = 0;
        bool was = sim.World.Get<AnimatorMotion>(player).Search;
        for (int t = 0; t < 120 && resumed == 0; t++)
        {
            Tick(RunForward);
            bool now = sim.World.Get<AnimatorMotion>(player).Search;
            if (!now) blocked.Add(sim.Tick);
            if (now && !was) resumed = sim.Tick;
            was = now;
        }
        Assert.True(blocked.Count > 10, $"рывок вёл всё тело {blocked.Count} тиков");
        Assert.True(resumed > 0, "после рывка поиск не разрешился");
        Assert.DoesNotContain(searchTicks, blocked.Contains);
        Assert.Contains(resumed, searchTicks);
        output.WriteLine($"рывок: без поиска {blocked.Count} тиков, поиск сразу в тике {resumed}");
        Clean(sim);
    }

    /// <summary>
    /// Одна правда предсказания: модель контроллера тика (<see cref="SteerModel"/>, пишет <c>core.root-steer</c>) — это шаг настоящего
    /// контроллера. При постоянном намерении (команда сцены: скорость и курс-цель) предсказание модели движка на N тиков от состояния после
    /// тика — побитово корень, курс и скорость после N настоящих тиков <c>core.root-steer</c> + <c>core.root-control</c>; в моменты будущего
    /// запроса (1/3, 2/3, 1 с) — тоже. Рывок, сдвиг клипом действия, столкновения и отдача — вне модели (здесь их нет).
    /// </summary>
    [Fact]
    public void Prediction_EqualsRealController_NTicks()
    {
        var sim = Create();
        var intent = new SceneCommand { Action = SceneAction.PlayerIntent, Slot = 0, Velocity = new Vector2(3f, 2f), Facing = 2f };
        void Tick()
        {
            sim.Enqueue(intent);
            Step(sim, default(PlayerCommand));
        }
        for (int t = 0; t < 4; t++) Tick();   // разгон и поворот идут
        var player = GameCore.Player(sim.World);
        var model = sim.World.Get<SteerModel>(player);
        Assert.Equal(new Vector3(3f, 0, 2f), model.GoalVelocity);
        Assert.Equal(2f, model.GoalYaw);
        var trajectory = new TrajectoryModel(model.Rate, model.TurnRate, model.GoalVelocity, model.GoalYaw);
        var t0 = sim.World.Get<Transform>(player);
        var state0 = new TrajectoryState(new Pose2(t0.Position.X, t0.Position.Z, t0.Facing), sim.World.Get<RootVelocity>(player).Value);
        Assert.True(MathF.Abs(state0.Root.Yaw - 2f) > 0.5f, "курс уже догнал цель — поворот не проверится");
        float dt = sim.Dt;
        var future = new TrajectoryState[GaspMotion.FutureTimes.Length];
        trajectory.Predict(state0, GaspMotion.FutureTimes, dt, future);
        var s = state0;
        for (int k = 1; k <= 120; k++)
        {
            Tick();
            Assert.Equal(model, sim.World.Get<SteerModel>(player));   // намерение постоянно — модель та же
            s = trajectory.Step(s, dt);
            var t = sim.World.Get<Transform>(player);
            var v = sim.World.Get<RootVelocity>(player).Value;
            Assert.True(s.Root.X == t.Position.X && s.Root.Z == t.Position.Z && s.Root.Yaw == t.Facing && s.Velocity == v,
                $"тик +{k}: предсказано {s.Root} {s.Velocity}, настоящий ({t.Position.X}, {t.Position.Z}; {t.Facing}) {v}");
            for (int f = 0; f < future.Length; f++)
                if (k == (int)MathF.Round(GaspMotion.FutureTimes[f] / dt))
                    Assert.True(future[f] == s, $"момент {GaspMotion.FutureTimes[f]} с: {future[f]} ≠ {s}");
        }
        Clean(sim);
    }

    /// <summary>
    /// На бегу с ударами (масштаб поворота удара 0,6 в окне урона, у игрока ход не замедляется) модель тика — ровно шаг контроллера: из
    /// корня и скорости после прошлого тика один шаг модели этого тика даёт корень и скорость после него (ударов стоя с клипом нет — на бегу
    /// клип ног не двигает корень).
    /// </summary>
    [Fact]
    public void SteerModel_EachTick_IsTheControllerStep_WithStrikes()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 60, RunForward);
        float dt = sim.Dt;
        int scaled = 0;
        for (int t = 0; t < 600; t++)
        {
            var before = sim.World.Get<Transform>(player);
            var velocity = sim.World.Get<RootVelocity>(player).Value;
            var gaze = new Vector3(10 * MathF.Sin(t * 0.02f), 0, 10 * MathF.Cos(t * 0.02f) + 30);
            Step(sim, new PlayerCommand { Move = new Vector2(MathF.Sin(t * 0.01f), MathF.Cos(t * 0.01f)), HasGaze = true, Gaze = gaze, Attack = t % 50 == 0 });
            var m = sim.World.Get<SteerModel>(player);
            if (m.TurnRate < 12f) scaled++;
            var step = new TrajectoryModel(m.Rate, m.TurnRate, m.GoalVelocity, m.GoalYaw)
                .Step(new TrajectoryState(new Pose2(before.Position.X, before.Position.Z, before.Facing), velocity), dt);
            var after = sim.World.Get<Transform>(player);
            Assert.True(step.Root.X == after.Position.X && step.Root.Z == after.Position.Z && step.Root.Yaw == after.Facing
                && step.Velocity == sim.World.Get<RootVelocity>(player).Value, $"тик {sim.Tick}: шаг модели {step.Root} — настоящий {after.Position} {after.Facing}");
        }
        Assert.True(scaled > 10, $"тиков с масштабом поворота удара {scaled}");
        Clean(sim);
    }

    /// <summary>
    /// R, смерть командой сцены и постройка заново (<c>Added&lt;Fighter&gt;</c> на той же сущности) — состояние MM с нуля: проигрыватель
    /// заново (тот же слот, поколение +1, кадр позы постройки, переходов 0), история корня — одна запись тика постройки, счётчики поиска с
    /// нуля. Мёртвый не трогается: состояние, проигрыватель и история (участник <c>locomotion</c>) стоят.
    /// </summary>
    [Fact]
    public void ResetDeathRebuild_RestartState_DeadUntouched()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 240, RunForward);
        var running = State(sim);
        Assert.True(running.Searches > 10 && running.Transitions > 0);
        Assert.True(History(sim, running.Player) > 30, $"история {History(sim, running.Player)}");

        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim, 1, RunForward);
        AssertFresh(sim, running.Player);

        Step(sim, 120, RunForward);
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        Step(sim, 2, RunForward);
        Assert.True(sim.World.Has<Dead>(player));
        var dead = State(sim);
        ulong hash = sim.HashState().Parts["locomotion"];
        Step(sim, 90, RunForward);
        Assert.Equal(dead, State(sim));
        Assert.Equal(hash, sim.HashState().Parts["locomotion"]);

        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(3, -2), Facing = 1 });
        Step(sim, 1, RunForward);
        Assert.False(sim.World.Has<Dead>(player));
        AssertFresh(sim, dead.Player);
        Step(sim, 60, RunForward);
        Clean(sim);
    }

    private static void AssertFresh(Simulation sim, MotionPlayerHandle before)
    {
        var s = State(sim);
        Assert.Equal(before.Index, s.Player.Index);
        Assert.Equal(before.Generation + 1, s.Player.Generation);
        Assert.False(s.Restart);
        Assert.Equal((0, 0, 0), (s.Searches, s.Switches, s.Transitions));
        Assert.Equal(StartFrame, s.Frame);
        Assert.Equal(1, History(sim, s.Player));
    }

    /// <summary>Кадр позы постройки — первый кадр стойки базы.</summary>
    private static int StartFrame
    {
        get
        {
            var b = Base;
            for (int c = 0; c < b.Names.Length; c++)
                if (b.Names[c].Kind == GaspKind.Idle) return b.Database.Clips[c].Start;
            return 0;
        }
    }

    /// <summary>Записей истории корня проигрывателя — из описания участника <c>locomotion</c> (как видит агент: <c>state.describe</c>).</summary>
    private static int History(Simulation sim, MotionPlayerHandle player)
    {
        var d = JsonNode.Parse(sim.DescribeState("locomotion", new DescribeQuery($"{player.Index}v{player.Generation}:—", Take: 5)))!;
        return d["items"]![0]!["history"]!.AsArray().Count;
    }

    [Fact]
    public void DataError_IsStartError()
    {
        // синтетические ассеты — без клипов GASP: MM игрока не стартует, отката на клипы нет
        var noGasp = Assert.Throws<InvalidDataException>(() => SwordSimulation.Create(ConfigSet.Empty("locomotion.player=mm"), TestAssets.Assets));
        Assert.Contains("locomotion.player = mm", noGasp.Message);
        Assert.Contains("locomotion", noGasp.Message);
        var regex = Assert.Throws<InvalidDataException>(() => Create("locomotion.clips=(Walk"));
        Assert.Contains("не регулярка", regex.Message);
        var empty = Assert.Throws<InvalidDataException>(() => Create("locomotion.clips=^Nothing$"));
        Assert.Contains("не взял ни одного", empty.Message);
        var mode = Assert.Throws<InvalidDataException>(() => SwordSimulation.Create(ConfigSet.Empty("locomotion.player=motion"), TestAssets.Assets));
        Assert.Contains("locomotion.player", mode.Message);
        var knob = Assert.Throws<InvalidDataException>(() => Create("locomotion.searchPeriod=0"));
        Assert.Contains("locomotion.searchPeriod", knob.Message);
    }

    /// <summary>Сценарий реплея с MM: бег, шаг, взгляд, рывок, удары, ручка посреди прогона, R.</summary>
    private static PlayerCommand Scripted(int t) => t switch
    {
        < 60 => new PlayerCommand { HasGaze = true, Gaze = new Vector3(0, 0, 50) },
        < 240 => new PlayerCommand { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(20, 0, 50), Attack = t % 70 == 0 },
        < 360 => new PlayerCommand { Move = new Vector2(1, -1), Walk = true, HasGaze = true, Gaze = new Vector3(-30, 0, 0) },
        < 480 => new PlayerCommand { Move = new Vector2(-1, 0), Dash = t == 400, HasGaze = true, Gaze = new Vector3(0, 0, -40) },
        _ => Circle(t),
    };

    [Fact]
    public void Replay_WithMotionMatching_SameHashes()
    {
        var a = Create("arena.enemies=2", "combat.enemiesAttack=false");
        var recorder = a.Record(new ReplayInfo("test", "tests", "reference"));
        for (int t = 0; t < 900; t++)
        {
            if (t == 300) a.Enqueue(new TuningCommand { Knob = TuningKey.Of("locomotion.switchGain"), Value = 0.5f });
            if (t == 600) a.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
            Step(a, Scripted(t));
        }
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        Assert.Contains("locomotion", replay.Parts);
        var b = SwordSimulation.Create(replay.CreateConfig(), Assets);
        var player = b.Play(replay);
        while (b.Tick < replay.Ticks)
        {
            b.Export.BeginFrame();
            b.Step();
        }
        Assert.Null(player.Divergence);
        Assert.Equal(901, player.HashesChecked);
        Assert.Equal(a.HashState().Total, b.HashState().Total);
        Assert.True(State(a).Transitions > 0);
        Clean(a);
    }

    /// <summary>
    /// 0 Б в тике с MM после прогрева (поиски, переходы по поиску и по рёбрам графа, инерциализация): бег по кругу 8 направлений; пул поз и
    /// хранилище проигрывателей не растут на переходах (ёмкость и запас те же).
    /// </summary>
    [Fact]
    public void SteadyTicks_DoNotAllocate_PoolDoesNotGrow()
    {
        var sim = Create("arena.enemies=3", "combat.enemiesAttack=false");
        int tick = 0;
        for (; tick < 360; tick++) Step(sim, Circle(tick));
        var pool = Summary(sim, "animation");
        var players = Summary(sim, "locomotion");
        var before = State(sim);
        long allocated = 0;
        for (; tick < 1800; tick++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            Step(sim, Circle(tick));
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
        var after = State(sim);
        Assert.True(after.Transitions - before.Transitions > 20 && after.Switches - before.Switches > 20,
            $"переходов {after.Transitions - before.Transitions}, смен {after.Switches - before.Switches}");
        Assert.Equal(0, allocated);
        Assert.Equal(pool, Summary(sim, "animation"));
        Assert.Equal(players, Summary(sim, "locomotion"));
        Clean(sim);
    }

    /// <summary>Ёмкость и запас участника (<c>state.describe</c>, сводка): у пула поз — capacity, reserved, jobsReserved; у проигрывателей — capacity, reserved.</summary>
    private static string Summary(Simulation sim, string part)
    {
        var d = JsonNode.Parse(sim.DescribeState(part, new DescribeQuery("-", Take: 0)))!;
        return $"{d["capacity"]} {d["reserved"]} {d["jobsReserved"]}";
    }

    /// <summary>
    /// Сценарий «бег по кругу 8 направлений» (0,5 с на направление, взгляд вперёд, 3 круга): переходов и смен в секунду, доля тиков с
    /// поиском — в вывод (замер 8е, пороги — 8и). Грубые рамки: поиск — в 8–12 % тиков (период 12 тиков и события), переходов — меньше 10 в
    /// секунду.
    /// </summary>
    [Fact]
    public void Circle8_TransitionsPerSecond()
    {
        var sim = Create();
        const int ticks = 1440, from = 120;
        int searchTicks = 0, beforeSearches = 0;
        MotionMatchState start = default;
        for (int t = 0; t < ticks; t++)
        {
            Step(sim, Circle(t));
            var s = State(sim);
            if (t == from) start = s;
            if (t > from && s.Searches != beforeSearches) searchTicks++;
            beforeSearches = s.Searches;
        }
        var end = State(sim);
        int measured = ticks - from - 1;
        float seconds = measured * sim.Dt;
        float transitions = (end.Transitions - start.Transitions) / seconds, switches = (end.Switches - start.Switches) / seconds;
        float share = searchTicks / (float)measured;
        output.WriteLine(string.Create(Inv,
            $"бег по кругу 8 направлений ({seconds:F1} с): переходов {transitions:F2}/с (по поиску {switches:F2}/с, остальные — рёбра графа), поисков {(end.Searches - start.Searches) / seconds:F2}/с, доля тиков с поиском {share:P1}"));
        Assert.InRange(share, 0.08f, 0.12f);
        Assert.InRange(transitions, 0.5f, 10f);
        Clean(sim);
    }
}
