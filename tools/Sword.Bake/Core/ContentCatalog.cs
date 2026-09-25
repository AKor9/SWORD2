using Sword.Engine.Content;

namespace Sword.Bake;

/// <summary>
/// Ассет контента — то, что видят игра и человек: файлы пака с общей основой имени (<c>weapons/katana.swmesh</c> и
/// <c>.swblood</c> — ассет <c>weapons/katana</c>) или объявленный каталог-набор (<c>anim/katana/</c>). ID — путь в паке без
/// расширения. Производители — единицы сборки (манифесты), которые его пишут; у набора их может быть несколько.
/// </summary>
public sealed record ContentAsset(string Id, bool IsSet, IReadOnlyList<string> Files, IReadOnlyList<string> Producers);

/// <summary>Нарушение раскладки проекта у единицы сборки: выход вне своей папки, общий с другой единицей файл.</summary>
public sealed record LayoutIssue(string AssetId, string Text);

/// <summary>
/// Каталог контента проекта: ассеты контента из выходов, объявленных обработчиками до сборки
/// (<see cref="IAssetProcessor.DeclareOutputs"/>), и выходов из сведений о сборке пака. Отдельного реестра нет —
/// каталог выводится из манифестов и пака. Заодно проверяет раскладку (<see cref="Issues"/>):
/// единица пишет только в папку своего манифеста, и файл пака объявлен ровно у одной единицы.
/// </summary>
public sealed class ContentCatalog
{
    private readonly Dictionary<string, IReadOnlyList<DeclaredOutput>?> _declared = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<LayoutIssue>> _issuesOf = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ContentAsset> Assets { get; private set; } = [];
    public IReadOnlyList<LayoutIssue> Issues { get; private set; } = [];

    /// <summary>Объявленные выходы единицы; null — обработчик не объявляет их до сборки (или параметры не разбираются).</summary>
    public IReadOnlyList<DeclaredOutput>? DeclaredOf(string assetId) => _declared.GetValueOrDefault(assetId);

    public IReadOnlyList<LayoutIssue> IssuesOf(string assetId) => _issuesOf.TryGetValue(assetId, out var list) ? list : [];

    /// <summary>Ассеты контента, которые пишет единица сборки.</summary>
    public IReadOnlyList<ContentAsset> ProducedBy(string assetId) =>
        Assets.Where(c => c.Producers.Contains(assetId, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Ассет контента по ID, по пути файла пака (с расширением или без) или по пути внутри набора; не нашёлся — null.
    /// </summary>
    public ContentAsset? Find(string pathOrId)
    {
        string p;
        try { p = ContentPath.Normalize(pathOrId); }
        catch (ArgumentException) { return null; }
        return Assets.FirstOrDefault(c => c.Id.Equals(p, StringComparison.OrdinalIgnoreCase))
               ?? Assets.FirstOrDefault(c => c.Files.Any(f => f.Equals(p, StringComparison.OrdinalIgnoreCase)
                                                              || StemOf(f).Equals(p, StringComparison.OrdinalIgnoreCase)))
               ?? Assets.FirstOrDefault(c => c.IsSet && p.StartsWith(c.Id + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Путь без расширения последнего сегмента: «weapons/katana.swmesh» → «weapons/katana».</summary>
    public static string StemOf(string path)
    {
        int slash = path.LastIndexOf('/'), dot = path.LastIndexOf('.');
        return dot > slash + 1 ? path[..dot] : path;
    }

    /// <summary>Папка манифеста — ID без последнего сегмента; у манифеста в корне — пусто.</summary>
    public static string FolderOf(string assetId) => assetId.Contains('/') ? assetId[..assetId.LastIndexOf('/')] : "";

    /// <param name="info">Сведения о сборке текущего пака; null — пака нет, каталог только по объявлениям.</param>
    public static ContentCatalog Build(BakeProject project, PakBuildInfo? info)
    {
        var c = new ContentCatalog();
        var issues = new List<LayoutIssue>();
        foreach (var a in project.Assets) c._declared[a.Id] = Declare(project, a);

        // ── раскладка ──
        foreach (var (id, declared) in c._declared)
        {
            if (declared is null) continue;
            string folder = FolderOf(id);
            foreach (var d in declared)
                if (folder.Length > 0 && !d.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new LayoutIssue(id, $"выход {d} вне папки своего манифеста {folder}/"));
        }
        var files = c._declared
            .Where(kv => kv.Value is not null)
            .SelectMany(kv => kv.Value!.Where(d => !d.IsDirectory).Select(d => (Path: d.Path, Owner: kv.Key)))
            .ToList();
        foreach (var group in files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var (path, owner) in group)
                issues.Add(new LayoutIssue(owner, $"файл {path} объявлен и у {string.Join(", ", group.Select(g => g.Owner).Where(o => o != owner))}"));
        var dirs = c._declared
            .Where(kv => kv.Value is not null)
            .SelectMany(kv => kv.Value!.Where(d => d.IsDirectory).Select(d => (Dir: d, Owner: kv.Key)))
            .ToList();
        foreach (var (path, owner) in files)
            foreach (var (dir, dirOwner) in dirs)
                if (dirOwner != owner && dir.Covers(path))
                    issues.Add(new LayoutIssue(owner, $"файл {path} лежит в наборе {dir} единицы {dirOwner}"));

        // ── ассеты контента: наборы по объявленным каталогам, остальное — по основе имени ──
        var byId = new Dictionary<string, (bool IsSet, SortedSet<string> Files, SortedSet<string> Producers)>(StringComparer.OrdinalIgnoreCase);
        (bool IsSet, SortedSet<string> Files, SortedSet<string> Producers) Entry(string id, bool isSet)
        {
            if (!byId.TryGetValue(id, out var e))
                byId[id] = e = (isSet, new SortedSet<string>(StringComparer.OrdinalIgnoreCase), new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            return e;
        }
        foreach (var (dir, owner) in dirs) Entry(dir.Path, true).Producers.Add(owner);
        void AddFile(string path, string owner)
        {
            string id = dirs.FirstOrDefault(d => d.Dir.Covers(path)).Dir.Path ?? StemOf(path);
            var e = Entry(id, byId.TryGetValue(id, out var known) && known.IsSet);
            e.Files.Add(path);
            e.Producers.Add(owner);
        }
        foreach (var (path, owner) in files) AddFile(path, owner);
        if (info is not null)
            foreach (var (id, rec) in info.Assets)
                if (project.Find(id) is not null)
                    foreach (var o in rec.Outputs) AddFile(o.Path, id);

        c.Assets = byId.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ContentAsset(kv.Key, kv.Value.IsSet, [.. kv.Value.Files], [.. kv.Value.Producers]))
            .ToList();
        c.Issues = issues;
        foreach (var issue in issues)
        {
            if (!c._issuesOf.TryGetValue(issue.AssetId, out var list)) c._issuesOf[issue.AssetId] = list = [];
            list.Add(issue);
        }
        return c;
    }

    private static IReadOnlyList<DeclaredOutput>? Declare(BakeProject project, AssetManifest a)
    {
        if (project.Processors.Find(a.Processor) is not { } proc) return null;
        try { return proc.DeclareOutputs(a, a.ReadParams(proc)); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { return null; }
    }
}
