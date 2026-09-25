using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>Проверки: протокол, отмена, аргументы, транспорты.</summary>
public class ProtocolFixTests
{
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""", -32600)]      // id не может быть null
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""", -32600)]             // method — строка
    [InlineData("""{"jsonrpc":"2.0","id":1,"id":1,"method":"ping"}""", -32700)] // повтор ключа — не тот JSON
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""", -32600)]
    public async Task BadRequests_GetErrors_NeverSilence(string message, int code)
    {
        using var f = new AgentFixture();
        var r = JsonNode.Parse((await f.Host.Server.HandleAsync(message))!)!;
        Assert.Equal(code, r["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ClientResponse_Ignored_Batch_Answered()
    {
        using var f = new AgentFixture();
        Assert.Null(await f.Host.Server.HandleAsync("""{"jsonrpc":"2.0","id":9,"result":{}}"""));   // ответ клиента
        var batch = JsonNode.Parse((await f.Host.Server.HandleAsync(
            """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":"b","method":"nope"}]"""))!)!.AsArray();
        Assert.Equal(2, batch.Count);                           // на уведомление ответа нет
        Assert.Equal(-32601, batch.First(x => x!["id"]!.ToJsonString() == "\"b\"")!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnknownArgument_IsError_IntegralFloatAccepted()
    {
        using var f = new AgentFixture();
        var (typo, err) = await f.Tool("time.step", new JsonObject { ["tick"] = 50 });   // опечатка вместо ticks
        Assert.True(err);
        Assert.Contains("нет аргумента tick", typo["error"]!.GetValue<string>());
        Assert.Equal(0ul, f.Sim.Tick);                          // ничего не сыграно
        var (step, _) = await f.Tool("time.step", new JsonObject { ["ticks"] = 2.0 });
        Assert.Equal(2ul, step["tick"]!.GetValue<ulong>());
        var (bad, badErr) = await f.Tool("time.step", new JsonObject { ["ticks"] = 3, ["timeoutSeconds"] = 0 });
        Assert.True(badErr);
        Assert.Contains("timeoutSeconds", bad["error"]!.GetValue<string>());
        await Task.Delay(50);
        Assert.Equal(2ul, f.Sim.Tick);                          // аргументы проверены до заказа тиков
    }

    [Fact]
    public async Task CancelledCall_GetsNoReply_AndWaitLeaves()
    {
        using var f = new AgentFixture();
        var call = f.Host.Server.HandleAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":10000000,"timeoutSeconds":60}}}""",
            session: "s");
        await Task.Delay(100);
        f.Host.Time.Pause();                                    // чтобы не крутить 10 млн тиков
        // Чужой сеанс с тем же номером не отменяет.
        await f.Host.Server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""", session: "other");
        Assert.False(call.IsCompleted);
        await f.Host.Server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""", session: "s");
        Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(5)));   // отменённый — без ответа
        Assert.Equal(0, f.Host.Time.Waiters);
    }

    [Fact]
    public async Task ServiceTimeout_CommandIsNotAppliedLater()
    {
        // Точку обслуживания никто не крутит: command.send — ошибка по таймауту, и команда не должна пройти потом.
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        var service = new ServiceQueue();
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(false), Service = service,
            Log = new LogRing(), ArtifactsRoot = Path.GetTempPath(), Stop = () => { }, ServiceTimeout = TimeSpan.FromMilliseconds(100),
        });
        var r = JsonNode.Parse((await host.Server.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"command.send","arguments":{"command":"Push","fields":{"X":5}}}}"""))!)!;
        Assert.True(r["result"]!["isError"]!.GetValue<bool>());
        service.Serve(new ServiceContext(sim), TimeSpan.FromSeconds(1));   // окно вернулось
        sim.Step();
        Assert.Equal(0f, sim.World.Get<Body>(new Sword.Engine.Ecs.Entity(0, 1)).Position.X);
    }

    [Fact]
    public async Task StdioEof_CancelsCallsInFlight()
    {
        using var f = new AgentFixture(paused: true);
        var input = new Pipe();
        var output = new MemoryStream();
        var run = StdioTransport.Run(f.Host.Server, input.Reader.AsStream(), output, CancellationToken.None);
        await input.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":10000000,"timeoutSeconds":600}}}""" + "\n"));
        await Task.Delay(100);
        f.Host.Time.Pause();
        await input.Writer.CompleteAsync();                     // клиент ушёл
        await run.WaitAsync(TimeSpan.FromSeconds(5));           // не ждёт 600 с
    }
}

public class HttpFixTests
{
    private static StringContent Json(string s, string type = "application/json") => new(s, Encoding.UTF8, type);

    [Fact]
    public async Task ContentType_ProtocolVersion_Sessions_Expect100()
    {
        using var f = new AgentFixture();
        using var http = new HttpTransport(f.Host.Server, 0);
        using var client = new HttpClient();
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(http.Url, Json(ping, "text/plain"))).StatusCode);

        var bad = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = Json(ping) };
        bad.Headers.Add("MCP-Protocol-Version", "1999-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(bad)).StatusCode);

        const string init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""";
        var a = await client.PostAsync(http.Url, Json(init));
        var b = await client.PostAsync(http.Url, Json(init));
        string sa = a.Headers.GetValues("Mcp-Session-Id").Single(), sb = b.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.NotEqual(sa, sb);                                // новый клиент — свой сеанс
        var again = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = Json(ping) };
        again.Headers.Add("Mcp-Session-Id", sa);
        Assert.Equal(sa, (await client.SendAsync(again)).Headers.GetValues("Mcp-Session-Id").Single());
        Assert.False((await client.PostAsync(http.Url, Json(ping))).Headers.Contains("Mcp-Session-Id"));   // разовый — без номера

        var expect = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = Json(ping) };
        expect.Headers.ExpectContinue = true;                   // PowerShell 5.1 и curl шлют Expect: 100-continue
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(expect)).StatusCode);
        Assert.True(watch.ElapsedMilliseconds < 300, $"ответ на Expect ждали {watch.ElapsedMilliseconds} мс");
    }

    [Theory]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: abc\r\n\r\n", 400)]
    [InlineData("GARBAGE\r\n\r\n", 400)]
    public async Task MalformedRequests_Get400(string raw, int code)
    {
        using var f = new AgentFixture();
        using var http = new HttpTransport(f.Host.Server, 0);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, http.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        var reader = new StreamReader(stream, Encoding.ASCII);
        string? status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith($"HTTP/1.1 {code}", status);
    }
}
