using Sword.Engine.Content;

namespace Sword.Bake;

/// <summary>Файл пака в сравнении двух паков.</summary>
public enum FileDiffKind
{
    /// <summary>Тот же размер и хеш оглавления.</summary>
    Same,
    /// <summary>Хеши разные, сравнитель формата: равны по содержимому. Не расхождение.</summary>
    SameContent,
    OnlyA,
    OnlyB,
    /// <summary>Другой размер (и по содержимому не равны).</summary>
    Size,
    /// <summary>Тот же размер, другой хеш (и по содержимому не равны).</summary>
    Hash,
}

/// <summary>
/// Файл в сравнении: путь, записи оглавления A и B (нет файла — null), сравнитель (null — не было) и пояснение
/// (чем отличаются или что сравнитель не сравнивал); <see cref="Owner"/> — ассет, который его записал (по сведениям о сборке
/// B, иначе A).
/// </summary>
public sealed record FileDiff(string Path, FileDiffKind Kind, PakEntry? A, PakEntry? B, string? Comparer, string Detail, string? Owner)
{
    /// <summary>Расхождение: только в одном паке или по содержимому не равны.</summary>
    public bool IsDiscrepancy => Kind is FileDiffKind.OnlyA or FileDiffKind.OnlyB or FileDiffKind.Size or FileDiffKind.Hash;

    /// <summary>Вид для правил ожидаемых расхождений.</summary>
    public ExpectKind ExpectKind => Kind is FileDiffKind.OnlyA or FileDiffKind.OnlyB ? ExpectKind.Absent : ExpectKind.Different;

    /// <summary>Правило, по которому расхождение ожидаемо; null — неожиданное (или не расхождение).</summary>
    public ExpectationRule? Expected { get; init; }
}

/// <summary>
/// Расхождение сведений о сборке (<c>.bake/build.json</c>) у ассета: поле (<see cref="PakDiff.Fields"/>), путь (у входов,
/// каталогов-входов, входов из контента и выходов), значения A и B (нет — null), пояснение и пометка.
/// </summary>
public sealed record BuildInfoDiff(string Asset, string Field, string? Path, string? A, string? B, string Detail, string? Note = null)
{
    /// <summary>
    /// Вид для правил: ассет только в одном паке — <see cref="ExpectKind.Absent"/>; остальное — <see cref="ExpectKind.Different"/>
    /// у правил ассета. Выход только в одном паке для правил пути — <see cref="ExpectKind.Absent"/> (как файл).
    /// </summary>
    public ExpectKind ExpectKind => Field == PakDiff.Fields.Asset ? ExpectKind.Absent : ExpectKind.Different;

    public ExpectationRule? Expected { get; init; }
}

/// <summary>
/// Сведения о сборке в сравнении: есть ли файл в паках, сколько ассетов (не разобраны — 0), расхождения (сравниваются, только
/// если файл есть в обоих; его нет в одном — это расхождение файла <see cref="PakBuildInfo.EntryPath"/>).
/// </summary>
public sealed record BuildInfoReport(bool InA, bool InB, int AssetsA, int AssetsB, IReadOnlyList<BuildInfoDiff> Differences)
{
    public bool Compared => InA && InB;
    public bool Equal => Compared && Differences.Count == 0;
}

/// <summary>Пак в сравнении: путь, файлов, байт, отпечаток содержимого; проект и время сборки — из сведений о сборке (нет — null).</summary>
public sealed record PakSide(string Path, int Files, long Bytes, string ContentHash, string? Project, DateTime? BuiltUtc);

/// <summary>Сравнитель, которым шло сравнение: название, расширения и сборка (плагин).</summary>
public sealed record ComparerInfo(string Title, IReadOnlyList<string> Extensions, string Assembly);

public sealed class PakDiffReport
{
    public required PakSide A { get; init; }
    public required PakSide B { get; init; }
    public required IReadOnlyList<ComparerInfo> Comparers { get; init; }
    /// <summary>Все файлы обоих паков по пути, кроме <c>.bake/build.json</c>, если он есть в обоих (он — <see cref="BuildInfo"/>).</summary>
    public required IReadOnlyList<FileDiff> Files { get; init; }
    public required BuildInfoReport BuildInfo { get; init; }
    /// <summary>Файл ожидаемых расхождений; без него — null.</summary>
    public DiffExpectations? Expectations { get; init; }
    /// <summary>Правила, не подошедшие ни к одному расхождению: список устарел.</summary>
    public required IReadOnlyList<ExpectationRule> UnusedRules { get; init; }

    public int Count(FileDiffKind kind) => Files.Count(f => f.Kind == kind);

