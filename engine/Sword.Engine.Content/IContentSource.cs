namespace Sword.Engine.Content;

/// <summary>
/// Запечённый контент: пак (<see cref="PakSource"/>) у игры, каталог (<see cref="DirectorySource"/>) у инструментов
/// и тестов.
/// Пути — относительные, через «/» (<see cref="ContentPath"/>), одинаковые для всех вариантов.
/// </summary>
public interface IContentSource
{
    /// <summary>Каталог или файл пака — для сообщений.</summary>
    string Location { get; }

    bool Exists(string path);

    /// <exception cref="FileNotFoundException">Нет такого файла.</exception>
    Stream OpenRead(string path);

    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Файлы с расширением <paramref name="extension"/> под каталогом <paramref name="directory"/>, рекурсивно;
    /// пути от корня контента, по возрастанию без учёта регистра. Нет каталога — пусто.
    /// </summary>
    IReadOnlyList<string> List(string directory, string extension);

    /// <summary>Путь для сообщений об ошибках: полный путь файла или «пак:путь».</summary>
    string Describe(string path);
}

/// <summary>
/// Пути внутри контента: «/», без ведущего «/», без «.» и «..». Путь контента не выходит за пределы контента: диск
/// («C:»), альтернативный поток NTFS (любое «:»), управляющие и запрещённые в именах Windows символы отвергаются —
/// иначе <c>Path.Combine(корень, "C:/…")</c> молча отбросил бы корень.
/// </summary>
public static class ContentPath
{
    /// <exception cref="ArgumentException">Пустой сегмент, «.», «..» или недопустимый символ.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string p = path.Replace('\\', '/').Trim('/');
        foreach (var part in p.Split('/'))
        {
            if (part is "" or "." or "..") throw new ArgumentException($"путь контента «{path}»: пустой сегмент, «.» или «..»");
            foreach (char c in part)
                if (c == ':' || char.IsControl(c) || InvalidChars.Contains(c))
                    throw new ArgumentException($"путь контента «{path}»: недопустимый символ U+{(int)c:X4}");
        }
        return p;
    }

    /// <summary>Запрещённые в именах файлов Windows (кроме «:» и управляющих) — на всех системах: пак переносим.</summary>
    private const string InvalidChars = "<>\"|?*";

    public static string Combine(params string[] parts) => Normalize(string.Join('/', parts));
}

/// <summary>Контент россыпью в каталоге.</summary>
public sealed class DirectorySource(string root) : IContentSource
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Location => Root;

    /// <summary>Полный путь файла контента — всегда внутри <see cref="Root"/>.</summary>
    /// <exception cref="ArgumentException">Не путь контента или путь за пределами <see cref="Root"/>.</exception>
    public string FullPath(string path)
    {
        string full = Path.GetFullPath(Path.Combine(Root, ContentPath.Normalize(path).Replace('/', Path.DirectorySeparatorChar)));
        // Второй рубеж после Normalize: итог обязан лежать под корнем. Через относительный путь, а не сравнение
        // префиксов: у корня диска («C:\») разделитель уже в конце, и префикс «корень + разделитель» его бы не нашёл.
        string relative = Path.GetRelativePath(Root, full);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"путь контента «{path}» выходит за {Root}");
        return full;
    }

    public bool Exists(string path) => File.Exists(FullPath(path));

    public Stream OpenRead(string path)
    {
        string full = FullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"нет файла контента {full}", full);
        return File.OpenRead(full);
    }

    public byte[] ReadAllBytes(string path)
    {
        using var s = OpenRead(path);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    public IReadOnlyList<string> List(string directory, string extension)
    {
        string dir = directory.Length == 0 ? Root : FullPath(directory);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*" + extension, SearchOption.AllDirectories)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))   // «*.ext» у Windows ловит и «.extra»
            .Select(f => Path.GetRelativePath(Root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Describe(string path) => FullPath(path);
}
