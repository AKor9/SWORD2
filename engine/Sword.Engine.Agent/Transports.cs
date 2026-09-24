using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Agent;

/// <summary>
/// Транспорт stdio (MCP): сообщение JSON-RPC — одна строка UTF-8 во входе, ответ — одна строка в выходе. Так агента
/// подключают к <c>Sword.Headless --agent</c>: клиент сам запускает процесс. stdout занят протоколом — журнал идёт в
/// stderr. Запросы исполняются параллельно, строки ответов не перемешиваются. Конец входа (клиент ушёл) — конец работы:
/// вызовы, не закончившиеся за <see cref="EofGrace"/>, отменяются (долгий <c>time.runUntil</c> не держит процесс), а
/// не ответившие на отмену за <see cref="CancelGrace"/> бросаются — транспорт возвращается за секунды при любом вызове.
/// </summary>
public static class StdioTransport
{
    /// <summary>Сколько после конца входа ждать исполняемые вызовы, прежде чем отменить.</summary>
    public static readonly TimeSpan EofGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Сколько после отмены ждать вызовы, прежде чем бросить их (вызов, не проверяющий отмену, не держит выход; ответ
    /// всё равно некому писать).
    /// </summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    public static async Task Run(McpServer server, Stream input, Stream output, CancellationToken stop)
    {
        var reader = new StreamReader(input, new UTF8Encoding(false));
        var gate = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        using var calls = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        string? response = await server.HandleAsync(line, calls.Token, "stdio").ConfigureAwait(false);
                        if (response is null) return;
                        byte[] bytes = Encoding.UTF8.GetBytes(response + "\n");
                        await gate.WaitAsync(calls.Token).ConfigureAwait(false);
                        try
                        {
                            await output.WriteAsync(bytes, calls.Token).ConfigureAwait(false);
                            await output.FlushAsync(calls.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // клиент ушёл или процесс останавливается — отвечать некому
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                        Log.Warning("agent", $"stdio: ответ не записан: {e.Message}");
                    }
                }));
            }
        }
        catch (OperationCanceledException)
        {
        }
        // Конец входа: быстрые вызовы успевают ответить (клиент мог закрыть вход сразу после запросов), долгие —
        // отменяются через секунды, а не держат процесс; не ответившие на отмену — бросаются (точка 6а).
        var all = Task.WhenAll(running);
        if (await Task.WhenAny(all, Task.Delay(EofGrace, stop)).ConfigureAwait(false) != all)
        {
            calls.Cancel();
            if (await Task.WhenAny(all, Task.Delay(CancelGrace)).ConfigureAwait(false) != all)
            {
                Log.Warning("agent", $"stdio: вызовов, не ответивших на отмену за {CancelGrace.TotalSeconds:0.#} с, — " +
                    $"{running.Count(t => !t.IsCompleted)}: брошены");
                return;
            }
        }
        await all.ConfigureAwait(false);
    }
}

