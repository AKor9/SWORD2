using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests;

/// <summary>
/// Наблюдение (этап 3в): язык условий, история событий по журналам, узел наблюдения (условие остановки, останов посреди
/// тика, наблюдения), снимки и их разница. Подопытный — <see cref="TestModule"/>: тела X = 0…4, метки у чётных, Order в S1
/// двигает все тела по X.
/// </summary>
public class ObservationTests
{
    private static (Simulation Sim, Catalog Catalog) World()
    {
        var sim = TestModule.Build();
        return (sim, new Catalog(sim.World, sim.CommandTypes));
    }

    private static ObservationPredicate Compile(Simulation sim, Catalog catalog, string json, PredicateScope scope = PredicateScope.World) =>
        ObservationPredicate.Compile(JsonNode.Parse(json), new ServiceContext(sim), catalog, null, scope);

    private static bool Eval(Simulation sim, Catalog catalog, string json) => Compile(sim, catalog, json).Evaluate(new ServiceContext(sim));

    [Fact]
    public void Predicate_FieldsComponentsAggregatesSets()
    {
        var (sim, c) = World();
        Assert.True(Eval(sim, c, """{"field":"Body.Position.X","greaterOrEqual":3}"""));        // на сущность наверху — «есть такая»
        Assert.False(Eval(sim, c, """{"field":"Body.Position.X","greaterThan":4}"""));
        Assert.True(Eval(sim, c, """{"count":{"component":"Marker"},"equals":3}"""));
        Assert.True(Eval(sim, c, """{"count":{},"equals":5}"""));
        Assert.True(Eval(sim, c, """{"max":"Body.Position.X","equals":4}"""));
        Assert.True(Eval(sim, c, """{"mean":"Body.Speed","equals":1}"""));
        Assert.True(Eval(sim, c, """{"sum":"Body.Speed","where":{"component":"Marker"},"equals":3}"""));   // 0 + 1 + 2
        Assert.True(Eval(sim, c, """{"none":{"field":"Body.Position.X","lessThan":0}}"""));
        Assert.True(Eval(sim, c, """{"not":{"exists":{"field":"Body.Position.Y","notEquals":0}}}"""));
        Assert.True(Eval(sim, c, """{"all":[{"sim":"ticks","equals":0},{"resource":"SimTime.Tick","equals":0}]}"""));
        Assert.True(Eval(sim, c, """{"exists":{"field":"Body.Position","isFinite":true}}"""));   // структура — все листья конечны
        Assert.False(Eval(sim, c, """{"stage":"S1"}"""));                                         // между тиками стадии нет

        var marked = Compile(sim, c, """{"all":[{"component":"Marker"},{"field":"Body.Speed","between":[0.9,1.1]}]}""", PredicateScope.Entity);
        var ctx = new ServiceContext(sim);
        Assert.True(marked.Matches(ctx, new Entity(2, 1)));
        Assert.False(marked.Matches(ctx, new Entity(3, 1)));
        Assert.False(marked.Matches(ctx, new Entity(9, 1)));                                      // мёртвая — не подходит
        Assert.True(Compile(sim, c, """{"entity":"3v1"}""", PredicateScope.Entity).Matches(ctx, new Entity(3, 1)));
        Assert.Equal(new[] { c.Component("Marker").Id, c.Component("Body").Id }.Order(), marked.Required.Order());
    }

