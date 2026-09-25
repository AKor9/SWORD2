using System.Text;
using Sword.Engine.Content;

namespace Sword.Bake;

/// <summary>
/// Контекст запекания одного ассета. Всё, что обработчик читает, идёт через <see cref="InputFile"/>/<see cref="InputDir"/>
/// (исходники) и <see cref="ContentFile"/> (готовые файлы других ассетов), всё, что пишет, — через
/// <see cref="Output"/>/<see cref="OutputDir"/>: по этим спискам сборка решает, что положить в пак и когда ассет устарел.
/// Выходы пишутся во временный каталог <see cref="OutputRoot"/>, который живёт только до конца сборки.
/// </summary>
public sealed class BakeContext
{
    private readonly object _lock = new();
    private readonly List<string> _inputFiles = [], _inputDirs = [], _contentInputs = [], _outputFiles = [], _outputDirs = [], _missing = [];
    private readonly Action<string> _warn;
    private readonly Func<string, Stream?> _readContent;
    private readonly string _contentInDir;

    internal BakeContext(BakeProject project, AssetManifest asset, string workDir, Func<string, Stream?> readContent,
        TextWriter log, Action<string> warn, CancellationToken cancellation)
    {
        Project = project;
        Asset = asset;
        Log = log;
        _warn = warn;
        _readContent = readContent;
        Cancellation = cancellation;
        OutputRoot = Path.Combine(workDir, "out");
        _contentInDir = Path.Combine(workDir, "in");
        Directory.CreateDirectory(OutputRoot);
    }

    public BakeProject Project { get; }
    public AssetManifest Asset { get; }
    /// <summary>Построчный лог; строки уходят событиями сборки.</summary>
    public TextWriter Log { get; }
    public CancellationToken Cancellation { get; }
    /// <summary>
    /// Временный каталог выходов ассета: пути под ним — пути в паке. У обработчика с
    /// <see cref="IAssetProcessor.Incremental"/> здесь до запекания лежат выходы ассета из прежнего пака.
    /// </summary>
    public string OutputRoot { get; }

    public void Warn(string text) => _warn(text);

    /// <summary>Путь манифеста (<c>${корень}/…</c>, относительный — от корня проекта) → полный, без регистрации.</summary>
    public string Source(string path) => Project.Roots.Expand(path);

    /// <summary>
    /// Параметры единицы, на которую ссылается поле с <see cref="AssetRefAttribute"/>. Её исходники обработчик
    /// регистрирует сам (<see cref="InputFile"/>, <see cref="InputDir"/>) — так они становятся входами этого ассета.
    /// </summary>
    /// <exception cref="ArgumentException">Нет такой единицы или её параметры другого типа.</exception>
    /// <exception cref="InvalidDataException">Параметры цели не разбираются.</exception>
    public T Ref<T>(string id) where T : class
    {
        var target = Project.Find(id) ?? throw new ArgumentException($"ссылка на «{id}»: нет такого ассета");
        var processor = Project.Processors.Find(target.Processor)
                        ?? throw new ArgumentException($"ссылка на {id}: неизвестный обработчик «{target.Processor}»");
        if (processor.ParamsType != typeof(T))
            throw new ArgumentException($"ссылка на {id}: у него обработчик {processor.Id}, параметры которого не {typeof(T).Name}");
        return (T)target.ReadParams(processor);
    }

    /// <exception cref="FileNotFoundException">Нет файла.</exception>
    public string InputFile(string path)
    {
        string full = Source(path);
        if (!File.Exists(full))
        {
            lock (_lock) _missing.Add(full);
            throw new FileNotFoundException($"нет исходника {full} ({path})", full);
        }
        lock (_lock) if (!_inputFiles.Contains(full, StringComparer.OrdinalIgnoreCase)) _inputFiles.Add(full);
        return full;
    }

    /// <summary>Каталог-вход: все файлы под ним, рекурсивно; добавленный или удалённый файл — ассет устарел.</summary>
    /// <exception cref="DirectoryNotFoundException">Нет каталога.</exception>
    public string InputDir(string path)
    {
        string full = Source(path);
        if (!Directory.Exists(full))
        {
            lock (_lock) _missing.Add(full);
            throw new DirectoryNotFoundException($"нет каталога исходников {full} ({path})");
        }
        lock (_lock) if (!_inputDirs.Contains(full, StringComparer.OrdinalIgnoreCase)) _inputDirs.Add(full);
        return full;
    }

