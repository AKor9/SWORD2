using System.Collections.Concurrent;
using System.Diagnostics;
using Sword.Engine.Platform.Win32;

namespace Sword.Bake.Studio;

/// <summary>Состояние ассета в текущей (или последней) фоновой задаче.</summary>
internal enum LiveKind { Queued, Building, UpToDate, Built, Failed, Skipped, Cancelled }

internal sealed record LogEntry(DateTime Clock, BakeEventKind Kind, string Asset, string Text);

/// <summary>
/// Модель Studio без отрисовки: проект, статусы ассетов и пака, фоновые задачи (сборка, проверка целостности,
/// проверка статусов), лог, окна выбора файлов и слежение за файлами. Фоновые потоки только кладут события в очередь
/// и будят окно; разбирает их <see cref="Pump"/> на потоке окна — всё, что читает отрисовка, меняется только там.
/// </summary>
internal sealed class StudioApp : IDisposable
{
    private const int LogLimit = 50_000;
    private const double Debounce = 0.4;

    private readonly Action _wake;
    private readonly nint _owner;
    private readonly ConcurrentQueue<BakeEvent> _events = new();
    private readonly ConcurrentQueue<Action> _calls = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private Task<BuildSummary>? _task;
    private CancellationTokenSource? _cts;
    private string _taskTitle = "";
    /// <summary>Итог текущей задачи одной строкой для лога; null — как у сборки.</summary>
    private Func<BuildSummary, string>? _describe;

    private int _refreshing;
    private volatile bool _refreshAgain;
    private volatile Refreshed? _fresh;
    private bool _autoBuildAfterRefresh;

    private volatile bool _changed, _manifestsChanged, _pakChanged;
    private long _changedAt;

    private sealed record Refreshed(IReadOnlyDictionary<string, AssetCheck> Checks, PakCheck Pak, IReadOnlyList<PakContentEntry> Contents);

    public StudioApp(BakeProject project, Action wake, nint owner)
    {
        Project = project;
        Builder = new Builder(project);
        _wake = wake;
        _owner = owner;
        Selected = project.Assets.FirstOrDefault()?.Id;
        AddLog(BakeEventKind.Log, "", $"проект {project.Name}: {project.Root}; ассетов {project.Assets.Count}, обработчиков {project.Processors.Processors.Count}");
        ReportProblems();
        RefreshStatuses();
        RebuildWatchers();
    }

    public BakeProject Project { get; }
    public Builder Builder { get; }

    public IReadOnlyDictionary<string, AssetCheck> Statuses { get; private set; } = new Dictionary<string, AssetCheck>();
    public PakCheck? PakStatus { get; private set; }
    public IReadOnlyList<PakContentEntry> PakContents { get; private set; } = [];
    public bool StatusesKnown { get; private set; }
    public bool Refreshing => _refreshing != 0;

    /// <summary>Состояния ассетов в текущей или последней задаче.</summary>
    public readonly Dictionary<string, LiveKind> Live = [];
    public readonly List<LogEntry> Log = [];
    /// <summary>Растёт при каждом изменении лога — отрисовка пересобирает отфильтрованный список только тогда.</summary>
    public int LogVersion { get; private set; }

    public string? Selected;
    /// <summary>Следить за исходниками и манифестами: изменение — проверка статусов и сборка устаревшего.</summary>
    public bool Watch = true;

    public bool Busy => _task is not null;
    public int TaskTotal { get; private set; }
    public int TaskDone { get; private set; }
    public string? TaskCurrent { get; private set; }
    public string TaskTitle => _taskTitle;
    public BuildSummary? LastSummary { get; private set; }
    /// <summary>Итог последней проверки целостности и когда она была.</summary>
    public (PakVerifyReport Report, DateTime Time)? LastVerify { get; private set; }

    /// <summary>Открыто системное окно выбора файла: второе не открывается.</summary>
    public bool DialogOpen { get; private set; }
    /// <summary>Выбран другой проект — окно заменит модель.</summary>
    public BakeProject? NextProject { get; private set; }

    /// <summary>Ничего не происходит: статусы проверены, задач и отложенных изменений нет.</summary>
    public bool Idle => StatusesKnown && !Busy && !Refreshing && !_changed && _events.IsEmpty && _calls.IsEmpty && _fresh is null;

