using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// Приёмка и эксперименты через агента (этап 3д): feature.list/verify над манифестом во временном каталоге,
/// experiment.fork/compare/sweep над записью сеанса — подопытная симуляция <see cref="PushModule"/> с построителем, как у
/// Headless.
/// </summary>
public sealed class FeatureToolsTests : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _loop;
    private readonly AgentHost _host;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-feature-tools-tests", Guid.NewGuid().ToString("N")[..8]);
    private int _id;
    private bool _attachedAtStart;

    private static Simulation Build(ConfigSet config)
    {
        var sim = Simulation.Build(config, ["S"], [new PushModule()]);
        config.Complete();
        return sim;
    }

    public FeatureToolsTests()
    {
        var sim = Build(ConfigSet.Empty());
        _host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless",
            Catalog = new Catalog(sim.World, sim.CommandTypes),
            Time = new TimeControl(paused: true, sim.Tick),
            Service = new ServiceQueue(),
            Log = new LogRing(64),
            ArtifactsRoot = Path.Combine(_root, "artifacts"),
            Stop = () => { },
            ServiceTimeout = TimeSpan.FromSeconds(5),
        }) { Names = new FirstName() };
        var session = new AgentSimulation(sim, new ReplayInfo("1.0.0+test", "tests", "none"), Build, sets => ConfigSet.Empty([.. sets]));
        // Как Headless: вызовы — после started, когда сеанс подключён.
        using var started = new ManualResetEventSlim();
        _loop = new Thread(() => SimulationLoop.Run(session, _host, _stop.Token, started: () =>
        {
            _attachedAtStart = _host.Simulation is { CanRebuild: true };
            started.Set();
        })) { IsBackground = true };
        _loop.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SessionAttachedBeforeTransportStarts()
    {
        // Headless запускает stdio в started: первый же вызов пересборки после initialize не должен видеть «живую игру».
        Assert.True(_attachedAtStart);
        var r = await Tool("experiment.sweep", new JsonObject { ["params"] = new JsonObject { ["sim.seed"] = new JsonArray(1) }, ["repeats"] = 1, ["ticks"] = 10, ["warmup"] = 0 });
        Assert.Single(r["rows"]!.AsArray());
    }

    public void Dispose()
    {
        _stop.Cancel();
        _host.Service.Wake();
        _loop.Join(TimeSpan.FromSeconds(5));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<JsonObject> Tool(string name, JsonObject? arguments = null, bool error = false)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments ?? [] },
        };
        var r = JsonNode.Parse((await _host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
        Assert.True(error == (r["isError"]?.GetValue<bool>() ?? false), r.ToJsonString());
        return error ? new JsonObject { ["text"] = r["content"]![0]!["text"]!.GetValue<string>() } : r["structuredContent"]!.AsObject();
    }

    private void Manifest()
    {
        string dir = Path.Combine(_root, "features", "push");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), "# толчки\r\n");
        File.WriteAllText(Path.Combine(dir, FeatureManifest.FileName), """
            {
              "format": "sword-feature", "version": 1, "id": "push", "title": "толчки", "doc": "README.md",
              "sourceRoots": ["features/push"],
              "scenarios": [
                {
                  "id": "push", "ticks": 20,
                  "commands": [{ "command": "Push", "fields": { "X": 0.5 }, "from": 0, "to": 10 }],
                  "checks": [{ "id": "moved", "expect": "eventually", "condition": { "all": [{ "entity": "first" }, { "field": "Body.Position.X", "greaterOrEqual": 5 }] } }]
                }
              ],
              "invariants": ["ecs.finite"],
              "metrics": [{ "id": "x", "scenario": "push", "reference": "тест", "entity": "first", "field": "Body.Position.X", "expected": 5 }],
              "views": [{ "id": "end", "scenario": "push", "tick": 20 }],
              "budgets": [{ "id": "push", "scenario": "push", "allocatedBytesPerTick": 0, "repeats": 2, "warmup": 5 }],
              "checklist": ["посмотреть"]
            }
            """);
        _host.Features = new FeatureSetup(_root, [Path.Combine(_root, "features")], CaptureView: null, Optimized: false);
    }

    [Fact]
    public async Task FeatureList_Verify_ViewsNeedApp()
    {
        Assert.Contains("фич нет", (await Tool("feature.list", error: true))["text"]!.GetValue<string>());
        Manifest();
        var list = await Tool("feature.list");
        var f = Assert.Single(list["features"]!.AsArray())!;
        Assert.Equal(("push", 1, 1), (f["id"]!.GetValue<string>(), f["views"]!.GetValue<int>(), f["budgets"]!.GetValue<int>()));
        var full = await Tool("feature.list", new JsonObject { ["id"] = "push" });
        Assert.EndsWith(FeatureManifest.FileName, full["path"]!.GetValue<string>());
        Assert.Equal("sword-feature", full["format"]!.GetValue<string>());

        var ok = await Tool("feature.verify", new JsonObject { ["id"] = "push", ["views"] = false });
        Assert.True(ok["ok"]!.GetValue<bool>(), ok["failures"]!.ToJsonString());
        Assert.Equal(5.0, ok["metrics"]![0]!["value"]!.GetValue<double>());
        Assert.True(File.Exists(Path.Combine(ok["artifacts"]!.GetValue<string>(), "report.json")));
        Assert.Equal(0.0, ok["budgets"]![0]!["allocatedBytesPerTick"]!.GetValue<double>());

        // Виды без App — провал с причиной, а не тихий пропуск.
        var noApp = await Tool("feature.verify", new JsonObject { ["id"] = "push", ["budgets"] = false });
        Assert.False(noApp["ok"]!.GetValue<bool>());
        Assert.Contains("вид end: App нет", noApp["failures"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Assert.Contains("нет фичи «nope»", (await Tool("feature.verify", new JsonObject { ["id"] = "nope" }, error: true))["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Fork_Compare_Sweep_OverSessionRecording()
    {
        await Tool("command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 1 }, ["tick"] = 0, ["repeat"] = 40 });
        await Tool("time.step", new JsonObject { ["ticks"] = 40 });
        await Tool("replay.save", new JsonObject { ["name"] = "base" });

        var fork = await Tool("experiment.fork", new JsonObject
        {
            ["replay"] = "base.swreplay", ["at"] = 20,
            ["variants"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "double",
                    ["commands"] = new JsonArray(new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 2 }, ["from"] = 20, ["to"] = 21 }),
                },
                new JsonObject { ["name"] = "seed", ["set"] = new JsonArray("sim.seed=5") }),
            ["measure"] = new JsonArray(new JsonObject { ["entity"] = "first", ["field"] = "Body.Position.X" }),
        });
        var branches = fork["branches"]!.AsArray();
        Assert.Equal(["base", "double", "seed"], branches.Select(b => b!["name"]!.GetValue<string>()));
        Assert.Equal(40.0, branches[0]!["measure"]!["first:Body.Position.X"]!.GetValue<double>());
        Assert.Equal(41.0, branches[1]!["measure"]!["first:Body.Position.X"]!.GetValue<double>());
        Assert.Equal(21ul, branches[1]!["vsBase"]!["firstHashTick"]!.GetValue<ulong>());   // команда тика 20 — после 21 тика
        Assert.Equal(0ul, branches[2]!["vsBase"]!["firstHashTick"]!.GetValue<ulong>());    // зерно — с тика 0
        Assert.All(branches, b => Assert.True(File.Exists(b!["file"]!.GetValue<string>())));

        var compare = await Tool("experiment.compare", new JsonObject
        {
            ["a"] = branches[0]!["file"]!.GetValue<string>(), ["b"] = branches[1]!["file"]!.GetValue<string>(),
        });
        Assert.Equal(21ul, compare["describedAt"]!.GetValue<ulong>());
        var ecs = compare["describe"]![0]!;
        Assert.Equal("ecs", ecs["part"]!.GetValue<string>());
        Assert.Contains(ecs["lines"]!.AsArray(), l => l!["path"]!.GetValue<string>().Contains("Position.X")
            && l["a"]!.GetValue<string>() == "21" && l["b"]!.GetValue<string>() == "22");

        var sweep = await Tool("experiment.sweep", new JsonObject
        {
            ["params"] = new JsonObject { ["sim.seed"] = new JsonArray(1, 2) }, ["ticks"] = 30, ["repeats"] = 2, ["warmup"] = 0,
        });
        var rows = sweep["rows"]!.AsArray();
        Assert.Equal(["sim.seed=1", "sim.seed=2"], rows.Select(r => r!["set"]![0]!.GetValue<string>()));
        Assert.All(rows, r => Assert.True(r!["deterministic"]!.GetValue<bool>()));
        await Tool("experiment.fork", new JsonObject { ["replay"] = "base.swreplay", ["at"] = 20, ["variants"] = new JsonArray() }, error: true);
    }
}
