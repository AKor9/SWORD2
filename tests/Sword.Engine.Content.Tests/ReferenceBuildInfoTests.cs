namespace Sword.Engine.Content.Tests;

/// <summary>
/// Сведения о сборке эталонного пака <c>Content.reference.swpak</c> (категория Content): читаются форматом SWORD2, описывают
/// ровно этот пак (выходы 29 ассетов — файлы оглавления с теми же размером и хешем, ничьих файлов нет) и записываются
/// обратно теми же байтами.
/// </summary>
[Trait("Category", "Content")]
public sealed class ReferenceBuildInfoTests
{
    /// <summary>Эталонный пак — ближайший вверх от тестов (корень репозитория или рабочего дерева), не <c>Content.swpak</c>.</summary>
    public static string ReferencePak()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string pak = Path.Combine(dir.FullName, ContentLocator.ReferencePakName);
            if (File.Exists(pak)) return pak;
        }
        throw new FileNotFoundException($"нет {ContentLocator.ReferencePakName} выше {AppContext.BaseDirectory} (категория Content; " +
                                        "без пака — verify.ps1 -SkipReference)");
    }

    [Fact]
    public void ReferencePak_BuildInfoDescribesThisPak_AndWritesBackSameBytes()
    {
        using var pak = PakSource.Open(ReferencePak());
        var info = PakBuildInfo.Read(pak);

        Assert.NotNull(info);
        Assert.Equal("SWORD", info.Project);
        Assert.Equal(29, info.Assets.Count);
        Assert.Equal(1083, pak.Entries.Count);

        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PakBuildInfo.EntryPath };
        foreach (var (id, rec) in info.Assets)
        {
            Assert.False(string.IsNullOrEmpty(rec.Processor), id);
            Assert.StartsWith(rec.Processor + "|", rec.Fingerprint, StringComparison.Ordinal);
            Assert.NotEmpty(rec.Outputs);
            foreach (var o in rec.Outputs)
            {
                var entry = pak.Find(o.Path);
                Assert.True(entry is not null, $"{id}: в паке нет выхода {o.Path}");
                Assert.Equal(PakFormat.HashText(entry.Value.Hash), o.Hash);
                Assert.Equal(entry.Value.Size, o.Size);
                owned.Add(o.Path);
            }
        }
        Assert.Empty(pak.Entries.Where(e => !owned.Contains(e.Path)).Select(e => e.Path));

        Assert.Equal(pak.ReadAllBytes(PakBuildInfo.EntryPath), info.ToUtf8());
    }
}
