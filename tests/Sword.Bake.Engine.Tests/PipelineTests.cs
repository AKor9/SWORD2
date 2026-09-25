using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Bake.Engine.Anim;
using Sword.Engine.Animation;
using Sword.Engine.Content;
using ContentClip = Sword.Engine.Animation.ClipSource;

namespace Sword.Bake.Engine.Tests;

/// <summary>
/// Плагин в настоящем хосте: временный проект пекарни на синтетическом риге, sword-bake процессом (плагин грузится из
/// tools/Sword.Bake.Engine/bin/&lt;конфигурация&gt;, как в проекте Bake/). Скелет, меш и наборы клипов попадают в пак, игра
/// читает их своими загрузчиками (<see cref="Skeleton"/>, <see cref="ClipLibrary"/>), и прямая кинематика с корневым
/// движением повторяет экспорт; повторная сборка ничего не перепекает, принудительная не переписывает клипы с тем же хешем
/// исходника (так и после смены конфигурации пекарни), изменённый клип — перепекается один.
/// </summary>
public sealed class PipelineTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-engine-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project, _raw;

    public PipelineTests()
    {
        _project = Path.Combine(_root, "project");
        _raw = Path.Combine(_root, "src", "raw");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static RawClip[] Clips(float idleSwing = 0.05f) =>
    [
        SynthSkeleton.Walk("Walk_F", 31, loop: true, distance: 1.2f, turn: 0, swing: 0.4f, path: "/Game/Synth/Walk"),
        SynthSkeleton.Walk("Turn_R", 21, loop: false, distance: 0.3f, turn: MathF.PI / 2, swing: 0.2f, path: "/Game/Synth/Turn"),
        SynthSkeleton.Walk("Idle", 11, loop: true, distance: 0, turn: 0, swing: idleSwing, path: "/Game/Synth/Walk"),
    ];

    private void Manifest(string id, JsonObject manifest)
    {
        string path = Path.Combine(_project, id.Replace('/', Path.DirectorySeparatorChar) + ".bake.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    private void CreateProject()
    {
        string src = Path.GetDirectoryName(_raw)!;
        RawAnimIO.Write(_raw, SynthSkeleton.Manifest(), Clips());
        SynthMesh.WriteGlb(Path.Combine(src, "synth.glb"));
        SynthMesh.WriteSkeletonJson(Path.Combine(src, "skeleton.json"));
        File.WriteAllText(Path.Combine(_project, BakeProject.FileName), new JsonObject
        {
            ["name"] = "Synth", ["assets"] = ".", ["pak"] = "Content.swpak",
            ["roots"] = new JsonObject { ["src"] = src },
            ["plugins"] = new JsonArray(TestPaths.Plugin()),
        }.ToJsonString());
        Manifest("skeletons/synth", new JsonObject
        {
            ["processor"] = "anim.skeleton",
            ["params"] = new JsonObject { ["clips"] = "${src}/raw", ["refPose"] = "${src}/skeleton.json" },
        });
        Manifest("characters/synth/mesh", new JsonObject
        {
            ["processor"] = "skin.mesh",
            ["params"] = new JsonObject { ["skeleton"] = "skeletons/synth", ["mesh"] = "${src}/synth.glb" },
        });
        Manifest("anim/synth", new JsonObject
        {
            ["processor"] = "anim.pack",
            ["params"] = new JsonObject
            {
                ["skeleton"] = "skeletons/synth", ["anims"] = "${src}/raw",
                ["sets"] = new JsonObject { ["walk"] = new JsonArray("Walk"), ["turn"] = new JsonArray("Turn") },
            },
        });
    }

    private (int Exit, string Out) Cli(params string[] args)
    {
        var (exit, stdout, stderr) = TestPaths.RunCli(_project, Timeout, ["--project", _project, .. args]);
        return (exit, stdout + stderr);
    }

    [Fact]
    public void BakedRigLoadsInEngine_AndRebuildKeepsClips()
    {
        CreateProject();
        var (exit, output) = Cli("build", "--json");
        Assert.True(exit == 0, output);
        var json = JsonNode.Parse(output)!;
        Assert.Empty(json["project"]!["problems"]!.AsArray());
        Assert.Equal(["anim.pack", "anim.skeleton", "file.copy", "font.vector", "skin.mesh", "texture.array"],
            json["project"]!["processors"]!.AsArray().Select(p => (string)p!).Order(StringComparer.Ordinal));
        Assert.All(json["assets"]!.AsArray(), a => Assert.Equal("built", (string)a!["result"]!));

        string pakPath = Path.Combine(_project, "Content.swpak");
        using (var pak = PakSource.Open(pakPath))
        {
            Assert.Equal(["anim/turn/Turn_R.swclip", "anim/walk/Idle.swclip", "anim/walk/Walk_F.swclip", "characters/synth/mesh.swmesh",
                          "skeletons/synth.swskeleton"],
                pak.Entries.Select(e => e.Path).Where(p => !p.StartsWith(".bake/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

            // как игра: скелет, поза привязки из меша, библиотека клипов
            Skeleton skeleton;
            using (var s = pak.OpenRead(ClipLibrary.SkeletonFile("synth"))) skeleton = Skeleton.Read(s, "synth");
            using (var s = pak.OpenRead("characters/synth/mesh.swmesh")) skeleton = skeleton.WithMeshBindPose(s, "mesh");
            var skipped = new List<string>();
            var library = ClipLibrary.Load(skeleton,
                pak.List(ClipLibrary.AnimDir, ClipLibrary.ClipExt).Select(p => new ContentClip(p, () => pak.OpenRead(p), p)), skipped)!;
            Assert.Empty(skipped);
            Assert.Equal(["turn/Turn_R", "walk/Idle", "walk/Walk_F"], library.Clips.ToArray().Select(c => c.Name));

            // прямая кинематика · корневое движение = экспорт (первый кадр каждого клипа синтетики — в нуле лицом вперёд)
            var man = RawAnimIO.ReadManifest(_raw);
            var space = new UeSpace(man);
            int jc = skeleton.JointCount;
            var rot = new Quaternion[jc];
            var pos = new Vector3[jc];
            var model = new Matrix4x4[jc];
            foreach (var info in man.Clips)
            {
                var raw = RawAnimIO.ReadClip(_raw, man, info);
                int clip = library.FindClip(info.Name);
                var c = library.Clips[clip];
                for (int f = 0; f < c.Count; f += 5)
                {
                    library.Sample(clip, f / library.SampleRate, rot, pos);
                    skeleton.ToModel(rot, pos, model);
                    var motion = ClipLibrary.MotionMatrix(library.RootMotion[c.Start + f], library.RootYaw[c.Start + f]);
                    for (int j = 0; j < jc; j++)
                        Assert.True(Vector3.Distance(Vector3.Transform(model[j].Translation, motion), space.Point(raw.P(f, j))) < 1e-4f,
                            $"{info.Name} кадр {f}, сустав {man.Bones[j]}");
                }
            }
            // поворот UE +90° вокруг Z (левая система, Z вверх) — направо; у персонажа «+» — от +Z к +X, влево: поворот корня −90°
            Assert.InRange(library.RootTurn(library.FindClip("Turn_R")), -MathF.PI / 2 - 0.01f, -MathF.PI / 2 + 0.01f);
        }
        var built = File.GetLastWriteTimeUtc(pakPath);

        // повторная сборка — всё актуально, пак не переписан
        (exit, output) = Cli("build", "--json");
        Assert.True(exit == 0, output);
        json = JsonNode.Parse(output)!;
        Assert.All(json["assets"]!.AsArray(), a => Assert.Equal("upToDate", (string)a!["result"]!));
        Assert.Equal(built, File.GetLastWriteTimeUtc(pakPath));

        // принудительная сборка (как после смены конфигурации: отпечаток кода обработчика другой) — клипы не переписаны
        (exit, output) = Cli("build", "anim/synth", "--force");
        Assert.True(exit == 0, output);
        Assert.Contains("записано 0, без изменений 3, удалено 0", output);

        // изменился один клип экспорта — перепечён он один
        RawAnimIO.Write(_raw, SynthSkeleton.Manifest(), Clips(idleSwing: 0.1f));
        (exit, output) = Cli("build");
        Assert.True(exit == 0, output);
        Assert.Contains("записано 1, без изменений 2, удалено 0", output);
        Assert.DoesNotContain("✓ skeletons/synth", output);
    }

    [Fact]
    public void InspectCommand_ReadsExportAndGltf()
    {
        CreateProject();
        string src = Path.GetDirectoryName(_raw)!;
        var (exit, output) = Cli("inspect", "rawpose", _raw, "--clip", "Turn_R", "--frame", "20");
        Assert.True(exit == 0, output);
        Assert.Contains("Turn_R кадр 20: курс root", output);
        Assert.Contains("pelvis", output);

        (exit, output) = Cli("inspect", "meshinfo", Path.Combine(src, "synth.glb"));
        Assert.True(exit == 0, output);
        Assert.Contains($"вершин {SynthSkeleton.Joints.Length * SynthMesh.VertsPerJoint}", output);
        Assert.Contains($"суставов {SynthSkeleton.Joints.Length}", output);
        Assert.Contains("pelvis    bind t=(0.000,95.000,0.000)", output);

        (exit, output) = Cli("inspect", "compare", "a", "--with", "b");
        Assert.Equal(1, exit);
        Assert.Contains("неизвестная команда compare", output);
    }
}
