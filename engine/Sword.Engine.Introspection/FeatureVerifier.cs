using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>Где и чем проверять фичи: корень репозитория, та же композиция симуляции, что у exe, имена, App для видов.</summary>
public sealed class FeatureEnvironment
{
    /// <summary>Корень репозитория: от него пути реплеев, трасс и <c>sourceRoots</c>.</summary>
    public required string RepoRoot { get; init; }

    /// <summary>Симуляция из конфига — композиция exe (конфиг — до <see cref="ConfigSet.Complete"/> включительно); с любого потока.</summary>
    public required Func<ConfigSet, Simulation> Build { get; init; }

    /// <summary>Сборка и контент — в заголовок записанных сценариев; контент сверяется с реплеями сценариев.</summary>
    public required ReplayInfo Info { get; init; }

    public IEntityNames? Names { get; init; }

    /// <summary>Вычисляемые поля игры (капсулы тела и т. п.) — метрики читают их как поля компонентов; null — только компоненты.</summary>
    public IEntityFields? Fields { get; init; }

    /// <summary>Снять вид в App; null — App нет (виды не снимаются, это провал, если их просили).</summary>
    public Func<ViewRequest, CancellationToken, ViewResult>? CaptureView { get; init; }

    /// <summary>Сборка оптимизирована (Release): бюджеты времени проверяются; в Debug — только меряются.</summary>
    public bool Optimized { get; init; }

    /// <summary>Замерить кадр App на записи сценария (бюджет кадра); null — App нет (бюджет кадра — провал, если его просили).</summary>
    public Func<FrameRequest, CancellationToken, FrameResult>? MeasureFrame { get; init; }
}

/// <summary>Что делать в <see cref="FeatureVerifier.Verify"/>: виды, бюджеты, куда класть артефакты.</summary>
public sealed record FeatureVerifyOptions
{
    public required string OutputDirectory { get; init; }
    public bool Views { get; init; } = true;
    public bool Budgets { get; init; } = true;
}

/// <summary>Вид для App: реплей сценария (записанный Headless), после скольких тиков, ракурс, HUD, куда PNG, оверлеи игры.</summary>
public sealed record ViewRequest(string Feature, string View, string Replay, ulong Tick, string? Camera, bool Hud, string Png)
{
    public IReadOnlyList<string> Overlays { get; init; } = [];
}

/// <summary>Итог вида: снят ли кадр, совпали ли хеши App с реплеем, подробности для людей.</summary>
public sealed record ViewResult(bool Ok, string Detail);

/// <summary>
/// Замер кадра для App (бюджет кадра): реплей сценария (записанный Headless), секунды бенча (первые 4 — прогрев), оверлеи игры
/// поверх каждого кадра (5г).
/// </summary>
public sealed record FrameRequest(string Feature, string Budget, string Replay, double Seconds)
{
    public IReadOnlyList<string> Overlays { get; init; } = [];
}

/// <summary>
/// Итог замера кадра: App отработал и его хеши совпали с записью (<see cref="Ok"/>), кадров замера, средний кадр, 99-й
/// перцентиль и худший, мс; подробности для людей.
/// </summary>
public sealed record FrameResult(bool Ok, string Detail, long Frames = 0, double AverageMs = double.NaN, double P99Ms = double.NaN, double MaxMs = double.NaN)
{
    /// <summary>Машина за замер App (точка Пб: <c>report.machine</c> итога Sword.exe) — JSON как есть; null — нет.</summary>
    public JsonObject? Machine { get; init; }
}

/// <summary>Итог <c>feature.verify</c>: вердикт, провалы словами, полный отчёт JSON (он же — <c>report.json</c> в артефактах).</summary>
public sealed record FeatureReport(string Feature, bool Ok, IReadOnlyList<string> Failures, JsonObject Json, string Directory);

