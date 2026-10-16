using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Bake.Anim;
using Sword.Bake.Font;
using Sword.Engine.Animation;
using Sword.Engine.Formats;
using Sword.Game.Data;
using ContentClip = Sword.Engine.Animation.ClipSource;

namespace Sword.Bake.Tests;

/// <summary>
/// Пекарня во временном каталоге: таблица видов файлов, «конвертера нет», рецепт первой выпечки и повторная выпечка без
/// вопросов (sword-bake процессом), выход каждого конвертера на синтетическом риге читается читателем формата, а клипы
/// с прямой кинематикой и корневым движением повторяют выгрузку.
/// </summary>
public sealed class BakerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));
    private readonly string _bake, _content, _src, _raw;

    public BakerTests()
    {
        _bake = Path.Combine(_root, "Bake");
        _content = Path.Combine(_root, "Content");
        _src = Path.Combine(_root, "src");
        _raw = Path.Combine(_src, "skin_synth");
        Directory.CreateDirectory(_bake);
        File.WriteAllText(Path.Combine(_bake, SourceRoots.UserFile), new JsonObject { ["roots"] = new JsonObject { ["src"] = _src } }.ToJsonString());
        RawAnimIO.Write(_raw, SynthSkeleton.Manifest(),
        [
            SynthSkeleton.Walk("Walk_F", 31, loop: true, distance: 1.2f, turn: 0, swing: 0.4f, path: "/Game/Synth/Walk"),
            SynthSkeleton.Walk("Turn_R", 21, loop: false, distance: 0.3f, turn: MathF.PI / 2, swing: 0.2f, path: "/Game/Synth/Turn"),
            SynthSkeleton.Walk("Idle", 11, loop: true, distance: 0, turn: 0, swing: 0.05f, path: "/Game/Synth/Walk"),
        ]);
        SynthMesh.WriteGlb(Path.Combine(_src, "synth.glb"));
        Directory.CreateDirectory(Path.Combine(_src, "synth"));
        SynthMesh.WriteSkeletonJson(Path.Combine(_src, "synth", "skeleton.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private Baker Baker() => new(_bake, _content);

    private (int Exit, string Out) Cli(params string[] args)
    {
        var (exit, stdout, stderr) = TestPaths.RunCli(_root, Timeout, ["--bake", _bake, "--content", _content, .. args]);
        return (exit, stdout + stderr);
    }

    [Theory]
    [InlineData("skin_gasp/manifest.json", "anim")]
    [InlineData("meshes/skeleton.json", "skeleton")]
    [InlineData("weapons/SM_Katana.glb", "weapon,character,anatomy")]
    [InlineData("fire/T_Noise3.PNG", "texture")]
    [InlineData("fonts/golos.ttf", "font")]
    [InlineData("anim/actions.json", "copy")]
    [InlineData("fonts/OFL.txt", "copy")]
    [InlineData("meshes/manny.fbx", "")]
    [InlineData("recipes/x.glb.bake.json", "")]
    public void FileKind_PicksConverters(string file, string expected)
    {
        string path = Path.Combine(_src, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (file.EndsWith("manifest.json", StringComparison.Ordinal)) Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "clips"));
        Assert.Equal(expected, string.Join(",", Sword.Bake.Baker.Candidates(path).Select(c => c.Id)));
    }

    [Fact]
    public void ManifestWithoutClips_IsCopiedNotBakedAsAnimation() =>
        Assert.Equal(["copy"], Sword.Bake.Baker.Candidates(Path.Combine(_src, "data", "manifest.json")).Select(c => c.Id));

    [Fact]
    public void NoConverter_SaysSoAndListsKinds()
    {
        string fbx = Path.Combine(_src, "manny.fbx");
        File.WriteAllText(fbx, "");
        var (exit, output) = Cli(fbx);
        Assert.Equal(2, exit);
        Assert.Contains("для файла manny.fbx конвертера нет", output);
        Assert.Contains("*.glb, *.gltf", output);
        Assert.False(Directory.Exists(Path.Combine(_bake, Sword.Bake.Baker.RecipesDir)));
    }

    [Fact]
    public void FileOutsideRoots_IsAnError()
    {
        string outside = Path.Combine(_root, "elsewhere.png");
        File.WriteAllText(outside, "");
        var e = Assert.Throws<ArgumentException>(() => Baker().RecipePath(outside));
        Assert.Contains("вне корней", e.Message);
    }

    [Fact]
    public void FirstBakeAsks_ThenRecipeIsWritten_AndRebakeAsksNothing()
    {
        string json = Path.Combine(_src, "data", "actions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        File.WriteAllText(json, "{\"a\":1}");
        string recipe = Path.Combine(_bake, "recipes", "src", "data", "actions.json.bake.json");

        // рецепта нет — параметры по умолчанию показаны, ничего не записано
        var (exit, output) = Cli(json);
        Assert.Equal(3, exit);
        Assert.Contains("рецепта нет", output);
        Assert.False(File.Exists(recipe));
        Assert.False(Directory.Exists(_content));

        // ответ — ключом: рецепт записан, файл испечён
        (exit, output) = Cli(json, "--set", "out=data/combat/actions.json");
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(recipe));
        Assert.Equal("copy", Recipe.Load(recipe).Converter);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(_content, "data", "combat", "actions.json")));
        Assert.Contains("\r\n", File.ReadAllText(recipe));

        // повторно — без вопросов, по рецепту
        File.WriteAllText(json, "{\"a\":2}");
        (exit, output) = Cli(json);
        Assert.True(exit == 0, output);
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(_content, "data", "combat", "actions.json")));

        // несколько конвертеров — выбор обязателен
        File.Copy(Path.Combine(_src, "synth.glb"), Path.Combine(_src, "other.glb"));
        (exit, output) = Cli(Path.Combine(_src, "other.glb"), "--yes");
        Assert.Equal(3, exit);
        Assert.Contains("--converter", output);
    }

    [Fact]
    public void EveryConverter_OutputReadsWithFormatReader()
    {
        // скелет: рецепт с ID и эталонным экспортом, остальные ссылаются на него полем rig
        string rig = Path.Combine(_src, "synth", "skeleton.json");
        var (exit, output) = Cli(rig, "--set", "id=synth", "--set", "clips=${src}/skin_synth");
        Assert.True(exit == 0, output);
        (exit, output) = Cli(Path.Combine(_raw, "manifest.json"), "--set", "sets={\"walk\":[\"Walk\"],\"turn\":[\"Turn\"]}");
        Assert.True(exit == 0, output);
        (exit, output) = Cli(Path.Combine(_src, "synth.glb"), "--converter", "character", "--yes");
        Assert.True(exit == 0, output);

        // анатомия — второй файл того же меша (рецепт у каждого сырого файла свой)
        File.Copy(Path.Combine(_src, "synth.glb"), Path.Combine(_src, "bones.glb"));
        (exit, output) = Cli(Path.Combine(_src, "bones.glb"), "--converter", "anatomy", "--yes");
        Assert.True(exit == 0, output);

        string png = Path.Combine(_src, "T_Mask.png");
        var rgb = new byte[32 * 16 * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 7);
        File.WriteAllBytes(png, Png.Encode(rgb, 32, 16));
        (exit, output) = Cli(png, "--set", "size=32", "--set", "out=textures/blood/mask.swtex");
        Assert.True(exit == 0, output);

        string ttf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf");
        string font = Path.Combine(_src, "consola.ttf");
        File.Copy(ttf, font);
        (exit, output) = Cli(font, "--set", "name=num", "--set", "roles=[\"Num\"]");
        Assert.True(exit == 0, output);

        Assert.Equal(["animations/turn/Turn_R.swclip", "animations/walk/Idle.swclip", "animations/walk/Walk_F.swclip", "fonts/num.swfont",
                      "meshes/characters/anatomy/bones.swmesh", "meshes/characters/synth.swmesh", "skeletons/synth.swskeleton",
                      "textures/blood/mask.swtex"],
            Directory.GetFiles(_content, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_content, f).Replace('\\', '/')).Order(StringComparer.Ordinal));

        Stream Open(string path) => File.OpenRead(Path.Combine(_content, path));
        using (var s = Open("textures/blood/mask.swtex")) Assert.Equal(1, TextureData.Read(s, "mask").Slices);
        using (var s = Open("fonts/num.swfont")) Assert.NotNull(UiFontRaster.Read(s, "num"));
        SkinnedMeshData bones;
        using (var s = Open("meshes/characters/anatomy/bones.swmesh")) bones = SkinnedMeshData.Read(s, "bones");
        Assert.Equal(SynthSkeleton.Joints, bones.JointNames);

        // как игра: скелет, поза привязки из меша, библиотека клипов; прямая кинематика · корневое движение = выгрузка
        Skeleton skeleton;
        using (var s = Open(ClipLibrary.SkeletonFile("synth"))) skeleton = Skeleton.Read(s, "synth");
        using (var s = Open("meshes/characters/synth.swmesh")) skeleton = skeleton.WithMeshBindPose(s, "mesh");
        var clipFiles = Directory.GetFiles(Path.Combine(_content, "animations"), "*.swclip", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_content, f).Replace('\\', '/')).Order(StringComparer.Ordinal);
        var skipped = new List<string>();
        var library = ClipLibrary.Load(skeleton, clipFiles.Select(p => new ContentClip(p, () => Open(p), p)), skipped)!;
        Assert.Empty(skipped);
        Assert.Equal(["turn/Turn_R", "walk/Idle", "walk/Walk_F"], library.Clips.ToArray().Select(c => c.Name));
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

        // все рецепты разом — те же байты
        var before = Directory.GetFiles(_content, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        (exit, output) = Cli("--all");
        Assert.True(exit == 0, output);
        Assert.Contains("рецептов 6, ошибок 0, файлов 8", output);
        Assert.All(before, kv => Assert.Equal(kv.Value, File.ReadAllBytes(kv.Key)));
    }

    /// <summary>Оружие — на настоящем риге (у синтетики нет пальцев для хвата): меш и зоны крови читаются форматами.</summary>
    [SourceFact]
    public void Weapon_OnRealRig_MeshAndBloodRead()
    {
        string src = TestPaths.SourceRoot()!;
        File.WriteAllText(Path.Combine(_bake, SourceRoots.UserFile), new JsonObject { ["roots"] = new JsonObject { ["src"] = src } }.ToJsonString());
        // рецепт рига кладётся руками: рецепты основного дерева в этом каталоге пекарни не видны
        var skeleton = new Recipe { Converter = "skeleton" };
        skeleton.Set("id", "ue5_manny");
        skeleton.Set("clips", "${src}/_Export/skin_sword");
        skeleton.Set("fingerSrc", "${src}/_Export/skin_sword_ue4");
        Baker().SaveRecipe(Path.Combine(src, "_Export", "meshes", "skeleton.json"), skeleton);

        var (exit, output) = Cli(Path.Combine(src, "_Export", "weapons", "SM_Katana.glb"), "--converter", "weapon", "--yes");
        Assert.True(exit == 0, output);
        SkinnedMeshData mesh;
        using (var s = File.OpenRead(Path.Combine(_content, "meshes", "weapons", "katana.swmesh"))) mesh = SkinnedMeshData.Read(s, "katana");
        Assert.Equal(0, mesh.JointCount);
        WeaponBloodData blood;
        using (var s = File.OpenRead(Path.Combine(_content, "data", "weapons", "katana" + WeaponBloodData.Extension))) blood = WeaponBloodData.Read(s, "katana");
        Assert.Equal(mesh.Vertices.Length, blood.VertexZones!.Length);
    }
}
