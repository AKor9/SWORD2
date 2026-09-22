using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>Что наблюдать в прогоне реплея: окно тиков, события по типам (<c>*</c> — все), наблюдение, условие.</summary>
public sealed record ReplayObserveOptions
{
    /// <summary>С какого тика писать (номер тика событий и <c>tickId</c> строк — не меньше).</summary>
    public ulong From { get; init; }

    /// <summary>До скольких сыгранных тиков идти (не больше длины реплея, если она задана).</summary>
    public ulong To { get; init; }

    /// <summary>
    /// Типы событий и срабатываний (<c>PlayerCommand</c>, <c>Added&lt;Fighter&gt;</c>, <c>InvariantViolation</c> — нарушения
    /// инвариантов, <c>*</c> — всё).
    /// </summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>Наблюдение как у <c>watch.add</c> (<c>entities</c>, <c>where</c>, <c>fields</c>, <c>every</c>); null — нет.</summary>
    public JsonObject? Watch { get; init; }

    /// <summary>Условие мира: после каждого тика — выполняется ли (переходы и сводка); null — нет.</summary>
    public JsonNode? Condition { get; init; }

    /// <summary>Строк в итоге не больше (события, строки наблюдения, переходы условия).</summary>
    public int MaxLines { get; init; } = 50_000;
}

/// <summary>Итог прогона: строки (JSON), сводка по событиям, условию и наблюдению, воспроизведение реплея.</summary>
public sealed class ReplayObserveResult
{
    public List<JsonObject> Lines { get; } = [];
    public bool Truncated { get; set; }
    public ulong Ticks { get; set; }
    public SortedDictionary<string, long> EventCounts { get; } = new(StringComparer.Ordinal);
    public long WatchRows { get; set; }

    /// <summary>Нарушений инвариантов с from (пишутся всегда, строки — если просили) и первое из них.</summary>
    public long Violations { get; set; }
    public InvariantViolation? FirstViolation { get; set; }

    /// <summary>Условие: первый и последний <c>tickId</c>, когда выполнялось, и сколько тиков; null — условия не было.</summary>
    public (ulong? First, ulong? Last, long Ticks)? Condition { get; set; }
    public string? ConditionCost { get; set; }
    public required ReplayPlayer Player { get; init; }
}

/// <summary>
/// Наблюдение за прогоном реплея (<c>replay.observe</c>, «глубже кольца» истории): реплей идёт в отдельной симуляции с
/// начала до <see cref="ReplayObserveOptions.To"/>, с наблюдателем — события выбранных типов по тикам (стадия, поля),
/// строки наблюдения, переходы условия. Сеанс агента не трогается. Тот же язык условий и наблюдений, что у живого сеанса.
/// </summary>
public static class ReplayObservation
{
    /// <param name="simulation">Симуляция, собранная из конфига реплея (<see cref="Replay.CreateConfig"/>), на тике 0.</param>
    public static ReplayObserveResult Run(Simulation simulation, Replay replay, IEntityNames? names, ReplayObserveOptions options,
        CancellationToken cancel = default)
    {
        if (options.To < options.From) throw new ArgumentException($"to {options.To} меньше from {options.From}");
        var player = simulation.Play(replay);
        var catalog = new Catalog(simulation.World, simulation.CommandTypes);
        var hub = new ObservationHub(new TimeControl(paused: false), historyTicks: 1024);
        hub.Attach(simulation, catalog, names);
        var ctx = new ServiceContext(simulation);
        var result = new ReplayObserveResult { Player = player };

        var journals = new HashSet<int>();
        bool violationLines = false;
        foreach (string type in options.Events)
        {
            if (type == "*")
            {
                journals.UnionWith(simulation.World.Journals.Select(j => j.Id));
                violationLines = true;
            }
            else if (string.Equals(type, InvariantRecord.Type, StringComparison.OrdinalIgnoreCase)) violationLines = true;
            else journals.Add(ObservationPredicate.ResolveJournal(simulation.World, type, "events").Id);
        }
        var condition = options.Condition is null ? null : ObservationPredicate.Compile(options.Condition, ctx, catalog, names);
        if (condition is not null) result.ConditionCost = condition.Cost(simulation.World);
        var watch = options.Watch is null ? null : hub.AddWatch(ctx, options.Watch);

        bool? was = null;
        ulong? first = null, last = null;
        long held = 0;
        void Line(JsonObject line)
        {
            if (result.Lines.Count < options.MaxLines) result.Lines.Add(line);
            else result.Truncated = true;
        }

        while (simulation.Tick < options.To)
        {
            if ((simulation.Tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
            simulation.Export.BeginFrame();
            long appended = watch?.Appended ?? 0;
            simulation.Step();
            ulong tick = simulation.Tick - 1, tickId = simulation.Tick;
            // Условие — каждый тик и до from: окно for считает тики подряд с начала прогона; пишется — с from.
            bool now = condition?.Evaluate(ctx, hub.History) ?? false;
            if (tick < options.From) continue;

            if (journals.Count > 0)
                for (int i = 0, n = hub.History.CountAt(tick); i < n; i++)
                {
                    var (journal, stage) = hub.History.RecordAt(tick, i, out var bytes);
                    if (!journals.Contains(journal)) continue;
                    var info = hub.History.Journals[journal];
                    string type = Catalog.ShortName(info.EntryType);
                    result.EventCounts[type] = result.EventCounts.GetValueOrDefault(type) + 1;
                    Line(new JsonObject
                    {
                        ["tick"] = tick, ["stage"] = hub.History.Stages[stage], ["type"] = type,
                        ["fields"] = info.EntrySize > 0 ? FieldCodec.Decode(info.EntryType, bytes) : null,
                    });
                }

            foreach (var v in hub.History.ViolationsAt(tick))
            {
                result.Violations++;
                result.FirstViolation ??= v;
                if (!violationLines) continue;
                result.EventCounts[InvariantRecord.Type] = result.EventCounts.GetValueOrDefault(InvariantRecord.Type) + 1;
                Line(new JsonObject { ["tick"] = tick, ["stage"] = v.Stage, ["type"] = InvariantRecord.Type, ["fields"] = InvariantRecord.Fields(v) });
            }

            if (watch is not null)
                foreach (var row in watch.Tail((int)Math.Min(watch.Appended - appended, watch.Capacity)))
                {
                    result.WatchRows++;
                    var values = new JsonObject();
                    for (int f = 0; f < watch.Fields.Count; f++)
                        values[watch.Fields[f].Path] = row.Values[f] is { } bits ? ObservationPredicate.LeafJson(watch.Fields[f].Kind, bits) : null;
                    Line(new JsonObject { ["tickId"] = row.TickId, ["entity"] = row.Entity.ToString(), ["values"] = values });
                }

            if (condition is not null)
            {
                if (now)
                {
                    first ??= tickId;
                    last = tickId;
                    held++;
                }
                if (now != was) Line(new JsonObject { ["tickId"] = tickId, ["condition"] = now });
                was = now;
            }
        }
        result.Ticks = simulation.Tick;
        if (condition is not null) result.Condition = (first, last, held);
        simulation.Unobserve(hub);
        return result;
    }
}
