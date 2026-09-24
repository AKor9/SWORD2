using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime.Tests;

public struct Gauge
{
    public float Level;
    public Entity Peer;
}

/// <summary>
/// Модуль-неисправность (этап 3г): датчики с уровнем 1; на тике <see cref="NaNTick"/> система стадии A портит уровень
/// первого датчика в NaN, на тике <see cref="NegativeTick"/> — в −1 у второго; на тике <see cref="OrphanTick"/> третий
/// датчик теряет соседа (сосед удалён, ссылка осталась). Свой инвариант модуля — «уровень не отрицателен» после стадии A.
/// </summary>
public sealed class FaultyModule : IModule
{
    public const ulong NaNTick = 7, NegativeTick = 11, OrphanTick = 4;

    public string Id => "faulty";

    public void Configure(SimulationBuilder b) =>
        b.Component<Gauge>()
            .System("A", new Breaker())
            .Invariant(new NonNegative())
            .OnWorldBuilt(w =>
            {
                var peer = w.CreateImmediate();
                for (int i = 0; i < 3; i++) w.AddImmediate(w.CreateImmediate(), new Gauge { Level = 1, Peer = peer });
            });

    private sealed class Breaker : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("faulty.breaker")).Writes<Gauge>().Destroys();
            _query = s.Query().Write<Gauge>();
        }

        public void Run(in SystemContext ctx)
        {
            ulong tick = ctx.World.Resource<SimTime>().Tick;
            foreach (var chunk in ctx.Iterate(_query))
            {
                var gauges = chunk.Write<Gauge>();
                if (tick == NaNTick) gauges[0].Level = float.NaN;
                if (tick == NegativeTick) gauges[1].Level = -1;
                if (tick == OrphanTick) ctx.Commands.Destroy(new Entity(0, 1));
            }
        }
    }

    public sealed class NonNegative : IInvariant
    {
        private ReadQuery? _query;

        public string Id => "faulty.nonNegative";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => "A";

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            _query ??= ctx.World.Query().Read<Gauge>();
            foreach (var chunk in ctx.World.Iterate(_query))
            {
                var gauges = chunk.Read<Gauge>();
                for (int i = 0; i < gauges.Length; i++)
                    if (gauges[i].Level < 0) violations.Report(chunk.Entities[i], $"уровень {gauges[i].Level}");
            }
        }
    }

    public static Simulation Build(ConfigSet? config = null) => Simulation.Build(config ?? ConfigSet.Empty(), ["A", "B"], [new FaultyModule()]);
}

/// <summary>
/// Инварианты (этап 3г, раздел 8 плана): реестр в симуляции, проверка после барьера своей стадии, нарушение — на своём
/// тике, стоимость и отказы, хеш и память не затрагиваются.
/// </summary>
public class InvariantTests
{
    [Fact]
    public void FaultyModule_CaughtOnItsTick_AfterItsStage()
    {
        var sim = FaultyModule.Build();
        var ids = sim.Invariants.List().Select(i => (i.Id, i.Owner, i.Enabled)).ToList();
        Assert.Equal([("ecs.finite", "engine", true), ("ecs.entityRefs", "engine", false), ("faulty.nonNegative", "faulty", true)], ids);

        while (sim.Tick < 15) sim.Step();
        var recent = sim.Invariants.Recent();
        // NaN с тика 7 держится — ecs.finite ловит его в конце каждого тика, первым — на тике 7.
        var nan = recent.First(v => v.Id == "ecs.finite");
        Assert.Equal((FaultyModule.NaNTick, Simulation.ExportStage, new Entity(1, 1)), (nan.Tick, nan.Stage, nan.Entity));
        Assert.Contains("Gauge.Level = NaN", nan.Details);
        Assert.Equal(15 - (long)FaultyModule.NaNTick, sim.Invariants.Find("ecs.finite")!.Violations);
        // −1 — свой инвариант модуля, сразу после стадии A тика 11.
        var negative = recent.First(v => v.Id == "faulty.nonNegative");
        Assert.Equal((FaultyModule.NegativeTick, "A", new Entity(2, 1)), (negative.Tick, negative.Stage, negative.Entity));
        Assert.Equal(recent.Count, sim.Invariants.Total);
        var info = sim.Invariants.Find("faulty.nonNegative")!;
        Assert.Equal(15, info.Evaluations);
        Assert.True(info.MeanMicroseconds > 0);
        Assert.Equal(("faulty.nonNegative", 14ul), (info.Last!.Value.Id, info.Last.Value.Tick));   // −1 держится — последнее на тике 14
    }