    [Theory]
    [InlineData("""{"feld":"Body.Speed"}""", "нет узла")]
    [InlineData("""{"field":"Body.Sped","lessThan":1}""", "нет поля «Sped»")]
    [InlineData("""{"field":"Body.Position","lessThan":1}""", "структура из 3 полей")]
    [InlineData("""{"field":"Order.Move.X","lessThan":1}""", "нет компонента")]
    [InlineData("""{"field":"Body.Speed"}""", "нет сравнения")]
    [InlineData("""{"field":"Body.Speed","lessThen":1}""", "не сравнение")]
    [InlineData("""{"stage":"Physics"}""", "нет стадии")]
    [InlineData("""{"event":"Nothing"}""", "журнала «Event<Nothing>» нет")]
    [InlineData("""{"all":[{"for":3,"that":{"sim":"ticks","greaterThan":0}},{"field":"Body.Speed","greaterThan":1}]}""", "только для условия мира")]
    [InlineData("""{"field":"Body.Speed","lessThan":1,"component":"Marker"}""", "два узла сразу")]
    [InlineData("""{"between":[1,2]}""", "нет узла")]
    public void Predicate_Errors_NameThePlace(string json, string expected)
    {
        var (sim, c) = World();
        var e = Assert.Throws<ArgumentException>(() => Compile(sim, c, json));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Predicate_EnumBoolEntityFields_OnRecords()
    {
        var (sim, c) = World();
        var hub = new ObservationHub(new TimeControl(paused: false));
        hub.Attach(sim, c, null);
        sim.Enqueue(new Order { Move = new Vector2(1, 0), Hurry = true, Gait = Gait.Run, Target = new Entity(3, 1) });
        sim.Step();
        var ctx = new ServiceContext(sim);
        Assert.True(Eval(sim, c, """{"event":"Order","where":{"all":[{"field":"Hurry","equals":true},{"field":"Gait","equals":"Run"},{"field":"Order.Move.X","equals":1}]}}"""));
        Assert.True(Eval(sim, c, """{"event":"Order","where":{"entity":"3v1"}}"""));
        Assert.False(Eval(sim, c, """{"event":"Order","where":{"field":"Target","equals":"2v1"}}"""));
        Assert.Throws<ArgumentException>(() => Compile(sim, c, """{"event":"Order","where":{"field":"Gait","equals":"Fly"}}"""));
        Assert.Throws<ArgumentException>(() => Compile(sim, c, """{"event":"Order","where":{"field":"Target","lessThan":"2v1"}}"""));
        Assert.Throws<ArgumentException>(() => Compile(sim, c, """{"event":"Order","where":{"component":"Body"}}"""));

        // Окно тиков по событиям: в тике 1 приказа нет — within 1 ложно, within 2 видит тик 0 в истории.
        sim.Step();
        Assert.False(Compile(sim, c, """{"event":"Order"}""").Evaluate(new ServiceContext(sim), hub.History));
        Assert.True(Compile(sim, c, """{"event":"Order","within":2}""").Evaluate(new ServiceContext(sim), hub.History));
        Assert.False(Compile(sim, c, """{"event":"Order","within":2,"atLeast":2}""").Evaluate(new ServiceContext(sim), hub.History));
    }

    [Fact]
    public void History_RecordsEventsWithTickAndStage()
    {
        var (sim, c) = World();
        var hub = new ObservationHub(new TimeControl(paused: false), historyTicks: 3);
        hub.Attach(sim, c, null);
        for (int t = 0; t < 5; t++)
        {
            if (t % 2 == 0) sim.Enqueue(new Order { Move = new Vector2(t, 0) });
            sim.Step();
        }
        var h = hub.History;
        Assert.Equal(2ul, h.Oldest);                                   // кольцо на 3 тика
        Assert.Equal(4ul, h.Newest);
        int order = sim.World.Journals.Single(j => j.Name.EndsWith("Order>")).Id;
        var (journal, stage) = h.RecordAt(4, 0, out var bytes);
        Assert.Equal(order, journal);
        Assert.Equal(Simulation.CommandStage, h.Stages[stage]);        // команда — событие служебной стадии команд
        Assert.Equal(4f, FieldCodec.Decode(typeof(Event<Order>), bytes)!["Value"]!["Move"]!["X"]!.GetValue<float>());
        Assert.Equal(0, h.CountAt(3));
        Assert.False(h.Has(1));
    }

    [Fact]
    public async Task Hub_RunUntil_HaltsOnHitTick_LimitGivesNotMet()
    {
        var (sim, c) = World();
        var time = new TimeControl(paused: true);
        var hub = new ObservationHub(time);
        hub.Attach(sim, c, null);
        var ctx = new ServiceContext(sim);
        var hit = hub.Arm(ctx, Compile(sim, c, """{"field":"Body.Position.X","greaterThan":10}"""), null, limit: 100);
        time.RunUntil(100);
        // Цикл, как у Headless: тик, пока время разрешает; приказ X+1 каждый тик.
        while (time.Allow(1) > 0)
        {
            sim.Enqueue(new Order { Move = new Vector2(1, 0) });
            sim.Step();
            time.Ran(sim.Tick);
        }
        Assert.True(hit.IsCompleted);
        var result = await hit;
        Assert.True(result.Met);
        Assert.Equal(7ul, result.Tick);                            // X = 4 + k > 10 → после 7 тиков
        Assert.Equal(7ul, sim.Tick);
        Assert.True(time.Paused);
        Assert.True(time.Stopping(7));

        var miss = hub.Arm(new ServiceContext(sim), Compile(sim, c, """{"sim":"ticks","greaterThan":1000}"""), null, limit: 10);
        time.RunUntil(10);
        while (time.Allow(1) > 0)
        {
            sim.Step();
            time.Ran(sim.Tick);
        }
        var missed = await miss;
        Assert.False(missed.Met);
        Assert.Equal(10ul, missed.Tick);
    }

    [Fact]
    public void Hub_Break_MidTick_SeesStage_CommandsOnlyNextTick()
    {
        var (sim, c) = World();
        var time = new TimeControl(paused: true);
        var hub = new ObservationHub(time);
        (ulong Tick, string? Stage, ulong Next, float X, bool CurrentRefused)? seen = null;
        hub.BreakHandler = (s, stage) =>
        {
            var ctx = new ServiceContext(s);
            bool refused = false;
            try
            {
                ctx.Enqueue(typeof(Order), s.Tick, new byte[FieldCodec.SizeOf(typeof(Order))]);
            }
            catch (ArgumentOutOfRangeException)
            {
                refused = true;
            }
            ctx.Enqueue(typeof(Order), ctx.NextTick, FieldCodec.Encode(typeof(Order), JsonNode.Parse("""{"Move":{"X":10}}""")));
            seen = (ctx.Tick, ctx.Stage, ctx.NextTick, s.World.Get<Body>(new Entity(0, 1)).Position.X, refused);
        };
        hub.Attach(sim, c, null);
        hub.Arm(new ServiceContext(sim), Compile(sim, c, """{"event":"Order"}"""), "S1", limit: 50);
        sim.Enqueue(new Order { Move = new Vector2(1, 0) });
        sim.Step();
        Assert.Equal((0ul, "S1", 1ul, 1f, true), seen);               // после барьера S1 тела уже сдвинуты; тик 0 ещё идёт
        Assert.Null(sim.Stage);
        sim.Step();
        Assert.Equal(11f, sim.World.Get<Body>(new Entity(0, 1)).Position.X);   // команда из останова — на тик 1
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new ObservationHub(time).Arm(new ServiceContext(sim), Compile(sim, c, """{"sim":"ticks","greaterThan":0}"""), "S1", 10);
        });   // без обработчика останова — нельзя
    }

    [Fact]
    public async Task Hub_ForWindow_CountsConsecutiveTicks()
    {
        var (sim, c) = World();
        var time = new TimeControl(paused: true);
        var hub = new ObservationHub(time);
        hub.Attach(sim, c, null);
        // Держится 3 тика подряд: приказы в тиках 0, 1, 3, 4, 5 — выполняется после тика 5 (серия 3, 4, 5).
        var hit = hub.Arm(new ServiceContext(sim), Compile(sim, c, """{"for":3,"that":{"event":"Order"}}"""), null, limit: 20);
        time.RunUntil(20);
        while (time.Allow(1) > 0)
        {
            if (sim.Tick != 2) sim.Enqueue(new Order { Move = new Vector2(1, 0) });
            sim.Step();
            time.Ran(sim.Tick);
        }
        var result = await hit;
        Assert.True(result.Met);
        Assert.Equal(6ul, result.Tick);
    }

    /// <summary>
    /// Окно for внутри all/any считает каждый тик, даже когда all/any остановились на соседнем узле: итог не зависит
    /// от порядка узлов. Внутреннее условие истинно с тика 1 — окно из 3 тиков держится уже к тику 3.
    /// </summary>
    [Theory]
    [InlineData("""{"all":[{"sim":"ticks","greaterOrEqual":5},{"for":3,"that":{"sim":"ticks","greaterOrEqual":0}}]}""", 5ul)]
    [InlineData("""{"all":[{"for":3,"that":{"sim":"ticks","greaterOrEqual":0}},{"sim":"ticks","greaterOrEqual":5}]}""", 5ul)]
    [InlineData("""{"all":[{"sim":"ticks","greaterOrEqual":4},{"any":[{"sim":"ticks","lessOrEqual":3},{"for":4,"that":{"sim":"ticks","greaterOrEqual":0}}]}]}""", 4ul)]
    [InlineData("""{"all":[{"sim":"ticks","greaterOrEqual":2},{"for":2,"that":{"for":3,"that":{"sim":"ticks","greaterOrEqual":0}}}]}""", 4ul)]
    public async Task Hub_ForWindow_InsideAllAny_CountsEveryTick(string json, ulong expected)
    {
        var (sim, c) = World();
        var time = new TimeControl(paused: true);
        var hub = new ObservationHub(time);
        hub.Attach(sim, c, null);
        var hit = hub.Arm(new ServiceContext(sim), Compile(sim, c, json), null, limit: 20);
        time.RunUntil(20);
        while (time.Allow(1) > 0)
        {
            sim.Step();
            time.Ran(sim.Tick);
        }
        var result = await hit;
        Assert.True(result.Met);
        Assert.Equal(expected, result.Tick);
    }

    [Fact]
    public void Watch_SamplesRows_ReattachesToNewSimulation()
    {
        var (sim, c) = World();
        var hub = new ObservationHub(new TimeControl(paused: false));
        hub.Attach(sim, c, null);
        var w = hub.AddWatch(new ServiceContext(sim), (JsonObject)JsonNode.Parse("""{"where":{"component":"Marker"},"fields":["Body.Position.X","Body.Speed"],"every":2}""")!);
        Assert.Equal(["Body.Position.X", "Body.Speed"], w.Fields.Select(f => f.Path));
        for (int t = 0; t < 4; t++)
        {
            sim.Enqueue(new Order { Move = new Vector2(1, 0) });
            sim.Step();
        }
        Assert.Equal(6, w.Rows);                                       // тики 2 и 4, по 3 тела с меткой
        var rows = w.Read(new Entity(2, 1), 0, ulong.MaxValue).ToList();
        Assert.Equal([2ul, 4ul], rows.Select(r => r.TickId));
        Assert.Equal(6f, BitConverter.UInt32BitsToSingle((uint)rows[1].Values[0]!.Value));   // 2 + 4
        Assert.True(w.Covers(new Entity(4, 1)));
        Assert.False(w.Covers(new Entity(1, 1)));

        var (fresh, fc) = World();
        hub.Attach(fresh, fc, null);
        Assert.Equal(0, w.Rows);                                       // новая симуляция — строки заново
        Assert.Throws<ArgumentException>(() => hub.AddWatch(new ServiceContext(fresh), (JsonObject)JsonNode.Parse("""{"fields":["Body.Nope"]}""")!));
    }

    [Fact]
    public void Observers_DoNotChangeStateHash()
    {
        var (a, ca) = World();
        var (b, _) = World();
        var hub = new ObservationHub(new TimeControl(paused: false));
        hub.Attach(a, ca, null);
        hub.AddWatch(new ServiceContext(a), (JsonObject)JsonNode.Parse("""{"fields":["Body"]}""")!);
        hub.Arm(new ServiceContext(a), Compile(a, ca, """{"event":"Order","within":5}"""), null, 1000);
        for (int t = 0; t < 30; t++)
        {
            var o = new Order { Move = new Vector2(t % 3, 0), Hurry = t % 2 == 0 };
            a.Enqueue(o);
            b.Enqueue(o);
            a.Step();
            b.Step();
            Assert.Equal(b.HashState().Total, a.HashState().Total);
        }
    }

    [Fact]
    public void Snapshot_Diff_ShowsChangedFieldsWithDelta()
    {
        var (sim, c) = World();
        var before = WorldSnapshot.Take(new ServiceContext(sim), "a");
        sim.Enqueue(new Order { Move = new Vector2(1, 0), Hurry = true });
        sim.Step();
        var after = WorldSnapshot.Take(new ServiceContext(sim), "b");
        var flat = after.Flatten(c);
        Assert.Equal(2f, flat["0v1/Body/Position.X"]!.GetValue<float>());
        Assert.True(flat["0v1/Marker"]!.GetValue<bool>());

        var d = SnapshotDiff.Compare(before.Flatten(c), flat, "Body/Position", 0);
        Assert.Equal(5, d.Changed);                                    // X у всех пяти тел; Y и Z те же
        var row = d.Rows.Single(r => r.Key == "3v1/Body/Position.X");
        Assert.Equal(2, row.Delta);
        Assert.Equal(0, SnapshotDiff.Compare(before.Flatten(c), flat, "Body/Position", tolerance: 2).Changed);
        Assert.Contains(SnapshotDiff.Compare(before.Flatten(c), flat, null, 0).Rows, r => r.Key.StartsWith("resource/SimTime", StringComparison.Ordinal));
        Assert.Empty(d.EntitiesAdded);
    }
}
