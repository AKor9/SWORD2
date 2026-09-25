using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sword.Bake.Engine.Tests;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Game.Data;
using Xunit.Abstractions;

namespace Sword.Bake.Game.Tests;

/// <summary>
/// Выпечка настоящих исходников плагином игры (категория Source; без исходников — пропуск): временный проект пекарни
/// с манифестами проекта Bake/ SWORD — риг ue5_manny и меш Manny (на них ссылаются оружие и анатомия), шесть единиц
/// weapon.static, три anatomy.mesh и combat/actions.json (file.copy ядра); плагины — Sword.Bake.Engine, затем
/// Sword.Bake.Game; sword-bake build обычным прогоном, затем сверка с эталонным паком: меши анатомии и таблица действий —
/// побайтно; оружие — та же геометрия, перенесённая одним жёстким преобразованием на меч (хват по центрам изгиба пальцев,
/// а не по линии костяшек, как в эталоне). Итог с числами — в выводе теста и artifacts/bake/source-check.json (ключи game*).
/// </summary>
[Trait("Category", "Source")]
public sealed class SourceBakeTests(GameSourceBake bake, ITestOutputHelper output) : IClassFixture<GameSourceBake>
{
    /// <summary>Допуск жёсткого переноса оружия: м у положений, доля у направлений.</summary>
    private const float RigidTolerance = 1e-4f;

