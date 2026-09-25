using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Инварианты (раздел 8 плана): <c>invariant.list</c>, <c>invariant.enable</c>, <c>invariant.run</c>,
/// <c>invariant.define</c> и <c>time.breakOnInvariant</c>. Реестр и проверка — в Runtime (<see cref="SimulationInvariants"/>),
/// инварианты агента и остановка — в узле наблюдения (<see cref="AgentHost.Observation"/>); нарушения тиков — в истории
/// (<c>events.history</c>, тип <see cref="InvariantRecord.Type"/>).
/// </summary>
internal static class InvariantTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "invariant.list", Title = "Инварианты",
            Description = "Инварианты симуляции: id, чей (engine, core, модуль, agent), что касается, когда проверяется (after — " +
                "стадия, runtime.export — конец тика; cost — каждый тик, раз в N тиков, по запросу; every — как часто проверяется сейчас: " +
                "период кодового инварианта действует только в живой игре — livePeriods, Sword.exe: тяжёлые раз в 4 тика), включён ли, " +
                "проверок, нарушений, " +
                "средняя стоимость проверки (мкс), последнее нарушение; recent — последние нарушения. filter — часть id.",
            Arguments = CoreTools.Props(
                ("filter", CoreTools.Str("только id, содержащие строку")),
                ("recent", CoreTools.Int("последних нарушений в ответе (по умолчанию 10)", 0, SimulationInvariants.RecentCapacity))),
            Run = async call =>
            {
                string? filter = call.String("filter");
                int recent = (int)call.Integer("recent", 10, 0, SimulationInvariants.RecentCapacity);
                var r = await host.Serve(ctx =>
                {
                    var invariants = ctx.Invariants;
                    var defined = host.Observation.DefinedInvariants;
                    var list = invariants.List().Where(i => filter is null || i.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(i => Describe(i, defined.FirstOrDefault(d => (string?)d["id"] == i.Id))).ToList();
                    var last = invariants.Recent();
                    return (List: list, Recent: last.Skip(Math.Max(0, last.Count - recent)).ToList(), invariants.Total,
                        invariants.EnabledByConfig, invariants.LivePeriods, ctx.Point);
                }, call.Cancel);
                return new ToolOutput(new JsonObject
                {
                    ["enabledByConfig"] = r.EnabledByConfig,
                    ["livePeriods"] = r.LivePeriods,
                    ["violationsTotal"] = r.Total,
                    ["invariants"] = new JsonArray([.. r.List.Select(i => (JsonNode?)i)]),
                    ["recent"] = new JsonArray([.. r.Recent.Select(v => (JsonNode?)ObservationTools.ViolationJson(v))]),
                }) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "invariant.enable", Title = "Включить инварианты",
            Description = "Включить (enabled true) или выключить инварианты: ids — id, «animation.*» (все с этим началом) или «all». " +
                "Включённый «по запросу» проверяется каждый тик. Включение переживает session.start и перемотку.",
            Arguments = CoreTools.Props(
                ("ids", CoreTools.StrArray("id, «префикс.*» или all")),
                ("enabled", new JsonObject { ["type"] = "boolean", ["description"] = "включить (true) или выключить (false)" })),
            Required = ["ids", "enabled"],
            ReadOnly = false, Idempotent = true,
            Run = async call =>
            {
                var ids = call.Strings("ids");
                if (ids.Count == 0) throw new ArgumentException("ids: хотя бы один id, «префикс.*» или all");
                bool enabled = call.Bool("enabled", true);
                var r = await host.Serve(ctx =>
                {
                    var all = ctx.Invariants.List().Select(i => i.Id).ToList();
                    var chosen = new List<string>();
                    foreach (string pattern in ids)
                    {
                        var match = pattern == "all" ? all
                            : pattern.EndsWith(".*", StringComparison.Ordinal) ? all.Where(id => id.StartsWith(pattern[..^1], StringComparison.Ordinal)).ToList()
                            : all.Contains(pattern) ? [pattern] : [];
                        if (match.Count == 0) throw new ArgumentException($"«{pattern}» — нет такого инварианта; есть: {string.Join(", ", all)}");
                        chosen.AddRange(match.Where(id => !chosen.Contains(id)));
                    }
                    foreach (string id in chosen) host.Observation.SetInvariantEnabled(ctx, id, enabled);
                    return (Changed: chosen, ctx.Point);
                }, call.Cancel);
                Log.Info("agent", $"invariant.enable {enabled}: {string.Join(", ", r.Changed)}");
                return new ToolOutput(new JsonObject
                {
                    ["enabled"] = enabled, ["ids"] = new JsonArray([.. r.Changed.Select(id => (JsonNode?)id)]),
                }) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "invariant.run", Title = "Проверить инварианты сейчас",
            Description = "Проверить инварианты сейчас, на мире как он есть (между тиками или посреди тика после time.break): ids " +
                "(без них — все, в том числе выключенные и «по запросу»). Нарушения только в ответе — в историю идут нарушения тиков.",
            Arguments = CoreTools.Props(("ids", CoreTools.StrArray("id инвариантов (без них — все)"))),
            Cost = ToolCost.Moderate,
            Run = async call =>
            {
                var ids = call.Strings("ids");
                var r = await host.Serve(ctx => (Results: ctx.Invariants.Run(ids.Count == 0 ? null : ids, ctx.Tick, ctx.Stage ?? "между тиками", ctx.Dt),
                    ctx.Point), call.Cancel);
                int broken = r.Results.Count(x => x.Violations > 0);
                return new ToolOutput(new JsonObject
                {
                    ["checked"] = r.Results.Count,
                    ["violated"] = broken,
                    ["results"] = new JsonArray([.. r.Results.Select(x => (JsonNode?)new JsonObject
                    {
                        ["id"] = x.Id, ["violations"] = x.Violations,
                        ["details"] = new JsonArray([.. x.Details.Select(v => (JsonNode?)ObservationTools.ViolationJson(v))]),
                    })]),
                }) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "invariant.define", Title = "Свой инвариант",
            Description = "Инвариант агента из условия: condition — условие мира, которое должно держаться; или must — условие на " +
                "сущность, которое должно держаться у каждой сущности под where (без where — у всех с компонентами из must). " +
                "after — стадия, после барьера которой проверять (по умолчанию runtime.export — конец тика), every — раз в столько " +
                "тиков. Тот же id агента заменяется; remove true — убрать. Переживает session.start и перемотку. " + ObservationPredicate.Syntax,
            Arguments = CoreTools.Props(
                ("id", CoreTools.Str("id: части из латиницы, цифр и _ через точку (agent.playerOnArena)")),
                ("condition", new JsonObject { ["type"] = "object", ["description"] = "условие мира (язык — в описании)" }),
                ("must", new JsonObject { ["type"] = "object", ["description"] = "условие на каждую сущность" }),
                ("where", new JsonObject { ["type"] = "object", ["description"] = "какие сущности проверять (с must)" }),
                ("after", CoreTools.Str("стадия (ecs.plan) или runtime.export — конец тика")),
                ("every", CoreTools.Int("раз в столько тиков (по умолчанию 1)", 1, 1_000_000)),
                ("remove", new JsonObject { ["type"] = "boolean", ["description"] = "убрать инвариант агента с этим id" })),
            Required = ["id"],
            ReadOnly = false, Idempotent = true,
            Run = async call =>
            {
                string id = call.RequiredString("id");
                if (call.Bool("remove", false))
                {
                    var removed = await host.Serve(ctx => (Removed: host.Observation.RemoveInvariant(ctx, id), ctx.Point), call.Cancel);
                    if (!removed.Removed) throw new ArgumentException($"нет инварианта агента «{id}»");
                    return new ToolOutput(new JsonObject { ["id"] = id, ["removed"] = true }) { Point = removed.Point };
                }
                var definition = new JsonObject();
                foreach (var key in new[] { "id", "condition", "must", "where", "after", "every" })
                    if (call.Node(key) is { } value) definition[key] = value.DeepClone();
                var r = await host.Serve(ctx =>
                {
                    var invariant = host.Observation.DefineInvariant(ctx, definition);
                    return (Info: ctx.Invariants.Find(invariant.Id)!, Cost: invariant.CostText(ctx.World), ctx.Point);
                }, call.Cancel);
                Log.Info("agent", $"invariant.define {definition.ToJsonString()}");
                var data = Describe(r.Info, definition);
                data["conditionCost"] = r.Cost;
                return new ToolOutput(data) { Point = r.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "time.breakOnInvariant", Title = "Играть до нарушения инварианта",
            Description = "Играть, пока не нарушится инвариант (ids — какие; без них — любой включённый), и встать: Headless — посреди " +
                "тика после нарушившего барьера (квитанция со stage; world.*, entity.get, invariant.run видят мир там), живая игра — " +
                "после тика. Ответ — met, tick, stage и нарушения; не нарушился за maxTicks — met false. Дальше: time.step 1 доигрывает тик. " +
                "В живой игре тяжёлые инварианты проверяются раз в 4 тика (invariant.list: livePeriods, every) — на время ожидания они " +
                "проверяются каждый тик: остановка на тике нарушения, а не до 3 тиков позже.",
            Arguments = CoreTools.Props(
                ("ids", CoreTools.StrArray("id инвариантов (без них — любой включённый)")),
                ("maxTicks", CoreTools.Int("не больше стольких тиков (по умолчанию 100 000)", 1, 10_000_000)),
                ("timeoutSeconds", CoreTools.Num("сколько ждать, с (по умолчанию 60)", 0.1, 3600))),
            ReadOnly = false, Idempotent = false, Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var ids = call.Strings("ids");
                // Живая игра: периоды тяжёлых кодовых инвариантов сняты на время ожидания — агенту нужна
                // точная остановка на тике нарушения; после ответа (и при ошибке, таймауте) — снова как было.
                bool everyTick = false;
                try
                {
                    return await ObservationTools.Await(host, call, Math.Min(60, host.MaxWaitSeconds), (ctx, limit) =>
                    {
                        var invariants = ctx.Invariants;
                        foreach (string id in ids)
                            if (invariants.Find(id) is { Enabled: false })
                                throw new ArgumentException($"инвариант {id} выключен — invariant.enable {{ids:[\"{id}\"], enabled:true}}");
                        if (ids.Count == 0 && !invariants.List().Any(i => i.Enabled))
                            throw new ArgumentException("ни одного включённого инварианта — invariant.enable");
                        if (invariants.LivePeriods)
                        {
                            invariants.LivePeriods = false;
                            everyTick = true;
                        }
                        return (host.Observation.ArmInvariant(ctx, ids.Count == 0 ? null : ids, limit), null);
                    }, host.Simulation is null ? "живая игра встаёт после тика нарушения (на время ожидания тяжёлые — каждый тик); stage нарушения — в violations"
                        : "стоим посреди тика после нарушившего барьера: invariant.run, entity.get — мир здесь; time.step 1 — доиграть тик").ConfigureAwait(false);
                }
                finally
                {
                    if (everyTick)
                        try
                        {
                            await host.Serve(ctx => ctx.Invariants.LivePeriods = true, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            Log.Warning("agent", $"time.breakOnInvariant: периоды тяжёлых инвариантов не вернулись ({e.Message}) — проверяются каждый тик");
                        }
                }
            },
        });
    }

    private static JsonObject Describe(InvariantInfo i, JsonObject? definition) => new()
    {
        ["id"] = i.Id,
        ["owner"] = i.Owner,
        ["scope"] = i.Scope.ToString().ToLowerInvariant(),
        ["cost"] = i.Cost.ToString(),
        ["every"] = i.Every,
        ["after"] = i.After,
        ["enabled"] = i.Enabled,
        ["evaluations"] = i.Evaluations,
        ["violations"] = i.Violations,
        ["meanMicroseconds"] = Math.Round(i.MeanMicroseconds, 3),
        ["last"] = i.Last is { } v ? ObservationTools.ViolationJson(v) : null,
        ["definition"] = definition?.DeepClone(),
    };
}
