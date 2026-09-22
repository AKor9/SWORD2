using System.Globalization;
using System.Text.Json.Nodes;
using Sword.Engine.Introspection;

namespace Sword.Engine.Agent;

/// <summary>Класс стоимости инструмента — в описании и аннотациях: агент видит, что дорого.</summary>
public enum ToolCost
{
    /// <summary>Мгновенно, без ожидания симуляции или с одной точкой обслуживания.</summary>
    Cheap,
    /// <summary>Ждёт тики или кадр (шаги времени, снимок).</summary>
    Moderate,
    /// <summary>Долго: много тиков, файлы на диск.</summary>
    Heavy,
}

/// <summary>
/// Инструмент MCP: имя (<c>группа.действие</c>), описание, схема аргументов (JSON Schema), аннотации — только чтение
/// или мутация, идемпотентность, класс стоимости — и исполнитель. Исполнитель работает на потоке агента: к миру он
/// обращается только через точку обслуживания (<see cref="AgentHost.Service"/>).
/// </summary>
public sealed class AgentTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Свойства аргументов (JSON Schema, объект <c>properties</c>); null — аргументов нет.</summary>
    public JsonObject? Arguments { get; init; }

    /// <summary>Обязательные аргументы.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>Ничего не меняет (ни симуляцию, ни время, ни файлы вне артефактов).</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>Повторный вызов с теми же аргументами ничего не добавляет.</summary>
    public bool Idempotent { get; init; } = true;

    /// <summary>Разрушает то, что не вернуть (завершает процесс).</summary>
    public bool Destructive { get; init; }

    public ToolCost Cost { get; init; } = ToolCost.Cheap;

    public required Func<ToolCall, Task<ToolOutput>> Run { get; init; }

    /// <summary>Описание для <c>tools/list</c>.</summary>
    internal JsonObject Describe()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = Arguments?.DeepClone() ?? new JsonObject(),
            ["additionalProperties"] = false,
        };
        if (Required.Count > 0) schema["required"] = new JsonArray([.. Required.Select(r => (JsonNode?)JsonValue.Create(r))]);
        return new JsonObject
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = $"{Description} [{(ReadOnly ? "только чтение" : "меняет")}; стоимость: {CostText(Cost)}]",
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["title"] = Title,
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["idempotentHint"] = Idempotent,
                ["openWorldHint"] = false,
            },
            ["_meta"] = new JsonObject { ["sword/cost"] = Cost.ToString().ToLowerInvariant() },
        };
    }

    private static string CostText(ToolCost cost) => cost switch
    {
        ToolCost.Cheap => "дёшево",
        ToolCost.Moderate => "ждёт тики или кадр",
        _ => "дорого",
    };
}

/// <summary>Вызов инструмента: аргументы с проверкой, отмена, хост.</summary>
public sealed class ToolCall(AgentHost host, JsonObject arguments, CancellationToken cancel)
{
    public AgentHost Host { get; } = host;
    public JsonObject Arguments { get; } = arguments;
    public CancellationToken Cancel { get; } = cancel;

    public JsonNode? Node(string name) => Arguments.TryGetPropertyValue(name, out var v) ? v : null;

