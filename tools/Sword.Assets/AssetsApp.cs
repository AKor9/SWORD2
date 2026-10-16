using System.Collections.Concurrent;
using Sword.Bake;
using Sword.Engine.Platform.Win32;

namespace Sword.Assets;

internal enum LogKind { Info, Done, Failed }

internal sealed record LogEntry(DateTime Clock, LogKind Kind, string Text);

/// <summary>
/// Модель окна без отрисовки: ресурсы и пак (<see cref="ContentPack"/>), пекарня (<see cref="Baker"/>), выбор, лог и одна
/// фоновая задача — «Собрать пак» или «Испечь». Фоновые потоки только кладут вызовы в очередь и будят окно; разбирает их
/// <see cref="Pump"/> на потоке окна — всё, что читает отрисовка, меняется только там.
/// </summary>
internal sealed class AssetsApp : IDisposable
{
    private const int LogLimit = 20_000;

    private readonly Action _wake;
    private readonly nint _owner;
    private readonly ConcurrentQueue<Action> _calls = new();
    private Task? _task;
    private int _taskDone, _taskTotal;

    public AssetsApp(ContentPack pack, Baker baker, Action wake, nint owner)
    {
        Pack = pack;
        Baker = baker;
        _wake = wake;
        _owner = owner;
        AddLog(LogKind.Info, $"ресурсы {pack.ContentDir}: файлов {pack.Files.Count}, выключено {pack.Files.Count(f => !pack.IsIncluded(f.Path))}; пак {pack.PakPath}");
    }

    public ContentPack Pack { get; }
    public Baker Baker { get; }

    /// <summary>Выбранный файл или каталог <c>Content/</c> (через «/»); пусто — весь <c>Content/</c>.</summary>
    public string Selected { get; set; } = "";

    public List<LogEntry> Log { get; } = [];
    /// <summary>Растёт с каждой записью лога — вид прокручивает к новым.</summary>
    public int LogVersion { get; private set; }

    public bool Busy => _task is { IsCompleted: false };
    public string TaskTitle { get; private set; } = "";
    public (int Done, int Total) Progress => (Volatile.Read(ref _taskDone), Volatile.Read(ref _taskTotal));
    public bool DialogOpen { get; private set; }
    /// <summary>Окно затихло: задач и окон выбора нет, очередь разобрана (снимки ждут этого).</summary>
    public bool Idle => !Busy && !DialogOpen && _calls.IsEmpty;
    public PackResult? LastPack { get; private set; }

    // ── пекарня ────────────────────────────────────────────────────────

    /// <summary>Сырой файл окна пекарни; null — не выбран.</summary>
    public string? Source { get; private set; }
    /// <summary>Конвертеры, которым годится <see cref="Source"/>.</summary>
    public IReadOnlyList<IConverter> Candidates { get; private set; } = [];
    /// <summary>Рецепт, который правит форма: прочитанный или новый с параметрами по умолчанию.</summary>
    public Recipe? Recipe { get; private set; }
    public bool RecipeExists { get; private set; }
    public string? SourceError { get; private set; }
    /// <summary>Файлы последней выпечки — пути в <c>Content/</c>.</summary>
    public IReadOnlyList<string> LastWritten { get; private set; } = [];
    /// <summary>Растёт, когда меняется рецепт формы (новый файл, другой конвертер) — форма перечитывает параметры.</summary>
    public int RecipeVersion { get; private set; }

    /// <summary>Выбрать сырой файл: конвертер по виду файла, рецепт с диска или новый.</summary>
    public void SetSource(string path)
    {
        Source = Path.GetFullPath(path);
        SourceError = null;
        Recipe = null;
        RecipeExists = false;
        LastWritten = [];
        Candidates = Baker.Candidates(Source);
        RecipeVersion++;
        if (Candidates.Count == 0)
        {
            SourceError = Baker.NoConverter(Source);
            return;
        }
        try
        {
            Recipe = Baker.LoadRecipe(Source);
            RecipeExists = Recipe is not null;
            Recipe ??= Baker.NewRecipe(Source, Candidates[0]);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException)
        {
            SourceError = e.Message;
        }
    }

    /// <summary>Другой конвертер для файла, которому годятся несколько: параметры — по умолчанию.</summary>
    public void ChooseConverter(IConverter converter)
    {
        if (Source is null || Recipe?.Converter == converter.Id) return;
        Recipe = Baker.NewRecipe(Source, converter);
        RecipeVersion++;
    }

