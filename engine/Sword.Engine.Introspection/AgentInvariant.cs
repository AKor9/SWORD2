using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>Нарушение инварианта как запись истории (<c>events.history</c>, <c>replay.observe</c>): тип и поля.</summary>
public static class InvariantRecord
{
    /// <summary>Тип записи рядом с событиями журналов.</summary>
    public const string Type = "InvariantViolation";

    public static JsonObject Fields(in InvariantViolation v) => new()
    {
        ["id"] = v.Id, ["entity"] = v.Entity?.ToString(), ["details"] = v.Details,
    };
}

/// <summary>
/// Инвариант агента (<c>invariant.define</c>, раздел 8 плана): условие языка <see cref="ObservationPredicate"/>, которое
/// должно держаться. Два вида: <c>condition</c> — условие мира (нарушение — оно ложно); <c>must</c> — условие на
/// сущность для каждой сущности под <c>where</c> (без <c>where</c> — у кого есть компоненты из <c>must</c>; нарушение —
/// по сущности). Проверяется после барьера стадии <c>after</c> (по умолчанию конец тика) раз в <c>every</c> тиков.
/// Живёт в симуляции, к которой привязан (<see cref="ObservationHub"/> переносит определение в новую при смене).
/// </summary>
public sealed class AgentInvariant : IInvariant
{
    public const string Owner = "agent";

    private readonly Simulation _simulation;
    private readonly ObservationHistory _history;
    private readonly ObservationPredicate? _condition, _must, _where;

    private AgentInvariant(string id, InvariantCost cost, string after, JsonObject definition, Simulation simulation,
        ObservationHistory history, ObservationPredicate? condition, ObservationPredicate? must, ObservationPredicate? where)
    {
        Id = id;
        Cost = cost;
        After = after;
        Definition = definition;
        _simulation = simulation;
        _history = history;
        _condition = condition;
        _must = must;
        _where = where;
    }

    public string Id { get; }
    public InvariantCost Cost { get; }
    public InvariantScope Scope => _condition is null ? InvariantScope.Entity : InvariantScope.World;
    public string After { get; }

    /// <summary>Определение, как его задали (для <c>invariant.list</c> и переноса в новую симуляцию).</summary>
    public JsonObject Definition { get; }

    /// <summary>Стоимость условия для агента.</summary>
    public string CostText(WorldReader world) =>
        (_condition ?? _must)!.Cost(world) + (_must is not null ? "; по каждой сущности" + (_where is null ? " с компонентами must" : " под where") : "");

    /// <summary>
    /// Собрать из определения (поток симуляции, точка обслуживания): <c>{id, condition | must [+ where], after, every}</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Неверное определение или условие — с местом.</exception>
    public static AgentInvariant Create(JsonObject definition, in ServiceContext ctx, Catalog catalog, IEntityNames? names, ObservationHistory history)
    {
        // Через текст: узлы, собранные в памяти, хранят значение своего типа (int не читается как double).
        var d = (JsonObject)JsonNode.Parse(definition.ToJsonString())!;
        foreach (var (key, _) in d)
            if (key is not ("id" or "condition" or "must" or "where" or "after" or "every"))
                throw new ArgumentException($"«{key}» — лишний ключ; есть: id, condition, must, where, after, every");
        string id = d["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("id: строка");
        if (!IInvariant.IsValidId(id)) throw new ArgumentException($"id «{id}»: нужно {IInvariant.IdRule}");
        string after = d["after"] is { } a ? (a is JsonValue av && av.TryGetValue<string>(out var st) ? st : throw new ArgumentException("after: имя стадии"))
            : Simulation.ExportStage;
        if (!ctx.World.Stages.Contains(after))
            throw new ArgumentException($"after: нет стадии «{after}»; есть: {string.Join(", ", ctx.World.Stages)} ({Simulation.ExportStage} — конец тика)");
        int every = 1;
        if (d["every"] is { } e)
        {
            double n = e.GetValue<double>();
            if (n != Math.Floor(n) || n < 1 || n > 1_000_000) throw new ArgumentException("every: целое 1…1000000");
            every = (int)n;
        }
        ObservationPredicate? condition = null, must = null, where = null;
        if (d["condition"] is { } c)
        {
            if (d["must"] is not null || d["where"] is not null) throw new ArgumentException("condition или must (+ where) — что-то одно");
            condition = ObservationPredicate.Compile(c, ctx, catalog, names);
            if (condition.Stateful && every > 1) throw new ArgumentException("for (окно тиков) — только с every 1: окно считает проверки подряд");
        }
        else if (d["must"] is { } m)
        {
            must = ObservationPredicate.Compile(m, ctx, catalog, names, PredicateScope.Entity);
            if (d["where"] is { } w) where = ObservationPredicate.Compile(w, ctx, catalog, names, PredicateScope.Entity);
            else if (must.Required.Count == 0)
                throw new ArgumentException("must без where и без компонента — неясно, какие сущности проверять: задайте where или {component:…}");
        }
        else throw new ArgumentException("нужно condition (условие мира) или must (условие на каждую сущность); " + ObservationPredicate.Syntax);
        return new AgentInvariant(id, InvariantCost.Every(every), after, d, ctx.Simulation, history, condition, must, where);
    }

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        var sctx = new ServiceContext(_simulation);
        if (_condition is not null)
        {
            if (!_condition.Evaluate(sctx, _history)) violations.Report($"не выполнено: {_condition.Source.ToJsonString()}");
            return;
        }
        var world = ctx.World;
        var required = _must!.Required;
        for (int i = 0, n = world.EntitySlots; i < n; i++)
        {
            var e = world.EntityAt(i);
            if (e.IsNull) continue;
            if (_where is not null ? !_where.Matches(sctx, e, _history) : !HasAll(world, e, required)) continue;
            if (_must.Matches(sctx, e, _history)) continue;
            violations.Report(e, $"не выполнено: {_must.Source.ToJsonString()}");
            if (violations.Full) return;
        }
    }

    private static bool HasAll(WorldReader world, Entity e, IReadOnlyList<int> components)
    {
        for (int k = 0; k < components.Count; k++)   // по индексу: перечислитель интерфейса выделял бы память
            if (!world.TryRead(e, components[k], out _)) return false;
        return true;
    }
}
