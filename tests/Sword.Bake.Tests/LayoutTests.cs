using System.Text.Json.Nodes;
using Sword.Engine.Content;

namespace Sword.Bake.Tests;

/// <summary>Каталог контента и раскладка проекта: ассеты по основе имени, выход вне папки манифеста, общий файл, strictLayout.</summary>
public sealed class LayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project, _sources;

    public LayoutTests()
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

    /// <summary>file.copy с ID <paramref name="id"/>, который пишет файл пака <paramref name="to"/>.</summary>
    private static AssetManifest Copy(BakeProject project, string id, string from, string to)
    {
        var asset = project.AddAsset(id, "file.copy", new Dictionary<string, string> { ["from"] = from });
        var processor = project.Processors.Find("file.copy")!;
        var parameters = asset.ReadParams(processor);
        processor.ParamsType.GetProperty("To")!.SetValue(parameters, to);
        Assert.True(ManifestWriter.SaveParams(asset, processor, parameters));
        project.Reload();
        return project.Find(id)!;
    }

    private static BuildSummary Build(Builder builder) => builder.Build(null, new BuildOptions { Jobs = 2 }, _ => { });

    [Fact]
    public void FilesWithOneStemAreOneContentAssetBeforeAndAfterBuild()
    {
        var project = BakeProject.Create(_project, "Test");
        Copy(project, "weapons/katana_mesh", Source("mesh.bin", "m"), "weapons/katana.mesh");
        Copy(project, "weapons/katana_blood", Source("blood.bin", "b"), "weapons/katana.blood");
        var builder = new Builder(project);

        var before = Assert.Single(builder.Catalog().Assets);
        Assert.Equal("weapons/katana", before.Id);
        Assert.Equal(["weapons/katana.blood", "weapons/katana.mesh"], before.Files);
        Assert.Equal(["weapons/katana_blood", "weapons/katana_mesh"], before.Producers);
        Assert.Empty(builder.Catalog().Issues);

        Assert.True(Build(builder).Ok);
        var catalog = builder.Catalog();
        Assert.Same(catalog.Find("weapons/katana"), catalog.Find("weapons/katana.mesh"));
        Assert.Equal(2, catalog.Find("weapons/katana")!.Files.Count);
        Assert.Null(catalog.Find("weapons/sword"));
    }

    [Fact]
    public void OutputOutsideManifestFolderAndSharedFileAreIssues()
    {
        var project = BakeProject.Create(_project, "Test");
        var stray = Copy(project, "anim/actions", Source("actions.json", "{}"), "game/actions.json");
        Copy(project, "data/a", Source("a.txt", "a"), "data/shared.txt");
        Copy(project, "data/b", Source("b.txt", "b"), "data/shared.txt");
        var builder = new Builder(project);

        var issues = builder.Catalog().Issues;
        Assert.Contains(issues, i => i.AssetId == "anim/actions" && i.Text.Contains("вне папки своего манифеста anim/"));
        Assert.Contains(issues, i => i.AssetId == "data/a" && i.Text.Contains("data/b"));
        Assert.Contains(issues, i => i.AssetId == "data/b" && i.Text.Contains("data/a"));

        // без strictLayout это предупреждения: ассет с выходом вне папки собирается
        Assert.Equal(AssetStatus.Dirty, builder.Check(stray).Status);
    }

    [Fact]
    public void StrictLayoutMakesIssueAnAssetErrorAndBuildSkipsIt()
    {
        var project = BakeProject.Create(_project, "Test");
        var file = project.ProjectFile;
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["strictLayout"] = true;
        TextFiles.WriteJson(file, json);
        project.Reload();
        Assert.True(project.StrictLayout);

        var stray = Copy(project, "anim/actions", Source("actions.json", "{}"), "game/actions.json");
        var good = Copy(project, "combat/actions", Source("good.json", "{}"), "combat/actions.json");
        // при строгой раскладке исправной единице нужно и происхождение
        File.WriteAllText(Path.Combine(_project, ImportRegistry.FileName), """{ "own": { "collection": "Свой контент" } }""");
        var manifest = JsonNode.Parse(File.ReadAllText(good.File))!.AsObject();
        manifest["import"] = "own";
        TextFiles.WriteJson(good.File, manifest);
        project.Reload();
        good = project.Find(good.Id)!;
        Assert.Equal("own", good.Import);
        var builder = new Builder(project);

        var check = builder.Check(stray);
        Assert.Equal(AssetStatus.Error, check.Status);
        Assert.StartsWith("раскладка:", check.Reason);
        Assert.Equal(AssetStatus.Dirty, builder.Check(good).Status);

        var s = Build(builder);
        Assert.False(s.Ok);
        Assert.Equal(1, s.Failed);
        Assert.False(File.Exists(project.PakPath));   // упавший ассет — пак не пишется
    }

    [Fact]
    public void DeclaredOutputCoversFilesUnderItsDirectory()
    {
        var dir = DeclaredOutput.Directory("anim/katana/");
        Assert.Equal("anim/katana", dir.Path);
        Assert.True(dir.Covers("anim/katana/Slash.swclip"));
        Assert.False(dir.Covers("anim/katana"));
        Assert.False(dir.Covers("anim/katana_v2/Slash.swclip"));
        Assert.True(DeclaredOutput.File("weapons\\katana.swmesh").Covers("weapons/katana.swmesh"));
        Assert.Equal("weapons/katana", ContentCatalog.StemOf("weapons/katana.swmesh"));
        Assert.Equal("anim/katana", ContentCatalog.StemOf("anim/katana"));
        Assert.Equal("", ContentCatalog.FolderOf("actions"));
    }
}
