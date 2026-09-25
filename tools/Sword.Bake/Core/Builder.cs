using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sword.Engine.Content;

namespace Sword.Bake;

public enum AssetStatus
{
    UpToDate,
    /// <summary>Нужно собрать: нет в паке, изменились исходники, параметры, код обработчика или зависимость.</summary>
    Dirty,
    /// <summary>Нет исходника, который был входом сборки пака.</summary>
    NoSource,
    /// <summary>Манифест не разбирается, неизвестный обработчик или последняя сборка упала.</summary>
    Error,
}

public readonly record struct AssetCheck(AssetStatus Status, string Reason);

public enum PakStatus
{
    UpToDate,
    /// <summary>Пак есть, но собран не из текущих исходников, настроек или кода обработчиков.</summary>
    Stale,
    Missing,
    /// <summary>Пак не открывается.</summary>
    Error,
}

public sealed record PakCheck(PakStatus Status, string Reason, IReadOnlyList<string> Details);

public enum BakeEventKind { Queued, Started, Log, Warning, UpToDate, Finished, Failed, Skipped, Cancelled }

public sealed record BakeEvent(BakeEventKind Kind, string AssetId, string Text, TimeSpan Elapsed = default);

public sealed class BuildOptions
{
    /// <summary>Запекать и актуальные ассеты.</summary>
    public bool Force { get; init; }
    public int Jobs { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
}

public sealed class BuildSummary
{
    public int Built, UpToDate, Failed, Skipped;
    public bool Cancelled;
    public TimeSpan Elapsed;
    /// <summary>Пак соответствует сборке: новый записан и заменил прежний или прежний уже был таким (<see cref="PakUnchanged"/>).</summary>
    public bool PakWritten;
    /// <summary>Ничего не пересобрано, прежний пак оставлен как есть.</summary>
    public bool PakUnchanged;
    public int PakFiles;
    public long PakBytes;
    public bool Ok => Failed == 0 && Skipped == 0 && !Cancelled && PakWritten;
}

/// <summary>Файл пака и ассет, который его записал (null — не известен).</summary>
public sealed record PakContentEntry(string Path, long Size, string Hash, string? Owner);

public sealed class PakVerifyReport
{
    public int Files;
    public long Bytes;
    public readonly List<string> Problems = [];
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Сборка пака проекта. Проверка актуальности — по сведениям о сборке внутри текущего пака (<see cref="PakBuildInfo"/>):
/// ассет актуален, если его исходники, параметры, код обработчика и зависимости те же, из которых собран пак.
/// Сборка запекает устаревшие ассеты во временный каталог, актуальные берёт из прежнего пака и пишет новый пак;
/// прежний заменяется только после успешной записи, а при любой ошибке остаётся нетронутым.
/// Потокобезопасен: Studio проверяет статусы, пока идёт сборка.
/// </summary>
/// <remarks>
/// Зависимость A → B: B в <see cref="AssetManifest.DependsOn"/> ассета A или готовый файл, прочитанный A
/// (<see cref="BakeContext.ContentFile"/>), — выход B.
/// </remarks>
public sealed class Builder
{
    private const string BuildInfoOwner = "(сведения о сборке)";

    private readonly BakeProject _project;
    private readonly object _lock = new();
    private readonly string _cachePath;
    private readonly LocalCache _cache;
    private readonly ConcurrentDictionary<IAssetProcessor, string> _fingerprints = new();
    private int _building;

    private PakBuildInfo? _info;
    private Dictionary<string, PakEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private string? _pakError;
    private bool _pakExists;
    private DateTime _pakTime;
    private long _pakLength;

    public Builder(BakeProject project)
    {
        _project = project;
        _cachePath = Path.Combine(project.StateDir, "cache.json");
        _cache = LocalCache.Load(_cachePath);
        ReloadPak();
    }

    public BakeProject Project => _project;
    public bool Building => _building != 0;

    // ── текущий пак ─────────────────────────────────────────────────────

    /// <summary>Перечитать оглавление и сведения о сборке пака (после сборки или замены пака извне). Пак не остаётся открытым.</summary>
    public void ReloadPak()
    {
        PakBuildInfo? info = null;
        var entries = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        bool exists = File.Exists(_project.PakPath);
        DateTime time = default;
        long length = 0;
        if (exists)
        {
            try
            {
                var fi = new FileInfo(_project.PakPath);
                time = fi.LastWriteTimeUtc;
                length = fi.Length;
                using var pak = PakSource.Open(_project.PakPath);
                foreach (var e in pak.Entries) entries[e.Path] = e;
                info = PakBuildInfo.Read(pak);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error = e.Message;
                entries.Clear();
                info = null;
            }
        }
        lock (_lock)
        {
            _info = info;
            _entries = entries;
            _pakError = error;
            _pakExists = exists;
            _pakTime = time;
            _pakLength = length;
        }
    }