    /// <summary>Чем модель занята — почему не <see cref="Idle"/> (снимок по таймауту пишет это в консоль).</summary>
    public string Activity => string.Join(", ", new[]
    {
        StatusesKnown ? null : "статусы не проверены", Busy ? $"задача «{_taskTitle}»" : null, Refreshing ? "проверка статусов" : null,
        _changed ? "изменения файлов ждут паузы" : null, _events.IsEmpty ? null : "события задачи", _calls.IsEmpty ? null : "вызовы",
        _fresh is null ? null : "свежие статусы",
    }.OfType<string>());

    /// <summary>
    /// Через сколько секунд нужен кадр без ввода (истекает пауза после изменения файлов).
    /// Пока идёт задача, изменения всё равно ждут её конца (<see cref="Pump"/>), и будильник не нужен:
    /// иначе окно крутило бы пустые кадры всю сборку.
    /// </summary>
    public double WakeIn => _changed && !Busy ? Math.Max(0, Debounce - Stopwatch.GetElapsedTime(_changedAt).TotalSeconds) : double.PositiveInfinity;

    public AssetManifest? SelectedAsset => Selected is null ? null : Project.Find(Selected);

    public AssetCheck StatusOf(string id) =>
        Statuses.TryGetValue(id, out var c) ? c : new AssetCheck(AssetStatus.Dirty, StatusesKnown ? "статус неизвестен" : "проверяется…");

    // ── кадр ───────────────────────────────────────────────────────────

    /// <summary>Разобрать всё, что пришло от фоновых задач, окон выбора и слежения. Зовётся раз в кадр на потоке окна.</summary>
    /// <returns>Что-то изменилось — кадр нужно перерисовать.</returns>
    public bool Pump()
    {
        bool any = false;
        bool taskDone = _task?.IsCompleted == true;   // до разбора очереди: последние события задачи должны попасть в лог раньше итога
        while (_calls.TryDequeue(out var call))
        {
            call();
            any = true;
        }
        while (_events.TryDequeue(out var e))
        {
            Apply(e);
            any = true;
        }

        if (_fresh is { } fresh)
        {
            _fresh = null;
            Statuses = fresh.Checks;
            PakStatus = fresh.Pak;
            PakContents = fresh.Contents;
            StatusesKnown = true;
            any = true;
            if (_autoBuildAfterRefresh && !Busy)
            {
                _autoBuildAfterRefresh = false;
                var dirty = Project.Assets.Where(a => fresh.Checks.TryGetValue(a.Id, out var c) && c.Status == AssetStatus.Dirty).ToList();
                if (dirty.Count > 0) StartBuild(dirty, force: false, "автосборка");
            }
        }

        if (taskDone && _task is { IsCompleted: true } done)
        {
            _task = null;
            _cts?.Dispose();
            _cts = null;
            var s = done.Result;
            LastSummary = s;
            TaskCurrent = null;
            AddLog(s.Ok || _describe is not null && s.Failed == 0 ? BakeEventKind.Finished : BakeEventKind.Failed, "",
                _describe?.Invoke(s)
                ?? $"{_taskTitle}: за {s.Elapsed.TotalSeconds:F1} с — запечено {s.Built}, актуальны {s.UpToDate}, ошибок {s.Failed}, пропущено {s.Skipped}" +
                   (s.Cancelled ? ", отменено" : "") +
                   (s.PakUnchanged ? "; пак не изменился" : s.PakWritten ? $"; пак записан: файлов {s.PakFiles}, {s.PakBytes / 1048576.0:F1} МБ" : "; пак не записан"));
            _pakChanged = false;   // свою запись пака уже учли: сборка перечитала его сама
            RefreshStatuses();
            RebuildWatchers();   // у собранных ассетов могли появиться новые входы
            any = true;
        }

        if (_changed && Stopwatch.GetElapsedTime(_changedAt).TotalSeconds >= Debounce && !Busy)
        {
            _changed = false;
            if (_manifestsChanged)
            {
                _manifestsChanged = false;
                _pakChanged = false;
                ReloadProject("файлы проекта изменены");
            }
            else if (_pakChanged)
            {
                _pakChanged = false;
                Builder.ReloadPak();
                AddLog(BakeEventKind.Log, "", "пак изменён снаружи — перечитан");
                RefreshStatuses();
            }
            else
            {
                _autoBuildAfterRefresh = Watch;
                RefreshStatuses();
            }
            any = true;
        }
        return any;
    }