    public string? String(string name) =>
        Node(name) switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            var other => throw new ArgumentException($"{name}: ожидается строка, а не {other.ToJsonString()}"),
        };

    public string RequiredString(string name) =>
        String(name) is { Length: > 0 } s ? s : throw new ArgumentException($"{name}: обязательный аргумент");

    public long Integer(string name, long fallback, long min, long max)
    {
        var node = Node(name);
        if (node is null) return fallback;
        long value;
        try
        {
            // 5.0 — тоже целое (JSON Schema integer); 5.5 — нет.
            value = node.GetValueKind() == System.Text.Json.JsonValueKind.Number && node.GetValue<double>() is var d
                    && Math.Floor(d) == d && Math.Abs(d) < 9e15 ? (long)d : node.GetValue<long>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"{name}: ожидается целое, а не {node.ToJsonString()}");
        }
        if (value < min || value > max)
            throw new ArgumentException($"{name} = {value.ToString(CultureInfo.InvariantCulture)}: допустимо {min}…{max}");
        return value;
    }

    public double Number(string name, double fallback, double min, double max)
    {
        var node = Node(name);
        if (node is null) return fallback;
        double value;
        try
        {
            value = node.GetValue<double>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"{name}: ожидается число, а не {node.ToJsonString()}");
        }
        if (!(value >= min && value <= max)) throw new ArgumentException($"{name} = {value}: допустимо {min}…{max}");
        return value;
    }

    public bool Bool(string name, bool fallback) =>
        Node(name) switch
        {
            null => fallback,
            JsonValue v when v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => v.GetValue<bool>(),
            var other => throw new ArgumentException($"{name}: ожидается true или false, а не {other.ToJsonString()}"),
        };

    public IReadOnlyList<string> Strings(string name) =>
        Node(name) switch
        {
            null => [],
            JsonArray a => [.. a.Select((n, i) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? v.GetValue<string>() : throw new ArgumentException($"{name}[{i}]: ожидается строка"))],
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => [v.GetValue<string>()],
            var other => throw new ArgumentException($"{name}: ожидается массив строк, а не {other.ToJsonString()}"),
        };
}

/// <summary>
/// Итог инструмента: данные и то, что уходит в квитанцию — тик и кадр, к которым относится ответ, стадия (останов
/// посреди тика), полнота (<see cref="Truncated"/>, <see cref="Continuation"/>), артефакт.
/// </summary>
public sealed record ToolOutput(JsonObject Data)
{
    public ulong? Tick { get; init; }
    public ulong? Frame { get; init; }

    /// <summary>
    /// Тик и кадр из точки обслуживания — вместе (<see cref="ServiceContext.Point"/>): ответ о мире относится к одному
    /// состоянию, а не к тику точки и кадру, до которого клиент успел дойти, пока агент собирал JSON.
    /// </summary>
    public ServicePoint Point
    {
        init
        {
            Tick = value.Tick;
            Frame = value.Frame;
        }
    }
    public string? Stage { get; init; }
    public bool Truncated { get; init; }
    public string? Continuation { get; init; }
    public string? ArtifactId { get; init; }
}

/// <summary>
/// Квитанция в каждом ответе (раздел 8 плана): сборка, контент, сеанс, тик (<c>TickId</c> — сыграно тиков к моменту
/// ответа), стадия, кадр, полнота, продолжение, артефакт — по ней видно, к какому состоянию относится ответ.
/// </summary>
public sealed record Receipt(string BuildId, string ContentHash, string SessionId, ulong? TickId, string? Stage, ulong? FrameId,
    bool Complete, bool Truncated, string? ContinuationToken, string? ArtifactId)
{
    public JsonObject ToJson() => new()
    {
        ["buildId"] = BuildId,
        ["contentHash"] = ContentHash,
        ["sessionId"] = SessionId,
        ["tickId"] = TickId,
        ["stage"] = Stage,
        ["frameId"] = FrameId,
        ["complete"] = Complete,
        ["truncated"] = Truncated,
        ["continuationToken"] = ContinuationToken,
        ["artifactId"] = ArtifactId,
    };
}

/// <summary>Схема аргументов инструментов (JSON Schema) — для инструментов клиента и игры.</summary>
public static class ToolSchema
{
    public static JsonObject Props(params (string Name, JsonObject Schema)[] props) => CoreTools.Props(props);
    public static JsonObject Str(string description) => CoreTools.Str(description);
    public static JsonObject StrArray(string description) => CoreTools.StrArray(description);
    public static JsonObject Int(string description, long min, long max) => CoreTools.Int(description, min, max);
    public static JsonObject Num(string description, double min, double max) => CoreTools.Num(description, min, max);
}
