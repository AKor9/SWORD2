using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// Инструменты наблюдения (этап 3в) через MCP над подопытной симуляцией (<see cref="PushModule"/>: тела X = 0, 1, 2; Push
/// сдвигает все по X): условие в time.runUntil, останов посреди тика, наблюдения и история, события, снимки, where и
/// агрегаты в world.query.
/// </summary>
public class ObservationToolsTests
{
    private static async Task<JsonObject> Ok(AgentFixture f, string tool, JsonObject? args = null)
    {
        var (data, error) = await f.Tool(tool, args);
        Assert.False(error, data.ToJsonString());
        return data;
    }

    private static async Task<string> Error(AgentFixture f, string tool, JsonObject? args = null)
    {
        var (data, error) = await f.Tool(tool, args);
        Assert.True(error, data.ToJsonString());
        return data["error"]!.GetValue<string>();
    }

    private static Task<JsonObject> Push(AgentFixture f, float x, ulong tick, int repeat = 1) =>
        Ok(f, "command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = x }, ["tick"] = tick, ["repeat"] = repeat });

    private static float X(JsonObject entity) => entity["components"]!["Body"]!["Position"]!["X"]!.GetValue<float>();

    [Fact]
    public async Task RunUntil_Condition_StopsOnHitTick_LimitGivesNotMet()
    {
        using var f = new AgentFixture();
        await Push(f, 1, 0, repeat: 100);
        var hit = await Ok(f, "time.runUntil", new JsonObject
        {
            ["condition"] = JsonNode.Parse("""{"field":"Body.Position.X","greaterThan":10}"""),
        });
        Assert.True(hit["met"]!.GetValue<bool>());
        Assert.Equal(9ul, hit["tick"]!.GetValue<ulong>());                  // тело X = 2 + k > 10 → после 9 тиков
        Assert.Equal(9ul, hit["receipt"]!["tickId"]!.GetValue<ulong>());
        var status = await Ok(f, "session.status");
        Assert.Equal(9ul, status["tick"]!.GetValue<ulong>());
        Assert.True(status["paused"]!.GetValue<bool>());
        Assert.Equal(11f, X(await Ok(f, "entity.get", new JsonObject { ["entity"] = "2v1" })));

        var miss = await Ok(f, "time.runUntil", new JsonObject
        {
            ["condition"] = JsonNode.Parse("""{"sim":"ticks","greaterThan":1000}"""), ["maxTicks"] = 5,
        });
        Assert.False(miss["met"]!.GetValue<bool>());
        Assert.Equal(14ul, miss["tick"]!.GetValue<ulong>());
        Assert.Contains("нет узла", await Error(f, "time.runUntil", new JsonObject { ["condition"] = new JsonObject { ["x"] = 1 } }));
        Assert.Contains("что-то одно", await Error(f, "time.runUntil", new JsonObject { ["tick"] = 3, ["condition"] = new JsonObject { ["sim"] = "ticks", ["equals"] = 1 } }));
    }

    [Fact]
    public async Task Break_MidTick_ReceiptHasStage_CommandsGoToNextTick_StepFinishesTick()
    {
        using var f = new AgentFixture();
        await Push(f, 1, 3);
        var hit = await Ok(f, "time.break", new JsonObject { ["stage"] = "S", ["condition"] = JsonNode.Parse("""{"event":"Push"}""") });
        Assert.True(hit["met"]!.GetValue<bool>());
        Assert.Equal(3ul, hit["tick"]!.GetValue<ulong>());
        Assert.Equal("S", hit["stage"]!.GetValue<string>());
        Assert.Equal("S", hit["receipt"]!["stage"]!.GetValue<string>());

        var body = await Ok(f, "entity.get", new JsonObject { ["entity"] = "first" });
        Assert.Equal(1f, X(body));                                          // после барьера S толчок тика 3 уже применён
        Assert.Equal("S", body["receipt"]!["stage"]!.GetValue<string>());
        Assert.Equal(3ul, body["receipt"]!["tickId"]!.GetValue<ulong>());
        await Ok(f, "state.hash");                                           // хеш посреди тика — можно
        // История посреди тика видит и записи идущего тика — как условие {event}, которое здесь остановило.
        var events = await Ok(f, "events.history", new JsonObject { ["types"] = new JsonArray("Push") });
        var running = events["events"]!.AsArray().Single()!;
        Assert.Equal(3ul, running["tick"]!.GetValue<ulong>());
        Assert.Equal(Simulation.CommandStage, running["stage"]!.GetValue<string>());
        Assert.Equal(2ul, events["window"]!["newest"]!.GetValue<ulong>());   // сыгранные тики — по 2
        var sent = await Ok(f, "command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 5 } });
        Assert.Equal(4ul, sent["firstTick"]!.GetValue<ulong>());             // идущий тик 3 уже выдал команды
        Assert.Contains("идёт", await Error(f, "command.send", new JsonObject { ["command"] = "Push", ["tick"] = 3 }));

        var step = await Ok(f, "time.step", new JsonObject { ["ticks"] = 1 });   // доиграть тик 3
        Assert.Equal(4ul, step["tick"]!.GetValue<ulong>());
        Assert.Null(step["receipt"]!["stage"]);
        await Ok(f, "time.step", new JsonObject { ["ticks"] = 1 });
        Assert.Equal(6f, X(await Ok(f, "entity.get", new JsonObject { ["entity"] = "first" })));

        // Без условия — останов на ближайшем тике; следующий break — следующее попадание.
        var next = await Ok(f, "time.break", new JsonObject { ["stage"] = Simulation.CommandStage });
        Assert.Equal(5ul, next["tick"]!.GetValue<ulong>());
        Assert.Contains("нет стадии", await Error(f, "time.break", new JsonObject { ["stage"] = "Physics" }));
    }

    [Fact]
    public async Task Watch_EntityHistory_ChangesOnly()
    {
        using var f = new AgentFixture();
        var w = await Ok(f, "watch.add", new JsonObject { ["entities"] = new JsonArray("first"), ["fields"] = new JsonArray("Body.Position.X") });
        Assert.Equal("w1", w["id"]!.GetValue<string>());
        Assert.Equal("Body.Position.X", w["fields"]![0]!.GetValue<string>());
        await Push(f, 1, 0, repeat: 3);
        await Ok(f, "time.step", new JsonObject { ["ticks"] = 5 });
        var h = await Ok(f, "entity.history", new JsonObject { ["entity"] = "first" });
        Assert.Equal(5, h["total"]!.GetValue<int>());
        Assert.Equal([1f, 2f, 3f, 3f, 3f], h["rows"]!.AsArray().Select(r => r!["values"]!["Body.Position.X"]!.GetValue<float>()));
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul], h["rows"]!.AsArray().Select(r => r!["tickId"]!.GetValue<ulong>()));
        var changes = await Ok(f, "entity.history", new JsonObject { ["entity"] = "0v1", ["changes"] = true });
        Assert.Equal(3, changes["total"]!.GetValue<int>());
        var list = await Ok(f, "watch.list");
        Assert.Equal(5, list["watches"]![0]!["rows"]!.GetValue<int>());
        Assert.Contains("не наблюдается", await Error(f, "entity.history", new JsonObject { ["entity"] = "1v1" }));
        Assert.Equal(1, (await Ok(f, "watch.remove", new JsonObject { ["id"] = "all" }))["removed"]!.GetValue<int>());
        Assert.Contains("не наблюдается", await Error(f, "entity.history", new JsonObject { ["entity"] = "first" }));
    }

