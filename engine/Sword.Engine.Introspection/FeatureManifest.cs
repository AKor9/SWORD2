using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Engine.Introspection;

/// <summary>
/// Манифест фичи (раздел 8 плана, «Контракт фичи»; раздел 12): машинное описание приёмки рядом с md-документом фичи —
/// <c>feature.json</c>. Сценарии с проверками, инварианты, метрики с допусками и источником эталона, виды в App, бюджеты,
/// список «что проверить в игре». Исполняет его <see cref="FeatureVerifier"/> (<c>feature.verify</c>). Разбор строгий:
/// неизвестный ключ, неверный тип, ссылка на несуществующий сценарий — ошибка с путём. Ослабить ожидания (допуск,
/// инвариант) можно только правкой файла — в коммите и с сообщением пользователю.
/// </summary>
public sealed record FeatureManifest
{
    public const string Format = "sword-feature", FileName = "feature.json";
    public const int Version = 1;

    /// <summary>Правило Id фичи: строчная латиница, цифры и <c>_</c>, первой — буква (как у модулей).</summary>
    public const string IdRule = "строчная латиница, цифры и '_', первой — буква";

    /// <summary>Стабильный Id (<c>movement</c>, <c>animation</c>).</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Файл манифеста (полный путь) и md-документ фичи (полный путь).</summary>
    public required string Path { get; init; }
    public required string Doc { get; init; }

    /// <summary>Где код фичи — пути от корня репозитория.</summary>
    public IReadOnlyList<string> SourceRoots { get; init; } = [];

    /// <summary>С какими модулями фича проверяется: в сценариях они должны быть активны.</summary>
    public IReadOnlyList<string> RequiredModules { get; init; } = [];

    public IReadOnlyList<FeatureScenario> Scenarios { get; init; } = [];

    /// <summary>
    /// Инварианты, которые должны держаться: id или «префикс.*». В сценариях они включаются явно (и «по запросу» тоже);
    /// нарушение любого включённого инварианта — провал сценария.
    /// </summary>
    public IReadOnlyList<string> Invariants { get; init; } = [];

    public IReadOnlyList<FeatureMetric> Metrics { get; init; } = [];
    public IReadOnlyList<FeatureView> Views { get; init; } = [];
    public IReadOnlyList<FeatureBudget> Budgets { get; init; } = [];