    public string? PakError { get { lock (_lock) return _pakError; } }
    public bool PakExists { get { lock (_lock) return _pakExists; } }
    public DateTime PakTimeUtc { get { lock (_lock) return _pakTime; } }
    public long PakLength { get { lock (_lock) return _pakLength; } }
    /// <summary>Сведения о сборке текущего пака; нет пака или сведений — null. Менять нельзя: сборка заменяет их целиком.</summary>
    public PakBuildInfo? BuildInfo { get { lock (_lock) return _info; } }

    /// <summary>Запись ассета в сведениях о сборке пака; в паке его нет — null.</summary>
    public PakAssetRecord? RecordOf(string assetId)
    {
        lock (_lock) return _info?.Assets.GetValueOrDefault(assetId);
    }

    /// <summary>Последняя упавшая сборка ассета (по локальному кэшу), если она ещё в силе.</summary>
    public AssetFailure? FailureOf(string assetId)
    {
        lock (_lock) return _cache.Failures.GetValueOrDefault(assetId);
    }

    /// <summary>Файлы пака с владельцами, по возрастанию пути.</summary>
    public IReadOnlyList<PakContentEntry> Contents()
    {
        lock (_lock)
        {
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_info is not null)
                foreach (var (id, rec) in _info.Assets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    foreach (var o in rec.Outputs)
                        owners.TryAdd(o.Path, id);
            return _entries.Values
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Select(e => new PakContentEntry(e.Path, e.Size, PakFormat.HashText(e.Hash),
                    e.Path.Equals(PakBuildInfo.EntryPath, StringComparison.OrdinalIgnoreCase) ? BuildInfoOwner : owners.GetValueOrDefault(e.Path)))
                .ToList();
        }
    }

    // ── каталог контента ────────────────────────────────────────────────

    private ContentCatalog? _catalog;
    private (IReadOnlyList<AssetManifest> Assets, PakBuildInfo? Info) _catalogKey;

    /// <summary>Каталог контента по манифестам проекта и сведениям о сборке текущего пака (пересчитывается, когда меняются они).</summary>
    public ContentCatalog Catalog()
    {
        lock (_lock) return CatalogLocked();
    }

    private ContentCatalog CatalogLocked()
    {
        if (_catalog is null || !ReferenceEquals(_catalogKey.Assets, _project.Assets) || !ReferenceEquals(_catalogKey.Info, _info))
        {
            _catalog = ContentCatalog.Build(_project, _info);
            _catalogKey = (_project.Assets, _info);
        }
        return _catalog;
    }

    // ── отпечатки ───────────────────────────────────────────────────────

    public string Fingerprint(IAssetProcessor p) =>
        _fingerprints.GetOrAdd(p, proc =>
            $"{proc.Id}|{proc.Version}|{string.Join(",", proc.CodeAssemblies.Distinct().Select(Mvid).Order(StringComparer.Ordinal))}");

    private static string Mvid(Assembly a) => a.GetName().Name + ":" + a.ManifestModule.ModuleVersionId.ToString("N")[..12];

    /// <summary>
    /// Хеш параметров единицы: свои параметры с корнями и, если есть ссылки (<see cref="AssetRefAttribute"/>), хеши параметров
    /// целей — правка рига пересобирает всех, кто на него ссылается. Без ссылок — прежний хеш.
    /// </summary>
    /// <exception cref="InvalidDataException">Циклическая ссылка или параметры цели не разбираются.</exception>
    private string ParamsHash(AssetManifest a, object parameters, IAssetProcessor proc, HashSet<string>? visiting = null)
    {
        string own = OwnParamsHash(parameters, proc.ParamsType);
        var refs = _project.ReferencesOf(a).Where(r => r.Target is not null).ToList();
        if (refs.Count == 0) return own;
        visiting ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visiting.Add(a.Id)) throw new InvalidDataException($"циклическая ссылка через {a.Id}");
        var text = new StringBuilder(own);
        foreach (var r in refs)
        {
            var target = r.Target!;
            var targetProc = _project.Processors.Find(target.Processor);
            string hash = targetProc is null ? "?" + target.Processor : ParamsHash(target, target.ReadParams(targetProc), targetProc, visiting);
            text.Append('|').Append(r.Field).Append('=').Append(r.TargetId).Append(':').Append(hash);
        }
        visiting.Remove(a.Id);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>Параметры вместе с корнями исходников: ${root} в путях раскрывается по корням, их смена — другие входы.</summary>
    private string OwnParamsHash(object parameters, Type type) =>
        Convert.ToHexString(SHA256.HashData([.. JsonSerializer.SerializeToUtf8Bytes(parameters, type, AssetManifest.ParamsJson),
            .. Encoding.UTF8.GetBytes(RootsHash())]))[..32];

