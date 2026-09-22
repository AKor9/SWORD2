using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Эксперименты (этап 3д, раздел 8 плана): <c>experiment.fork</c> — реплей до тика и дальше ветками, <c>experiment.compare</c>
/// — два прогона (хеши, команды, разница описаний участников), <c>experiment.sweep</c> — перебор конфига с замерами
/// (<see cref="Experiments"/>). Отдельные симуляции той же композиции — сеанс не трогается; только Headless.
/// </summary>
internal static class ExperimentTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "experiment.fork", Title = "Развилка реплея",
            Description = "Сыграть реплей до тика at и дальше ветками (параллельно): база — реплей как есть, варианты variants — " +
                "{name, set — ключи конфига поверх конфига реплея (действуют с тика 0: конфиг собирает симуляцию), commands — " +
                "[{command, fields, from, to}] на тиках from…to−1 не раньше at (перебивают команды реплея своего типа), cut — " +
                "команды реплея после at не идут}. Каждая ветка — до ticks тиков (по умолчанию длина реплея), запись с хешами " +
                "каждого тика — в артефакты. Ответ — по ветке: сравнение с базой (firstHashTick — после скольких тиков хеши " +
                "разошлись, участники; firstCommandTick), measure в конце ([{entity, field}]), нарушения инвариантов, мс на тик. " +
                "Команда тика at меняет состояние после at+1 тиков. Дальше — experiment.compare по файлам веток. Только Headless.",
            Arguments = CoreTools.Props(
                ("replay", CoreTools.Str("реплей: путь, имя в каталоге артефактов или «session»")),
                ("at", CoreTools.Int("тик развилки", 0, long.MaxValue)),
                ("ticks", CoreTools.Int("до скольких тиков играть ветки (по умолчанию — длина реплея)", 1, 10_000_000)),
                ("variants", new JsonObject
                {
                    ["type"] = "array", ["description"] = "ветки: {name, set:[\"секция.ключ=значение\"], commands:[{command, fields, from, to}], cut}",
                    ["items"] = new JsonObject { ["type"] = "object" },
                }),
                ("measure", new JsonObject
                {
                    ["type"] = "array", ["description"] = "что измерить в конце ветки: [{entity:\"player\", field:\"Transform.Position\"}]",
                    ["items"] = new JsonObject { ["type"] = "object" },
                }),
                ("timeoutSeconds", CoreTools.Num("сколько ждать, с (по умолчанию 120)", 1, 3600))),
            Required = ["replay", "at", "variants"],
            ReadOnly = false, Idempotent = true, Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = ReplayTools.RequireRebuild(host);
                var (replay, path) = await ReplayTools.Load(host, call.RequiredString("replay"), call.Cancel);
                ReplayTools.CheckContent(host, replay);
                ulong at = (ulong)call.Integer("at", 0, 0, long.MaxValue);
                ulong ticks = (ulong)call.Integer("ticks", (long)Math.Min(replay.Ticks, 10_000_000), 1, 10_000_000);
                var variants = Objects(call.Node("variants"), "variants").Select((v, i) => Variant(v, $"variants[{i}]")).ToList();
                var measure = Objects(call.Node("measure"), "measure").Select((m, i) => new ForkMeasure(
                    Text(m, "entity", $"measure[{i}]"), Text(m, "field", $"measure[{i}]"))).ToList();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(call.Number("timeoutSeconds", 120, 1, 3600), host.MaxWaitSeconds)));
                var branches = await Task.Run(() => Experiments.Fork(replay, at, ticks, variants, measure, session.Build, session.Info, host.Names,
                    timeout.Token), timeout.Token).ConfigureAwait(false);

                string stem = ReplayTools.ArtifactName($"fork-{Path.GetFileNameWithoutExtension(path)}-t{at}")[..^Replay.Extension.Length];
                var list = new JsonArray();
                foreach (var b in branches)
                {
                    string? file = null;
                    if (b.Recorded is { } rec)
                    {
                        file = host.ArtifactPath($"{stem}-{b.Name}{Replay.Extension}");
                        rec.Save(file);
                    }
                    list.Add(Branch(b, file));
                }
                Log.Info("agent", $"experiment.fork {path} @ {at}: веток {branches.Count}");
                return new ToolOutput(new JsonObject
                {
                    ["replay"] = path, ["at"] = at, ["ticks"] = ticks, ["branches"] = list,
                    ["hint"] = "firstHashTick — после стольких тиков состояние ветки впервые отличается от базы; команда тика at — это at+1; " +
                        "что именно отличается — experiment.compare {a, b} по файлам веток",
                });
            },
        });

        host.Add(new AgentTool
        {
            Name = "experiment.compare", Title = "Сравнить прогоны",
            Description = "Сравнить два прогона (реплея с хешами — например ветки experiment.fork): заголовки и конфиг по ключам, " +
                "первый тик с разными командами, первый хеш с разными участниками и последний совпавший; describe (по умолчанию " +
                "true) — оба прогона переигрываются до первого разошедшегося хеша и сравниваются описания разошедшихся участников " +
                "(state.describe) по листьям: путь, a, b. Только Headless.",
            Arguments = CoreTools.Props(
                ("a", CoreTools.Str("первый прогон: путь, имя в каталоге артефактов или «session»")),
                ("b", CoreTools.Str("второй прогон: то же")),
                ("describe", new JsonObject { ["type"] = "boolean", ["description"] = "сравнить описания участников (по умолчанию true)" }),
                ("maxLines", CoreTools.Int("строк разницы на участника (по умолчанию 50)", 1, 1000)),
                ("timeoutSeconds", CoreTools.Num("сколько ждать, с (по умолчанию 120)", 1, 3600))),
            Required = ["a", "b"],
            Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = ReplayTools.RequireRebuild(host);
                var (a, pa) = await ReplayTools.Load(host, call.RequiredString("a"), call.Cancel);
                var (b, pb) = await ReplayTools.Load(host, call.RequiredString("b"), call.Cancel);
                ReplayTools.CheckContent(host, a);
                ReplayTools.CheckContent(host, b);
                bool describe = call.Bool("describe", true);
                int maxLines = (int)call.Integer("maxLines", 50, 1, 1000);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(call.Number("timeoutSeconds", 120, 1, 3600), host.MaxWaitSeconds)));
                var (diff, at, parts) = await Task.Run(() => Experiments.Compare(a, b, session.Build, describe, maxLines, timeout.Token), timeout.Token)
                    .ConfigureAwait(false);
                var data = Diff(diff);
                data["a"] = pa;
                data["b"] = pb;
                data["describedAt"] = at;
                data["describe"] = new JsonArray([.. parts.Select(p => (JsonNode?)new JsonObject
                {
                    ["part"] = p.Part, ["differ"] = p.Total,
                    ["lines"] = new JsonArray([.. p.Lines.Select(l => (JsonNode?)new JsonObject { ["path"] = l.Path, ["a"] = l.A, ["b"] = l.B })]),
                })]);
                return new ToolOutput(data) { Truncated = parts.Any(p => p.Total > p.Lines.Count) };
            },
        });

        host.Add(new AgentTool
        {
            Name = "experiment.sweep", Title = "Перебор конфига",
            Description = "Перебор комбинаций ключей конфига (params: {\"секция.ключ\": [значения]}, до 64 комбинаций) — каждая repeats раз " +
                "по очереди: реплей replay (без него — мир без команд) до ticks тиков, первые warmup — прогрев. Строка — ключи, мс на " +
                "тик (медиана, мин, макс), мс стадий, байты на тик, сущностей, хеш итога и одинаков ли он у повторов " +
                "(детерминизм), нарушения инвариантов. scaling — первое резкое ухудшение масштабирования по числовому ключу. " +
                "Время осмысленно в Release. Только Headless.",
            Arguments = CoreTools.Props(
                ("params", new JsonObject { ["type"] = "object", ["description"] = "ключ конфига → массив значений: {\"sim.tickRate\":[60,120]}" }),
                ("replay", CoreTools.Str("реплей-сценарий: путь, имя в артефактах или «session» (без него — мир без команд)")),
                ("ticks", CoreTools.Int("тиков прогона (по умолчанию — длина реплея или 600)", 2, 10_000_000)),
                ("repeats", CoreTools.Int("повторов каждой комбинации (по умолчанию 3)", 1, 20)),
                ("warmup", CoreTools.Int("тиков прогрева (по умолчанию 120)", 0, 100_000)),
                ("timeoutSeconds", CoreTools.Num("сколько ждать, с (по умолчанию 300)", 1, 3600))),
            Required = ["params"],
            Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = ReplayTools.RequireRebuild(host);
                Replay? replay = null;
                string? path = null;
                if (call.String("replay") is { } which)
                {
                    (replay, path) = await ReplayTools.Load(host, which, call.Cancel);
                    ReplayTools.CheckContent(host, replay);
                }
                var parameters = Params(call.Node("params"));
                ulong ticks = (ulong)call.Integer("ticks", replay is null ? 600 : (long)Math.Min(replay.Ticks, 10_000_000), 2, 10_000_000);
                int repeats = (int)call.Integer("repeats", 3, 1, 20), warmup = (int)call.Integer("warmup", 120, 0, 100_000);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(call.Number("timeoutSeconds", 300, 1, 3600), host.MaxWaitSeconds)));
                var (rows, scaling) = await Task.Run(() => Experiments.Sweep(replay, ticks, parameters, repeats, warmup, session.Build, timeout.Token),
                    timeout.Token).ConfigureAwait(false);
                return new ToolOutput(new JsonObject
                {
                    ["replay"] = path, ["ticks"] = ticks, ["repeats"] = repeats, ["warmup"] = warmup,
                    ["optimized"] = host.Features?.Optimized,
                    ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject
                    {
                        ["set"] = new JsonArray([.. r.Sets.Select(s => (JsonNode?)s)]),
                        ["msPerTick"] = new JsonObject
                        {
                            ["median"] = Math.Round(r.MsMedian, 5), ["min"] = Math.Round(r.MsMin, 5), ["max"] = Math.Round(r.MsMax, 5),
                            ["spread"] = r.MsMedian > 0 ? Math.Round((r.MsMax - r.MsMin) / r.MsMedian, 3) : 0,
                        },
                        ["stages"] = new JsonObject([.. r.Stages.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Math.Round(p.Value, 5)))]),
                        ["allocatedBytesPerTick"] = Math.Round(r.AllocatedBytesPerTick, 3),
                        ["entities"] = r.Entities, ["hash"] = r.Hash, ["deterministic"] = r.Deterministic, ["violations"] = r.Violations,
                        ["error"] = r.Error,
                    })]),
                    ["scaling"] = new JsonArray([.. scaling.Select(s => (JsonNode?)s)]),
                });
            },
        });
    }

    internal static JsonObject Diff(ReplayDiff.Result d) => new()
    {
        ["same"] = d.Header.Count == 0 && d.FirstCommandTick is null && d.FirstHashTick is null,
        ["header"] = new JsonArray([.. d.Header.Take(50).Select(h => (JsonNode?)h)]),
        ["commands"] = new JsonObject { ["firstTick"] = d.FirstCommandTick, ["detail"] = d.CommandDetail },
        ["hashes"] = new JsonObject
        {
            ["common"] = d.CommonHashes, ["firstHashTick"] = d.FirstHashTick, ["lastCommon"] = d.LastCommonHash,
            ["parts"] = new JsonArray([.. d.HashParts.Select(p => (JsonNode?)p)]),
        },
    };

    private static JsonObject Branch(ForkBranch b, string? file)
    {
        var o = new JsonObject
        {
            ["name"] = b.Name, ["file"] = file, ["ticks"] = b.Ticks, ["msPerTick"] = Math.Round(b.MsPerTick, 4), ["error"] = b.Error,
            ["violations"] = b.Violations, ["firstViolation"] = b.FirstViolation,
            ["measure"] = new JsonObject([.. b.Measures.Select(p => KeyValuePair.Create(p.Key,
                (JsonNode?)(double.IsFinite(p.Value) ? JsonValue.Create(p.Value) : JsonValue.Create(p.Value.ToString(CultureInfo.InvariantCulture)))))]),
        };
        if (b.Variant is { } v)
            o["variant"] = new JsonObject
            {
                ["set"] = new JsonArray([.. v.Sets.Select(s => (JsonNode?)s)]), ["commands"] = v.Commands.Count, ["cut"] = v.Cut,
            };
        if (b.VsBase is { } d)
        {
            var diff = Diff(d);
            // Главное — сверху: после скольких тиков ветка впервые другая, последний общий хеш, чем.
            diff["firstHashTick"] = d.FirstHashTick;
            diff["lastCommon"] = d.LastCommonHash;
            diff["parts"] = new JsonArray([.. d.HashParts.Select(p => (JsonNode?)p)]);
            o["vsBase"] = diff;
        }
        if (b.PlaybackDivergence is not null) o["baseReplayDivergence"] = b.PlaybackDivergence;
        return o;
    }

    private static ForkVariant Variant(JsonObject v, string at)
    {
        foreach (var (key, _) in v)
            if (key is not ("name" or "set" or "commands" or "cut")) throw new ArgumentException($"{at}.{key}: неизвестный ключ; есть: name, set, commands, cut");
        var sets = v["set"] switch
        {
            null => [],
            JsonArray a => a.Select((n, i) => n is JsonValue s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>()
                : throw new ArgumentException($"{at}.set[{i}]: строка «секция.ключ=значение»")).ToList(),
            _ => throw new ArgumentException($"{at}.set: массив строк"),
        };
        var commands = Objects(v["commands"], $"{at}.commands").Select((c, i) => ScenarioCommand.FromJson(c, $"{at}.commands[{i}]")).ToList();
        bool cut = v["cut"] switch
        {
            null => false,
            JsonValue b when b.GetValueKind() is JsonValueKind.True or JsonValueKind.False => b.GetValue<bool>(),
            _ => throw new ArgumentException($"{at}.cut: true или false"),
        };
        return new ForkVariant(Text(v, "name", at), sets, commands, cut);
    }

    private static IReadOnlyList<(string Key, IReadOnlyList<string> Values)> Params(JsonNode? node)
    {
        if (node is not JsonObject o) throw new ArgumentException("params: объект {\"секция.ключ\": [значения]}");
        var list = new List<(string, IReadOnlyList<string>)>();
        foreach (var (key, value) in o)
        {
            if (value is not JsonArray a) throw new ArgumentException($"params.{key}: массив значений");
            list.Add((key, [.. a.Select((n, i) => n is JsonValue v
                ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString()
                : throw new ArgumentException($"params.{key}[{i}]: число, строка или true/false"))]));
        }
        return list;
    }

    private static List<JsonObject> Objects(JsonNode? node, string at) => node switch
    {
        null => [],
        JsonArray a => [.. a.Select((n, i) => n as JsonObject ?? throw new ArgumentException($"{at}[{i}]: объект"))],
        _ => throw new ArgumentException($"{at}: массив объектов"),
    };

    private static string Text(JsonObject o, string key, string at) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s ? s
            : throw new ArgumentException($"{at}.{key}: непустая строка");
}