    /// <summary>Что проверить в игре (раздел 12, пункт 4) — те же пункты, что в md фичи.</summary>
    public IReadOnlyList<string> Checklist { get; init; } = [];

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] is < 'a' or > 'z') return false;
        foreach (char c in id)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

    public FeatureScenario Scenario(string id) => Scenarios.FirstOrDefault(s => s.Id == id)
        ?? throw new ArgumentException($"{Id}: нет сценария «{id}»; есть: {string.Join(", ", Scenarios.Select(s => s.Id))}");

    /// <summary>Прочитать и проверить манифест.</summary>
    /// <exception cref="InvalidDataException">Ошибка формата — с путём в файле.</exception>
    public static FeatureManifest Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: не JSON — {e.Message}");
        }
        try
        {
            return Parse(Reader.Object(root, ""), path);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}");
        }
    }

    private static FeatureManifest Parse(Reader r, string path)
    {
        r.Only("format", "version", "id", "title", "doc", "sourceRoots", "requiredModules", "scenarios", "invariants", "metrics",
            "views", "budgets", "checklist");
        if (r.String("format") != Format) throw new ArgumentException($"format: «{Format}»");
        if (r.Integer("version") != Version) throw new ArgumentException($"version: {Version}");
        string id = r.String("id");
        if (!IsValidId(id)) throw new ArgumentException($"id «{id}»: {IdRule}");
        string dir = System.IO.Path.GetDirectoryName(path)!;
        var scenarios = r.Objects("scenarios").Select(FeatureScenario.Parse).ToList();
        Unique(scenarios.Select(s => s.Id), "scenarios");
        if (scenarios.Count == 0) throw new ArgumentException("scenarios: хотя бы один сценарий");
        var names = scenarios.Select(s => s.Id).ToHashSet();
        void Known(string scenario, string at)
        {
            if (!names.Contains(scenario)) throw new ArgumentException($"{at}.scenario: нет сценария «{scenario}»; есть: {string.Join(", ", names)}");
        }
        var metrics = r.Objects("metrics", optional: true).Select(FeatureMetric.Parse).ToList();
        Unique(metrics.Select(m => m.Id), "metrics");
        foreach (var m in metrics) Known(m.Scenario, $"metrics.{m.Id}");
        var views = r.Objects("views", optional: true).Select(FeatureView.Parse).ToList();
        Unique(views.Select(v => v.Id), "views");
        foreach (var v in views) Known(v.Scenario, $"views.{v.Id}");
        var budgets = r.Objects("budgets", optional: true).Select(FeatureBudget.Parse).ToList();
        Unique(budgets.Select(b => b.Id), "budgets");
        foreach (var b in budgets) Known(b.Scenario, $"budgets.{b.Id}");
        var checklist = r.Strings("checklist");
        if (checklist.Count == 0) throw new ArgumentException("checklist: хотя бы один пункт «что проверить в игре»");
        return new FeatureManifest
        {
            Id = id,
            Title = r.String("title"),
            Path = path,
            Doc = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, r.String("doc"))),
            SourceRoots = r.Strings("sourceRoots"),
            RequiredModules = r.Strings("requiredModules", optional: true),
            Scenarios = scenarios,
            Invariants = r.Strings("invariants", optional: true),
            Metrics = metrics,
            Views = views,
            Budgets = budgets,
            Checklist = checklist,
        };
    }

    internal static void Unique(IEnumerable<string> ids, string at)
    {
        var seen = new HashSet<string>();
        foreach (string id in ids)
            if (!seen.Add(id)) throw new ArgumentException($"{at}: id «{id}» дважды");
    }

    /// <summary>Манифесты под каталогами <paramref name="roots"/> (кроме bin, obj) — по Id; Id без повторов.</summary>
    /// <exception cref="InvalidDataException">Манифест с ошибкой или Id дважды.</exception>
    public static IReadOnlyList<FeatureManifest> Find(IEnumerable<string> roots)
    {
        var list = new List<FeatureManifest>();
        foreach (string root in roots.Where(Directory.Exists))
            foreach (string file in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var parts = file.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                if (parts.Any(p => p is "bin" or "obj")) continue;
                var m = Load(file);
                if (list.FirstOrDefault(x => x.Id == m.Id) is { } other)
                    throw new InvalidDataException($"фича {m.Id} дважды: {other.Path} и {m.Path}");
                list.Add(m);
            }
        return [.. list.OrderBy(m => m.Id, StringComparer.Ordinal)];
    }

    /// <summary>Строгое чтение объекта JSON: известные ключи, типы, путь в сообщениях.</summary>
    internal sealed class Reader(JsonObject node, string at)
    {
        public JsonObject Node { get; } = node;
        public string At { get; } = at;

        public static Reader Object(JsonNode? node, string at) =>
            new(node as JsonObject ?? throw new ArgumentException($"{(at.Length == 0 ? "корень" : at)}: объект"), at);

        private string P(string key) => At.Length == 0 ? key : $"{At}.{key}";

        public void Only(params string[] keys)
        {
            foreach (var (key, _) in Node)
                if (!keys.Contains(key)) throw new ArgumentException($"{P(key)}: неизвестный ключ; есть: {string.Join(", ", keys)}");
        }

        public bool Has(string key) => Node[key] is not null;

        public string String(string key) =>
            Node[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new ArgumentException($"{P(key)}: непустая строка");

        public string? OptionalString(string key) => Has(key) ? String(key) : null;

        public double Number(string key) =>
            Node[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.IsFinite(v.GetValue<double>())
                ? v.GetValue<double>() : throw new ArgumentException($"{P(key)}: конечное число");

        public double? OptionalNumber(string key) => Has(key) ? Number(key) : null;

        public long Integer(string key, long min = long.MinValue, long max = long.MaxValue)
        {
            double d = Number(key);
            if (d != Math.Floor(d) || d < min || d > max)
                throw new ArgumentException($"{P(key)}: целое {min.ToString(CultureInfo.InvariantCulture)}…{max.ToString(CultureInfo.InvariantCulture)}");
            return (long)d;
        }

        public long? OptionalInteger(string key, long min = long.MinValue, long max = long.MaxValue) => Has(key) ? Integer(key, min, max) : null;

        public bool Bool(string key, bool fallback) =>
            !Has(key) ? fallback
            : Node[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>()
            : throw new ArgumentException($"{P(key)}: true или false");

        public IReadOnlyList<string> Strings(string key, bool optional = false)
        {
            if (!Has(key))
                return optional ? [] : throw new ArgumentException($"{P(key)}: массив строк");
            if (Node[key] is not JsonArray a) throw new ArgumentException($"{P(key)}: массив строк");
            return [.. a.Select((n, i) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s
                : throw new ArgumentException($"{P(key)}[{i}]: непустая строка"))];
        }

        public IEnumerable<Reader> Objects(string key, bool optional = false)
        {
            if (!Has(key))
                return optional ? [] : throw new ArgumentException($"{P(key)}: массив объектов");
            if (Node[key] is not JsonArray a) throw new ArgumentException($"{P(key)}: массив объектов");
            return [.. a.Select((n, i) => Object(n, $"{P(key)}[{i}]"))];
        }

        public JsonNode Any(string key) => Node[key]?.DeepClone() ?? throw new ArgumentException($"{P(key)}: нужно значение");

        public JsonObject? OptionalObject(string key) =>
            !Has(key) ? null : Node[key] as JsonObject is { } o ? (JsonObject)o.DeepClone() : throw new ArgumentException($"{P(key)}: объект");
    }
}

/// <summary>
/// Сценарий фичи: реплей (<see cref="Replay"/>, путь от корня репозитория) или команды по тикам (<see cref="Commands"/>) на
/// конфиге из кода, <see cref="Sets"/> поверх; <see cref="Ticks"/> тиков (у реплея по умолчанию — его длина). Хеши реплея
/// сверяются, если они в нём есть. Проверки — условия языка <see cref="ObservationPredicate"/> по тикам.
/// </summary>
public sealed record FeatureScenario
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Replay { get; init; }
    public ulong? Ticks { get; init; }
    public IReadOnlyList<string> Sets { get; init; } = [];
    public IReadOnlyList<ScenarioCommand> Commands { get; init; } = [];
    public IReadOnlyList<FeatureCheck> Checks { get; init; } = [];

    internal static FeatureScenario Parse(FeatureManifest.Reader r)
    {
        r.Only("id", "title", "replay", "ticks", "set", "commands", "checks");
        string id = r.String("id");
        if (!FeatureManifest.IsValidId(id.Replace('-', '_'))) throw new ArgumentException($"{r.At}.id «{id}»: {FeatureManifest.IdRule} (и '-')");
        var commands = r.Objects("commands", optional: true).Select(ScenarioCommand.Parse).ToList();
        string? replay = r.OptionalString("replay");
        ulong? ticks = r.OptionalInteger("ticks", 1, 10_000_000) is { } t ? (ulong)t : null;
        if (replay is null && ticks is null) throw new ArgumentException($"{r.At}: без реплея нужно ticks — сколько тиков играть");
        var checks = r.Objects("checks", optional: true).Select(FeatureCheck.Parse).ToList();
        FeatureManifest.Unique(checks.Select(c => c.Id), $"{r.At}.checks");
        return new FeatureScenario
        {
            Id = id, Title = r.OptionalString("title"), Replay = replay, Ticks = ticks, Sets = r.Strings("set", optional: true),
            Commands = commands, Checks = checks,
        };
    }
}

/// <summary>
/// Команда сценария (и ветки эксперимента): тип команды тика (<c>world.types</c>), поля как у <c>command.send</c>, тики
/// <see cref="From"/> … <see cref="To"/> − 1 (номера тиков, как у команд).
/// </summary>
public sealed record ScenarioCommand(string Command, JsonObject? Fields, ulong From, ulong To)
{
    internal static ScenarioCommand Parse(FeatureManifest.Reader r)
    {
        r.Only("command", "fields", "from", "to");
        ulong from = (ulong)r.Integer("from", 0, 100_000_000), to = (ulong)r.Integer("to", 1, 100_000_000);
        if (to <= from) throw new ArgumentException($"{r.At}: to {to} не больше from {from}");
        return new ScenarioCommand(r.String("command"), r.OptionalObject("fields"), from, to);
    }

    /// <summary>Из JSON инструмента (<c>experiment.fork</c>).</summary>
    public static ScenarioCommand FromJson(JsonNode? node, string at) => Parse(FeatureManifest.Reader.Object(node, at));
}

/// <summary>
/// Проверка сценария: условие мира <see cref="Condition"/> после каждого тика в окне <see cref="From"/> … <see cref="To"/>
/// (номера <c>tickId</c> — сыграно тиков; по умолчанию — весь сценарий) и ожидание <see cref="Expect"/>:
/// <c>always</c> — всегда, <c>never</c> — ни разу, <c>eventually</c> — хотя бы раз, <c>atEnd</c> — после последнего тика.
/// </summary>
public sealed record FeatureCheck(string Id, string Expect, JsonNode Condition, ulong? From, ulong? To, string? Title)
{
    public static readonly string[] Expectations = ["always", "never", "eventually", "atEnd"];

    internal static FeatureCheck Parse(FeatureManifest.Reader r)
    {
        r.Only("id", "title", "expect", "condition", "from", "to");
        string expect = r.String("expect");
        if (!Expectations.Contains(expect)) throw new ArgumentException($"{r.At}.expect «{expect}»: {string.Join(", ", Expectations)}");
        ulong? from = r.OptionalInteger("from", 0) is { } f ? (ulong)f : null, to = r.OptionalInteger("to", 0) is { } t ? (ulong)t : null;
        if (from > to) throw new ArgumentException($"{r.At}: from больше to");
        if (expect == "atEnd" && (from is not null || to is not null)) throw new ArgumentException($"{r.At}: atEnd — без from и to");
        return new FeatureCheck(r.String("id"), expect, r.Any("condition"), from, to, r.OptionalString("title"));
    }
}

/// <summary>
/// Метрика фичи с ожиданием, допуском и источником эталона (<c>sword</c> — SWORD 2eab13b, <c>sword2</c> — эталон SWORD2):
/// значение поля сущности (<see cref="Field"/>; несколько листьев — длина вектора) после тика <see cref="At"/> (по
/// умолчанию — последнего) или <see cref="Stat"/> по тикам окна; либо ряд полей тик за тиком против трассы
/// (<see cref="Trace"/> — файл <c>{ticks:[{tick, ключ: число | [числа]}]}</c>, <see cref="Series"/> — ключ трассы → поле).
/// </summary>
public sealed record FeatureMetric
{
    public static readonly string[] Sources = ["sword", "sword2"];
    public static readonly string[] Stats = ["value", "min", "max", "mean"];

    public required string Id { get; init; }
    public required string Scenario { get; init; }
    public required string Source { get; init; }

    /// <summary>Откуда ожидание — для людей (файл и константа SWORD, реплей SWORD2).</summary>
    public required string Reference { get; init; }

    public required string Entity { get; init; }
    public string? Field { get; init; }
    public string Stat { get; init; } = "value";
    public ulong? At { get; init; }
    public ulong? From { get; init; }
    public ulong? To { get; init; }
    public double? Expected { get; init; }
    public double Tolerance { get; init; }
    public string? Trace { get; init; }
    public IReadOnlyDictionary<string, string>? Series { get; init; }

    internal static FeatureMetric Parse(FeatureManifest.Reader r)
    {
        r.Only("id", "scenario", "source", "reference", "entity", "field", "stat", "at", "from", "to", "expected", "tolerance", "trace", "series");
        string source = r.String("source");
        if (!Sources.Contains(source)) throw new ArgumentException($"{r.At}.source «{source}»: {string.Join(", ", Sources)}");
        double tolerance = r.OptionalNumber("tolerance") ?? 0;
        if (tolerance < 0) throw new ArgumentException($"{r.At}.tolerance: не меньше 0");
        var m = new FeatureMetric
        {
            Id = r.String("id"), Scenario = r.String("scenario"), Source = source, Reference = r.String("reference"),
            Entity = r.String("entity"), Field = r.OptionalString("field"), Stat = r.OptionalString("stat") ?? "value",
            At = r.OptionalInteger("at", 0) is { } at ? (ulong)at : null,
            From = r.OptionalInteger("from", 0) is { } f ? (ulong)f : null, To = r.OptionalInteger("to", 0) is { } t ? (ulong)t : null,
            Expected = r.OptionalNumber("expected"), Tolerance = tolerance, Trace = r.OptionalString("trace"),
        };
        if (!Stats.Contains(m.Stat)) throw new ArgumentException($"{r.At}.stat «{m.Stat}»: {string.Join(", ", Stats)}");
        if (m.Trace is not null)
        {
            var series = r.OptionalObject("series") ?? throw new ArgumentException($"{r.At}: с trace нужно series — ключ трассы → поле");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in series)
                map[key] = value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s
                    : throw new ArgumentException($"{r.At}.series.{key}: путь поля");
            if (map.Count == 0) throw new ArgumentException($"{r.At}.series: хотя бы одна пара");
            if (m.Field is not null || m.Expected is not null || r.Has("stat") || m.At is not null)
                throw new ArgumentException($"{r.At}: с trace — только series и tolerance (без field, expected, stat, at)");
            return m with { Series = map };
        }
        if (m.Field is null || m.Expected is null) throw new ArgumentException($"{r.At}: нужно field и expected (или trace и series)");
        if (m.Stat == "value" && (m.From is not null || m.To is not null)) throw new ArgumentException($"{r.At}: from/to — только со stat min, max, mean");
        if (m.Stat != "value" && m.At is not null) throw new ArgumentException($"{r.At}: at — только со stat value");
        return m;
    }
}

