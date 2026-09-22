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

    [Fact]
    public void Periodic_AndThrowing_Invariants()
    {
        var periodic = new Periodic(4);
        var sim = Simulation.Build(ConfigSet.Empty(), ["A", "B"], [new Module(periodic, new Throws())]);
        while (sim.Tick < 10) sim.Step();
        Assert.Equal([0ul, 4ul, 8ul], periodic.Ticks);
        var thrown = sim.Invariants.Find("test.throws")!;
        Assert.Equal(10, thrown.Violations);                                      // исключение — нарушение, а не падение тика
        Assert.Contains("исключение в проверке: InvalidOperationException: сломался", thrown.Last!.Value.Details);
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
