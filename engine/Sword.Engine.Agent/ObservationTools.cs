using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Наблюдение (этап 3в, раздел 8 плана): условие остановки (<c>time.runUntil {condition}</c>, <c>time.break</c> — посреди
/// тика), наблюдения (<c>watch.*</c>) и история (<c>entity.history</c>, <c>events.history</c>), снимки и их сравнение
/// (<c>world.snapshot</c>, <c>world.diff</c>). Условия — один язык (<see cref="ObservationPredicate"/>); компилируются в точке
/// обслуживания, живут на потоке симуляции (<see cref="AgentHost.Observation"/>).
/// </summary>
internal static class ObservationTools
{
    private const int MaxTake = 500;

    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "time.break", Title = "Останов посреди тика",
            Description = "Играть, пока после барьера стадии stage не выполнится condition (без condition — на ближайшем тике), и " +
                "встать посреди тика: квитанция — tickId (номер идущего тика) и stage; world.query, entity.get, state.*, " +
                "world.snapshot видят мир после этой стадии, command.send — на следующий тик. Дальше: time.step 1 доигрывает тик, " +
                "time.break — до следующего попадания. " + ObservationPredicate.Syntax + " Только Headless.",
            Arguments = CoreTools.Props(
                ("stage", CoreTools.Str("стадия (ecs.plan): после её барьера проверяется условие")),
                ("condition", Condition("условие; без него — останов на ближайшем тике")),
                ("maxTicks", CoreTools.Int("не больше стольких тиков (по умолчанию 100 000)", 1, 10_000_000)),
                ("timeoutSeconds", CoreTools.Num("сколько ждать, с (по умолчанию 60)", 0.1, 3600))),
            Required = ["stage"],
            ReadOnly = false, Idempotent = false, Cost = ToolCost.Heavy,
            Run = call => RunUntil(host, call, call.RequiredString("stage"), call.Node("condition") ?? Always),
        });

        host.Add(new AgentTool
        {
            Name = "watch.add", Title = "Наблюдать за полями",
            Description = "Наблюдение: после каждого every-го тика — строка на каждую подходящую сущность (entities — заданные, " +
                "иначе все, для которых выполняется where) со значениями полей fields. Строки — в кольце на capacity строк; " +
                "читает entity.history. Наблюдение переживает session.start и перемотку (привязывается заново). " + ObservationPredicate.Syntax,
            Arguments = CoreTools.Props(
                ("id", CoreTools.Str("имя наблюдения (по умолчанию w1, w2…)")),
                ("entities", CoreTools.StrArray("сущности: номера «12v3» или имена (player)")),
                ("where", Condition("условие на сущность (field, component, entity…)")),
                ("fields", CoreTools.StrArray("поля: «Transform.Position», «Transform» (все листья), «RootVelocity.Linear.X»")),
                ("every", CoreTools.Int("каждые столько тиков (по умолчанию 1)", 1, 1_000_000)),
                ("capacity", CoreTools.Int($"строк в кольце (по умолчанию {Watch.DefaultCapacity})", 1, Watch.MaxCapacity))),
            Required = ["fields"],
            ReadOnly = false, Idempotent = false,
            Run = async call =>
            {
                var definition = new JsonObject();
                foreach (var key in new[] { "id", "entities", "where", "fields", "every", "capacity" })
                    if (call.Node(key) is { } value) definition[key] = value.DeepClone();
                if (call.Node("entities") is { } e && e is not JsonArray) definition["entities"] = new JsonArray(e.DeepClone());
                if (call.Node("fields") is JsonValue f) definition["fields"] = new JsonArray(f.DeepClone());
                var r = await host.Serve(ctx =>
                {
                    var w = host.Observation.AddWatch(ctx, definition);
                    return (Json: Describe(w, ctx.World), ctx.Point);
                }, call.Cancel);
                Log.Info("agent", $"watch.add {r.Json["id"]}: {definition.ToJsonString()}");
                return new ToolOutput(r.Json) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "watch.list", Title = "Наблюдения",
            Description = "Наблюдения: поля, сущности, условие, строк в кольце и окно тиков.",
            Run = async call =>
            {
                var r = await host.Serve(ctx => (Json: new JsonArray([.. host.Observation.Watches.Select(w => (JsonNode?)Describe(w, ctx.World))]), ctx.Point), call.Cancel);
                return new ToolOutput(new JsonObject { ["watches"] = r.Json, ["max"] = ObservationHub.MaxWatches }) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "watch.remove", Title = "Снять наблюдение",
            Description = "Снять наблюдение по id (all — все); его строки пропадают.",
            Arguments = CoreTools.Props(("id", CoreTools.Str("id наблюдения или all"))),
            Required = ["id"],
            ReadOnly = false,
            Run = async call =>
            {
                string id = call.RequiredString("id");
                var r = await host.Serve(ctx =>
                {
                    if (id == "all")
                    {
                        int n = host.Observation.Watches.Count;
                        host.Observation.ClearWatches();
                        return (Removed: n, ctx.Point);
                    }
                    return host.Observation.RemoveWatch(id) ? (Removed: 1, ctx.Point)
                        : throw new ArgumentException($"нет наблюдения {id}; есть: {string.Join(", ", host.Observation.Watches.Select(w => w.Id))}");
                }, call.Cancel);
                return new ToolOutput(new JsonObject { ["removed"] = r.Removed }) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "entity.history", Title = "История сущности",
            Description = "Значения полей сущности по тикам (tickId — сыграно тиков) из наблюдений watch.add, которые её " +
                "покрывают; fields — только поля, содержащие эти строки; changes — только строки, где что-то изменилось. " +
                "Без наблюдения истории нет — сначала watch.add.",
            Arguments = CoreTools.Props(
                ("entity", CoreTools.Str("номер «12v3» или имя")),
                ("fields", CoreTools.StrArray("только поля, в пути которых есть эта строка")),
                ("from", CoreTools.Int("с tickId", 0, long.MaxValue)),
                ("to", CoreTools.Int("по tickId", 0, long.MaxValue)),
                ("changes", new JsonObject { ["type"] = "boolean", ["description"] = "только строки с изменениями (первая — всегда)" }),
                ("skip", CoreTools.Int("пропустить строк", 0, int.MaxValue)),
                ("take", CoreTools.Int($"строк (по умолчанию 100, не больше {MaxTake})", 1, MaxTake))),
            Required = ["entity"],
            Run = async call =>
            {
                string which = call.RequiredString("entity");
                var filters = call.Strings("fields");
                ulong from = (ulong)call.Integer("from", 0, 0, long.MaxValue), to = (ulong)call.Integer("to", long.MaxValue, 0, long.MaxValue);
                bool changes = call.Bool("changes", false);
                int skip = (int)call.Integer("skip", 0, 0, int.MaxValue), take = (int)call.Integer("take", 100, 1, MaxTake);
                var r = await host.Serve(ctx =>
                {
                    var entity = ResolveEntity(ctx, which, host.Names);
                    var rows = new SortedDictionary<ulong, SortedDictionary<string, (FieldLayout.Kind Kind, ulong? Bits)>>();
                    var watches = new List<string>();
                    foreach (var w in host.Observation.Watches)
                    {
                        if (!w.Covers(entity)) continue;
                        watches.Add(w.Id);
                        foreach (var (tickId, _, values) in w.Read(entity, from, to))
                        {
                            if (!rows.TryGetValue(tickId, out var row)) rows[tickId] = row = new(StringComparer.Ordinal);
                            for (int f = 0; f < w.Fields.Count; f++)
                                if (filters.Count == 0 || filters.Any(s => w.Fields[f].Path.Contains(s, StringComparison.OrdinalIgnoreCase)))
                                    row[w.Fields[f].Path] = (w.Fields[f].Kind, values[f]);
                        }
                    }
                    if (watches.Count == 0)
                        throw new ArgumentException($"сущность {entity} не наблюдается — watch.add {{entities:[\"{which}\"], fields:[…]}}; " +
                            $"наблюдения: {string.Join(", ", host.Observation.Watches.Select(x => x.Id))}");
                    return (Entity: entity, Rows: rows, Watches: watches, ctx.Point);
                }, call.Cancel);

                var list = new List<JsonObject>();
                SortedDictionary<string, (FieldLayout.Kind Kind, ulong? Bits)>? previous = null;
                foreach (var (tickId, row) in r.Rows)
                {
                    if (changes && previous is not null && row.Count == previous.Count && row.All(p => previous.TryGetValue(p.Key, out var q) && q.Bits == p.Value.Bits))
                        continue;
                    previous = row;
                    var values = new JsonObject();
                    foreach (var (path, (kind, bits)) in row) values[path] = bits is { } b ? ObservationPredicate.LeafJson(kind, b) : null;
                    list.Add(new JsonObject { ["tickId"] = tickId, ["values"] = values });
                }
                bool truncated = skip + take < list.Count;
                return new ToolOutput(new JsonObject
                {
                    ["entity"] = r.Entity.ToString(),
                    ["watches"] = new JsonArray([.. r.Watches.Select(w => (JsonNode?)w)]),
                    ["total"] = list.Count,
                    ["rows"] = new JsonArray([.. list.Skip(skip).Take(take)]),
                    ["note"] = "null — у сущности в этом тике не было компонента поля",
                }) { Point = r.Point, Truncated = truncated, Continuation = truncated ? $"skip={skip + take}" : null };
            },
        });

        host.Add(new AgentTool
        {
            Name = "events.history", Title = "История событий",
            Description = "События, команды и срабатывания (Added/Removed/Changed) последних тиков из истории наблюдений: tick — " +
                "номер тика, в котором запись сделана (как у command.send), stage — стадия, на барьере которой; поля записи. " +
                "types — типы (PlayerCommand, Added<Fighter>; без них — все), entity — только записи про эту сущность, where — " +
                "условие на поля записи (с types). Видны журналы, у которых в мире есть потребитель. Нарушения инвариантов — тип " +
                "InvariantViolation (поля id, entity, details). Посреди тика (time.break) — и записи идущего тика до этой стадии " +
                "(window — только сыгранные тики).",
            Arguments = CoreTools.Props(
                ("types", CoreTools.StrArray("типы: PlayerCommand, Added<Fighter>, InvariantViolation… (world.types; без них — все)")),
                ("entity", CoreTools.Str("только записи с этой сущностью в полях (номер или имя)")),
                ("where", Condition("условие на поля записи ({field:\"Walk\", equals:true}); только вместе с types")),
                ("from", CoreTools.Int("с тика", 0, long.MaxValue)),
                ("to", CoreTools.Int("по тик", 0, long.MaxValue)),
                ("skip", CoreTools.Int("пропустить записей", 0, int.MaxValue)),
                ("take", CoreTools.Int($"записей (по умолчанию 50, не больше {MaxTake})", 1, MaxTake))),
            Run = async call =>
            {
                var types = call.Strings("types");
                string? entityName = call.String("entity");
                var where = call.Node("where");
                if (where is not null && types.Count == 0) throw new ArgumentException("where — только вместе с types (поля у типов разные)");
                ulong from = (ulong)call.Integer("from", 0, 0, long.MaxValue), to = (ulong)call.Integer("to", long.MaxValue, 0, long.MaxValue);
                int skip = (int)call.Integer("skip", 0, 0, int.MaxValue), take = (int)call.Integer("take", 50, 1, MaxTake);
                var r = await host.Serve(ctx =>
                {
                    var history = host.Observation.History;
                    var journals = history.Journals;
                    var selected = new Dictionary<int, (ObservationPredicate? Where, ObservationPredicate? Entity)>();
                    bool IsViolation(string t) => string.Equals(t, InvariantRecord.Type, StringComparison.OrdinalIgnoreCase);
                    bool violations = types.Count == 0 || types.Any(IsViolation);
                    if (violations && where is not null) throw new ArgumentException($"where — поля записей журналов; у {InvariantRecord.Type} — только entity");
                    Entity? violationEntity = violations && entityName is not null ? ResolveEntity(ctx, entityName, host.Names) : null;
                    IEnumerable<JournalInfo> chosen = types.Count == 0 ? journals
                        : types.Where(t => !IsViolation(t)).Select(t => ObservationPredicate.ResolveJournal(ctx.World, t, "types"));
                    foreach (var j in chosen)
                    {
                        ObservationPredicate? byEntity = null;
                        if (entityName is not null)
                        {
                            if (j.EntrySize == 0 || !FieldLayout.Of(j.EntryType).Leaves.Any(l => l.Kind == FieldLayout.Kind.Entity))
                            {
                                if (types.Count > 0) throw new ArgumentException($"entity: в записи {Catalog.ShortName(j.EntryType)} нет полей-сущностей");
                                continue;
                            }
                            byEntity = ObservationPredicate.Compile(new JsonObject { ["entity"] = entityName }, ctx, host.Catalog, host.Names, PredicateScope.Record, j);
                        }
                        var filter = where is null ? null : ObservationPredicate.Compile(where, ctx, host.Catalog, host.Names, PredicateScope.Record, j);
                        selected[j.Id] = (filter, byEntity);
                    }
                    var rows = new List<(ulong Tick, int Stage, int Journal, byte[] Bytes, InvariantViolation? Violation)>();
                    int total = 0;
                    void Consider(in ServiceContext ctx, ulong t, int journal, int stage, ReadOnlySpan<byte> bytes)
                    {
                        if (!selected.TryGetValue(journal, out var f)) return;
                        if (f.Entity is not null && !f.Entity.MatchesRecord(ctx, bytes)) return;
                        if (f.Where is not null && !f.Where.MatchesRecord(ctx, bytes, history)) return;
                        if (total++ < skip || rows.Count >= take) return;
                        rows.Add((t, stage, journal, bytes.ToArray(), null));
                    }
                    void ConsiderViolation(in InvariantViolation v)
                    {
                        if (!violations || violationEntity is { } only && v.Entity != only) return;
                        if (total++ < skip || rows.Count >= take) return;
                        rows.Add((v.Tick, -1, -1, [], v));
                    }
                    if (history.Oldest is { } oldest && history.Newest is { } newest)
                        for (ulong t = Math.Max(oldest, from); t <= Math.Min(newest, to); t++)
                        {
                            for (int i = 0, n = history.CountAt(t); i < n; i++)
                            {
                                var (journal, stage) = history.RecordAt(t, i, out var bytes);
                                Consider(ctx, t, journal, stage, bytes);
                            }
                            foreach (var v in history.ViolationsAt(t)) ConsiderViolation(v);
                        }
                    // Посреди тика (time.break) — и записи идущего тика до этой стадии: в историю они попадут после тика,
                    // а в журналах мира — с возрастом 1…число пройденных стадий (как их видит условие {event}).
                    int done = ctx.Stage is { } at ? ctx.World.Stages.ToList().IndexOf(at) + 1 : 0;
                    if (done > 0 && ctx.Tick >= from && ctx.Tick <= to)
                        foreach (var j in journals)
                            for (int i = 0, n = ctx.World.JournalLength(j.Id); i < n; i++)
                            {
                                var bytes = ctx.World.JournalEntry(j.Id, i, out long age);
                                if (age >= 1 && age <= done) Consider(ctx, ctx.Tick, j.Id, done - (int)age, bytes);
                            }
                    if (done > 0 && ctx.Tick >= from && ctx.Tick <= to)
                        foreach (var v in history.Pending) ConsiderViolation(v);
                    return (Rows: rows, Total: total, history.Oldest, history.Newest, history.Capacity, Journals: journals, Stages: history.Stages, ctx.Point);
                }, call.Cancel);

                var list = new JsonArray();
                foreach (var (tick, stage, journal, bytes, violation) in r.Rows)
                {
                    if (violation is { } v)
                    {
                        list.Add(new JsonObject { ["tick"] = tick, ["stage"] = v.Stage, ["type"] = InvariantRecord.Type, ["fields"] = InvariantRecord.Fields(v) });
                        continue;
                    }
                    var info = r.Journals[journal];
                    list.Add(new JsonObject
                    {
                        ["tick"] = tick,
                        ["stage"] = r.Stages[stage],
                        ["type"] = Catalog.ShortName(info.EntryType),
                        ["fields"] = info.EntrySize > 0 ? FieldCodec.Decode(info.EntryType, bytes) : null,
                    });
                }
                bool truncated = skip + r.Rows.Count < r.Total;
                return new ToolOutput(new JsonObject
                {
                    ["window"] = new JsonObject { ["oldest"] = r.Oldest, ["newest"] = r.Newest, ["capacity"] = r.Capacity },
                    ["total"] = r.Total,
                    ["events"] = list,
                }) { Point = r.Point, Truncated = truncated, Continuation = truncated ? $"skip={skip + r.Rows.Count}" : null };
            },
        });

        host.Add(new AgentTool
        {
            Name = "world.snapshot", Title = "Снимок мира",
            Description = "Наблюдательный снимок: все сущности с компонентами по полям, ресурсы и описания участников хеша " +
                "(animation…) — хранится под id (и файлом в артефактах); сравнивает world.diff. Посреди тика (time.break) — " +
                "мир после стадии.",
            Arguments = CoreTools.Props(("id", CoreTools.Str("имя снимка (по умолчанию s1, s2…)"))),
            ReadOnly = false, Cost = ToolCost.Moderate,
            Run = async call =>
            {
                string? name = call.String("id");
                if (name is { } n && (n == "now" || n.Length > 40 || !n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    throw new ArgumentException("id: буквы, цифры, «-», «_», до 40 знаков, не «now»");
                string id = name ?? host.NextSnapshotId();
                var snapshot = await host.Serve(ctx => WorldSnapshot.Take(ctx, id), call.Cancel);
                var flat = snapshot.Flatten(host.Catalog);
                host.PutSnapshot(snapshot);
                string file = $"snapshot-{id}-t{snapshot.Point.Tick}{(snapshot.Point.Stage is { } s ? "-" + s : "")}.json";
                var json = new JsonObject();
                foreach (var (key, value) in flat) json[key] = value?.DeepClone();
                File.WriteAllText(host.ArtifactPath(file), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new ToolOutput(new JsonObject
                {
                    ["id"] = id, ["tick"] = snapshot.Point.Tick, ["stage"] = snapshot.Point.Stage, ["entities"] = snapshot.Entities,
                    ["keys"] = flat.Count, ["file"] = host.ArtifactPath(file),
                }) { Point = snapshot.Point, ArtifactId = file };
            },
        });

        host.Add(new AgentTool
        {
            Name = "world.diff", Title = "Разница снимков",
            Description = "Сравнить два снимка по полям (a, b — id снимков; «now» — снять сейчас): что изменилось (было → стало, " +
                "у чисел — разница), что появилось и пропало, какие сущности появились и пропали. filter — только ключи с этой " +
                "строкой («1v1/Transform», «animation»), tolerance — числа ближе этого равны.",
            Arguments = CoreTools.Props(
                ("a", CoreTools.Str("снимок «до» (id или now)")),
                ("b", CoreTools.Str("снимок «после» (id или now)")),
                ("filter", CoreTools.Str("только ключи, содержащие строку")),
                ("tolerance", CoreTools.Num("допуск для чисел (по умолчанию 0 — точно)", 0, 1e9)),
                ("skip", CoreTools.Int("пропустить строк", 0, int.MaxValue)),
                ("take", CoreTools.Int($"строк (по умолчанию 100, не больше {MaxTake})", 1, MaxTake))),
            Required = ["a", "b"],
            Cost = ToolCost.Moderate,
            Run = async call =>
            {
                string a = call.RequiredString("a"), b = call.RequiredString("b");
                string? filter = call.String("filter");
                double tolerance = call.Number("tolerance", 0, 0, 1e9);
                int skip = (int)call.Integer("skip", 0, 0, int.MaxValue), take = (int)call.Integer("take", 100, 1, MaxTake);
                async Task<WorldSnapshot> Get(string id) =>
                    id == "now" ? await host.Serve(ctx => WorldSnapshot.Take(ctx, "now"), call.Cancel) : host.GetSnapshot(id);
                var sa = await Get(a);
                var sb = await Get(b);
                var d = SnapshotDiff.Compare(sa.Flatten(host.Catalog), sb.Flatten(host.Catalog), filter, tolerance);
                bool truncated = skip + take < d.Rows.Count;
                return new ToolOutput(new JsonObject
                {
                    ["a"] = new JsonObject { ["id"] = sa.Id, ["tick"] = sa.Point.Tick, ["stage"] = sa.Point.Stage },
                    ["b"] = new JsonObject { ["id"] = sb.Id, ["tick"] = sb.Point.Tick, ["stage"] = sb.Point.Stage },
                    ["compared"] = d.Compared, ["changed"] = d.Changed, ["added"] = d.Added, ["removed"] = d.Removed,
                    ["entitiesAdded"] = new JsonArray([.. d.EntitiesAdded.Select(e => (JsonNode?)e)]),
                    ["entitiesRemoved"] = new JsonArray([.. d.EntitiesRemoved.Select(e => (JsonNode?)e)]),
                    ["rows"] = new JsonArray([.. d.Rows.Skip(skip).Take(take).Select(r => (JsonNode?)new JsonObject
                    {
                        ["key"] = r.Key, ["a"] = r.A?.DeepClone(), ["b"] = r.B?.DeepClone(), ["delta"] = r.Delta,
                    })]),
                }) { Truncated = truncated, Continuation = truncated ? $"skip={skip + take}" : null, Tick = sb.Point.Tick, Stage = sb.Point.Stage };
            },
        });
    }

    /// <summary>Условие по умолчанию — всегда (останов на ближайшем тике).</summary>
    private static readonly JsonObject Always = new() { ["sim"] = "ticks", ["greaterOrEqual"] = 0 };

    private static JsonObject Condition(string description) =>
        new() { ["type"] = "object", ["description"] = description + " — " + ObservationPredicate.Syntax };

    /// <summary>
    /// Играть до условия (<c>time.runUntil {condition}</c>, <c>time.break</c>): условие ставится в точке обслуживания;
    /// на паузе время заказывается до предела тиков; выполнилось — время встаёт на его тике (посреди тика — на стадии).
    /// Таймаут — условие снимается, симуляция встаёт на паузу.
    /// </summary>
    internal static Task<ToolOutput> RunUntil(AgentHost host, ToolCall call, string? stage, JsonNode condition)
    {
        if (stage is not null && host.Simulation is null) throw new InvalidOperationException("time.break — только Sword.Headless --agent (в живой игре тик посреди кадра не останавливается)");
        return Await(host, call, stage is null ? Math.Min(30, host.MaxWaitSeconds) : 60, (ctx, limit) =>
        {
            var predicate = ObservationPredicate.Compile(condition, ctx, host.Catalog, host.Names);
            return (host.Observation.Arm(ctx, predicate, stage, limit), predicate.Cost(ctx.World));
        }, stage is null ? "условие проверяется после каждого тика; tick — сыграно тиков, когда оно выполнилось"
            : "стоим посреди тика после стадии: time.step 1 — доиграть тик, time.break — следующее попадание");
    }

    /// <summary>
    /// Поставить условие остановки (<paramref name="arm"/> — в точке обслуживания, с пределом тика) и ждать: на паузе время
    /// заказывается до предела; выполнилось — время встаёт на его тике (посреди тика — на стадии). Таймаут — условие
    /// снимается, симуляция встаёт на паузу. Общая часть <c>time.runUntil {condition}</c>, <c>time.break</c>,
    /// <c>time.breakOnInvariant</c>.
    /// </summary>
    internal static async Task<ToolOutput> Await(AgentHost host, ToolCall call, double defaultSeconds,
        Func<ServiceContext, ulong, (Task<ConditionHit> Task, string? Cost)> arm, string hint)
    {
        ulong maxTicks = (ulong)call.Integer("maxTicks", 100_000, 1, 10_000_000);
        double seconds = call.Number("timeoutSeconds", defaultSeconds, 0.1, Math.Max(host.MaxWaitSeconds, 0.1));
        var armed = await host.Serve(ctx =>
        {
            ulong limit = ctx.Tick + maxTicks;
            var (task, cost) = arm(ctx, limit);
            return (Task: task, Limit: limit, Cost: cost, From: ctx.Point);
        }, call.Cancel);
        if (host.Time.Paused) host.Time.RunUntil(armed.Limit);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        ConditionHit hit;
        try
        {
            hit = await armed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            host.Observation.Disarm(armed.Task, "таймаут ожидания");
            host.Time.Pause();
            if (call.Cancel.IsCancellationRequested) throw;
            throw new TimeoutException($"условие не выполнилось за {seconds:0.#} с (сыграно {host.Time.Tick}); условие снято, симуляция на паузе");
        }
        // Квитанция — из точки обслуживания после остановки (посреди тика — со стадией).
        var point = await host.Serve(ctx => ctx.Point, call.Cancel).ConfigureAwait(false);
        return new ToolOutput(new JsonObject
        {
            ["met"] = hit.Met,
            ["tick"] = hit.Tick,
            ["stage"] = hit.Stage,
            ["reason"] = hit.Reason,
            ["from"] = armed.From.Tick,
            ["paused"] = host.Time.Paused,
            ["cost"] = armed.Cost,
            ["violations"] = hit.Violations is { } list ? new JsonArray([.. list.Select(v => (JsonNode?)ViolationJson(v))]) : null,
            ["hint"] = hit.Met ? hint : null,
        }) { Point = point };
    }

    /// <summary>Нарушение инварианта для ответа: id, тик, стадия, сущность (с именем игры), подробности.</summary>
    internal static JsonObject ViolationJson(in InvariantViolation v)
    {
        var o = InvariantRecord.Fields(v);
        o["tick"] = v.Tick;
        o["stage"] = v.Stage;
        return o;
    }

    private static JsonObject Describe(Watch w, WorldReader world) => new()
    {
        ["id"] = w.Id,
        ["fields"] = new JsonArray([.. w.Fields.Select(f => (JsonNode?)f.Path)]),
        ["entities"] = w.Entities is null ? null : new JsonArray([.. w.Entities.Select(e => (JsonNode?)e.ToString())]),
        ["where"] = w.Where?.Source.DeepClone(),
        ["cost"] = w.Where?.Cost(world),
        ["every"] = w.Every,
        ["capacity"] = w.Capacity,
        ["rows"] = w.Rows,
        ["oldestTickId"] = w.OldestTick,
        ["newestTickId"] = w.NewestTick,
    };

    internal static Entity ResolveEntity(in ServiceContext ctx, string which, IEntityNames? names)
    {
        if (which.Length > 0 && char.IsDigit(which[0])) return FieldCodec.ParseEntity(which);
        return names?.Resolve(ctx, which) ?? throw new ArgumentException(
            $"нет сущности с именем «{which}»; имена: {string.Join(", ", names?.Known ?? [])} (или номер «12v3»)");
    }
}
