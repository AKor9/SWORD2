using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Sword.Engine.Runtime;

/// <summary>
/// Конфигурация запуска (раздел 7 плана). Слои, каждый перекрывает предыдущий: значения в коде → <c>sword.json</c>
/// (в репозитории) → <c>sword.user.json</c> (вне git) → переменные <c>SWORD_&lt;СЕКЦИЯ&gt;_&lt;КЛЮЧ&gt;</c> → ключи
/// <c>--set секция.ключ=значение</c>. Секции — типизированные записи (<see cref="Section{T}"/>); вложенные объекты
/// сливаются по слоям ключ за ключом (<c>--set секция.объект.ключ=…</c>, <c>SWORD_СЕКЦИЯ_ОБЪЕКТ_КЛЮЧ</c>). Подсистемы
/// конфиг не читают — получают готовые объекты при композиции. Секция групп (<see cref="Groups{T}"/>:
/// <c>modules.&lt;id&gt;</c>) — те же записи на уровень глубже. Строгость: ключ — только записываемое свойство записи,
/// неизвестный ключ и группа — ошибки, регистр имён не важен ни в одном слое, секция, которую до конца композиции никто не
/// запросил (<see cref="Complete"/>), — ошибка. После <see cref="Complete"/> конфиг не меняется и не читается заново.
/// </summary>
public sealed class ConfigSet
{
    public const string FileName = "sword.json", UserFileName = "sword.user.json", EnvPrefix = "SWORD_";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Лишний ключ в объекте, у которого нет значения в коде (null), ловит уже десериализация.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonDocumentOptions FileOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false,
    };

    private readonly JsonObject _files = [];
    private readonly JsonObject _sets = [];
    private readonly Func<string, string?> _env;
    private readonly JsonObject _effective = [];
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sources = [];
    private bool _completed;

    private ConfigSet(Func<string, string?> env) => _env = env;

    /// <summary>Откуда пришли значения (файлы, переменные, ключи) — для итога запуска и агента; без повторов.</summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>Композиция закончена: конфиг проверен целиком и больше не меняется (<see cref="Complete"/>).</summary>
    public bool Completed => _completed;

    /// <summary>
    /// Конфиг: файлы из <paramref name="directory"/> или ближайшего каталога выше, где есть <see cref="FileName"/>;
    /// переменные окружения (<paramref name="env"/>, по умолчанию — процесса); ключи <c>--set</c>.
    /// </summary>
    public static ConfigSet Load(string? directory, IEnumerable<string> sets, Func<string, string?>? env = null)
    {
        var c = new ConfigSet(env ?? Environment.GetEnvironmentVariable);
        string? dir = directory is null ? null : FindDirectory(directory);
        if (dir is not null)
            foreach (var name in (ReadOnlySpan<string>)[FileName, UserFileName])
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path), documentOptions: FileOptions) is not JsonObject obj)
                        throw new InvalidDataException("ожидается объект секций");
                    CheckDuplicates(obj, "");
                    Merge(c._files, obj);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
                {
                    throw new InvalidDataException($"{path}: {e.Message}", e);
                }
                c.AddSource(path);
            }
        foreach (var set in sets) c.ApplySet(set);
        return c;
    }

    /// <summary>Только значения в коде (тесты); <paramref name="sets"/> — ключи <c>--set</c>.</summary>
    public static ConfigSet Empty(params string[] sets)
    {
        var c = new ConfigSet(_ => null);
        foreach (var s in sets) c.ApplySet(s);
        return c;
    }

    private static string? FindDirectory(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, FileName))) return d.FullName;
        return null;
    }

    /// <summary>
    /// <c>секция.ключ=значение</c> (вложенно — <c>modules.cutting.enabled=false</c>); значение — литерал JSON или строка,
    /// <c>True</c>/<c>False</c> в любом регистре — логическое. Тот же ключ в другом регистре — тот же ключ: действует
    /// последний.
    /// </summary>
    public void ApplySet(string set)
    {
        NotCompleted();
        int eq = set.IndexOf('=');
        string[] path = eq < 0 ? [] : set[..eq].Split('.');
        if (path.Length < 2 || path.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"--set {set}: ожидается секция.ключ=значение");
        var node = _sets;
        for (int i = 0; i < path.Length - 1; i++)
        {
            var key = FindKey(node, path[i]);
            if (key is null)
            {
                JsonObject created = [];
                node[path[i]] = created;
                node = created;
            }
            else if (node[key] is JsonObject next) node = next;
            else throw new ArgumentException($"--set {set}: {string.Join('.', path[..(i + 1)])} уже задан значением, а не объектом");
        }
        node[FindKey(node, path[^1]) ?? path[^1]] = Literal(set[(eq + 1)..]);
        AddSource($"--set {set}");
    }

    private static JsonNode? Literal(string text)
    {
        if (bool.TryParse(text, out bool b)) return JsonValue.Create(b);
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>Секция <paramref name="name"/> как <typeparamref name="T"/> — все слои по порядку.</summary>
    public T Section<T>(string name) where T : new()
    {
        NotCompleted();
        var result = Resolve<T>([name]);
        _effective[name] = JsonSerializer.SerializeToNode(result, Json);
        _consumed.Add(name);
        return result;
    }

    /// <summary>
    /// Секция групп: <c>секция.&lt;группа&gt;.ключ</c> (например, <c>modules.cutting.enabled</c>). Каждая группа из
    /// <paramref name="groups"/> — запись <typeparamref name="T"/> по тем же слоям и с той же строгостью, что
    /// <see cref="Section{T}"/>; переменная — <c>SWORD_&lt;СЕКЦИЯ&gt;_&lt;ГРУППА&gt;_&lt;КЛЮЧ&gt;</c>. Группа не из списка — ошибка.
    /// </summary>
    public IReadOnlyDictionary<string, T> Groups<T>(string name, IReadOnlyCollection<string> groups) where T : new()
    {
        NotCompleted();
        foreach (var layer in (ReadOnlySpan<JsonObject>)[_files, _sets])
            if (FindPath(layer, [name], name) is { } section)
                foreach (var (group, _) in section)
                    if (!groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException($"конфиг: {name}.{group} — такой группы нет; есть: {string.Join(", ", groups)}");

        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        var effective = new JsonObject();
        foreach (var group in groups)
        {
            var value = Resolve<T>([name, group]);
            result[group] = value;
            effective[group] = JsonSerializer.SerializeToNode(value, Json);
        }
        _effective[name] = effective;
        _consumed.Add(name);
        return result;
    }

    /// <summary>Запись по пути: значения в коде → файлы → переменные → <c>--set</c>; неизвестный ключ — ошибка.</summary>
    private T Resolve<T>(string[] path) where T : new()
    {
        string where = string.Join('.', path);
        var info = Json.GetTypeInfo(typeof(T));
        var node = (JsonObject)JsonSerializer.SerializeToNode(new T(), Json)!;
        Overlay(node, info, FindPath(_files, path, where), where);
        ApplyEnv(node, info, EnvPrefix + string.Join('_', path).ToUpperInvariant());
        Overlay(node, info, FindPath(_sets, path, where), where);
        try
        {
            return node.Deserialize<T>(Json)!;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"конфиг: {where}: {e.Message}", e);
        }
    }

    /// <summary>Ключи записи — только записываемые свойства: вычисляемое свойство ключом конфига не бывает.</summary>
    private static IEnumerable<JsonPropertyInfo> Keys(JsonTypeInfo info) => info.Properties.Where(p => p.Set is not null);

    private static bool IsRecord(Type type) => Json.GetTypeInfo(type).Kind == JsonTypeInfoKind.Object;

    /// <summary>Слой поверх значений: вложенный объект — ключ за ключом (значения ниже по слоям не теряются).</summary>
    private static void Overlay(JsonObject node, JsonTypeInfo info, JsonObject? layer, string where)
    {
        if (layer is null) return;
        foreach (var (key, value) in layer)
        {
            var prop = Keys(info).FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    $"конфиг: в секции {where} нет ключа {key}; есть: {string.Join(", ", Keys(info).Select(p => p.Name).Order())}");
            if (value is JsonObject obj && IsRecord(prop.PropertyType) && node[prop.Name] is JsonObject current)
                Overlay(current, Json.GetTypeInfo(prop.PropertyType), obj, $"{where}.{prop.Name}");
            else node[prop.Name] = value?.DeepClone();
        }
    }

    /// <summary>Переменные окружения по известным ключам, вложенные — через <c>_</c>.</summary>
    private void ApplyEnv(JsonObject node, JsonTypeInfo info, string prefix)
    {
        foreach (var prop in Keys(info))
        {
            string name = prefix + "_" + prop.Name.ToUpperInvariant();
            if (IsRecord(prop.PropertyType) && node[prop.Name] is JsonObject current)
                ApplyEnv(current, Json.GetTypeInfo(prop.PropertyType), name);
            else if (_env(name) is { } value)
            {
                node[prop.Name] = Literal(value);
                AddSource($"{name}={value}");
            }
        }
    }

    /// <summary>Объект по пути в слое; не объект на пути (число, строка, null) — ошибка: <c>"sim": 5</c> не проходит молча.</summary>
    private static JsonObject? FindPath(JsonObject layer, string[] path, string where)
    {
        var node = layer;
        foreach (var part in path)
        {
            var key = FindKey(node, part);
            if (key is null) return null;
            node = node[key] as JsonObject
                ?? throw new InvalidDataException($"конфиг: {where}: ожидается объект ключей, а не {node[key]?.ToJsonString() ?? "null"}");
        }
        return node;
    }

    /// <summary>
    /// Секция выключенного модуля: не читается и не проверяется, но и неизвестной не считается — иначе выключение модуля
    /// с настройками роняло бы запуск (решение 9). В итоговый конфиг не входит.
    /// </summary>
    public void Skip(string name)
    {
        NotCompleted();
        _consumed.Add(name);
        _skipped.Add(name);
    }

    /// <summary>Пропущенные секции (выключенные модули) — для итога запуска.</summary>
    public IReadOnlyCollection<string> Skipped => _skipped;

    /// <summary>Секции файлов и <c>--set</c>, которые никто не запросил, — ошибка.</summary>
    public void ThrowOnUnknown()
    {
        var unknown = _files.Concat(_sets).Select(p => p.Key).Where(k => !_consumed.Contains(k))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"конфиг: неизвестные секции {string.Join(", ", unknown)}; есть: {string.Join(", ", _consumed.Order())}");
    }

    /// <summary>
    /// Конец композиции (exe — после симуляции и клиента: секции читают обе стороны): секция, которую никто не
    /// запросил, — ошибка; дальше конфиг не меняется (<see cref="ApplySet"/>) и не читается (<see cref="Section{T}"/>).
    /// </summary>
    public void Complete()
    {
        ThrowOnUnknown();
        _completed = true;
    }

    /// <summary>Итоговый конфиг после всех слоёв — по прочитанным секциям (в итог запуска, позже в реплей).</summary>
    public JsonObject Effective => (JsonObject)_effective.DeepClone();

    private void NotCompleted()
    {
        if (_completed) throw new InvalidOperationException("конфиг уже собран (Complete): после композиции он не меняется и не читается");
    }

    private void AddSource(string source)
    {
        if (!_sources.Contains(source)) _sources.Add(source);
    }

    /// <summary>Имя ключа объекта без учёта регистра (как он записан в объекте) или null.</summary>
    private static string? FindKey(JsonObject obj, string key)
    {
        foreach (var (k, _) in obj)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    /// <summary>Слияние слоя файлов: тот же ключ в другом регистре — тот же ключ; объекты — вглубь, прочее — замена.</summary>
    private static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (key, value) in from)
        {
            var existing = FindKey(into, key);
            if (existing is not null && value is JsonObject obj && into[existing] is JsonObject current) Merge(current, obj);
            else into[existing ?? key] = value?.DeepClone();
        }
    }

    /// <summary>Один ключ дважды в одном файле (в любом регистре) — ошибка: какое значение имелось в виду, не понять.</summary>
    private static void CheckDuplicates(JsonObject obj, string where)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in obj)
        {
            string at = where.Length == 0 ? key : $"{where}.{key}";
            if (!seen.Add(key)) throw new InvalidDataException($"ключ {at} встречается дважды");
            if (value is JsonObject child) CheckDuplicates(child, at);
        }
    }
}
