using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sword.Bake.Engine.Anim;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Xunit.Abstractions;

namespace Sword.Bake.Engine.Tests;

/// <summary>
/// Выпечка настоящих исходников (категория Source; без них — пропуск): временный проект пекарни с манифестами обработчиков
/// этого плагина из проекта Bake/ SWORD (скелет ue5_manny, меш Manny, четыре экспорта anim.pack с теми же наборами, восемь
/// массивов текстур, пять шрифтов с лицензиями), sword-bake build обычным прогоном, затем сверка с эталонным паком: каждый
/// клип anim/ — по содержимому без SourceHash (всё остальное побайтно); скелет, меш, текстуры, шрифты и лицензии — побайтно.
/// Итог с числами — в выводе теста и artifacts/bake/source-check.json.
/// </summary>
[Trait("Category", "Source")]
public sealed class SourceBakeTests(SourceBake bake, ITestOutputHelper output) : IClassFixture<SourceBake>
{
    [SourceFact]
    public void Clips_MatchReferenceWithoutSourceHash()
    {
        var result = bake.Result;
        using var reference = PakSource.Open(TestPaths.ReferencePak());
        using var baked = PakSource.Open(result.Pak);
        var refClips = reference.List(Sword.Engine.Animation.ClipLibrary.AnimDir, Sword.Engine.Animation.ClipLibrary.ClipExt);
        var bakedClips = baked.List(Sword.Engine.Animation.ClipLibrary.AnimDir, Sword.Engine.Animation.ClipLibrary.ClipExt);
        var missing = refClips.Except(bakedClips, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = bakedClips.Except(refClips, StringComparer.OrdinalIgnoreCase).ToList();
        var common = refClips.Intersect(bakedClips, StringComparer.OrdinalIgnoreCase).ToList();

        var watch = Stopwatch.StartNew();
        var mismatches = new ConcurrentBag<(string Path, string Problem)>();
        int sameHash = 0, sameSize = 0;
        Parallel.ForEach(common, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
        {
            if (reference.Find(path)!.Value.Size == baked.Find(path)!.Value.Size) Interlocked.Increment(ref sameSize);
            ClipFile a, b;
            using (var s = reference.OpenRead(path)) a = ClipFile.Read(s, reference.Describe(path));
            using (var s = baked.OpenRead(path)) b = ClipFile.Read(s, baked.Describe(path));
            if (a.SourceHash == b.SourceHash) Interlocked.Increment(ref sameHash);
            if (ClipContent.Diff(a, b) is { } problem) mismatches.Add((path, problem));
        });
        watch.Stop();

        var perSet = refClips.GroupBy(p => p.Split('/')[1]).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Set: g.Key, Reference: g.Count(), Matched: g.Count(p => common.Contains(p, StringComparer.OrdinalIgnoreCase)
                                                                                    && !mismatches.Any(m => m.Path == p))))
            .ToList();
        int matched = common.Count - mismatches.Count;
        output.WriteLine($"сборка: {result.Seconds:F1} с (sword-bake {result.BuildSeconds:F1} с); сверка клипов: {watch.Elapsed.TotalSeconds:F1} с");
        foreach (var (asset, seconds) in result.Assets) output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {asset}: {seconds:F1} с"));
        output.WriteLine($"клипов в эталоне {refClips.Count}, выпечено {bakedClips.Count}, совпало по содержимому {matched}, отличий {mismatches.Count}, " +
                         $"нет в выпечке {missing.Count}, лишних {extra.Count}; тот же размер {sameSize}, тот же SourceHash {sameHash}");
        foreach (var s in perSet) output.WriteLine($"  {s.Set}: {s.Matched} из {s.Reference}");
        foreach (var m in mismatches.OrderBy(m => m.Path, StringComparer.Ordinal).Take(30)) output.WriteLine($"≠ {m.Path}: {m.Problem}");