/// <summary>
/// <c>feature.verify</c> (раздел 8 плана, «Контракт фичи»; раздел 12, пункт 3): одним воспроизводимым вызовом — сценарии
/// манифеста в отдельных симуляциях (как Headless), их проверки и хеши реплеев, инварианты, метрики против эталонов SWORD
/// и SWORD2, бюджеты (отдельные прогоны без инвариантов, медиана повторов), виды в App по записанным сценариям — и краткий
/// вердикт с путями к артефактам. Вердикт опирается только на манифест, инварианты и эталоны: мнения агента в нём нет.
/// </summary>
public static class FeatureVerifier
{
    public static FeatureReport Verify(FeatureManifest manifest, FeatureEnvironment env, FeatureVerifyOptions options,
        CancellationToken cancel = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var failures = new List<string>();
        void Fail(string text) => failures.Add(text);
        long started = Stopwatch.GetTimestamp();

        if (!File.Exists(manifest.Doc)) Fail($"нет md-документа фичи {manifest.Doc}");
        foreach (string root in manifest.SourceRoots)
            if (!File.Exists(Path.Combine(env.RepoRoot, root)) && !Directory.Exists(Path.Combine(env.RepoRoot, root)))
                Fail($"sourceRoots: нет {root}");

        // Сценарии — по очереди; записи — в артефакты (их играют виды в App).
        var scenarios = new JsonArray();
        var runs = new Dictionary<string, ScenarioRun>();
        var invariantTotals = new Dictionary<string, (long Evaluations, long Violations)>(StringComparer.Ordinal);
        foreach (var scenario in manifest.Scenarios)
        {
            cancel.ThrowIfCancellationRequested();
            var run = ScenarioRun.Execute(manifest, scenario, env, options.OutputDirectory, cancel);
            runs[scenario.Id] = run;
            scenarios.Add(run.Json);
            foreach (string f in run.Failures) Fail($"сценарий {scenario.Id}: {f}");
            foreach (var (id, e, v) in run.Invariants)
            {
                var (pe, pv) = invariantTotals.GetValueOrDefault(id);
                invariantTotals[id] = (pe + e, pv + v);
            }
        }

        var invariants = new JsonArray();
        foreach (string pattern in manifest.Invariants)
        {
            var matched = invariantTotals.Where(p => Matches(pattern, p.Key)).ToList();
            if (matched.Count == 0) Fail($"инвариант {pattern}: в симуляции нет");
            foreach (var (id, (e, v)) in matched)
            {
                bool ok = e > 0 && v == 0;
                if (e == 0) Fail($"инвариант {id} ни разу не проверялся");
                invariants.Add(new JsonObject { ["id"] = id, ["evaluations"] = e, ["violations"] = v, ["ok"] = ok });
            }
        }

        var metrics = new JsonArray();
        foreach (var m in manifest.Metrics)
        {
            var result = runs[m.Scenario].Metric(m, env.RepoRoot);
            metrics.Add(result.Json);
            if (!result.Ok) Fail($"метрика {m.Id}: {result.Detail}");
        }

        var budgets = new JsonArray();
        if (options.Budgets)
            foreach (var b in manifest.Budgets)
            {
                cancel.ThrowIfCancellationRequested();
                var result = b.HasSimulation
                    ? Budget(manifest, b, manifest.Scenario(b.Scenario), env, cancel)
                    : (Json: new JsonObject { ["id"] = b.Id, ["scenario"] = b.Scenario }, Failures: new List<string>());
                if (b.Frame is { } frame) FrameBudget(manifest.Id, b, frame, runs[b.Scenario], env, options, result.Json, result.Failures, cancel);
                result.Json["ok"] = result.Failures.Count == 0;
                result.Json["failures"] = new JsonArray([.. result.Failures.Select(f => (JsonNode?)f)]);
                budgets.Add(result.Json);
                failures.AddRange(result.Failures.Select(f => $"бюджет {b.Id}: {f}"));
            }

        var views = new JsonArray();
        if (options.Views)
            foreach (var v in manifest.Views)
            {
                cancel.ThrowIfCancellationRequested();
                var run = runs[v.Scenario];
                string png = Path.Combine(options.OutputDirectory, $"view-{v.Id}.png");
                ViewResult result;
                if (env.CaptureView is null) result = new ViewResult(false, "App нет — виды снимает Sword.Headless рядом со собранным Sword.exe");
                else if (run.RecordedFile is null) result = new ViewResult(false, "сценарий не записан — снимать нечего");
                else if (v.Tick > run.Ticks) result = new ViewResult(false, $"тик {v.Tick} за концом сценария ({run.Ticks})");
                else result = env.CaptureView(new ViewRequest(manifest.Id, v.Id, run.RecordedFile, v.Tick, v.Camera, v.Hud, png) { Overlays = v.Overlays }, cancel);
                if (!result.Ok) Fail($"вид {v.Id}: {result.Detail}");
                views.Add(new JsonObject
                {
                    ["id"] = v.Id, ["title"] = v.Title, ["scenario"] = v.Scenario, ["tick"] = v.Tick, ["ok"] = result.Ok,
                    ["overlays"] = v.Overlays.Count > 0 ? new JsonArray([.. v.Overlays.Select(o => (JsonNode?)o)]) : null,
                    ["png"] = File.Exists(png) ? png : null, ["detail"] = result.Detail,
                });
            }

        bool okAll = failures.Count == 0;
        var json = new JsonObject
        {
            ["feature"] = manifest.Id,
            ["title"] = manifest.Title,
            ["ok"] = okAll,
            ["configuration"] = env.Optimized ? "Release" : "Debug",
            ["manifest"] = manifest.Path,
            ["doc"] = manifest.Doc,
            ["artifacts"] = options.OutputDirectory,
            ["seconds"] = Math.Round(Stopwatch.GetElapsedTime(started).TotalSeconds, 2),
            ["failures"] = new JsonArray([.. failures.Select(f => (JsonNode?)f)]),
            ["requiredModules"] = new JsonArray([.. manifest.RequiredModules.Select(m => (JsonNode?)m)]),
            ["sourceRoots"] = new JsonArray([.. manifest.SourceRoots.Select(m => (JsonNode?)m)]),
            ["scenarios"] = scenarios,
            ["invariants"] = invariants,
            ["metrics"] = metrics,
            ["budgets"] = options.Budgets ? budgets : "не проверялись (budgets false)",
            ["views"] = options.Views ? views : "не снимались (views false)",
            ["checklist"] = new JsonArray([.. manifest.Checklist.Select(c => (JsonNode?)c)]),
        };
        File.WriteAllText(Path.Combine(options.OutputDirectory, "report.json"),
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                .ReplaceLineEndings("\r\n"));
        return new FeatureReport(manifest.Id, okAll, failures, json, options.OutputDirectory);
    }