    private string RootsHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
            _project.Roots.Roots.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}")))))[..16];

    // ── актуальность ────────────────────────────────────────────────────

    public AssetCheck Check(AssetManifest asset)
    {
        lock (_lock)
        {
            var result = CheckLocked(asset, [], []);
            return result;
        }
    }

    /// <summary>Статусы всех ассетов проекта одним проходом (общий кэш проверок зависимостей).</summary>
    public IReadOnlyDictionary<string, AssetCheck> CheckAll()
    {
        Dictionary<string, AssetCheck> memo = [];
        lock (_lock)
            foreach (var a in _project.Assets) CheckLocked(a, memo, []);
        SaveCache();
        return memo;
    }

    /// <summary>Актуален ли пак целиком: собран, открывается, все ассеты проекта актуальны, нет удалённых ассетов.</summary>
    public PakCheck CheckPak(IReadOnlyDictionary<string, AssetCheck>? checks = null)
    {
        checks ??= CheckAll();
        lock (_lock)
        {
            if (_pakError is not null) return new(PakStatus.Error, "пак не открывается: " + _pakError, []);
            if (!_pakExists) return new(PakStatus.Missing, "пак не собран", []);
            if (_info is null) return new(PakStatus.Stale, "в паке нет сведений о сборке — пересоберите его", []);
            var details = new List<string>();
            foreach (var (file, error) in _project.Broken) details.Add($"манифест не разобран: {file}: {error}");
            foreach (var a in _project.Assets)
                if (checks.TryGetValue(a.Id, out var c) && c.Status != AssetStatus.UpToDate) details.Add($"{a.Id}: {c.Reason}");
            foreach (var id in _info.Assets.Keys.Where(id => _project.Find(id) is null).Order(StringComparer.OrdinalIgnoreCase))
                details.Add($"{id}: ассет удалён из проекта, но есть в паке");
            if (details.Count == 0) return new(PakStatus.UpToDate, "пак актуален", []);
            return new(PakStatus.Stale, $"пак устарел: {details.Count} {Plural(details.Count, "причина", "причины", "причин")}", details);
        }
    }

    private static string Plural(int n, string one, string few, string many) =>
        (n % 10, n % 100) switch
        {
            (1, not 11) => one,
            (>= 2 and <= 4, < 12 or > 14) => few,
            _ => many,
        };

