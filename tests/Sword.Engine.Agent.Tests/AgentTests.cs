using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Agent.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

public struct Body
{
    public Vector3 Position;
}

public struct Push
{
    public float X;
}

/// <summary>Подопытная симуляция: три тела, команда Push двигает все тела по X.</summary>
public sealed class PushModule : IModule
{
    public string Id => "push";

    public void Configure(SimulationBuilder b) =>
        b.Component<Body>().Command<Push>().System("S", new Mover()).OnWorldBuilt(w =>
        {
            for (int i = 0; i < 3; i++) w.AddImmediate(w.CreateImmediate(), new Body { Position = new Vector3(i, 0, 0) });
        });

    private sealed class Mover : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("push.mover")).ReadsJournal<Event<Push>>().Writes<Body>();
            _query = s.Query().Write<Body>();
        }

        public void Run(in SystemContext ctx)
        {
            var pushes = ctx.Journal<Event<Push>>();
            if (pushes.IsEmpty) return;
            float x = pushes[^1].Value.X;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var b in chunk.Write<Body>()) b.Position.X += x;
        }
    }
}

/// <summary>Имена: первое тело — «first».</summary>
public sealed class FirstName : IEntityNames
{
    public IReadOnlyList<string> Known => ["first"];
    public string? NameOf(in ServiceContext context, Entity entity) => entity.Index == 0 ? "first" : null;
    public Entity? Resolve(in ServiceContext context, string name) => name == "first" ? new Entity(0, 1) : null;
}

/// <summary>Агент над подопытной симуляцией: цикл Headless на своём потоке, вызовы — через сервер MCP (JSON-RPC).</summary>
public sealed class AgentFixture : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _loop;
    private int _id;

    public AgentFixture(bool paused = true)
    {
        Sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        Ring = new LogRing(64);
        Host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless",
            Catalog = new Catalog(Sim.World, Sim.CommandTypes),
            Time = new TimeControl(paused, Sim.Tick),
            Service = new ServiceQueue(),
            Log = Ring,
            ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-agent-tests"),
            Stop = () => Stopped = true,
            ServiceTimeout = TimeSpan.FromSeconds(5),
        }) { Names = new FirstName() };
        _loop = new Thread(() => SimulationLoop.Run(Sim, Host, _stop.Token)) { IsBackground = true };
        _loop.Start();
    }

    public Simulation Sim { get; }
    public AgentHost Host { get; }
    public LogRing Ring { get; }
    public bool Stopped { get; private set; }

    public async Task<JsonObject> Rpc(string method, JsonObject? parameters = null)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters ?? [] };
        string? response = await Host.Server.HandleAsync(request.ToJsonString());
        return JsonNode.Parse(response!)!.AsObject();
    }

    /// <summary>Вызов инструмента: структурированный итог и признак ошибки.</summary>
    public async Task<(JsonObject Data, bool Error)> Tool(string name, JsonObject? arguments = null)
    {
        var r = await Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments ?? [] });
        var result = r["result"]!.AsObject();
        // Текстовое содержимое — тот же JSON, что и структурированное (клиенты без structuredContent читают его).
        Assert.Equal(result["structuredContent"]!.ToJsonString(), JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.ToJsonString());
        return (result["structuredContent"]!.AsObject(), result["isError"]!.GetValue<bool>());
    }

    public void Dispose()
    {
        _stop.Cancel();
        Host.Service.Wake();
        _loop.Join(TimeSpan.FromSeconds(5));
    }
}

