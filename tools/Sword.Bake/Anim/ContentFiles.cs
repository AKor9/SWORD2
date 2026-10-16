namespace Sword.Bake.Anim;

/// <summary>
/// Запись готового файла контента форматом движка (<c>Write(Stream)</c> у <c>ClipFile</c>, <c>SkeletonFile</c>,
/// <c>SkinnedMeshData</c>): каталог создаётся, файл пишется во временный <c>*.tmp</c> рядом и подменяет прежний только
/// целиком — недописанный файл не выглядит годным (менеджер ресурсов <c>*.tmp</c> не видит и в пак не кладёт).
/// </summary>
public static class ContentFiles
{
    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var file = File.Create(tmp)) write(file);
        File.Move(tmp, path, overwrite: true);
    }
}