    /// <summary>
    /// Готовый файл другого ассета (путь в паке) как локальный файл. Ассет-владелец становится зависимостью:
    /// при первой сборке порядок задаёт dependsOn, дальше — эта регистрация.
    /// </summary>
    /// <exception cref="FileNotFoundException">Такого файла нет ни в этой сборке, ни в прежнем паке.</exception>
    public string ContentFile(string contentPath)
    {
        string rel = ContentPath.Normalize(contentPath);
        string full = Path.Combine(_contentInDir, rel.Replace('/', Path.DirectorySeparatorChar));
        lock (_lock)
        {
            if (!File.Exists(full))
            {
                using var source = _readContent(rel)
                    ?? throw new FileNotFoundException($"нет готового файла {rel}: его пишет ассет, который должен собираться раньше (dependsOn)", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var file = File.Create(full);
                source.CopyTo(file);
            }
            if (!_contentInputs.Contains(rel, StringComparer.OrdinalIgnoreCase)) _contentInputs.Add(rel);
        }
        return full;
    }

    /// <summary>Файл выхода (путь в паке через «/»): полный путь во временном каталоге, каталог создан.</summary>
    public string Output(string contentPath)
    {
        string rel = Normalize(contentPath);
        string full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        lock (_lock) if (!_outputFiles.Contains(rel)) _outputFiles.Add(rel);
        return full;
    }

    /// <summary>Каталог выхода: выходы — все файлы под ним после запекания.</summary>
    public string OutputDir(string contentPath)
    {
        string rel = Normalize(contentPath);
        string full = Full(rel);
        Directory.CreateDirectory(full);
        lock (_lock) if (!_outputDirs.Contains(rel)) _outputDirs.Add(rel);
        return full;
    }

    private string Full(string rel) => Path.Combine(OutputRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Normalize(string contentPath)
    {
        string rel = ContentPath.Normalize(contentPath);
        if (rel.StartsWith(BakeProject.StateDirName + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"выход «{contentPath}»: каталог {BakeProject.StateDirName} в паке — служебный");
        return rel;
    }

    internal IReadOnlyList<string> InputFiles { get { lock (_lock) return [.. _inputFiles]; } }
    internal IReadOnlyList<string> InputDirs { get { lock (_lock) return [.. _inputDirs]; } }
    /// <summary>Исходники, которых не оказалось на месте: их появление снимает ошибку сборки.</summary>
    internal IReadOnlyList<string> Missing { get { lock (_lock) return [.. _missing]; } }

    /// <summary>Объявленные выходы: путь в паке → файл.</summary>
    /// <exception cref="InvalidDataException">Обработчик объявил выход, но не записал его.</exception>
    internal IReadOnlyList<(string Path, string Full)> Outputs(string processor)
    {
        var outputs = new List<string>(_outputFiles);
        foreach (var d in _outputDirs)
            foreach (var f in ListDir(Full(d)))
            {
                string rel = Path.GetRelativePath(OutputRoot, f).Replace(Path.DirectorySeparatorChar, '/');
                if (!outputs.Contains(rel, StringComparer.OrdinalIgnoreCase)) outputs.Add(rel);
            }
        var result = new List<(string, string)>(outputs.Count);
        foreach (var rel in outputs)
        {
            string full = Full(rel);
            if (!File.Exists(full)) throw new InvalidDataException($"обработчик {processor} не записал объявленный выход {rel}");
            result.Add((rel, full));
        }
        return result;
    }

    /// <summary>Запись о сборке ассета по зарегистрированным входам; выходы заполняет сборка пака.</summary>
    internal PakAssetRecord Record(string processor, string fingerprint, string paramsHash, LocalCache cache)
    {
        var rec = new PakAssetRecord
        {
            Processor = processor, Fingerprint = fingerprint, ParamsHash = paramsHash, BakedUtc = DateTime.UtcNow,
            InputDirs = [.. _inputDirs], ContentInputs = [.. _contentInputs],
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _inputFiles.Concat(_inputDirs.SelectMany(ListDir)))
            if (seen.Add(f)) rec.Inputs.Add(cache.Describe(f));
        return rec;
    }

    /// <summary>Файлы под каталогом, рекурсивно, без временных *.tmp; по возрастанию.</summary>
    public static IEnumerable<string> ListDir(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];
}

/// <summary>TextWriter, отдающий законченные строки.</summary>
internal sealed class LineWriter(Action<string> onLine) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

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

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (char c in value) Write(c);
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