public class McpProtocolTests
{
    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", "2025-11-25")]   // незнакомая — последняя своя
    public async Task Initialize_NegotiatesVersion_GivesInstructions(string asked, string expected)
    {
        using var f = new AgentFixture();
        var r = await f.Rpc("initialize", new JsonObject { ["protocolVersion"] = asked, ["capabilities"] = new JsonObject() });
        var result = r["result"]!;
        Assert.Equal(expected, result["protocolVersion"]!.GetValue<string>());
        Assert.False(result["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.Contains("session.status", result["instructions"]!.GetValue<string>());
        Assert.DoesNotContain("\r", result["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolsList_HasSchemasAndAnnotations()
    {
        using var f = new AgentFixture();
        var tools = (await f.Rpc("tools/list"))["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
        foreach (string needed in (string[])["session.status", "time.step", "time.runUntil", "world.query", "entity.get", "command.send",
                     "log.tail", "architecture.projects", "ecs.plan", "ecs.explainSystem", "ecs.componentUsage", "module.graph"])
            Assert.Contains(needed, names);
        var step = tools.First(t => t!["name"]!.GetValue<string>() == "time.step")!;
        Assert.Equal("object", step["inputSchema"]!["type"]!.GetValue<string>());
        Assert.False(step["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        var query = tools.First(t => t!["name"]!.GetValue<string>() == "world.query")!;
        Assert.True(query["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Errors_ProtocolAndTool()
    {
        using var f = new AgentFixture();
        Assert.Equal(-32601, (await f.Rpc("nope"))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, JsonNode.Parse((await f.Host.Server.HandleAsync("{oops"))!)!["error"]!["code"]!.GetValue<int>());
        Assert.Null(await f.Host.Server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(-32602, (await f.Rpc("tools/call", new JsonObject { ["name"] = "no.such" }))["error"]!["code"]!.GetValue<int>());
        Assert.Equal("{}", (await f.Rpc("ping"))["result"]!.ToJsonString());
        // Ошибка инструмента — ответ с isError и квитанцией: агент видит и исправляет вызов.
        var (data, error) = await f.Tool("time.step", new JsonObject { ["ticks"] = 0 });
        Assert.True(error);
        Assert.Contains("ticks", data["error"]!.GetValue<string>());
        Assert.NotNull(data["receipt"]!["sessionId"]);
    }
}

public class CoreToolsTests
{
    [Fact]
    public async Task StepCommandQuery_OnFutureTicks_WithReceipts()
    {
        using var f = new AgentFixture();
        var (status, _) = await f.Tool("session.status");
        Assert.True(status["paused"]!.GetValue<bool>());
        Assert.Equal("Push", status["commands"]![0]!.GetValue<string>());

        var (sent, err) = await f.Tool("command.send", new JsonObject
        {
            ["command"] = "push", ["fields"] = new JsonObject { ["X"] = 0.5 }, ["delay"] = 1, ["repeat"] = 2,
        });
        Assert.False(err);
        Assert.Equal((1ul, 2ul), (sent["firstTick"]!.GetValue<ulong>(), sent["lastTick"]!.GetValue<ulong>()));

        var (stepped, _) = await f.Tool("time.step", new JsonObject { ["ticks"] = 4 });
        Assert.Equal(4ul, stepped["tick"]!.GetValue<ulong>());
        Assert.Equal(4ul, stepped["receipt"]!["tickId"]!.GetValue<ulong>());

        var (first, _) = await f.Tool("entity.get", new JsonObject { ["entity"] = "first" });
        Assert.Equal("first", first["name"]!.GetValue<string>());
        Assert.Equal(1f, first["components"]!["Body"]!["Position"]!["X"]!.GetValue<float>());   // 0 + 0,5 · 2 тика

        var (page, _) = await f.Tool("world.query", new JsonObject
        {
            ["with"] = new JsonArray("Body"), ["fields"] = new JsonArray("Body.Position.X"), ["take"] = 2,
        });
        Assert.Equal(3, page["total"]!.GetValue<int>());
        Assert.True(page["receipt"]!["truncated"]!.GetValue<bool>());
        Assert.Equal("skip=2", page["receipt"]!["continuationToken"]!.GetValue<string>());
        Assert.Equal(2f, page["entities"]![1]!["components"]!["Body"]!["Position.X"]!.GetValue<float>());

        var (past, pastErr) = await f.Tool("command.send", new JsonObject { ["command"] = "Push", ["tick"] = 1 });
        Assert.True(pastErr);
        Assert.Contains("уже прошёл", past["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task StateHash_MatchesSimulation_DescribeByParts()
    {
        using var f = new AgentFixture();
        await f.Tool("command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 0.25 } });
        await f.Tool("time.step", new JsonObject { ["ticks"] = 2 });
        var (hash, _) = await f.Tool("state.hash");
        Assert.Equal(2ul, hash["receipt"]!["tickId"]!.GetValue<ulong>());
        Assert.Equal(["ecs"], hash["parts"]!.AsObject().Select(p => p.Key));
        Assert.Matches("^[0-9A-F]{16}$", hash["total"]!.GetValue<string>());

        var (one, _) = await f.Tool("state.describe", new JsonObject { ["part"] = "ecs", ["filter"] = "0v1" });
        Assert.Equal(0.25, one["items"]![0]![typeof(Body).FullName!]!["Position.X"]!.GetValue<double>());

        var (page, _) = await f.Tool("state.describe", new JsonObject { ["part"] = "ecs", ["take"] = 2 });
        Assert.True(page["receipt"]!["truncated"]!.GetValue<bool>());
        Assert.Equal("skip=2", page["receipt"]!["continuationToken"]!.GetValue<string>());

        var (bad, err) = await f.Tool("state.describe", new JsonObject { ["part"] = "physics" });
        Assert.True(err);
        Assert.Contains("ecs", bad["error"]!.GetValue<string>());   // ошибка перечисляет участников
    }

    [Fact]
    public async Task StateDescribe_LargeAnswer_GoesToArtifact()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new CrowdModule()]);
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true, sim.Tick),
            Service = new ServiceQueue(), Log = new LogRing(8), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-agent-tests"),
            Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "state.describe", ["arguments"] = new JsonObject { ["part"] = "ecs", ["take"] = 500 } },
            };
            var r = JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!["structuredContent"]!;
            string path = r["artifact"]!.GetValue<string>();
            Assert.True(File.Exists(path));
            Assert.Equal(500, JsonNode.Parse(File.ReadAllText(path))!["items"]!.AsArray().Count);
            Assert.Equal(Path.GetFileName(path), r["receipt"]!["artifactId"]!.GetValue<string>());
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Поза тела матрицей — широкий компонент.</summary>
    public struct Wide
    {
        public Matrix4x4 Pose;
    }

    /// <summary>Толпа: 600 тел с матрицами — описание 500 из них не помещается в ответ.</summary>
    private sealed class CrowdModule : IModule
    {
        public string Id => "crowd";

        public void Configure(SimulationBuilder b) => b.Component<Body>().Component<Wide>().OnWorldBuilt(w =>
        {
            for (int i = 0; i < 600; i++)
            {
                var e = w.CreateImmediate();
                w.AddImmediate(e, new Body { Position = new Vector3(i * 1.25f, -i * 0.5f, i * 3.75f) });
                w.AddImmediate(e, new Wide { Pose = Matrix4x4.CreateRotationY(i * 0.1f) });
            }
        });
    }

    [Fact]
    public async Task RunUntil_Pause_Resume()
    {
        using var f = new AgentFixture();
        var (reached, _) = await f.Tool("time.runUntil", new JsonObject { ["tick"] = 30 });
        Assert.Equal(30ul, reached["tick"]!.GetValue<ulong>());
        await Task.Delay(50);
        Assert.Equal(30ul, f.Sim.Tick);            // на паузе — остановились на цели
        await f.Tool("time.resume");
        await f.Tool("time.runUntil", new JsonObject { ["tick"] = 200 });
        var (paused, _) = await f.Tool("time.pause");
        Assert.True(paused["paused"]!.GetValue<bool>());
        ulong at = paused["tick"]!.GetValue<ulong>();
        await Task.Delay(50);
        Assert.InRange(f.Sim.Tick, at, at + 1);    // пауза держит
    }

    [Fact]
    public async Task Architecture_Types_Log()
    {
        using var f = new AgentFixture();
        var (explain, _) = await f.Tool("ecs.explainSystem", new JsonObject { ["name"] = "push.mover" });
        Assert.Equal("S", explain["stage"]!.GetValue<string>());
        Assert.Contains("AgentTests.cs", explain["source"]!.GetValue<string>());
        var (usage, _) = await f.Tool("ecs.componentUsage", new JsonObject { ["name"] = "Push" });
        Assert.Equal(2, usage["systems"]!.AsArray().Count);      // команды тика порождают, push.mover потребляет
        var (types, _) = await f.Tool("world.types", new JsonObject { ["name"] = "Push" });
        Assert.Equal("number", types["types"]![0]!["fields"]!["X"]!.GetValue<string>());
        var (plan, _) = await f.Tool("ecs.plan");
        Assert.Equal(3, plan["stages"]!.AsArray().Count);
        var (projects, _) = await f.Tool("architecture.projects");
        Assert.Contains(projects["assemblies"]!.AsArray(), a => a!["name"]!.GetValue<string>() == "Sword.Engine.Agent"
            && a["side"]!.GetValue<string>() == "Simulation");

        f.Ring.Write(new LogEntry(DateTime.UtcNow, LogLevel.Warning, "test", "внимание"));
        f.Ring.Write(new LogEntry(DateTime.UtcNow, LogLevel.Info, "test", "сведение"));
        var (log, _) = await f.Tool("log.tail", new JsonObject { ["level"] = "warning" });
        Assert.Equal("внимание", Assert.Single(log["entries"]!.AsArray())!["message"]!.GetValue<string>());

        var (stop, _) = await f.Tool("session.stop");
        Assert.True(stop["stopping"]!.GetValue<bool>());
        await Task.Delay(400);
        Assert.True(f.Stopped);
    }

    [Fact]
    public async Task ServiceTimeout_WhenSimulationDoesNotServe()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(false), Service = new ServiceQueue(),
            Log = new LogRing(), ArtifactsRoot = Path.GetTempPath(), Stop = () => { }, ServiceTimeout = TimeSpan.FromMilliseconds(100),
        });
        var r = JsonNode.Parse((await host.Server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"session.status"}}"""))!)!;
        Assert.True(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("точки обслуживания", r["result"]!["structuredContent"]!["error"]!.GetValue<string>());
    }
}

public class TransportTests
{
    [Fact]
    public async Task Stdio_OneMessagePerLine_ResponsesByIdOnly()
    {
        using var f = new AgentFixture();
        string input = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"entity.get","arguments":{"entity":"first"}}}""") + "\n";
        var output = new MemoryStream();
        await StdioTransport.Run(f.Host.Server, new MemoryStream(Encoding.UTF8.GetBytes(input)), output, CancellationToken.None);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);                            // на уведомление ответа нет
        var ids = lines.Select(l => JsonNode.Parse(l)!["id"]!.GetValue<int>()).Order();
        Assert.Equal([1, 2], ids);
        Assert.Contains("\"first\"", lines.First(l => l.Contains("\"id\":2")));   // кириллица и имена — как есть, одной строкой
    }

    [Fact]
    public async Task Http_PostJson_NotificationAccepted_GetRefused_ForeignOriginForbidden()
    {
        using var f = new AgentFixture();
        using var http = new HttpTransport(f.Host.Server, 0);
        using var client = new HttpClient();
        var init = await client.PostAsync(http.Url, Json("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.StartsWith("application/json", init.Content.Headers.ContentType!.ToString());
        Assert.True(init.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal("2025-06-18", JsonNode.Parse(await init.Content.ReadAsStringAsync())!["result"]!["protocolVersion"]!.GetValue<string>());

        var note = await client.PostAsync(http.Url, Json("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        var call = await client.PostAsync(http.Url, Json("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"time.step","arguments":{"ticks":3}}}"""));
        Assert.Equal(3ul, JsonNode.Parse(await call.Content.ReadAsStringAsync())!["result"]!["structuredContent"]!["tick"]!.GetValue<ulong>());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(http.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(http.Url.Replace("/mcp", "/x"), Json("{}"))).StatusCode);
        var evil = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = Json("""{"jsonrpc":"2.0","id":3,"method":"ping"}""") };
        evil.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(evil)).StatusCode);
        var local = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = Json("""{"jsonrpc":"2.0","id":4,"method":"ping"}""") };
        local.Headers.Add("Origin", "http://localhost:3000");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(local)).StatusCode);
    }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
}

public class CaptureGeometryTests
{
    [Fact]
    public void ScreenRect_ProjectsBox_ClipsToFrame_DropsBehindCamera()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 100);
        var vp = view * proj;
        var r = ClientAgent.ScreenRect(vp, new Vector3(-1, -1, -1), new Vector3(1, 1, 1), 200, 200)!.Value;
        Assert.True(r.X0 < 100 && r.X1 > 100 && r.Y0 < 100 && r.Y1 > 100, $"рамка вокруг центра: {r}");
        Assert.Equal(r.X1 - 100, 100 - r.X0);                                   // симметрична
        Assert.Null(ClientAgent.ScreenRect(vp, new Vector3(-1, -1, 20), new Vector3(1, 1, 21), 200, 200));   // за камерой
        Assert.Null(ClientAgent.ScreenRect(vp, new Vector3(50, -1, -1), new Vector3(52, 1, 1), 200, 200));    // вне кадра
        var edge = ClientAgent.ScreenRect(vp, new Vector3(5, -1, -1), new Vector3(30, 1, 1), 200, 200)!.Value;
        Assert.Equal(200, edge.X1);                                             // обрезана кадром
    }
}
