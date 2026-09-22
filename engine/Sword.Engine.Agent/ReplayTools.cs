using System.Diagnostics;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Реплеи и сеанс (этап 3б, раздел 8 плана): запись сеанса с тика 0 (<c>replay.save</c>, <c>replay.status</c>), сверка
/// реплея в отдельной симуляции (<c>replay.check</c>), сравнение двух реплеев (<c>replay.diff</c>), новая симуляция
/// из конфига или реплея (<c>session.start</c>) и перемотка (<c>time.seek</c>). Пересборка симуляции — только у Headless
/// (<see cref="AgentHost.Simulation"/> с построителем); запись и сравнение — везде.
/// </summary>
internal static class ReplayTools
{
    /// <summary>Слово вместо пути: запись текущего сеанса.</summary>
    private const string SessionWord = "session";

    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "replay.status", Title = "Запись и реплей сеанса",
            Description = "Запись сеанса (пишется с тика 0: тиков, хешей, отрезков команд) и воспроизводимый реплей (файл, длина, " +
                "сверено хешей, последнее совпадение, первое расхождение — тик и участники).",
            Run = async call =>
            {
                var s = await host.Serve(ctx => (Recording: Recording(ctx.Recorder), Playback: Playback(ctx.Player, ctx.Tick), ctx.Point), call.Cancel);
                return new ToolOutput(new JsonObject
                {
                    ["run"] = host.Simulation?.Run,
                    ["source"] = host.Simulation?.SourceFile,
                    ["recording"] = s.Recording,
                    ["playback"] = s.Playback,
                }) { Point = s.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "replay.save", Title = "Сохранить реплей сеанса",
            Description = "Сохранить запись сеанса (команды всех типов по тикам и хеши состояния с тика 0) в файл .swreplay " +
                "в каталоге артефактов. Его воспроизводит Sword.Headless --replay и session.start {replay}.",
            Arguments = Props(("name", Str("имя файла в каталоге артефактов (по умолчанию session-r<номер>-t<тик>.swreplay)"))),
            Cost = ToolCost.Moderate,
            Run = async call =>
            {
                string? name = call.String("name");
                var r = await host.Serve(ctx => (Replay: ctx.Recorder?.ToReplay(), ctx.Point), call.Cancel);
                var replay = r.Replay ?? throw new InvalidOperationException("симуляция не пишет реплей (запись начинается только с тика 0)");
                string file = ArtifactName(name ?? $"session-r{host.Simulation?.Run ?? 1}-t{r.Point.Tick}");
                string path = host.ArtifactPath(file);
                replay.Save(path);
                Log.Info("agent", $"replay.save: {path} ({replay.Ticks} тиков)");
                return new ToolOutput(Summary(replay, path)) { Point = r.Point, ArtifactId = file };
            },
        });

        host.Add(new AgentTool
        {
            Name = "replay.check", Title = "Проверить реплей",
            Description = "Воспроизвести реплей целиком в отдельной симуляции (сеанс не трогается) и сверить хеши по участникам: " +
                "совпал ли, первое расхождение — после скольких тиков и у каких участников (animation, ecs…), последнее совпадение. " +
                "set — ключи конфига поверх реплея (эксперимент). Только Headless.",
            Arguments = Props(
                ("path", Str("файл реплея: путь или имя в каталоге артефактов; «session» — запись сеанса")),
                ("set", StrArray("ключи конфига секция.ключ=значение поверх конфига реплея")),
                ("timeoutSeconds", Num("сколько ждать, с (по умолчанию 120)", 1, 3600))),
            Required = ["path"],
            Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = RequireRebuild(host);
                var sets = call.Strings("set");
                double seconds = call.Number("timeoutSeconds", 120, 1, 3600);
                var (replay, path) = await Load(host, call.RequiredString("path"), call.Cancel);
                CheckContent(host, replay);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                var result = await Task.Run(() =>
                {
                    long started = Stopwatch.GetTimestamp();
                    var sim = session.Build(replay.CreateConfig(sets));
                    var player = sim.Play(replay);
                    while (sim.Tick < replay.Ticks)
                    {
                        if ((sim.Tick & 1023) == 0) timeout.Token.ThrowIfCancellationRequested();
                        sim.Export.BeginFrame();
                        sim.Step();
                    }
                    return (player, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }, timeout.Token).ConfigureAwait(false);
                var data = Playback(result.player, replay.Ticks)!;
                data["file"] = path;
                data["msPerTick"] = replay.Ticks > 0 ? Math.Round(result.Item2 / replay.Ticks, 4) : 0;
                if (sets.Count > 0) data["set"] = new JsonArray([.. sets.Select(s => (JsonNode?)s)]);
                return new ToolOutput(data);
            },
        });

