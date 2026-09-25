using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sword.Bake.Engine.Tests;
using Sword.Engine.Content;
using Sword.Game.Data;
using Xunit.Abstractions;

namespace Sword.Bake.Game.Tests;

/// <summary>
/// Выпечка настоящих исходников плагином игры (категория Source; без исходников — пропуск): временный проект пекарни
/// с манифестами проекта Bake/ SWORD — риг ue5_manny и меш Manny (на них ссылаются оружие и анатомия), шесть единиц
/// weapon.static, три anatomy.mesh и combat/actions.json (file.copy ядра); плагины — Sword.Bake.Engine, затем
/// Sword.Bake.Game; sword-bake build обычным прогоном, затем сверка с эталонным паком побайтно: меши и зоны крови оружия,
/// меши анатомии, таблица действий. Итог с числами — в выводе теста и artifacts/bake/source-check.json (ключи game*).
/// </summary>
[Trait("Category", "Source")]
public sealed class SourceBakeTests(GameSourceBake bake, ITestOutputHelper output) : IClassFixture<GameSourceBake>
{
    [SourceFact]
    public void Weapons_MatchReferenceByteForByte() => SameFiles("gameWeapons", WeaponMesh.Dir, WeaponMesh.Ext, 6);

    [SourceFact]
    public void WeaponBlood_MatchesReferenceByteForByte() => SameFiles("gameWeaponBlood", WeaponMesh.Dir, WeaponBloodData.Extension, 6);

    [SourceFact]
    public void Anatomy_MatchesReferenceByteForByte() => SameFiles("gameAnatomy", "characters/manny/anatomy", ".swmesh", 3);

    [SourceFact]
    public void Actions_MatchReferenceByteForByte() => SameFiles("gameActions", "combat", ".json", 1);

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
        TestPaths.SourceReport(key, new JsonObject
        {
            ["reference"] = paths.Count, ["matched"] = paths.Count - problems.Count, ["extra"] = new JsonArray([.. extra.Select(e => (JsonNode)e)]),
            ["files"] = files,
        });
        Assert.Equal(expected, paths.Count);
        Assert.Empty(extra);
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }
}

/// <summary>Одна выпечка исходников на класс тестов: временный проект с обоими плагинами, sword-bake build, пак — во временном каталоге.</summary>
public sealed partial class GameSourceBake : IDisposable
{
    private readonly Lazy<(string Pak, string Log)> _result;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-game-tests", "source-" + Guid.NewGuid().ToString("N")[..12]);

    public GameSourceBake() => _result = new(Bake, LazyThreadSafetyMode.ExecutionAndPublication);

    public (string Pak, string Log) Result => _result.Value;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Манифесты проекта Bake/ SWORD 2eab13b — те же параметры, без полей import и notes.</summary>
    private static readonly (string Id, string Json)[] Manifests =
    [
        ("skeletons/ue5_manny", """{"processor":"anim.skeleton","params":{"clips":"${src}/_Export/skin_sword","refPose":"${src}/_Export/meshes/skeleton.json","fingerSrc":"${src}/_Export/skin_sword_ue4"}}"""),
        ("characters/manny/mesh", """{"processor":"skin.mesh","params":{"skeleton":"skeletons/ue5_manny","mesh":"${src}/_Export/meshes/manny.glb"}}"""),
        ("weapons/combat_sword", """{"processor":"weapon.static","params":{"mesh":"${src}/_Export/weapons/SM_CombatSword.glb","skeleton":"skeletons/ue5_manny"}}"""),
        ("weapons/katana", """{"processor":"weapon.static","params":{"mesh":"${src}/_Export/weapons/SM_Katana.glb","skeleton":"skeletons/ue5_manny"}}"""),
        ("weapons/katana_v2", """{"processor":"weapon.static","params":{"mesh":"${src}/_Export/weapons/SM_Katana_V2.glb","skeleton":"skeletons/ue5_manny"}}"""),
        ("weapons/powerful_sword", """{"processor":"weapon.static","params":{"mesh":"${src}/_Export/weapons/SM_PowerfulSword.glb","skeleton":"skeletons/ue5_manny"}}"""),
        ("weapons/sword_1h", """{"processor":"weapon.static","params":{"mesh":"${src}/_Export/meshes/sword_1h.glb","skeleton":"skeletons/ue5_manny","grip":9}}"""),
        ("weapons/sword_dss", """{"processor":"weapon.static","params":{"mesh":"${src}/_Export/weapons/SM_Sword_DSS.glb","skeleton":"skeletons/ue5_manny"}}"""),
        ("characters/manny/anatomy/skeleton", """{"processor":"anatomy.mesh","params":{"mesh":"${src}/_Export/organs/SK_Skeleton.glb","skeleton":"skeletons/ue5_manny","skin":"characters/manny/mesh","materials":["Ribs","Spine","Thigh","Legs","Head","Hands","Wrist"]}}"""),
        ("characters/manny/anatomy/organs", """{"processor":"anatomy.mesh","params":{"mesh":"${src}/_Export/organs/SK_SkeletonWithOrgans.glb","skeleton":"skeletons/ue5_manny","skin":"characters/manny/mesh","materials":["Ribs","Spine","Thigh","Legs","Head","Hands","Wrist","Trachea","LungL","LungR","Glands","Diaphragm","Liver","Stomach","Spleen","Gallbladder","Pancreas","Kidneys","Colon","Heart"],"keep":["Trachea","LungL","LungR","Glands","Diaphragm","Liver","Stomach","Spleen","Gallbladder","Pancreas","Kidneys","Colon","Heart"]}}"""),
        ("characters/manny/anatomy/brain", """{"processor":"anatomy.mesh","params":{"mesh":"${src}/_Export/organs/BrainHighPoly.glb","skeleton":"skeletons/ue5_manny","skin":"characters/manny/mesh","materials":["Brain"],"attachBone":"head","attachCenter":[0,1.722,0.028],"attachSize":0.155,"maxTriangles":16000}}"""),
        ("combat/actions", """{"processor":"file.copy","params":{"from":"${src}/_Sword/anim/actions.json","to":"combat/actions.json"}}"""),
    ];

    private (string Pak, string Log) Bake()
    {
        string src = TestPaths.SourceRoot() ?? throw new InvalidOperationException("нет исходников выпечки");
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, BakeProject.FileName), new JsonObject
        {
            ["name"] = "SourceCheckGame", ["assets"] = ".", ["pak"] = "Content.swpak",
            ["roots"] = new JsonObject { ["src"] = src },
            ["plugins"] = new JsonArray(TestPaths.Plugin("Sword.Bake.Engine"), TestPaths.Plugin("Sword.Bake.Game")),
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
            .Select(m => KeyValuePair.Create(m.Groups[1].Value, (JsonNode?)double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        TestPaths.SourceReport("gameBake", new JsonObject
        {
            ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            ["buildSeconds"] = Done().Match(text) is { Success: true } d ? double.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            ["assets"] = new JsonObject(assets), ["source"] = src, ["configuration"] = TestPaths.Configuration,
            ["log"] = new JsonArray([.. text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => (JsonNode)l)]),
        });
        return (Path.Combine(project, "Content.swpak"), text);
    }

    [GeneratedRegex(@"^✓ (\S+) за ([\d.]+) с", RegexOptions.Multiline)]
    private static partial Regex AssetLine();

    [GeneratedRegex(@"готово за ([\d.]+) с")]
    private static partial Regex Done();
}
