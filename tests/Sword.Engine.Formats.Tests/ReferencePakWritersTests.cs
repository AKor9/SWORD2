using System.Collections.Concurrent;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Engine.Formats.Tests;

/// <summary>
/// Писатели форматов SWORD2 = байты эталонного пака <c>Content.reference.swpak</c> (категория Content): каждый файл формата
/// читается форматом SWORD2 и записывается обратно — те же байты. Отделяет «писатель верен» от «логика выпечки верна»:
/// расхождение пересобранного пекарней пака с эталоном тогда — в обработчике, а не в записи формата.
/// Форматы, которых в SWORD2 ещё нет (<c>.swtex</c>, <c>.swblood</c>), добавятся сюда вместе с ними.
/// </summary>
[Trait("Category", "Content")]
public sealed class ReferencePakWritersTests
{
    /// <summary>Эталонный пак — ближайший вверх от тестов (корень репозитория или рабочего дерева), не <c>Content.swpak</c>.</summary>
    private static string ReferencePak()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string pak = Path.Combine(dir.FullName, ContentLocator.ReferencePakName);
            if (File.Exists(pak)) return pak;
        }
        throw new FileNotFoundException($"нет {ContentLocator.ReferencePakName} выше {AppContext.BaseDirectory} (категория Content; " +
                                        "без пака — verify.ps1 -SkipReference)");
    }

    /// <summary>
    /// Все файлы пака с расширением <paramref name="extension"/>: чтение → запись → те же байты. Расхождение — путь, длины и
    /// первый отличный байт; число файлов — как в эталоне (проверка не пуста).
    /// </summary>
    private static void RoundTrip(string extension, int expected, Func<Stream, string, Action<Stream>> read)
    {
        using var pak = PakSource.Open(ReferencePak());
        var paths = pak.List("", extension);
        var mismatches = new ConcurrentBag<string>();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
        {
            byte[] original = pak.ReadAllBytes(path);
            var write = read(new MemoryStream(original, writable: false), pak.Describe(path));
            using var copy = new MemoryStream(original.Length);
            write(copy);
            var written = copy.GetBuffer().AsSpan(0, (int)copy.Length);
            if (written.SequenceEqual(original)) return;
            int at = original.AsSpan().CommonPrefixLength(written);
            mismatches.Add($"{path}: {original.Length} Б в паке, {written.Length} Б записано, первое отличие — байт {at}");
        });
        Assert.Equal(expected, paths.Count);
        Assert.True(mismatches.IsEmpty, $"{extension}: записано не байт в байт {mismatches.Count} из {paths.Count}:\n" +
                                        string.Join("\n", mismatches.Order(StringComparer.Ordinal).Take(20)));
    }

    [Fact]
    public void Clips_WriteSameBytes() =>
        RoundTrip(".swclip", 1048, (s, name) => ClipFile.Read(s, name).Write);

    [Fact]
    public void Skeletons_WriteSameBytes() =>
        RoundTrip(".swskeleton", 1, (s, name) => SkeletonFile.Read(s, name).Write);

    [Fact]
    public void Meshes_WriteSameBytes() =>
        RoundTrip(".swmesh", 10, (s, name) => SkinnedMeshData.Read(s, name).Write);

    [Fact]
    public void Fonts_WriteSameBytes() =>
        RoundTrip(UiVectorFontFormat.Extension, 5, (s, name) => UiFontFile.Read(s, name).Write);
}
