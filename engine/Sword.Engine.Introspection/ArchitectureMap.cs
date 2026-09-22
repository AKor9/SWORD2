using System.Reflection;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;

namespace Sword.Engine.Introspection;

/// <summary>
/// Карта архитектуры из живого мира (раздел 8 плана): сборки процесса, план тика, объяснение системы, кто пользуется
/// компонентом, модули. Строится из фактически собранного мира и загруженных сборок, а не из документации. Работает над
/// копиями (<see cref="SystemInfo"/>, <see cref="Catalog"/>) — на потоке агента.
/// </summary>
public static class ArchitectureMap
{
    /// <summary>Сборки SWORD в процессе: слой и сторона (метаданные сборки), ссылки на другие сборки SWORD.</summary>
    public static JsonObject Projects()
    {
        var sword = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Sword", StringComparison.Ordinal) == true)
            .OrderBy(a => a.GetName().Name, StringComparer.Ordinal).ToList();
        var list = new JsonArray();
        foreach (var a in sword)
        {
            var meta = a.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(m => m.Key, m => m.Value);
            list.Add(new JsonObject
            {
                ["name"] = a.GetName().Name,
                ["layer"] = meta.GetValueOrDefault("SwordLayer"),
                ["side"] = meta.GetValueOrDefault("SwordSide"),
                ["references"] = new JsonArray([.. a.GetReferencedAssemblies().Select(r => r.Name)
                    .Where(n => n?.StartsWith("Sword", StringComparison.Ordinal) == true).Order()
                    .Select(n => (JsonNode?)JsonValue.Create(n))]),
            });
        }
        return new JsonObject
        {
            ["process"] = Assembly.GetEntryAssembly()?.GetName().Name,
            ["assemblies"] = list,
            ["note"] = "загруженные сборки SWORD этого процесса; правила ссылок — раздел 4 ARCHITECTURE_PLAN.md",
        };
    }

    /// <summary>План тика: стадии по порядку и системы в порядке исполнения.</summary>
    public static JsonObject Plan(IReadOnlyList<string> stages, IReadOnlyList<SystemInfo> systems)
    {
        var list = new JsonArray();
        foreach (string stage in stages)
        {
            var inStage = new JsonArray();
            foreach (var s in systems.Where(s => s.Stage == stage).OrderBy(s => s.Order))
                inStage.Add(new JsonObject
                {
                    ["system"] = s.Name,
                    ["module"] = s.Module,
                    ["active"] = s.Active,
                    ["trigger"] = s.Trigger,
                });
            list.Add(new JsonObject { ["stage"] = stage, ["systems"] = inStage });
        }
        return new JsonObject { ["stages"] = list, ["barriers"] = "после каждой стадии — барьер: команды стадии применяются, срабатывания нормализуются" };
    }

    /// <summary>
    /// Система по имени (<c>Метка@Стадия</c>, метке или типу, без учёта регистра; часть имени — если однозначна): что
    /// читает и пишет, что порождает и потребляет, где стоит, кто поставляет ей входы и кто потребляет её выходы.
    /// </summary>
    public static JsonObject ExplainSystem(IReadOnlyList<SystemInfo> systems, string name)
    {
        var s = FindSystem(systems, name);
        var upstream = new JsonArray();
        var downstream = new JsonArray();
        int at = IndexOf(systems, s);
        // Стадии — в порядке исполнения (системы идут по стадиям, внутри — по порядку).
        var stages = systems.Select(x => x.Stage).Distinct().ToList();
        for (int i = 0; i < systems.Count; i++)
        {
            var o = systems[i];
            if (i == at) continue;
            // Связь — по виду входа: чтение колонки берёт запись, Added — добавление, Removed — снятие или удаление
            // сущности, Changed — Set (и возможный ошибочный Add существующего), Event — порождение события, ResourceChanged — установку
            // ресурса. Когда её видно — по тому, как она доходит (SameTick).
            foreach (var group in Inputs(s).Where(input => Feeds(o, input)).GroupBy(input => SameTick(o, i, s, at, input, stages)))
                upstream.Add(Link(o, [.. group.Select(Via)], group.Key ? "в этом тике, раньше" : "с прошлого тика", Why(o, s, group.Key)));
            foreach (var group in Inputs(o).Where(input => Feeds(s, input)).GroupBy(input => SameTick(s, at, o, i, input, stages)))
                downstream.Add(Link(o, [.. group.Select(Via)], group.Key ? "в этом тике, позже" : "в следующем тике", Why(s, o, group.Key)));
        }
        return new JsonObject
        {
            ["system"] = s.Name,
            ["type"] = s.SystemType,
            ["stage"] = s.Stage,
            ["order"] = s.Order,
            ["module"] = s.Module,
            ["active"] = s.Active,
            ["reactive"] = s.Reactive,
            ["trigger"] = s.Trigger,
            ["reads"] = Array(s.Reads),
            ["writes"] = Array(s.Writes),
            ["adds"] = Array(s.Adds),
            ["removes"] = Array(s.Removes),
            ["sets"] = Array(s.Sets),
            ["emits"] = Array(s.Emits),
            ["setsResources"] = Array(s.SetsResources),
            ["consumes"] = Array(s.Consumes),
            ["nextTickOk"] = Array(s.NextTickOk),
            ["after"] = Array(s.After),
            ["before"] = Array(s.Before),
            ["createsEntities"] = s.CreatesEntities,
            ["destroysEntities"] = s.DestroysEntities,
            ["source"] = RelativeSource(s.Source),
            ["upstream"] = upstream,
            ["downstream"] = downstream,
        };
    }

