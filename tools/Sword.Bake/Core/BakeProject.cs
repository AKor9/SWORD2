using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sword.Bake;

/// <summary>
/// Проект пекарни — каталог с файлом <see cref="FileName"/>; в репозитории игры это обычно подкаталог <see cref="SubdirName"/>
/// в корне, где собрано всё про сборку контента:
/// <code>
/// {
///   "name": "SWORD",
///   "assets": ".",                      каталог манифестов *.bake.json от файла проекта (по умолчанию — рядом с ним)
///   "pak": "Content.swpak",             собираемый пак (по умолчанию &lt;name&gt;.swpak)
///   "roots": { "src": "…" },            необязательные корни исходников (см. <see cref="SourceRoots"/>)
///   "plugins": [ "…/Plugin.dll" ]       сборки с обработчиками проекта; ${config} — Debug или Release хоста
///   "strictLayout": true                нарушение раскладки (выход вне папки манифеста, общий файл) — ошибка ассета
/// }
/// </code>
/// Рядом — служебный каталог <see cref="StateDirName"/> (вне git): локальный кэш проверок и копии удалённых манифестов.
/// Готового контента проект не хранит: результат сборки — только пак.
/// </summary>
public sealed class BakeProject
{
    public const string FileName = "bake.project.json";
    public const string StateDirName = ".bake";
    public const string DefaultAssetsDir = ".";
    /// <summary>Подкаталог, в котором проект ищется, если файла проекта нет в самом каталоге: <c>&lt;репозиторий&gt;/Bake</c>.</summary>
    public const string SubdirName = "Bake";

    public string Root { get; }
    public string ProjectFile => Path.Combine(Root, FileName);
    public string StateDir => Path.Combine(Root, StateDirName);
    public string Name { get; private set; } = "";
    public string AssetsDir { get; private set; } = "";
    public string PakPath { get; private set; } = "";
    public SourceRoots Roots { get; private set; }
    /// <summary>Обработчики загружаются при открытии проекта: сборку в процессе не выгрузить, новый список плагинов — после перезапуска.</summary>
    public ProcessorCatalog Processors { get; }
    public IReadOnlyList<string> PluginPaths { get; private set; } = [];
    /// <summary>
    /// Строгая раскладка (поле strictLayout): нарушение раскладки (<see cref="ContentCatalog.Issues"/>) — ошибка ассета,
    /// а не предупреждение.
    /// </summary>
    public bool StrictLayout { get; private set; }
    private readonly IReadOnlyList<string> _loadedPlugins;
    public IReadOnlyList<AssetManifest> Assets { get; private set; } = [];
    /// <summary>Манифесты, которые не разобрались: полный путь файла и причина (без пути в тексте).</summary>
    public IReadOnlyList<(string File, string Error)> Broken { get; private set; } = [];
    /// <summary>Реестр импортов (<see cref="ImportRegistry"/>): ID → происхождение.</summary>
    public IReadOnlyDictionary<string, ImportRecord> Imports { get; private set; } = new Dictionary<string, ImportRecord>();
    /// <summary>Проблемы самого проекта: плагины, локальные корни.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    private sealed class Shape
    {
        public string? Name { get; set; }
        public string? Notes { get; set; }
        public string? Assets { get; set; }
        public string? Pak { get; set; }
        public Dictionary<string, string>? Roots { get; set; }
        public List<string>? Plugins { get; set; }
        public bool? StrictLayout { get; set; }
    }

    private BakeProject(string root)
    {
        Root = Path.GetFullPath(root);
        var shape = ReadShape();
        Apply(shape);
        Roots = SourceRoots.Empty(Root);   // настоящие корни читает Reload: испорченный локальный файл — проблема проекта, а не отказ открыть
        _loadedPlugins = PluginPaths;
        Processors = ProcessorCatalog.Load(PluginPaths);
    }