    /// <summary>Испечь <see cref="Source"/> с параметрами формы в фоне; удачно — записать рецепт.</summary>
    public void BakeSource(object parameters)
    {
        if (Busy || Source is null || Recipe is null) return;
        string source = Source;
        var recipe = new Recipe { Converter = Recipe.Converter, Import = Recipe.Import, Notes = Recipe.Notes, Params = Sword.Bake.Recipe.WriteParams(parameters) };
        StartTask("выпечка " + Path.GetFileName(source), () =>
        {
            var log = new LineLog(line => Post(() => AddLog(LogKind.Info, line)));
            var written = Baker.Bake(source, recipe, log);
            log.Flush();
            Baker.SaveRecipe(source, recipe);
            Post(() =>
            {
                LastWritten = written;
                if (Source == source)
                {
                    Recipe = recipe;
                    RecipeExists = true;
                }
                Pack.Rescan();
                AddLog(LogKind.Done, $"испечено {Path.GetFileName(source)}: файлов {written.Count}; рецепт {Baker.RecipePath(source)}");
            });
        });
    }

    // ── пак ────────────────────────────────────────────────────────────

    public void ToggleIncluded(string path)
    {
        bool included = Pack.Under(path).Any(f => Pack.IsIncluded(f.Path));
        try
        {
            Pack.SetIncluded(path, !included);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddLog(LogKind.Failed, "pak.json не записан: " + e.Message);
        }
    }

    public void BuildPak()
    {
        if (Busy) return;
        StartTask("сборка пака", () =>
        {
            var result = Pack.Pack(progress: (done, total) =>
            {
                Volatile.Write(ref _taskTotal, total);
                Volatile.Write(ref _taskDone, done);
            });
            Post(() =>
            {
                LastPack = result;
                AddLog(LogKind.Done, ContentPack.Describe(result));
            });
        });
    }

    public void Rescan()
    {
        Pack.Rescan();
        AddLog(LogKind.Info, $"Content/ перечитан: файлов {Pack.Files.Count}");
    }

    private void StartTask(string title, Action work)
    {
        TaskTitle = title;
        _taskDone = _taskTotal = 0;
        AddLog(LogKind.Info, title + "…");
        _task = Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Post(() => AddLog(LogKind.Failed, $"{title}: {e.Message}"));
            }
            finally
            {
                _wake();
            }
        });
    }

    // ── кадр ───────────────────────────────────────────────────────────

    /// <summary>Разобрать вызовы фоновых задач и окон выбора. Зовётся раз в кадр на потоке окна.</summary>
    /// <returns>Что-то изменилось — кадр нужно перерисовать.</returns>
    public bool Pump()
    {
        bool any = false;
        while (_calls.TryDequeue(out var call))
        {
            call();
            any = true;
        }
        return any || Busy;
    }

    public void AddLog(LogKind kind, string text)
    {
        if (Log.Count >= LogLimit) Log.RemoveRange(0, LogLimit / 10);
        Log.Add(new LogEntry(DateTime.Now, kind, text));
        LogVersion++;
    }

    /// <summary>Системное окно выбора файла в фоне; <paramref name="done"/> — на потоке окна, если выбор сделан.</summary>
    public void Pick(string title, bool directory, string filter, string? near, Action<string[]> done, bool multiple = false)
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
                result = directory
                    ? NativeDialogs.PickFolder(_owner, title, folder) is { } dir ? [dir] : null
                    : NativeDialogs.OpenFiles(_owner, title, multiple, filter, folder);
            }
            catch (InvalidOperationException e) { error = e.Message; }
            Post(() =>
            {
                DialogOpen = false;
                if (error is not null) AddLog(LogKind.Failed, error);
                if (result is { Length: > 0 }) done(result);
            });
        });
    }

    private void Post(Action call)
    {
        _calls.Enqueue(call);
        _wake();
    }

    /// <summary>Дождаться начатой задачи: пак и рецепт не остаются недописанными при закрытии окна.</summary>
    public void Dispose() => _task?.Wait();

    /// <summary>TextWriter, отдающий законченные строки.</summary>
    private sealed class LineLog(Action<string> onLine) : TextWriter
    {
        private readonly System.Text.StringBuilder _line = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            string? done = null;
            lock (_line)
            {
                if (value == '\n')
                {
                    done = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                }
                else _line.Append(value);
            }
            if (done is not null) onLine(done);
        }

        public override void Flush()
        {
            string? rest = null;
            lock (_line)
                if (_line.Length > 0)
                {
                    rest = _line.ToString();
                    _line.Clear();
                }
            if (rest is not null) onLine(rest);
        }
    }
}