    /// <summary>Кто и как пользуется компонентом (или событием) — по стабильному имени.</summary>
    public static JsonObject ComponentUsage(IReadOnlyList<SystemInfo> systems, string stableName)
    {
        var list = new JsonArray();
        foreach (var s in systems)
        {
            var roles = new List<string>();
            if (s.Reads.Contains(stableName)) roles.Add("reads");
            if (s.Writes.Contains(stableName)) roles.Add("writes");
            if (s.Adds.Contains(stableName)) roles.Add("adds");
            if (s.Removes.Contains(stableName)) roles.Add("removes");
            if (s.Sets.Contains(stableName)) roles.Add("sets");
            if (s.Emits.Contains(stableName)) roles.Add("emits");
            if (s.ConsumesData.Contains(stableName)) roles.Add("consumes");
            if (roles.Count > 0)
                list.Add(new JsonObject { ["system"] = s.Name, ["module"] = s.Module, ["active"] = s.Active, ["roles"] = Array(roles) });
        }
        return new JsonObject { ["data"] = stableName, ["systems"] = list };
    }

    /// <summary>Модули: включённые и выключенные, системы и данные каждого (ядро игры — без модуля).</summary>
    public static JsonObject ModuleGraph(IReadOnlyList<SystemInfo> systems, IReadOnlyList<string> active, IReadOnlyList<string> inactive)
    {
        var list = new JsonArray();
        foreach (var group in systems.GroupBy(s => s.Module ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
            list.Add(new JsonObject
            {
                ["module"] = group.Key.Length == 0 ? null : group.Key,
                ["kind"] = group.Key.Length == 0 ? "ядро игры и служебные стадии" : "модуль",
                ["systems"] = Array([.. group.Select(s => s.Name)]),
                ["writes"] = Array([.. group.SelectMany(Produced).Distinct().Order()]),
                ["reads"] = Array([.. group.SelectMany(Consumed).Distinct().Order()]),
            });
        return new JsonObject { ["active"] = Array(active), ["inactive"] = Array(inactive), ["groups"] = list };
    }

    /// <summary>Система по имени — точное совпадение, иначе единственное вхождение части.</summary>
    public static SystemInfo FindSystem(IReadOnlyList<SystemInfo> systems, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool Same(string? a) => string.Equals(a, name, StringComparison.OrdinalIgnoreCase);
        var exact = systems.Where(s => Same(s.Name) || Same(s.Label) || Same(s.SystemType) || Same(TypeName(s.SystemType))).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = exact.Count > 1 ? exact
            : systems.Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || s.SystemType.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        throw new ArgumentException(partial.Count == 0
            ? $"нет системы «{name}»; есть: {string.Join(", ", systems.Select(s => s.Name))}"
            : $"система «{name}» неоднозначна: {string.Join(", ", partial.Select(s => s.Name))}");
    }

    private static IEnumerable<string> Produced(SystemInfo s) => [.. s.Writes, .. s.Adds, .. s.Removes, .. s.Sets, .. s.Emits];

    /// <summary>Входы системы: чтение колонок (<c>Read</c>) и потребляемые журналы с видом.</summary>
    private static IEnumerable<TriggerInfo> Inputs(SystemInfo s) =>
        [.. s.Reads.Select(r => new TriggerInfo("Read", r)), .. s.ConsumesTriggers];

    /// <summary>Даёт ли система <paramref name="p"/> вход <paramref name="input"/>.</summary>
    private static bool Feeds(SystemInfo p, TriggerInfo input) => input.Kind switch
    {
        "Read" => p.Writes.Contains(input.Data) || p.Adds.Contains(input.Data) || p.Sets.Contains(input.Data),
        "Added" => p.Adds.Contains(input.Data),
        "Removed" => p.Removes.Contains(input.Data) || p.DestroysEntities,
        // Как Registration.Produces: штатно Changed даёт только Set (Add существующего проверки запрещают), но при
        // отключённых проверках ошибочный Add существующего заменяет значение — Add возможный производитель Changed.
        "Changed" => p.Sets.Contains(input.Data) || p.Adds.Contains(input.Data),
        "Event" => p.Emits.Contains(input.Data),
        "ResourceChanged" => p.SetsResources.Contains(input.Data),
        _ => false,
    };

    private static string Via(TriggerInfo input) => $"{input.Kind}:{input.Data}";

    private static IEnumerable<string> Consumed(SystemInfo s) => [.. s.Reads, .. s.ConsumesData];

    /// <summary>Имя типа без пространства имён и обобщённых аргументов (<c>A.B+C`1[[...]]</c> → <c>C`1</c>).</summary>
    private static string TypeName(string fullName)
    {
        int generic = fullName.IndexOf('[');
        string head = generic > 0 ? fullName[..generic] : fullName;
        return head.Split('.', '+')[^1];
    }

    /// <summary>
    /// Видит ли потребитель <paramref name="c"/> вход <paramref name="input"/> от производителя <paramref name="p"/> в
    /// том же тике. Прямая запись колонки (<c>Writes</c>) видна сразу — системе, что идёт позже по порядку, в том числе в
    /// той же стадии. Всё, что идёт командами (<c>Add</c>, <c>Set</c>, <c>Remove</c>, <c>Emit</c>, удаление, ресурсы),
    /// применяется на барьере после стадии производителя: в том же тике — только в более поздней стадии (как
    /// <c>WorldBuilder.CheckProducers</c>, у которого иначе нужен <c>NextTickOk</c>).
    /// </summary>
    private static bool SameTick(SystemInfo p, int pAt, SystemInfo c, int cAt, TriggerInfo input, List<string> stages)
    {
        bool laterStage = stages.IndexOf(p.Stage) < stages.IndexOf(c.Stage);
        if (input.Kind == "Read" && p.Writes.Contains(input.Data) && pAt < cAt) return true;
        return laterStage;
    }

    private static string Why(SystemInfo p, SystemInfo c, bool sameTick) =>
        sameTick ? (p.Stage == c.Stage ? "прямая запись колонки, раньше по порядку стадии" : "стадия производителя раньше, барьер между ними")
        : p.Stage == c.Stage ? "та же стадия: команды применяются на барьере после неё, запись колонки — позже по порядку"
        : "стадия производителя позже стадии потребителя";

    private static JsonObject Link(SystemInfo s, List<string> via, string when, string why) =>
        new() { ["system"] = s.Name, ["stage"] = s.Stage, ["via"] = Array(via), ["when"] = when, ["why"] = why };

    private static int IndexOf(IReadOnlyList<SystemInfo> systems, SystemInfo s)
    {
        for (int i = 0; i < systems.Count; i++)
            if (ReferenceEquals(systems[i], s)) return i;
        return -1;
    }

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);

    /// <summary>Путь исходника от корня репозитория (папка с SWORD.slnx), если он найден; иначе как есть.</summary>
    public static string? RelativeSource(string? source)
    {
        if (source is null) return null;
        int colon = source.LastIndexOf(':');
        string file = colon > 2 ? source[..colon] : source;
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "SWORD.slnx")))
                return Path.GetRelativePath(dir, file).Replace('\\', '/') + (colon > 2 ? source[colon..] : "");
        return source;
    }
}