        host.Add(new AgentTool
        {
            Name = "replay.diff", Title = "Сравнить реплеи",
            Description = "Сравнить два реплея: заголовки (сборка, контент, конфиг по ключам, модули), первый тик с разными " +
                "командами (какие), первый общий хеш с разными участниками и последний совпавший. «session» — запись сеанса.",
            Arguments = Props(
                ("a", Str("первый реплей: путь, имя в каталоге артефактов или «session»")),
                ("b", Str("второй реплей: то же"))),
            Required = ["a", "b"],
            Cost = ToolCost.Moderate,
            Run = async call =>
            {
                var (a, pa) = await Load(host, call.RequiredString("a"), call.Cancel);
                var (b, pb) = await Load(host, call.RequiredString("b"), call.Cancel);
                var d = ReplayDiff.Compare(a, b);
                return new ToolOutput(new JsonObject
                {
                    ["a"] = pa,
                    ["b"] = pb,
                    ["same"] = d.Header.Count == 0 && d.FirstCommandTick is null && d.FirstHashTick is null,
                    ["header"] = new JsonArray([.. d.Header.Take(50).Select(h => (JsonNode?)h)]),
                    ["commands"] = new JsonObject { ["firstTick"] = d.FirstCommandTick, ["detail"] = d.CommandDetail },
                    ["hashes"] = new JsonObject
                    {
                        ["common"] = d.CommonHashes, ["firstTick"] = d.FirstHashTick, ["lastCommon"] = d.LastCommonHash,
                        ["parts"] = new JsonArray([.. d.HashParts.Select(p => (JsonNode?)p)]),
                    },
                });
            },
        });