    /// <summary>Неожиданные расхождения: файлы и сведения о сборке.</summary>
    public int Unexpected => Files.Count(f => f.IsDiscrepancy && f.Expected is null) + BuildInfo.Differences.Count(d => d.Expected is null);

    public int Expected => Files.Count(f => f.IsDiscrepancy && f.Expected is not null) + BuildInfo.Differences.Count(d => d.Expected is not null);

    /// <summary>Неожиданных расхождений нет — код выхода 0.</summary>
    public bool Ok => Unexpected == 0;
}

/// <summary>
/// Сравнение двух паков (<c>sword-bake diff</c>, раздел 11 плана): по оглавлению без распаковки — только в A, только в B,
/// другой размер, другой хеш, одинаковые. Файлы с разными хешами — сравнителем формата по расширению
/// (<see cref="IContentComparer"/>, из плагинов проекта); нет сравнителя — «отличаются байтами». Сведения о сборке
/// (<see cref="PakBuildInfo.EntryPath"/>) — семантически (<see cref="CompareBuildInfo"/>). Расхождения делятся на ожидаемые
/// (по <see cref="DiffExpectations"/>) и неожиданные. Пак не меняется; читаются только файлы с разными хешами.
/// </summary>
public static class PakDiff
{
    /// <summary>Поля записи ассета в итоге сравнения сведений о сборке.</summary>
    public static class Fields
    {
        public const string Asset = "asset";
        public const string Processor = "processor";
        public const string Fingerprint = "fingerprint";
        public const string ParamsHash = "paramsHash";
        public const string Inputs = "inputs";
        public const string InputDirs = "inputDirs";
        public const string ContentInputs = "contentInputs";
        public const string Outputs = "outputs";
        public const string Version = "version";
    }

    /// <summary>Название сравнения сведений о сборке в итоге.</summary>
    public const string BuildInfoTitle = "сведения о сборке: семантически, без времени и сборок в отпечатке";

    /// <summary>Пометка у <c>paramsHash</c>: он зависит от корней исходников, а не только от параметров.</summary>
    public const string ParamsHashNote = "хеш параметров включает корни исходников (bake.user.json, BAKE_ROOT_*): при других корнях отличается и без правки параметров";

    /// <exception cref="InvalidDataException">Пак не открывается или сведения о сборке не разбираются.</exception>
    /// <exception cref="ArgumentException">Два сравнителя на одно расширение.</exception>
    public static PakDiffReport Compare(string pakA, string pakB, IEnumerable<IContentComparer> comparers, DiffExpectations? expectations = null)
    {
        var list = comparers.ToList();
        var byExt = new Dictionary<string, IContentComparer>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in list)
            foreach (var ext in c.Extensions)
                if (!byExt.TryAdd(ext, c)) throw new ArgumentException($"два сравнителя для {ext}: {byExt[ext].Title} и {c.Title}");
        var rules = expectations ?? DiffExpectations.None;
        var used = new HashSet<ExpectationRule>();

        using var a = PakSource.Open(pakA);
        using var b = PakSource.Open(pakB);
        bool hasA = a.Exists(PakBuildInfo.EntryPath), hasB = b.Exists(PakBuildInfo.EntryPath), buildInfoInBoth = hasA && hasB;
        var infoA = hasA ? PakBuildInfo.Read(a) : null;
        var infoB = hasB ? PakBuildInfo.Read(b) : null;
        var owners = Owners(infoA);
        foreach (var (path, owner) in Owners(infoB)) owners[path] = owner;   // владелец по B, иначе по A

        var files = new List<FileDiff>();
        var paths = a.Entries.Select(e => e.Path).Union(b.Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (buildInfoInBoth && path.Equals(PakBuildInfo.EntryPath, StringComparison.OrdinalIgnoreCase)) continue;
            var diff = CompareFile(path, a, b, byExt, owners.GetValueOrDefault(path));
            if (diff.IsDiscrepancy && rules.ForPath(path, diff.ExpectKind) is { } rule)
            {
                used.Add(rule);
                diff = diff with { Expected = rule };
            }
            files.Add(diff);
        }