    private void Apply(BakeEvent e)
    {
        bool asset = e.AssetId.Length > 0;
        switch (e.Kind)
        {
            case BakeEventKind.Queued:
                if (asset) Live[e.AssetId] = LiveKind.Queued;
                TaskTotal++;
                return;   // в лог не идёт: список очереди выводит начало задачи
            case BakeEventKind.Started:
                if (asset) Live[e.AssetId] = LiveKind.Building;
                TaskCurrent = e.AssetId;
                break;
            case BakeEventKind.UpToDate:
                if (asset) Live[e.AssetId] = LiveKind.UpToDate;
                TaskDone++;
                break;
            case BakeEventKind.Finished:
                if (asset)
                {
                    Live[e.AssetId] = LiveKind.Built;
                    TaskDone++;
                }
                break;
            case BakeEventKind.Failed:
                if (asset)
                {
                    Live[e.AssetId] = LiveKind.Failed;
                    TaskDone++;
                }
                break;
            case BakeEventKind.Skipped:
                if (asset) Live[e.AssetId] = LiveKind.Skipped;
                TaskDone++;
                break;
            case BakeEventKind.Cancelled:
                if (asset)
                {
                    Live[e.AssetId] = LiveKind.Cancelled;
                    TaskDone++;
                }
                break;
        }
        string text = e.Kind == BakeEventKind.Finished && e.Elapsed > TimeSpan.Zero ? $"за {e.Elapsed.TotalSeconds:F1} с: {e.Text}" : e.Text;
        AddLog(e.Kind, e.AssetId, text);
    }

    public void AddLog(BakeEventKind kind, string asset, string text)
    {
        if (Log.Count >= LogLimit) Log.RemoveRange(0, LogLimit / 10);
        Log.Add(new LogEntry(DateTime.Now, kind, asset, text));
        LogVersion++;
    }

    public void ClearLog()
    {
        Log.Clear();
        LogVersion++;
    }

    private void ReportProblems()
    {
        foreach (var problem in Project.Problems) AddLog(BakeEventKind.Warning, "", "проект: " + problem);
        foreach (var (file, error) in Project.Broken) AddLog(BakeEventKind.Failed, "", $"манифест не разобран: {file}: {error}");
        foreach (var root in Project.MissingRoots())
            AddLog(BakeEventKind.Warning, "", $"корень ${{{root}}} упомянут в манифестах, но не задан — узел проекта над деревом ассетов");
    }

    // ── сборка и пак ───────────────────────────────────────────────────

    /// <summary>Собрать пак: устаревшие ассеты запекаются, остальные берутся из прежнего пака.</summary>
    public void BuildAll() =>
        StartTask("сборка пака", $"сборка пака → {Project.PakPath}",
            (emit, token) => Builder.Build(null, new BuildOptions(), emit, token));

    /// <summary>Собрать пак, запекая <paramref name="assets"/> (с зависимостями); остальные ассеты — из прежнего пака.</summary>
    public void StartBuild(IReadOnlyList<AssetManifest> assets, bool force, string title)
    {
        if (assets.Count == 0) return;
        StartTask(title, $"{title}{(force ? " (принудительно)" : "")}: {string.Join(", ", assets.Select(a => a.Id))}",
            (emit, token) => Builder.Build(assets, new BuildOptions { Force = force }, emit, token));
    }

    /// <summary>Проверить целостность пака: хеши файлов с оглавлением и оглавление со сведениями о сборке.</summary>
    public void VerifyPak() =>
        StartTask("проверка пака", $"проверка целостности {Project.PakPath}", (emit, token) =>
        {
            var sw = Stopwatch.StartNew();
            var report = Builder.Verify(text => emit(new BakeEvent(BakeEventKind.Log, "", text)), token);
            foreach (var p in report.Problems) emit(new BakeEvent(BakeEventKind.Failed, "", p));
            _calls.Enqueue(() => LastVerify = (report, DateTime.Now));
            return new BuildSummary { Failed = report.Problems.Count, PakWritten = report.Ok, Elapsed = sw.Elapsed };
        }, s => s.Failed == 0
            ? $"пак цел: за {s.Elapsed.TotalSeconds:F1} с все хеши совпали, оглавление согласно со сведениями о сборке"
            : $"пак повреждён или не согласован: проблем {s.Failed}");

