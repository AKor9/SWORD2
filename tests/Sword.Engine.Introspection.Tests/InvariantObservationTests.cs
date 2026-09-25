using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests;

/// <summary>
/// Инварианты в наблюдении: остановка на нарушении — посреди тика после нарушившего барьера (Headless) или после
/// тика (живая игра), нарушения в истории, инварианты агента условием (мир и по сущностям) и их перенос в новую симуляцию.
/// Подопытный — <see cref="TestModule"/>: тела X = 0…4, скорость 0…2, Order в S1 двигает все тела по X.
/// </summary>
public class InvariantObservationTests
{
    private static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    /// <summary>Играть приказами X+1 каждый тик, пока время разрешает (цикл Headless).</summary>
    private static void Play(Simulation sim, TimeControl time)
    {
        while (time.Allow(1) > 0)
        {
            sim.Enqueue(new Order { Move = new Vector2(1, 0) });
            sim.Step();
            time.Ran(sim.Tick);
        }
    }

    [Fact]
    public async Task BreakOnInvariant_MidTick_WithHandler()
    {
        var sim = TestModule.Build();
        var time = new TimeControl(paused: true);
        var hub = new ObservationHub(time);
        (ulong Tick, string Stage)? seen = null;
        hub.BreakHandler = (s, stage) => seen = (s.Tick, stage);
        hub.Attach(sim, new Catalog(sim.World, sim.CommandTypes), null);
        var ctx = new ServiceContext(sim);
        // X самого дальнего тела: 4 + (тик + 1) после S1 — больше 6 на тике 2.
        hub.DefineInvariant(ctx, Json("""{"id":"agent.near","condition":{"none":{"field":"Body.Position.X","greaterThan":6}},"after":"S1"}"""));
        var hit = hub.ArmInvariant(ctx, null, 50);
        time.RunUntil(50);
        Play(sim, time);
        var result = await hit;
        Assert.True(result.Met);
        Assert.Equal((2ul, "S1"), (result.Tick, result.Stage));
        Assert.Equal((2ul, "S1"), seen);
        var v = Assert.Single(result.Violations!);
        Assert.Equal(("agent.near", 2ul, "S1"), (v.Id, v.Tick, v.Stage));
        Assert.Equal(3ul, sim.Tick);                                         // обработчик вернулся — тик доигран
        Assert.Equal(v, hub.History.ViolationsAt(2).ToArray().Single());    // нарушение — в истории своего тика
    }

    [Fact]
    public async Task BreakOnInvariant_WithoutHandler_StopsAfterTick_OnlyChosenIds()
    {
        var sim = TestModule.Build();
        var time = new TimeControl(paused: true);
        var hub = new ObservationHub(time);
        hub.Attach(sim, new Catalog(sim.World, sim.CommandTypes), null);
        var ctx = new ServiceContext(sim);
        hub.DefineInvariant(ctx, Json("""{"id":"agent.near","condition":{"none":{"field":"Body.Position.X","greaterThan":6}},"after":"S1"}"""));
        hub.DefineInvariant(ctx, Json("""{"id":"agent.far","condition":{"none":{"field":"Body.Position.X","greaterThan":9}}}"""));
        var hit = hub.ArmInvariant(ctx, ["agent.far"], 50);                 // agent.near нарушается раньше — не он
        time.RunUntil(50);
        Play(sim, time);
        var result = await hit;
        Assert.True(result.Met);
        Assert.Null(result.Stage);                                           // живая игра встаёт после тика
        Assert.Equal(6ul, result.Tick);                                      // 4 + (5 + 1) > 9 — тик 5, сыграно 6
        Assert.Equal(("agent.far", 5ul, Simulation.ExportStage), (result.Violations![0].Id, result.Violations[0].Tick, result.Violations[0].Stage));
        Assert.Equal(6ul, sim.Tick);
        Assert.True(time.Paused);
        Assert.Throws<ArgumentException>(() => { _ = hub.ArmInvariant(ctx, ["no.such"], 10); });
    }

