using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Engine.Ui;

namespace Sword.Tests;

/// <summary>
/// Шрифты интерфейса из пака контента — все <c>fonts/*.swfont</c>. Общий файл тестов (подключается ссылкой Compile в .csproj);
/// фикстурой класса — читаются и склеиваются один раз на класс.
/// </summary>
public sealed class PakFonts : IDisposable
{
    public UiFonts Fonts { get; } = Read();

    public void Dispose() => Fonts.Dispose();

    public static UiFonts Read()
    {
        using var pak = ContentLocator.Open();
        var files = new List<UiFontFile>();
        foreach (var f in pak.List(UiVectorFontFormat.Dir, UiVectorFontFormat.Extension))
            using (var s = pak.OpenRead(f))
                files.Add(UiFontFile.Read(s, pak.Describe(f)));
        return new UiFonts(files);
    }
}
