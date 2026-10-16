using Sword.Engine.Content;

namespace Sword.Bake;

/// <summary>
/// Конвертер: сырой файл одного вида → файлы игровых форматов в <c>Content/&lt;тема&gt;/…</c>. Параметры — класс
/// <see cref="ParamsType"/> (поле params рецепта); значения по умолчанию для первой выпечки — <see cref="Defaults"/>.
/// </summary>
public interface IConverter
{
    /// <summary>Значение поля converter рецепта: «weapon».</summary>
    string Id { get; }
    /// <summary>Для людей: «Оружие (статический меш в кисти)».</summary>
    string Title { get; }
    /// <summary>Вид сырого файла для списка «какие файлы печём»: «*.glb, *.gltf».</summary>
    string Kind { get; }
    Type ParamsType { get; }
    /// <summary>Годится ли файл этому конвертеру — по имени и расширению, без чтения.</summary>
    bool Accepts(string file);
    /// <summary>Параметры первой выпечки файла.</summary>
    object Defaults(BakeJob job);
    void Bake(BakeJob job, object parameters);
}

public abstract class Converter<TParams> : IConverter where TParams : class, new()
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Kind { get; }
    public Type ParamsType => typeof(TParams);
    public abstract bool Accepts(string file);
    public object Defaults(BakeJob job) => Default(job);
    public void Bake(BakeJob job, object parameters) => Bake(job, (TParams)parameters);

    protected virtual TParams Default(BakeJob job) => new();
    protected abstract void Bake(BakeJob job, TParams p);

    protected static bool HasExtension(string file, params string[] extensions) =>
        extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Имя выхода по сырому файлу: «SM_Katana.glb» → «sm_katana».</summary>
    protected static string NameOf(string file) => Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
}

/// <summary>Поле параметров — путь к сырому файлу или каталогу: окно пекарни показывает рядом кнопку выбора.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SourceAttribute(bool directory = false) : Attribute
{
    public bool Directory { get; } = directory;
    /// <summary>Маски файлов для окна выбора через «;»: «*.ttf;*.otf». Пусто — любые.</summary>
    public string Filter { get; init; } = "";
}

/// <summary>
/// Выпечка одного сырого файла: пути исходников (<see cref="Input"/>, <see cref="InputDir"/>), выходы в <c>Content/</c>
/// (<see cref="Output"/>), лог и список записанного.
/// </summary>
public sealed class BakeJob(Baker baker, string source, TextWriter log)
{
    private readonly List<string> _written = [];

    public Baker Baker { get; } = baker;
    /// <summary>Сырой файл — полный путь.</summary>
    public string Source { get; } = Path.GetFullPath(source);
    public TextWriter Log { get; } = log;
    /// <summary>Записанные файлы — пути в <c>Content/</c> через «/».</summary>
    public IReadOnlyList<string> Written { get { lock (_written) return [.. _written]; } }

    /// <summary>Путь рецепта (<c>${корень}/…</c> или полный) → полный путь файла.</summary>
    /// <exception cref="FileNotFoundException">Нет файла.</exception>
    public string Input(string path)
    {
        string full = Baker.Roots.Expand(path);
        return File.Exists(full) ? full : throw new FileNotFoundException($"нет исходника {full} ({path})", full);
    }

    /// <exception cref="DirectoryNotFoundException">Нет каталога.</exception>
    public string InputDir(string path)
    {
        string full = Baker.Roots.Expand(path);
        return Directory.Exists(full) ? full : throw new DirectoryNotFoundException($"нет каталога исходников {full} ({path})");
    }

    /// <summary>Сырой файл для поля рецепта: <c>${корень}/…</c>.</summary>
    public string Contract(string fullPath) => Baker.Roots.Contract(fullPath);

    /// <summary>Файл выхода — путь в <c>Content/</c> через «/»: полный путь, каталог создан.</summary>
    public string Output(string contentPath)
    {
        string rel = ContentPath.Normalize(contentPath);
        string full = Path.Combine(Baker.ContentDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        lock (_written) if (!_written.Contains(rel, StringComparer.OrdinalIgnoreCase)) _written.Add(rel);
        return full;
    }
}