        host.Add(new AgentTool
        {
            Name = "replay.observe", Title = "Наблюдать прогон реплея",
            Description = "Прогнать реплей в отдельной симуляции (сеанс не трогается) и записать наблюдение глубже кольца истории: " +
                "события типов events («*» — все, InvariantViolation — нарушения инвариантов; tick — номер тика), строки наблюдения watch (как watch.add; tickId), переходы " +
                "условия condition (tickId, true/false) и сводка — первый и последний tickId, когда условие выполнялось. Строки — " +
                "в файл .jsonl в артефактах, в ответе — сводка и первые строки. " + ObservationPredicate.Syntax + " Только Headless.",
            Arguments = Props(
                ("path", Str("реплей: путь, имя в каталоге артефактов или «session»")),
                ("from", Int("писать с тика (по умолчанию 0)", 0, long.MaxValue)),
                ("to", Int("до скольких тиков (по умолчанию — длина реплея)", 0, long.MaxValue)),
                ("events", StrArray("типы событий: PlayerCommand, Added<Fighter>, * — все")),
                ("watch", new JsonObject { ["type"] = "object", ["description"] = "наблюдение: {entities, where, fields, every} как у watch.add" }),
                ("condition", new JsonObject { ["type"] = "object", ["description"] = "условие мира (язык — в описании)" }),
                ("set", StrArray("ключи конфига секция.ключ=значение поверх конфига реплея")),
                ("timeoutSeconds", Num("сколько ждать, с (по умолчанию 120)", 1, 3600))),
            Required = ["path"],
            Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = RequireRebuild(host);
                var sets = call.Strings("set");
                double seconds = call.Number("timeoutSeconds", 120, 1, 3600);
                var (replay, path) = await Load(host, call.RequiredString("path"), call.Cancel);
                CheckContent(host, replay);
                var options = new ReplayObserveOptions
                {
                    From = (ulong)call.Integer("from", 0, 0, long.MaxValue),
                    To = (ulong)call.Integer("to", (long)Math.Min(replay.Ticks, long.MaxValue), 0, long.MaxValue),
                    Events = call.Strings("events"),
                    Watch = call.Node("watch") is { } w ? w as JsonObject ?? throw new ArgumentException("watch: объект {fields, entities, where}") : null,
                    Condition = call.Node("condition"),
                };
                if (options.Events.Count == 0 && options.Watch is null && options.Condition is null)
                    throw new ArgumentException("нечего наблюдать: events, watch или condition");
                if (options.To - Math.Min(options.To, options.From) > 10_000_000) throw new ArgumentException("не больше 10 000 000 тиков");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                var result = await Task.Run(() =>
                {
                    var sim = session.Build(replay.CreateConfig(sets));
                    return ReplayObservation.Run(sim, replay, host.Names, options, timeout.Token);
                }, timeout.Token).ConfigureAwait(false);

                string file = ArtifactName($"observe-{Path.GetFileNameWithoutExtension(path)}-{options.From}-{options.To}").Replace(Replay.Extension, ".jsonl");
                using (var writer = new StreamWriter(host.ArtifactPath(file), false, new System.Text.UTF8Encoding(false)) { NewLine = "\r\n" })
                    foreach (var line in result.Lines) writer.WriteLine(line.ToJsonString());
                var data = new JsonObject
                {
                    ["file"] = host.ArtifactPath(file),
                    ["ticks"] = result.Ticks,
                    ["lines"] = result.Lines.Count,
                    ["truncated"] = result.Truncated,
                    ["events"] = new JsonObject([.. result.EventCounts.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))]),
                    ["watchRows"] = result.WatchRows,
                    ["violations"] = new JsonObject
                    {
                        ["count"] = result.Violations,
                        ["first"] = result.FirstViolation is { } v ? ObservationTools.ViolationJson(v) : null,
                    },
                    ["condition"] = result.Condition is { } c ? new JsonObject
                    {
                        ["firstTickId"] = c.First, ["lastTickId"] = c.Last, ["ticks"] = c.Ticks, ["cost"] = result.ConditionCost,
                    } : null,
                    ["playback"] = Playback(result.Player, result.Ticks),
                    ["head"] = new JsonArray([.. result.Lines.Take(20).Select(l => (JsonNode?)l.DeepClone())]),
                };
                if (sets.Count > 0) data["set"] = new JsonArray([.. sets.Select(s => (JsonNode?)s)]);
                return new ToolOutput(data) { ArtifactId = file, Truncated = result.Truncated };
            },
        });

        host.Add(new AgentTool
        {
            Name = "session.start", Title = "Новая симуляция",
            Description = "Начать симуляцию заново с тика 0 (на паузе): из конфига запуска (файлы и set) или из реплея — тогда его " +
                "команды идут в каждом тике первыми, хеши сверяются (replay.status), а свои команды (command.send) ветвят его. " +
                "Запись прежней симуляции сохраняется в артефакты. Только Headless.",
            Arguments = Props(
                ("replay", Str("реплей: путь или имя в каталоге артефактов; без него — конфиг запуска")),
                ("set", StrArray("ключи конфига секция.ключ=значение (поверх файлов или конфига реплея)"))),
            ReadOnly = false, Idempotent = false, Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = RequireRebuild(host);
                var sets = call.Strings("set");
                Replay? replay = null;
                string? path = null;
                if (call.String("replay") is { } which)
                {
                    (replay, path) = await Load(host, which, call.Cancel);
                    CheckContent(host, replay);
                }
                var result = await OnSimulation(host, s =>
                {
                    // Запись прежней симуляции — в артефакты: сеанс агента проверяем человеком целиком.
                    string? previous = null;
                    if (s.Current.Recorder is { } recorder && s.Current.Tick > 0)
                    {
                        previous = host.ArtifactPath(ArtifactName($"session-r{s.Run}-t{s.Current.Tick}"));
                        recorder.ToReplay().Save(previous);
                    }
                    var config = replay?.CreateConfig(sets) ?? s.LoadConfig(sets);
                    var sim = s.Start(config, replay, path);
                    return (previous, sim.ActiveModules, sim.InactiveModules, config.Sources, Warnings: sim.Player?.Warnings ?? [], s.Run,
                        Playback: Playback(sim.Player, sim.Tick));
                }, call.Cancel);
                return new ToolOutput(new JsonObject
                {
                    ["run"] = result.Run,
                    ["tick"] = 0,
                    ["paused"] = true,
                    ["replay"] = path,
                    ["playback"] = result.Playback,
                    ["warnings"] = new JsonArray([.. result.Warnings.Select(w => (JsonNode?)w)]),
                    ["previousReplay"] = result.previous,
                    ["activeModules"] = new JsonArray([.. result.ActiveModules.Select(m => (JsonNode?)m)]),
                    ["inactiveModules"] = new JsonArray([.. result.InactiveModules.Select(m => (JsonNode?)m)]),
                    ["configSources"] = new JsonArray([.. result.Sources.Select(m => (JsonNode?)m)]),
                }) { Tick = 0 };
            },
        });

        host.Add(new AgentTool
        {
            Name = "time.seek", Title = "Перемотать",
            Description = "Перейти к состоянию после tick тиков и встать на паузу. Вперёд — тики подряд (с командами очереди и " +
                "реплея); назад — симуляция собирается заново из конфига сеанса и повторяет таймлайн: команды, сыгранные до " +
                "прежнего тика (в том числе агента), — из записи, дальше — воспроизводимый реплей. Детерминизм даёт то же " +
                "состояние (state.hash). После перемотки назад прежнее будущее повторяется (свои команды его ветвят), а " +
                "команды агента на ещё не сыгранные тики сбрасываются. Таймаут вперёд — сеанс остаётся там, куда дошёл (тик — " +
                "в ошибке); назад — на прежнем тике. Только Headless (в живой игре нет).",
            Arguments = Props(
                ("tick", Int("сыграно тиков — цель", 0, long.MaxValue)),
                ("timeoutSeconds", Num("сколько ждать, с (по умолчанию 60)", 1, 3600))),
            Required = ["tick"],
            ReadOnly = false, Idempotent = true, Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = host.Simulation ?? throw new InvalidOperationException("time.seek: в живой игре нет — Sword.Headless --agent");
                ulong tick = (ulong)call.Integer("tick", 0, 0, long.MaxValue);
                double seconds = call.Number("timeoutSeconds", 60, 1, 3600);
                if (tick > host.Time.Tick + 10_000_000) throw new ArgumentException("tick: не дальше 10 000 000 тиков вперёд");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                host.Time.Pause();
                ulong from = host.Time.Tick, reached = from;
                (bool rebuilt, int Run, JsonObject? Playback) result;
                try
                {
                    result = await OnSimulation(host, s =>
                    {
                        var before = s.Current;
                        from = reached = before.Tick;
                        try
                        {
                            bool rebuilt = s.Seek(tick, timeout.Token);
                            return (rebuilt, s.Run, Playback(s.Current.Player, s.Current.Tick));
                        }
                        finally
                        {
                            // Вперёд симуляция та же и уже сыграла тики (даже если время вышло): время сеанса — по ней.
                            // Пересборку (назад) время узнаёт само (AgentSimulation.Changed → TimeControl.Reset).
                            if (s.Current == before) host.Time.Reset(before.Tick);
                            reached = s.Current.Tick;
                        }
                    }, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !call.Cancel.IsCancellationRequested)
                {
                    throw new TimeoutException(tick >= from
                        ? $"time.seek: за {seconds} с дошли до тика {reached} из {tick}; сеанс на паузе на тике {reached} — продолжить: time.seek ещё раз или больше timeoutSeconds"
                        : $"time.seek: пересборка до тика {tick} не успела за {seconds} с — сеанс остался на тике {reached}; больше timeoutSeconds");
                }
                return new ToolOutput(new JsonObject
                {
                    ["tick"] = tick,
                    ["from"] = from,
                    ["rebuilt"] = result.rebuilt,
                    ["run"] = result.Run,
                    ["paused"] = true,
                    ["playback"] = result.Playback,
                }) { Tick = tick };
            },
        });
    }

    private static AgentSimulation RequireRebuild(AgentHost host) =>
        host.Simulation is { CanRebuild: true } s ? s
            : throw new InvalidOperationException("в этом процессе симуляция не собирается заново (живая игра) — нужен Sword.Headless --agent");

    /// <summary>Операция над симуляцией сеанса: исполнится на потоке симуляции (цикл будится).</summary>
    private static Task<T> OnSimulation<T>(AgentHost host, Func<AgentSimulation, T> op, CancellationToken cancel)
    {
        var session = host.Simulation ?? throw new InvalidOperationException("нет симуляции сеанса (живая игра)");
        var task = session.Post(op, cancel);
        host.Service.Wake();
        return task;
    }

    /// <summary>Реплей по пути, имени в артефактах или «session» (копия записи — в точке обслуживания).</summary>
    private static async Task<(Replay Replay, string Path)> Load(AgentHost host, string which, CancellationToken cancel)
    {
        if (which == SessionWord)
        {
            var r = await host.Serve(ctx => ctx.Recorder?.ToReplay(), cancel).ConfigureAwait(false);
            return (r ?? throw new InvalidOperationException("симуляция не пишет реплей"), SessionWord);
        }
        string path = Path.IsPathRooted(which) ? which
            : File.Exists(Path.Combine(host.Artifacts, which)) ? Path.Combine(host.Artifacts, which) : Path.GetFullPath(which);
        if (!File.Exists(path)) throw new ArgumentException($"нет файла реплея {path}");
        return (Replay.Load(path), path);
    }

    private static void CheckContent(AgentHost host, Replay replay)
    {
        if (replay.Content != "none" && host.ContentHash != "none" && replay.Content != host.ContentHash)
            throw new InvalidDataException($"реплей записан на контенте {replay.Content}, а здесь {host.ContentHash}: хеши сравнивать бессмысленно");
    }

    /// <summary>Имя файла в каталоге артефактов — без каталогов, с расширением реплея.</summary>
    private static string ArtifactName(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"name: «{name}» — только имя файла, без каталогов");
        return name.EndsWith(Replay.Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Replay.Extension;
    }

    private static JsonObject Summary(Replay replay, string path) => new()
    {
        ["file"] = path,
        ["ticks"] = replay.Ticks,
        ["hashes"] = replay.Hashes.Count,
        ["hashEvery"] = replay.HashEvery,
        ["runs"] = replay.Runs.Count,
        ["commands"] = replay.CommandCount,
        ["commandTypes"] = new JsonArray([.. replay.CommandTypes.Select(t => (JsonNode?)t.Name)]),
        ["parts"] = new JsonArray([.. replay.Parts.Select(p => (JsonNode?)p)]),
    };

    private static JsonObject? Recording(ReplayRecorder? recorder) => recorder is null ? null : new JsonObject
    {
        ["ticks"] = recorder.Ticks,
        ["hashes"] = recorder.HashCount,
        ["hashEvery"] = recorder.HashEvery,
        ["runs"] = recorder.RunCount,
    };

    private static JsonObject? Playback(ReplayPlayer? player, ulong tick) => player is null ? null : new JsonObject
    {
        ["length"] = player.Length,
        ["tick"] = tick,
        ["finished"] = tick >= player.Length,
        ["match"] = player.Divergence is null,
        ["hashesChecked"] = player.HashesChecked,
        ["hashesLeft"] = player.HashesLeft,
        ["lastMatch"] = player.LastMatch,
        ["divergence"] = player.Divergence is { } d ? new JsonObject
        {
            ["tick"] = d.Tick,
            ["lastMatch"] = d.LastMatch,
            ["parts"] = new JsonArray([.. d.Parts.Select(p => (JsonNode?)new JsonObject
            {
                ["part"] = p.Part, ["expected"] = $"{p.Expected:X16}", ["actual"] = $"{p.Actual:X16}",
            })]),
            ["hint"] = "расхождение возникло в тиках lastMatch … tick−1; что отличается — state.describe участника",
        } : null,
        ["warnings"] = new JsonArray([.. player.Warnings.Take(20).Select(w => (JsonNode?)w)]),
    };

    private static JsonObject Props(params (string Name, JsonObject Schema)[] props) => CoreTools.Props(props);
    private static JsonObject Str(string d) => CoreTools.Str(d);
    private static JsonObject StrArray(string d) => CoreTools.StrArray(d);
    private static JsonObject Int(string d, long min, long max) => CoreTools.Int(d, min, max);
    private static JsonObject Num(string d, double min, double max) => CoreTools.Num(d, min, max);
}
