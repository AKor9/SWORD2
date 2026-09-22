using System.Globalization;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// Наблюдательный снимок мира (раздел 8 плана, <c>world.snapshot</c>): в точке обслуживания — копии байтов компонентов
/// всех живых сущностей, ресурсов-значений и описания участников хеша (кроме <c>ecs</c> — его данные и так в снимке);
/// разбор — на потоке агента в плоский словарь «ключ → значение листа» (<see cref="Flatten"/>). Не сериализация для
/// восстановления мира — её нет (раздел 5): снимок только сравнивают (<see cref="SnapshotDiff"/>).
/// </summary>
public sealed class WorldSnapshot
{
    private readonly List<(Entity Entity, int Component, byte[] Bytes)> _components = [];
    private readonly List<(int Resource, byte[] Bytes)> _resources = [];
    private readonly List<(string Part, string Json)> _parts = [];
    private SortedDictionary<string, JsonNode?>? _flat;

    private WorldSnapshot(string id, ServicePoint point)
    {
        Id = id;
        Point = point;
    }

    public string Id { get; }

    /// <summary>Тик, кадр и стадия снимка (посреди тика — после барьера стадии).</summary>
    public ServicePoint Point { get; }

    public int Entities { get; private set; }

    /// <summary>Снять (поток симуляции, в точке обслуживания): только копии байтов и описаний.</summary>
    public static WorldSnapshot Take(in ServiceContext ctx, string id, bool parts = true)
    {
        var s = new WorldSnapshot(id, ctx.Point);
        var world = ctx.World;
        for (int i = 0, n = world.EntitySlots; i < n; i++)
        {
            var e = world.EntityAt(i);
            if (e.IsNull) continue;
            s.Entities++;
            foreach (int c in world.ComponentIds(e))
                s._components.Add((e, c, world.Copy(e, c)!));
        }
        for (int r = 0; r < world.Resources.Count; r++)
            if (world.CopyResource(r) is { } bytes) s._resources.Add((r, bytes));
        if (parts)
            foreach (string part in ctx.StateParts)
                if (part != "ecs") s._parts.Add((part, ctx.DescribeState(part, new DescribeQuery(null, 0, int.MaxValue))));
        return s;
    }

    /// <summary>
    /// Плоский вид (поток агента, один раз): <c>12v3/Transform/Position.X</c>, <c>12v3/PlayerTag</c> (метка — true),
    /// <c>resource/SimTime/Time</c>, <c>animation/2v1/joints[3].local.x</c> — значения листьев.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> Flatten(Catalog catalog)
    {
        if (_flat is not null) return _flat;
        var flat = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (e, c, bytes) in _components)
        {
            var info = catalog.Components[c];
            string prefix = $"{e}/{catalog.DisplayName(info)}";
            if (info.IsTag)
            {
                flat[prefix] = JsonValue.Create(true);
                continue;
            }
            AddLeaves(flat, prefix, FieldLayout.Of(info.Type), bytes);
        }
        foreach (var (r, bytes) in _resources)
        {
            var info = catalog.Resources[r];
            AddLeaves(flat, $"resource/{Catalog.ShortName(info.Type)}", FieldLayout.Of(info.Type), bytes);
        }
        foreach (var (part, json) in _parts)
            if (JsonNode.Parse(json) is JsonObject o)
            {
                if (o["items"] is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                        AddJson(flat, $"{part}/{item["id"]?.GetValue<string>() ?? "?"}", item, skip: "id");
                foreach (var (key, value) in o)
                    if (key is not ("items" or "matched")) AddJson(flat, $"{part}/{key}", value, skip: null, top: false);
            }
        return _flat = flat;
    }

    private static void AddLeaves(SortedDictionary<string, JsonNode?> flat, string prefix, FieldLayout layout, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < layout.Leaves.Count; i++)
        {
            var leaf = layout.Leaves[i];
            flat[leaf.Path.Length == 0 ? prefix : $"{prefix}/{leaf.Path}"] = ObservationPredicate.LeafJson(leaf.Kind, layout.Bits(bytes, i));
        }
    }

    /// <summary>Описание участника: первый уровень — через «/», глубже — через «.», массивы — [i].</summary>
    private static void AddJson(SortedDictionary<string, JsonNode?> flat, string prefix, JsonNode? node, string? skip, bool top = true)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                    if (key != skip) AddJson(flat, top ? $"{prefix}/{key}" : $"{prefix}.{key}", value, null, top: false);
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) AddJson(flat, $"{prefix}[{i}]", a[i], null, top: false);
                break;
            default:
                flat[prefix] = node?.DeepClone();
                break;
        }
    }
}

/// <summary>Сравнение двух снимков по ключам (<c>world.diff</c>): что появилось, пропало и изменилось (с разницей у чисел).</summary>
public static class SnapshotDiff
{
    public sealed record Row(string Key, JsonNode? A, JsonNode? B, double? Delta);

    public sealed record Result(int Compared, int Changed, int Added, int Removed, IReadOnlyList<Row> Rows,
        IReadOnlyList<string> EntitiesAdded, IReadOnlyList<string> EntitiesRemoved);

    /// <param name="filter">Только ключи, содержащие эту строку (без учёта регистра); null — все.</param>
    /// <param name="tolerance">Числа, различающиеся не больше чем на столько, считаются равными (0 — точно).</param>
    public static Result Compare(IReadOnlyDictionary<string, JsonNode?> a, IReadOnlyDictionary<string, JsonNode?> b, string? filter, double tolerance)
    {
        var rows = new List<Row>();
        int compared = 0, changed = 0, added = 0, removed = 0;
        bool Keep(string key) => filter is null || key.Contains(filter, StringComparison.OrdinalIgnoreCase);
        foreach (var (key, va) in a)
        {
            if (!Keep(key)) continue;
            compared++;
            if (!b.TryGetValue(key, out var vb))
            {
                removed++;
                rows.Add(new Row(key, va?.DeepClone(), null, null));
                continue;
            }
            if (Same(va, vb, tolerance, out double? delta)) continue;
            changed++;
            rows.Add(new Row(key, va?.DeepClone(), vb?.DeepClone(), delta));
        }
        foreach (var (key, vb) in b)
        {
            if (!Keep(key) || a.ContainsKey(key)) continue;
            added++;
            rows.Add(new Row(key, null, vb?.DeepClone(), null));
        }
        rows.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        var entitiesA = EntitiesOf(a.Keys);
        var entitiesB = EntitiesOf(b.Keys);
        return new Result(compared, changed, added, removed, rows,
            [.. entitiesB.Except(entitiesA).Order(StringComparer.Ordinal)], [.. entitiesA.Except(entitiesB).Order(StringComparer.Ordinal)]);
    }

    private static HashSet<string> EntitiesOf(IEnumerable<string> keys) =>
        [.. keys.Select(k => k[..Math.Max(k.IndexOf('/'), 0)]).Where(k => k.Length > 0 && char.IsDigit(k[0]))];

    private static bool Same(JsonNode? a, JsonNode? b, double tolerance, out double? delta)
    {
        delta = null;
        if (TryNumber(a, out double x) && TryNumber(b, out double y))
        {
            delta = y - x;
            if (x.Equals(y)) return true;
            return tolerance > 0 && Math.Abs(y - x) <= tolerance;
        }
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>Число JSON (любого типа в памяти: float, long…) — через его текст.</summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
