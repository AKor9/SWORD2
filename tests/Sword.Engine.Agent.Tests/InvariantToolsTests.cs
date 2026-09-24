using System.Text.Json.Nodes;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// Инструменты инвариантов (этап 3г) через MCP над подопытной симуляцией (<see cref="PushModule"/>: тела X = 0, 1, 2,
/// Push сдвигает все по X): список, включение, проверка сейчас, свой инвариант, остановка на нарушении посреди тика,
/// нарушения в истории.
/// </summary>
public sealed class InvariantToolsTests
{
    private static async Task<JsonObject> Ok(AgentFixture f, string name, JsonObject? arguments = null)
    {
        var (data, error) = await f.Tool(name, arguments);
        Assert.False(error, data.ToJsonString());
        return data;
    }

    private static async Task<string> Error(AgentFixture f, string name, JsonObject? arguments = null)
    {
        var (data, error) = await f.Tool(name, arguments);
        Assert.True(error, data.ToJsonString());
        return data["error"]!.GetValue<string>();
    }

    private static IEnumerable<(string Id, bool Enabled)> Ids(JsonObject list) =>
        list["invariants"]!.AsArray().Select(i => (i!["id"]!.GetValue<string>(), i["enabled"]!.GetValue<bool>()));

    [Fact]
    public async Task List_Enable_Run()
    {
        using var f = new AgentFixture();
        await Ok(f, "time.step", new JsonObject { ["ticks"] = 3 });
        var list = await Ok(f, "invariant.list");
        Assert.Equal([("ecs.finite", true), ("ecs.entityRefs", false)], Ids(list));
        var finite = list["invariants"]![0]!;
        // Исправления после Пд: ecs.finite — тяжёлый (раз в 4 тика), но период кодового — только в живой игре: здесь каждый тик.
        Assert.Equal(("engine", "раз в 4 тика", 1, "runtime.export", 3), (finite["owner"]!.GetValue<string>(), finite["cost"]!.GetValue<string>(),
            finite["every"]!.GetValue<int>(), finite["after"]!.GetValue<string>(), finite["evaluations"]!.GetValue<int>()));
        Assert.True(list["enabledByConfig"]!.GetValue<bool>());
        Assert.False(list["livePeriods"]!.GetValue<bool>());

        var on = await Ok(f, "invariant.enable", new JsonObject { ["ids"] = new JsonArray("ecs.*"), ["enabled"] = true });
        Assert.Equal(["ecs.finite", "ecs.entityRefs"], on["ids"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.All(Ids(await Ok(f, "invariant.list")), i => Assert.True(i.Enabled));
        await Ok(f, "invariant.enable", new JsonObject { ["ids"] = new JsonArray("all"), ["enabled"] = false });
        Assert.All(Ids(await Ok(f, "invariant.list")), i => Assert.False(i.Enabled));
        Assert.Contains("нет такого", await Error(f, "invariant.enable", new JsonObject { ["ids"] = new JsonArray("nope"), ["enabled"] = true }));
        Assert.Contains("ни одного включённого", await Error(f, "time.breakOnInvariant"));

        var run = await Ok(f, "invariant.run");                                     // и выключенные, и «по запросу»
        Assert.Equal((2, 0), (run["checked"]!.GetValue<int>(), run["violated"]!.GetValue<int>()));
    }

    /// <summary>
    /// Исправления после Пд: живая игра (<c>LivePeriods</c>, как Sword.exe) — <c>invariant.list</c> показывает <c>livePeriods</c> и
    /// <c>every</c> 4 у тяжёлого; <c>time.breakOnInvariant</c> на время ожидания проверяет тяжёлые каждый тик (точная остановка),
    /// после ответа — снова раз в 4.
    /// </summary>
    [Fact]
    public async Task LivePeriods_ListShowsEvery_BreakChecksEveryTickWhileWaiting()
    {
        using var f = new AgentFixture();
        f.Sim.Invariants.LivePeriods = true;   // до первого шага: симуляция на паузе
        var list = await Ok(f, "invariant.list");
        Assert.True(list["livePeriods"]!.GetValue<bool>());
        Assert.Equal(4, list["invariants"]![0]!["every"]!.GetValue<int>());
        await Ok(f, "invariant.define", new JsonObject
        {
            ["id"] = "agent.near", ["condition"] = JsonNode.Parse("""{"none":{"field":"Body.Position.X","greaterThan":5}}"""), ["after"] = "S",
        });
        await Ok(f, "command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 1 }, ["repeat"] = 10 });
        var hit = await Ok(f, "time.breakOnInvariant", new JsonObject { ["ids"] = new JsonArray("agent.near") });
        Assert.Equal((3ul, "S"), (hit["tick"]!.GetValue<ulong>(), hit["stage"]!.GetValue<string>()));
        // Тики 0, 1, 2 доиграны во время ожидания — ecs.finite проверен в каждом (без ожидания — только на тике 0).
        list = await Ok(f, "invariant.list", new JsonObject { ["filter"] = "ecs.finite" });
        Assert.Equal(3, list["invariants"]![0]!["evaluations"]!.GetValue<int>());
        Assert.True(list["livePeriods"]!.GetValue<bool>());   // после ответа — периоды вернулись
        await Ok(f, "time.step", new JsonObject { ["ticks"] = 5 });   // конец тика 3 и тики 4…7: раз в 4 — только тик 4
        list = await Ok(f, "invariant.list", new JsonObject { ["filter"] = "ecs.finite" });
        Assert.Equal(4, list["invariants"]![0]!["evaluations"]!.GetValue<int>());
    }

    [Fact]
    public async Task Define_BreakMidTick_ViolationInHistory()
    {
        using var f = new AgentFixture();
        // Дальнее тело X = 2 + (тик + 1) после стадии S — больше 5 на тике 3.
        var defined = await Ok(f, "invariant.define", new JsonObject
        {
            ["id"] = "agent.near", ["condition"] = JsonNode.Parse("""{"none":{"field":"Body.Position.X","greaterThan":5}}"""), ["after"] = "S",
        });
        Assert.Equal(("agent", "world", "S"), (defined["owner"]!.GetValue<string>(), defined["scope"]!.GetValue<string>(), defined["after"]!.GetValue<string>()));
        await Ok(f, "command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 1 }, ["repeat"] = 10 });

        var hit = await Ok(f, "time.breakOnInvariant", new JsonObject { ["ids"] = new JsonArray("agent.near") });
        Assert.True(hit["met"]!.GetValue<bool>());
        Assert.Equal((3ul, "S"), (hit["tick"]!.GetValue<ulong>(), hit["stage"]!.GetValue<string>()));
        Assert.Equal(("S", 3ul), (hit["receipt"]!["stage"]!.GetValue<string>(), hit["receipt"]!["tickId"]!.GetValue<ulong>()));
        var v = hit["violations"]!.AsArray().Single()!;
        Assert.Equal(("agent.near", 3ul, "S"), (v["id"]!.GetValue<string>(), v["tick"]!.GetValue<ulong>(), v["stage"]!.GetValue<string>()));

        // Посреди тика: мир после стадии, проверка сейчас видит то же нарушение, история — запись идущего тика.
        var run = await Ok(f, "invariant.run", new JsonObject { ["ids"] = new JsonArray("agent.near") });
        Assert.Equal(1, run["violated"]!.GetValue<int>());
        var events = await Ok(f, "events.history", new JsonObject { ["types"] = new JsonArray("InvariantViolation") });
        Assert.Equal(("InvariantViolation", 3ul), (events["events"]![0]!["type"]!.GetValue<string>(), events["events"]![0]!["tick"]!.GetValue<ulong>()));
        Assert.Contains("Body.Position.X", events["events"]![0]!["fields"]!["details"]!.GetValue<string>());

        await Ok(f, "time.step", new JsonObject { ["ticks"] = 1 });                  // доиграть тик 3
        var after = await Ok(f, "events.history", new JsonObject { ["types"] = new JsonArray("InvariantViolation") });
        Assert.Equal(1, after["total"]!.GetValue<int>());                           // теперь — из истории тика 3
        Assert.Contains("только entity", await Error(f, "events.history", new JsonObject
        {
            ["types"] = new JsonArray("InvariantViolation"), ["where"] = JsonNode.Parse("""{"field":"X","equals":1}"""),
        }));

        var list = await Ok(f, "invariant.list", new JsonObject { ["filter"] = "agent" });
        var agent = list["invariants"]![0]!;
        Assert.Equal(1, agent["violations"]!.GetValue<int>());
        Assert.NotNull(agent["definition"]!["condition"]);
        Assert.Equal("agent.near", list["recent"]![0]!["id"]!.GetValue<string>());

        Assert.True((await Ok(f, "invariant.define", new JsonObject { ["id"] = "agent.near", ["remove"] = true }))["removed"]!.GetValue<bool>());
        Assert.Contains("нет инварианта агента", await Error(f, "invariant.define", new JsonObject { ["id"] = "agent.near", ["remove"] = true }));
        Assert.Contains("id занят", await Error(f, "invariant.define", new JsonObject
        {
            ["id"] = "ecs.finite", ["condition"] = JsonNode.Parse("""{"sim":"ticks","greaterThan":0}"""),
        }));
    }

    [Fact]
    public async Task BreakOnInvariant_NotMet_WithinMaxTicks()
    {
        using var f = new AgentFixture();
        var miss = await Ok(f, "time.breakOnInvariant", new JsonObject { ["maxTicks"] = 5 });
        Assert.False(miss["met"]!.GetValue<bool>());
        Assert.Equal(5ul, miss["tick"]!.GetValue<ulong>());
        Assert.Contains("выключен", await Error(f, "time.breakOnInvariant", new JsonObject { ["ids"] = new JsonArray("ecs.entityRefs") }));
    }
}
