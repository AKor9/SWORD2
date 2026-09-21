using Sword.Engine.Content;

namespace Sword.Engine.Content.Tests;

public sealed class ContentPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-content-{Guid.NewGuid():N}");

    public ContentPathTests() => Directory.CreateDirectory(Path.Combine(_dir, "root", "characters"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("characters/manny/mesh.swmesh", "characters/manny/mesh.swmesh")]
    [InlineData(@"characters\manny\mesh.swmesh", "characters/manny/mesh.swmesh")]
    [InlineData("/weapons/sword.swmesh/", "weapons/sword.swmesh")]
    public void NormalizesSeparatorsAndEdges(string path, string expected) =>
        Assert.Equal(expected, ContentPath.Normalize(path));

    [Theory]
    [InlineData("C:/Windows/win.ini")]        // диск: Path.Combine отбросил бы корень
    [InlineData("C:win.ini")]                 // путь относительно текущего каталога диска
    [InlineData("mesh.swmesh:stream")]        // альтернативный поток NTFS
    [InlineData("../secret.txt")]
    [InlineData("a/../../secret.txt")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("")]
    [InlineData("a/b\u0001")]
    [InlineData("a/b?.txt")]
    public void RejectsPathsOutsideContent(string path) =>
        Assert.Throws<ArgumentException>(() => ContentPath.Normalize(path));

    [Fact]
    public void DirectorySourceStaysUnderRoot()
    {
        File.WriteAllText(Path.Combine(_dir, "outside.txt"), "секрет");
        var source = new DirectorySource(Path.Combine(_dir, "root"));
        Assert.Throws<ArgumentException>(() => source.OpenRead("../outside.txt"));
        Assert.Throws<ArgumentException>(() => source.OpenRead(Path.Combine(_dir, "outside.txt")));
        Assert.Throws<ArgumentException>(() => source.Exists("C:/Windows/win.ini"));
        Assert.StartsWith(source.Root, source.FullPath("characters/manny.swmesh"));
    }

    [Fact]
    public void DriveRootWorksAsContentRoot()
    {
        // Корень диска оканчивается разделителем («C:\»): путь под ним — внутри, а не «за корнем».
        string drive = Path.GetPathRoot(_dir)!;
        var source = new DirectorySource(drive);
        string relative = Path.GetRelativePath(drive, Path.Combine(_dir, "root", "characters")).Replace(Path.DirectorySeparatorChar, '/');
        Assert.Equal(Path.Combine(_dir, "root", "characters"), source.FullPath(relative));
        Assert.True(Directory.Exists(source.FullPath(relative)));
        Assert.Throws<ArgumentException>(() => source.FullPath("../x.txt"));
    }
}
