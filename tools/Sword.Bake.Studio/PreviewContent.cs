using Sword.Engine.Content;

namespace Sword.Bake.Studio;

/// <summary>
/// Пак для предпросмотра (<see cref="IAssetPreview"/>): открывается при первом чтении и закрывается в конце кадра
/// (<see cref="Release"/>) — сборка в фоне свободно заменяет файл пака между кадрами. Поток <see cref="OpenRead"/> читает
/// прямо из пака и годен до конца кадра: обработчик дочитывает его сразу (заголовки клипов — без чтения поз).
/// </summary>
internal sealed class PreviewContent(string pakPath) : IContentSource, IDisposable
{
    private PakSource? _pak;

    public string Location => pakPath;

    private PakSource Pak => _pak ??= PakSource.Open(pakPath);

    public bool Exists(string path) => Pak.Exists(path);

    public Stream OpenRead(string path) => Pak.OpenRead(path);

    public byte[] ReadAllBytes(string path) => Pak.ReadAllBytes(path);

    public IReadOnlyList<string> List(string directory, string extension) => Pak.List(directory, extension);

    public string Describe(string path) => $"{pakPath}:{path}";

    /// <summary>Закрыть пак до следующего чтения.</summary>
    public void Release()
    {
        _pak?.Dispose();
        _pak = null;
    }

    public void Dispose() => Release();
}