    public void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            AddLog(BakeEventKind.Warning, "", "отмена: начатые ассеты доделываются, остальные не начнутся, пак не пишется");
        }
    }

    private void StartTask(string title, string announce, Func<Action<BakeEvent>, CancellationToken, BuildSummary> work,
        Func<BuildSummary, string>? describe = null)
    {
        if (Busy) return;
        _describe = describe;
        Live.Clear();
        TaskTotal = TaskDone = 0;
        TaskCurrent = null;
        _taskTitle = title;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        AddLog(BakeEventKind.Started, "", announce);
        void Emit(BakeEvent e)
        {
            _events.Enqueue(e);
            _wake();
        }
        _task = Task.Run(() =>
        {
            try
            {
                return work(Emit, token);
            }
            catch (Exception e)
            {
                Emit(new BakeEvent(BakeEventKind.Failed, "", e is InvalidDataException or IOException or ArgumentException or InvalidOperationException ? e.Message : e.ToString()));
                return new BuildSummary { Failed = 1 };
            }
            finally
            {
                _wake();
            }
        });
    }

    /// <summary>Проверить статусы ассетов и пака в фоне; уже идёт проверка — повторить после неё.</summary>
    public void RefreshStatuses()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshAgain = true;
            return;
        }
        Task.Run(() =>
        {
            try
            {
                do
                {
                    _refreshAgain = false;
                    var checks = Builder.CheckAll();
                    _fresh = new Refreshed(checks, Builder.CheckPak(checks), Builder.Contents());
                    _wake();
                }
                while (_refreshAgain);
            }
            catch (Exception e)
            {
                _events.Enqueue(new BakeEvent(BakeEventKind.Failed, "", "проверка статусов: " + e.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
                _wake();
            }
            // Заявка, пришедшая между выходом из цикла и снятием флага, иначе потерялась бы — и статусы
            // остались бы старыми до следующего события: перепроверяем уже отпустив флаг.
            if (_refreshAgain) RefreshStatuses();
        });
    }

    /// <summary>Перечитать проект (после сохранения из Studio или правки снаружи) и проверить статусы.</summary>
    public void ReloadProject(string reason)
    {
        try
        {
            Project.Reload();
        }
        catch (InvalidDataException e)
        {
            AddLog(BakeEventKind.Failed, "", "проект не перечитан: " + e.Message);
            return;
        }
        ReportProblems();
        if (Selected is not null && Project.Find(Selected) is null) Selected = Project.Assets.FirstOrDefault()?.Id;
        AddLog(BakeEventKind.Log, "", $"проект перечитан: {reason}; ассетов {Project.Assets.Count}");
        _autoBuildAfterRefresh = Watch;
        RefreshStatuses();
        RebuildWatchers();
    }

    // ── ассеты и проект ────────────────────────────────────────────────

    /// <summary>Добавить ассет. Ошибка — текстом (для окна добавления), успех — null.</summary>
    public string? AddAsset(string id, string processor, IReadOnlyDictionary<string, string> sources, string notes)
    {
        try
        {
            var asset = Project.AddAsset(id, processor, sources, notes);
            Selected = asset.Id;
            AddLog(BakeEventKind.Finished, asset.Id, $"ассет добавлен: {asset.File}");
            foreach (var s in Project.SourcesOf(asset).Where(s => s.Problem is not null))
                AddLog(BakeEventKind.Warning, asset.Id, $"{s.Field} = {s.Value}: {s.Problem}");
            RefreshStatuses();
            RebuildWatchers();
            return null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>Удалить ассет: манифест уходит в <c>.bake/removed</c>; из пака ассет уберёт следующая сборка.</summary>
    public void Delete(AssetManifest asset)
    {
        if (Busy) return;
        try
        {
            string backup = Project.RemoveAsset(asset);
            AddLog(BakeEventKind.Finished, asset.Id, $"ассет удалён; копия манифеста — {backup}. Пак обновит сборка");
            if (Selected == asset.Id) Selected = Project.Assets.FirstOrDefault()?.Id;
            _autoBuildAfterRefresh = false;
            RefreshStatuses();
            RebuildWatchers();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddLog(BakeEventKind.Failed, asset.Id, "не удалён: " + e.Message);
        }
    }

    /// <summary>Задать корень исходников в локальном файле проекта.</summary>
    public void SetRoot(string name, string path)
    {
        try
        {
            SourceRoots.SetUserRoot(Project.Root, name, path);
            AddLog(BakeEventKind.Log, "", $"${{{name}}} = {path} (в {SourceRoots.UserFile})");
            ReloadProject($"корень ${{{name}}}");
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AddLog(BakeEventKind.Failed, "", $"корень ${{{name}}} не задан: {e.Message}");
        }
    }

    /// <summary>Выбрать и открыть другой проект (или создать в выбранном каталоге).</summary>
    public void OpenOtherProject()
    {
        if (DialogOpen || Busy) return;
        DialogOpen = true;
        Task.Run(() =>
        {
            string? root = null;
            string? error = null;
            try { root = Program.ChooseProject(_owner); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { error = e.Message; }
            Post(() =>
            {
                DialogOpen = false;
                if (error is not null) AddLog(BakeEventKind.Failed, "", "проект не открыт: " + error);
                if (root is null) return;
                try { NextProject = BakeProject.Open(root); }
                catch (Exception e) when (e is IOException or InvalidDataException) { AddLog(BakeEventKind.Failed, "", "проект не открыт: " + e.Message); }
            });
        });
    }

    /// <summary>Системное окно выбора файлов или каталога в фоне; <paramref name="done"/> — на потоке окна, если выбор сделан.</summary>
    public void Pick(SourceKind kind, string title, bool multiple, string filter, string? near, Action<string[]> done)
    {
        if (DialogOpen) return;
        DialogOpen = true;
        string? folder = near is null ? null : Directory.Exists(near) ? near : Path.GetDirectoryName(near);
        Task.Run(() =>
        {
            string[]? result = null;
            string? error = null;
            try
            {
                result = kind == SourceKind.Directory
                    ? NativeDialogs.PickFolder(_owner, title, folder) is { } dir ? [dir] : null
                    : NativeDialogs.OpenFiles(_owner, title, multiple, filter, folder);
            }
            catch (InvalidOperationException e) { error = e.Message; }
            Post(() =>
            {
                DialogOpen = false;
                if (error is not null) AddLog(BakeEventKind.Failed, "", error);
                if (result is { Length: > 0 }) done(result);
            });
        });
    }

    private void Post(Action call)
    {
        _calls.Enqueue(call);
        _wake();
    }

    // ── слежение ───────────────────────────────────────────────────────

    /// <summary>
    /// Следить за манифестами, файлом проекта и локальными корнями, за паком и за исходниками, из которых он собран.
    /// Корни исходников целиком не отслеживаются: в них бывают проекты движков с кэшами, которые меняются постоянно
    /// и к ассетам отношения не имеют.
    /// </summary>
    private void RebuildWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        AddWatcher(Project.AssetsDir, "*" + AssetManifest.Extension, recursive: true, Change.Manifests);
        AddWatcher(Project.Root, "bake.*.json", recursive: false, Change.Manifests);
        if (Path.GetDirectoryName(Project.PakPath) is { } pakDir) AddWatcher(pakDir, Path.GetFileName(Project.PakPath), recursive: false, Change.Pak);

        var info = Builder.BuildInfo;
        var dirs = Project.Assets
            .Select(a => info?.Assets.GetValueOrDefault(a.Id))
            .Where(r => r is not null)
            .SelectMany(r => r!.InputDirs.Concat(r.Inputs.Select(i => Path.GetDirectoryName(i.Path)!)))
            .Concat(Project.Assets.SelectMany(a => Project.SourcesOf(a))
                .Where(s => s.FullPath is not null)
                .Select(s => s.Kind == SourceKind.Directory ? s.FullPath! : Path.GetDirectoryName(s.FullPath!)!))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Length)
            .ToList();
        var roots = new List<string>();
        foreach (var d in dirs)
            if (!roots.Any(r => d.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || d.Equals(r, StringComparison.OrdinalIgnoreCase)))
                roots.Add(d);
        foreach (var d in roots) AddWatcher(d, "", recursive: true, Change.Sources);
    }

    private enum Change { Sources, Manifests, Pak }

    private void AddWatcher(string dir, string filter, bool recursive, Change change)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            if (filter.Length > 0) w.Filter = filter;
            FileSystemEventHandler changed = (_, e) => OnChanged(change, e.FullPath);
            w.Changed += changed;
            w.Created += changed;
            w.Deleted += changed;
            w.Renamed += (_, e) => OnChanged(change, e.FullPath);
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            AddLog(BakeEventKind.Warning, "", $"слежение за {dir} не включилось: {e.Message}");
        }
    }

    private void OnChanged(Change change, string path)
    {
        if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        // кэш и копии удалённых манифестов; и сам каталог .bake: запись в нём меняет его время, и слежение за корнем
        // исходников, совпавшим с корнем проекта, иначе крутило бы проверку статусов без конца (проверка пишет кэш)
        if (path.Equals(Project.StateDir, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Project.StateDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        if (change == Change.Pak && Builder.Building) return;   // свою запись пака сборка перечитает сама
        if (change == Change.Manifests) _manifestsChanged = true;
        if (change == Change.Pak) _pakChanged = true;
        _changedAt = Stopwatch.GetTimestamp();
        _changed = true;
        _wake();
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        if (_task is not null)
        {
            _cts?.Cancel();
            Console.WriteLine("[studio] модель закрывается во время задачи — дожидаюсь начатых ассетов");
            _task.Wait();
        }
        _cts?.Dispose();
    }
}