    /// <summary>Id совпадает с шаблоном: точно или «префикс.*».</summary>
    public static bool Matches(string pattern, string id) =>
        pattern.EndsWith(".*", StringComparison.Ordinal) ? id.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == id;

    // ── сценарий ───────────────────────────────────────────────────────────

    /// <summary>
    /// Симуляция сценария на тике 0: конфиг (реплея или из кода) с <paramref name="extraSets"/> поверх, реплей воспроизводится,
    /// команды сценария поставлены. Ошибки подготовки — в <paramref name="failures"/> (симуляции тогда нет).
    /// </summary>
    internal static (Simulation? Sim, Replay? Replay) Prepare(FeatureManifest manifest, FeatureScenario scenario, FeatureEnvironment env,
        IEnumerable<string> extraSets, List<string> failures, bool hashes = true)
    {
        Replay? replay = null;
        if (scenario.Replay is { } file)
        {
            string path = Path.Combine(env.RepoRoot, file);
            if (!File.Exists(path))
            {
                failures.Add($"нет реплея {file}");
                return (null, null);
            }
            replay = Replay.Load(path);
            if (replay.Content != "none" && env.Info.Content != "none" && replay.Content != env.Info.Content)
            {
                failures.Add($"реплей записан на контенте {replay.Content}, а здесь {env.Info.Content}: хеши сравнивать бессмысленно");
                return (null, null);
            }
        }
        string[] sets = [.. scenario.Sets, .. extraSets];
        Simulation sim;
        try
        {
            sim = env.Build(replay?.CreateConfig(sets) ?? ConfigSet.Empty(sets));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            failures.Add($"симуляция не собралась: {e.Message}");
            return (null, null);
        }
        foreach (string m in manifest.RequiredModules.Where(m => !sim.ActiveModules.Contains(m)))
            failures.Add($"модуль {m} не активен (активны: {string.Join(", ", sim.ActiveModules)})");
        if (replay is not null) sim.Play(hashes ? replay : replay.Head(replay.Ticks, hashes: false));
        var catalog = new Catalog(sim.World, sim.CommandTypes);
        foreach (var c in scenario.Commands)
        {
            var type = catalog.Command(c.Command);
            sim.EnqueueRaw(type, c.From, FieldCodec.Encode(type, c.Fields), checked((int)(c.To - c.From)));
        }
        return (sim, replay);
    }

    private sealed class ScenarioRun
    {
        public required JsonObject Json { get; init; }
        public List<string> Failures { get; } = [];
        public ulong Ticks { get; set; }
        public int TickRate { get; set; } = 120;
        public bool Completed { get; private set; }
        public string? RecordedFile { get; set; }
        public List<(string Id, long Evaluations, long Violations)> Invariants { get; } = [];
        private readonly Dictionary<(string Entity, string Field), double[]> _series = [];

        public static ScenarioRun Execute(FeatureManifest manifest, FeatureScenario scenario, FeatureEnvironment env, string dir,
            CancellationToken cancel)
        {
            var run = new ScenarioRun { Json = new JsonObject { ["id"] = scenario.Id, ["title"] = scenario.Title } };
            try
            {
                run.Play(manifest, scenario, env, dir, cancel);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or KeyNotFoundException)
            {
                run.Failures.Add(e.Message);
            }
            run.Json["ok"] = run.Failures.Count == 0;
            run.Json["failures"] = new JsonArray([.. run.Failures.Select(f => (JsonNode?)f)]);
            return run;
        }

