using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sword.Bake;

/// <summary>
/// Именованные корни исходников — необязательное удобство: путь в манифесте можно записать как <c>${имя}/…</c>,
/// а сам корень задать на каждой машине свой. Значения: поле roots файла проекта, затем локальный файл
/// <see cref="UserFile"/> в корне проекта (вне git), затем переменные <c>BAKE_ROOT_ИМЯ</c>. Без корней пути
/// в манифестах — полные или относительные от корня проекта; <c>${project}</c> — сам корень проекта.
/// </summary>
public sealed partial class SourceRoots
{
    public const string UserFile = "bake.user.json";
    public const string EnvPrefix = "BAKE_ROOT_";
    public const string ProjectRoot = "project";

    public string ProjectDir { get; }
    /// <summary>Корни по имени, полные пути; без <c>${project}</c>.</summary>
    public IReadOnlyDictionary<string, string> Roots => _roots;
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

    private SourceRoots(string projectDir) => ProjectDir = projectDir;

    /// <summary>Без корней: только пути от проекта и полные.</summary>
    public static SourceRoots Empty(string projectDir) => new(Path.GetFullPath(projectDir));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    private sealed class UserShape
    {
        public Dictionary<string, string> Roots { get; set; } = [];
    }

    /// <exception cref="InvalidDataException">Локальный файл не разбирается.</exception>
    public static SourceRoots Load(string projectDir, IReadOnlyDictionary<string, string>? projectRoots)
    {
        var r = new SourceRoots(Path.GetFullPath(projectDir));
        foreach (var (name, value) in projectRoots ?? new Dictionary<string, string>()) r._roots[name] = value;
        string user = Path.Combine(r.ProjectDir, UserFile);
        if (File.Exists(user))
        {
            try
            {
                var shape = JsonSerializer.Deserialize<UserShape>(File.ReadAllText(user), Json) ?? new UserShape();
                foreach (var (name, value) in shape.Roots) r._roots[name] = value;
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"{user}: {e.Message}", e);
            }
        }
        foreach (var name in r._roots.Keys.ToList())
            if (Environment.GetEnvironmentVariable(EnvPrefix + name.ToUpperInvariant()) is { Length: > 0 } env) r._roots[name] = env;
        foreach (var name in r._roots.Keys.ToList())
            r._roots[name] = Path.GetFullPath(Path.Combine(r.ProjectDir, r._roots[name]));
        r._roots.Remove(ProjectRoot);
        return r;
    }

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Variable();

    /// <summary>Имена корней, упомянутые в тексте (манифесте).</summary>
    public static IEnumerable<string> Mentioned(string text) =>
        Variable().Matches(text).Select(m => m.Groups[1].Value).Where(n => !n.Equals(ProjectRoot, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Полный путь: подставлены корни, относительный — от корня проекта.</summary>
    /// <exception cref="ArgumentException">Неизвестный корень.</exception>
    public string Expand(string path)
    {
        string expanded = Variable().Replace(path, m =>
            m.Groups[1].Value.Equals(ProjectRoot, StringComparison.OrdinalIgnoreCase) ? ProjectDir
            : _roots.TryGetValue(m.Groups[1].Value, out var root) ? root
            : throw new ArgumentException($"«{path}»: корень ${{{m.Groups[1].Value}}} не задан — задайте его в {UserFile} (Studio: узел проекта над деревом ассетов) " +
                                          $"или переменной {EnvPrefix}{m.Groups[1].Value.ToUpperInvariant()}"));
        return Path.GetFullPath(Path.Combine(ProjectDir, expanded));
    }

    /// <summary>
    /// Путь для манифеста: под известным корнем — <c>${корень}/…</c> (самый длинный), под проектом — относительный,
    /// иначе полный. Разделитель — «/».
    /// </summary>
    public string Contract(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        foreach (var (name, root) in _roots.OrderByDescending(kv => kv.Value.Length))
            if (Under(fullPath, root)) return $"${{{name}}}/{Path.GetRelativePath(root, fullPath).Replace('\\', '/')}";
        if (Under(fullPath, ProjectDir)) return Path.GetRelativePath(ProjectDir, fullPath).Replace('\\', '/');
        return fullPath.Replace('\\', '/');
    }

    private static bool Under(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Задать корень в локальном файле <see cref="UserFile"/>: остальные поля файла сохраняются.</summary>
    public static void SetUserRoot(string projectDir, string name, string path)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$") || name.Equals(ProjectRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"имя корня «{name}»: только латиница, цифры и _, не «{ProjectRoot}»");
        string file = Path.Combine(projectDir, UserFile);
        var root = File.Exists(file)
            ? JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException($"{file}: не объект JSON")
            : new JsonObject();
        if (root["roots"] is not JsonObject roots) root["roots"] = roots = new JsonObject();
        roots[name] = Path.GetFullPath(path).Replace('\\', '/');
        TextFiles.WriteJson(file, root);
    }
}
