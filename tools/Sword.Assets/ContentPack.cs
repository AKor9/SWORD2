using System.Globalization;
using System.Text.Json;
using Sword.Bake;
using Sword.Engine.Content;

namespace Sword.Assets;

/// <param name="Path">Путь в <c>Content/</c> через «/» — он же путь в паке.</param>
internal sealed record ContentFile(string Path, long Size, DateTime ModifiedUtc);

/// <summary>Итог сборки пака.</summary>
internal sealed record PackResult(string PakPath, int Files, long Bytes, int Excluded, TimeSpan Elapsed);

/// <summary>
/// Игровые ресурсы и пак: файлы <c>Content/</c>, выключенные из пака (<c>Bake/pak.json</c>, по умолчанию в пак идёт всё)
/// и сборка пака. Путь файла в паке = путь в <c>Content/</c>.
/// </summary>
internal sealed class ContentPack
{
    public const string ExclusionsFile = "pak.json";

    private sealed class Shape
    {
        public List<string> Exclude { get; set; } = [];
    }

    private readonly List<string> _exclude = [];

    public string ContentDir { get; }
    public string BakeDir { get; }
    public string PakPath { get; }
    public string ExclusionsPath => Path.Combine(BakeDir, ExclusionsFile);
    /// <summary>Файлы <c>Content/</c> по возрастанию пути без учёта регистра (на момент <see cref="Rescan"/>).</summary>
    public IReadOnlyList<ContentFile> Files { get; private set; } = [];
    /// <summary>Исключения: путь файла или каталог с «/» в конце.</summary>
    public IReadOnlyList<string> Exclude => _exclude;

    public ContentPack(string contentDir, string bakeDir, string pakPath)
    {
        ContentDir = Path.GetFullPath(contentDir);
        BakeDir = Path.GetFullPath(bakeDir);
        PakPath = Path.GetFullPath(pakPath);
        LoadExclusions();
        Rescan();
    }

    /// <exception cref="InvalidDataException">pak.json не разбирается.</exception>
    private void LoadExclusions()
    {
        _exclude.Clear();
        if (!File.Exists(ExclusionsPath)) return;
        try
        {
            var shape = JsonSerializer.Deserialize<Shape>(File.ReadAllText(ExclusionsPath), Recipe.Json) ?? new Shape();
            _exclude.AddRange(shape.Exclude.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{ExclusionsPath}: {e.Message}", e);
        }
    }

    private static string Normalize(string entry) =>
        entry.EndsWith('/') ? ContentPath.Normalize(entry) + "/" : ContentPath.Normalize(entry);

    public void Rescan()
    {
        Files = Directory.Exists(ContentDir)
            ? Directory.GetFiles(ContentDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .Select(f => new ContentFile(Path.GetRelativePath(ContentDir, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    public bool IsIncluded(string path) =>
        !_exclude.Any(e => e.EndsWith('/') ? path.StartsWith(e, StringComparison.OrdinalIgnoreCase) : path.Equals(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Файлы под каталогом (без «/» в конце; пусто — все) или сам файл.</summary>
    public IEnumerable<ContentFile> Under(string path) =>
        path.Length == 0 ? Files
            : Files.Where(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Включить или выключить файл или каталог (все его файлы). Исключение-каталог, который накрывает включаемый файл,
    /// раскладывается на файлы: остальные файлы каталога остаются выключенными. Пишет <c>pak.json</c>.
    /// </summary>
    public void SetIncluded(string path, bool included)
    {
        var files = Under(path).Select(f => f.Path).ToList();
        foreach (var dir in _exclude.Where(e => e.EndsWith('/') && files.Any(f => f.StartsWith(e, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _exclude.Remove(dir);
            _exclude.AddRange(Under(dir.TrimEnd('/')).Select(f => f.Path).Where(f => !_exclude.Contains(f, StringComparer.OrdinalIgnoreCase)));
        }
        _exclude.RemoveAll(e => files.Contains(e, StringComparer.OrdinalIgnoreCase));
        if (!included) _exclude.AddRange(files);
        _exclude.Sort(StringComparer.OrdinalIgnoreCase);
        SaveExclusions();
    }

    private void SaveExclusions()
    {
        if (_exclude.Count == 0 && !File.Exists(ExclusionsPath)) return;
        TextFiles.Write(ExclusionsPath, JsonSerializer.Serialize(new Shape { Exclude = [.. _exclude] }, Recipe.Json));
    }

    /// <summary>
    /// Собрать пак из включённых файлов <c>Content/</c> (по возрастанию пути без учёта регистра). Пишется во временный файл
    /// и подменяет прежний только целиком: ошибка — прежний пак цел.
    /// </summary>
    /// <param name="pakPath">Куда; null — <see cref="PakPath"/>.</param>
    public PackResult Pack(string? pakPath = null, Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Rescan();
        var included = Files.Where(f => IsIncluded(f.Path)).ToList();
        if (included.Count == 0) throw new InvalidDataException($"в паке нечего класть: {ContentDir} пуст или всё выключено в {ExclusionsPath}");
        string target = Path.GetFullPath(pakPath ?? PakPath);
        long bytes = 0;
        using (var writer = new PakWriter(target))
        {
            for (int i = 0; i < included.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                using var source = File.OpenRead(Path.Combine(ContentDir, included[i].Path));
                bytes += writer.Add(included[i].Path, source).Size;
                progress?.Invoke(i + 1, included.Count);
            }
            writer.Commit();
        }
        return new PackResult(target, included.Count, bytes, Files.Count - included.Count, watch.Elapsed);
    }

    /// <summary>
    /// Собран ли пак из текущего <c>Content/</c> и <c>pak.json</c>: те же пути и те же хеши файлов (файлы <c>Content/</c>
    /// читаются целиком). Пусто — да; иначе — расхождения по одному на строку (не больше <paramref name="limit"/>).
    /// </summary>
    /// <exception cref="FileNotFoundException">Пака нет.</exception>
    public List<string> Check(string? pakPath = null, int limit = 20)
    {
        Rescan();
        using var pak = PakSource.Open(Path.GetFullPath(pakPath ?? PakPath));
        var problems = new List<string>();
        var inPak = pak.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var included = Files.Where(f => IsIncluded(f.Path)).ToList();
        foreach (var f in included)
        {
            if (problems.Count >= limit) break;
            if (!inPak.Remove(f.Path, out var entry)) problems.Add($"нет в паке: {f.Path}");
            else
            {
                using var s = File.OpenRead(Path.Combine(ContentDir, f.Path));
                if (entry.Size != f.Size || entry.Hash != PakFormat.HashOf(s)) problems.Add($"другое содержимое: {f.Path}");
            }
        }
        foreach (var path in inPak.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (problems.Count >= limit) break;
            problems.Add($"лишний в паке (выключен или нет в Content/): {path}");
        }
        return problems;
    }

    public static string Describe(PackResult r) =>
        string.Create(CultureInfo.InvariantCulture,
            $"пак → {r.PakPath}: файлов {r.Files}, {r.Bytes / 1048576.0:F1} МБ; выключено {r.Excluded}; {r.Elapsed.TotalSeconds:F1} с");
}