        var differences = new List<BuildInfoDiff>();
        if (buildInfoInBoth)
        {
            if (infoA is null || infoB is null)
                differences.Add(new BuildInfoDiff("", Fields.Version, null, Version(infoA), Version(infoB),
                    $"сведения о сборке не версии {PakBuildInfo.CurrentVersion} — семантически не сравнивались"));
            else if (a.Find(PakBuildInfo.EntryPath)!.Value.Hash != b.Find(PakBuildInfo.EntryPath)!.Value.Hash)
                differences.AddRange(CompareBuildInfo(infoA, infoB, files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase)));
        }
        var buildInfo = new BuildInfoReport(hasA, hasB, infoA?.Assets.Count ?? 0, infoB?.Assets.Count ?? 0,
            [.. differences.Select(d => Expect(d, rules, used))]);

        return new PakDiffReport
        {
            A = Side(a, infoA),
            B = Side(b, infoB),
            Comparers = [.. list.Select(c => new ComparerInfo(c.Title, c.Extensions, c.GetType().Assembly.GetName().Name ?? ""))],
            Files = files,
            BuildInfo = buildInfo,
            Expectations = expectations,
            UnusedRules = [.. rules.Rules.Where(r => !used.Contains(r))],
        };
    }

    private static string Version(PakBuildInfo? info) =>
        info is null ? "другая" : PakBuildInfo.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static PakSide Side(PakSource pak, PakBuildInfo? info) =>
        new(pak.Location, pak.Entries.Count, pak.Length, pak.ContentHash, info?.Project, info?.BuiltUtc);

    private static Dictionary<string, string> Owners(PakBuildInfo? info)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (info is null) return owners;
        foreach (var (id, rec) in info.Assets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var o in rec.Outputs)
                owners.TryAdd(o.Path, id);
        return owners;
    }

    /// <summary>Один файл: по оглавлению, при разных хешах — сравнителем формата.</summary>
    private static FileDiff CompareFile(string path, PakSource a, PakSource b, Dictionary<string, IContentComparer> comparers, string? owner)
    {
        var ea = a.Find(path);
        var eb = b.Find(path);
        if (ea is null) return new FileDiff(path, FileDiffKind.OnlyB, null, eb, null, "только в B", owner);
        if (eb is null) return new FileDiff(path, FileDiffKind.OnlyA, ea, null, null, "только в A", owner);
        var (x, y) = (ea.Value, eb.Value);
        if (x.Size == y.Size && x.Hash == y.Hash) return new FileDiff(path, FileDiffKind.Same, x, y, null, "", owner);

        var kind = x.Size != y.Size ? FileDiffKind.Size : FileDiffKind.Hash;
        string sizes = x.Size != y.Size ? $"размер {x.Size} ≠ {y.Size}; " : "";
        if (!comparers.TryGetValue(System.IO.Path.GetExtension(path), out var comparer))
            return new FileDiff(path, kind, x, y, null, sizes + "отличаются байтами", owner);
        ContentComparison verdict;
        try
        {
            verdict = comparer.Compare(path, a.ReadAllBytes(path), b.ReadAllBytes(path));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // плагин — чужой код: его сбой — расхождение с текстом ошибки, а не отказ всего сравнения
            return new FileDiff(path, kind, x, y, comparer.Title, $"{sizes}сравнитель не разобрал: {e.GetType().Name}: {e.Message}", owner);
        }
        return verdict.Equal
            ? new FileDiff(path, FileDiffKind.SameContent, x, y, comparer.Title, verdict.Detail, owner)
            : new FileDiff(path, kind, x, y, comparer.Title, sizes + (verdict.Detail.Length > 0 ? verdict.Detail : "по содержимому не равны"), owner);
    }

    private static BuildInfoDiff Expect(BuildInfoDiff d, DiffExpectations rules, HashSet<ExpectationRule> used)
    {
        var rule = rules.ForAsset(d.Asset, d.ExpectKind);
        if (rule is null && d is { Field: Fields.Outputs, Path: { } path })
            rule = rules.ForPath(path, d.A is null || d.B is null ? ExpectKind.Absent : ExpectKind.Different);
        if (rule is null) return d;
        used.Add(rule);
        return d with { Expected = rule };
    }

    /// <summary>
    /// Семантика сведений о сборке (разбор пекарни, таблица 3.3): набор ассетов и обработчики; отпечаток — только префикс
    /// <c>ID|версия</c> (сборки и их MVID другие по устройству); <c>paramsHash</c> — с пометкой о корнях; входы — путь,
    /// размер и хеш без времени; каталоги-входы и входы из контента — наборы; выходы — путь, размер и хеш, а при разных хешах
    /// — итог сравнения файла (<paramref name="files"/>): равны по содержимому — не расхождение. Время (<c>builtUtc</c>,
    /// <c>bakedUtc</c>) и имя проекта не сравниваются.
    /// </summary>
    public static IReadOnlyList<BuildInfoDiff> CompareBuildInfo(PakBuildInfo a, PakBuildInfo b, IReadOnlyDictionary<string, FileDiff> files)
    {
        var result = new List<BuildInfoDiff>();
        foreach (var id in a.Assets.Keys.Union(b.Assets.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var ra = a.Assets.GetValueOrDefault(id);
            var rb = b.Assets.GetValueOrDefault(id);
            if (ra is null || rb is null)
            {
                result.Add(new BuildInfoDiff(id, Fields.Asset, null, ra?.Processor, rb?.Processor, ra is null ? "ассет только в B" : "ассет только в A"));
                continue;
            }
            if (ra.Processor != rb.Processor)
                result.Add(new BuildInfoDiff(id, Fields.Processor, null, ra.Processor, rb.Processor, "другой обработчик"));
            string fa = FingerprintPrefix(ra.Fingerprint), fb = FingerprintPrefix(rb.Fingerprint);
            if (fa != fb)
                result.Add(new BuildInfoDiff(id, Fields.Fingerprint, null, fa, fb, "другой обработчик или его версия (ID|версия отпечатка)"));
            if (!string.Equals(ra.ParamsHash, rb.ParamsHash, StringComparison.OrdinalIgnoreCase))
                result.Add(new BuildInfoDiff(id, Fields.ParamsHash, null, ra.ParamsHash, rb.ParamsHash, "другой хеш параметров", ParamsHashNote));
            CompareFiles(result, id, Fields.Inputs, ra.Inputs, rb.Inputs, "вход", null);
            CompareSets(result, id, Fields.InputDirs, ra.InputDirs, rb.InputDirs, "каталог-вход");
            CompareSets(result, id, Fields.ContentInputs, ra.ContentInputs, rb.ContentInputs, "вход из контента");
            CompareFiles(result, id, Fields.Outputs, ra.Outputs, rb.Outputs, "выход", files);
        }
        return result;
    }

    /// <summary>«anim.pack|1|Sword.Anim:…,Sword.Bake.Sword:…» → «anim.pack|1».</summary>
    public static string FingerprintPrefix(string fingerprint)
    {
        int first = fingerprint.IndexOf('|');
        if (first < 0) return fingerprint;
        int second = fingerprint.IndexOf('|', first + 1);
        return second < 0 ? fingerprint : fingerprint[..second];
    }

    private static void CompareSets(List<BuildInfoDiff> result, string id, string field, List<string> a, List<string> b, string what)
    {
        var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        foreach (var p in a.Where(p => !sb.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            result.Add(new BuildInfoDiff(id, field, p, p, null, $"{what} только в A"));
        foreach (var p in b.Where(p => !sa.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            result.Add(new BuildInfoDiff(id, field, p, null, p, $"{what} только в B"));
    }

    /// <param name="files">Итог сравнения файлов пака — у выходов; у входов null (размер и хеш — как записаны, время не сравнивается).</param>
    private static void CompareFiles(List<BuildInfoDiff> result, string id, string field, List<PakFileRecord> a, List<PakFileRecord> b, string what,
        IReadOnlyDictionary<string, FileDiff>? files)
    {
        var ma = ByPath(a);
        var mb = ByPath(b);
        foreach (var path in ma.Keys.Union(mb.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var x = ma.GetValueOrDefault(path);
            var y = mb.GetValueOrDefault(path);
            if (x is null || y is null)
            {
                result.Add(new BuildInfoDiff(id, field, path, Text(x), Text(y), x is null ? $"{what} только в B" : $"{what} только в A"));
                continue;
            }
            if (x.Size == y.Size && string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            string detail = x.Size != y.Size ? $"{what}: размер {x.Size} ≠ {y.Size}" : $"{what}: другой хеш";
            if (files is not null)
            {
                // выход — файл пака: хеш записи тот же, что в оглавлении; равны по содержимому — не расхождение
                if (files.GetValueOrDefault(path) is not { } file) detail += "; файла нет в оглавлении";
                else if (file.Kind == FileDiffKind.SameContent && Same(file.A, x) && Same(file.B, y)) continue;
                else if (file.IsDiscrepancy) detail += "; " + file.Detail;
                else detail += "; запись не совпадает с оглавлением";
            }
            result.Add(new BuildInfoDiff(id, field, path, Text(x), Text(y), detail));
        }
    }

    private static bool Same(PakEntry? entry, PakFileRecord record) =>
        entry is { } e && e.Size == record.Size && string.Equals(PakFormat.HashText(e.Hash), record.Hash, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, PakFileRecord> ByPath(List<PakFileRecord> files)
    {
        var map = new Dictionary<string, PakFileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) map.TryAdd(f.Path, f);
        return map;
    }

    private static string? Text(PakFileRecord? f) => f is null ? null : $"{f.Size} {f.Hash}";
}