        private void Play(FeatureManifest manifest, FeatureScenario scenario, FeatureEnvironment env, string dir, CancellationToken cancel)
        {
            var (sim, replay) = Prepare(manifest, scenario, env, [], Failures);
            if (sim is null) return;
            ulong ticks = scenario.Ticks ?? replay!.Ticks;
            Ticks = ticks;
            TickRate = sim.Settings.TickRate;
            Json["ticks"] = ticks;
            Json["modules"] = new JsonArray([.. sim.ActiveModules.Select(m => (JsonNode?)m)]);

            // Инварианты манифеста — включены явно (и «по запросу»); нарушение любого включённого — провал.
            var all = sim.Invariants.List().Select(i => i.Id).ToList();
            foreach (string pattern in manifest.Invariants)
                foreach (string id in all.Where(id => Matches(pattern, id)))
                    sim.Invariants.SetEnabled(id, true);

            var recorder = sim.Record(env.Info);
            var catalog = new Catalog(sim.World, sim.CommandTypes);
            var hub = new ObservationHub(new TimeControl(paused: false), historyTicks: 256);
            hub.Attach(sim, catalog, env.Names);
            var ctx = new ServiceContext(sim);

            var checks = scenario.Checks.Select(c => (Check: c, Predicate: ObservationPredicate.Compile(c.Condition, ctx, catalog, env.Names))).ToList();
            foreach (var (c, _) in checks)
                if ((c.To ?? 0) > ticks) throw new ArgumentException($"проверка {c.Id}: окно до {c.To} за концом сценария ({ticks})");
            var state = new (ulong? FirstTrue, ulong? FirstFalse, long True, bool Last)[checks.Count];

            // Метрики сценария — ряды значений по тикам (с тика 0).
            var probes = new List<(FieldProbe Probe, double[] Values)>();
            foreach (var m in manifest.Metrics.Where(m => m.Scenario == scenario.Id))
                foreach (string field in m.Series?.Values ?? [m.Field!])
                {
                    if (_series.ContainsKey((m.Entity, field))) continue;
                    var values = new double[ticks + 1];
                    _series[(m.Entity, field)] = values;
                    probes.Add((new FieldProbe(catalog, env.Names, m.Entity, field, $"метрика {m.Id}", env.Fields), values));
                }
            foreach (var (p, values) in probes) values[0] = p.Read(ctx);

            long startedAt = Stopwatch.GetTimestamp();
            while (sim.Tick < ticks)
            {
                if ((sim.Tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
                sim.Export.BeginFrame();
                sim.Step();
                ulong tickId = sim.Tick;
                for (int i = 0; i < checks.Count; i++)
                {
                    var (c, predicate) = checks[i];
                    bool now = predicate.Evaluate(ctx, hub.History);   // каждый тик: окно for считает проверки подряд
                    state[i].Last = now;
                    if (tickId < (c.From ?? 1) || tickId > (c.To ?? ticks)) continue;
                    if (now)
                    {
                        state[i].FirstTrue ??= tickId;
                        state[i].True++;
                    }
                    else state[i].FirstFalse ??= tickId;
                }
                foreach (var (p, values) in probes) values[tickId] = p.Read(ctx);
            }
            double ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            sim.Unobserve(hub);
            Completed = true;
            Json["msPerTick"] = ticks > 0 ? Math.Round(ms / ticks, 4) : 0;

            // Хеши реплея.
            if (sim.Player is { } player)
            {
                int expected = player.Replay.Hashes.Count(h => h.Tick <= ticks);
                bool match = player.Divergence is null && player.HashesChecked == expected;
                Json["replay"] = new JsonObject
                {
                    ["file"] = scenario.Replay, ["ticks"] = player.Length, ["hashes"] = expected, ["hashesChecked"] = player.HashesChecked,
                    ["match"] = match, ["divergence"] = player.Divergence?.ToString(),
                };
                if (player.Divergence is { } d) Failures.Add($"хеши реплея разошлись: {d}");
                else if (!match) Failures.Add($"сверено хешей {player.HashesChecked} из {expected}");
            }

            var checksJson = new JsonArray();
            for (int i = 0; i < checks.Count; i++)
            {
                var (c, predicate) = checks[i];
                var s = state[i];
                (bool ok, string detail) = c.Expect switch
                {
                    "always" => (s.FirstFalse is null, s.FirstFalse is { } f ? $"не выполнено после {f} тиков" : $"выполнялось все {s.True} тиков окна"),
                    "never" => (s.FirstTrue is null, s.FirstTrue is { } t ? $"выполнилось после {t} тиков" : "ни разу не выполнилось"),
                    "eventually" => (s.FirstTrue is not null, s.FirstTrue is { } t ? $"впервые — после {t} тиков" : "ни разу не выполнилось"),
                    _ => (s.Last, s.Last ? "выполнено в конце" : "в конце не выполнено"),
                };
                if (!ok) Failures.Add($"проверка {c.Id} ({c.Expect}): {detail}");
                checksJson.Add(new JsonObject
                {
                    ["id"] = c.Id, ["title"] = c.Title, ["expect"] = c.Expect, ["ok"] = ok, ["detail"] = detail,
                    ["firstTrue"] = s.FirstTrue, ["firstFalse"] = s.FirstFalse, ["ticksTrue"] = s.True, ["cost"] = predicate.Cost(sim.World),
                });
            }
            Json["checks"] = checksJson;

            // Инварианты: все включённые, нарушения — провал.
            foreach (var i in sim.Invariants.List().Where(i => i.Enabled || i.Evaluations > 0))
                Invariants.Add((i.Id, i.Evaluations, i.Violations));
            Json["invariants"] = new JsonObject
            {
                ["enabled"] = sim.Invariants.List().Count(i => i.Enabled),
                ["violations"] = sim.Invariants.Total,
                ["first"] = new JsonArray([.. sim.Invariants.Recent().Take(5).Select(v => (JsonNode?)v.ToString())]),
            };
            if (sim.Invariants.Total > 0)
                Failures.Add($"нарушений инвариантов {sim.Invariants.Total}: {string.Join("; ", sim.Invariants.Recent().Take(3))}");

            RecordedFile = Path.Combine(dir, $"scenario-{scenario.Id}{Replay.Extension}");
            recorder.ToReplay().Save(RecordedFile);
            Json["recorded"] = RecordedFile;
        }

        public (bool Ok, string Detail, JsonObject Json) Metric(FeatureMetric m, string repoRoot)
        {
            var json = new JsonObject
            {
                ["id"] = m.Id, ["scenario"] = m.Scenario, ["source"] = m.Source, ["reference"] = m.Reference, ["entity"] = m.Entity,
                ["tolerance"] = m.Tolerance,
            };
            (bool ok, string detail) = Evaluate(m, repoRoot, json);
            json["ok"] = ok;
            json["detail"] = detail;
            return (ok, detail, json);
        }

        private (bool, string) Evaluate(FeatureMetric m, string repoRoot, JsonObject json)
        {
            if (!Completed) return (false, "сценарий не сыгран");
            if (m.Trace is not null) return Trace(m, repoRoot, json);
            if (!_series.TryGetValue((m.Entity, m.Field!), out var values)) return (false, "ряда нет — сценарий не сыгран");
            json["field"] = m.Field;
            json["stat"] = m.Stat;
            json["expected"] = m.Expected;
            double value;
            if (m.Stat == "value")
            {
                ulong at = m.At ?? Ticks;
                if (at > Ticks) return (false, $"тик {at} за концом сценария ({Ticks})");
                json["at"] = at;
                value = values[at];
            }
            else
            {
                ulong from = m.From ?? 1, to = m.To ?? Ticks;
                if (to > Ticks || from > to) return (false, $"окно {from}…{to} вне сценария (1…{Ticks})");
                json["from"] = from;
                json["to"] = to;
                var window = values.AsSpan((int)from, (int)(to - from + 1));
                value = m.Stat switch
                {
                    "min" => Min(window), "max" => Max(window), _ => Mean(window),
                };
            }
            json["value"] = double.IsFinite(value) ? value : value.ToString(CultureInfo.InvariantCulture);
            if (double.IsNaN(value)) return (false, "значения нет (сущности или поля не было)");
            double delta = Math.Abs(value - m.Expected!.Value);
            json["delta"] = delta;
            return delta <= m.Tolerance
                ? (true, $"{Fmt(value)} ≈ {Fmt(m.Expected.Value)} (±{Fmt(m.Tolerance)})")
                : (false, $"{Fmt(value)}, ждали {Fmt(m.Expected.Value)} ±{Fmt(m.Tolerance)} (разница {Fmt(delta)})");
        }

        private (bool, string) Trace(FeatureMetric m, string repoRoot, JsonObject json)
        {
            string path = Path.Combine(repoRoot, m.Trace!);
            json["trace"] = m.Trace;
            if (!File.Exists(path)) return (false, $"нет трассы {m.Trace}");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("ticks", out var ticks) || ticks.ValueKind != JsonValueKind.Array)
                return (false, "в трассе нет массива ticks");
            long compared = 0, mismatches = 0;
            JsonObject? first = null;
            foreach (var entry in ticks.EnumerateArray())
            {
                ulong tick = entry.GetProperty("tick").GetUInt64();
                if (tick > Ticks) return (false, $"трасса длиннее сценария: тик {tick}, сыграно {Ticks}");
                foreach (var (key, field) in m.Series!)
                {
                    double expected = TraceValue(entry, key);
                    double actual = _series[(m.Entity, field)][tick];
                    compared++;
                    // Трасса — числа float SWORD: сравнение в float (допуск 0 — побитово, кроме −0 = +0).
                    float fe = (float)expected, fa = (float)actual;
                    if (Math.Abs(fe - fa) <= m.Tolerance) continue;
                    mismatches++;
                    first ??= new JsonObject
                    {
                        ["tick"] = tick, ["key"] = key, ["field"] = field,
                        ["sword"] = fe.ToString("R", CultureInfo.InvariantCulture), ["sword2"] = fa.ToString("R", CultureInfo.InvariantCulture),
                    };
                }
            }
            json["compared"] = compared;
            json["mismatches"] = mismatches;
            json["firstMismatch"] = first;
            if (compared == 0) return (false, "сравнивать нечего");
            return mismatches == 0
                ? (true, $"совпало {compared} значений ({m.Series!.Count} рядов по {compared / m.Series.Count} тиков)")
                : (false, $"не совпало {mismatches} из {compared}; первое — тик {first!["tick"]}, {first["key"]}: SWORD {first["sword"]}, SWORD2 {first["sword2"]}");
        }

        private static double TraceValue(JsonElement entry, string key)
        {
            int bracket = key.IndexOf('[');
            string name = bracket < 0 ? key : key[..bracket];
            if (!entry.TryGetProperty(name, out var value)) throw new InvalidDataException($"в трассе нет ключа {name}");
            if (bracket >= 0)
            {
                int index = int.Parse(key[(bracket + 1)..^1], CultureInfo.InvariantCulture);
                value = value[index];
            }
            return value.GetDouble();
        }

        private static double Min(ReadOnlySpan<double> v)
        {
            double r = double.PositiveInfinity;
            foreach (double x in v) r = Math.Min(r, x);
            return r;
        }

        private static double Max(ReadOnlySpan<double> v)
        {
            double r = double.NegativeInfinity;
            foreach (double x in v) r = Math.Max(r, x);
            return r;
        }

        private static double Mean(ReadOnlySpan<double> v)
        {
            double s = 0;
            foreach (double x in v) s += x;
            return v.Length == 0 ? double.NaN : s / v.Length;
        }
    }

