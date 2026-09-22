namespace Sword.Engine.Content;

/// <summary>
/// Контент в памяти: путь → байты (<c>MemorySource</c> SWORD). Тесты и сгенерированный контент — без диска и пака;
/// пути и поиск — как у пака (<see cref="ContentPath"/>, без учёта регистра).
/// </summary>
public sealed class MemorySource(string location = "память") : IContentSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Location { get; } = location;

    /// <summary>Положить файл (заменить, если есть).</summary>
    public MemorySource Add(string path, byte[] bytes)
    {
        _files[ContentPath.Normalize(path)] = bytes;
        return this;
    }

    /// <summary>Положить файл, записанный в поток <paramref name="write"/>.</summary>
    public MemorySource Add(string path, Action<Stream> write)
    {
        using var s = new MemoryStream();
        write(s);
        return Add(path, s.ToArray());
    }

    public bool Exists(string path) => _files.ContainsKey(ContentPath.Normalize(path));

    public Stream OpenRead(string path) =>
        _files.TryGetValue(ContentPath.Normalize(path), out var bytes)
            ? new MemoryStream(bytes, writable: false)
            : throw new FileNotFoundException($"нет файла контента {Describe(path)}", path);

    public byte[] ReadAllBytes(string path)
    {
        using var s = OpenRead(path);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    public IReadOnlyList<string> List(string directory, string extension)
    {
        string dir = directory.Length == 0 ? "" : ContentPath.Normalize(directory) + "/";
        return _files.Keys
            .Where(p => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Describe(string path) => $"{Location}:{path}";
}
