using Sword.Bake.Engine.Font;

namespace Sword.Bake.Engine.Tests;

/// <summary>
/// Детерминизм пекаря шрифтов (<see cref="UiFontBaker"/>): две выпечки одного ttf — одни байты. У Consolas есть контуры из
/// одной точки, для которых stb читает точку из неинициализированной памяти (без их отбрасывания — разное число индексов
/// полос от прогона к прогону); шрифты пака таких контуров не имеют, их байты сверяет с эталоном <see cref="SourceBakeTests"/>.
/// </summary>
public sealed class FontBakerTests
{
    [Fact]
    public void SystemFont_TwoBakesSameBytes()
    {
        string ttf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf");
        Assert.True(File.Exists(ttf), $"нет системного шрифта {ttf}");
        SameBytesTwice(ttf, ["Num"]);
    }

    [SourceFact]
    public void PackFonts_TwoBakesSameBytes()
    {
        string dir = Path.Combine(TestPaths.SourceRoot()!, "_Sword", "fonts");
        foreach (var name in (string[])["golos-regular", "golos-semibold", "ptnarrow-bold", "plexmono-regular", "plexmono-light"])
            SameBytesTwice(Path.Combine(dir, name + ".ttf"), []);
    }

    private static void SameBytesTwice(string ttf, string[] roles)
    {
        byte[] data = File.ReadAllBytes(ttf);
        byte[] a = UiFontBaker.Bake(data, ttf, "test", roles, out string ra);
        byte[] b = UiFontBaker.Bake(data, ttf, "test", roles, out string rb);
        Assert.Equal(ra, rb);
        Assert.True(a.AsSpan().SequenceEqual(b), $"{Path.GetFileName(ttf)}: две выпечки дали разные байты ({a.Length} и {b.Length})");
    }
}
