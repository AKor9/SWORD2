using Sword.Engine.Animation;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Game.Data;

namespace Sword.Assets.Preview;

/// <summary>
/// Предпросмотр по виду файла <c>Content/</c>: клипы и скелет — на персонаже, меш персонажа и анатомия — по секциям
/// (анатомия ещё и под кожей), оружие — по зонам крови, текстура — слои с мипами, шрифт — образец текста, остальное —
/// начало текста. Выбран каталог — смотрится вид его первого файла и все файлы этого вида под ним.
/// </summary>
internal static class Previews
{
    /// <summary>Вид файла для подписи: «клипы», «меш», …; null — смотреть нечего.</summary>
    public static string? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".swclip" => "клипы",
        ".swskeleton" => "скелет",
        ".swmesh" => path.StartsWith(WeaponMesh.Dir + "/", StringComparison.OrdinalIgnoreCase) ? "оружие" : "меш",
        ".swblood" => "оружие",
        ".swtex" => "текстура",
        ".swfont" => "шрифт",
        ".json" or ".txt" => "текст",
        _ => null,
    };

    /// <param name="paths">Файлы выбора (файл или все файлы каталога).</param>
    /// <returns>null — смотреть нечего.</returns>
    public static PreviewScene? Build(IContentSource content, IReadOnlyList<string> paths, int mode)
    {
        if (paths.Count == 0) return null;
        string ext = Path.GetExtension(paths[0]).ToLowerInvariant();
        var same = paths.Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();
        switch (ext)
        {
            case ClipLibrary.ClipExt:
            case ClipLibrary.SkeletonExt:
                return AnimPreview.Build(content, same, mode);
            case ".swmesh":
                if (same[0].StartsWith(WeaponMesh.Dir + "/", StringComparison.OrdinalIgnoreCase)) return WeaponPreview.Build(content, [same[0]], mode);
                return MeshPreview.Build(content, [same[0]], mode, withSkin: same[0].Contains("/anatomy/", StringComparison.OrdinalIgnoreCase));
            case WeaponBloodData.Extension:
                return WeaponPreview.Build(content, [WeaponPreview.MeshOf(same[0])], mode);
            case TextureData.Extension:
                return TexturePreview.Build(content, same, mode);
            case UiVectorFontFormat.Extension:
                return FontPreview.Build(content, [same[0]], mode);
            case ".json" or ".txt":
                return Text(content, same[0]);
            default:
                return null;
        }
    }

    /// <summary>Первые строки текстового файла.</summary>
    private static PreviewScene Text(IContentSource content, string path)
    {
        var scene = new PreviewScene();
        using var reader = new StreamReader(content.OpenRead(path));
        for (int i = 0; i < 40 && reader.ReadLine() is { } line; i++) scene.Info.Add(line.Length > 200 ? line[..200] + "…" : line);
        return scene;
    }
}