    /// <summary>
    /// Ближайший вверх от <paramref name="start"/> каталог с <see cref="FileName"/> — в нём самом или в его подкаталоге
    /// <see cref="SubdirName"/>; <paramref name="start"/> может быть и самим файлом.
    /// </summary>
    public static string? FindRoot(string start)
    {
        start = Path.GetFullPath(start);
        if (File.Exists(start) && Path.GetFileName(start).Equals(FileName, StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(start);
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, FileName))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, SubdirName, FileName))) return Path.Combine(dir.FullName, SubdirName);
        }
        return null;
    }

    /// <summary>Проект над <paramref name="start"/> (по умолчанию — текущий каталог).</summary>
    /// <exception cref="DirectoryNotFoundException">Проекта нет.</exception>
    /// <exception cref="InvalidDataException">Файл проекта не разбирается.</exception>
    public static BakeProject Open(string? start = null)
    {
        string from = start ?? Directory.GetCurrentDirectory();
        string root = FindRoot(from) ?? throw new DirectoryNotFoundException($"нет проекта пекарни ({FileName}) в {Path.GetFullPath(from)} и выше — создать: sword-bake init");
        var p = new BakeProject(root);
        p.Reload();
        return p;
    }

    /// <summary>Создать проект в <paramref name="dir"/>: файл проекта и пустой каталог ассетов.</summary>
    /// <exception cref="IOException">Проект уже есть.</exception>
    public static BakeProject Create(string dir, string? name = null)
    {
        dir = Path.GetFullPath(dir);
        string file = Path.Combine(dir, FileName);
        if (File.Exists(file)) throw new IOException($"проект уже есть: {file}");
        name ??= new DirectoryInfo(dir).Name;
        TextFiles.WriteJson(file, new JsonObject
        {
            ["name"] = name,
            ["assets"] = DefaultAssetsDir,
            ["pak"] = SafeFileName(name) + Sword.Engine.Content.PakFormat.Extension,
            ["plugins"] = new JsonArray(),
        });
        Directory.CreateDirectory(Path.Combine(dir, DefaultAssetsDir));
        return Open(dir);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string s = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return s.Trim().Length == 0 ? "content" : s.Trim();
    }

    private Shape ReadShape()
    {
        try
        {
            return JsonSerializer.Deserialize<Shape>(File.ReadAllText(ProjectFile), AssetManifest.Json)
                   ?? throw new InvalidDataException($"{ProjectFile}: пустой файл проекта");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{ProjectFile}: {e.Message}", e);
        }
    }

    private void Apply(Shape shape)
    {
        Name = shape.Name is { Length: > 0 } n ? n : new DirectoryInfo(Root).Name;
        StrictLayout = shape.StrictLayout ?? false;
        AssetsDir = Path.GetFullPath(Path.Combine(Root, shape.Assets is { Length: > 0 } a ? a : DefaultAssetsDir));
        PakPath = Path.GetFullPath(Path.Combine(Root, shape.Pak is { Length: > 0 } pak ? pak : SafeFileName(Name) + Sword.Engine.Content.PakFormat.Extension));
        string config = PluginLoader.HostConfiguration;
        PluginPaths = (shape.Plugins ?? [])
            .Select(p => Path.GetFullPath(Path.Combine(Root, p.Replace("${config}", config, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Перечитать файл проекта, локальные корни и манифесты. Новые плагины подключаются только при следующем открытии.</summary>
    public void Reload()
    {
        var problems = new List<string>();
        var shape = ReadShape();
        Apply(shape);
        if (!PluginPaths.SequenceEqual(_loadedPlugins, StringComparer.OrdinalIgnoreCase))
            problems.Add("список плагинов изменился — откройте проект заново, чтобы загрузить обработчики");
        try
        {
            Roots = SourceRoots.Load(Root, shape.Roots);
        }
        catch (InvalidDataException e)
        {
            Roots = SourceRoots.Empty(Root);
            problems.Add(e.Message);
        }
        try
        {
            Imports = ImportRegistry.Load(Root);
        }
        catch (InvalidDataException e)
        {
            Imports = new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
            problems.Add(e.Message);
        }
        problems.AddRange(Processors.Errors);

        var assets = new List<AssetManifest>();
        var broken = new List<(string, string)>();
        if (Directory.Exists(AssetsDir))
            foreach (var file in Directory.GetFiles(AssetsDir, "*" + AssetManifest.Extension, SearchOption.AllDirectories)
                         .Where(file => !file.StartsWith(StateDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))   // копии удалённых манифестов
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                try { assets.Add(AssetManifest.Load(file, AssetsDir)); }
                catch (InvalidDataException e)
                {
                    // путь хранится отдельным полем: сообщение разбора уже начинается с него, и показывать
                    // его вторым разом не нужно — «файл: файл: ошибка» читалось как сбой самого сообщения
                    string prefix = file + ": ";
                    broken.Add((file, e.Message.StartsWith(prefix, StringComparison.Ordinal) ? e.Message[prefix.Length..] : e.Message));
                }
            }
        Assets = assets;
        Broken = broken;
        Problems = problems;
    }

    public AssetManifest? Find(string id) => Assets.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Корни, упомянутые в манифестах, но не заданные.</summary>
    public IReadOnlyList<string> MissingRoots() =>
        Assets.SelectMany(a => SourceRoots.Mentioned(a.Params.ValueKind == JsonValueKind.Undefined ? "" : a.Params.GetRawText()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !Roots.Roots.ContainsKey(n))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Ассеты по шаблонам: точный ID, папка («anim» — всё под anim/), маска со «*», «import:ИМЯ», «processor:ID».
    /// Шаблон, не совпавший ни с одним ID, ищется среди ассетов контента по объявленным выходам — берутся их
    /// производители: «anim/katana» выберет единицу, которая пишет этот набор. Пусто — все.
    /// </summary>
    /// <exception cref="ArgumentException">Шаблон ничего не выбрал.</exception>
    public IReadOnlyList<AssetManifest> Select(IReadOnlyCollection<string> patterns)
    {
        if (patterns.Count == 0) return Assets;
        var result = new List<AssetManifest>();
        foreach (var pattern in patterns)
        {
            var hits = pattern.StartsWith("import:", StringComparison.OrdinalIgnoreCase)
                ? Assets.Where(a => a.Import.Equals(pattern["import:".Length..], StringComparison.OrdinalIgnoreCase)).ToList()
                : pattern.StartsWith("processor:", StringComparison.OrdinalIgnoreCase)
                    ? Assets.Where(a => a.Processor.Equals(pattern["processor:".Length..], StringComparison.OrdinalIgnoreCase)).ToList()
                    : Assets.Where(a => Matches(a.Id, pattern)).ToList();
            if (hits.Count == 0 && !pattern.Contains(':') && ContentCatalog.Build(this, null).Find(pattern) is { } content)
                hits = content.Producers.Select(Find).OfType<AssetManifest>().ToList();
            if (hits.Count == 0)
                throw new ArgumentException($"нет ассетов по «{pattern}»; есть: {string.Join(", ", Assets.Select(a => a.Id))}");
            foreach (var a in hits)
                if (!result.Contains(a)) result.Add(a);
        }
        return result;
    }

    private bool Matches(string id, string pattern)
    {
        string p = pattern.Replace('\\', '/').Trim('/');
        if (p.EndsWith(AssetManifest.Extension, StringComparison.OrdinalIgnoreCase)) p = p[..^AssetManifest.Extension.Length];
        string assetsPrefix = Path.GetFileName(AssetsDir) + "/";
        if (p.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase)) p = p[assetsPrefix.Length..];
        if (p.Contains('*'))
            return Regex.IsMatch(id, "^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return id.Equals(p, StringComparison.OrdinalIgnoreCase) || id.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
    }

    // ── ассеты ─────────────────────────────────────────────────────────

    /// <summary>ID ассета: сегменты через «/» из a-z, 0-9, _ . -, без пустых и «.»/«..».</summary>
    public static bool IsValidAssetId(string id) =>
        id.Length > 0 && id.Split('/').All(s => Regex.IsMatch(s, "^[a-z0-9_][a-z0-9_.-]*$") && s is not ("." or "..") && !s.EndsWith('.'));

    public string ManifestPath(string id) => Path.Combine(AssetsDir, id.Replace('/', Path.DirectorySeparatorChar) + AssetManifest.Extension);

    /// <summary>
    /// Добавить ассет: манифест с обработчиком и параметрами. Пути исходников в <paramref name="sources"/> (имя поля
    /// параметров → полный путь) записываются через <see cref="SourceRoots.Contract"/>; сами файлы не копируются.
    /// </summary>
    /// <exception cref="ArgumentException">Неверный ID, неизвестный обработчик или поле.</exception>
    /// <exception cref="IOException">Такой ассет уже есть.</exception>
    public AssetManifest AddAsset(string id, string processorId, IReadOnlyDictionary<string, string>? sources = null, string notes = "")
    {
        if (!IsValidAssetId(id)) throw new ArgumentException($"ID «{id}»: сегменты через «/» из a-z, 0-9, _ . -");
        if (Find(id) is not null) throw new IOException($"ассет {id} уже есть");
        var processor = Processors.Find(processorId) ?? throw new ArgumentException($"неизвестный обработчик «{processorId}»");
        var parameters = processor.NewParams(id);
        foreach (var (field, path) in sources ?? new Dictionary<string, string>())
        {
            var prop = processor.ParamsType.GetProperties().FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"у обработчика {processorId} нет поля «{field}»");
            if (prop.PropertyType == typeof(string)) prop.SetValue(parameters, path.Length == 0 ? null : Roots.Contract(path));
            else if (prop.PropertyType == typeof(List<string>))
                prop.SetValue(parameters, path.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Roots.Contract).ToList());
            else throw new ArgumentException($"поле «{field}» обработчика {processorId} — не путь");
        }
        ManifestWriter.Create(ManifestPath(id), processor, parameters, notes);
        Reload();
        return Find(id) ?? throw new InvalidDataException($"манифест {ManifestPath(id)} записан, но не разобрался");
    }

    /// <summary>
    /// Исходники, заданные в параметрах ассета (поля с <see cref="SourceAttribute"/>): значение, полный путь и что с ним
    /// не так. Видны и до первой сборки, в отличие от входов, которые обработчик регистрирует сам.
    /// </summary>
    public IReadOnlyList<SourceField> SourcesOf(AssetManifest a)
    {
        var processor = Processors.Find(a.Processor);
        if (processor is null) return [];
        object parameters;
        try { parameters = a.ReadParams(processor); }
        catch (InvalidDataException) { return []; }
        var result = new List<SourceField>();
        foreach (var prop in processor.ParamsType.GetProperties())
        {
            if (prop.GetCustomAttributes(typeof(SourceAttribute), true).FirstOrDefault() is not SourceAttribute attr) continue;
            var values = prop.GetValue(parameters) switch
            {
                string s => [s],
                IEnumerable<string> list => list.ToList(),
                _ => new List<string>(),
            };
            string name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            foreach (var value in values.Where(v => v.Length > 0))
            {
                string? full = null, problem = null;
                try
                {
                    full = Roots.Expand(value);
                    bool exists = attr.Kind == SourceKind.File ? File.Exists(full) : Directory.Exists(full);
                    if (!exists) problem = attr.Kind == SourceKind.File ? "нет файла" : "нет каталога";
                }
                catch (ArgumentException e)
                {
                    problem = e.Message;
                }
                result.Add(new SourceField(name, attr.Kind, value, full, problem));
            }
        }
        return result;
    }

    /// <summary>
    /// Ссылки единицы на другие (поля параметров с <see cref="AssetRefAttribute"/>): цель и что с ней не так —
    /// нет такого ассета, не тот обработчик, ссылка на себя. Параметры не разбираются — ссылок нет.
    /// </summary>
    public IReadOnlyList<AssetReference> ReferencesOf(AssetManifest a)
    {
        if (Processors.Find(a.Processor) is not { } processor) return [];
        object parameters;
        try { parameters = a.ReadParams(processor); }
        catch (InvalidDataException) { return []; }
        var result = new List<AssetReference>();
        foreach (var prop in processor.ParamsType.GetProperties())
        {
            if (prop.GetCustomAttributes(typeof(AssetRefAttribute), true).FirstOrDefault() is not AssetRefAttribute attr) continue;
            if (prop.GetValue(parameters) is not string { Length: > 0 } id) continue;
            string field = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            var target = Find(id);
            string? problem = target is null ? $"нет ассета «{id}»"
                : target == a ? "ссылка на себя"
                : attr.Processors.Count > 0 && !attr.Processors.Contains(target.Processor)
                    ? $"у {id} обработчик {target.Processor}, а нужен {string.Join(" или ", attr.Processors)}"
                    : null;
            result.Add(new AssetReference(field, id, target, problem));
        }
        return result;
    }

    /// <summary>
    /// Замечания о происхождении: у единицы нет импорта или он неизвестен, у импорта нет файла лицензии.
    /// При <see cref="StrictLayout"/> замечания с единицей — ошибки её сборки.
    /// </summary>
    public IReadOnlyList<ProvenanceIssue> ProvenanceIssues()
    {
        var result = new List<ProvenanceIssue>();
        foreach (var a in Assets)
            if (a.Import.Length == 0) result.Add(new ProvenanceIssue(a.Id, "происхождение не указано (поле import)"));
            else if (!Imports.ContainsKey(a.Import)) result.Add(new ProvenanceIssue(a.Id, $"неизвестный импорт «{a.Import}» — его нет в {ImportRegistry.FileName}"));
        foreach (var (id, record) in Imports.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var file in record.LicenseFiles)
            {
                string? problem = null;
                try
                {
                    if (!File.Exists(Roots.Expand(file))) problem = $"нет файла лицензии {file}";
                }
                catch (ArgumentException e)
                {
                    problem = e.Message;
                }
                if (problem is not null) result.Add(new ProvenanceIssue(null, $"импорт {id}: {problem}"));
            }
        return result;
    }

    /// <summary>Удалить манифест ассета; копия остаётся в <c>.bake/removed</c>. Пак обновит следующая сборка.</summary>
    /// <returns>Путь копии.</returns>
    public string RemoveAsset(AssetManifest asset)
    {
        string dir = Path.Combine(StateDir, "removed");
        Directory.CreateDirectory(dir);
        string backup = Path.Combine(dir, $"{asset.Id.Replace('/', '-')}-{DateTime.Now:yyyyMMdd-HHmmss}{AssetManifest.Extension}");
        File.Copy(asset.File, backup, overwrite: true);
        File.Delete(asset.File);
        Reload();
        return backup;
    }
}

/// <summary>Путь к исходнику из параметров ассета: поле, вид, значение из манифеста, полный путь и проблема (null — в порядке).</summary>
public sealed record SourceField(string Field, SourceKind Kind, string Value, string? FullPath, string? Problem);
