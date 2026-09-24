using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Агенту — трасса физики и суставы трупа (7и): расширение <c>physics.bodies</c> (раздел 8 плана — нового инструмента нет): схема
/// (<c>trace</c> — on, off, reset, show; <c>events</c> — 0…64), неверные значения — ошибки вызова; on — диагностический режим
/// <c>physics.trace</c> включён (мир и хеш те же), в ответе — last, window, run и контакты; reset — окно заново; off — выключен;
/// <c>traceOn</c> — в каждом ответе, <c>joints</c> — null у тела без суставов.
/// </summary>
public sealed class CorpseAgentTests
{
    [Fact]
    public async Task Agent_PhysicsBodiesTrace_SchemaLimitsOnShowResetOff()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=1"));
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true, sim.Tick),
            Service = new ServiceQueue(), Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"),
            Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        Agent.SwordAgent.Register(host);
        var tool = host.Tools.Single(t => t.Name == "physics.bodies");
        Assert.False(tool.ReadOnly);   // trace on/off/reset меняет режим сеанса — аннотация «меняет», как у profile.stages
        var args = tool.Arguments!;
        Assert.Equal(["on", "off", "reset", "show"], args["trace"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal((0, 64), (args["events"]!["minimum"]!.GetValue<int>(), args["events"]!["maximum"]!.GetValue<int>()));
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<JsonNode> Raw(string name, JsonObject a)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = name, ["arguments"] = a },
                };
                return JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
            }
            async Task<JsonNode> Call(string name, JsonObject a)
            {
                var result = await Raw(name, a);
                Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                return result["structuredContent"] ?? result;
            }
            async Task<string> Error(JsonObject a)
            {
                var result = await Raw("physics.bodies", a);
                Assert.True(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                return result.ToJsonString();
            }

            await Call("time.step", new JsonObject { ["ticks"] = 3 });
            var plain = await Call("physics.bodies", new JsonObject { ["entity"] = "enemy 1" });
            Assert.False(plain["traceOn"]!.GetValue<bool>());
            Assert.Null(plain["trace"]);
            Assert.Null(plain["joints"]);   // у живого суставов нет

            Assert.Contains("trace", await Error(new JsonObject { ["trace"] = "maybe" }));
            Assert.Contains("events", await Error(new JsonObject { ["trace"] = "show", ["events"] = 65 }));
            Assert.Contains("events", await Error(new JsonObject { ["trace"] = "show", ["events"] = -1 }));
            Assert.Contains("reset", await Error(new JsonObject { ["trace"] = "reset" }));   // выключенную окно не сбросить
            Assert.Contains("nobody", await Error(new JsonObject { ["entity"] = "nobody", ["trace"] = "on" }));
            Assert.False((await Call("physics.bodies", new JsonObject()))["traceOn"]!.GetValue<bool>());   // ошибка вызова — трасса не включена

            var hash = await Call("state.hash", new JsonObject());
            var on = (await Call("physics.bodies", new JsonObject { ["trace"] = "on", ["events"] = 2 }))["trace"]!;
            Assert.True(on["on"]!.GetValue<bool>());
            Assert.Equal("включена", on["action"]!.GetValue<string>());
            foreach (string period in (string[])["last", "window", "run"])
            {
                var p = on[period]!;
                Assert.NotNull(p["speed"]?["max"]);
                Assert.NotNull(p["energy"]);
                Assert.IsType<JsonArray>(p["sources"]);
                Assert.IsType<JsonArray>(p["top"]);
            }
            Assert.Equal(0, on["contacts"]!["count"]!.GetValue<int>());
            Assert.Equal(hash["total"]!.ToJsonString(), (await Call("state.hash", new JsonObject()))["total"]!.ToJsonString());   // режим хеш не меняет

            await Call("time.step", new JsonObject { ["ticks"] = 4 });
            var shown = await Call("physics.bodies", new JsonObject { ["entity"] = "player", ["trace"] = "show" });
            Assert.True(shown["traceOn"]!.GetValue<bool>());
            Assert.Null(shown["trace"]!["action"]);
            Assert.True(shown["trace"]!["recent"]!.AsArray().Count <= 8);
            Assert.Equal("окно начато заново", (await Call("physics.bodies", new JsonObject { ["trace"] = "reset" }))["trace"]!["action"]!.GetValue<string>());
            var off = await Call("physics.bodies", new JsonObject { ["trace"] = "off" });
            Assert.Equal("выключена", off["trace"]!["action"]!.GetValue<string>());
            Assert.False(off["trace"]!["on"]!.GetValue<bool>());
            Assert.Null(off["trace"]!["run"]);
            Assert.False((await Call("physics.bodies", new JsonObject()))["traceOn"]!.GetValue<bool>());
        }
        finally
        {
            stop.Cancel();
            loop.Join();
        }
    }
}