    [Fact]
    public async Task EventsHistory_TypesWhereWindow()
    {
        using var f = new AgentFixture();
        await Push(f, 1, 1);
        await Push(f, 2, 2);
        await Ok(f, "time.step", new JsonObject { ["ticks"] = 3 });
        var all = await Ok(f, "events.history", new JsonObject { ["types"] = new JsonArray("Push") });
        Assert.Equal(2, all["total"]!.GetValue<int>());
        var first = all["events"]![0]!;
        Assert.Equal(1ul, first["tick"]!.GetValue<ulong>());
        Assert.Equal(Simulation.CommandStage, first["stage"]!.GetValue<string>());
        Assert.Equal("Event<Push>", first["type"]!.GetValue<string>());
        Assert.Equal(1f, first["fields"]!["Value"]!["X"]!.GetValue<float>());
        Assert.Equal(0ul, all["window"]!["oldest"]!.GetValue<ulong>());
        Assert.Equal(2ul, all["window"]!["newest"]!.GetValue<ulong>());

        var big = await Ok(f, "events.history", new JsonObject { ["types"] = new JsonArray("Push"), ["where"] = JsonNode.Parse("""{"field":"X","greaterThan":1.5}""") });
        Assert.Equal(2ul, big["events"]!.AsArray().Single()!["tick"]!.GetValue<ulong>());
        var range = await Ok(f, "events.history", new JsonObject { ["from"] = 2, ["to"] = 2 });
        Assert.Equal(1, range["total"]!.GetValue<int>());
        Assert.Contains("только вместе с types", await Error(f, "events.history", new JsonObject { ["where"] = new JsonObject { ["field"] = "X", ["equals"] = 1 } }));
        Assert.Contains("нет полей-сущностей", await Error(f, "events.history", new JsonObject { ["types"] = new JsonArray("Push"), ["entity"] = "first" }));
    }