    internal static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    // ── бюджет ─────────────────────────────────────────────────────────────

    private static (JsonObject Json, List<string> Failures) Budget(FeatureManifest manifest, FeatureBudget budget, FeatureScenario scenario,
        FeatureEnvironment env, CancellationToken cancel)
    {
        var failures = new List<string>();
        var json = new JsonObject { ["id"] = budget.Id, ["scenario"] = budget.Scenario, ["repeats"] = budget.Repeats, ["warmup"] = budget.Warmup };
        var runs = new List<SimulationMeasure>();
        for (int r = 0; r < budget.Repeats; r++)
        {
            // Как бенч: без инвариантов (их стоимость меряется отдельно), без записи, наблюдения и сверки хешей — только таймер стадий.
            // Реплей — без хешей: сверка хеша каждого тика мерила бы хеш, а не игру.
            var (sim, replay) = Prepare(manifest, scenario, env, ["sim.invariants=false"], failures, hashes: false);
            if (sim is null) break;
            ulong ticks = scenario.Ticks ?? replay!.Ticks;
            if (ticks <= (ulong)budget.Warmup)
            {
                failures.Add($"сценарий короче прогрева ({ticks} ≤ {budget.Warmup})");
                break;
            }
            runs.Add(SimulationMeasure.Run(sim, ticks, budget.Warmup, cancel));
        }
        if (runs.Count == 0)
        {
            json["ok"] = false;
            return (json, failures);
        }
        var ms = runs.Select(x => x.MsPerTick).Order().ToList();
        double median = Median(ms);
        json["msPerTick"] = new JsonObject
        {
            ["median"] = Math.Round(median, 5), ["min"] = Math.Round(ms[0], 5), ["max"] = Math.Round(ms[^1], 5), ["limit"] = budget.MsPerTick,
        };
        var stages = new JsonObject();
        foreach (string stage in runs[0].Stages.Keys)
            stages[stage] = Math.Round(Median([.. runs.Select(x => x.Stages[stage]).Order()]), 5);
        json["stages"] = stages;
        double bytes = runs.Max(x => x.AllocatedBytesPerTick);
        json["allocatedBytesPerTick"] = Math.Round(bytes, 3);

        // Машина за замеры повторов (точка Пб): поток на CPU, загрузка системы, частота, GC и JIT — и вердикт «код или машина».
        // Пределы не меняются: вердикт только объясняет провал времени (машина занята — так и сказано в провале).
        var machine = MachineReport.Combine(runs.Select(x => x.Machine));
        if (machine is not null)
        {
            var mj = machine.ToJson();
            mj["runs"] = new JsonArray([.. runs.Select(x => (JsonNode?)new JsonObject
            {
                ["msPerTick"] = Math.Round(x.MsPerTick, 5),
                ["verdict"] = x.Machine is { } m ? MachineReport.VerdictWord(m.Verdict) : null,
                ["threadOnCpuPct"] = x.Machine is { } m1 ? Math.Round(m1.ThreadOnCpu * 100, 1) : null,
                ["othersBusyPct"] = x.Machine is { } m2 ? Math.Round(m2.OthersBusyShare * 100, 1) : null,
                ["mhz"] = x.Machine is { } m3 ? Math.Round(m3.ProbeMhz) : null,
                ["gc"] = x.Machine?.Runtime.Collections, ["jit"] = x.Machine?.Runtime.JitProcess,
            })]);
            json["machine"] = mj;
        }
        string why = machine is { Verdict: not MachineVerdict.Code } ? $" — {machine.Text}" : "";

        if (budget.AllocatedBytesPerTick is { } maxBytes && bytes > maxBytes)
            failures.Add($"выделено {Fmt(bytes)} Б/тик, предел {Fmt(maxBytes)}");

        // Время систем (точка Па): ещё один прогон — с профайлером CPU; медиана и пределы выше — без него. Байты этого прогона
        // держат тот же предел: включённый профайлер не выделяет памяти.
        var (profiledSim, profiledReplay) = Prepare(manifest, scenario, env, ["sim.invariants=false"], failures, hashes: false);
        if (profiledSim is not null)
        {
            var profiled = SimulationMeasure.Run(profiledSim, scenario.Ticks ?? profiledReplay!.Ticks, budget.Warmup, cancel, profile: true);
            json["profiled"] = new JsonObject
            {
                ["note"] = "отдельный прогон с профайлером CPU — время систем; медиана и пределы бюджета — прогоны без него",
                ["msPerTick"] = Math.Round(profiled.MsPerTick, 5),
                ["allocatedBytesPerTick"] = Math.Round(profiled.AllocatedBytesPerTick, 3),
                ["runtime"] = profiled.Runtime is { } rt ? CpuProfiler.RuntimeJson(rt) : null,
            };
            json["systems"] = new JsonObject([.. profiled.Systems!.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Math.Round(p.Value, 5)))]);
            if (budget.AllocatedBytesPerTick is { } maxProfiled && profiled.AllocatedBytesPerTick > maxProfiled)
                failures.Add($"с профайлером выделено {Fmt(profiled.AllocatedBytesPerTick)} Б/тик, предел {Fmt(maxProfiled)}");
        }
        if (!env.Optimized)
            json["time"] = "не проверяется: сборка Debug (время — только в Release, verify.ps1 -Configuration Release)";
        else
        {
            if (budget.MsPerTick is { } limit && median > limit) failures.Add($"{Fmt(median)} мс/тик (медиана), предел {Fmt(limit)}{why}");
            foreach (var (stage, limit2) in budget.Stages)
            {
                if (!runs[0].Stages.TryGetValue(stage, out _)) failures.Add($"нет стадии {stage}; есть: {string.Join(", ", runs[0].Stages.Keys)}");
                else if ((double)stages[stage]! > limit2) failures.Add($"стадия {stage} — {stages[stage]} мс/тик, предел {Fmt(limit2)}{why}");
            }
        }
        json["stageLimits"] = new JsonObject([.. budget.Stages.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))]);
        json["ok"] = failures.Count == 0;
        json["failures"] = new JsonArray([.. failures.Select(f => (JsonNode?)f)]);
        return (json, failures);
    }

    /// <summary>
    /// Бюджет кадра: App играет запись сценария бенчем (<see cref="FeatureEnvironment.MeasureFrame"/>); хеши App обязаны
    /// совпасть с записью. Без видов (<c>--no-views</c>: App не запускается, например без видеокарты) — не меряется; время —
    /// проверяется только в Release, как время бюджета симуляции.
    /// </summary>
    private static void FrameBudget(string feature, FeatureBudget budget, FeatureFrameBudget frame, ScenarioRun run, FeatureEnvironment env,
        FeatureVerifyOptions options, JsonObject json, List<string> failures, CancellationToken cancel)
    {
        var fj = new JsonObject { ["seconds"] = frame.Seconds, ["limitAverageMs"] = frame.AverageMs, ["limitP99Ms"] = frame.P99Ms };
        if (frame.Overlays.Count > 0) fj["overlays"] = new JsonArray([.. frame.Overlays.Select(o => (JsonNode?)o)]);
        json["frame"] = fj;
        if (!options.Views)
        {
            fj["measured"] = "не мерялся: виды выключены (--no-views) — App не запускается";
            return;
        }
        if (env.MeasureFrame is null)
        {
            failures.Add("кадр: App нет — кадр меряет Sword.Headless рядом со собранным Sword.exe");
            return;
        }
        if (run.RecordedFile is null)
        {
            failures.Add("кадр: сценарий не записан — играть нечего");
            return;
        }
        ulong need = (ulong)Math.Ceiling(frame.Seconds * run.TickRate);
        if (run.Ticks < need)
        {
            failures.Add($"кадр: сценарий ({run.Ticks} тиков) короче бенча ({frame.Seconds} с — {need} тиков)");
            return;
        }
        var r = env.MeasureFrame(new FrameRequest(feature, budget.Id, run.RecordedFile, frame.Seconds) { Overlays = frame.Overlays }, cancel);
        fj["ok"] = r.Ok;
        fj["detail"] = r.Detail;
        fj["frames"] = r.Frames;
        if (double.IsFinite(r.AverageMs)) fj["averageMs"] = r.AverageMs;
        if (double.IsFinite(r.P99Ms)) fj["p99Ms"] = r.P99Ms;
        if (double.IsFinite(r.MaxMs)) fj["maxMs"] = r.MaxMs;
        if (r.Machine is not null) fj["machine"] = r.Machine.DeepClone();
        if (!r.Ok)
        {
            failures.Add($"кадр: {r.Detail}");
            return;
        }
        if (!env.Optimized)
        {
            fj["time"] = "не проверяется: сборка Debug (время — только в Release, verify.ps1 -Configuration Release)";
            return;
        }
        // Вердикт машины App (точка Пб) — в текст провала времени: «машина занята» видно сразу.
        string why = r.Machine?["kind"]?.GetValue<string>() is { } kind && kind != "code" ? $" — {r.Machine["text"]}" : "";
        if (frame.AverageMs is { } avg && !(r.AverageMs <= avg)) failures.Add($"кадр {Fmt(r.AverageMs)} мс в среднем, предел {Fmt(avg)}{why}");
        if (frame.P99Ms is { } p99 && !(r.P99Ms <= p99)) failures.Add($"кадр {Fmt(r.P99Ms)} мс (99-й перцентиль), предел {Fmt(p99)}{why}");
    }

    internal static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count == 0 ? double.NaN : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

