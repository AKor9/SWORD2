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
          "views": [{ "id": "end", "scenario": "rec", "tick": 30, "camera": "0.6,0.95,24", "overlays": ["capsules"] }],
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
    [InlineData("\"overlays\": [\"capsules\"]", "\"overlays\": [\"Capsules\"]", "views[0].overlays[0]: имя оверлея")]
    [InlineData("\"overlays\": [\"capsules\"]", "\"overlays\": [\"capsules\", \"capsules\"]", "views[0].overlays[1]: capsules дважды")]
    [InlineData("\"overlays\": [\"capsules\"]", "\"overlays\": \"capsules\"", "views[0].overlays: массив строк")]
    [InlineData("\"overlays\": [\"capsules\"]", "\"overlays\": [\"\"]", "views[0].overlays[0]: непустая строка")]
    [InlineData("\"at\": 5, \"expected\": 5", "\"at\": 5", "нужно field и expected")]
    [InlineData("\"stat\": \"max\", \"from\": 1", "\"stat\": \"max\", \"at\": 3, \"from\": 1", "at — только со stat value")]
    [InlineData("{ \"id\": \"rec\", \"replay\": \"rec.swreplay\" }", "{ \"id\": \"rec\" }", "без реплея нужно ticks")]
    [InlineData("\"msPerTick\": 5", "\"msPerTick\": -5", "пределы — положительные")]
    [InlineData("\"from\": 0, \"to\": 10", "\"from\": 10, \"to\": 10", "to 10 не больше from 10")]
    [InlineData("\"allocatedBytesPerTick\": 0, \"msPerTick\": 5, \"repeats\": 3, \"warmup\": 5", "\"frame\": { \"seconds\": 5 }", "frame: нечего проверять — averageMs или p99Ms")]
    [InlineData("\"allocatedBytesPerTick\": 0, \"msPerTick\": 5, \"repeats\": 3, \"warmup\": 5", "\"frame\": { \"averageMs\": 1, \"seconds\": 4 }", "frame.seconds: больше 4")]
    [InlineData("\"allocatedBytesPerTick\": 0, \"msPerTick\": 5, \"repeats\": 3, \"warmup\": 5", "\"frame\": { \"p99Ms\": 0 }", "frame: пределы — положительные")]
    [InlineData("\"allocatedBytesPerTick\": 0, \"msPerTick\": 5, \"repeats\": 3, \"warmup\": 5", "\"frame\": { \"p99Ms\": 1, \"fps\": 60 }", "fps: неизвестный ключ")]
    [InlineData("\"allocatedBytesPerTick\": 0, \"msPerTick\": 5, \"repeats\": 3, \"warmup\": 5", "\"frame\": { \"p99Ms\": 1, \"overlays\": [\"Capsules\"] }", "frame.overlays[0]: имя оверлея")]
    public void Manifest_Errors_NamePlace(string from, string to, string expected)
    {
        string json = Good();
        Assert.Contains(from, json);
        var e = Assert.Throws<InvalidDataException>(() => FeatureManifest.Load(Manifest(json.Replace(from, to))));
        Assert.Contains(expected, e.Message);
    }

    /// <summary>Вычисляемое поле игры (5б): <c>Twice.X</c> — удвоенный X тела; остальные пути — не его.</summary>
    private sealed class TwiceFields : IEntityFields
    {
        public IReadOnlyList<string> Known { get; } = ["Twice.X"];

        public EntityFieldReader? Compile(string path, string at) => path switch
        {
            "Twice.X" => (in ServiceContext ctx, Ecs.Entity e) => ctx.World.IsAlive(e) ? ctx.World.Get<Body>(e).Position.X * 2 : double.NaN,
            _ when path.StartsWith("Twice.", StringComparison.Ordinal) => throw new ArgumentException($"{at}: {path} — только Twice.X"),
            _ => null,
        };
    }

    [Fact]
    public void Verify_Metric_ReadsComputedFieldOfTheGame()
    {
        // Метрика читает вычисляемое поле игры (IEntityFields: тела бойца вне ECS — Body.pelvis.Position.Y) как поле компонента;
        // путь, который поля игры не понимают, — по-прежнему компонент.
        string json = $$"""
            {
              "format": "sword-feature", "version": 1, "id": "twice", "title": "поля", "doc": "README.md", "sourceRoots": ["feat"],
              "scenarios": [{ "id": "push", "ticks": 5, "commands": [{{Push}}] }],
              "metrics": [
                { "id": "twice", "scenario": "push", "source": "sword2", "reference": "тест", "entity": "{{First}}", "field": "Twice.X", "expected": 10 },
                { "id": "plain", "scenario": "push", "source": "sword2", "reference": "тест", "entity": "{{First}}", "field": "Body.Position.X", "expected": 5 }
              ],
              "checklist": ["посмотреть"]
            }
            """;
        var m = FeatureManifest.Load(Manifest(json));
        var env = new FeatureEnvironment { RepoRoot = _root, Build = Build, Info = Info, Optimized = false, Fields = new TwiceFields() };
        var report = FeatureVerifier.Verify(m, env, new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out"), Views = false, Budgets = false });
        Assert.True(report.Ok, string.Join('|', report.Failures));
        Assert.Equal((10.0, 5.0), (report.Json["metrics"]![0]!["value"]!.GetValue<double>(), report.Json["metrics"]![1]!["value"]!.GetValue<double>()));

        // С ошибкой в пути поля игры — её текст; без полей игры путь Twice.X — неизвестный компонент.
        var bad = FeatureManifest.Load(Manifest(json.Replace("\"field\": \"Twice.X\"", "\"field\": \"Twice.Y\"")));
        var failed = FeatureVerifier.Verify(bad, env, new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out3"), Views = false, Budgets = false });
        Assert.Contains(failed.Failures, f => f.Contains("только Twice.X"));
        var none = FeatureVerifier.Verify(m, new FeatureEnvironment { RepoRoot = _root, Build = Build, Info = Info, Optimized = false },
            new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out4"), Views = false, Budgets = false });
        Assert.False(none.Ok);
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
        // Точка Па: время систем — отдельный прогон с профайлером CPU; его байты — тот же предел (0).
        Assert.Equal(["runtime.commands@runtime.commands", "runtime.export@runtime.export", "test.mover@S1", "test.reader@S2"], budget["systems"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(0.0, budget["profiled"]!["allocatedBytesPerTick"]!.GetValue<double>());

        // Вид играет запись сценария Headless: те же команды и хеши каждого тика.
        Assert.NotNull(seen);
        Assert.Equal((30ul, "0.6,0.95,24", false), (seen!.Tick, seen.Camera, seen.Hud));
        Assert.Equal(["capsules"], seen.Overlays);   // оверлеи вида (5в) — App рисует их поверх кадра (--overlay)
        Assert.Equal(["capsules"], m.Views[0].Overlays);
        Assert.Equal("capsules", j["views"]![0]!["overlays"]![0]!.GetValue<string>());
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

    /// <summary>
    /// Бюджет кадра (точка 4д): App играет запись сценария бенчем; в Debug кадр только меряется, в Release — пределы среднего и
    /// 99-го перцентиля; разошедшиеся хеши App — провал; без видов (App не запускается) — не меряется; бенч длиннее записи — провал.
    /// </summary>
    [Fact]
    public void Verify_FrameBudget_PlaysRecordedScenario_ChecksLimitsOnlyInRelease()
    {
        RecordReplay("rec.swreplay", 30);
        string json = Good()
            .Replace("\"budgets\": [", "\"budgets\": [{ \"id\": \"frame\", \"scenario\": \"slow\", \"frame\": { \"averageMs\": 2, \"p99Ms\": 4, \"overlays\": [\"capsules\"] } }, ")
            .Replace("{ \"id\": \"rec\", \"replay\": \"rec.swreplay\" }",
                "{ \"id\": \"rec\", \"replay\": \"rec.swreplay\" }, { \"id\": \"slow\", \"set\": [\"sim.tickRate=10\"], \"ticks\": 60 }");
        var m = FeatureManifest.Load(Manifest(json));
        var fb = m.Budgets[0];
        Assert.Equal((2.0, 4.0, 5.0, false), (fb.Frame!.AverageMs, fb.Frame.P99Ms, fb.Frame.Seconds, fb.HasSimulation));

        FrameRequest? seen = null;
        var result = new FrameResult(true, "замер", 100, 3, 3.5, 9);
        FeatureEnvironment Env2(bool optimized) => new()
        {
            RepoRoot = _root, Build = Build, Info = Info, Optimized = optimized,
            CaptureView = (r, _) =>
            {
                File.WriteAllBytes(r.Png, [1]);
                return new ViewResult(true, "снят");
            },
            MeasureFrame = (r, _) =>
            {
                seen = r;
                return result;
            },
        };
        FeatureReport Run(FeatureManifest manifest, bool optimized, bool views = true) =>
            FeatureVerifier.Verify(manifest, Env2(optimized), new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out"), Views = views });

        // Debug: кадр меряется на записи сценария (60 тиков по 10/с — не короче бенча 5 с), но не проверяется.
        var debug = Run(m, optimized: false);
        Assert.True(debug.Ok, string.Join("\n", debug.Failures));
        Assert.Equal(("push", "frame", 5.0), (seen!.Feature, seen.Budget, seen.Seconds));
        Assert.Equal(["capsules"], seen.Overlays);   // 5г: оверлеи бюджета кадра — App рисует их весь замер (--overlay)
        Assert.Equal(60ul, Replay.Load(seen.Replay).Ticks);
        var frame = debug.Json["budgets"]![0]!["frame"]!;
        Assert.Equal((3.0, 3.5, 100L), (frame["averageMs"]!.GetValue<double>(), frame["p99Ms"]!.GetValue<double>(), frame["frames"]!.GetValue<long>()));
        Assert.Contains("Debug", frame["time"]!.GetValue<string>());
        Assert.Equal("capsules", frame["overlays"]![0]!.GetValue<string>());
        Assert.Null(debug.Json["budgets"]![0]!["msPerTick"]);   // только кадр: симуляция бюджета не гонялась

        // Release: средний 3 мс больше предела 2 — провал; p99 3,5 ≤ 4 — нет.
        var release = Run(m, optimized: true);
        Assert.False(release.Ok);
        Assert.Contains("бюджет frame: кадр 3 мс в среднем, предел 2", release.Failures);
        Assert.DoesNotContain(release.Failures, f => f.Contains("перцентиль"));

        // Хеши App разошлись с записью — провал в любой сборке.
        result = new FrameResult(false, "хеши App разошлись с записью Headless: тик 5");
        Assert.Contains("бюджет frame: кадр: хеши App разошлись с записью Headless: тик 5", Run(m, optimized: false).Failures);

        // Без видов App не запускается — кадр не меряется, это не провал.
        seen = null;
        result = new FrameResult(true, "замер", 100, 1, 1, 1);
        var noViews = Run(m, optimized: true, views: false);
        Assert.True(noViews.Ok, string.Join("\n", noViews.Failures));
        Assert.Null(seen);
        Assert.Contains("не мерялся", noViews.Json["budgets"]![0]!["frame"]!["measured"]!.GetValue<string>());

        // Бенч длиннее записи сценария — провал, App не запускается.
        var longer = FeatureManifest.Load(Manifest(json.Replace("\"p99Ms\": 4,", "\"p99Ms\": 4, \"seconds\": 7,")));
        Assert.Contains("бюджет frame: кадр: сценарий (60 тиков) короче бенча (7 с — 70 тиков)", Run(longer, optimized: true).Failures);
        Assert.Null(seen);
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

        // Порядок прогонов (4д): круг прогрева — каждая комбинация по разу, затем повторы по кругу комбинаций, а не все
        // повторы одной подряд (первые комбинации свежего процесса иначе мерились на неоптимизированном JIT).
        var order = new List<string>();
        Simulation Logged(ConfigSet config)
        {
            var sim = Build(config);
            order.Add($"{sim.Settings.TickRate}/{sim.Settings.Seed}");
            return sim;
        }
        var (warmed, _) = Experiments.Sweep(null, 20, [("sim.tickRate", ["60", "120"]), ("sim.seed", ["1", "2"])], 2, 5, Logged);
        // Точка Па: после повторов — ещё круг с профайлером CPU (время систем); медианы — только из повторов.
        Assert.Equal(["60/1", "60/2", "120/1", "120/2", "60/1", "60/2", "120/1", "120/2", "60/1", "60/2", "120/1", "120/2",
            "60/1", "60/2", "120/1", "120/2"], order);
        Assert.All(warmed, r => Assert.Equal(["runtime.commands@runtime.commands", "runtime.export@runtime.export", "test.mover@S1", "test.reader@S2"], r.Systems!.Select(p => p.Key).Order()));
        order.Clear();
        var (bare, _) = Experiments.Sweep(null, 20, [("sim.seed", ["1"])], 1, 5, Logged, systems: false);
        Assert.Equal(["120/1", "120/1"], order);   // круг прогрева и повтор, без прогона с профайлером
        Assert.Null(Assert.Single(bare).Systems);
        Assert.All(warmed, r => Assert.True(r.Repeats == 2 && r.Deterministic, string.Join(",", r.Sets)));

        var (bad, _) = Experiments.Sweep(null, 20, [("sim.nope", ["1"])], 1, 0, Build);
        Assert.NotNull(Assert.Single(bad).Error);
        Assert.Throws<ArgumentException>(() => Experiments.Sweep(null, 20, [("sim.seed", [.. Enumerable.Range(0, 65).Select(i => $"{i}")])], 1, 0, Build));
    }
}