    [Fact]
    public async Task Snapshot_Diff_WorldQueryWhereAggregate()
    {
        using var f = new AgentFixture();
        var s1 = await Ok(f, "world.snapshot");
        Assert.Equal("s1", s1["id"]!.GetValue<string>());
        Assert.True(File.Exists(s1["file"]!.GetValue<string>()));
        await Push(f, 1, 0);
        await Ok(f, "time.step", new JsonObject { ["ticks"] = 1 });
        var diff = await Ok(f, "world.diff", new JsonObject { ["a"] = "s1", ["b"] = "now", ["filter"] = "/Body/" });
        Assert.Equal(3, diff["changed"]!.GetValue<int>());
        var row = diff["rows"]!.AsArray().Single(r => r!["key"]!.GetValue<string>() == "0v1/Body/Position.X")!;
        Assert.Equal(0f, row["a"]!.GetValue<float>());
        Assert.Equal(1f, row["b"]!.GetValue<float>());
        Assert.Equal(1.0, row["delta"]!.GetValue<double>());
        Assert.Contains("нет снимка", await Error(f, "world.diff", new JsonObject { ["a"] = "s9", ["b"] = "now" }));

        var q = await Ok(f, "world.query", new JsonObject
        {
            ["where"] = JsonNode.Parse("""{"field":"Body.Position.X","greaterOrEqual":2}"""), ["aggregate"] = new JsonArray("Body.Position.X"),
        });
        Assert.Equal(2, q["total"]!.GetValue<int>());                        // X = 1, 2, 3 → два тела
        var agg = q["aggregate"]!["Body.Position.X"]!;
        Assert.Equal(2, agg["count"]!.GetValue<int>());
        Assert.Equal(2.0, agg["min"]!.GetValue<double>());
        Assert.Equal(2.5, agg["mean"]!.GetValue<double>());
        Assert.Contains("не числовой лист", await Error(f, "world.query", new JsonObject { ["aggregate"] = new JsonArray("Body.Position") }));
    }

    [Fact]
    public async Task Break_InLiveGame_IsRefused()
    {
        // Живая игра: хозяин цикла — клиент, у хоста нет симуляции сеанса — останова посреди тика нет.
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(false), Service = new ServiceQueue(),
            Log = new LogRing(8), ArtifactsRoot = Path.GetTempPath(), Stop = () => { },
        });
        var response = await host.Server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"time.break","arguments":{"stage":"S"}}}""");
        var result = JsonNode.Parse(response!)!["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("только Sword.Headless", result["structuredContent"]!["error"]!.GetValue<string>());
    }
}
