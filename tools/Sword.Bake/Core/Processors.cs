using System.Reflection;
using Sword.Bake.Processors;

namespace Sword.Bake;

/// <summary>
/// Обработчик ассетов одного вида. Параметры — класс <see cref="ParamsType"/> (из поля params манифеста);
/// входы и выходы обработчик регистрирует через <see cref="BakeContext"/>, иначе проверка актуальности о них не узнает.
/// Общее ядро знает только встроенные обработчики (<see cref="ProcessorCatalog.BuiltIn"/>); обработчики проекта
/// приходят из его плагинов (<see cref="BakeProject"/>, поле plugins).
/// </summary>
public interface IAssetProcessor
{
    /// <summary>Значение поля processor манифеста: «font.vector».</summary>
    string Id { get; }
    /// <summary>Для людей: «Векторный шрифт интерфейса».</summary>
    string Title { get; }
    /// <summary>Поднимается, когда меняется смысл выхода без изменения кода сборки (формат, соглашение).</summary>
    int Version { get; }
    Type ParamsType { get; }
    /// <summary>
    /// Можно ли запекать два ассета этого обработчика одновременно. Нельзя, если у них общие выходы
    /// (скелет библиотеки анимаций пишут все источники).
    /// </summary>
    bool Parallel { get; }
    /// <summary>
    /// Обработчик переиспользует свои прежние выходы (не пересчитывает то, что не изменилось): перед запеканием
    /// выходы ассета из прежнего пака раскладываются в <see cref="BakeContext.OutputRoot"/>.
    /// </summary>
    bool Incremental { get; }
    /// <summary>Сборки, чей код определяет выход: их изменение пересобирает ассеты обработчика.</summary>
    IEnumerable<Assembly> CodeAssemblies { get; }
    /// <summary>Параметры нового ассета с ID <paramref name="assetId"/> — значения по умолчанию, пути выходов по ID.</summary>
    object NewParams(string assetId);
    void Process(BakeContext ctx, object parameters);

    /// <summary>
    /// Выходы ассета, известные до сборки, — по параметрам, без чтения исходников. По ним строится каталог контента
    /// и проверяется раскладка проекта; записанное при сборке должно в них укладываться. null — неизвестно до сборки.
    /// </summary>
    IReadOnlyList<DeclaredOutput>? DeclareOutputs(AssetManifest asset, object parameters) => null;
}

/// <summary>Выход, объявленный до сборки: файл пака или каталог (выходы — всё под ним).</summary>
public readonly record struct DeclaredOutput(string Path, bool IsDirectory)
{
    public static DeclaredOutput File(string path) => new(Sword.Engine.Content.ContentPath.Normalize(path), false);
    public static DeclaredOutput Directory(string path) => new(Sword.Engine.Content.ContentPath.Normalize(path), true);

    /// <summary>Покрывает ли объявление файл пака <paramref name="path"/>.</summary>
    public bool Covers(string path) =>
        IsDirectory ? path.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase) : path.Equals(Path, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => IsDirectory ? Path + "/" : Path;
}

public abstract class AssetProcessor<TParams> : IAssetProcessor where TParams : class, new()
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public virtual int Version => 1;
    public virtual bool Parallel => true;
    public virtual bool Incremental => false;
    public Type ParamsType => typeof(TParams);

    /// <summary>По умолчанию — сборка обработчика и сборки, на которые она ссылается напрямую (кроме платформы .NET).</summary>
    public virtual IEnumerable<Assembly> CodeAssemblies => ProcessorCatalog.WithReferences(GetType().Assembly, typeof(ProcessorCatalog).Assembly);

    public object NewParams(string assetId) => Defaults(assetId);

    protected virtual TParams Defaults(string assetId) => new();

    public void Process(BakeContext ctx, object parameters) => Process(ctx, (TParams)parameters);

    protected abstract void Process(BakeContext ctx, TParams p);

    public IReadOnlyList<DeclaredOutput>? DeclareOutputs(AssetManifest asset, object parameters) => Outputs(asset, (TParams)parameters);

    /// <summary>Выходы по параметрам (см. <see cref="IAssetProcessor.DeclareOutputs"/>); null — неизвестно до сборки.</summary>
    protected virtual IReadOnlyList<DeclaredOutput>? Outputs(AssetManifest asset, TParams p) => null;

    /// <summary>Последний сегмент ID ассета: «weapons/katana» → «katana».</summary>
    protected static string LastSegment(string assetId) => assetId[(assetId.LastIndexOf('/') + 1)..];
}

public enum SourceKind { File, Directory }

/// <summary>
/// Поле параметров — путь к исходнику: Studio показывает рядом кнопку выбора файла или каталога
/// и спрашивает его при добавлении ассета.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SourceAttribute(SourceKind kind) : Attribute
{
    public SourceKind Kind { get; } = kind;
    /// <summary>Маски файлов для окна выбора через «;»: «*.ttf;*.otf». Пусто — любые.</summary>
    public string Filter { get; init; } = "";
}

/// <summary>
/// Команда плагина для консоли: <c>sword-bake ИМЯ …</c> (диагностика форматов проекта и т. п.).
/// </summary>
public interface IBakeCommand
{
    string Name { get; }
    /// <summary>Одна строка для списка команд.</summary>
    string Summary { get; }
    /// <returns>Код выхода процесса.</returns>
    int Run(IReadOnlyList<string> args, TextWriter output);
}

/// <summary>Обработчики, команды и сравнители форматов (<see cref="IContentComparer"/>) проекта: встроенные и из плагинов.</summary>
public sealed class ProcessorCatalog
{
    private readonly List<IAssetProcessor> _processors = [];
    private readonly List<IBakeCommand> _commands = [];
    private readonly List<IContentComparer> _comparers = [];
    private readonly List<string> _errors = [];