        SourceBake.Report("clips", new JsonObject
        {
            ["reference"] = refClips.Count, ["baked"] = bakedClips.Count, ["matched"] = matched, ["different"] = mismatches.Count,
            ["missing"] = missing.Count, ["extra"] = extra.Count, ["sameSize"] = sameSize, ["sameSourceHash"] = sameHash,
            ["compareSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            ["sets"] = new JsonObject(perSet.Select(s => KeyValuePair.Create(s.Set, (JsonNode?)new JsonObject { ["reference"] = s.Reference, ["matched"] = s.Matched }))),
            ["mismatches"] = new JsonArray([.. mismatches.OrderBy(m => m.Path, StringComparer.Ordinal).Take(100)
                .Select(m => (JsonNode)new JsonObject { ["path"] = m.Path, ["problem"] = m.Problem })]),
        });

        Assert.Equal(1048, refClips.Count);
        Assert.Empty(missing);
        Assert.Empty(extra);
        Assert.True(mismatches.IsEmpty, $"клипы отличаются от эталона: {mismatches.Count} из {common.Count}\n" +
            string.Join("\n", mismatches.OrderBy(m => m.Path, StringComparer.Ordinal).Take(20).Select(m => $"{m.Path}: {m.Problem}")));
        Assert.Equal(common.Count, sameSize);
    }

    [SourceFact]
    public void Skeleton_MatchesReferenceByteForByte() => SameBytes("skeletons/ue5_manny.swskeleton", "skeleton");

    [SourceFact]
    public void Mesh_MatchesReferenceByteForByte() => SameBytes("characters/manny/mesh.swmesh", "mesh");

    [SourceFact]
    public void Textures_MatchReferenceByteForByte() => SameFiles("textures", "", TextureData.Extension, 8);

    [SourceFact]
    public void Fonts_MatchReferenceByteForByte() => SameFiles("fonts", UiVectorFontFormat.Dir, "", 8);

    /// <summary>
    /// Все файлы эталона в <paramref name="dir"/> с расширением <paramref name="ext"/> — побайтно с выпечкой; итог по файлам —
    /// в отчёте под <paramref name="key"/>. Число файлов эталона — <paramref name="expected"/> (проверка не пуста).
    /// </summary>
    private void SameFiles(string key, string dir, string ext, int expected)
    {
        using var reference = PakSource.Open(TestPaths.ReferencePak());
        using var baked = PakSource.Open(bake.Result.Pak);
        var paths = reference.List(dir, ext);
        var extra = baked.List(dir, ext).Except(paths, StringComparer.OrdinalIgnoreCase).ToList();
        var files = new JsonArray();
        var problems = new List<string>();
        foreach (string path in paths)
        {
            byte[] a = reference.ReadAllBytes(path);
            byte[]? b = baked.Find(path) is null ? null : baked.ReadAllBytes(path);
            bool same = b is not null && a.AsSpan().SequenceEqual(b);
            int at = b is null ? 0 : a.AsSpan().CommonPrefixLength(b);
            string line = same ? $"{path}: {a.Length} Б — байт в байт"
                : b is null ? $"{path}: нет в выпечке"
                : $"{path}: эталон {a.Length} Б, выпечено {b.Length} Б, первое отличие — байт {at}";
            output.WriteLine(line);
            if (!same) problems.Add(line);
            files.Add(new JsonObject
            {
                ["path"] = path, ["bytes"] = a.Length, ["baked"] = b?.Length, ["same"] = same, ["firstDifference"] = same || b is null ? null : (JsonNode)at,
            });
        }
        SourceBake.Report(key, new JsonObject
        {
            ["reference"] = paths.Count, ["matched"] = paths.Count - problems.Count, ["extra"] = new JsonArray([.. extra.Select(e => (JsonNode)e)]),
            ["files"] = files,
        });
        Assert.Equal(expected, paths.Count);
        Assert.Empty(extra);
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private void SameBytes(string path, string key)
    {
        using var reference = PakSource.Open(TestPaths.ReferencePak());
        using var baked = PakSource.Open(bake.Result.Pak);
        byte[] a = reference.ReadAllBytes(path), b = baked.ReadAllBytes(path);
        bool same = a.AsSpan().SequenceEqual(b);
        int at = a.AsSpan().CommonPrefixLength(b);
        output.WriteLine(same ? $"{path}: {a.Length} Б — байт в байт" : $"{path}: эталон {a.Length} Б, выпечено {b.Length} Б, первое отличие — байт {at}");
        SourceBake.Report(key, new JsonObject { ["path"] = path, ["bytes"] = a.Length, ["baked"] = b.Length, ["same"] = same, ["firstDifference"] = same ? null : (JsonNode)at });
        Assert.True(same, $"{path}: эталон {a.Length} Б, выпечено {b.Length} Б, первое отличие — байт {at}");
    }
}

/// <summary>Одна выпечка исходников на класс тестов: временный проект, sword-bake build, пак — во временном каталоге.</summary>
public sealed partial class SourceBake : IDisposable
{
    private readonly Lazy<BakeResult> _result;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-engine-tests", "source-" + Guid.NewGuid().ToString("N")[..12]);

