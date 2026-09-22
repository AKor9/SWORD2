using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests;

/// <summary>
/// Приёмка и эксперименты (этап 3д): манифест фичи (разбор и его ошибки), <see cref="FeatureVerifier"/> (сценарии,
/// проверки, хеши реплея, инварианты, метрики, бюджеты, виды) и <see cref="Experiments"/> (развилка расходится на тике
/// изменения, сравнение описаний, перебор конфига). Подопытный — <see cref="TestModule"/>: тела X = 0…4, Order двигает все
/// тела по X в S1.
/// </summary>
public sealed class FeatureTests : IDisposable
{
    private static readonly ReplayInfo Info = new("1.0.0+test", "tests", "none");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-feature-tests", Guid.NewGuid().ToString("N")[..8]);

    public FeatureTests() => Directory.CreateDirectory(Path.Combine(_root, "feat"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Simulation Build(ConfigSet config)
    {
        var sim = Simulation.Build(config, ["S1", "S2"], [new TestModule()]);
        config.Complete();
        return sim;
    }

    /// <summary>Первое тело (X = 0 на старте) — номер сущности текстом.</summary>
    private static string First => TestModule.Build().World.EntityAt(0).ToString();

    private string Manifest(string json)
    {
        File.WriteAllText(Path.Combine(_root, "feat", "README.md"), "# фича\r\n");
        string path = Path.Combine(_root, "feat", FeatureManifest.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Реплей подопытного: Order X+1 каждый тик, хеши каждого тика.</summary>
    private string RecordReplay(string name, int ticks)
    {
        var sim = Build(ConfigSet.Empty());
        var recorder = sim.Record(Info);
        for (int t = 0; t < ticks; t++)
        {
            sim.Enqueue(new Order { Move = new Vector2(1, 0) });
            sim.Step();
        }
        string path = Path.Combine(_root, name);
        recorder.ToReplay().Save(path);
        return path;
    }

    private FeatureEnvironment Env(Func<ViewRequest, CancellationToken, ViewResult>? view = null) =>
        new() { RepoRoot = _root, Build = Build, Info = Info, CaptureView = view, Optimized = false };

    private const string Push = """{ "command": "Order", "fields": { "Move": { "X": 1, "Y": 0 } }, "from": 0, "to": 10 }""";

    private string Good() => $$"""
        {
          // комментарии допустимы
          "format": "sword-feature", "version": 1, "id": "push", "title": "толчки", "doc": "README.md",
          "sourceRoots": ["feat"],
          "scenarios": [
            {
              "id": "push", "ticks": 20, "commands": [{{Push}}],
              "checks": [
                { "id": "far", "expect": "eventually", "condition": { "field": "Body.Position.X", "greaterOrEqual": 14 } },
                { "id": "bounded", "expect": "always", "condition": { "none": { "field": "Body.Position.X", "greaterThan": 14 } } },
                { "id": "never-far", "expect": "never", "condition": { "field": "Body.Position.X", "greaterThan": 100 } },
                { "id": "stopped", "expect": "atEnd", "condition": { "field": "Body.Position.X", "equals": 10 } },
                { "id": "early", "expect": "always", "from": 1, "to": 3, "condition": { "none": { "field": "Body.Position.X", "greaterThan": 7 } } }
              ]
            },
            { "id": "rec", "replay": "rec.swreplay" }
          ],
          "invariants": ["ecs.*"],
          "metrics": [
            { "id": "x5", "scenario": "push", "source": "sword2", "reference": "тест", "entity": "{{First}}", "field": "Body.Position.X",
              "at": 5, "expected": 5 },
            { "id": "xmax", "scenario": "push", "source": "sword", "reference": "тест", "entity": "{{First}}", "field": "Body.Position",
              "stat": "max", "from": 1, "to": 20, "expected": 10, "tolerance": 0.001 },
            { "id": "mean", "scenario": "rec", "source": "sword2", "reference": "тест", "entity": "{{First}}", "field": "Body.Position.X",
              "stat": "mean", "expected": 15.5 }
          ],
          "views": [{ "id": "end", "scenario": "rec", "tick": 30, "camera": "0.6,0.95,24" }],
          "budgets": [{ "id": "push", "scenario": "push", "allocatedBytesPerTick": 0, "msPerTick": 5, "repeats": 3, "warmup": 5 }],
          "checklist": ["посмотреть"]
        }
        """;

    [Fact]
    public void Manifest_Parses_AndFindsById()
    {
        var m = FeatureManifest.Load(Manifest(Good()));
        Assert.Equal("push", m.Id);
        Assert.Equal(Path.Combine(_root, "feat", "README.md"), m.Doc);
        Assert.Equal(["push", "rec"], m.Scenarios.Select(s => s.Id));
        var c = Assert.Single(m.Scenarios[0].Commands);
        Assert.Equal(("Order", 0ul, 10ul), (c.Command, c.From, c.To));
        Assert.Equal(["far", "bounded", "never-far", "stopped", "early"], m.Scenarios[0].Checks.Select(x => x.Id));
        Assert.Equal("max", m.Metrics[1].Stat);
        Assert.Equal(3, m.Budgets[0].Repeats);
        Assert.Equal("push", Assert.Single(FeatureManifest.Find([_root])).Id);
    }

    [Theory]
    [InlineData("\"title\": \"толчки\"", "\"title\": \"толчки\", \"extra\": 1", "extra: неизвестный ключ")]
    [InlineData("\"id\": \"push\", \"title\"", "\"id\": \"Push\", \"title\"", "id «Push»")]
    [InlineData("\"expect\": \"atEnd\"", "\"expect\": \"sometimes\"", "expect «sometimes»")]
    [InlineData("\"views\": [{ \"id\": \"end\", \"scenario\": \"rec\"", "\"views\": [{ \"id\": \"end\", \"scenario\": \"nope\"", "views.end.scenario: нет сценария «nope»")]
    [InlineData("\"checklist\": [\"посмотреть\"]", "\"checklist\": []", "checklist: хотя бы один")]
    [InlineData("\"at\": 5, \"expected\": 5", "\"at\": 5", "нужно field и expected")]
    [InlineData("\"stat\": \"max\", \"from\": 1", "\"stat\": \"max\", \"at\": 3, \"from\": 1", "at — только со stat value")]
    [InlineData("{ \"id\": \"rec\", \"replay\": \"rec.swreplay\" }", "{ \"id\": \"rec\" }", "без реплея нужно ticks")]
    [InlineData("\"msPerTick\": 5", "\"msPerTick\": -5", "пределы — положительные")]
    [InlineData("\"from\": 0, \"to\": 10", "\"from\": 10, \"to\": 10", "to 10 не больше from 10")]
    public void Manifest_Errors_NamePlace(string from, string to, string expected)
    {
        string json = Good();
        Assert.Contains(from, json);
        var e = Assert.Throws<InvalidDataException>(() => FeatureManifest.Load(Manifest(json.Replace(from, to))));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Verify_GoodFeature_AllGreen_ViewPlaysRecordedScenario()
    {
        RecordReplay("rec.swreplay", 30);
        var m = FeatureManifest.Load(Manifest(Good()));
        ViewRequest? seen = null;
        var report = FeatureVerifier.Verify(m, Env((r, _) =>
        {
            seen = r;
            File.WriteAllBytes(r.Png, [1]);
            return new ViewResult(true, "снят");
        }), new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out") });

        Assert.True(report.Ok, string.Join("\n", report.Failures));
        var j = report.Json;
        var push = j["scenarios"]![0]!;
        Assert.Equal(20ul, push["ticks"]!.GetValue<ulong>());
        Assert.Equal("впервые — после 10 тиков", push["checks"]![0]!["detail"]!.GetValue<string>());   // 4 + 10
        Assert.All(push["checks"]!.AsArray(), c => Assert.True(c!["ok"]!.GetValue<bool>()));
        var rec = j["scenarios"]![1]!["replay"]!;
        Assert.Equal((31, true), (rec["hashesChecked"]!.GetValue<int>(), rec["match"]!.GetValue<bool>()));
        Assert.Equal(["ecs.entityRefs", "ecs.finite"], j["invariants"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).Order());
        Assert.Equal(5.0, j["metrics"]![0]!["value"]!.GetValue<double>());
        Assert.Equal(15.5, j["metrics"]![2]!["value"]!.GetValue<double>());   // X = 1…30
        var budget = j["budgets"]![0]!;
        Assert.Equal(0.0, budget["allocatedBytesPerTick"]!.GetValue<double>());
        Assert.Contains("Debug", budget["time"]!.GetValue<string>());
        Assert.Contains("S1", budget["stages"]!.AsObject().Select(p => p.Key));

        // Вид играет запись сценария Headless: те же команды и хеши каждого тика.
        Assert.NotNull(seen);
        Assert.Equal((30ul, "0.6,0.95,24", false), (seen!.Tick, seen.Camera, seen.Hud));
        var recorded = Replay.Load(seen.Replay);
        Assert.Equal((30ul, 31), (recorded.Ticks, recorded.Hashes.Count));
        Assert.True(File.Exists(Path.Combine(_root, "out", "report.json")));
        Assert.True(File.Exists(Path.Combine(_root, "out", "scenario-push.swreplay")));
    }

    [Fact]
    public void Verify_Failures_NameEachBrokenPart()
    {
        // Реплей с порченой командой тика 12: хеши разойдутся после 13 тиков.
        string path = RecordReplay("rec.swreplay", 30);
        var replay = Replay.Load(path);
        string text = replay.ToJson();
        Assert.Contains("[0, 30, [0, 1, 0, ", text);
        File.WriteAllText(path, text.Replace("[0, 30, [0, 1, 0, ", "[0, 12, [0, 1, 0, "));
        string json = Good()
            .Replace("\"greaterOrEqual\": 14", "\"greaterOrEqual\": 15")                    // far — не выполнится
            .Replace("\"expected\": 5 }", "\"expected\": 6 }")                              // x5 — мимо
            .Replace("\"invariants\": [\"ecs.*\"]", "\"invariants\": [\"ecs.*\", \"no.such\"]")
            .Replace("\"sourceRoots\": [\"feat\"]", "\"sourceRoots\": [\"feat\", \"gone\"]");
        var report = FeatureVerifier.Verify(FeatureManifest.Load(Manifest(json)), Env(),
            new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out") });
        Assert.False(report.Ok);
        string all = string.Join("\n", report.Failures);
        Assert.Contains("sourceRoots: нет gone", all);
        Assert.Contains("проверка far (eventually): ни разу не выполнилось", all);
        Assert.Contains("сценарий rec: хеши реплея разошлись", all);
        Assert.Contains("метрика x5: 5, ждали 6", all);
        Assert.Contains("инвариант no.such: в симуляции нет", all);
        Assert.Contains("вид end: App нет", all);
    }

    [Fact]
    public void Verify_InvariantViolation_FailsScenario()
    {
        // NaN в Order → NaN в Body.Position.X: ecs.finite в конце тика 3.
        string json = Good().Replace(Push, """{ "command": "Order", "fields": { "Move": { "X": "NaN", "Y": 0 } }, "from": 3, "to": 4 }""");
        var m = FeatureManifest.Load(Manifest(json)) with { Scenarios = [FeatureManifest.Load(Manifest(json)).Scenarios[0] with { Checks = [] }] };
        m = m with { Metrics = [], Views = [], Budgets = [] };
        var report = FeatureVerifier.Verify(m, Env(), new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out") });
        Assert.False(report.Ok);
        Assert.Contains(report.Failures, f => f.Contains("нарушений инвариантов") && f.Contains("ecs.finite: тик 3"));
        Assert.Contains(report.Json["invariants"]!.AsArray(), x => x!["id"]!.GetValue<string>() == "ecs.finite" && !x["ok"]!.GetValue<bool>());
    }

    // ── эксперименты ──────────────────────────────────────────────────────

    private static ScenarioCommand Order(float x, ulong from, ulong to) =>
        new("Order", new JsonObject { ["Move"] = new JsonObject { ["X"] = x, ["Y"] = 0 } }, from, to);

    [Fact]
    public void Fork_BranchDivergesOnTickAfterChange_ConfigFromStart()
    {
        var replay = Replay.Load(RecordReplay("base.swreplay", 40));
        string first = First;
        var branches = Experiments.Fork(replay, 20, 40,
        [
            new ForkVariant("double", [], [Order(2, 20, 21)], Cut: false),
            new ForkVariant("stop", [], [], Cut: true),
            new ForkVariant("seed", ["sim.seed=7"], [], Cut: false),
        ], [new ForkMeasure(first, "Body.Position.X")], Build, Info, null);

        Assert.Equal(["base", "double", "stop", "seed"], branches.Select(b => b.Name));
        Assert.All(branches, b => Assert.Null(b.Error));
        Assert.Null(branches[0].PlaybackDivergence);                              // база повторяет файл
        Assert.Equal(40.0, branches[0].Measures[$"{first}:Body.Position.X"]);
        // Команда тика 20 меняет состояние после 21 тика; до того ветка — база бит в бит.
        var d = branches[1].VsBase!;
        Assert.Equal((20ul, 21ul, 20ul), (d.FirstCommandTick, d.FirstHashTick, d.LastCommonHash));
        Assert.Equal(["ecs"], d.HashParts);
        Assert.Equal(41.0, branches[1].Measures[$"{first}:Body.Position.X"]);
        Assert.Equal((20ul, 21ul), (branches[2].VsBase!.FirstCommandTick, branches[2].VsBase!.FirstHashTick));
        Assert.Equal(20.0, branches[2].Measures[$"{first}:Body.Position.X"]);   // после 20 команд нет
        // Конфиг собирает симуляцию: другое зерно — другое состояние с тика 0 (SimRandom хешируется), заголовок называет ключ.
        Assert.Equal(0ul, branches[3].VsBase!.FirstHashTick);
        Assert.Contains(branches[3].VsBase!.Header, h => h.Contains("seed"));

        Assert.Throws<ArgumentException>(() => Experiments.Fork(replay, 20, 40, [new ForkVariant("early", [], [Order(2, 19, 21)], false)],
            [], Build, Info, null));
        Assert.Throws<ArgumentException>(() => Experiments.Fork(replay, 20, 40, [new ForkVariant("same", [], [], false)], [], Build, Info, null));
        Assert.Throws<ArgumentException>(() => Experiments.Fork(replay, 20, 40, [new ForkVariant("base", ["sim.seed=2"], [], false)], [], Build, Info, null));
    }

    [Fact]
    public void Compare_DescribesDivergedPart()
    {
        var replay = Replay.Load(RecordReplay("base.swreplay", 40));
        var branches = Experiments.Fork(replay, 20, 40, [new ForkVariant("double", [], [Order(2, 20, 21)], false)], [], Build, Info, null);
        var (diff, at, parts) = Experiments.Compare(branches[0].Recorded!, branches[1].Recorded!, Build, describe: true);
        Assert.Equal(21ul, diff.FirstHashTick);
        Assert.Equal(21ul, at);
        var ecs = Assert.Single(parts);
        Assert.Equal("ecs", ecs.Part);
        Assert.True(ecs.Total >= 5, $"тел 5 — каждое сдвинулось; разница: {ecs.Total}");
        Assert.Contains(ecs.Lines, l => l.Path.Contains("Position.X") && l.A == "21" && l.B == "22");
        var same = Experiments.Compare(branches[0].Recorded!, branches[0].Recorded!, Build, describe: true);
        Assert.Null(same.DescribedAt);
        Assert.Empty(same.Describe);
    }

    [Fact]
    public void Sweep_RowsPerCombination_DeterministicRepeats_Errors()
    {
        var replay = Replay.Load(RecordReplay("base.swreplay", 60));
        var (rows, scaling) = Experiments.Sweep(replay, 60, [("sim.tickRate", ["60", "120"]), ("sim.seed", ["1", "2"])], 2, 5, Build);
        Assert.Equal(4, rows.Count);
        Assert.Equal(["sim.tickRate=60", "sim.seed=1"], rows[0].Sets);
        Assert.All(rows, r => Assert.True(r.Deterministic && r.Error is null && r.Repeats == 2 && r.AllocatedBytesPerTick == 0, $"{string.Join(",", r.Sets)}: {r.Error}"));
        Assert.Equal(4, rows.Select(r => r.Hash).Distinct().Count());            // частота и зерно — в хеше
        Assert.Contains("S1", rows[0].Stages.Keys);
        Assert.Contains(scaling, s => s.StartsWith("sim.tickRate:", StringComparison.Ordinal));

        var (bad, _) = Experiments.Sweep(null, 20, [("sim.nope", ["1"])], 1, 0, Build);
        Assert.NotNull(Assert.Single(bad).Error);
        Assert.Throws<ArgumentException>(() => Experiments.Sweep(null, 20, [("sim.seed", [.. Enumerable.Range(0, 65).Select(i => $"{i}")])], 1, 0, Build));
    }
}
