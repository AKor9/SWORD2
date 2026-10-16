using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Motion Matching на экран (категория Content): вид <see cref="IMotionMatchView"/> осмыслен, события <see cref="MotionTransition"/>
/// сходятся со счётчиками и графом, тяжёлые инварианты MM ловят неисправность, замеры скольжения и скачка — в вывод, зона
/// <c>locomotion.search</c>, отпечаток базы в реплее, вкладка F3 «Локомоция».
/// </summary>
[Trait("Category", "Content")]
public sealed class MotionMatchViewTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Animation.CharacterAssets Assets => TestContent.Reference.Value.Assets;

    private static Simulation Create(params string[] sets) =>
        SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm", .. sets]), Assets);

    private static void Step(Simulation sim, in PlayerCommand command)
    {
        sim.Export.BeginFrame();
        sim.Enqueue(command);
        sim.Step();
    }

    private static MotionMatchState State(Simulation sim) => sim.World.Get<MotionMatchState>(GameCore.Player(sim.World));

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    private static readonly PlayerCommand RunForward = new() { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 200) };

    /// <summary>Бег по кругу 8 направлений (0,5 с на направление, взгляд вперёд).</summary>
    private static PlayerCommand Circle(int tick)
    {
        float a = tick / 60 % 8 * MathF.PI / 4;
        return new PlayerCommand { Move = new Vector2(MathF.Sin(a), MathF.Cos(a)), HasGaze = true, Gaze = new Vector3(0, 0, 60) };
    }

    /// <summary>Сценарии замеров: команда игрока на тик (12 с каждый).</summary>
    private static readonly (string Name, Func<int, PlayerCommand> Command)[] Scenarios =
    [
        ("бег по кругу 8 направлений", Circle),
        ("зигзаг бегом (0,4 с)", t => new PlayerCommand { Move = new Vector2(t / 48 % 2 == 0 ? 1 : -1, 1), HasGaze = true, Gaze = new Vector3(0, 0, 200) }),
        ("шаг ↔ бег (1 с)", t => new PlayerCommand { Move = new Vector2(0, 1), Walk = t / 120 % 2 == 1, HasGaze = true, Gaze = new Vector3(0, 0, 200) }),
        ("старт и стоп (1 с), вперёд и вбок", t => t / 120 % 2 == 1 ? new PlayerCommand { HasGaze = true, Gaze = new Vector3(0, 0, 200) }
            : new PlayerCommand { Move = t / 240 % 2 == 0 ? new Vector2(0, 1) : new Vector2(1, 0), HasGaze = true, Gaze = new Vector3(0, 0, 200) }),
        ("разворот 180° на бегу (1,5 с)", t => new PlayerCommand { Move = new Vector2(0, t / 180 % 2 == 0 ? 1 : -1), HasGaze = true, Gaze = new Vector3(0, 0, 200) }),
        ("стрейф вокруг точки взгляда", t => new PlayerCommand
        {
            Move = new Vector2(MathF.Cos(t * 0.01f), -MathF.Sin(t * 0.01f)), HasGaze = true, Gaze = Vector3.Zero,
        }),
        ("бег с ударами и рывками", t => new PlayerCommand
        {
            Move = new Vector2(MathF.Sin(t * 0.004f), MathF.Cos(t * 0.004f)), HasGaze = true, Gaze = new Vector3(0, 0, 200),
            Attack = t % 50 == 25, Dash = t % 150 == 70,
        }),
    ];

    // ── вид ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вид на паке: игрок бежит и поворачивает — состояние, последний поиск (цены конечны, сумма групп — цена), траектории конечны
    /// и по времени, история по тикам, скачок в пределе инварианта, отпечаток базы процесса; у врага и при <c>clips</c> — ничего.
    /// </summary>
    [Fact]
    public void View_OnPack_GivesMeaningfulData()
    {
        var sim = Create("arena.enemies=2", "combat.enemiesAttack=false");
        for (int t = 0; t < 180; t++) Step(sim, RunForward);
        for (int t = 0; t < 30; t++) Step(sim, RunForward with { Move = new Vector2(1, 0) });
        var view = sim.World.View<IMotionMatchView>();
        var anim = sim.World.View<IAnimationView>();
        var player = GameCore.Player(sim.World);
        var baseDb = GaspMotionBase.For(Assets.Clips).Database;
        Assert.Equal(baseDb.Fingerprint, view.Base.Fingerprint);
        Assert.Equal((baseDb.Summary.Clips, baseDb.Summary.Frames, baseDb.Summary.Searchable), (view.Base.Clips, view.Base.Frames, view.Base.Searchable));
        Assert.Equal(GaspMotion.DefaultClips, view.Pattern);
        Assert.Equal(GaspMotion.WeightKeys.Count, view.GroupCount);
        Assert.Equal(4, view.ContactJointCount);
        Assert.Equal(["foot_l", "foot_r", "ball_l", "ball_r"], Enumerable.Range(0, 4).Select(k => anim.JointName(view.ContactJoint(k))));

        Assert.True(view.TryStatus(player, out var s));
        var c = State(sim);
        Assert.InRange(s.Frame, 0, view.Base.Frames - 1);
        Assert.StartsWith("locomotion/", anim.ClipName(s.Clip));
        Assert.Equal((c.Frame, c.Clip, c.ClipFrame, c.Searches, c.Switches, c.Transitions), (s.Frame, s.Clip, s.ClipFrame, s.Searches, s.Switches, s.Transitions));
        Assert.True(s.Stepping && s.Searching && s.Alpha is >= 0 and < 1 && s.Rate is >= 0.6f and <= 1.4f, s.ToString());
        output.WriteLine(string.Create(Inv, $"клип {anim.ClipName(s.Clip)} кадр {s.ClipFrame}, доля {s.Alpha:F2}, темп {s.Rate:F3}, контакты {s.Contacts}"));

        Assert.True(view.TrySearch(player, out var q));
        Assert.InRange(q.Frame, 0, view.Base.Frames - 1);
        Assert.True(q.Tick > 0 && q.Tick < (long)sim.Tick && q.Reason != MotionSearchReason.None && float.IsFinite(q.Cost) && q.Cost >= 0, q.ToString());
        Assert.True(q.Microseconds > 0 && q.MaxMicroseconds >= q.Microseconds);
        float sum = 0;
        for (int g = 0; g < view.GroupCount; g++) sum += view.GroupCost(player, g);
        Assert.Equal(q.Cost, sum, 3);
        Assert.Equal(GaspMotion.Groups[0].Name, view.GroupName(0));

        var desired = view.Desired(player).ToArray();
        var matched = view.Matched(player).ToArray();
        Assert.Equal(1 + 1 + GaspMotion.FutureTimes.Length, desired.Length);
        Assert.Equal(desired.Length, matched.Length);
        foreach (var points in new[] { desired, matched })
        {
            Assert.All(points, p => Assert.True(float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Z) && float.IsFinite(p.Yaw) && p.Position.Y == 0));
            for (int i = 1; i < points.Length; i++) Assert.True(points[i].Time > points[i - 1].Time);
            Assert.Equal(0, points[1].Time);
        }
        Assert.Equal(desired[1].Position, matched[1].Position);   // «сейчас» — корень поиска у обеих
        var root = sim.World.Get<Transform>(player).Position;
        Assert.True(Vector2.Distance(new(desired[1].Position.X, desired[1].Position.Z), new(root.X, root.Z)) < 0.6f, "корень поиска — не рядом с игроком");
        Assert.True(Vector3.Distance(desired[^1].Position, desired[1].Position) > 2, "будущее через 1 с на бегу — ближе 2 м");

        var history = view.History(player).ToArray();
        Assert.True(history.Length > 30);
        Assert.Equal(0, history[^1].Time);
        Assert.Equal(new Vector3(root.X, 0, root.Z), history[^1].Position);
        for (int i = 1; i < history.Length; i++) Assert.Equal(history[i].Time - history[i - 1].Time, sim.Dt, 4);

        var m = view.Measure(player);
        Assert.True(float.IsFinite(m.JointJump) && m.JointJump >= 0 && m.JointJump < MotionMatchContinuityInvariant.Limit && m.JumpJoint >= 0, m.ToString());
        Assert.Equal(m.FeetOnGround > 0, float.IsFinite(m.FootSlide));
        Assert.Equal(System.Numerics.BitOperations.PopCount((uint)s.Contacts), m.FeetOnGround);

        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>()))
            foreach (var e in chunk.Entities)
            {
                Assert.False(view.TryStatus(e, out _) || view.TrySearch(e, out _));
                Assert.True(view.Desired(e).IsEmpty && view.History(e).IsEmpty && float.IsNaN(view.Measure(e).JointJump));
            }
        Clean(sim);

        var clips = SwordSimulation.Create(ConfigSet.Empty(), Assets);
        Assert.False(clips.World.TryView<IMotionMatchView>(out _));
        Assert.False(clips.Export.Exports<Event<MotionTransition>>());
    }

    // ── события переходов ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// На сценариях замеров события <see cref="MotionTransition"/>: поиском — столько, сколько смен, всего — сколько переходов; у
    /// поиска — повод, вход найденного кадра, цена не больше цены продолжения; у ребра — кадр и вход по графу. На круге 8
    /// направлений — только поиском: ребро не успевает.
    /// </summary>
    [Fact]
    public void Transitions_AreEvents_MatchCountersAndGraph()
    {
        var db = GaspMotionBase.For(Assets.Clips).Database;
        int allEdges = 0;
        var allReasons = new Dictionary<MotionSearchReason, int>();
        foreach (var (name, command) in Scenarios)
        {
            var sim = Create();
            var events = new List<(ulong Tick, MotionTransition E)>();
            var start = State(sim);
            for (int t = 0; t < 1440; t++)
            {
                Step(sim, command(t));
                foreach (var (tick, r) in sim.Export.Frame<Event<MotionTransition>>()) events.Add((tick, r.Value));
            }
            var end = State(sim);
            var player = GameCore.Player(sim.World);
            int search = events.Count(x => x.E.Kind == MotionTransitionKind.Search), edges = events.Count(x => x.E.Kind == MotionTransitionKind.Edge);
            output.WriteLine($"{name}, 12 с: переходов {end.Transitions}, событий {events.Count} (поиском {search}, рёбер {edges}), смен {end.Switches}");
            Assert.True(search > 5, name);
            Assert.Equal(end.Switches - start.Switches, search);
            Assert.Equal(end.Transitions - start.Transitions, events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i].E;
                Assert.Equal(player, e.Fighter);
                Assert.Equal(i + 1, e.Transitions);
                Assert.InRange(e.From, 0, db.FrameCount - 1);
                Assert.InRange(e.To, 0, db.FrameCount - 1);
                Assert.Equal(db.Clips[db.ClipOf[e.From]].LibraryClip, e.FromClip);
                Assert.Equal(db.Clips[db.ClipOf[e.To]].LibraryClip, e.ToClip);
                Assert.Equal((db.FrameInClip(e.From), db.FrameInClip(e.To)), (e.FromClipFrame, e.ToClipFrame));
                Assert.Equal(-1, db.Entry[e.To]);   // куда — всегда вход, не кадр выхода
                if (e.Kind == MotionTransitionKind.Search)
                {
                    Assert.NotEqual(MotionSearchReason.None, e.Reason);
                    // Из искомого кадра уходят, только если найденный дешевле продолжения (с порогом); из неискомого — всегда.
                    Assert.True(!db.Searchable[e.From] || e.Cost <= e.ContinuationCost, $"{name}: поиск — цена {e.Cost} дороже продолжения {e.ContinuationCost}");
                    allReasons[e.Reason] = allReasons.GetValueOrDefault(e.Reason) + 1;
                }
                else
                {
                    Assert.Equal(MotionSearchReason.None, e.Reason);
                    Assert.True(e.Cost >= 0 && e.ContinuationCost == -1);
                    Assert.True(db.Entry[e.From] >= 0 || db.Next[e.From] >= 0 && db.ClipOf[db.Next[e.From]] != db.ClipOf[e.From],
                        $"{name}: ребро из кадра {e.From} — не ребро графа");
                    // Вход ребра — по графу: у кадра выхода — его вход, у последнего кадра клипа — вход следующего кадра (цепочка входов).
                    int to = db.Entry[e.From] >= 0 ? e.From : db.Next[e.From];
                    while (db.Entry[to] >= 0) to = db.Entry[to];
                    Assert.Equal(to, e.To);
                }
            }
            allEdges += edges;
            Clean(sim);
        }
        output.WriteLine($"всего рёбер {allEdges}; поводы смен: " + string.Join(", ", allReasons.Select(r => $"{r.Key} {r.Value}")));
        Assert.True(allReasons.ContainsKey(MotionSearchReason.GoalJump), "смена направления — скачок цели, а смен по нему нет");
        Assert.True(allEdges > 0, "ни одного ребра графа на сценариях — путь события «ребро» не проверен");
    }

    // ── инварианты-неисправности ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Неисправность MM: лишние задания пула игроку после шага <c>locomotion.mm</c> — через рантайм MM (ресурс мира).</summary>
    public enum MmFault
    {
        /// <summary>Инерциализация выключена: переход в далёкий кадр (стойка на бегу) и сразу затухание с полупериодом 10⁻⁴ с — поза прыгает.</summary>
        Pop,

        /// <summary>Переход в далёкий кадр, через 3 тика — шаг затухания с полупериодом 10⁶ с на 0,5 с: смещения растут без перехода.</summary>
        Grow,
    }

    private sealed class MmFaultModule(MmFault fault, ulong tick) : IModule
    {
        public string Id => "mmfault";

        public void Configure(SimulationBuilder b) => b.System(GameStages.Animation, new FaultJobs(fault, tick));
    }

    private sealed class FaultJobs(MmFault fault, ulong tick) : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("mmfault.jobs")).After(MotionMatchSystems.Step).Before(GameLabels.AnimatorPose).Reads<MotionMatchState>().Reads<PoseRef>();
            _query = s.Query().With<PlayerTag>().Read<MotionMatchState>().Read<PoseRef>();
        }

        public void Run(in SystemContext ctx)
        {
            var world = ctx.World;
            var time = world.Resource<SimTime>();
            bool jump = time.Tick == (fault == MmFault.Pop ? tick : tick - 3), grow = fault == MmFault.Grow && time.Tick == tick;
            if (!jump && !grow) return;
            var runtime = world.Resource<MotionMatchRuntime>();
            var pool = world.Resource<PosePool>();
            float halflife = world.Resource<LocomotionRules>().InertialHalflife;
            foreach (var chunk in ctx.Iterate(_query))
            {
                var s = chunk.Read<MotionMatchState>()[0];
                var r = chunk.Read<PoseRef>()[0];
                var pose = new PoseHandle(r.Index, r.Generation);
                if (jump) pool.Motion(pose, runtime.Players, s.Player, new MotionStep(0, time.Dt, runtime.StartFrame, halflife));
                if (jump && fault == MmFault.Pop) pool.Motion(pose, runtime.Players, s.Player, new MotionStep(0, time.Dt, -1, 1e-4f));
                if (grow) pool.Motion(pose, runtime.Players, s.Player, new MotionStep(0, 0.5f, -1, 1e6f));
            }
        }
    }

    [Theory]
    [InlineData(MmFault.Pop, "locomotion.mmContinuity", "прыгнул за тик")]
    [InlineData(MmFault.Grow, "locomotion.mmDecay", "выросла без перехода")]
    public void Fault_CaughtOnItsTick(MmFault fault, string id, string details)
    {
        const ulong at = 200;
        var sim = Simulation.Build(ConfigSet.Empty("locomotion.player=mm"), GameStages.All, b => GameCore.Configure(b, Assets),
            [.. SwordSimulation.Modules(), new MmFaultModule(fault, at)]);
        Assert.Contains(sim.Invariants.List(), i => i.Id == id && i.Enabled);
        for (ulong t = 0; t < at; t++) Step(sim, Circle((int)t));
        Assert.DoesNotContain(sim.Invariants.Recent(), v => v.Id is "locomotion.mmContinuity" or "locomotion.mmDecay");
        Step(sim, Circle((int)at));
        var first = sim.Invariants.Recent().First(v => v.Id == id);
        Assert.Equal((at, GameStages.End), (first.Tick, first.Stage));
        Assert.Contains(details, first.Details);
        output.WriteLine($"{id} на тике {first.Tick}: {first.Details}");
    }

    /// <summary>Живая игра: тяжёлые инварианты MM — раз в 4 тика, лёгкий <c>locomotion.mm</c> — каждый тик; вне живой игры — каждый тик.</summary>
    [Fact]
    public void LiveGame_MmInvariants_HeavyEveryFourTicks()
    {
        var sim = Create();
        var ids = new[] { "locomotion.mmDecay", "locomotion.mmContinuity" };
        Assert.All(sim.Invariants.List().Where(i => i.Id.StartsWith("locomotion.mm", StringComparison.Ordinal)), i => Assert.Equal(1, i.Every));
        sim.Invariants.LivePeriods = true;
        foreach (var i in sim.Invariants.List().Where(i => i.Id.StartsWith("locomotion.mm", StringComparison.Ordinal)))
            Assert.Equal(ids.Contains(i.Id) ? InvariantCost.HeavyPeriod : 1, i.Every);
        Assert.Equal(ids, sim.Invariants.List().Select(i => i.Id).Where(ids.Contains));
    }

    // ── замеры: скольжение опорной стопы и скачок сустава ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Сценарии по 12 с: скольжение опорной стопы (среднее, p95), наибольший скачок сустава (<see cref="IMotionMatchView.Measure"/>),
    /// переходы в секунду — в вывод; скачок ниже <see cref="MotionMatchContinuityInvariant.Limit"/>, инварианты чисты.
    /// </summary>
    [Fact]
    public void Measures_FootSlideAndJointJump_OnScenarios()
    {
        float worst = 0;
        foreach (var (name, command) in Scenarios)
        {
            var sim = Create();
            var view = sim.World.View<IMotionMatchView>();
            var player = GameCore.Player(sim.World);
            var slides = new List<double>();
            var jumps = new List<double>();
            int from = 120, ticks = 1440, transitions = 0;
            string jumpJoint = "";
            for (int t = 0; t < ticks; t++)
            {
                Step(sim, command(t));
                if (t == from) transitions = State(sim).Transitions;
                if (t <= from) continue;
                var m = view.Measure(player);
                if (float.IsFinite(m.FootSlide)) slides.Add(m.FootSlide);
                if (!float.IsFinite(m.JointJump)) continue;
                if (m.JointJump > jumps.DefaultIfEmpty(0).Max()) jumpJoint = sim.World.View<IAnimationView>().JointName(m.JumpJoint);
                jumps.Add(m.JointJump);
            }
            slides.Sort();
            jumps.Sort();
            double P(List<double> v, double p) => v.Count == 0 ? double.NaN : v[Math.Clamp((int)Math.Ceiling(p * v.Count), 1, v.Count) - 1];
            float seconds = (ticks - from - 1) * sim.Dt;
            output.WriteLine(string.Create(Inv,
                $"{name}: скольжение опорной стопы среднее {slides.Average():F3} м/с, p95 {P(slides, 0.95):F3}, макс {slides.Max():F3} (тиков со стопой {slides.Count} из {ticks - from - 1}); " +
                $"скачок сустава за тик p99 {P(jumps, 0.99):F4} м, макс {jumps.Max():F4} ({jumpJoint}); переходов {(State(sim).Transitions - transitions) / seconds:F2}/с"));
            worst = MathF.Max(worst, (float)jumps.Max());
            Assert.True(slides.Count > 100);
            Clean(sim);
        }
        output.WriteLine(string.Create(Inv, $"худший скачок сустава за тик на сценариях {worst:F4} м; предел инварианта {MotionMatchContinuityInvariant.Limit} м ({MotionMatchContinuityInvariant.Limit / worst:F2}×)"));
        Assert.True(worst < MotionMatchContinuityInvariant.Limit);
    }

    // ── профайлер ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Зона <c>locomotion.search</c>: под <c>locomotion.mm@Animation</c>, вызовов — сколько поисков; хеши с профайлером те же.</summary>
    [Fact]
    public void ProfilerZone_Search_UnderMotionMatchSystem()
    {
        var plain = Create();
        var sim = Create();
        var profiler = new CpuProfiler();
        sim.AttachProfiler(profiler);
        profiler.SetEnabled(true);
        for (int t = 0; t < 240; t++)
        {
            Step(plain, Circle(t));
            Step(sim, Circle(t));
        }
        var zone = profiler.Stats(ProfilePeriod.Tick).Single(z => z.Name == MotionMatchDiagnostics.Zone);
        Assert.Equal("locomotion.mm@Animation", zone.Parent);
        Assert.InRange(zone.Calls * 240, State(sim).Searches - 1, State(sim).Searches + 1);   // вызовов на тик × тиков ≈ поисков
        Assert.True(zone.MaxMs > 0);
        output.WriteLine(string.Create(Inv, $"locomotion.search: среднее {zone.MeanMs:F4} мс на тик, макс {zone.MaxMs:F4} мс, вызовов на тик {zone.Calls:F3}"));
        Assert.Equal(plain.HashState().Total, sim.HashState().Total);
    }

    // ── реплей ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Отпечаток базы в заголовке реплея (<c>facts.locomotion.base</c>): тот же — воспроизведение без предупреждений; другая база (отбор без
    /// клипов Dance) — предупреждение и причина в тексте расхождения; при <c>clips</c> сведения нет.
    /// </summary>
    [Fact]
    public void Replay_CarriesBaseFingerprint_OtherBaseNamedInDivergence()
    {
        var a = Create();
        var recorder = a.Record(new ReplayInfo("test", "tests", "reference"));
        for (int t = 0; t < 240; t++) Step(a, Circle(t));
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        string fingerprint = GaspMotionBase.For(Assets.Clips).Database.Fingerprint.ToString("X16", Inv);
        Assert.Equal(fingerprint, replay.Facts![MotionMatchObservers.BaseFact]);

        static void PlayAll(Simulation sim, ulong ticks)
        {
            while (sim.Tick < ticks)
            {
                sim.Export.BeginFrame();
                sim.Step();
            }
        }
        var same = SwordSimulation.Create(replay.CreateConfig(), Assets);
        var ok = same.Play(replay);
        PlayAll(same, replay.Ticks);
        Assert.Null(ok.Divergence);
        Assert.Empty(ok.Warnings);

        var other = SwordSimulation.Create(replay.CreateConfig(["locomotion.clips=^(?!.*(Circle_Strafe|Pivot))"]), Assets);
        var bad = other.Play(replay);
        PlayAll(other, replay.Ticks);
        Assert.Contains(bad.Warnings, w => w.Contains($"locomotion.base: в реплее {fingerprint}, в симуляции"));
        Assert.NotNull(bad.Divergence);
        Assert.Contains("вероятная причина: сведения реплея другие — locomotion.base", bad.Divergence.ToString());
        output.WriteLine(bad.Divergence.ToString());

        Assert.Empty(SwordSimulation.Create(ConfigSet.Empty(), Assets).Facts);
    }

    // ── F3 ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вкладка F3 «Локомоция»: 22 ручки по порядку регистрации, строки состояния по виду пишут текст на стек без выделений; при
    /// <c>clips</c> вкладки нет.
    /// </summary>
    [Fact]
    public void TuningGroup_KnobsAndRows_NoAllocations()
    {
        var sim = Create();
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(Circle((int)link.Tick)), ticksPerFrame: 1);
        var view = new ArenaView(ArenaTerrain.Height);
        var group = LocomotionTuning.Build(new TuningBinding(clock.Link), clock.Link, view)!;
        Assert.NotNull(group);
        Assert.Equal((LocomotionKnobs.Section, "Локомоция"), (group.Id, group.Title));
        Assert.Equal(sim.Tuning.Where(k => k.Section == LocomotionKnobs.Section).Select(k => k.Key).Order(), group.Values().Keys.Order());
        Assert.Equal(["MOTION MATCHING ИГРОКА", "ПОИСК, ПЕРЕХОД, ВЕСА ГРУПП, РУКА, ПОВОРОТ"], group.Items.OfType<TuningHeader>().Select(h => h.Label));
        Assert.Equal(sim.Tuning.Where(k => k.Section == LocomotionKnobs.Section).Select(k => k.Key),
            group.Items.OfType<TuningFloat>().Select(f => f.Key));   // порядок регистрации: поиск и переход, веса, вес руки, поворот
        Assert.Equal(22, group.Items.OfType<TuningFloat>().Count());
        var rows = group.Items.OfType<TuningInfo>().ToList();
        Assert.Equal(["клип", "поиск", "время поиска", "переходов/с", "поворот на месте"], rows.Select(r => r.Label));
        long bytes = 0;
        Span<char> d = stackalloc char[128];
        for (int f = 0; f < 400; f++)
        {
            clock.RunFrame(0);
            view.Extract(clock.Link, 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < rows.Count; i++) rows[i].Text(d);
            if (f > 300) bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var texts = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) texts[i] = d[..rows[i].Text(d)].ToString();
        output.WriteLine(string.Join(" | ", texts));
        Assert.Equal(0, bytes);
        var clip = sim.World.View<IAnimationView>().ClipName(State(sim).Clip);
        string tail = clip[(clip.LastIndexOf('/') + 1)..];
        Assert.Contains(" · ", texts[0]);
        Assert.True(texts[0].StartsWith(tail + " · ", StringComparison.Ordinal) || texts[0].StartsWith("..", StringComparison.Ordinal) && tail.EndsWith(texts[0][2..texts[0].IndexOf(" · ", StringComparison.Ordinal)], StringComparison.Ordinal),
            $"«{texts[0]}» — не клип «{clip}»");
        Assert.True(texts[0].IndexOf(" · ", StringComparison.Ordinal) <= LocomotionTuning.ClipChars);
        Assert.Contains(" / ", texts[1]);
        Assert.Contains("мс (макс", texts[2]);
        Assert.Contains("(всего ", texts[3]);
        Assert.False(texts[3].StartsWith("—", StringComparison.Ordinal), texts[3]);
        var clips = new SimulationLink(SwordSimulation.Create(ConfigSet.Empty(), Assets));
        Assert.Null(LocomotionTuning.Build(new TuningBinding(clips), clips, view));
    }
}