    /// <summary>
    /// Оружие эталона и выпечки — одна геометрия в разном хвате: треугольники, UV, секции и разметка зон крови те же; вершины,
    /// нормали, клинок и зоны крови — эталонные, перенесённые одним жёстким преобразованием на меч; хват сдвинут (не тождество).
    /// </summary>
    [SourceFact]
    public void Weapons_AreReferenceMovedRigidly()
    {
        using var reference = PakSource.Open(TestPaths.ReferencePak());
        using var baked = PakSource.Open(bake.Result.Pak);
        var paths = reference.List(WeaponMesh.Dir, WeaponMesh.Ext);
        var extra = baked.List(WeaponMesh.Dir, WeaponMesh.Ext).Except(paths, StringComparer.OrdinalIgnoreCase).ToList();
        var files = new JsonArray();
        var problems = new List<string>();
        foreach (string path in paths)
        {
            string line = baked.Find(path) is null ? $"{path}: нет в выпечке" : CompareWeapon(reference, baked, path, files);
            output.WriteLine(line);
            if (!line.Contains("перенесённый жёстко")) problems.Add(line);
        }
        TestPaths.SourceReport("gameWeapons", new JsonObject
        {
            ["reference"] = paths.Count, ["matched"] = paths.Count - problems.Count, ["extra"] = new JsonArray([.. extra.Select(e => (JsonNode)e)]),
            ["files"] = files,
        });
        Assert.Equal(6, paths.Count);
        Assert.Empty(extra);
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>Меш и зоны крови меча <paramref name="path"/>: строка итога («перенесённый жёстко» — годен), строка отчёта — в <paramref name="files"/>.</summary>
    private static string CompareWeapon(PakSource reference, PakSource baked, string path, JsonArray files)
    {
        var a = SkinnedMeshData.Read(reference.OpenRead(path), path);
        var b = SkinnedMeshData.Read(baked.OpenRead(path), path);
        string blood = Path.ChangeExtension(path, WeaponBloodData.Extension).Replace('\\', '/');
        var ba = WeaponBloodData.Read(reference.OpenRead(blood), blood);
        var bb = WeaponBloodData.Read(baked.OpenRead(blood), blood);
        bool same = a.Vertices.Length == b.Vertices.Length && a.Indices.AsSpan().SequenceEqual(b.Indices)
            && a.Sections.SequenceEqual(b.Sections) && ba.Zones.Length == bb.Zones.Length && ba.VertexZones.AsSpan().SequenceEqual(bb.VertexZones);
        if (!same)
        {
            files.Add(new JsonObject { ["path"] = path, ["same"] = false });
            return $"{path}: другая геометрия, секции или разметка зон крови";
        }
        var (q, t) = Rigid(a.Vertices.Select(v => v.Position).ToArray(), b.Vertices.Select(v => v.Position).ToArray());
        Vector3 Point(Vector3 p) => Vector3.Transform(p, q) + t;
        float worst = 0, moved = 0;
        void Near(Vector3 expected, Vector3 actual) => worst = MathF.Max(worst, Vector3.Distance(expected, actual));
        for (int v = 0; v < a.Vertices.Length; v++)
        {
            Near(Point(a.Vertices[v].Position), b.Vertices[v].Position);
            Near(Vector3.Transform(a.Vertices[v].Normal, q), b.Vertices[v].Normal);
            same &= a.Vertices[v].Uv == b.Vertices[v].Uv;
            moved = MathF.Max(moved, Vector3.Distance(a.Vertices[v].Position, b.Vertices[v].Position));
        }
        Near(Point(ba.Base), bb.Base);
        Near(Point(ba.Tip), bb.Tip);
        for (int z = 0; z < ba.Zones.Length; z++)
        {
            var (za, zb) = (ba.Zones[z], bb.Zones[z]);
            same &= (za with { Origin = zb.Origin, AxisU = zb.AxisU, AxisV = zb.AxisV, Length = zb.Length, Half = zb.Half }) == zb
                && MathF.Abs(za.Length - zb.Length) < RigidTolerance && MathF.Abs(za.Half - zb.Half) < RigidTolerance;
            Near(Point(za.Origin), zb.Origin);
            Near(Vector3.Transform(za.AxisU, q), zb.AxisU);
            Near(Vector3.Transform(za.AxisV, q), zb.AxisV);
        }
        files.Add(new JsonObject { ["path"] = path, ["same"] = same, ["deviation"] = worst, ["moved"] = moved });
        return !same ? $"{path}: другие UV или зоны крови"
            : worst > RigidTolerance ? string.Create(CultureInfo.InvariantCulture, $"{path}: не жёсткий перенос эталона, отклонение {worst * 100:F3} см")
            : moved < 1e-3f ? $"{path}: хват как в эталоне — выпечка старая"
            : string.Create(CultureInfo.InvariantCulture, $"{path}: эталон, перенесённый жёстко (вершины до {moved * 100:F1} см), отклонение {worst * 100:F4} см");
    }

    /// <summary>Поворот и перенос, переводящие точки <paramref name="a"/> в <paramref name="b"/> по наименьшим квадратам (метод Хорна).</summary>
    private static (Quaternion Q, Vector3 T) Rigid(Vector3[] a, Vector3[] b)
    {
        Vector3 ca = Vector3.Zero, cb = Vector3.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            ca += a[i];
            cb += b[i];
        }
        ca /= a.Length;
        cb /= b.Length;
        var s = new double[3, 3];
        for (int i = 0; i < a.Length; i++)
        {
            var (p, r) = (a[i] - ca, b[i] - cb);
            double[] pa = [p.X, p.Y, p.Z], rb = [r.X, r.Y, r.Z];
            for (int u = 0; u < 3; u++)
                for (int w = 0; w < 3; w++) s[u, w] += pa[u] * rb[w];
        }
        double[,] n =
        {
            { s[0, 0] + s[1, 1] + s[2, 2], s[1, 2] - s[2, 1], s[2, 0] - s[0, 2], s[0, 1] - s[1, 0] },
            { s[1, 2] - s[2, 1], s[0, 0] - s[1, 1] - s[2, 2], s[0, 1] + s[1, 0], s[2, 0] + s[0, 2] },
            { s[2, 0] - s[0, 2], s[0, 1] + s[1, 0], -s[0, 0] + s[1, 1] - s[2, 2], s[1, 2] + s[2, 1] },
            { s[0, 1] - s[1, 0], s[2, 0] + s[0, 2], s[1, 2] + s[2, 1], -s[0, 0] - s[1, 1] + s[2, 2] },
        };
        // собственный вектор наибольшего собственного числа — вращениями Якоби (n симметрична)
        var vec = new double[4, 4];
        for (int u = 0; u < 4; u++) vec[u, u] = 1;
        for (int sweep = 0; sweep < 64; sweep++)
            for (int p = 0; p < 3; p++)
                for (int r = p + 1; r < 4; r++)
                {
                    if (Math.Abs(n[p, r]) < 1e-300) continue;
                    double theta = (n[r, r] - n[p, p]) / (2 * n[p, r]);
                    double tan = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(tan * tan + 1), sn = tan * c;
                    for (int k = 0; k < 4; k++)
                    {
                        double kp = n[k, p], kr = n[k, r];
                        n[k, p] = c * kp - sn * kr;
                        n[k, r] = sn * kp + c * kr;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double pk = n[p, k], rk = n[r, k];
                        n[p, k] = c * pk - sn * rk;
                        n[r, k] = sn * pk + c * rk;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double kp = vec[k, p], kr = vec[k, r];
                        vec[k, p] = c * kp - sn * kr;
                        vec[k, r] = sn * kp + c * kr;
                    }
                }
        int best = 0;
        for (int u = 1; u < 4; u++)
            if (n[u, u] > n[best, best]) best = u;
        var q = Quaternion.Normalize(new Quaternion((float)vec[1, best], (float)vec[2, best], (float)vec[3, best], (float)vec[0, best]));
        return (q, cb - Vector3.Transform(ca, q));
    }

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
