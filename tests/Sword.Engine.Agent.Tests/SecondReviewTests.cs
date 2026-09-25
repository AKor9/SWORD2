using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>Проверки: форма аргументов, страницы, горизонт команд, JSON-RPC, предел HTTP.</summary>
public class SecondReviewTests
{
    [Fact]
    public async Task NullAndMissingRequired_AreErrors_TimeDoesNotMove()
    {
        using var f = new AgentFixture();
        var (step, stepErr) = await f.Tool("time.step", new JsonObject { ["ticks"] = null });
        Assert.True(stepErr);
        Assert.Contains("ticks = null", step["error"]!.GetValue<string>());
        var (until, untilErr) = await f.Tool("time.runUntil", []);
        Assert.True(untilErr);
        Assert.Contains("нужен tick или condition", until["error"]!.GetValue<string>());   // tick или условие
        await Task.Delay(50);
        Assert.Equal(0ul, f.Sim.Tick);                          // ни один ошибочный вызов не сыграл тиков
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"ticks\"")]
    public async Task ArgumentsNotObject_IsProtocolError(string arguments)
    {
        using var f = new AgentFixture();
        var r = JsonNode.Parse((await f.Host.Server.HandleAsync(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"time.step","arguments":{{{arguments}}}}}"""))!)!;
        Assert.Equal(-32602, r["error"]!["code"]!.GetValue<int>());
        await Task.Delay(50);
        Assert.Equal(0ul, f.Sim.Tick);
    }

    [Fact]
    public async Task QueryTakeZero_IsComplete_WithoutContinuation()
    {
        using var f = new AgentFixture();
        var (data, _) = await f.Tool("world.query", new JsonObject { ["take"] = 0 });
        Assert.Equal(3, data["total"]!.GetValue<int>());
        var receipt = data["receipt"]!;
        Assert.True(receipt["complete"]!.GetValue<bool>());
        Assert.False(receipt["truncated"]!.GetValue<bool>());
        Assert.Null(receipt["continuationToken"]);
    }

    [Fact]
    public async Task CommandRepeat_WholeRangeWithinHorizon()
    {
        using var f = new AgentFixture();
        var (far, farErr) = await f.Tool("command.send", new JsonObject
        {
            ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 1 }, ["delay"] = 1_000_000, ["repeat"] = 2,
        });
        Assert.True(farErr);
        Assert.Contains("1000000…1000001", far["error"]!.GetValue<string>());
        var (ok, okErr) = await f.Tool("command.send", new JsonObject
        {
            ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = 1 }, ["delay"] = 999_999, ["repeat"] = 2,
        });
        Assert.False(okErr);
        Assert.Equal(1_000_000ul, ok["lastTick"]!.GetValue<ulong>());   // ровно на горизонте — можно
        var (huge, hugeErr) = await f.Tool("command.send", new JsonObject
        {
            ["command"] = "Push", ["tick"] = long.MaxValue, ["repeat"] = 100_000,
        });
        Assert.True(hugeErr);                                   // без переполнения first + repeat − 1
        Assert.Contains("не дальше", huge["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
    [InlineData("""{"id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":2.0,"id":1,"method":"ping"}""")]
    public async Task JsonRpcVersion_Checked(string message)
    {
        using var f = new AgentFixture();
        var r = JsonNode.Parse((await f.Host.Server.HandleAsync(message))!)!;
        Assert.Equal(-32600, r["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, r["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task HttpCall_LimitedInExecution_TimeStaysOrdered()
    {
        // Симуляцию никто не крутит: time.runUntil ждал бы свои 60 с, а HTTP-запрос — не дольше предела.
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        var time = new TimeControl(true);
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = time, Service = new ServiceQueue(),
            Log = new LogRing(), ArtifactsRoot = Path.GetTempPath(), Stop = () => { },
        });
        using var http = new HttpTransport(host.Server, 0, callTime: TimeSpan.FromMilliseconds(300));
        using var client = new HttpClient();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.PostAsync(http.Url, new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":5,"timeoutSeconds":60}}}""",
            Encoding.UTF8, "application/json"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"запрос шёл {watch.Elapsed}");
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal("TimeoutException", result["structuredContent"]!["kind"]!.GetValue<string>());
        Assert.Contains("предел HTTP-запроса", result["structuredContent"]!["error"]!.GetValue<string>());
        Assert.Equal(0, time.Waiters);                          // ожидание ушло
        Assert.Equal(5, time.Allow(100));                       // цель осталась: симуляция дойдёт до тика 5
    }
}

