using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sword.Bake;

/// <summary>
/// Именованные корни сырых исходников машины: путь в рецепте пишется как <c>${имя}/…</c>, сам корень — в локальном файле
/// <see cref="UserFile"/> каталога пекарни (вне git); переменная <c>BAKE_ROOT_ИМЯ</c> перекрывает его. Рецепт сырого
/// файла лежит в <c>recipes/&lt;корень&gt;/&lt;путь от корня&gt;.bake.json</c>, поэтому файл вне корней испечь нельзя.
/// </summary>
public sealed partial class SourceRoots
{
    public const string UserFile = "bake.user.json";
    public const string EnvPrefix = "BAKE_ROOT_";

    /// <summary>Корни по имени, полные пути.</summary>
    public IReadOnlyDictionary<string, string> Roots => _roots;
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

    private sealed class UserShape
    {
        public Dictionary<string, string> Roots { get; set; } = [];
    }

    /// <exception cref="InvalidDataException">Локальный файл не разбирается.</exception>
    public static SourceRoots Load(string bakeDir)
    {
        var r = new SourceRoots();
        string user = Path.Combine(bakeDir, UserFile);
        if (File.Exists(user))
        {
            try
            {
                var shape = JsonSerializer.Deserialize<UserShape>(File.ReadAllText(user), Recipe.Json) ?? new UserShape();
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
            r._roots[name] = Path.GetFullPath(Path.Combine(bakeDir, r._roots[name]));
        return r;
    }

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Variable();

    /// <summary>Полный путь: подставлены корни.</summary>
    /// <exception cref="ArgumentException">Неизвестный корень или путь не полный.</exception>
    public string Expand(string path)
    {
        string expanded = Variable().Replace(path, m =>
            _roots.TryGetValue(m.Groups[1].Value, out var root) ? root
            : throw new ArgumentException($"«{path}»: корень ${{{m.Groups[1].Value}}} не задан — задайте его в {UserFile} " +
                                          $"или переменной {EnvPrefix}{m.Groups[1].Value.ToUpperInvariant()}"));
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException($"«{path}»: нужен полный путь или путь от корня ${{имя}}/…");
        return Path.GetFullPath(expanded);
    }

    /// <summary>Корень и путь от него (через «/»); вне корней — null. Из нескольких подходящих — самый длинный.</summary>
    public (string Root, string Relative)? Split(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        foreach (var (name, root) in _roots.OrderByDescending(kv => kv.Value.Length))
            if (fullPath.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (name, Path.GetRelativePath(root, fullPath).Replace('\\', '/'));
        return null;
    }

    /// <summary>Путь для рецепта: под корнем — <c>${корень}/…</c>, иначе полный через «/».</summary>
    public string Contract(string fullPath) =>
        Split(fullPath) is var (root, rel) ? $"${{{root}}}/{rel}" : Path.GetFullPath(fullPath).Replace('\\', '/');
}
