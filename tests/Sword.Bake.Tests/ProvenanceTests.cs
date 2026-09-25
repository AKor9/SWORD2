using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Content;

namespace Sword.Bake.Tests;

/// <summary>Импорты (происхождение), ссылки между манифестами и отбор ассетов по импорту, обработчику и пути контента.</summary>
public sealed class ProvenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project, _sources;

    public ProvenanceTests()
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

    // ── обработчики с ссылкой: «риг» и «снаряжение», которое берёт исходник рига ──

    internal sealed class RigParams
    {
        [Source(SourceKind.File)]
        public string Pose { get; set; } = "";
    }

    internal sealed class RigProcessor : AssetProcessor<RigParams>
    {
        public override string Id => "test.rig";
        public override string Title => "Риг (тест)";

        protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, RigParams p) => [DeclaredOutput.File(asset.Id + ".rig")];

        protected override void Process(BakeContext ctx, RigParams p) => File.Copy(ctx.InputFile(p.Pose), ctx.Output(ctx.Asset.Id + ".rig"), true);
    }

    internal sealed class GearParams
    {
        [AssetRef("test.rig")]
        public string Rig { get; set; } = "";
    }

    internal sealed class GearProcessor : AssetProcessor<GearParams>
    {
        public override string Id => "test.gear";
        public override string Title => "Снаряжение на риге (тест)";

        protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, GearParams p) => [DeclaredOutput.File(asset.Id + ".gear")];

        protected override void Process(BakeContext ctx, GearParams p)
        {
            var rig = ctx.Ref<RigParams>(p.Rig);
            File.WriteAllText(ctx.Output(ctx.Asset.Id + ".gear"), "на риге: " + File.ReadAllText(ctx.InputFile(rig.Pose)));
        }
    }

    private string Source(string name, string text)
    {
        string path = Path.Combine(_sources, name);
        File.WriteAllText(path, text);
        return path;
    }

    private BakeProject NewProject()
    {
        var project = BakeProject.Create(_project, "Test");
        project.Processors.Add(new RigProcessor());
        project.Processors.Add(new GearProcessor());
        return project;
    }

    private static AssetManifest Add(BakeProject project, string id, IAssetProcessor processor, object parameters, string import = "")
    {
        ManifestWriter.Create(project.ManifestPath(id), processor, parameters, import: import);
        project.Reload();
        return project.Find(id)!;
    }

    private static AssetManifest Rig(BakeProject project, string id, string pose) =>
        Add(project, id, project.Processors.Find("test.rig")!, new RigParams { Pose = pose });

    private static AssetManifest Gear(BakeProject project, string id, string rig) =>
        Add(project, id, project.Processors.Find("test.gear")!, new GearParams { Rig = rig });

    private static BuildSummary Build(Builder builder) => builder.Build(null, new BuildOptions { Jobs = 2 }, _ => { });

    [Fact]
    public void ReferencedParamsAreInReferrerHashButTargetRebuildDoesNotRebuildIt()
    {
        var project = NewProject();
        var rig = Rig(project, "rigs/manny", Source("pose.txt", "поза-1"));
        var gear = Gear(project, "gear/katana", "rigs/manny");
        var builder = new Builder(project);

        var reference = Assert.Single(project.ReferencesOf(gear));
        Assert.Equal(("rig", "rigs/manny", null), (reference.Field, reference.TargetId, reference.Problem));
        Assert.Equal(rig.Id, reference.Target!.Id);

        Assert.True(Build(builder).Ok);
        using (var pak = PakSource.Open(project.PakPath))
            Assert.Equal("на риге: поза-1", Encoding.UTF8.GetString(pak.ReadAllBytes("gear/katana.gear")));
        Assert.Equal(AssetStatus.UpToDate, builder.Check(gear).Status);

        // пересборка цели без изменений не трогает ссылающегося: ссылка — не зависимость сборки
        var s = builder.Build([rig], new BuildOptions { Force = true, Jobs = 1 }, _ => { });
        Assert.True(s.Ok);
        Assert.Equal(1, s.Built);
        Assert.Equal(AssetStatus.UpToDate, builder.Check(gear).Status);

        // другие параметры цели — ссылающийся устарел: они в его хеше
        var processor = project.Processors.Find("test.rig")!;
        Assert.True(ManifestWriter.SaveParams(rig, processor, new RigParams { Pose = Source("pose2.txt", "поза-2") }));
        project.Reload();
        gear = project.Find("gear/katana")!;
        Assert.Equal("изменились параметры", builder.Check(gear).Reason);
        Assert.True(Build(builder).Ok);
        Assert.Equal(AssetStatus.UpToDate, builder.Check(gear).Status);

        // исходник цели — вход ссылающегося: он его регистрирует сам
        string pose2 = Path.Combine(_sources, "pose2.txt");
        File.WriteAllText(pose2, "поза-3");
        File.SetLastWriteTimeUtc(pose2, DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("изменён", builder.Check(gear).Reason);
    }

    [Fact]
    public void BrokenReferenceIsAnErrorAndIsNotBaked()
    {
        var project = NewProject();
        var copy = project.AddAsset("data/pose", "file.copy", new Dictionary<string, string> { ["from"] = Source("pose.txt", "p") });
        var missing = Gear(project, "gear/a", "rigs/none");
        var wrong = Gear(project, "gear/b", copy.Id);
        var builder = new Builder(project);

        var check = builder.Check(missing);
        Assert.Equal(AssetStatus.Error, check.Status);
        Assert.Equal("ссылка rig: нет ассета «rigs/none»", check.Reason);
        Assert.Contains("нужен test.rig", builder.Check(wrong).Reason);

        var s = Build(builder);
        Assert.Equal(2, s.Failed);
        Assert.False(s.Ok);
    }

    [Fact]
    public void ImportsGiveProvenanceAndProblemsWithoutRebuilding()
    {
        var project = NewProject();
        File.WriteAllText(Path.Combine(_project, ImportRegistry.FileName), """
            {
              "fab_pack": {
                "collection": "Some Pack",
                "source": "Fab",
                "license": "Epic Content License",
                "licenseFiles": [ "${project}/LICENSE-pack.txt" ],
                "pipeline": [ "ue_mesh_export" ]
              }
            }
            """);
        project.Reload();
        Assert.Empty(project.Problems);
        var record = project.Imports["fab_pack"];
        Assert.Equal(("Some Pack", "Epic Content License"), (record.Collection, record.License));

        var known = Add(project, "data/a", project.Processors.Find("file.copy")!,
            project.Processors.Find("file.copy")!.NewParams("data/a"), "fab_pack");
        var unknown = Add(project, "data/b", project.Processors.Find("test.rig")!, new RigParams { Pose = Source("b.txt", "b") }, "nope");
        var none = Rig(project, "data/c", Source("c.txt", "c"));

        var issues = project.ProvenanceIssues();
        Assert.Contains(issues, i => i.AssetId == unknown.Id && i.Text.Contains("неизвестный импорт «nope»"));
        Assert.Contains(issues, i => i.AssetId == none.Id && i.Text.Contains("не указано"));
        Assert.Contains(issues, i => i.AssetId is null && i.Text.Contains("нет файла лицензии"));
        Assert.DoesNotContain(issues, i => i.AssetId == known.Id);

        File.WriteAllText(Path.Combine(_project, "LICENSE-pack.txt"), "license");
        Assert.DoesNotContain(project.ProvenanceIssues(), i => i.AssetId is null);

        // без strictLayout замечания не мешают сборке; строгая раскладка делает их ошибкой единицы
        var builder = new Builder(project);
        Assert.Equal(AssetStatus.Dirty, builder.Check(unknown).Status);
        var json = JsonNode.Parse(File.ReadAllText(project.ProjectFile))!.AsObject();
        json["strictLayout"] = true;
        TextFiles.WriteJson(project.ProjectFile, json);
        project.Reload();
        builder = new Builder(project);
        Assert.Equal("неизвестный импорт «nope»", builder.Check(project.Find(unknown.Id)!).Reason);
        Assert.Equal("происхождение не указано (поле import)", builder.Check(project.Find(none.Id)!).Reason);
    }

    [Fact]
    public void BrokenImportsFileIsAProjectProblem()
    {
        var project = NewProject();
        File.WriteAllText(Path.Combine(_project, ImportRegistry.FileName), """{ "a": { "colection": "опечатка" } }""");
        project.Reload();
        Assert.Contains(project.Problems, p => p.Contains(ImportRegistry.FileName));
        Assert.Empty(project.Imports);
    }

    [Fact]
    public void SelectByImportProcessorAndContentPath()
    {
        var project = NewProject();
        var copy = project.Processors.Find("file.copy")!;
        var actions = Add(project, "anim/actions", copy, SetTo(copy, "game/actions.json", Source("actions.json", "{}")), "sword");
        var rig = Rig(project, "rigs/manny", Source("pose.txt", "p"));

        Assert.Equal([actions.Id], project.Select(["import:sword"]).Select(a => a.Id));
        Assert.Equal([rig.Id], project.Select(["processor:test.rig"]).Select(a => a.Id));
        Assert.Equal([actions.Id], project.Select(["game/actions"]).Select(a => a.Id));            // путь ассета контента → его производитель
        Assert.Equal([actions.Id], project.Select(["game/actions.json"]).Select(a => a.Id));
        Assert.Throws<ArgumentException>(() => project.Select(["import:none"]));
    }

    private static object SetTo(IAssetProcessor copy, string to, string from)
    {
        var p = copy.NewParams("x");
        copy.ParamsType.GetProperty("From")!.SetValue(p, from);
        copy.ParamsType.GetProperty("To")!.SetValue(p, to);
        return p;
    }
}
