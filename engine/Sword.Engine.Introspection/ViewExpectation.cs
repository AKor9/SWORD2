using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Engine.Introspection;

/// <summary>
/// Ожидание вида фичи по итогу App (<c>view-&lt;id&gt;.json</c>, раздел <c>--json</c>): значение по пути <see cref="Path"/> равно
/// <see cref="Equal"/> или лежит в [<see cref="Min"/>, <see cref="Max"/>]. Путь — имена через точку, индекс массива — <c>[n]</c>, <c>*</c> —
/// каждый элемент объекта или массива (хотя бы один; ожидание — у каждого): <c>fire.checks.*</c>, <c>fire.metrics[0].flame.heightCm</c>.
/// Значения нет (путь не найден, пустое <c>*</c>) — провал; числа сравниваются как числа.
/// </summary>
public sealed class ViewExpectation
{
    /// <summary>Шаги пути: имя свойства, индекс массива или «каждый элемент».</summary>
    private readonly List<Step> _steps;

    private ViewExpectation(string path, JsonNode? equal, double? min, double? max)
    {
        _steps = ParsePath(path);
        Path = path;
        Equal = equal;
        Min = min;
        Max = max;
    }

    public string Path { get; }

    /// <summary>Значение (число, строка, true/false) — или null, тогда диапазон <see cref="Min"/>…<see cref="Max"/>.</summary>
    public JsonNode? Equal { get; }
    public double? Min { get; }
    public double? Max { get; }

    private readonly record struct Step(string? Name, int Index, bool Each);

    internal static ViewExpectation Parse(FeatureManifest.Reader r)
    {
        r.Only("path", "equals", "min", "max");
        string path = r.String("path");
        try
        {
            ParsePath(path);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"{r.At}.path «{path}»: {e.Message}");
        }
        JsonNode? equal = r.Has("equals") ? r.Any("equals") : null;
        double? min = r.OptionalNumber("min"), max = r.OptionalNumber("max");
        if (equal is null == (min is null && max is null))
            throw new ArgumentException($"{r.At}: нужно equals или min/max (одно из двух)");
        if (equal is not null and not JsonValue) throw new ArgumentException($"{r.At}.equals: число, строка или true/false");
        if (min > max) throw new ArgumentException($"{r.At}: min {Text(min.Value)} больше max {Text(max!.Value)}");
        return new ViewExpectation(path, equal, min, max);
    }

    /// <summary>Что ждём — словами (<c>= true</c>, <c>20…40</c>, <c>≥ 20</c>).</summary>
    public string Expected => Equal is not null ? $"= {Equal.ToJsonString()}"
        : Min is { } lo && Max is { } hi ? $"{Text(lo)}…{Text(hi)}"
        : Min is { } l ? $"≥ {Text(l)}" : $"≤ {Text(Max!.Value)}";

    /// <summary>
    /// Проверить итог App <paramref name="root"/>: по записи на каждое найденное значение (путь с индексами вместо <c>*</c>, значение JSON,
    /// выполнено ли) и провал словами, если значения нет или оно не то.
    /// </summary>
    public (bool Ok, JsonArray Values, string? Failure) Check(JsonNode? root)
    {
        var found = new List<(string Path, JsonNode? Value)>();
        string? missing = Resolve(root, 0, "", found);
        var values = new JsonArray();
        var bad = new List<string>();
        foreach (var (path, value) in found)
        {
            bool ok = Matches(value);
            values.Add(new JsonObject { ["path"] = path, ["value"] = value?.DeepClone(), ["ok"] = ok });
            if (!ok) bad.Add($"{path} = {value?.ToJsonString() ?? "null"}");
        }
        string? failure = missing is not null ? $"{Path}: {missing}"
            : bad.Count > 0 ? $"{string.Join(", ", bad)}; ждали {Expected}" : null;
        return (failure is null, values, failure);
    }

    private string? Resolve(JsonNode? node, int step, string at, List<(string, JsonNode?)> found)
    {
        if (step == _steps.Count)
        {
            found.Add((at, node));
            return null;
        }
        var s = _steps[step];
        if (s.Each)
        {
            switch (node)
            {
                case JsonObject o when o.Count > 0:
                    foreach (var (key, child) in o)
                        if (Resolve(child, step + 1, Join(at, key), found) is { } m) return m;
                    return null;
                case JsonArray a when a.Count > 0:
                    for (int i = 0; i < a.Count; i++)
                        if (Resolve(a[i], step + 1, $"{at}[{i}]", found) is { } m) return m;
                    return null;
                default:
                    return $"{(at.Length == 0 ? "корень" : at)} — {(node is JsonObject or JsonArray ? "пусто" : "не объект и не массив")}";
            }
        }
        if (s.Name is { } name)
            return node is JsonObject o && o.TryGetPropertyValue(name, out var child)
                ? Resolve(child, step + 1, Join(at, name), found)
                : $"нет {Join(at, name)}";
        return node is JsonArray arr && s.Index < arr.Count
            ? Resolve(arr[s.Index], step + 1, $"{at}[{s.Index}]", found)
            : $"нет {at}[{s.Index}]{(node is JsonArray a2 ? $" (элементов {a2.Count})" : "")}";
    }

    private bool Matches(JsonNode? value)
    {
        if (Equal is not null)
        {
            if (value is not JsonValue v) return false;
            var expected = (JsonValue)Equal;
            return (v.GetValueKind(), expected.GetValueKind()) switch
            {
                (JsonValueKind.Number, JsonValueKind.Number) => v.GetValue<double>() == expected.GetValue<double>(),
                _ => JsonNode.DeepEquals(v, expected),
            };
        }
        if (value is not JsonValue n || n.GetValueKind() != JsonValueKind.Number) return false;
        double d = n.GetValue<double>();
        return double.IsFinite(d) && (Min is not { } lo || d >= lo) && (Max is not { } hi || d <= hi);
    }

    private static string Join(string at, string name) => at.Length == 0 ? name : $"{at}.{name}";

    private static string Text(double d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>Путь: <c>a.b[0][1].*.c</c> — имена, индексы, «каждый».</summary>
    private static List<Step> ParsePath(string path)
    {
        var steps = new List<Step>();
        foreach (string part in path.Split('.'))
        {
            if (part == "*")
            {
                steps.Add(new Step(null, 0, true));
                continue;
            }
            int bracket = part.IndexOf('[');
            string name = bracket < 0 ? part : part[..bracket];
            if (name.Length == 0) throw new ArgumentException("пустое имя (a.b, a[0], *)");
            if (name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                throw new ArgumentException($"имя «{name}» — буквы, цифры, «_» и «-»");
            steps.Add(new Step(name, 0, false));
            for (int i = bracket; i >= 0 && i < part.Length;)
            {
                int close = part.IndexOf(']', i);
                if (part[i] != '[' || close < 0 || !int.TryParse(part.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    throw new ArgumentException($"индекс в «{part}» — [0], [1], …");
                steps.Add(new Step(null, index, false));
                i = close + 1;
            }
        }
        return steps;
    }
}
