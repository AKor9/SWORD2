using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>Проверки по пятой самопроверке 1е: журнал, квитанции времени, пределы ожидания, сеансы HTTP, отмены.</summary>
public class FifthReviewTests
{
    private static (Simulation Sim, ServiceQueue Service, AgentHost Host) Manual(TimeSpan? maxWait = null)
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new PushModule()]);
        var service = new ServiceQueue();
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true), Service = service,
            Log = new LogRing(), ArtifactsRoot = Path.GetTempPath(), Stop = () => { },
            MaxWait = maxWait ?? TimeSpan.FromSeconds(600),
        });
        return (sim, service, host);
    }

    private static async Task<JsonObject> Call(AgentHost host, string tool, string arguments, int id = 1) =>
        JsonNode.Parse((await host.Server.HandleAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}"""))!)!
            ["result"]!["structuredContent"]!.AsObject();

    [Fact]
    public async Task LogTail_LevelFilter_FindsOlderRecords_NotOnlyAmongLastLimit()
    {
        // Ошибка, за ней 60 строк info: без after — последние записи уровня error, а не error среди последних 50 строк.
        // Записи — прямо в кольцо сеанса (64 записи), а не через общий Log: тесты сборки идут параллельно, и чужие записи
        // общего журнала (больше трёх за эти 61 строку) вытесняли ошибку из кольца — тест изредка падал под нагрузкой.
        using var f = new AgentFixture();
        f.Ring.Write(new LogEntry(DateTime.UtcNow, LogLevel.Error, "test", "та самая ошибка"));
        for (int i = 0; i < 60; i++) f.Ring.Write(new LogEntry(DateTime.UtcNow, LogLevel.Info, "test", $"шум {i}"));
        var (data, err) = await f.Tool("log.tail", new JsonObject { ["level"] = "error", ["category"] = "test" });
        Assert.False(err);
        var entry = Assert.Single(data["entries"]!.AsArray())!;
        Assert.Equal("та самая ошибка", entry["message"]!.GetValue<string>());
        Assert.False(data["receipt"]!["truncated"]!.GetValue<bool>());
        // Продолжение — с номера последней записи кольца: следующий вызов не повторит старое.
        Assert.Equal(f.Ring.Last, data["last"]!.GetValue<long>());

        var (latest, _) = await f.Tool("log.tail", new JsonObject { ["limit"] = 2, ["category"] = "test" });
        Assert.Equal(["шум 58", "шум 59"], latest["entries"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task TimeStep_Receipt_TickAndFrame_OfOneServicePoint()
    {
        // Ожидание отпускается в кадре 6 (Ran) до того, как клиент запишет номер кадра; квитанция — из точки кадра 6,
        // а не «тик 1 с кадром 5».
        var (sim, service, host) = Manual();
        host.LastFrame = 5;
        var call = Call(host, "time.step", "{}");
        await Task.Delay(50);
        sim.Step();
        host.Time.Ran(sim.Tick);
        await Task.Delay(50);
        service.Serve(new ServiceContext(sim, 6), TimeSpan.FromSeconds(1));
        var receipt = (await call.WaitAsync(TimeSpan.FromSeconds(5)))["receipt"]!;
        Assert.Equal((1ul, 6ul), (receipt["tickId"]!.GetValue<ulong>(), receipt["frameId"]!.GetValue<ulong>()));
    }

    [Fact]
    public async Task TimeoutSeconds_LimitedByTransport()
    {
        // Живая игра: HTTP обрывает вызов через 35 с — схема и проверка не обещают больше HttpTransport.MaxWait.
        var (_, _, host) = Manual(HttpTransport.MaxWait);
        double max = HttpTransport.MaxWait.TotalSeconds;
        var tools = JsonNode.Parse((await host.Server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))!)!["result"]!["tools"]!.AsArray();
        foreach (string name in (string[])["time.step", "time.runUntil"])
        {
            var schema = tools.First(t => t!["name"]!.GetValue<string>() == name)!["inputSchema"]!["properties"]!["timeoutSeconds"]!;
            Assert.Equal(max, schema["maximum"]!.GetValue<double>());
        }
        var result = await Call(host, "time.step", $$"""{"timeoutSeconds":{{max + 1}}}""");
        Assert.Contains("timeoutSeconds", result["error"]!.GetValue<string>());
        Assert.Equal(0ul, host.Time.Remaining);   // отвергнутый вызов шагов не заказал
        Assert.True(HttpTransport.MaxWait < HttpTransport.DefaultCallTime);
    }

    [Fact]
    public async Task Http_RejectedInitialize_GetsNoSession()
    {
        var (_, _, host) = Manual();
        using var http = new HttpTransport(host.Server, 0);
        using var client = new HttpClient();
        foreach (string bad in (string[])[
                     """{"jsonrpc":"1.0","id":1,"method":"initialize","params":{}}""",
                     """{"jsonrpc":"2.0","id":1,"method":"initialize","params":[]}"""])
        {
            var r = await client.PostAsync(http.Url, new StringContent(bad, Encoding.UTF8, "application/json"));
            Assert.False(r.Headers.Contains("Mcp-Session-Id"), bad);
            Assert.NotNull(JsonNode.Parse(await r.Content.ReadAsStringAsync())!["error"]);
        }
        var ok = await client.PostAsync(http.Url, new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""", Encoding.UTF8, "application/json"));
        Assert.True(ok.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task Http_Head_405WithoutBody()
    {
        var (_, _, host) = Manual();
        using var http = new HttpTransport(host.Server, 0);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, http.Port);
        var stream = tcp.GetStream();
        // Два запроса на одном соединении: тело у HEAD сдвинуло бы разбор второго ответа.
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD /mcp HTTP/1.1\r\nHost: x\r\n\r\nHEAD /mcp HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        string all = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var responses = all.Split("HTTP/1.1 ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, responses.Length);
        Assert.All(responses, r =>
        {
            Assert.StartsWith("405", r);
            Assert.Contains("Content-Length: 0\r\n", r);
            Assert.EndsWith("\r\n\r\n", r);
        });
    }

    [Fact]
    public async Task Cancel_ArrivingBeforeItsCall_StillCancels()
    {
        // stdio исполняет строки параллельно: уведомление об отмене может обогнать свой запрос. Симуляцию никто не
        // крутит — без отмены вызов ждал бы до таймаута.
        var (_, _, host) = Manual();
        Assert.Null(await host.Server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""", session: "stdio"));
        var answer = await host.Server.HandleAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":5,"timeoutSeconds":30}}}""",
            session: "stdio").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(answer);   // отменённому вызову не отвечают
        Assert.Equal(0, host.Time.Waiters);
        Assert.Equal(0ul, host.Time.Remaining);   // и шаги он не заказал

        // Отмена другого сеанса или номера чужой вызов не трогает.
        var other = host.Server.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"ecs.plan","arguments":{}}}""", session: "other");
        await Task.Delay(50);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task CommandQueue_OfTools_HasALimit()
    {
        using var f = new AgentFixture();
        for (int k = 0; k < 10; k++)
        {
            var (_, err) = await f.Tool("command.send", new JsonObject { ["command"] = "Push", ["tick"] = k * 100_000, ["repeat"] = 100_000 });
            Assert.False(err);
        }
        var (data, full) = await f.Tool("command.send", new JsonObject { ["command"] = "Push", ["tick"] = 5 });
        Assert.True(full);
        Assert.Contains("предела", data["error"]!.GetValue<string>());
    }
}