    /// <summary>Обработчики общего ядра — годятся любому проекту.</summary>
    public static IReadOnlyList<IAssetProcessor> BuiltIn { get; } = [new FileCopyProcessor()];

    public IReadOnlyList<IAssetProcessor> Processors => _processors;
    public IReadOnlyList<IBakeCommand> Commands => _commands;
    /// <summary>Сравнители форматов для <c>diff</c>: у расширения — один (второй — ошибка в <see cref="Errors"/>).</summary>
    public IReadOnlyList<IContentComparer> Comparers => _comparers;
    /// <summary>Плагины, которые не загрузились, и конфликты ID (обработчиков, команд, расширений сравнителей).</summary>
    public IReadOnlyList<string> Errors => _errors;
    /// <summary>Загруженные плагины: путь из проекта → сборка.</summary>
    public IReadOnlyList<(string Path, Assembly Assembly)> Plugins { get; private set; } = [];

    public IAssetProcessor? Find(string id) => _processors.FirstOrDefault(p => p.Id == id);

    /// <summary>Добавить обработчик в обход плагинов — для тестов ядра.</summary>
    internal void Add(IAssetProcessor processor)
    {
        if (Find(processor.Id) is not null) throw new ArgumentException($"обработчик «{processor.Id}» уже есть");
        _processors.Add(processor);
    }

    /// <param name="plugins">
    /// Полные пути сборок-плагинов по порядку. Плагин, который ссылается на другой плагин списка, стоит после него: иначе —
    /// ошибка проекта, а обработчики этого плагина не регистрируются (как и при сбое загрузки его типов).
    /// </param>
    public static ProcessorCatalog Load(IEnumerable<string> plugins)
    {
        var c = new ProcessorCatalog();
        foreach (var p in BuiltIn) c._processors.Add(p);
        var loaded = new List<(string, Assembly)>();
        var paths = plugins.ToList();
        var names = paths.Select(AssemblyNameOf).ToList();
        for (int index = 0; index < paths.Count; index++)
        {
            string path = paths[index];
            Assembly asm;
            try
            {
                asm = PluginLoader.Load(path);
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                c._errors.Add($"плагин {path}: {e.Message}");
                continue;
            }
            loaded.Add((path, asm));
            var later = names.Skip(index + 1).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bases = asm.GetReferencedAssemblies().Select(a => a.Name).OfType<string>().Where(later.Contains).ToList();
            if (bases.Count > 0)
            {
                // основа, загруженная позже, работает лишь случайно (пока типы надстройки не нужны при загрузке) — не полагаться
                c._errors.Add($"плагин {path}: ссылается на плагин {string.Join(", ", bases)}, а стоит в plugins раньше него — " +
                              "поставьте основу раньше надстройки; обработчики плагина не загружены");
                continue;
            }
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                c._errors.Add($"плагин {path}: {string.Join("; ", e.LoaderExceptions.Select(x => x?.Message).Distinct())}");
                types = e.Types.Where(t => t is not null).ToArray()!;
            }
            foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
                         .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                bool processor = typeof(IAssetProcessor).IsAssignableFrom(type), command = typeof(IBakeCommand).IsAssignableFrom(type),
                    comparer = typeof(IContentComparer).IsAssignableFrom(type);
                if (!processor && !command && !comparer) continue;
                object instance;
                try
                {
                    instance = Activator.CreateInstance(type, nonPublic: true)!;
                }
                catch (Exception e) when (e is MissingMethodException or TargetInvocationException)
                {
                    c._errors.Add($"плагин {path}: {type.FullName} не создаётся — {e.InnerException?.Message ?? e.Message}");
                    continue;
                }
                if (instance is IAssetProcessor proc)
                {
                    if (c.Find(proc.Id) is { } existing)
                        c._errors.Add($"плагин {path}: обработчик «{proc.Id}» уже есть ({existing.GetType().FullName}) — {type.FullName} пропущен");
                    else c._processors.Add(proc);
                }
                if (instance is IBakeCommand cmd)
                {
                    if (c._commands.Any(x => x.Name == cmd.Name)) c._errors.Add($"плагин {path}: команда «{cmd.Name}» уже есть");
                    else c._commands.Add(cmd);
                }
                if (instance is IContentComparer cmp)
                {
                    var taken = c._comparers.SelectMany(x => x.Extensions.Intersect(cmp.Extensions, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (taken.Count > 0)
                        c._errors.Add($"плагин {path}: сравнитель для {string.Join(", ", taken)} уже есть — {type.FullName} пропущен");
                    else c._comparers.Add(cmp);
                }
            }
        }
        c.Plugins = loaded;
        return c;
    }

    /// <summary>Имя сборки плагина без загрузки; нет файла или не сборка — null (ошибку даст загрузка).</summary>
    private static string? AssemblyNameOf(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Name;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Сборка и сборки, на которые она ссылается напрямую, кроме платформы .NET. По ним считается отпечаток кода
    /// обработчика: сборка детерминирована, MVID меняется только вместе с кодом (и между Debug и Release).
    /// </summary>
    public static IEnumerable<Assembly> WithReferences(Assembly assembly, params Assembly[] except)
    {
        yield return assembly;
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            if (IsPlatform(name.Name)) continue;
            Assembly? a = null;
            try { a = Assembly.Load(name); }
            catch (Exception e) when (e is IOException or BadImageFormatException) { }
            if (a is not null && !except.Contains(a)) yield return a;
        }
    }

    private static bool IsPlatform(string? name) =>
        name is null || name is "mscorlib" or "netstandard" || name.StartsWith("System", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);
}
