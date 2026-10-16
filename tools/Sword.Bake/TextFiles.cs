namespace Sword.Bake;

/// <summary>
/// Запись текстовых файлов пекарни (рецепты, исключения пака): через временный файл, в UTF-8 без BOM,
/// с окончаниями строк, которые уже были в файле; у нового файла — CRLF, как принято в репозитории.
/// </summary>
internal static class TextFiles
{
    public static void Write(string file, string text)
    {
        string newline = "\r\n";
        if (File.Exists(file))
        {
            string before = File.ReadAllText(file);
            if (before.Contains('\n') && !before.Contains("\r\n")) newline = "\n";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, text.ReplaceLineEndings(newline) + newline);
        File.Move(tmp, file, overwrite: true);
    }
}