    [Fact]
    public void AgentInvariant_PerEntity_Errors_AndCarriedToNewSimulation()
    {
        var sim = TestModule.Build();
        var hub = new ObservationHub(new TimeControl(paused: false));
        var catalog = new Catalog(sim.World, sim.CommandTypes);
        hub.Attach(sim, catalog, null);
        var ctx = new ServiceContext(sim);
        // Скорость 0; 0,5; 1; 1,5; 2 — у двух последних не меньше 1,5.
        var slow = hub.DefineInvariant(ctx, Json("""{"id":"agent.slow","must":{"field":"Body.Speed","lessThan":1.5},"every":2}"""));
        Assert.Equal((InvariantScope.Entity, 2, Simulation.ExportStage), (slow.Scope, slow.Cost.Period, slow.After));
        var run = sim.Invariants.Run(["agent.slow"], sim.Tick, "между тиками", sim.Dt).Single();
        Assert.Equal([new Entity(3, 1), new Entity(4, 1)], run.Details.Select(d => d.Entity!.Value));
        // С where — только метки (чётные: скорость 0, 1, 2).
        hub.DefineInvariant(ctx, Json("""{"id":"agent.slow","must":{"field":"Body.Speed","lessThan":1.5},"where":{"component":"Marker"}}"""));
        Assert.Equal([new Entity(4, 1)], sim.Invariants.Run(["agent.slow"], 0, "—", sim.Dt).Single().Details.Select(d => d.Entity!.Value));
        Assert.Single(hub.DefinedInvariants);                                // тот же id заменён

        Assert.Contains("что-то одно", Assert.Throws<ArgumentException>(() => hub.DefineInvariant(ctx,
            Json("""{"id":"agent.x","condition":{"sim":"ticks","greaterThan":0},"must":{"component":"Body"}}"""))).Message);
        Assert.Contains("нет стадии", Assert.Throws<ArgumentException>(() => hub.DefineInvariant(ctx,
            Json("""{"id":"agent.x","condition":{"sim":"ticks","greaterThan":0},"after":"S9"}"""))).Message);
        Assert.Contains("лишний ключ", Assert.Throws<ArgumentException>(() => hub.DefineInvariant(ctx,
            Json("""{"id":"agent.x","condition":{"sim":"ticks","greaterThan":0},"stage":"S1"}"""))).Message);
        Assert.Contains("id занят", Assert.Throws<ArgumentException>(() => hub.DefineInvariant(ctx,
            Json("""{"id":"ecs.finite","condition":{"sim":"ticks","greaterThan":0}}"""))).Message);
        Assert.Contains("where", Assert.Throws<ArgumentException>(() => hub.DefineInvariant(ctx,
            Json("""{"id":"agent.x","must":{"sim":"ticks","greaterThan":0}}"""))).Message);

        // Перенос: инвариант агента и выключение кодового — в новой симуляции сеанса.
        hub.SetInvariantEnabled(ctx, "ecs.finite", false);
        var fresh = TestModule.Build();
        hub.Attach(fresh, new Catalog(fresh.World, fresh.CommandTypes), null);
        Assert.Equal(AgentInvariant.Owner, fresh.Invariants.Find("agent.slow")!.Owner);
        Assert.False(fresh.Invariants.Find("ecs.finite")!.Enabled);
        fresh.Step();
        Assert.Equal(1, fresh.Invariants.Find("agent.slow")!.Violations);

        Assert.True(hub.RemoveInvariant(new ServiceContext(fresh), "agent.slow"));
        Assert.Null(fresh.Invariants.Find("agent.slow"));
        Assert.Throws<ArgumentException>(() => hub.RemoveInvariant(new ServiceContext(fresh), "ecs.finite"));   // кодовый — только выключить
    }
}