    public sealed record BakeResult(string Pak, double Seconds, double BuildSeconds, IReadOnlyList<(string Asset, double Seconds)> Assets, string Output);

    public SourceBake() => _result = new Lazy<BakeResult>(Bake, LazyThreadSafetyMode.ExecutionAndPublication);

    public BakeResult Result => _result.Value;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Манифесты обработчиков плагина движка из проекта Bake/ SWORD 2eab13b — те же параметры, без полей import и notes.</summary>
    private static readonly (string Id, string Json)[] Manifests =
    [
        ("skeletons/ue5_manny", """{"processor":"anim.skeleton","params":{"clips":"${src}/_Export/skin_sword","refPose":"${src}/_Export/meshes/skeleton.json","fingerSrc":"${src}/_Export/skin_sword_ue4"}}"""),
        ("characters/manny/mesh", """{"processor":"skin.mesh","params":{"skeleton":"skeletons/ue5_manny","mesh":"${src}/_Export/meshes/manny.glb"}}"""),
        ("anim/1hm_sword", """{"processor":"anim.pack","params":{"skeleton":"skeletons/ue5_manny","anims":"${src}/_Export/skin_sword","fingerSrc":"${src}/_Export/skin_sword_ue4","sets":{"sword_1h":[]}}}"""),
        ("anim/combat_master", """{"processor":"anim.pack","params":{"skeleton":"skeletons/ue5_manny","anims":"${src}/_Export/skin_combat","fingerSrc":"${src}/_Export/skin_combat_ue4","sets":{"sword_1h":[]}}}"""),
        ("anim/combat_master_ext", """{"processor":"anim.pack","params":{"skeleton":"skeletons/ue5_manny","anims":"${src}/_Export/skin_combat_ext","fingerSrc":"${src}/_Export/skin_combat_ext_ue4","sets":{"katana":["DynamicKatanaAnims*","KatanaAnims"],"sword_shield":["DynamicSwordAndShield*"],"twin_swords":["TwinSwordAnims"],"unarmed":["DynamicHandtoHandAnims"]}}}"""),
        ("anim/gasp", """{"processor":"anim.pack","params":{"skeleton":"skeletons/ue5_manny","anims":"${src}/_Export/skin_gasp","sets":{"locomotion":[]}}}"""),
        ("effects/blood/directional", """{"processor":"texture.array","params":{"layers":["${src}/_Export/blood/Decals/T_BloodSplatter_01.png","${src}/_Export/blood/Decals/T_BloodDirectionalSpots_01.png","${src}/_Export/blood/Splatters/SplatterDirectional01/T_BloodSplatter_Directional_01.png","${src}/_Export/blood/Splatters/SplatterDirectional01/T_BloodSplatter_Directional_S_01.png","${src}/_Export/blood/Splatters/SplatterDirectional04/T_BloodSplatter_Directional_04.png","${src}/_Export/blood/Splatters/SplatterDirectional05/T_BloodSplatter_Directional_05.png","${src}/_Export/blood/Splatters/SplatterDirectional06/T_BloodSplatter_Directional_06.png","${src}/_Export/blood/Splatters/SplatterDirectional07/T_BloodSplatter_Directional_07.png","${src}/_Export/blood/Splatters/SplatterDirectionalThick01/T_BloodSplatter_DirectionalThick_01.png","${src}/_Export/blood/Splatters/SplatterDirectionalThick02/T_BloodSplatter_DirectionalThick_02.png","${src}/_Export/blood/Splatters/SplatterDirectionalThick02/T_BloodSplatter_Directional_S_02.png"],"flipU":["T_BloodSplatter_Directional_04","T_BloodSplatter_Directional_05"],"channel":"luma","size":1024}}"""),
        ("effects/blood/noise", """{"processor":"texture.array","params":{"layers":["${src}/_Export/blood/Noises/T_Noise_0?.png"],"channel":"luma","size":512}}"""),
        ("effects/blood/omni", """{"processor":"texture.array","params":{"layers":["${src}/_Export/blood/Decals/T_BloodDecal_01.png","${src}/_Export/blood/Decals/T_BloodDecal_02.png","${src}/_Export/blood/Splatters/Splatter01/T_BloodSplatter_01.png","${src}/_Export/blood/Splatters/Splatter02/T_BloodSplatter_02.png","${src}/_Export/blood/Splatters/Splatter06/T_BloodSplatter_06.png","${src}/_Export/blood/Splatters/Splatter01/T_BloodSplatter_Smooth_01.png","${src}/_Export/blood/Splatters/Splatter02/T_BloodSplatter_Smooth_02.png","${src}/_Export/blood/Splatters/SplatterThick01/T_BloodSplatter_Thick_01.png","${src}/_Export/blood/Splatters/SplatterThick02/T_BloodSplatter_Thick_02.png","${src}/_Export/blood/Decals/T_BloodSplotch_0?.png","${src}/_Export/blood/Decals/T_Splat_02.png"],"channel":"luma","size":1024}}"""),
        ("effects/blood/puddles", """{"processor":"texture.array","params":{"layers":["${src}/_Export/blood/Decals/T_BloodPuddle_0?.png","${src}/_Export/blood/Decals/T_BloodDecal_03.png","${src}/_Export/blood/Decals/T_Splat_03.png","${src}/_Export/blood/Decals/T_Splat_04.png","${src}/_Export/blood/Decals/Splatters/T_SplatterDecal_0?.png"],"channel":"luma","size":1024}}"""),
        ("effects/blood/spots", """{"processor":"texture.array","params":{"layers":["${src}/_Export/blood/Decals/T_BloodSpots_01.png","${src}/_Export/blood/Shapes/T_CellSpot_01.png"],"channel":"luma","size":1024}}"""),
        ("effects/innards/cut_albedo", """{"processor":"texture.array","params":{"layers":["${assets}/Artstation - Gore & Horror Props Megapack/tex/pile_bone_DefaultMaterial_BaseColor.png@1300,1000,1400#meat","${assets}/Artstation - Gore & Horror Props Megapack/tex/pile_bone_DefaultMaterial_BaseColor.png@3360,2380,380#bone"],"channel":"rgb","size":512,"tile":true}}"""),
        ("characters/manny/anatomy/albedo", """{"processor":"texture.array","params":{"layers":["${src}/_Export/organs/textures/Rebra_D.png","${src}/_Export/organs/textures/Spine_D.png","${src}/_Export/organs/textures/Thigh_D.png","${src}/_Export/organs/textures/Legs_D.png","${src}/_Export/organs/textures/Head_D.png","${src}/_Export/organs/textures/Hand_D.png","${src}/_Export/organs/textures/Wrist_D.png","${src}/_Export/organs/textures/Trachea_D.png","${src}/_Export/organs/textures/L_lung_D.png","${src}/_Export/organs/textures/R_lungs_D.png","${src}/_Export/organs/textures/Glands_D.png","${src}/_Export/organs/textures/Diaphragm_D.png","${src}/_Export/organs/textures/Liver_D.png","${src}/_Export/organs/textures/Stomach_D.png","${src}/_Export/organs/textures/Spleen_D.png","${src}/_Export/organs/textures/Gallbladder_D.png","${src}/_Export/organs/textures/Pancreas_D.png","${src}/_Export/organs/textures/Kidneys_D.png","${src}/_Export/organs/textures/Colon_D.png","${src}/_Export/organs/textures/Heart_D.png","${src}/_Export/organs/textures/brain_D.png"],"channel":"rgb","size":1024}}"""),
        ("characters/manny/anatomy/normals", """{"processor":"texture.array","params":{"layers":["${src}/_Export/organs/textures/Rebra_NM.png","${src}/_Export/organs/textures/Spine_NM.png","${src}/_Export/organs/textures/Thigh_NM.png","${src}/_Export/organs/textures/Legs_NM.png","${src}/_Export/organs/textures/Head_NM.png","${src}/_Export/organs/textures/Hand_NM.png","${src}/_Export/organs/textures/Wrist_NM.png","${src}/_Export/organs/textures/Trachea_NM.png","${src}/_Export/organs/textures/L_Lungs_NM.png","${src}/_Export/organs/textures/R_lungs_NM.png","${src}/_Export/organs/textures/Glands_NM.png","${src}/_Export/organs/textures/Diaphragm_NM.png","${src}/_Export/organs/textures/Liver_NM.png","${src}/_Export/organs/textures/Stomach_NM.png","${src}/_Export/organs/textures/Spleen_NM.png","${src}/_Export/organs/textures/Gallbladder_NM.png","${src}/_Export/organs/textures/Pancreas_NM.png","${src}/_Export/organs/textures/Kidneys_NM.png","${src}/_Export/organs/textures/Colon_NM.png","${src}/_Export/organs/textures/Heart_NM.png"],"channel":"normal","size":1024}}"""),
        ("ui/fonts/body", """{"processor":"font.vector","params":{"font":"${src}/_Sword/fonts/golos-regular.ttf","roles":["Body"],"extra":["${src}/_Sword/fonts/OFL-GolosText.txt"]}}"""),
        ("ui/fonts/head", """{"processor":"font.vector","params":{"font":"${src}/_Sword/fonts/golos-semibold.ttf","roles":["Head"],"extra":[]}}"""),
        ("ui/fonts/label", """{"processor":"font.vector","params":{"font":"${src}/_Sword/fonts/ptnarrow-bold.ttf","roles":["Label"],"extra":["${src}/_Sword/fonts/OFL-PTSansNarrow.txt"]}}"""),
        ("ui/fonts/num", """{"processor":"font.vector","params":{"font":"${src}/_Sword/fonts/plexmono-regular.ttf","roles":["Num"],"extra":["${src}/_Sword/fonts/OFL-IBMPlexMono.txt"]}}"""),
        ("ui/fonts/num_light", """{"processor":"font.vector","params":{"font":"${src}/_Sword/fonts/plexmono-light.ttf","roles":["NumLight"],"extra":[]}}"""),
    ];