/// <summary>
/// Замер прогона (бюджеты фич, <c>experiment.sweep</c>): мс на тик после прогрева, мс стадий (<see cref="StageTimer"/>),
/// выделенные байты на тик потока симуляции после прогрева; с профайлером (<c>profile: true</c>, точка Па) — ещё мс систем
/// (<see cref="Systems"/>: зоны <c>метка@стадия</c> профайлера CPU, среднее на тик после прогрева). Прогон с профайлером —
/// отдельный: медианы бюджета меряются без него (пара зон включённого — ~0,04 мкс; тик толпы 32 врагов без инвариантов
/// с ним дольше на ~5 %: 0,110 против 0,105 мс, Release).
/// </summary>
public sealed record SimulationMeasure(double MsPerTick, IReadOnlyDictionary<string, double> Stages, double AllocatedBytesPerTick)
{
    /// <summary>Мс систем на тик (по убыванию) — только у прогона с профайлером, иначе null.</summary>
    public IReadOnlyList<KeyValuePair<string, double>>? Systems { get; init; }

    /// <summary>
    /// Машина за замер (точка Пб): от первого тика после прогрева до конца — поток на CPU, загрузка системы, частота, GC и JIT
    /// во время замера, вердикт «код или машина». Снимки — на границах замера, вне тиков (в мс на тик не входят).
    /// </summary>
    public MachineReport? Machine { get; init; }

