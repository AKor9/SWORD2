namespace Sword.Bake.Engine.Anim;

/// <summary>
/// Запись готового файла контента форматом движка (<c>Write(Stream)</c> у <c>ClipFile</c>, <c>SkeletonFile</c>,
/// <c>SkinnedMeshData</c>): каталог создаётся, файл пишется во временный <c>*.tmp</c> рядом и подменяет прежний только
/// целиком — недописанный клип не выглядит годным при следующей инкрементальной сборке (как <c>ClipAsset.Save</c> SWORD).
/// Временные <c>*.tmp</c> выходами ассета не считаются (<see cref="BakeContext.ListDir"/>). Открыт: так же пишет свои
/// выходы плагин игры (<c>Sword.Bake.Game</c>).
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