/// <summary>
/// Вид фичи — кадр App (<c>Sword.exe --replay</c>) после <see cref="Tick"/> тиков сценария: ракурс <see cref="Camera"/>
/// (<c>поворот,наклон,дистанция[,x,z]</c>; без него — камера SWORD за игроком), HUD — по <see cref="Hud"/>, отладочные
/// оверлеи игры поверх кадра — <see cref="Overlays"/> (<c>Sword.exe --overlay</c>: у арены — <c>capsules</c>, капсулы тел, 5в;
/// имя, которого игра не знает, — провал вида).
/// </summary>
public sealed record FeatureView(string Id, string Scenario, ulong Tick, string? Camera, bool Hud, string? Title)
{
    public IReadOnlyList<string> Overlays { get; init; } = [];

    internal static FeatureView Parse(FeatureManifest.Reader r)
    {
        r.Only("id", "title", "scenario", "tick", "camera", "hud", "overlays");
        var overlays = ParseOverlays(r);
        return new FeatureView(r.String("id"), r.String("scenario"), (ulong)r.Integer("tick", 1, 1_000_000), r.OptionalString("camera"),
            r.Bool("hud", false), r.OptionalString("title")) { Overlays = overlays };
    }

    /// <summary>Поле <c>overlays</c> (вида и бюджета кадра): имена оверлеев игры — строчные латинские буквы, цифры и «-», без повторов.</summary>
    internal static IReadOnlyList<string> ParseOverlays(FeatureManifest.Reader r)
    {
        var overlays = r.Strings("overlays", optional: true);
        for (int i = 0; i < overlays.Count; i++)
        {
            if (!overlays[i].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                throw new ArgumentException($"{r.At}.overlays[{i}]: имя оверлея — строчные латинские буквы, цифры и «-», а не «{overlays[i]}»");
            if (overlays.Take(i).Contains(overlays[i])) throw new ArgumentException($"{r.At}.overlays[{i}]: {overlays[i]} дважды");
        }
        return overlays;
    }
}

/// <summary>
/// Бюджет производительности сценария: мс на тик (медиана <see cref="Repeats"/> прогонов без инвариантов, как бенч), мс
/// стадий, выделенные байты на тик после <see cref="Warmup"/> тиков прогрева; кадр App (<see cref="Frame"/>, точка 4д).
/// Время проверяется только в оптимизированной сборке (Release); байты — всегда.
/// </summary>
public sealed record FeatureBudget
{
    public required string Id { get; init; }
    public required string Scenario { get; init; }
    public double? MsPerTick { get; init; }
    public IReadOnlyDictionary<string, double> Stages { get; init; } = new Dictionary<string, double>();
    public double? AllocatedBytesPerTick { get; init; }
    public int Repeats { get; init; } = 5;
    public int Warmup { get; init; } = 120;