/// <summary>Проверки: сеансы HTTP, пути проекции, производители Changed.</summary>
public class ThirdReviewTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static HttpRequestMessage In(HttpMethod method, string url, string session, string? body = null)
    {
        var m = new HttpRequestMessage(method, url) { Content = body is null ? null : Json(body) };
        m.Headers.Add("Mcp-Session-Id", session);
        return m;
    }

    [Fact]
    public async Task Delete_EndsSession_ThenUnknown404_AndCancelsItsCalls()
    {
        using var f = new AgentFixture();
        using var http = new HttpTransport(f.Host.Server, 0);
        using var client = new HttpClient();
        var init = await client.PostAsync(http.Url, Json("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""));
        string sid = init.Headers.GetValues("Mcp-Session-Id").Single();
        const string ping = """{"jsonrpc":"2.0","id":2,"method":"ping"}""";
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(In(HttpMethod.Post, http.Url, sid, ping))).StatusCode);

        // Долгий вызов сеанса: DELETE его отменяет.
        var wait = client.SendAsync(In(HttpMethod.Post, http.Url, sid,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":10000000,"timeoutSeconds":30}}}"""));
        await Task.Delay(100);
        f.Host.Time.Pause();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(In(HttpMethod.Delete, http.Url, sid))).StatusCode);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Host.Time.Waiters);

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(In(HttpMethod.Post, http.Url, sid, ping))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(In(HttpMethod.Delete, http.Url, sid))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(In(HttpMethod.Post, http.Url, "made-up", ping))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(http.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(http.Url, Json(ping))).StatusCode);   // без номера — разовый
    }

    [Theory]
    [InlineData("Body.Positoin.X", "нет поля «Positoin»")]
    [InlineData("Body.Position.W", "нет поля «W»")]
    [InlineData("Body.Position.X.Y", "вложенных полей нет")]
    public async Task QueryFieldTypo_IsError(string path, string message)
    {
        using var f = new AgentFixture();
        var (data, err) = await f.Tool("world.query", new JsonObject { ["fields"] = new JsonArray(path) });
        Assert.True(err);
        Assert.Contains(message, data["error"]!.GetValue<string>());
        var (ok, okErr) = await f.Tool("world.query", new JsonObject { ["fields"] = new JsonArray("body.position.x") });
        Assert.False(okErr);
        Assert.Equal(0.0, ok["entities"]![0]!["components"]!["Body"]!["Position.X"]!.GetValue<double>());
    }
}

/// <summary>Проверки: квитанция одного состояния, завершение сеанса без гонок.</summary>
public class FourthReviewTests
{
    private static (Simulation Sim, ServiceQueue Service, AgentHost Host) Manual()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        var service = new ServiceQueue();
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true), Service = service,
            Log = new LogRing(), ArtifactsRoot = Path.GetTempPath(), Stop = () => { },
        }) { Names = new FirstName() };
        return (sim, service, host);
    }

    [Theory]
    [InlineData("world.query", """{"take":1}""")]
    [InlineData("entity.get", """{"entity":"first"}""")]
    [InlineData("session.status", "{}")]
    [InlineData("ecs.plan", "{}")]
    public async Task Receipt_TickAndFrame_OfTheServicePoint(string tool, string arguments)
    {
        // Точка обслуживания — в кадре 7; пока агент собирает JSON, клиент дошёл до кадра 9. Квитанция — кадр 7.
        var (sim, service, host) = Manual();
        var call = host.Server.HandleAsync(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""");
        await Task.Delay(50);
        service.Serve(new ServiceContext(sim, 7), TimeSpan.FromSeconds(1));
        host.LastFrame = 9;
        var receipt = JsonNode.Parse((await call.WaitAsync(TimeSpan.FromSeconds(5)))!)!["result"]!["structuredContent"]!["receipt"]!;
        Assert.Equal(7ul, receipt["frameId"]!.GetValue<ulong>());
        Assert.Equal(0ul, receipt["tickId"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task Delete_RacingCalls_NeverLeavesOneRunning()
    {
        // Симуляцию никто не крутит: ждущий вызов закончится, только если DELETE его отменит (иначе — 35 с предела).
        var (_, _, host) = Manual();
        using var http = new HttpTransport(host.Server, 0);
        using var client = new HttpClient();
        for (int round = 0; round < 30; round++)
        {
            var init = await client.PostAsync(http.Url, new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""", Encoding.UTF8, "application/json"));
            string sid = init.Headers.GetValues("Mcp-Session-Id").Single();
            var call = new HttpRequestMessage(HttpMethod.Post, http.Url)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":5,"timeoutSeconds":30}}}""",
                    Encoding.UTF8, "application/json"),
            };
            call.Headers.Add("Mcp-Session-Id", sid);
            var delete = new HttpRequestMessage(HttpMethod.Delete, http.Url);
            delete.Headers.Add("Mcp-Session-Id", sid);
            var running = client.SendAsync(call);
            if (round % 3 != 0) await Task.Delay(round % 3);    // DELETE — до, во время и после регистрации вызова
            var deleted = client.SendAsync(delete);
            var answer = await running.WaitAsync(TimeSpan.FromSeconds(10));
            await deleted;
            Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NotFound, $"{answer.StatusCode}");
            var after = new HttpRequestMessage(HttpMethod.Post, http.Url)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", Encoding.UTF8, "application/json"),
            };
            after.Headers.Add("Mcp-Session-Id", sid);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(after)).StatusCode);   // не ожил
        }
        await Task.Delay(50);
        Assert.Equal(0, host.Time.Waiters);
    }
}