    private BakeResult Bake()
    {
        string src = TestPaths.SourceRoot() ?? throw new InvalidOperationException("нет исходников выпечки");
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, BakeProject.FileName), new JsonObject
        {
            ["name"] = "SourceCheck", ["assets"] = ".", ["pak"] = "Content.swpak",
            ["roots"] = new JsonObject { ["src"] = src, ["assets"] = TestPaths.AssetsRoot() },
            ["plugins"] = new JsonArray(TestPaths.Plugin()),
        }.ToJsonString());
        foreach (var (id, json) in Manifests)
        {
            string path = Path.Combine(project, id.Replace('/', Path.DirectorySeparatorChar) + ".bake.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        var watch = Stopwatch.StartNew();
        var (exit, stdout, stderr) = TestPaths.RunCli(project, TimeSpan.FromMinutes(10), "--project", project, "build");
        watch.Stop();
        string text = stdout + stderr;
        Assert.True(exit == 0, $"sword-bake build: код {exit}\n{text}");
        var assets = AssetLine().Matches(text)
            .Select(m => (m.Groups[1].Value, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        double build = Done().Match(text) is { Success: true } d ? double.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        var result = new BakeResult(Path.Combine(project, "Content.swpak"), watch.Elapsed.TotalSeconds, build, assets, text);
        Report("bake", new JsonObject
        {
            ["seconds"] = Math.Round(result.Seconds, 1), ["buildSeconds"] = build,
            ["assets"] = new JsonObject(assets.Select(a => KeyValuePair.Create(a.Item1, (JsonNode?)a.Item2))),
            ["jobs"] = Math.Max(1, Environment.ProcessorCount / 2), ["source"] = src, ["configuration"] = TestPaths.Configuration,
            ["log"] = new JsonArray([.. text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => (JsonNode)l)]),
        });
        return result;
    }

    public static void Report(string key, JsonNode value) => TestPaths.SourceReport(key, value);

    [GeneratedRegex(@"^✓ (\S+) за ([\d.]+) с", RegexOptions.Multiline)]
    private static partial Regex AssetLine();

    [GeneratedRegex(@"готово за ([\d.]+) с")]
    private static partial Regex Done();
}
