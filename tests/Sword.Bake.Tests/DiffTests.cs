using System.Text;
using System.Text.Json.Nodes;
using Sword.Bake.Cli;
using Sword.Engine.Content;

namespace Sword.Bake.Tests;

/// <summary>
/// <c>sword-bake diff</c> (<see cref="PakDiff"/>) на синтетических паках (<see cref="PakWriter"/>): оглавление, сравнитель формата
/// из тестового плагина A (<c>.skel</c>: строки <c>source:</c> — не содержимое), семантика <c>.bake/build.json</c>, файл ожидаемых
/// расхождений, итог <c>--json</c> и коды выхода процесса.
/// </summary>
public sealed class DiffTests : IDisposable
{
    private const string PluginA = "Sword.Bake.Tests.PluginA";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));

    public DiffTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── синтетические паки ──────────────────────────────────────────────

    /// <summary>Пак из текстовых файлов; <paramref name="info"/> — сведения о сборке по записям оглавления (последним файлом, как у пекарни).</summary>
    private string Pak(string name, IReadOnlyDictionary<string, string> files, Func<IReadOnlyDictionary<string, PakEntry>, PakBuildInfo>? info = null)
    {
        string path = Path.Combine(_root, name + PakFormat.Extension);
        using var writer = new PakWriter(path);
        var entries = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, text) in files)
        {
            using var s = new MemoryStream(Encoding.UTF8.GetBytes(text));
            entries[p] = writer.Add(p, s);
        }
        if (info is not null)
        {
            using var s = new MemoryStream(info(entries).ToUtf8());
            writer.Add(PakBuildInfo.EntryPath, s);
        }
        writer.Commit();
        return path;
    }

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Сведения о сборке одного ассета «skeletons/manny» (обработчик плагина A) с выходами <paramref name="outputs"/>: время
    /// сборки и MVID в отпечатке — из <paramref name="build"/> (сравнение их не видит), остальное — правится <paramref name="edit"/>.
    /// </summary>
    private static Func<IReadOnlyDictionary<string, PakEntry>, PakBuildInfo> Info(int build, string[] outputs, Action<PakAssetRecord>? edit = null) =>
        entries =>
        {
            var time = new DateTime(2026, 9, 20 + build, 12, 0, 0, DateTimeKind.Utc);
            var rec = new PakAssetRecord
            {
                Processor = "testa.skeleton",
                Fingerprint = $"testa.skeleton|1|{PluginA}:{build:x12},Sword.Engine.Content:{build * 7:x12}",
                ParamsHash = "0123456789ABCDEF0123456789ABCDEF",
                BakedUtc = time,
                Inputs = [new PakFileRecord { Path = @"D:\Sources\joints.txt", Size = 12, Mtime = time.Ticks, Hash = "AAAABBBBCCCCDDDDAAAABBBBCCCCDDDD" }],
                InputDirs = [@"D:\Sources\extra"],
                ContentInputs = ["skeletons/base.skel"],
                Outputs = [.. outputs.Select(o => new PakFileRecord { Path = o, Size = entries[o].Size, Hash = PakFormat.HashText(entries[o].Hash) })],
            };
            edit?.Invoke(rec);
            return new PakBuildInfo { Project = "Diff" + build, BuiltUtc = time.AddMinutes(1), Assets = { ["skeletons/manny"] = rec } };
        };

    /// <summary>Сравнители плагина A — как их найдёт проект (рефлексией при загрузке плагина).</summary>
    private IReadOnlyList<IContentComparer> PluginComparers()
    {
        string dir = Path.Combine(_root, "project");
        CreateProject(dir);
        var project = BakeProject.Open(dir);
        Assert.Empty(project.Problems);
        return project.Processors.Comparers;
    }

    private static void CreateProject(string dir)
    {
        BakeProject.Create(dir, "Diff");
        string file = Path.Combine(dir, BakeProject.FileName);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["plugins"] = new JsonArray((JsonNode)TestPaths.Plugin(PluginA));
        TextFiles.WriteJson(file, json);
    }

    private static FileDiff FileOf(PakDiffReport r, string path) => r.Files.Single(f => f.Path == path);

    // ── оглавление ──────────────────────────────────────────────────────

    [Fact]
    public void SamePaks_AllSame_BuildInfoEqual()
    {
        var files = Files(("data/a.txt", "a"), ("skeletons/manny.skel", "skeleton:pelvis\nsource:1"));
        string a = Pak("a", files, Info(1, ["skeletons/manny.skel"]));
        string b = Pak("b", files, Info(1, ["skeletons/manny.skel"]));

        var r = PakDiff.Compare(a, b, []);

        Assert.True(r.Ok);
        Assert.Equal(0, r.Unexpected);
        Assert.Equal(2, r.Files.Count);   // .bake/build.json есть в обоих — он в сведениях о сборке, не в файлах
        Assert.All(r.Files, f => Assert.Equal(FileDiffKind.Same, f.Kind));
        Assert.Equal("skeletons/manny", FileOf(r, "skeletons/manny.skel").Owner);
        Assert.True(r.BuildInfo.Equal);
        Assert.Equal((3, 3), (r.A.Files, r.B.Files));
        Assert.Equal(r.A.ContentHash, r.B.ContentHash);
    }

    [Fact]
    public void OnlyInOnePak_AndOtherSize_AreUnexpected()
    {
        string a = Pak("a", Files(("data/gone.txt", "x"), ("data/grow.txt", "abc"), ("data/same.txt", "s")));
        string b = Pak("b", Files(("data/grow.txt", "abcd"), ("data/same.txt", "s"), ("data/new.txt", "n")));

        var r = PakDiff.Compare(a, b, []);

        Assert.False(r.Ok);
        Assert.Equal(3, r.Unexpected);
        Assert.Equal(FileDiffKind.OnlyA, FileOf(r, "data/gone.txt").Kind);
        Assert.Equal(FileDiffKind.OnlyB, FileOf(r, "data/new.txt").Kind);
        var grow = FileOf(r, "data/grow.txt");
        Assert.Equal(FileDiffKind.Size, grow.Kind);
        Assert.Equal("размер 3 ≠ 4; отличаются байтами", grow.Detail);
        Assert.Null(grow.Comparer);
        Assert.Equal(FileDiffKind.Same, FileOf(r, "data/same.txt").Kind);
        Assert.False(r.BuildInfo.InA || r.BuildInfo.InB);
    }

    [Fact]
    public void OtherHash_PluginComparer_DecidesByContent()
    {
        string a = Pak("a", Files(
            ("skeletons/stamp.skel", "skeleton:pelvis\nsource:1111"),
            ("skeletons/bone.skel", "skeleton:pelvis\nsource:1"),
            ("skeletons/broken.skel", "skeleton:x"),
            ("data/blob.bin", "12")));
        string b = Pak("b", Files(
            ("skeletons/stamp.skel", "skeleton:pelvis\nsource:2222"),
            ("skeletons/bone.skel", "skeleton:thighs\nsource:1"),
            ("skeletons/broken.skel", "garbage!!"),
            ("data/blob.bin", "13")));

        // без сравнителей (вне проекта) — только байты
        var bytes = PakDiff.Compare(a, b, []);
        Assert.Equal(4, bytes.Unexpected);
        Assert.All(bytes.Files, f => Assert.EndsWith("отличаются байтами", f.Detail));

        // сравнитель .skel найден в плагине A рефлексией
        var comparers = PluginComparers();
        var skel = Assert.Single(comparers);
        Assert.Equal([".skel"], skel.Extensions);

        var r = PakDiff.Compare(a, b, comparers);
        var stamp = FileOf(r, "skeletons/stamp.skel");
        Assert.Equal(FileDiffKind.SameContent, stamp.Kind);   // отличается только source: — равны по содержимому
        Assert.False(stamp.IsDiscrepancy);
        Assert.Equal(skel.Title, stamp.Comparer);
        Assert.Equal("без строк source:", stamp.Detail);

        var bone = FileOf(r, "skeletons/bone.skel");
        Assert.Equal(FileDiffKind.Hash, bone.Kind);
        Assert.Equal("строка 1: «skeleton:pelvis» ≠ «skeleton:thighs»", bone.Detail);

        var broken = FileOf(r, "skeletons/broken.skel");   // сбой сравнителя — расхождение с текстом ошибки, не отказ diff
        Assert.Equal(FileDiffKind.Size, broken.Kind);
        Assert.Contains("сравнитель не разобрал", broken.Detail);
        Assert.Contains("не скелет", broken.Detail);

        Assert.Equal("отличаются байтами", FileOf(r, "data/blob.bin").Detail);   // .bin — сравнителя нет
        Assert.Equal(3, r.Unexpected);
        var info = Assert.Single(r.Comparers);
        Assert.Equal((skel.Title, PluginA, ".skel"), (info.Title, info.Assembly, Assert.Single(info.Extensions)));

        Assert.Throws<ArgumentException>(() => PakDiff.Compare(a, b, [skel, skel]));   // два сравнителя на расширение
    }

    // ── сведения о сборке ──────────────────────────────────────────────

    [Fact]
    public void BuildInfo_TimeMvidAndInputTimeIgnored()
    {
        var files = Files(("skeletons/manny.skel", "skeleton:pelvis"));
        string a = Pak("a", files, Info(1, ["skeletons/manny.skel"]));
        string b = Pak("b", files, Info(2, ["skeletons/manny.skel"]));   // другие builtUtc, bakedUtc, MVID, mtime входа, имя проекта

        var r = PakDiff.Compare(a, b, []);

        Assert.NotEqual(r.A.BuiltUtc, r.B.BuiltUtc);
        Assert.True(r.BuildInfo.Compared);
        Assert.True(r.BuildInfo.Equal, string.Join("; ", r.BuildInfo.Differences));
        Assert.True(r.Ok);
        Assert.Equal("anim.pack|1", PakDiff.FingerprintPrefix("anim.pack|1|Sword.Anim:362c0533bddf,Sword.Bake.Sword:9f5912cda2ec"));
    }

    [Fact]
    public void BuildInfo_ParamsInputsVersionAndAssetsAreDifferences()
    {
        var files = Files(("skeletons/manny.skel", "skeleton:pelvis"));
        string a = Pak("a", files, Info(1, ["skeletons/manny.skel"]));
        string b = Pak("b", files, entries =>
        {
            var info = Info(2, ["skeletons/manny.skel"], rec =>
            {
                rec.ParamsHash = "FFFF456789ABCDEF0123456789ABCDEF";
                rec.Fingerprint = rec.Fingerprint.Replace("|1|", "|2|", StringComparison.Ordinal);
                rec.Inputs[0].Hash = "0000BBBBCCCCDDDDAAAABBBBCCCCDDDD";
                rec.Inputs.Add(new PakFileRecord { Path = @"D:\Sources\more.txt", Size = 1, Hash = "11" });
                rec.InputDirs.Clear();
                rec.ContentInputs.Add("skeletons/other.skel");
            })(entries);
            info.Assets["skeletons/extra"] = new PakAssetRecord { Processor = "file.copy", Fingerprint = "file.copy|1|Sword.Bake:0" };
            return info;
        });

        var r = PakDiff.Compare(a, b, []);

        Assert.False(r.Ok);
        var d = r.BuildInfo.Differences;
        var paramsHash = Assert.Single(d, x => x.Field == PakDiff.Fields.ParamsHash);
        Assert.Equal(PakDiff.ParamsHashNote, paramsHash.Note);   // пометка: зависит от корней
        var fingerprint = Assert.Single(d, x => x.Field == PakDiff.Fields.Fingerprint);
        Assert.Equal(("testa.skeleton|1", "testa.skeleton|2"), (fingerprint.A, fingerprint.B));
        Assert.Contains(d, x => x is { Field: PakDiff.Fields.Inputs, Path: @"D:\Sources\joints.txt", Detail: "вход: другой хеш" });
        Assert.Contains(d, x => x is { Field: PakDiff.Fields.Inputs, Path: @"D:\Sources\more.txt", A: null, Detail: "вход только в B" });
        Assert.Contains(d, x => x is { Field: PakDiff.Fields.InputDirs, Path: @"D:\Sources\extra", B: null });
        Assert.Contains(d, x => x is { Field: PakDiff.Fields.ContentInputs, Path: "skeletons/other.skel", A: null });
        var extra = Assert.Single(d, x => x.Asset == "skeletons/extra");
        Assert.Equal((PakDiff.Fields.Asset, ExpectKind.Absent, "ассет только в B"), (extra.Field, extra.ExpectKind, extra.Detail));
        Assert.Equal(7, d.Count);
        Assert.Equal(7, r.Unexpected);
        Assert.DoesNotContain(r.Files, x => x.IsDiscrepancy);
    }

    [Fact]
    public void BuildInfo_OutputsFollowComparerVerdict()
    {
        string a = Pak("a", Files(("skeletons/manny.skel", "skeleton:pelvis\nsource:1"), ("skeletons/hand.skel", "skeleton:hand")),
            Info(1, ["skeletons/manny.skel", "skeletons/hand.skel"]));
        string b = Pak("b", Files(("skeletons/manny.skel", "skeleton:pelvis\nsource:2"), ("skeletons/hand.skel", "skeleton:foot")),
            Info(2, ["skeletons/manny.skel", "skeletons/hand.skel"]));

        // без сравнителя: оба выхода отличаются — и файлом, и выходом в сведениях о сборке
        var bytes = PakDiff.Compare(a, b, []);
        Assert.Equal(2, bytes.BuildInfo.Differences.Count(x => x.Field == PakDiff.Fields.Outputs));

        // со сравнителем: manny равен по содержимому — и выход не расхождение; hand — расхождение с текстом сравнителя
        var r = PakDiff.Compare(a, b, PluginComparers());
        Assert.Equal(FileDiffKind.SameContent, FileOf(r, "skeletons/manny.skel").Kind);
        var output = Assert.Single(r.BuildInfo.Differences);
        Assert.Equal((PakDiff.Fields.Outputs, "skeletons/hand.skel"), (output.Field, output.Path));
        Assert.Equal("выход: другой хеш; строка 1: «skeleton:hand» ≠ «skeleton:foot»", output.Detail);
        Assert.Equal(2, r.Unexpected);   // файл hand и его выход
    }

    // ── ожидаемые расхождения ──────────────────────────────────────────

    [Fact]
    public void Expectations_SplitExpectedAndUnexpected_UnusedRulesNamed()
    {
        string a = Pak("a", Files(("effects/blood/a.tex", "1"), ("effects/blood/b.tex", "2"), ("weapons/katana.mesh", "k"), ("data/x.txt", "x")),
            Info(1, ["weapons/katana.mesh"]));
        string b = Pak("b", Files(("effects/blood/a.tex", "11"), ("weapons/katana.mesh", "K"), ("data/x.txt", "y")),
            Info(2, ["weapons/katana.mesh"], rec => rec.ParamsHash = "changed"));
        string file = Path.Combine(_root, "expected.json");
        File.WriteAllText(file, """
            {
              // кровь переделывается — решение 15
              "expected": [
                { "path": "effects/blood/*", "expect": "absent", "reason": "кровь: не печётся" },
                { "path": "effects/blood", "expect": "different", "reason": "кровь: другая" },
                { "path": "weapons/*.mesh", "expect": "different", "reason": "меш оружия: другой" },
                { "asset": "skeletons/manny", "expect": "different", "reason": "параметры скелета правили" },
                { "asset": "anim/*", "expect": "absent", "reason": "анимации нет" },
              ]
            }
            """);
        var expectations = DiffExpectations.Load(file);
        Assert.Equal(5, expectations.Rules.Count);

        var r = PakDiff.Compare(a, b, [], expectations);

        Assert.Equal("кровь: не печётся", FileOf(r, "effects/blood/b.tex").Expected?.Reason);   // только в A — absent
        Assert.Equal("кровь: другая", FileOf(r, "effects/blood/a.tex").Expected?.Reason);   // папка, размер другой — different
        Assert.Equal("меш оружия: другой", FileOf(r, "weapons/katana.mesh").Expected?.Reason);
        Assert.Null(FileOf(r, "data/x.txt").Expected);
        // сведения о сборке: paramsHash — по правилу ассета, выход katana — по правилу пути (как файл)
        Assert.All(r.BuildInfo.Differences, d => Assert.NotNull(d.Expected));
        Assert.Contains(r.BuildInfo.Differences, d => d.Field == PakDiff.Fields.Outputs && d.Expected!.Reason == "параметры скелета правили");
        Assert.Equal(1, r.Unexpected);   // data/x.txt
        Assert.Equal(5, r.Expected);     // 3 файла + paramsHash + выход
        Assert.False(r.Ok);
        Assert.Equal("anim/*", Assert.Single(r.UnusedRules).Asset);

        // правило пути для выхода, если правила ассета нет
        var byPath = PakDiff.Compare(a, b, [], DiffExpectations.Parse("""
            { "expected": [ { "path": "weapons", "expect": "different", "reason": "оружие" } ] }
            """, "inline"));
        Assert.Equal("оружие", Assert.Single(byPath.BuildInfo.Differences, d => d.Field == PakDiff.Fields.Outputs).Expected?.Reason);
        Assert.Null(Assert.Single(byPath.BuildInfo.Differences, d => d.Field == PakDiff.Fields.ParamsHash).Expected);
    }

    [Theory]
    [InlineData("""{ "expected": [ { "path": "a", "expect": "absent", "reason": "r", "typo": 1 } ] }""", "typo")]
    [InlineData("""{ "expected": [ { "path": "a", "expect": "absent" } ] }""", "reason")]
    [InlineData("""{ "expected": [ { "path": "a", "asset": "b", "expect": "absent", "reason": "r" } ] }""", "ровно одно")]
    [InlineData("""{ "expected": [ { "path": "a", "expect": "gone", "reason": "r" } ] }""", "gone")]
    [InlineData("""{ "rules": [] }""", "rules")]
    [InlineData("""{ }""", "expected")]
    public void Expectations_BadFileIsAnError(string json, string mentions)
    {
        var e = Assert.Throws<InvalidDataException>(() => DiffExpectations.Parse(json, "ожидания.json"));
        Assert.Contains("ожидания.json", e.Message);
        Assert.Contains(mentions, e.Message);
    }

    // ── итог --json и процесс ──────────────────────────────────────────

    [Fact]
    public void DiffJson_SummaryListsBuildInfoAndSplit()
    {
        string a = Pak("a", Files(("skeletons/manny.skel", "skeleton:pelvis\nsource:1"), ("data/gone.txt", "g"), ("data/x.txt", "x")),
            Info(1, ["skeletons/manny.skel"]));
        string b = Pak("b", Files(("skeletons/manny.skel", "skeleton:pelvis\nsource:2"), ("data/x.txt", "y")),
            Info(2, ["skeletons/manny.skel"], rec => rec.ParamsHash = "changed"));
        var expectations = DiffExpectations.Parse("""{ "expected": [ { "path": "data/gone.txt", "expect": "absent", "reason": "убран" } ] }""", "inline");
        var r = PakDiff.Compare(a, b, PluginComparers(), expectations);

        var json = CliJson.Diff(r, null, 2);

        Assert.Equal("diff", (string)json["command"]!);
        Assert.False((bool)json["ok"]!);
        Assert.Equal(2, (int)json["exitCode"]!);
        Assert.Null(json["project"]);
        Assert.Equal(".skel", (string)json["comparers"]![0]!["extensions"]![0]!);
        var summary = json["summary"]!;
        Assert.Equal((0, 1, 1, 0, 0, 1, 1, 1, 2),
            ((int)summary["same"]!, (int)summary["sameContent"]!, (int)summary["onlyA"]!, (int)summary["onlyB"]!, (int)summary["size"]!,
             (int)summary["hash"]!, (int)summary["buildInfo"]!, (int)summary["expected"]!, (int)summary["unexpected"]!));
        var same = json["sameContent"]![0]!;
        Assert.Equal(("skeletons/manny.skel", "skeletons/manny", "без строк source:"), ((string)same["path"]!, (string)same["owner"]!, (string)same["detail"]!));
        Assert.Equal("убран", (string)json["onlyA"]![0]!["expected"]!);
        var hash = json["hash"]![0]!;
        Assert.Equal(("data/x.txt", 1L, 1L), ((string)hash["path"]!, (long)hash["sizeA"]!, (long)hash["sizeB"]!));
        Assert.NotEqual((string)hash["hashA"]!, (string)hash["hashB"]!);
        var bi = json["buildInfo"]!;
        Assert.False((bool)bi["equal"]!);
        Assert.Equal(PakDiff.Fields.ParamsHash, (string)bi["differences"]![0]!["field"]!);
        Assert.Equal(PakDiff.ParamsHashNote, (string)bi["differences"]![0]!["note"]!);

        var unexpected = json["unexpected"]!.AsArray();
        Assert.Equal(["file:data/x.txt:hash:different", "buildInfo:skeletons/manny:paramsHash:different"],
            unexpected.Select(u => $"{(string?)u!["source"]}:{(string?)u["path"] ?? (string?)u["asset"]}:{(string?)u["kind"] ?? (string?)u["field"]}:{(string?)u["expect"]}"));
        var expected = Assert.Single(json["expected"]!.AsArray())!;
        Assert.Equal(("data/gone.txt", "onlyA", "absent", 1), ((string)expected["path"]!, (string)expected["kind"]!, (string)expected["expect"]!, (int)expected["rule"]!["rule"]!));
        Assert.Empty(json["expectations"]!["unused"]!.AsArray());
    }

    private static JsonNode Single(string stdout)
    {
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));   // одна строка — один объект
        return JsonNode.Parse(stdout)!;
    }

    [Fact]
    public void CliProcess_DiffExitCodes_ProjectGivesComparers()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string a = Pak("a", Files(("skeletons/manny.skel", "skeleton:pelvis\nsource:1"), ("data/x.txt", "x")));
        string b = Pak("b", Files(("skeletons/manny.skel", "skeleton:pelvis\nsource:2"), ("data/x.txt", "x")));

        // сам с собой — 0
        var (exit, stdout, stderr) = TestPaths.RunCliIn(outside, "diff", a, a, "--json");
        Assert.True(exit == 0, stderr);
        var same = Single(stdout);
        Assert.True((bool)same["ok"]!);
        Assert.Equal(2, (int)same["summary"]!["same"]!);

        // вне проекта сравнителей нет: .skel отличается байтами — 2
        (exit, stdout, _) = TestPaths.RunCliIn(outside, "diff", a, b, "--json");
        var bytes = Single(stdout);
        Assert.Equal(2, exit);
        Assert.Null(bytes["project"]);
        Assert.Empty(bytes["comparers"]!.AsArray());
        Assert.Equal("отличаются байтами", (string)bytes["unexpected"]![0]!["detail"]!);

        // расхождение ожидаемо — 0
        string expect = Path.Combine(_root, "expect.json");
        File.WriteAllText(expect, """{ "expected": [ { "path": "skeletons", "expect": "different", "reason": "отпечаток исходника" } ] }""");
        (exit, stdout, _) = TestPaths.RunCliIn(outside, "diff", a, b, "--expect", expect, "--json");
        Assert.Equal(0, exit);
        Assert.Equal(1, (int)Single(stdout)["summary"]!["expected"]!);

        // в проекте с плагином A — сравнитель .skel: равны по содержимому — 0; текстом — тот же код
        string project = Path.Combine(_root, "project");
        CreateProject(project);
        (exit, stdout, stderr) = TestPaths.RunCliIn(outside, "--project", project, "diff", a, b, "--json");
        Assert.True(exit == 0, stderr + stdout);
        var withPlugin = Single(stdout);
        Assert.Equal("Diff", (string)withPlugin["project"]!["name"]!);
        Assert.Equal(1, (int)withPlugin["summary"]!["sameContent"]!);
        (exit, stdout, _) = TestPaths.RunCliIn(project, "diff", a, b);   // проект найден от текущего каталога
        Assert.Equal(0, exit);
        Assert.Contains("равны по содержимому (скелет: без строк source:): 1", stdout);

        // ошибка — 1: нет пака, испорченный файл ожиданий
        (exit, stdout, _) = TestPaths.RunCliIn(outside, "diff", a, Path.Combine(_root, "nope.swpak"), "--json");
        Assert.Equal(1, exit);
        Assert.Contains("нет пака", (string)Single(stdout)["error"]!);
        File.WriteAllText(expect, """{ "expected": [ { "path": "x", "expect": "absent" } ] }""");
        (exit, _, stderr) = TestPaths.RunCliIn(outside, "diff", a, b, "--expect", expect);
        Assert.Equal(1, exit);
        Assert.Contains("reason", stderr);
    }
}
