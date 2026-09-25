using System.Text;

namespace Sword.Bake.Tests.PluginA;

/// <summary>
/// Сравнитель формата <c>.skel</c> для <c>sword-bake diff</c> (как сравнитель <c>.swclip</c> в Bake.Engine): строки
/// <c>source:…</c> — отпечаток исходника, не содержимое (как <c>SourceHash</c> клипа), остальные строки должны совпасть.
/// Файл не со строки <c>skeleton:</c> — не разбирается.
/// </summary>
public sealed class SkeletonComparer : IContentComparer
{
    public const string Prefix = "source:";

    public IReadOnlyList<string> Extensions { get; } = [".skel"];
    public string Title => "скелет: без строк source:";

    public ContentComparison Compare(string path, byte[] a, byte[] b)
    {
        var x = Lines(path, a);
        var y = Lines(path, b);
        for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            string? l = i < x.Count ? x[i] : null, r = i < y.Count ? y[i] : null;
            if (l != r) return ContentComparison.Differs($"строка {i + 1}: «{l ?? "—"}» ≠ «{r ?? "—"}»");
        }
        return ContentComparison.Same("без строк source:");
    }

    private static List<string> Lines(string path, byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        if (!text.StartsWith("skeleton:", StringComparison.Ordinal)) throw new InvalidDataException($"{path}: не скелет");
        return [.. text.Split('\n').Where(l => !l.StartsWith(Prefix, StringComparison.Ordinal))];
    }
}