    /// <summary>Бюджет кадра App на записи сценария; null — кадр не меряется.</summary>
    public FeatureFrameBudget? Frame { get; init; }

    /// <summary>Есть что мерить в симуляции (мс на тик, стадии, байты), а не только кадр.</summary>
    public bool HasSimulation => MsPerTick is not null || Stages.Count > 0 || AllocatedBytesPerTick is not null;

    internal static FeatureBudget Parse(FeatureManifest.Reader r)
    {
        r.Only("id", "scenario", "msPerTick", "stages", "allocatedBytesPerTick", "repeats", "warmup", "frame");
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        if (r.OptionalObject("stages") is { } o)
            foreach (var (key, _) in o)
                stages[key] = new FeatureManifest.Reader(o, $"{r.At}.stages").Number(key);
        var b = new FeatureBudget
        {
            Id = r.String("id"), Scenario = r.String("scenario"), MsPerTick = r.OptionalNumber("msPerTick"), Stages = stages,
            AllocatedBytesPerTick = r.OptionalNumber("allocatedBytesPerTick"),
            Repeats = (int)(r.OptionalInteger("repeats", 1, 50) ?? 5), Warmup = (int)(r.OptionalInteger("warmup", 0, 100_000) ?? 120),
            Frame = r.OptionalObject("frame") is { } f ? FeatureFrameBudget.Parse(new FeatureManifest.Reader(f, $"{r.At}.frame")) : null,
        };
        if (!b.HasSimulation && b.Frame is null)
            throw new ArgumentException($"{r.At}: нечего проверять — msPerTick, stages, allocatedBytesPerTick или frame");
        if (b.MsPerTick <= 0 || b.Stages.Values.Any(v => v <= 0) || b.AllocatedBytesPerTick < 0)
            throw new ArgumentException($"{r.At}: пределы — положительные (байты — не меньше 0)");
        return b;
    }
}

/// <summary>
/// Бюджет кадра (точка 4д): App играет запись сценария бенчем (<c>Sword.exe --replay … --bench <see cref="Seconds"/></c>:
/// первые 4 с игрового времени — прогрев, vsync выключен, хеши сверяются с записью Headless) — средний кадр и 99-й
/// перцентиль, мс. Как время бюджета — проверяется только в Release; нужен App (виды включены). С точки 5г — оверлеи игры
/// поверх кадра на всё время замера (<see cref="Overlays"/>, <c>Sword.exe --overlay</c>, как у вида): кадр с капсулами тел.
/// </summary>
public sealed record FeatureFrameBudget
{
    public double? AverageMs { get; init; }
    public double? P99Ms { get; init; }

