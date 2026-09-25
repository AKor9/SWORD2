using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Anim;

/// <summary>
/// Сравнитель <c>.swclip</c> для <c>sword-bake diff</c>: клипы равны, если совпадает всё, кроме
/// <see cref="ClipFile.SourceHash"/> (<see cref="ClipContent.Diff"/>): хеш исходника — сведения для инкрементальной
/// выпечки, у клипов из разных сборок пекарни он разный по устройству (в эталоне SWORD в него входил MVID конвертера).
/// Файл, который не разбирается как клип, — <see cref="InvalidDataException"/> (diff покажет расхождение с текстом).
/// </summary>
internal sealed class ClipComparer : IContentComparer
{
    public IReadOnlyList<string> Extensions { get; } = [ClipLibrary.ClipExt];
    public string Title => "клип: поля без SourceHash";

    public ContentComparison Compare(string path, byte[] a, byte[] b)
    {
        var x = ClipFile.Read(new MemoryStream(a, writable: false), path);
        var y = ClipFile.Read(new MemoryStream(b, writable: false), path);
        return ClipContent.Diff(x, y) is { } diff ? ContentComparison.Differs(diff) : ContentComparison.Same("без SourceHash");
    }
}
