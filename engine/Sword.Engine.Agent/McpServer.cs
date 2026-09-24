using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Agent;

/// <summary>
/// Сервер MCP без транспорта: сообщение JSON-RPC 2.0 на входе, ответ (или ничего — на уведомление) на выходе.
/// Свой минимальный JSON-RPC по спецификации MCP (раздел 8 плана: официальный SDK тянет хостинг и внедрение
/// зависимостей в exe игры, а нужны четыре метода): <c>initialize</c> (версия протокола — запрошенная клиентом из
/// поддерживаемых, иначе последняя своя; инструкции сервера), <c>ping</c>, <c>tools/list</c>, <c>tools/call</c>,
/// уведомления <c>notifications/initialized</c> и <c>notifications/cancelled</c> (отмена исполняемого вызова: ответа на
/// отменённый вызов нет). Пакет (массив сообщений) — ответ пакетом. На каждый запрос — ответ, в том числе при
/// внутренней ошибке (-32603). Вызовы исполняются параллельно: долгий шаг времени не держит <c>ping</c>.
/// </summary>
public sealed class McpServer(AgentHost host)
{
    /// <summary>Версии протокола MCP, которые сервер понимает; первая — последняя.</summary>
    public static readonly IReadOnlyList<string> ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>Исполняемые вызовы: ключ — сеанс транспорта и номер запроса (у разных клиентов номера совпадают).</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>
    /// Отмены, пришедшие раньше своего вызова (stdio исполняет строки параллельно: уведомление может обогнать запрос):
    /// ключ — как у <see cref="_running"/>, значение — когда пришла. Вызов при регистрации забирает свою отмену; не
    /// забранные за <see cref="EarlyCancelTtl"/> (вызов уже ответил или не придёт) забываются.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _earlyCancels = new();

    private static readonly TimeSpan EarlyCancelTtl = TimeSpan.FromSeconds(10);
    private const int MaxEarlyCancels = 256;

    /// <summary>Исполняемых вызовов инструментов (<c>tools/call</c>).</summary>
    private int _calls;

    /// <summary>Когда вызов инструмента последний раз начался или закончился (<see cref="Environment.TickCount64"/>).</summary>
    private long _lastCall = Environment.TickCount64;