    /// <summary>Секунды игрового времени бенча (больше 4 — прогрев): запись сценария должна быть не короче.</summary>
    public double Seconds { get; init; } = 5;

    /// <summary>Оверлеи игры поверх каждого кадра замера (имя, которого игра не знает, — провал замера); пусто — без них.</summary>
    public IReadOnlyList<string> Overlays { get; init; } = [];

    internal static FeatureFrameBudget Parse(FeatureManifest.Reader r)
    {
        r.Only("averageMs", "p99Ms", "seconds", "overlays");
        var f = new FeatureFrameBudget
        {
            AverageMs = r.OptionalNumber("averageMs"), P99Ms = r.OptionalNumber("p99Ms"), Seconds = r.OptionalNumber("seconds") ?? 5,
            Overlays = FeatureView.ParseOverlays(r),
        };
        if (f.AverageMs is null && f.P99Ms is null) throw new ArgumentException($"{r.At}: нечего проверять — averageMs или p99Ms");
        if (f.AverageMs <= 0 || f.P99Ms <= 0) throw new ArgumentException($"{r.At}: пределы — положительные");
        if (!(f.Seconds > 4 && f.Seconds <= 60)) throw new ArgumentException($"{r.At}.seconds: больше 4 (прогрев бенча) и не больше 60");
        return f;
    }
}