    private AssetCheck CheckLocked(AssetManifest a, Dictionary<string, AssetCheck> memo, HashSet<string> visiting)
    {
        if (memo.TryGetValue(a.Id, out var known)) return known;
        if (!visiting.Add(a.Id)) return new AssetCheck(AssetStatus.Error, "циклическая зависимость");
        var result = Compute();
        visiting.Remove(a.Id);
        memo[a.Id] = result;
        return result;

        AssetCheck Compute()
        {
            var proc = _project.Processors.Find(a.Processor);
            if (proc is null) return new(AssetStatus.Error, $"неизвестный обработчик «{a.Processor}» — нет среди встроенных и в плагинах проекта");
            object parameters;
            try { parameters = a.ReadParams(proc); }
            catch (InvalidDataException e) { return new(AssetStatus.Error, e.Message); }
            if (BlockingProblem(a) is { } blocking) return new(AssetStatus.Error, blocking);
            string paramsHash;
            try { paramsHash = ParamsHash(a, parameters, proc); }
            catch (InvalidDataException e) { return new(AssetStatus.Error, e.Message); }
            string fingerprint = Fingerprint(proc);

            if (_cache.Failures.TryGetValue(a.Id, out var failure))
            {
                if (FailureStands(failure, paramsHash, fingerprint)) return new(AssetStatus.Error, "последняя сборка упала: " + failure.Message);
                _cache.Failures.Remove(a.Id);
            }

            if (_pakError is not null) return new(AssetStatus.Dirty, "пак не открывается");
            if (_info is null) return new(AssetStatus.Dirty, _pakExists ? "в паке нет сведений о сборке" : "пак ещё не собран");
            if (!_info.Assets.TryGetValue(a.Id, out var rec)) return new(AssetStatus.Dirty, "нет в паке");
            if (rec.Processor != proc.Id || rec.Fingerprint != fingerprint) return new(AssetStatus.Dirty, "изменился код обработчика");
            if (rec.ParamsHash != paramsHash) return new(AssetStatus.Dirty, "изменились параметры");

            foreach (var dir in rec.InputDirs)
            {
                if (!Directory.Exists(dir)) return new(AssetStatus.NoSource, $"нет каталога исходников {dir}");
                var now = BakeContext.ListDir(dir).ToList();
                int recorded = rec.Inputs.Count(i => IsUnder(i.Path, dir));
                if (now.Count != recorded || now.Any(f => !rec.Inputs.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase))))
                    return new(AssetStatus.Dirty, $"в {dir} добавлены или удалены файлы");
            }
            foreach (var input in rec.Inputs)
            {
                if (!File.Exists(input.Path)) return new(AssetStatus.NoSource, $"нет исходника {input.Path}");
                if (!_cache.Matches(input, input.Path)) return new(AssetStatus.Dirty, $"изменён {input.Path}");
            }
            foreach (var output in rec.Outputs)
            {
                if (!_entries.TryGetValue(output.Path, out var entry)) return new(AssetStatus.Dirty, $"в паке нет выхода {output.Path}");
                if (PakFormat.HashText(entry.Hash) != output.Hash) return new(AssetStatus.Dirty, $"выход {output.Path} в паке не совпадает со сведениями о сборке");
            }
            List<AssetManifest> deps;
            try { deps = DependenciesLocked(a); }
            catch (InvalidDataException e) { return new(AssetStatus.Error, e.Message); }
            foreach (var content in rec.ContentInputs)
                if (_info.OwnerOf(content, a.Id) is null) return new(AssetStatus.Dirty, $"готовый файл {content}, который читает ассет, больше никто не пишет");
            foreach (var dep in deps)
            {
                var dc = CheckLocked(dep, memo, visiting);
                if (dc.Status != AssetStatus.UpToDate) return new(AssetStatus.Dirty, $"зависимость {dep.Id}: {dc.Reason}");
                if (_info.Assets.TryGetValue(dep.Id, out var depRec) && depRec.BakedUtc > rec.BakedUtc)
                    return new(AssetStatus.Dirty, $"зависимость {dep.Id} собрана позже");
            }
            return new(AssetStatus.UpToDate, "актуален");
        }
    }

    /// <summary>Ошибка в силе, пока не изменились параметры, код, корни и входы, которые обработчик успел прочитать.</summary>
    private bool FailureStands(AssetFailure f, string paramsHash, string fingerprint)
    {
        if (f.ParamsHash != paramsHash || f.Fingerprint != fingerprint || f.RootsHash != RootsHash()) return false;
        if (f.Missing.Any(p => File.Exists(p) || Directory.Exists(p))) return false;
        foreach (var file in f.Inputs)
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) > f.Utc) return false;
        foreach (var dir in f.InputDirs)
            if (!Directory.Exists(dir) || Directory.GetLastWriteTimeUtc(dir) > f.Utc
                || BakeContext.ListDir(dir).Any(x => File.GetLastWriteTimeUtc(x) > f.Utc)) return false;
        return true;
    }

    /// <summary>
    /// Что не даёт собирать единицу, кроме параметров и обработчика: битая ссылка; при strictLayout — нарушение
    /// раскладки и неуказанное или неизвестное происхождение. null — ничего.
    /// </summary>
    private string? BlockingProblem(AssetManifest a)
    {
        if (_project.ReferencesOf(a).FirstOrDefault(r => r.Problem is not null) is { } bad) return $"ссылка {bad.Field}: {bad.Problem}";
        if (!_project.StrictLayout) return null;
        lock (_lock)
            if (CatalogLocked().IssuesOf(a.Id) is [var issue, ..]) return "раскладка: " + issue.Text;
        if (a.Import.Length == 0) return "происхождение не указано (поле import)";
        if (!_project.Imports.ContainsKey(a.Import)) return $"неизвестный импорт «{a.Import}»";
        return null;
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ассеты, которые должны быть собраны раньше <paramref name="a"/>.</summary>
    /// <exception cref="InvalidDataException">dependsOn ссылается на несуществующий ассет.</exception>
    public IReadOnlyList<AssetManifest> Dependencies(AssetManifest a)
    {
        lock (_lock) return DependenciesLocked(a);
    }

    private List<AssetManifest> DependenciesLocked(AssetManifest a)
    {
        var deps = new List<AssetManifest>();
        foreach (var id in a.DependsOn)
        {
            var d = _project.Find(id) ?? throw new InvalidDataException($"{a.File}: dependsOn — нет ассета «{id}»");
            if (!deps.Contains(d)) deps.Add(d);
        }
        if (_info?.Assets.GetValueOrDefault(a.Id) is { } rec)
            foreach (var content in rec.ContentInputs)
                if (_info.OwnerOf(content, a.Id) is { } owner && _project.Find(owner) is { } d && !deps.Contains(d)) deps.Add(d);
        return deps;
    }

    private void SaveCache()
    {
        lock (_lock) _cache.Save(_cachePath);
    }

    // ── сборка ──────────────────────────────────────────────────────────

    private sealed record BuiltAsset(PakAssetRecord Record, IReadOnlyList<(string Path, string Full)> Outputs);

    /// <summary>Состояние одной сборки: прежний пак, собранные ассеты, временный каталог.</summary>
    private sealed class Session
    {
        public required string Work;
        public PakSource? Old;
        public PakBuildInfo? OldInfo;
        public readonly ConcurrentDictionary<string, BuiltAsset> Built = new(StringComparer.OrdinalIgnoreCase);
        public int Index;
    }

    /// <summary>
    /// Собрать пак: <paramref name="selected"/> (null — все ассеты проекта) вместе с зависимостями запекаются, если
    /// устарели; остальные ассеты попадают в пак такими, какими были в прежнем. Блокирует до конца; события приходят
    /// из рабочих потоков. Отмена не прерывает ассет на середине — не начинаются следующие, и пак не пишется.
    /// </summary>
    /// <exception cref="InvalidOperationException">Сборка этого проекта уже идёт.</exception>
    public BuildSummary Build(IReadOnlyList<AssetManifest>? selected, BuildOptions options, Action<BakeEvent> emit, CancellationToken cancellation = default)
    {
        if (Interlocked.Exchange(ref _building, 1) == 1) throw new InvalidOperationException("сборка уже идёт");
        var sw = Stopwatch.StartNew();
        var summary = new BuildSummary();
        var session = new Session { Work = Path.Combine(Path.GetTempPath(), "sword-bake", "build-" + Guid.NewGuid().ToString("N")[..12]) };
        try
        {
            ReloadPak();
            lock (_lock) session.OldInfo = _info;
            if (session.OldInfo is not null)
            {
                try { session.Old = PakSource.Open(_project.PakPath); }
                catch (Exception e) when (e is InvalidDataException or IOException) { session.OldInfo = null; }
            }
            else if (PakExists && PakError is null)
                emit(new BakeEvent(BakeEventKind.Warning, "", "в прежнем паке нет сведений о сборке — все ассеты собираются заново"));

            if (!BakeAssets(selected ?? _project.Assets, options, emit, summary, session, cancellation)) return summary;
            if (cancellation.IsCancellationRequested)
            {
                summary.Cancelled = true;
                return summary;
            }
            WritePak(session, emit, summary, cancellation);
            return summary;
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
            emit(new BakeEvent(BakeEventKind.Cancelled, "", "сборка отменена — прежний пак не тронут"));
            return summary;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            summary.Failed++;
            emit(new BakeEvent(BakeEventKind.Failed, "", $"пак не записан: {e.Message} — прежний пак не тронут"));
            return summary;
        }
        finally
        {
            session.Old?.Dispose();
            try { if (Directory.Exists(session.Work)) Directory.Delete(session.Work, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            SaveCache();
            ReloadPak();
            summary.Elapsed = sw.Elapsed;
            Interlocked.Exchange(ref _building, 0);
        }
    }

    /// <returns>Все ассеты собраны или актуальны.</returns>
    private bool BakeAssets(IReadOnlyList<AssetManifest> selected, BuildOptions options, Action<BakeEvent> emit, BuildSummary summary,
        Session session, CancellationToken cancellation)
    {
        var all = new List<AssetManifest>();
        void Add(AssetManifest a, HashSet<string> path)
        {
            if (!path.Add(a.Id)) throw new InvalidDataException($"циклическая зависимость через {a.Id}");
            foreach (var d in Dependencies(a)) Add(d, path);
            path.Remove(a.Id);
            if (!all.Contains(a)) all.Add(a);   // зависимости попадают раньше — порядок уже топологический
        }
        foreach (var a in selected) Add(a, []);

        var done = new Dictionary<string, bool>();
        var pending = new List<AssetManifest>(all);
        var running = new List<(AssetManifest Asset, IAssetProcessor? Proc, Task<bool> Task)>();
        var busy = new HashSet<string>();
        foreach (var a in pending) emit(new BakeEvent(BakeEventKind.Queued, a.Id, ""));

        while (pending.Count > 0 || running.Count > 0)
        {
            if (cancellation.IsCancellationRequested && pending.Count > 0)
            {
                foreach (var a in pending) emit(new BakeEvent(BakeEventKind.Cancelled, a.Id, "отменено"));
                pending.Clear();
                summary.Cancelled = true;
            }
            for (int i = 0; i < pending.Count && running.Count < Math.Max(1, options.Jobs);)
            {
                var a = pending[i];
                var deps = Dependencies(a);
                if (deps.Any(d => all.Contains(d) && !done.ContainsKey(d.Id))) { i++; continue; }
                var proc = _project.Processors.Find(a.Processor);
                if (proc is { Parallel: false } && busy.Contains(proc.Id)) { i++; continue; }
                pending.RemoveAt(i);
                if (deps.FirstOrDefault(d => done.TryGetValue(d.Id, out bool ok) && !ok) is { } failed)
                {
                    emit(new BakeEvent(BakeEventKind.Skipped, a.Id, $"не собрана зависимость {failed.Id}"));
                    Interlocked.Increment(ref summary.Skipped);
                    done[a.Id] = false;
                    continue;
                }
                if (proc is { Parallel: false }) busy.Add(proc.Id);
                var rebuiltDep = deps.FirstOrDefault(d => session.Built.ContainsKey(d.Id));
                int index = ++session.Index;
                running.Add((a, proc, Task.Run(() => BakeOne(a, index, options.Force, rebuiltDep, emit, summary, session, cancellation))));
            }
            if (running.Count == 0) continue;
            int k = Task.WaitAny(running.Select(r => (Task)r.Task).ToArray());
            var finished = running[k];
            running.RemoveAt(k);
            done[finished.Asset.Id] = finished.Task.Result;
            if (finished.Proc is { Parallel: false }) busy.Remove(finished.Proc.Id);
        }

        if (summary.Failed == 0 && summary.Skipped == 0 && !summary.Cancelled) return true;
        emit(new BakeEvent(summary.Cancelled && summary.Failed == 0 ? BakeEventKind.Cancelled : BakeEventKind.Failed, "",
            summary.Cancelled && summary.Failed == 0
                ? "сборка отменена — пак не обновлён, прежний не тронут"
                : $"пак не обновлён: ошибок {summary.Failed}, пропущено {summary.Skipped} — прежний пак не тронут"));
        return false;
    }

    private bool BakeOne(AssetManifest a, int index, bool force, AssetManifest? rebuiltDep, Action<BakeEvent> emit, BuildSummary summary,
        Session session, CancellationToken cancellation)
    {
        AssetCheck check;
        try { check = Check(a); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            emit(new BakeEvent(BakeEventKind.Failed, a.Id, $"исходник не читается: {e.Message}"));
            Interlocked.Increment(ref summary.Failed);
            return false;
        }
        if (!force && rebuiltDep is null && check.Status == AssetStatus.UpToDate)
        {
            emit(new BakeEvent(BakeEventKind.UpToDate, a.Id, check.Reason));
            Interlocked.Increment(ref summary.UpToDate);
            return true;
        }
        var sw = Stopwatch.StartNew();
        var proc = _project.Processors.Find(a.Processor);
        if (proc is null || BlockingProblem(a) is not null)
        {
            emit(new BakeEvent(BakeEventKind.Failed, a.Id, check.Reason));
            Interlocked.Increment(ref summary.Failed);
            return false;
        }
        string reason = check.Status == AssetStatus.UpToDate
            ? rebuiltDep is not null ? $"пересобрана зависимость {rebuiltDep.Id}" : "принудительно"
            : check.Reason;
        emit(new BakeEvent(BakeEventKind.Started, a.Id, reason));
        using var log = new LineWriter(line => emit(new BakeEvent(BakeEventKind.Log, a.Id, line)));
        var ctx = new BakeContext(_project, a, Path.Combine(session.Work, index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            path => ReadContent(session, path), log, text => emit(new BakeEvent(BakeEventKind.Warning, a.Id, text)), cancellation);
        string paramsHash = "", fingerprint = Fingerprint(proc);
        try
        {
            var parameters = a.ReadParams(proc);
            paramsHash = ParamsHash(a, parameters, proc);
            if (proc.Incremental) Prime(session, a, ctx.OutputRoot);
            proc.Process(ctx, parameters);
            log.Flush();
            var outputs = ctx.Outputs(proc.Id);
            if (proc.DeclareOutputs(a, parameters) is { } declared)
                foreach (var (path, _) in outputs)
                    if (!declared.Any(d => d.Covers(path)))
                        throw new InvalidDataException($"обработчик {proc.Id} записал необъявленный выход {path}; объявлены: {string.Join(", ", declared)}");
            var rec = ctx.Record(proc.Id, fingerprint, paramsHash, _cache);
            session.Built[a.Id] = new BuiltAsset(rec, outputs);
            lock (_lock) _cache.Failures.Remove(a.Id);
            emit(new BakeEvent(BakeEventKind.Finished, a.Id,
                $"выходов {outputs.Count}, {outputs.Sum(o => new FileInfo(o.Full).Length) / 1024} КБ", sw.Elapsed));
            Interlocked.Increment(ref summary.Built);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Flush();
            string message = e is ArgumentException or InvalidDataException or IOException or JsonException or FormatException or OperationCanceledException
                ? e.Message
                : e.ToString();
            lock (_lock)
                _cache.Failures[a.Id] = new AssetFailure
                {
                    Message = message, Utc = DateTime.UtcNow, ParamsHash = paramsHash, Fingerprint = fingerprint, RootsHash = RootsHash(),
                    Inputs = [.. ctx.InputFiles], InputDirs = [.. ctx.InputDirs], Missing = [.. ctx.Missing],
                };
            emit(new BakeEvent(BakeEventKind.Failed, a.Id, message, sw.Elapsed));
            Interlocked.Increment(ref summary.Failed);
            return false;
        }
    }

    /// <summary>Готовый файл для <see cref="BakeContext.ContentFile"/>: из ассета, собранного в этой сборке, иначе из прежнего пака.</summary>
    private static Stream? ReadContent(Session session, string path)
    {
        foreach (var built in session.Built.Values)
            foreach (var (p, full) in built.Outputs)
                if (p.Equals(path, StringComparison.OrdinalIgnoreCase)) return File.OpenRead(full);
        return session.Old is { } old && old.Exists(path) ? old.OpenRead(path) : null;
    }

    /// <summary>Разложить выходы ассета из прежнего пака в его временный каталог (для инкрементальных обработчиков).</summary>
    private static void Prime(Session session, AssetManifest a, string outputRoot)
    {
        if (session.Old is not { } old || session.OldInfo?.Assets.GetValueOrDefault(a.Id) is not { } rec) return;
        foreach (var o in rec.Outputs)
        {
            if (!old.Exists(o.Path)) continue;
            string full = Path.Combine(outputRoot, o.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var source = old.OpenRead(o.Path);
            using var file = File.Create(full);
            source.CopyTo(file);
        }
    }

    private sealed record PakItem(string Owner, string? Full, PakEntry? OldEntry);

    /// <summary>Записать новый пак: собранные ассеты — из временного каталога, остальные — из прежнего пака.</summary>
    private void WritePak(Session session, Action<BakeEvent> emit, BuildSummary summary, CancellationToken cancellation)
    {
        var items = new SortedDictionary<string, PakItem>(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, PakAssetRecord>(StringComparer.OrdinalIgnoreCase);
        var built = new Dictionary<string, IReadOnlyList<(string Path, string Full)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in _project.Assets.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (session.Built.TryGetValue(a.Id, out var b))
            {
                records[a.Id] = b.Record;
                built[a.Id] = b.Outputs;
                foreach (var (path, full) in b.Outputs) AddItem(path, new PakItem(a.Id, full, null));
            }
            else if (session.OldInfo?.Assets.GetValueOrDefault(a.Id) is { } prev && session.Old is { } old)
            {
                var missing = prev.Outputs.FirstOrDefault(o => old.Find(o.Path) is null);
                if (missing is not null)
                {
                    emit(new BakeEvent(BakeEventKind.Warning, a.Id, $"в прежнем паке нет выхода {missing.Path} — ассет в пак не попадёт, соберите его"));
                    continue;
                }
                records[a.Id] = prev;
                foreach (var o in prev.Outputs) AddItem(o.Path, new PakItem(a.Id, null, old.Find(o.Path)));
            }
            else emit(new BakeEvent(BakeEventKind.Warning, a.Id, "ещё не собирался — в пак не попадёт"));
        }

        // ничего не собрано, и в пак идут ровно те же ассеты — прежний пак и есть результат: не переписывать его
        // (иначе игра, держащая пак открытым, получала бы замену без единого изменения)
        if (built.Count == 0 && session.OldInfo is { } oldInfo && oldInfo.Assets.Count == records.Count
            && records.Keys.All(oldInfo.Assets.ContainsKey) && records.All(kv => ReferenceEquals(kv.Value, oldInfo.Assets[kv.Key])))
        {
            summary.PakWritten = true;
            summary.PakUnchanged = true;
            summary.PakFiles = items.Count;
            summary.PakBytes = items.Values.Sum(i => i.OldEntry?.Size ?? 0);
            emit(new BakeEvent(BakeEventKind.Log, "", $"пак не изменился: {_project.PakPath}"));
            return;
        }

        void AddItem(string path, PakItem item)
        {
            if (!items.TryGetValue(path, out var existing))
            {
                items[path] = item;
                return;
            }
            // общий выход нескольких ассетов (скелет библиотеки) — один файл, если содержимое одно
            if (HashOf(session, existing) != HashOf(session, item))
                throw new InvalidDataException($"«{path}» пишут {existing.Owner} и {item.Owner} с разным содержимым — пересоберите оба (--force)");
        }

        var entries = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        using var writer = new PakWriter(_project.PakPath);
        long bytes = 0;
        foreach (var (path, item) in items)
        {
            cancellation.ThrowIfCancellationRequested();
            using var source = item.Full is not null ? File.OpenRead(item.Full) : session.Old!.OpenRead(path);
            var entry = writer.Add(path, source);
            entries[path] = entry;
            bytes += entry.Size;
        }
        foreach (var (id, outputs) in built)
            records[id].Outputs = outputs
                .Select(o => new PakFileRecord { Path = o.Path, Size = entries[o.Path].Size, Hash = PakFormat.HashText(entries[o.Path].Hash) })
                .ToList();
        foreach (var (id, rec) in records)
            if (!built.ContainsKey(id) && rec.Outputs.FirstOrDefault(o => PakFormat.HashText(entries[o.Path].Hash) != o.Hash) is { } bad)
                emit(new BakeEvent(BakeEventKind.Warning, id, $"выход {bad.Path} в прежнем паке не совпадал со сведениями о сборке — ассет будет устаревшим"));

        var info = new PakBuildInfo { Project = _project.Name, BuiltUtc = DateTime.UtcNow, Assets = records };
        using (var json = new MemoryStream(info.ToUtf8())) writer.Add(PakBuildInfo.EntryPath, json);
        cancellation.ThrowIfCancellationRequested();
        session.Old?.Dispose();
        session.Old = null;
        writer.Commit();

        summary.PakWritten = true;
        summary.PakFiles = items.Count;
        summary.PakBytes = bytes;
        int carried = records.Count - built.Count;
        emit(new BakeEvent(BakeEventKind.Log, "",
            $"пак → {_project.PakPath}: файлов {items.Count}, {bytes / 1048576.0:F1} МБ; ассетов собрано {built.Count}, из прежнего пака {carried}"));
    }

    private static ulong HashOf(Session session, PakItem item)
    {
        if (item.OldEntry is { } e) return e.Hash;
        using var s = File.OpenRead(item.Full!);
        return PakFormat.HashOf(s);
    }

    // ── целостность ─────────────────────────────────────────────────────

    /// <summary>
    /// Проверить целостность пака: хеши всех файлов с оглавлением (читает пак целиком) и согласие оглавления
    /// со сведениями о сборке. Об актуальности ничего не говорит — это <see cref="CheckPak"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">Пака нет.</exception>
    public PakVerifyReport Verify(Action<string>? progress = null, CancellationToken cancellation = default)
    {
        var report = new PakVerifyReport();
        if (!File.Exists(_project.PakPath)) throw new FileNotFoundException($"нет пака {_project.PakPath}", _project.PakPath);
        PakSource pak;
        try
        {
            pak = PakSource.Open(_project.PakPath);
        }
        catch (InvalidDataException e)
        {
            report.Problems.Add(e.Message);
            return report;
        }
        using (pak)
        {
            report.Files = pak.Entries.Count;
            report.Bytes = pak.Length;
            progress?.Invoke($"оглавление в порядке: файлов {pak.Entries.Count}, {pak.Length / 1048576.0:F1} МБ; проверка хешей…");
            foreach (var bad in pak.Corrupted())
            {
                cancellation.ThrowIfCancellationRequested();
                report.Problems.Add($"{bad.Path}: содержимое не совпадает с хешем оглавления");
            }
            PakBuildInfo? info = null;
            try { info = PakBuildInfo.Read(pak); }
            catch (InvalidDataException e) { report.Problems.Add(e.Message); }
            if (info is null)
            {
                report.Problems.Add("нет сведений о сборке (.bake/build.json) — неизвестно, из чего собран пак");
                return report;
            }
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PakBuildInfo.EntryPath };
            foreach (var (id, rec) in info.Assets)
                foreach (var o in rec.Outputs)
                {
                    owned.Add(o.Path);
                    if (pak.Find(o.Path) is not { } e) report.Problems.Add($"{id}: в паке нет выхода {o.Path}");
                    else if (PakFormat.HashText(e.Hash) != o.Hash) report.Problems.Add($"{id}: {o.Path} не совпадает со сведениями о сборке");
                }
            foreach (var e in pak.Entries.Where(e => !owned.Contains(e.Path)))
                report.Problems.Add($"{e.Path}: файл не принадлежит ни одному ассету");
        }
        return report;
    }
}