/// <summary>
/// Транспорт Streamable HTTP (MCP) для живой игры (<c>Sword.exe --agent</c>): <c>POST /mcp</c> с сообщением JSON-RPC →
/// ответ <c>application/json</c> (уведомление и ответ клиента — 202 без тела); потока событий сервер не держит
/// (<c>GET</c> — 405). Только петля 127.0.0.1; чужой <c>Origin</c> — 403, тело не <c>application/json</c> — 415 (страница
/// браузера не пошлёт такой запрос без предварительного, а на него сервер не отвечает разрешением), неизвестная
/// <c>MCP-Protocol-Version</c> — 400. Сеанс — <c>Mcp-Session-Id</c>: выдаётся на <c>initialize</c>, DELETE его завершает
/// (вызовы сеанса отменяются), завершённый или незнакомый — 404; запрос без номера — разовый. Свой минимальный
/// HTTP/1.1 на <see cref="TcpListener"/>: http.sys (<c>HttpListener</c>) без прав администратора резервирует не всякий
/// адрес.
/// </summary>
public sealed class HttpTransport : IDisposable
{
    private const int MaxBody = 4 << 20, MaxHeaders = 100, MaxLine = 16 * 1024;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2), RequestTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Исполнение вызова — не дольше (с запасом над таймаутом ожидания по умолчанию, 30 с): HTTP-запрос не висит
    /// минутами, дольше — ошибка вызова, заказанное время остаётся.
    /// </summary>
    public static readonly TimeSpan DefaultCallTime = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Самое долгое ожидание инструмента под этим транспортом (<see cref="AgentHostOptions.MaxWait"/>): предел вызова
    /// минус запас на точку обслуживания и ответ.
    /// </summary>
    public static readonly TimeSpan MaxWait = DefaultCallTime - TimeSpan.FromSeconds(5);

    private readonly TimeSpan _callTime;

    /// <summary>
    /// Сеанс: токен завершения и время последнего запроса. Вызов сеанса исполняется под токеном, связанным с
    /// <see cref="Ended"/>, с самого приёма — DELETE отменяет и вызовы, принятые до него, но ещё не дошедшие до
    /// регистрации в сервере. Завершённый сеанс не оживает: запрос только читает реестр, пишет в него лишь
    /// <c>initialize</c> (новый номер).
    /// </summary>
    private sealed class Session
    {
        public readonly CancellationTokenSource Ended = new();
        public long Seen = Environment.TickCount64;
    }

    /// <summary>Живые сеансы (выданные на <c>initialize</c>, не завершённые DELETE).</summary>
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>Сеансов не больше: сверх — забывается самый давний (клиент получит 404 и начнёт заново).</summary>
    private const int MaxSessions = 64;
    private readonly McpServer _server;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="callTime">Предел исполнения вызова; null — <see cref="DefaultCallTime"/>.</param>
    public HttpTransport(McpServer server, int port, TimeSpan? callTime = null)
    {
        _server = server;
        _callTime = callTime ?? DefaultCallTime;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Порт (0 в конструкторе — свободный, выбирает система).</summary>
    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    private sealed class BadRequest(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException e)
            {
                Log.Warning("agent", $"HTTP: {e.Message}");
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Request? request;
                try
                {
                    request = await ReadRequest(stream).ConfigureAwait(false);
                }
                catch (BadRequest bad)
                {
                    await Write(stream, bad.Code, "Bad Request", "text/plain", Encoding.UTF8.GetBytes(bad.Message), keep: false).ConfigureAwait(false);
                    return;
                }
                if (request is null) return;
                bool keep = !request.Headers.TryGetValue("connection", out var c) || !c.Equals("close", StringComparison.OrdinalIgnoreCase);
                await Answer(stream, request, keep).ConfigureAwait(false);
                if (!keep) return;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Клиент ушёл, простой или остановка — соединение закрывается.
        }
        catch (Exception e)
        {
            Log.Warning("agent", $"HTTP: {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task Answer(NetworkStream stream, Request r, bool keep)
    {
        var headers = r.Headers;
        // Сеанс (спецификация MCP): номер выдаётся в ответе на initialize; запрос с номером — только в живом сеансе,
        // завершённый (DELETE) или незнакомый — 404, клиент начинает заново. Запрос без номера — разовый сеанс
        // (curl, PowerShell): номер ему не выдаётся.
        string? sid = headers.TryGetValue("mcp-session-id", out var h) ? h : null;
        string? session = sid;
        Session? state = null;
        if (headers.TryGetValue("origin", out var origin) && !IsLocal(origin))
            await Write(stream, 403, "Forbidden", "text/plain", "origin"u8.ToArray(), keep).ConfigureAwait(false);
        else if (r.Path.Split('?')[0].TrimEnd('/') != "/mcp")
            await Write(stream, 404, "Not Found", "text/plain", "MCP: /mcp"u8.ToArray(), keep).ConfigureAwait(false);
        else if (sid is not null && (state = Live(sid)) is null)
            await Write(stream, 404, "Not Found", "text/plain",
                Encoding.UTF8.GetBytes($"сеанс {sid} завершён или незнаком — начните заново (initialize)"), keep).ConfigureAwait(false);
        else if (r.Method == "POST")
        {
            string body = Encoding.UTF8.GetString(r.Body);
            if (!headers.TryGetValue("content-type", out var type) || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                await Write(stream, 415, "Unsupported Media Type", "text/plain", "application/json"u8.ToArray(), keep, session).ConfigureAwait(false);
            else if (headers.TryGetValue("mcp-protocol-version", out var version) && !McpServer.ProtocolVersions.Contains(version))
                await Write(stream, 400, "Bad Request", "text/plain", Encoding.UTF8.GetBytes($"MCP-Protocol-Version {version}"), keep, session).ConfigureAwait(false);
            else
            {
                // initialize: номер выдаётся и попадает в реестр только после успешного ответа — отвергнутый (чужой
                // jsonrpc, params не объект) не занимает место живых сеансов.
                string? opening = sid is null && Initializes(body) ? Guid.NewGuid().ToString("N") : null;
                using var calls = state is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, state.Ended.Token);
                string? response = await _server.HandleAsync(body, calls?.Token ?? _stop.Token,
                    session ?? opening ?? "once-" + Guid.NewGuid().ToString("N"), _callTime).ConfigureAwait(false);
                if (opening is not null && Initialized(response))
                {
                    Open(opening);
                    session = opening;
                }
                if (response is null) await Write(stream, 202, "Accepted", null, [], keep, session).ConfigureAwait(false);
                else await Write(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(response), keep, session).ConfigureAwait(false);
            }
        }
        else if (r.Method == "DELETE")
        {
            if (sid is null)
                await Write(stream, 400, "Bad Request", "text/plain", "DELETE: нет Mcp-Session-Id"u8.ToArray(), keep).ConfigureAwait(false);
            else
            {
                // Удалить может только один из параллельных DELETE; вызовы сеанса отменяются — отвечать некому.
                if (_sessions.TryRemove(new KeyValuePair<string, Session>(sid, state!))) End(state!);
                await Write(stream, 200, "OK", null, [], keep).ConfigureAwait(false);
            }
        }
        else   // на HEAD — без тела (RFC 9110): тело ответа клиент принял бы за начало следующего ответа
            await Write(stream, 405, "Method Not Allowed", "text/plain", r.Method == "HEAD" ? [] : "POST"u8.ToArray(), keep, session,
                allow: "POST, DELETE").ConfigureAwait(false);
    }

    /// <summary>Живой сеанс (отметить запрос) или null — завершён или незнаком. Реестр только читается.</summary>
    private Session? Live(string sid)
    {
        if (!_sessions.TryGetValue(sid, out var s) || s.Ended.IsCancellationRequested) return null;
        Volatile.Write(ref s.Seen, Environment.TickCount64);
        return s;
    }

    private void Open(string sid)
    {
        var state = new Session();
        _sessions[sid] = state;
        while (_sessions.Count > MaxSessions)
        {
            var oldest = _sessions.Where(p => p.Key != sid).MinBy(p => Volatile.Read(ref p.Value.Seen));
            if (_sessions.TryRemove(oldest)) End(oldest.Value);
        }
    }

    /// <summary>Ответ сервера содержит успешный ответ на initialize (результат с версией протокола).</summary>
    private static bool Initialized(string? response)
    {
        if (response is null) return false;
        try
        {
            static bool Is(JsonNode? n) => n is JsonObject o && o["result"] is JsonObject r && r["protocolVersion"] is not null;
            return JsonNode.Parse(response) switch
            {
                JsonArray a => a.Any(Is),
                var n => Is(n),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static void End(Session s)
    {
        try
        {
            s.Ended.Cancel();
        }
        catch (AggregateException e)
        {
            Log.Warning("agent", $"HTTP: завершение сеанса: {e.InnerException?.Message}");
        }
    }

    /// <summary>Сообщение (или пакет) содержит initialize.</summary>
    private static bool Initializes(string body)
    {
        try
        {
            static bool Is(JsonNode? n) => n is JsonObject o && o["method"] is JsonValue m
                && m.GetValueKind() == System.Text.Json.JsonValueKind.String && m.GetValue<string>() == "initialize";
            return JsonNode.Parse(body) switch
            {
                JsonArray a => a.Any(Is),
                var n => Is(n),
            };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException)
        {
            return false;   // не JSON — ответит сервер (-32700)
        }
    }

    private static bool IsLocal(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

    private async Task Write(NetworkStream stream, int code, string reason, string? type, byte[] body, bool keep,
        string? session = null, string? allow = null)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: ").Append(keep ? "keep-alive" : "close").Append("\r\n");
        if (session is not null) head.Append("Mcp-Session-Id: ").Append(session).Append("\r\n");
        if (type is not null) head.Append("Content-Type: ").Append(type).Append("; charset=utf-8\r\n");
        if (allow is not null) head.Append("Allow: ").Append(allow).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
        await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Запрос HTTP/1.1: строка, заголовки (имена — строчными), тело по Content-Length или chunked; null — соединение
    /// закрыто между запросами или простой дольше <see cref="Idle"/>. Испорченный запрос — <see cref="BadRequest"/>.
    /// </summary>
    private async Task<Request?> ReadRequest(NetworkStream stream)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        idle.CancelAfter(Idle);
        string? line;
        try
        {
            line = await ReadLine(stream, idle.Token).ConfigureAwait(false);
            while (line is { Length: 0 }) line = await ReadLine(stream, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            return null;   // простой — закрыть соединение
        }
        if (line is null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(RequestTime);   // заголовки и тело — не дольше
        var token = timeout.Token;
        var parts = line.Split(' ');
        if (parts.Length < 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new BadRequest(400, "не HTTP/1.x");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (string? h = await ReadLine(stream, token).ConfigureAwait(false); !string.IsNullOrEmpty(h); h = await ReadLine(stream, token).ConfigureAwait(false))
        {
            if (headers.Count >= MaxHeaders) throw new BadRequest(431, "заголовков больше предела");
            int colon = h.IndexOf(':');
            if (colon > 0) headers[h[..colon].Trim().ToLowerInvariant()] = h[(colon + 1)..].Trim();
        }
        if (headers.TryGetValue("expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
        byte[] body;
        if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                string size = (await ReadLine(stream, token).ConfigureAwait(false) ?? throw new BadRequest(400, "тело обрезано")).Split(';')[0].Trim();
                if (!int.TryParse(size, System.Globalization.NumberStyles.AllowHexSpecifier, null, out int n) || n < 0)
                    throw new BadRequest(400, $"размер куска «{size}»");
                if (n == 0)
                {
                    while (!string.IsNullOrEmpty(await ReadLine(stream, token).ConfigureAwait(false))) { }
                    break;
                }
                if (buffer.Length + n > MaxBody) throw new BadRequest(413, "тело больше предела");
                var chunk = new byte[n];
                await stream.ReadExactlyAsync(chunk, token).ConfigureAwait(false);
                buffer.Write(chunk);
                await ReadLine(stream, token).ConfigureAwait(false);
            }
            body = buffer.ToArray();
        }
        else
        {
            int length = 0;
            if (headers.TryGetValue("content-length", out var cl) && !int.TryParse(cl, out length)) throw new BadRequest(400, $"Content-Length «{cl}»");
            if (length is < 0 or > MaxBody) throw new BadRequest(413, "тело больше предела");
            body = new byte[length];
            await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        }
        return new Request(parts[0].ToUpperInvariant(), parts[1], headers, body);
    }

    private static async Task<string?> ReadLine(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (one[0] == (byte)'\n') break;
            if (bytes.Count > MaxLine) throw new BadRequest(431, "строка заголовка больше предела");
            bytes.Add(one[0]);
        }
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
        return Encoding.ASCII.GetString([.. bytes]);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
