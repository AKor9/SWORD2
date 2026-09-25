using System.Text;
using Sword.Engine.Content;

namespace Sword.Bake.Tests;

/// <summary>Проект в пустом временном каталоге, исходники — вне его; только встроенный обработчик file.copy.</summary>
public sealed class BuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project, _sources;

    public BuildTests()
    {
        _project = Path.Combine(_root, "project");
        _sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_sources);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Source(string name, string text)
    {
        string path = Path.Combine(_sources, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static BuildSummary Build(Builder builder, IReadOnlyList<AssetManifest>? assets = null, bool force = false) =>
        builder.Build(assets, new BuildOptions { Force = force, Jobs = 2 }, _ => { });

    private (BakeProject Project, Builder Builder, AssetManifest Asset) NewProjectWithCopy(string text = "hello")
    {
        var project = BakeProject.Create(_project, "Test");
        var asset = project.AddAsset("data/greeting", "file.copy",
            new Dictionary<string, string> { ["from"] = Source("greeting.txt", text) });
        return (project, new Builder(project), asset);
    }

    private static string Text(PakSource pak, string path) => Encoding.UTF8.GetString(pak.ReadAllBytes(path));

    [Fact]
    public void NewProjectOpensWithoutPluginsAndAddsAssetFromExternalFile()
    {
        var (project, builder, asset) = NewProjectWithCopy();

        Assert.Equal(Path.Combine(_project, "Test.swpak"), project.PakPath);
        Assert.Empty(project.Problems);
        Assert.True(File.Exists(asset.File));
        var source = Assert.Single(project.SourcesOf(asset));
        Assert.Equal("from", source.Field);
        Assert.Null(source.Problem);
        Assert.Equal(Path.Combine(_sources, "greeting.txt"), source.FullPath);
        Assert.False(File.Exists(Path.Combine(_project, "greeting.txt")));   // исходник не копируется в проект

        Assert.Equal(AssetStatus.Dirty, builder.Check(asset).Status);
        Assert.Equal(PakStatus.Missing, builder.CheckPak().Status);
    }

    [Fact]
    public void BuildWritesPakWithBuildInfoAndNoContentDirectory()
    {
        var (project, builder, asset) = NewProjectWithCopy();

        var s = Build(builder);

        Assert.True(s.Ok);
        Assert.Equal(1, s.Built);
        using (var pak = PakSource.Open(project.PakPath))
        {
            Assert.Equal("hello", Text(pak, "data/greeting"));
            var info = PakBuildInfo.Read(pak)!;
            Assert.Equal(Path.Combine(_sources, "greeting.txt"), info.SingleSourceOf("data/greeting"));
            Assert.Empty(pak.Corrupted());
        }
        // в проекте только манифесты и служебный кэш: готового контента на диске нет
        Assert.Equal(["data"], Directory.GetDirectories(_project).Select(Path.GetFileName).Where(d => d != BakeProject.StateDirName));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_project, "data")), f => !f.EndsWith(AssetManifest.Extension, StringComparison.Ordinal));
        Assert.Equal(AssetStatus.UpToDate, builder.Check(asset).Status);
        Assert.Equal(PakStatus.UpToDate, builder.CheckPak().Status);
        Assert.True(builder.Verify().Ok);
        Assert.Equal("data/greeting", builder.Contents().Single(c => c.Owner == asset.Id).Path);
    }

    [Fact]
    public void ChangedSourceOrParamsMakeAssetAndPakStale()
    {
        var (project, builder, asset) = NewProjectWithCopy("hello");
        Assert.True(Build(builder).Ok);

        string src = Path.Combine(_sources, "greeting.txt");
        File.WriteAllText(src, "HELLO");   // тот же размер: решает хеш
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(1));
        var check = builder.Check(asset);
        Assert.Equal(AssetStatus.Dirty, check.Status);
        Assert.Contains("изменён", check.Reason);
        Assert.Equal(PakStatus.Stale, builder.CheckPak().Status);

        Assert.True(Build(builder).Ok);
        Assert.Equal(AssetStatus.UpToDate, builder.Check(asset).Status);

        var processor = project.Processors.Find("file.copy")!;
        var parameters = asset.ReadParams(processor);
        processor.ParamsType.GetProperty("To")!.SetValue(parameters, "data/renamed");
        Assert.True(ManifestWriter.SaveParams(asset, processor, parameters));
        project.Reload();
        var reloaded = project.Find(asset.Id)!;
        Assert.Equal("изменились параметры", builder.Check(reloaded).Reason);

        Assert.True(Build(builder).Ok);
        using var pak = PakSource.Open(project.PakPath);
        Assert.True(pak.Exists("data/renamed"));
        Assert.False(pak.Exists("data/greeting"));
    }

    [Fact]
    public void FailedBuildKeepsPreviousPak()
    {
        var (project, builder, _) = NewProjectWithCopy();
        Assert.True(Build(builder).Ok);
        byte[] before = File.ReadAllBytes(project.PakPath);

        var broken = project.AddAsset("data/missing", "file.copy",
            new Dictionary<string, string> { ["from"] = Path.Combine(_sources, "nope.txt") });
        var s = Build(builder);

        Assert.False(s.Ok);
        Assert.False(s.PakWritten);
        Assert.Equal(1, s.Failed);
        Assert.Equal(before, File.ReadAllBytes(project.PakPath));
        Assert.False(File.Exists(project.PakPath + ".tmp"));
        var check = builder.Check(broken);
        Assert.Equal(AssetStatus.Error, check.Status);
        Assert.Contains("nope.txt", check.Reason);

        // исходник появился — ошибка снята, ассет собирается
        Source("nope.txt", "now here");
        Assert.Equal(AssetStatus.Dirty, builder.Check(broken).Status);
        Assert.True(Build(builder).Ok);
    }

    [Fact]
    public void UnchangedBuildDoesNotRewritePakAndOpenPakCanBeReplaced()
    {
        var (project, builder, _) = NewProjectWithCopy();
        Assert.True(Build(builder).Ok);
        var written = File.GetLastWriteTimeUtc(project.PakPath);

        var again = Build(builder);
        Assert.True(again.Ok);
        Assert.True(again.PakUnchanged);
        Assert.Equal(written, File.GetLastWriteTimeUtc(project.PakPath));

        // игра держит пак открытым — сборка всё равно заменяет его, открывший дочитывает прежний
        using var open = PakSource.Open(project.PakPath);
        File.WriteAllText(Path.Combine(_sources, "greeting.txt"), "changed text");
        var s = Build(builder);
        Assert.True(s.Ok, "пак не заменился, пока открыт");
        Assert.Equal("hello", Text(open, "data/greeting"));
        using var fresh = PakSource.Open(project.PakPath);
        Assert.Equal("changed text", Text(fresh, "data/greeting"));
    }

    [Fact]
    public void RemovedAssetMakesPakStaleAndNextBuildDropsIt()
    {
        var (project, builder, asset) = NewProjectWithCopy();
        var other = project.AddAsset("data/other", "file.copy", new Dictionary<string, string> { ["from"] = Source("other.txt", "x") });
        Assert.True(Build(builder).Ok);

        project.RemoveAsset(other);
        var pak = builder.CheckPak();
        Assert.Equal(PakStatus.Stale, pak.Status);
        Assert.Contains(pak.Details, d => d.StartsWith("data/other", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(Path.Combine(project.StateDir, "removed")));

        Assert.True(Build(builder).Ok);
        Assert.Equal(PakStatus.UpToDate, builder.CheckPak().Status);
        Assert.DoesNotContain(builder.Contents(), c => c.Path == "data/other");
        Assert.Equal(AssetStatus.UpToDate, builder.Check(project.Find(asset.Id)!).Status);
    }

    [Fact]
    public void VerifyFindsCorruptedData()
    {
        var (project, builder, _) = NewProjectWithCopy("some longer text to corrupt");
        Assert.True(Build(builder).Ok);
        long offset;
        using (var pak = PakSource.Open(project.PakPath)) offset = pak.Find("data/greeting")!.Value.Offset;
        using (var f = new FileStream(project.PakPath, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Position = offset + 2;
            f.WriteByte((byte)'#');
        }
        var report = builder.Verify();
        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.StartsWith("data/greeting", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPluginIsAProjectProblemNotAFailureToOpen()
    {
        BakeProject.Create(_project, "Plugins");
        string file = Path.Combine(_project, BakeProject.FileName);
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"plugins\": []", "\"plugins\": [ \"nowhere/${config}/Plugin.dll\" ]"));

        var project = BakeProject.Open(_project);

        Assert.Contains(project.Problems, p => p.Contains("Plugin.dll", StringComparison.Ordinal));
        Assert.NotNull(project.Processors.Find("file.copy"));
    }

    [Fact]
    public void ProjectInBakeSubdirIsFoundFromRepositoryAndManifestCopiesAreNotAssets()
    {
        string bake = Path.Combine(_project, BakeProject.SubdirName);
        Directory.CreateDirectory(bake);
        var created = BakeProject.Create(bake, "Game");
        created.AddAsset("data/a", "file.copy", new Dictionary<string, string> { ["from"] = Source("a.txt", "a") });
        created.RemoveAsset(created.Find("data/a")!);
        Directory.CreateDirectory(Path.Combine(_project, "src", "deep"));

        Assert.Equal(bake, BakeProject.FindRoot(_project));
        Assert.Equal(bake, BakeProject.FindRoot(Path.Combine(_project, "src", "deep")));
        var project = BakeProject.Open(Path.Combine(_project, "src"));
        Assert.Empty(project.Assets);   // копия в .bake/removed — не ассет
        Assert.Empty(project.Broken);
    }

    [Fact]
    public void RootsAreOptionalAndLocal()
    {
        var project = BakeProject.Create(_project, "Roots");
        string file = Source("a.txt", "a");
        Assert.Equal(file.Replace('\\', '/'), project.Roots.Contract(file));   // без корней — полный путь
        Assert.Throws<ArgumentException>(() => project.Roots.Expand("${src}/a.txt"));

        SourceRoots.SetUserRoot(project.Root, "src", _sources);
        project.Reload();
        Assert.Equal("${src}/a.txt", project.Roots.Contract(file));
        Assert.Equal(file, project.Roots.Expand("${src}/a.txt"));
        Assert.Equal(Path.Combine(_project, "x", "y.txt"), project.Roots.Expand("x/y.txt"));
    }

    [Fact]
    public void WrittenTextFilesKeepLineEndings()
    {
        var project = BakeProject.Create(_project, "Endings");
        Assert.Contains("\r\n", File.ReadAllText(project.ProjectFile));
        var asset = project.AddAsset("a", "file.copy", new Dictionary<string, string> { ["from"] = Source("a.txt", "a") });
        Assert.Contains("\r\n", File.ReadAllText(asset.File));

        File.WriteAllText(asset.File, File.ReadAllText(asset.File).ReplaceLineEndings("\n"));
        project.Reload();
        asset = project.Find("a")!;
        var processor = project.Processors.Find("file.copy")!;
        var parameters = asset.ReadParams(processor);
        processor.ParamsType.GetProperty("To")!.SetValue(parameters, "b");
        Assert.True(ManifestWriter.SaveParams(asset, processor, parameters));
        Assert.DoesNotContain("\r\n", File.ReadAllText(asset.File));
    }
}