    /// <summary>
    /// Сколько сервер без вызовов инструментов: с конца последнего вызова (или с создания); пока вызов исполняется — ноль.
    /// <c>initialize</c>, <c>tools/list</c>, <c>ping</c> и уведомления простоя не прерывают: клиент, который только
    /// держит соединение, агентом не пользуется (точка 6а — выход сервера после простоя).
    /// </summary>
    public TimeSpan IdleFor => Volatile.Read(ref _calls) > 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastCall)));

    /// <summary>
    /// Задача, которая завершается, когда простой (<see cref="IdleFor"/>) дошёл до <paramref name="idle"/>; опрос — не
    /// реже раза в 5 с. Отмена — <paramref name="cancel"/>.
    /// </summary>
    public async Task WhenIdle(TimeSpan idle, CancellationToken cancel)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 50, 5000));
        while (IdleFor < idle) await Task.Delay(poll, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Сообщение клиента → ответ; null — ответа нет (уведомление, ответ клиента, отменённый вызов).
    /// <paramref name="session"/> — сеанс транспорта (HTTP: <c>Mcp-Session-Id</c>), для отмены вызовов.
    /// <paramref name="limit"/> — предел исполнения вызова инструмента (HTTP-запрос): дольше — ошибка вызова с isError.
    /// </summary>
    public async Task<string?> HandleAsync(string message, CancellationToken stop = default, string session = "", TimeSpan? limit = null)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(message, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return Error(null, -32700, $"не JSON: {e.Message}").ToJsonString(Json.Compact);
        }
        if (parsed is JsonArray batch)
        {
            if (batch.Count == 0) return Error(null, -32600, "пустой пакет").ToJsonString(Json.Compact);
            var answers = await Task.WhenAll(batch.Select(m => One(m, stop, session, limit))).ConfigureAwait(false);
            var responses = answers.Where(a => a is not null).ToArray();
            return responses.Length == 0 ? null : new JsonArray(responses).ToJsonString(Json.Compact);
        }
        return (await One(parsed, stop, session, limit).ConfigureAwait(false))?.ToJsonString(Json.Compact);
    }

    private async Task<JsonNode?> One(JsonNode? node, CancellationToken stop, string session, TimeSpan? limit)
    {
        if (node is not JsonObject request) return Error(null, -32600, "ожидается объект запроса JSON-RPC");
        bool hasId = request.TryGetPropertyValue("id", out var rawId);
        JsonNode? id = rawId?.DeepClone();
        try
        {
            if (!request.TryGetPropertyValue("method", out var methodNode))
                return null;   // ответ клиента на запрос сервера (сервер клиенту не пишет) — принять и забыть
            if (methodNode is not JsonValue mv || mv.GetValueKind() != JsonValueKind.String)
                return Error(hasId ? id : null, -32600, "method — строка");
            // JSON-RPC 2.0: поле jsonrpc обязательно и равно "2.0" — иначе клиент говорит на другом протоколе.
            if (request["jsonrpc"] is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String || jv.GetValue<string>() != "2.0")
                return Error(hasId && id is JsonValue ? id : null, -32600, "jsonrpc: ожидается \"2.0\"");
            string method = mv.GetValue<string>();
            var parameters = request["params"] switch
            {
                null => [],
                JsonObject o => o,
                _ => null,
            };
            if (parameters is null)
            {
                if (!hasId) return null;   // уведомление с чужими params — без ответа (уведомлению не отвечают)
                return Error(id is JsonValue ? id : null, -32602, "params — объект");
            }
            if (!hasId)
            {
                Notify(method, parameters, session);
                return null;
            }
            if (id is not JsonValue v || v.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
                return Error(null, -32600, "id запроса — строка или число (не null)");
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallTool(id, parameters, stop, session, limit).ConfigureAwait(false),
                _ => Error(id, -32601, $"нет метода {method}"),
            };
            if (result is null) return null;   // вызов отменён клиентом — ответа нет
            return result["jsonrpc"] is not null ? result : Response(id, result);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            return Error(id, -32602, e.Message);
        }
        catch (Exception e)
        {
            // Внутренняя ошибка — тоже ответ: клиент не должен ждать до своего таймаута.
            Log.Error("agent", $"MCP: {e}");
            return Error(id, -32603, $"внутренняя ошибка: {e.GetType().Name}: {e.Message}");
        }
    }

    private void Notify(string method, JsonObject parameters, string session)
    {
        if (method != "notifications/cancelled" || parameters["requestId"] is not { } requestId) return;
        string key = Key(session, requestId);
        if (!_running.TryGetValue(key, out var cts))
        {
            // Вызова ещё нет (или уже нет): запомнить — вызов, зарегистрировавшись, отменится сразу.
            long now = Environment.TickCount64;
            foreach (var (k, at) in _earlyCancels)
                if (now - at > EarlyCancelTtl.TotalMilliseconds || _earlyCancels.Count > MaxEarlyCancels) _earlyCancels.TryRemove(k, out _);
            _earlyCancels[key] = now;
            if (!_running.TryGetValue(key, out cts)) return;   // вызов зарегистрировался между проверками — отменить его
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // вызов уже закончился
        }
    }

    private static string Key(string session, JsonNode id) => session + "|" + id.ToJsonString();

    private JsonObject Initialize(JsonObject parameters)
    {
        string? asked = parameters["protocolVersion"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.String ? pv.GetValue<string>() : null;
        string version = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0];
        string client = parameters["clientInfo"]?["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : "?";
        Log.Info("agent", $"клиент {client}, протокол {version}");
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "sword", ["title"] = host.Title, ["version"] = host.BuildId },
            ["instructions"] = host.Instructions,
        };
    }

    private JsonObject ListTools() =>
        new() { ["tools"] = new JsonArray([.. host.Tools.Select(t => (JsonNode?)t.Describe())]) };

    private async Task<JsonObject?> CallTool(JsonNode id, JsonObject parameters, CancellationToken stop, string session, TimeSpan? limit)
    {
        string name = parameters["name"] is JsonValue nv && nv.GetValueKind() == JsonValueKind.String
            ? nv.GetValue<string>() : throw new InvalidOperationException("tools/call: нет name");
        var tool = host.Tools.FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException($"нет инструмента {name}; есть: {string.Join(", ", host.Tools.Select(t => t.Name))}");
        // Аргументы — объект или ничего: массив или число — не «нет аргументов», а ошибка запроса.
        var arguments = parameters["arguments"] switch
        {
            null => [],
            JsonObject o => o,
            var other => throw new ArgumentException($"tools/call {name}: arguments — объект, а не {other.ToJsonString()}"),
        };
        // Предел запроса транспорта (HTTP) — отдельно от отмены клиентом: по нему видно, что ответить.
        using var deadline = limit is { } l ? new CancellationTokenSource(l) : new CancellationTokenSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop, deadline.Token);
        string key = Key(session, id);
        _running[key] = cts;
        if (_earlyCancels.TryRemove(key, out _)) cts.Cancel();   // отмена обогнала вызов
        Interlocked.Increment(ref _calls);
        Interlocked.Exchange(ref _lastCall, Environment.TickCount64);
        try
        {
            CheckArguments(tool, arguments);
            // Сеанс завершён или сервер останавливается ещё до начала — инструмент не запускается (time.step заказал бы тики).
            cts.Token.ThrowIfCancellationRequested();
            var output = await tool.Run(new ToolCall(host, arguments, cts.Token)).ConfigureAwait(false);
            return Result(output.Data, host.Receipt(output), isError: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            var data = new JsonObject
            {
                ["error"] = $"{name}: запрос дольше {limit!.Value.TotalSeconds:0.#} с (предел HTTP-запроса) прерван; заказанное " +
                    "время (шаги, цель) остаётся — повторите вызов с меньшим timeoutSeconds",
                ["kind"] = nameof(TimeoutException),
            };
            return Result(data, host.Receipt(new ToolOutput(data) { Tick = host.LastTick }), isError: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            return null;   // отменён клиентом (notifications/cancelled): спецификация — не отвечать
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException or OperationCanceledException
                                      or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Ошибка инструмента — ответ с isError (агент видит и исправляет вызов), а не ошибка протокола.
            var data = new JsonObject { ["error"] = e.Message, ["kind"] = e.GetType().Name };
            return Result(data, host.Receipt(new ToolOutput(data) { Tick = host.LastTick }), isError: true);
        }
        finally
        {
            _running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            Interlocked.Exchange(ref _lastCall, Environment.TickCount64);
            Interlocked.Decrement(ref _calls);
        }
    }

    /// <summary>
    /// Аргументы по схеме инструмента — до исполнения, чтобы ошибочный вызов ничего не успел поменять: неизвестный
    /// (опечатка) — ошибка, а не тихое «по умолчанию» (<c>additionalProperties: false</c>); явный null — ошибка, а не
    /// «не задан»; обязательный — задан.
    /// </summary>
    private static void CheckArguments(AgentTool tool, JsonObject arguments)
    {
        var unknown = arguments.Select(a => a.Key).Where(k => tool.Arguments?.ContainsKey(k) != true).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"{tool.Name}: нет аргумента {string.Join(", ", unknown)}; есть: " +
                (tool.Arguments is { Count: > 0 } a ? string.Join(", ", a.Select(p => p.Key)) : "нет аргументов"));
        var nulls = arguments.Where(a => a.Value is null).Select(a => a.Key).ToList();
        if (nulls.Count > 0)
            throw new ArgumentException($"{tool.Name}: {string.Join(", ", nulls)} = null — не значение; незаданный аргумент пропускается");
        var missing = tool.Required.Where(r => !arguments.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"{tool.Name}: нет обязательного аргумента {string.Join(", ", missing)}");
    }

    private static JsonObject Result(JsonObject data, Receipt receipt, bool isError)
    {
        var structured = new JsonObject { ["receipt"] = receipt.ToJson() };
        foreach (var (k, v) in data) structured[k] = v?.DeepClone();
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(Json.Compact) }),
            ["structuredContent"] = structured,
            ["isError"] = isError,
        };
    }

    private static JsonObject Response(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/// <summary>Настройки JSON ответов: одной строкой (stdio — сообщение на строку), кириллица без \uXXXX.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
