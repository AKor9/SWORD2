using System.Collections.Concurrent;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Game.Data;

namespace Sword.Engine.Formats.Tests;

/// <summary>
/// Писатели форматов = байты пака игры <c>Content.swpak</c> (категория Content): каждый файл формата читается и
/// записывается обратно — те же байты. Отделяет «писатель верен» от «конвертер пекарни верен». Формат пака из игровых
/// данных (<c>.swblood</c>, <c>Sword.Game.Data</c>) — здесь же, со всеми форматами пака.
/// </summary>
[Trait("Category", "Content")]
public sealed class GamePakWritersTests
{
    private static string GamePak() =>
        ContentLocator.Find() ?? throw new FileNotFoundException($"нет пака игры {ContentLocator.PakName} (категория Content; " +
                                                                 $"без пака — verify.ps1 -SkipReference). {ContentLocator.BakeHint}");

    /// <summary>
    /// Все файлы пака с расширением <paramref name="extension"/>: чтение → запись → те же байты. Расхождение — путь, длины и
    /// первый отличный байт; проверка не пуста.
    /// </summary>
    private static void RoundTrip(string extension, Func<Stream, string, Action<Stream>> read)
    {
        using var pak = PakSource.Open(GamePak());
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
        Assert.NotEmpty(paths);
        Assert.True(mismatches.IsEmpty, $"{extension}: записано не байт в байт {mismatches.Count} из {paths.Count}:\n" +
                                        string.Join("\n", mismatches.Order(StringComparer.Ordinal).Take(20)));
    }

    [Fact]
    public void Clips_WriteSameBytes() =>
        RoundTrip(".swclip", (s, name) => ClipFile.Read(s, name).Write);

    [Fact]
    public void Skeletons_WriteSameBytes() =>
        RoundTrip(".swskeleton", (s, name) => SkeletonFile.Read(s, name).Write);

    [Fact]
    public void Meshes_WriteSameBytes() =>
        RoundTrip(".swmesh", (s, name) => SkinnedMeshData.Read(s, name).Write);

    [Fact]
    public void Fonts_WriteSameBytes() =>
        RoundTrip(UiVectorFontFormat.Extension, (s, name) => UiFontFile.Read(s, name).Write);

    [Fact]
    public void Textures_WriteSameBytes() =>
        RoundTrip(TextureData.Extension, (s, name) => TextureData.Read(s, name).Write);

    [Fact]
    public void WeaponBlood_WriteSameBytes() =>
        RoundTrip(WeaponBloodData.Extension, (s, name) => WeaponBloodData.Read(s, name).Write);
}
