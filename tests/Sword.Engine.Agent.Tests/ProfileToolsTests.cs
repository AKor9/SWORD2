using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// <c>profile.stages</c> (этап П, точка Па) на Headless: выключен по умолчанию; включение — сразу (симуляция на паузе, между
/// тиками); после шагов — зоны тика, стадий, систем, барьеров, инвариантов, реплея и наблюдения агента; фильтр по началу
/// имени, история, файл отчёта; выключение — тики больше не считаются; профайлер подключён и к новой симуляции сеанса.
/// </summary>
public class ProfileToolsTests
{
    [Fact]
    public async Task ProfileStages_Headless_EnableStepReport()
    {
        using var f = new AgentFixture();
        var (off, error) = await f.Tool("profile.stages");
        Assert.False(error, off.ToJsonString());
        Assert.False(off["enabled"]!.GetValue<bool>());
        Assert.Equal(0, off["ticks"]!.GetValue<int>());

        var (on, _) = await f.Tool("profile.stages", new JsonObject { ["enable"] = true });
        Assert.True(on["enabled"]!.GetValue<bool>());   // Headless на паузе — между тиками: сразу
        Assert.Null(on["note"]);
        await f.Tool("time.step", new JsonObject { ["ticks"] = 30 });

        var (r, _) = await f.Tool("profile.stages", new JsonObject { ["history"] = 5, ["write"] = true, ["top"] = 100 });
        Assert.Equal("tick", r["per"]!.GetValue<string>());
        Assert.Equal(30, r["periods"]!.GetValue<int>());
        Assert.Equal(5, r["history"]!.AsArray().Count);
        Assert.Equal(29, r["history"]![4]!["tick"]!.GetValue<int>());
        var zones = r["zones"]!.AsArray().ToDictionary(z => z!["name"]!.GetValue<string>(), z => z!.AsObject());
        foreach (string zone in (string[])["tick", "stage.S", "push.mover@S", "barrier.S", "stage.runtime.export", "invariant.ecs.finite",
                     "sim.record", "sim.observers"])
            Assert.True(zones.ContainsKey(zone), $"нет зоны {zone}: {string.Join(", ", zones.Keys)}");
        Assert.Equal("stage.S", zones["push.mover@S"]["parent"]!.GetValue<string>());
        Assert.True(zones["tick"]["meanMs"]!.GetValue<double>() >= zones["stage.S"]["meanMs"]!.GetValue<double>());
        Assert.Equal(0, r["mismatches"]!.GetValue<int>());
        string file = r["file"]!.GetValue<string>();
        var saved = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(CpuProfiler.ReportFormat, saved["format"]!.GetValue<string>());
        Assert.Equal("headless", saved["process"]!.GetValue<string>());
        Assert.Equal(30, saved["ticks"]!["count"]!.GetValue<int>());

        var (stages, _) = await f.Tool("profile.stages", new JsonObject { ["prefix"] = "stage." });
        Assert.All(stages["zones"]!.AsArray(), z => Assert.StartsWith("stage.", z!["name"]!.GetValue<string>()));

        var (disabled, _) = await f.Tool("profile.stages", new JsonObject { ["enable"] = false });
        Assert.False(disabled["enabled"]!.GetValue<bool>());
        await f.Tool("time.step", new JsonObject { ["ticks"] = 10 });
        var (after, _) = await f.Tool("profile.stages");
        Assert.Equal(30, after["ticks"]!.GetValue<int>());

        var (bad, isError) = await f.Tool("profile.stages", new JsonObject { ["per"] = "week" });
        Assert.True(isError, bad.ToJsonString());
    }

    [Fact]
    public async Task ProfileStages_FollowsSessionSimulation()
    {
        static Runtime.Simulation Build(Runtime.ConfigSet config)
        {
            var sim = Runtime.Simulation.Build(config, ["S"], [new PushModule()]);
            config.Complete();
            return sim;
        }
        var first = Build(Runtime.ConfigSet.Empty());
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Introspection.Catalog(first.World, first.CommandTypes),
            Time = new Introspection.TimeControl(paused: true, 0), Service = new Introspection.ServiceQueue(), Log = new LogRing(16),
            ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-agent-tests"), Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        var session = new AgentSimulation(first, new Runtime.ReplayInfo("1.0.0+test", "tests", "none"), Build, sets => Runtime.ConfigSet.Empty([.. sets]));
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(session, host, stop.Token)) { IsBackground = true };
        loop.Start();
        int id = 0;
        async Task<JsonObject> Tool(string name, JsonObject arguments)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            };
            var result = JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
            Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }
        try
        {
            await Tool("profile.stages", new JsonObject { ["enable"] = true });
            await Tool("time.step", new JsonObject { ["ticks"] = 10 });
            // Перемотка назад — новая симуляция сеанса: профайлер подключён к ней до первого её тика (повтор до тика 4 — тоже).
            await Tool("time.seek", new JsonObject { ["tick"] = 4 });
            Assert.NotSame(first, session.Current);
            await Tool("profile.stages", new JsonObject { ["reset"] = true });
            await Tool("time.step", new JsonObject { ["ticks"] = 6 });
            var r = await Tool("profile.stages", new JsonObject { ["prefix"] = "push." });
            Assert.Equal(6, r["periods"]!.GetValue<int>());
            Assert.Equal("push.mover@S", r["zones"]![0]!["name"]!.GetValue<string>());
            Assert.Same(host.Profiler, session.Current.Profiler);
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }
}
