using System.Text.Json.Nodes;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Tests;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Bake.Game.Tests;

/// <summary>
/// Оба плагина пекарни в настоящем хосте (sword-bake процессом, плагины из tools/&lt;проект&gt;/bin/&lt;конфигурация&gt;, как
/// в проекте Bake/): <c>Sword.Bake.Engine</c>, затем <c>Sword.Bake.Game</c> — обработчики обоих в одном каталоге, ссылка
/// анатомии на риг (<c>ctx.Ref&lt;SkeletonParams&gt;</c>) проходит — тип параметров рига один, меш анатомии на синтетическом
/// риге читается форматом. Плагин игры раньше плагина движка — понятная ошибка проекта, а не сбой посреди выпечки.
/// </summary>
public sealed class PluginsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-game-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public PluginsTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Manifest(string id, JsonObject manifest)
    {
        string path = Path.Combine(_project, id.Replace('/', Path.DirectorySeparatorChar) + ".bake.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    /// <summary>Синтетический риг (скелет и поза покоя) и анатомия — меш на его же костях.</summary>
    private void CreateProject(params string[] plugins)
    {
        string src = Path.Combine(_root, "src");
        RawAnimIO.Write(Path.Combine(src, "raw"), SynthSkeleton.Manifest(),
            [SynthSkeleton.Walk("Idle", 11, loop: true, distance: 0, turn: 0, swing: 0.05f)]);
        SynthMesh.WriteGlb(Path.Combine(src, "synth.glb"));
        SynthMesh.WriteSkeletonJson(Path.Combine(src, "skeleton.json"));
        File.WriteAllText(Path.Combine(_project, BakeProject.FileName), new JsonObject
        {
            ["name"] = "Synth", ["assets"] = ".", ["pak"] = "Content.swpak",
            ["roots"] = new JsonObject { ["src"] = src },
            ["plugins"] = new JsonArray([.. plugins.Select(p => (JsonNode)TestPaths.Plugin(p))]),
        }.ToJsonString());
        Manifest("skeletons/synth", new JsonObject
        {
            ["processor"] = "anim.skeleton",
            ["params"] = new JsonObject { ["clips"] = "${src}/raw", ["refPose"] = "${src}/skeleton.json" },
        });
        Manifest("characters/synth/anatomy/bones", new JsonObject
        {
            ["processor"] = "anatomy.mesh",
            ["params"] = new JsonObject { ["skeleton"] = "skeletons/synth", ["mesh"] = "${src}/synth.glb" },
        });
    }

    private (int Exit, string Out) Cli(params string[] args)
    {
        var (exit, stdout, stderr) = TestPaths.RunCli(_project, Timeout, ["--project", _project, .. args]);
        return (exit, stdout + stderr);
    }

    [Fact]
    public void EngineThenGame_OneRigParamsType_AnatomyBakesOnRig()
    {
        CreateProject("Sword.Bake.Engine", "Sword.Bake.Game");
        var (exit, output) = Cli("build", "--json");
        Assert.True(exit == 0, output);
        var json = JsonNode.Parse(output)!;
        Assert.Empty(json["project"]!["problems"]!.AsArray());
        Assert.Equal(["anatomy.mesh", "anim.pack", "anim.skeleton", "file.copy", "font.vector", "skin.mesh", "texture.array", "weapon.static"],
            json["project"]!["processors"]!.AsArray().Select(p => (string)p!).Order(StringComparer.Ordinal));
        Assert.All(json["assets"]!.AsArray(), a => Assert.Equal("built", (string)a!["result"]!));

        using var pak = PakSource.Open(Path.Combine(_project, "Content.swpak"));
        SkinnedMeshData mesh;
        using (var s = pak.OpenRead("characters/synth/anatomy/bones.swmesh")) mesh = SkinnedMeshData.Read(s, "bones");
        Assert.Equal(SynthSkeleton.Joints, mesh.JointNames);
        Assert.NotEmpty(mesh.Vertices);
        Assert.All(mesh.Vertices, v => Assert.True(float.IsFinite(v.Position.X + v.Position.Y + v.Position.Z)));
    }

    [Fact]
    public void GameBeforeEngine_IsProjectProblem()
    {
        // типы плагина игры грузятся и без основы (она нужна только коду обработчиков) — порядок проверяет ядро
        CreateProject("Sword.Bake.Game", "Sword.Bake.Engine");
        var (exit, output) = Cli("build", "--json");
        Assert.NotEqual(0, exit);
        var project = JsonNode.Parse(output)!["project"]!;
        var problems = project["problems"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.Contains(problems, p => p.Contains("Sword.Bake.Game.dll", StringComparison.Ordinal)
                                       && p.Contains("ссылается на плагин Sword.Bake.Engine", StringComparison.Ordinal));
        Assert.DoesNotContain("anatomy.mesh", project["processors"]!.AsArray().Select(p => (string)p!));
    }
}