    [Fact]
    public void OnDemand_NotChecked_ByItself_RunFindsIt()
    {
        var sim = FaultyModule.Build();
        while (sim.Tick < 6) sim.Step();
        Assert.Equal(0, sim.Invariants.Find("ecs.entityRefs")!.Evaluations);    // «по запросу» сам не проверяется
        var run = sim.Invariants.Run(["ecs.entityRefs"], sim.Tick, "между тиками", sim.Dt).Single();
        Assert.Equal(3, run.Violations);                                          // три датчика ссылаются на удалённого соседа
        Assert.Contains("Gauge.Peer = 0v1 — не жива", run.Details[0].Details);
        Assert.Equal(0, sim.Invariants.Total);                                    // проверка по запросу — только в ответе

        sim.Invariants.SetEnabled("ecs.entityRefs", true);                        // включённый — каждый тик
        sim.Step();
        Assert.Equal(3, sim.Invariants.Find("ecs.entityRefs")!.Violations);
        Assert.Throws<ArgumentException>(() => sim.Invariants.SetEnabled("no.such", true));
    }

    [Fact]
    public void ConfigOff_NothingChecked_HashTheSame()
    {
        var on = FaultyModule.Build();
        var off = FaultyModule.Build(ConfigSet.Empty("sim.invariants=false"));
        Assert.False(off.Invariants.Active);
        Assert.All(off.Invariants.List(), i => Assert.False(i.Enabled));
        for (int t = 0; t < 20; t++)
        {
            on.Step();
            off.Step();
            Assert.Equal(off.HashState().Total, on.HashState().Total);   // инварианты мир не меняют
        }
        Assert.True(on.Invariants.Total > 0);
        Assert.Equal(0, off.Invariants.Total);
    }