    /// <summary>Среда тиков прогона с профайлером (CPU потока, GC, JIT за тик); без профайлера — null.</summary>
    public ProfileRuntimeStats? Runtime { get; init; }

    /// <summary>Сыграть симуляцию до <paramref name="ticks"/> тиков; первые <paramref name="warmup"/> — прогрев, не меряются.</summary>
    /// <param name="profile">С профайлером CPU (время систем); симуляции он не меняет.</param>
    public static SimulationMeasure Run(Simulation sim, ulong ticks, int warmup, CancellationToken cancel = default, bool profile = false)
    {
        var timer = new StageTimer(sim);
        sim.Observe(timer);
        var profiler = profile ? new CpuProfiler(history: 0) : null;
        if (profiler is not null)
        {
            sim.AttachProfiler(profiler);
            profiler.SetEnabled(true);
        }
        long allocated = 0, elapsed = 0;
        ulong measured = 0;
        MachineSample? machineStart = null;
        while (sim.Tick < ticks)
        {
            if ((sim.Tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
            sim.Export.BeginFrame();
            if (sim.Tick < (ulong)warmup)
            {
                sim.Step();
                if (sim.Tick == (ulong)warmup) profiler?.Reset();
                continue;
            }
            machineStart ??= MachineSample.Take();   // граница замера (точка Пб): вне времени и байтов тиков
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            timer.Begin();
            sim.Step();
            elapsed += Stopwatch.GetTimestamp() - start;
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            measured++;
        }
        var machine = machineStart is { } first ? MachineReport.Between(first, MachineSample.Take(), computeBound: true) : null;
        sim.Unobserve(timer);
        IReadOnlyList<KeyValuePair<string, double>>? systems = null;
        ProfileRuntimeStats? runtime = null;
        if (profiler is not null)
        {
            systems = [.. profiler.Stats(ProfilePeriod.Tick).Where(z => z.Name.Contains('@')).Select(z => KeyValuePair.Create(z.Name, z.MeanMs))];
            runtime = profiler.RuntimeStats(ProfilePeriod.Tick);
            sim.AttachProfiler(CpuProfiler.Off);
        }
        return new SimulationMeasure(
            measured == 0 ? 0 : elapsed * 1000.0 / Stopwatch.Frequency / measured, timer.Means(),
            measured == 0 ? 0 : allocated / (double)measured)
        {
            Systems = systems,
            Machine = machine,
            Runtime = runtime,
        };
    }
}

/// <summary>
/// Значение поля сущности после тика (метрики фич, <c>experiment.fork</c>): сущность — номер «12v3» или имя, поле —
/// путь (<c>Transform.Position.X</c>); путь к нескольким листьям (<c>RootVelocity.Value</c>) — длина вектора. Нет
/// сущности или компонента — NaN.
/// </summary>
public sealed class FieldProbe
{
    private readonly IEntityNames? _names;
    private readonly string _entityText;
    private readonly int _component;
    private readonly FieldLayout _layout = null!;
    private readonly int[] _leaves = [];
    private readonly EntityFieldReader? _computed;
    private Entity? _entity;

    /// <param name="fields">Вычисляемые поля игры: путь, который они понимают, читается ими (не компонентом).</param>
    public FieldProbe(Catalog catalog, IEntityNames? names, string entity, string field, string at, IEntityFields? fields = null)
    {
        _names = names;
        _entityText = entity;
        EntityName = entity;
        FieldPath = field;
        if (fields?.Compile(field, at) is { } computed)
        {
            _computed = computed;
            return;
        }
        var (component, rest) = ObservationPredicate.SplitComponent(catalog, field, at);
        if (component.IsTag) throw new ArgumentException($"{at}: {component.Name} — метка без полей");
        _component = component.Id;
        _layout = FieldLayout.Of(component.Type);
        _leaves = ObservationPredicate.LeavesOf(_layout, rest, at);
        foreach (int leaf in _leaves)
            if (_layout.Leaves[leaf].Kind is FieldLayout.Kind.Entity or FieldLayout.Kind.Bool or FieldLayout.Kind.Char)
                throw new ArgumentException($"{at}: {field} — не число");
    }

    public string EntityName { get; }
    public string FieldPath { get; }

    public double Read(in ServiceContext ctx)
    {
        var world = ctx.World;
        if (_entity is not { } e || !world.IsAlive(e))
        {
            _entity = _entityText.Length > 0 && char.IsDigit(_entityText[0]) ? FieldCodec.ParseEntity(_entityText) : _names?.Resolve(ctx, _entityText);
            if (_entity is not { } found || !world.IsAlive(found)) return double.NaN;
            e = found;
        }
        if (_computed is not null) return _computed(ctx, e);
        if (!world.TryRead(e, _component, out var bytes)) return double.NaN;
        if (_leaves.Length == 1) return ObservationPredicate.Number(_layout.Leaves[_leaves[0]].Kind, _layout.Bits(bytes, _leaves[0]));
        double sum = 0;
        foreach (int leaf in _leaves)
        {
            double x = ObservationPredicate.Number(_layout.Leaves[leaf].Kind, _layout.Bits(bytes, leaf));
            sum += x * x;
        }
        return Math.Sqrt(sum);
    }
}