    private sealed class Periodic(int period) : IInvariant
    {
        public readonly List<ulong> Ticks = [];
        public string Id => "test.periodic";
        public InvariantCost Cost => InvariantCost.Every(period);
        public InvariantScope Scope => InvariantScope.World;
        public string After => "B";
        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations) => Ticks.Add(ctx.Tick);
    }

    private sealed class Throws : IInvariant
    {
        public string Id => "test.throws";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.World;
        public string After => Simulation.ExportStage;
        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations) => throw new InvalidOperationException("сломался");
    }

    private sealed class Module(params IInvariant[] invariants) : IModule
    {
        public string Id => "probe";
        public void Configure(SimulationBuilder b) => b.Invariants(invariants);
    }

    /// <summary>
    /// Период кодового инварианта (исправления после Пд, решение пользователя) действует только в живой игре
    /// (<see cref="SimulationInvariants.LivePeriods"/>, Sword.exe): в тестах, verify и Headless — каждый тик; инвариант агента
    /// (<see cref="SimulationInvariants.Add"/>, <c>invariant.define every</c>) держит свой период всегда.
    /// </summary>
    [Fact]
    public void Periodic_AndThrowing_Invariants()
    {
        var periodic = new Periodic(4);
        var sim = Simulation.Build(ConfigSet.Empty(), ["A", "B"], [new Module(periodic, new Throws())]);
        Assert.False(sim.Invariants.LivePeriods);
        Assert.Equal(1, sim.Invariants.Find("test.periodic")!.Every);
        while (sim.Tick < 10) sim.Step();
        Assert.Equal([.. Enumerable.Range(0, 10).Select(t => (ulong)t)], periodic.Ticks);   // не живая игра — каждый тик
        var thrown = sim.Invariants.Find("test.throws")!;
        Assert.Equal(10, thrown.Violations);                                      // исключение — нарушение, а не падение тика
        Assert.Contains("исключение в проверке: InvalidOperationException: сломался", thrown.Last!.Value.Details);

        var live = new Periodic(4);
        var game = Simulation.Build(ConfigSet.Empty(), ["A", "B"], [new Module(live)]);
        game.Invariants.LivePeriods = true;
        Assert.Equal(4, game.Invariants.Find("test.periodic")!.Every);
        var agent = new AgentPeriodic(3);
        game.Invariants.Add(agent, "agent");
        while (game.Tick < 10) game.Step();
        Assert.Equal([0ul, 4ul, 8ul], live.Ticks);                               // живая игра — раз в 4
        Assert.Equal([0ul, 3ul, 6ul, 9ul], agent.Ticks);

        var agentOnly = new AgentPeriodic(3);
        sim.Invariants.Add(agentOnly, "agent");                                   // не живая игра: период агента — всё равно свой
        while (sim.Tick < 16) sim.Step();
        Assert.Equal([12ul, 15ul], agentOnly.Ticks);
        Assert.Equal(3, sim.Invariants.Find("agent.periodic")!.Every);
    }

    private sealed class AgentPeriodic(int period) : IInvariant
    {
        public readonly List<ulong> Ticks = [];
        public string Id => "agent.periodic";
        public InvariantCost Cost => InvariantCost.Every(period);
        public InvariantScope Scope => InvariantScope.World;
        public string After => Simulation.ExportStage;
        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations) => Ticks.Add(ctx.Tick);
    }

    /// <summary>Неисправность с тика <paramref name="from"/>: уровень датчика — NaN и дальше (держится).</summary>
    private sealed class LateNaN(ulong from) : IModule
    {
        public string Id => "late";

        public void Configure(SimulationBuilder b) =>
            b.Component<Gauge>()
                .System("A", new Breaker(from))
                .OnWorldBuilt(w => w.AddImmediate(w.CreateImmediate(), new Gauge { Level = 1 }));

        private sealed class Breaker(ulong from) : IContinuousSystem
        {
            private Query _query = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("late.breaker")).Writes<Gauge>();
                _query = s.Query().Write<Gauge>();
            }

            public void Run(in SystemContext ctx)
            {
                if (ctx.World.Resource<SimTime>().Tick != from) return;
                foreach (var chunk in ctx.Iterate(_query)) chunk.Write<Gauge>()[0].Level = float.NaN;
            }
        }
    }

    /// <summary>
    /// Живая игра (исправления после Пд): тяжёлый кодовый инвариант (<c>ecs.finite</c> — <see cref="InvariantCost.Heavy"/>) раз в
    /// 4 тика ловит нарушение, которое держится, не позже чем через 3 тика после его тика — при любом тике начала; не живая
    /// игра (тесты, verify, Headless) — на самом тике нарушения.
    /// </summary>
    [Fact]
    public void LiveGame_HeavyInvariant_CatchesPersistentViolationWithinPeriod()
    {
        Assert.Equal(InvariantCost.Heavy, InvariantCost.Every(InvariantCost.HeavyPeriod));
        for (ulong from = 0; from < 8; from++)
            foreach (bool live in (bool[])[false, true])
            {
                var sim = Simulation.Build(ConfigSet.Empty(), ["A"], [new LateNaN(from)]);
                Assert.Equal(InvariantCost.Heavy, sim.Invariants.List().Single(i => i.Id == "ecs.finite").Cost);
                sim.Invariants.LivePeriods = live;
                while (sim.Invariants.Total == 0 && sim.Tick < 32) sim.Step();
                var first = sim.Invariants.Recent()[0];
                Assert.Equal("ecs.finite", first.Id);
                if (live)
                {
                    Assert.InRange(first.Tick, from, from + InvariantCost.HeavyPeriod - 1);
                    Assert.Equal(0ul, first.Tick % InvariantCost.HeavyPeriod);
                }
                else Assert.Equal(from, first.Tick);
            }
    }

    private sealed class Named(string id, string after = "A") : IInvariant
    {
        public string Id => id;
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.World;
        public string After => after;
        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations) { }
    }

    [Theory]
    [InlineData("Bad.Id", "A", "нужно")]
    [InlineData("a..b", "A", "нужно")]
    [InlineData("probe.x", "Nope", "нет стадии")]
    public void Registration_Errors(string id, string after, string expected)
    {
        var e = Assert.ThrowsAny<Exception>(() => Simulation.Build(ConfigSet.Empty(), ["A"], [new Module(new Named(id, after))]));
        Assert.Contains(expected, e.Message);
        var twice = Assert.Throws<InvalidOperationException>(() =>
            Simulation.Build(ConfigSet.Empty(), ["A"], [new Module(new Named("probe.x"), new Named("probe.x"))]));
        Assert.Contains("дважды", twice.Message);
        Assert.True(IInvariant.IsValidId("physics.finite"));
        Assert.True(IInvariant.IsValidId("a.b_2.c"));
        Assert.False(IInvariant.IsValidId("a."));
    }

    [Fact]
    public void SteadyTick_WithInvariants_DoesNotAllocate()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["A", "B"], [new Module(new Named("probe.quiet"))]);
        for (int t = 0; t < 50; t++) sim.Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 200; t++) sim.Step();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(250, sim.Invariants.Find("ecs.finite")!.Evaluations);
    }
}
