using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>Где вычисляется условие: мир, одна сущность (в <c>exists</c>, <c>count</c>, наблюдении) или запись журнала.</summary>
public enum PredicateScope { World, Entity, Record }

/// <summary>
/// Язык условий (раздел 8 плана, «ObservationPredicate»): типизированное JSON-дерево из белого списка узлов — один язык у
/// <c>time.runUntil</c>, <c>time.break</c>, <c>watch.add</c>, <c>world.query</c>, <c>events.history</c>,
/// <c>replay.observe</c> (дальше — инварианты и манифесты фич). Условие компилируется при установке (в точке
/// обслуживания: имена сущностей и типов разрешаются один раз) в дерево узлов, которые читают листья полей прямо из
/// байтов мира по смещениям (<see cref="FieldLayout"/>) — без JSON и упаковки; вычисление детерминировано и мир не меняет.
/// Узлы — <see cref="Syntax"/>.
/// </summary>
public sealed class ObservationPredicate
{
    /// <summary>Краткая справка по узлам — в описаниях инструментов и ошибках.</summary>
    public const string Syntax =
        "условие — JSON: {all:[…]}, {any:[…]}, {not:…}; сущность: {component:\"Transform\"}, {entity:\"player\"|\"12v3\"}, " +
        "{field:\"Transform.Position.Y\", lessThan:-10}; мир: {resource:\"SimTime.Time\", …}, {sim:\"ticks\"|\"time\", …}, " +
        "{stage:\"Animation\"}, {event:\"PlayerCommand\"|\"Added<Fighter>\", where:{field:\"Walk\", equals:true}, atLeast:1, within:1}, " +
        "{exists:…}, {none:…}, {count:…, greaterThan:2}, {min|max|sum|mean:\"Transform.Position.X\", where:…, lessThan:0}, " +
        "{for:30, that:…} — держится 30 тиков подряд. Сравнения: equals, notEquals, lessThan, lessOrEqual, greaterThan, " +
        "greaterOrEqual, between:[a,b], isFinite, isNaN (несколько — все сразу). Условие на сущность наверху — «есть сущность, для которой…».";

    private readonly Node _root;
    private readonly ForNode[] _windows;
    private readonly IReadOnlyList<string> _stages;

    private ObservationPredicate(JsonNode source, Node root, ForNode[] windows, PredicateScope scope, int nodes, bool perEntity,
        bool history, bool stateful, IReadOnlyList<string> stages, int[] required)
    {
        Source = source;
        _root = root;
        _windows = windows;
        Scope = scope;
        NodeCount = nodes;
        PerEntity = perEntity;
        UsesHistory = history;
        Stateful = stateful;
        _stages = stages;
        Required = required;
    }

    /// <summary>Условие, как его задали.</summary>
    public JsonNode Source { get; }

    public PredicateScope Scope { get; }

    /// <summary>Узлов в дереве.</summary>
    public int NodeCount { get; }

    /// <summary>Вычисление перебирает сущности (<c>exists</c>, <c>count</c>, агрегаты, условие на сущность наверху).</summary>
    public bool PerEntity { get; }

    /// <summary>Смотрит события прошлых тиков (<c>within</c> больше 1) — нужна история наблюдений.</summary>
    public bool UsesHistory { get; }

    /// <summary>Есть окно тиков (<c>for</c>): вычислять ровно раз за тик, по порядку тиков.</summary>
    public bool Stateful { get; }

    /// <summary>Компоненты, без которых сущность условию не подходит (для перебора в <see cref="PredicateScope.Entity"/>).</summary>
    public IReadOnlyList<int> Required { get; }

    /// <summary>Стоимость для агента: узлы и перебор сущностей.</summary>
    public string Cost(WorldReader world) =>
        $"узлов {NodeCount}" + (PerEntity ? $"; перебор сущностей (живых {world.EntityCount}) в каждом вычислении" : "") +
        (UsesHistory ? "; события прошлых тиков — из истории наблюдений" : "") + (Stateful ? "; окно тиков — вычисляется каждый тик" : "");

    /// <summary>Условие мира в точке <paramref name="ctx"/> (между тиками или посреди тика после барьера стадии).</summary>
    public bool Evaluate(in ServiceContext ctx, ObservationHistory? history = null)
    {
        if (Scope != PredicateScope.World) throw new InvalidOperationException($"условие {Scope}: вычисляется для сущности или записи");
        var env = MakeEnv(ctx, history);
        foreach (var window in _windows) window.Update(env);
        return _root.Eval(env);
    }

    /// <summary>Условие на сущность <paramref name="entity"/> (<see cref="PredicateScope.Entity"/>).</summary>
    public bool Matches(in ServiceContext ctx, Entity entity, ObservationHistory? history = null)
    {
        if (Scope != PredicateScope.Entity) throw new InvalidOperationException($"условие {Scope}: не на сущность");
        var env = MakeEnv(ctx, history);
        env.Entity = entity;
        return ctx.World.IsAlive(entity) && _root.Eval(env);
    }

    /// <summary>Условие на запись журнала (<see cref="PredicateScope.Record"/>): байты записи того журнала, для которого компилировали.</summary>
    public bool MatchesRecord(in ServiceContext ctx, ReadOnlySpan<byte> record, ObservationHistory? history = null)
    {
        if (Scope != PredicateScope.Record) throw new InvalidOperationException($"условие {Scope}: не на запись");
        var env = MakeEnv(ctx, history);
        env.Record = record;
        return _root.Eval(env);
    }

    private Env MakeEnv(in ServiceContext ctx, ObservationHistory? history)
    {
        int stagesDone = ctx.Stage is { } s ? IndexOf(_stages, s) + 1 : _stages.Count;
        ulong current = ctx.Stage is null ? (ctx.Tick == 0 ? 0 : ctx.Tick - 1) : ctx.Tick;
        return new Env { Ctx = ctx, World = ctx.World, History = history, StagesDone = stagesDone, Current = current, Entity = Entity.Null };
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    /// <summary>
    /// Компилировать условие (поток симуляции, в точке обслуживания: имена сущностей разрешаются здесь один раз).
    /// <paramref name="record"/> — журнал, на записи которого условие (<see cref="PredicateScope.Record"/>). <paramref name="fields"/> —
    /// вычисляемые поля игры (<see cref="IEntityFields"/>, 7з): лист <c>{field:"Body.Energy", lessThan:…}</c> на сущности, которого нет в
    /// компонентах, читает игра (число; нет значения — NaN, сравнения с ним ложны); без них — только поля компонентов.
    /// </summary>
    /// <exception cref="ArgumentException">Неизвестный узел, поле, тип, сравнение не по виду поля — с путём в условии.</exception>
    public static ObservationPredicate Compile(JsonNode? source, in ServiceContext ctx, Catalog catalog, IEntityNames? names,
        PredicateScope scope = PredicateScope.World, JournalInfo? record = null, IEntityFields? fields = null)
    {
        if (source is null) throw new ArgumentException("условие пусто; " + Syntax);
        if (scope == PredicateScope.Record && record is null) throw new ArgumentException("условие на запись — нужен журнал");
        // Узлы, собранные в памяти, хранят значение своего типа (int не читается как double); из текста — любые числа.
        source = JsonNode.Parse(source.ToJsonString())!;
        var c = new Compiler(ctx, catalog, names, record, fields);
        var root = c.Compile(source, scope == PredicateScope.World ? PredicateScope.World : scope, "условие", out bool usesEntity);
        bool perEntity = c.PerEntity;
        if (scope == PredicateScope.World && usesEntity)
        {
            // Условие на сущность наверху — «есть такая сущность»: окно тиков по сущностям не считается.
            if (c.Stateful) throw new ArgumentException("условие: for (окно тиков) — только для условия мира; сущность — внутри {exists:…}");
            root = new ExistsNode(root, [.. Compiler.RequiredOf(root)], none: false);
            perEntity = true;
        }
        int[] required = scope == PredicateScope.Entity ? [.. Compiler.RequiredOf(root)] : [];
        return new ObservationPredicate(source, root, [.. c.Windows], scope, c.Nodes, perEntity, c.History, c.Stateful, ctx.World.Stages, required);
    }

    // ── вычисление ─────────────────────────────────────────────────────────

    internal ref struct Env
    {
        public ServiceContext Ctx;
        public WorldReader World;
        public ObservationHistory? History;
        /// <summary>Стадий идущего (или сыгранного) тика с барьером: записи тика в журналах — с возрастом 1…StagesDone.</summary>
        public int StagesDone;
        /// <summary>Номер тика, чьи записи в журналах: посреди тика — идущий, между тиками — последний сыгранный.</summary>
        public ulong Current;
        public Entity Entity;
        public ReadOnlySpan<byte> Record;
    }

    internal abstract class Node
    {
        public abstract bool Eval(in Env env);
    }

    private enum Op : byte { Equals, NotEquals, LessThan, LessOrEqual, GreaterThan, GreaterOrEqual, Between, IsFinite, IsNaN }

    private readonly record struct Check(Op Op, double A, double B, ulong Bits);

    private static readonly (string Name, Op Op)[] Ops =
    [
        ("equals", Op.Equals), ("notEquals", Op.NotEquals), ("lessThan", Op.LessThan), ("lessOrEqual", Op.LessOrEqual),
        ("greaterThan", Op.GreaterThan), ("greaterOrEqual", Op.GreaterOrEqual), ("between", Op.Between),
        ("isFinite", Op.IsFinite), ("isNaN", Op.IsNaN),
    ];

    /// <summary>Лист 64 битами (<see cref="FieldLayout.Bits"/>) → число: знаковые — со знаком, float и double — значением.</summary>
    public static double Number(FieldLayout.Kind kind, ulong bits) => kind switch
    {
        FieldLayout.Kind.I8 or FieldLayout.Kind.I16 or FieldLayout.Kind.I32 or FieldLayout.Kind.I64 => unchecked((long)bits),
        FieldLayout.Kind.F32 => BitConverter.UInt32BitsToSingle(unchecked((uint)bits)),
        FieldLayout.Kind.F64 => BitConverter.UInt64BitsToDouble(bits),
        _ => bits,
    };

    /// <summary>Лист в JSON: число, bool, символ, сущность <c>12v3</c>; нечисловые float — строками.</summary>
    public static JsonNode? LeafJson(FieldLayout.Kind kind, ulong bits)
    {
        switch (kind)
        {
            case FieldLayout.Kind.Bool: return JsonValue.Create(bits != 0);
            case FieldLayout.Kind.Char: return JsonValue.Create(((char)bits).ToString());
            case FieldLayout.Kind.Entity: return JsonValue.Create(new Entity(unchecked((int)(uint)bits), unchecked((int)(bits >> 32))).ToString());
            case FieldLayout.Kind.F32:
            {
                float f = BitConverter.UInt32BitsToSingle(unchecked((uint)bits));
                return float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture));
            }
            case FieldLayout.Kind.F64:
            {
                double d = BitConverter.UInt64BitsToDouble(bits);
                return double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));
            }
            case FieldLayout.Kind.I8 or FieldLayout.Kind.I16 or FieldLayout.Kind.I32 or FieldLayout.Kind.I64:
                return JsonValue.Create(unchecked((long)bits));
            default: return JsonValue.Create(bits);
        }
    }

    private static bool Test(ReadOnlySpan<Check> checks, FieldLayout.Kind kind, ulong bits)
    {
        foreach (ref readonly var c in checks)
        {
            if (kind == FieldLayout.Kind.Entity)
            {
                if ((c.Op == Op.Equals) != (bits == c.Bits)) return false;
                continue;
            }
            double v = Number(kind, bits);
            bool ok = c.Op switch
            {
                Op.Equals => v == c.A,
                Op.NotEquals => v != c.A,
                Op.LessThan => v < c.A,
                Op.LessOrEqual => v <= c.A,
                Op.GreaterThan => v > c.A,
                Op.GreaterOrEqual => v >= c.A,
                Op.Between => v >= c.A && v <= c.B,
                Op.IsFinite => double.IsFinite(v) == (c.A != 0),
                _ => double.IsNaN(v) == (c.A != 0),
            };
            if (!ok) return false;
        }
        return true;
    }

    private static bool TestValue(ReadOnlySpan<Check> checks, double v)
    {
        foreach (ref readonly var c in checks)
        {
            bool ok = c.Op switch
            {
                Op.Equals => v == c.A,
                Op.NotEquals => v != c.A,
                Op.LessThan => v < c.A,
                Op.LessOrEqual => v <= c.A,
                Op.GreaterThan => v > c.A,
                Op.GreaterOrEqual => v >= c.A,
                Op.Between => v >= c.A && v <= c.B,
                Op.IsFinite => double.IsFinite(v) == (c.A != 0),
                _ => double.IsNaN(v) == (c.A != 0),
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Листья значения под проверкой: один лист — сравнения; несколько (структура) — только isFinite/isNaN.</summary>
    private static bool TestLeaves(ReadOnlySpan<Check> checks, FieldLayout layout, int[] leaves, ReadOnlySpan<byte> bytes)
    {
        if (leaves.Length == 1) return Test(checks, layout.Leaves[leaves[0]].Kind, layout.Bits(bytes, leaves[0]));
        foreach (ref readonly var c in checks)
        {
            // isFinite:true — все конечны; isFinite:false — хоть один нет; isNaN:true — хоть один NaN; isNaN:false — ни одного.
            bool any = false, all = true;
            foreach (int l in leaves)
            {
                var kind = layout.Leaves[l].Kind;
                if (kind is not (FieldLayout.Kind.F32 or FieldLayout.Kind.F64)) continue;
                double v = Number(kind, layout.Bits(bytes, l));
                bool hit = c.Op == Op.IsFinite ? double.IsFinite(v) : double.IsNaN(v);
                any |= hit;
                all &= hit;
            }
            bool ok = c.Op == Op.IsFinite ? (c.A != 0 ? all : !all) : (c.A != 0 ? any : !any);
            if (!ok) return false;
        }
        return true;
    }

    private sealed class AllNode(Node[] children) : Node
    {
        public readonly Node[] Children = children;

        public override bool Eval(in Env env)
        {
            foreach (var c in Children)
                if (!c.Eval(env)) return false;
            return true;
        }
    }

    private sealed class AnyNode(Node[] children) : Node
    {
        public override bool Eval(in Env env)
        {
            foreach (var c in children)
                if (c.Eval(env)) return true;
            return false;
        }
    }

    private sealed class NotNode(Node inner) : Node
    {
        public override bool Eval(in Env env) => !inner.Eval(env);
    }

    private sealed class HasComponentNode(int component) : Node
    {
        public readonly int Component = component;

        public override bool Eval(in Env env) => !env.Entity.IsNull && env.World.TryRead(env.Entity, Component, out _);
    }

    private sealed class EntityIsNode(Entity target) : Node
    {
        public override bool Eval(in Env env) => env.Entity == target;
    }

    /// <summary>Вычисляемое поле игры на сущности (<see cref="IEntityFields"/>, 7з): число; нет сущности или значения — ложь.</summary>
    private sealed class ComputedFieldNode(EntityFieldReader read, Check[] checks) : Node
    {
        public override bool Eval(in Env env) => !env.Entity.IsNull && TestValue(checks, read(env.Ctx, env.Entity));
    }

    private sealed class FieldNode(int component, FieldLayout layout, int[] leaves, Check[] checks) : Node
    {
        public readonly int Component = component;

        public override bool Eval(in Env env) =>
            !env.Entity.IsNull && env.World.TryRead(env.Entity, Component, out var bytes) && TestLeaves(checks, layout, leaves, bytes);
    }

    private sealed class RecordFieldNode(FieldLayout layout, int[] leaves, Check[] checks) : Node
    {
        public override bool Eval(in Env env) => !env.Record.IsEmpty && TestLeaves(checks, layout, leaves, env.Record);
    }

    private sealed class RecordEntityNode(FieldLayout layout, int[] entityLeaves, ulong target) : Node
    {
        public override bool Eval(in Env env)
        {
            if (env.Record.IsEmpty) return false;
            foreach (int l in entityLeaves)
                if (layout.Bits(env.Record, l) == target) return true;
            return false;
        }
    }

    private sealed class ResourceNode(int resource, FieldLayout layout, int[] leaves, Check[] checks) : Node
    {
        public override bool Eval(in Env env) =>
            env.World.TryReadResource(resource, out var bytes) && TestLeaves(checks, layout, leaves, bytes);
    }

    private sealed class SimNode(bool time, Check[] checks) : Node
    {
        public override bool Eval(in Env env) =>
            TestValue(checks, time ? env.World.Resource<SimTime>().Time : env.Ctx.Tick);
    }

    private sealed class StageNode(string stage) : Node
    {
        public override bool Eval(in Env env) => env.Ctx.Stage == stage;
    }

    private sealed class ExistsNode(Node inner, int[] required, bool none) : Node
    {
        public override bool Eval(in Env env) => (CountEntities(env, inner, required, stopAtFirst: true) > 0) != none;
    }

    private sealed class CountNode(Node? inner, int[] required, Check[] checks) : Node
    {
        public override bool Eval(in Env env) => TestValue(checks, CountEntities(env, inner, required, stopAtFirst: false));
    }

    private static int CountEntities(in Env env, Node? inner, int[] required, bool stopAtFirst)
    {
        var world = env.World;
        Env sub = env;
        int count = 0;
        for (int i = 0, n = world.EntitySlots; i < n; i++)
        {
            var e = world.EntityAt(i);
            if (e.IsNull || !HasAll(world, e, required)) continue;
            sub.Entity = e;
            if (inner is not null && !inner.Eval(sub)) continue;
            count++;
            if (stopAtFirst) return count;
        }
        return count;
    }

    private static bool HasAll(WorldReader world, Entity e, int[] required)
    {
        foreach (int c in required)
            if (!world.TryRead(e, c, out _)) return false;
        return true;
    }

    internal enum Aggregate : byte { Min, Max, Sum, Mean }

    private sealed class AggregateNode(Aggregate op, int component, FieldLayout layout, int leaf, Node? where, int[] required, Check[] checks) : Node
    {
        public override bool Eval(in Env env)
        {
            var world = env.World;
            Env sub = env;
            int count = 0;
            double acc = op switch { Aggregate.Min => double.PositiveInfinity, Aggregate.Max => double.NegativeInfinity, _ => 0 };
            var kind = layout.Leaves[leaf].Kind;
            for (int i = 0, n = world.EntitySlots; i < n; i++)
            {
                var e = world.EntityAt(i);
                if (e.IsNull || !world.TryRead(e, component, out var bytes) || !HasAll(world, e, required)) continue;
                sub.Entity = e;
                if (where is not null && !where.Eval(sub)) continue;
                double v = Number(kind, layout.Bits(bytes, leaf));
                acc = op switch { Aggregate.Min => Math.Min(acc, v), Aggregate.Max => Math.Max(acc, v), _ => acc + v };
                count++;
            }
            if (count == 0) return false;   // по пустому набору агрегата нет — сравнение ложно
            return TestValue(checks, op == Aggregate.Mean ? acc / count : acc);
        }
    }

    private sealed class EventNode(int journal, Node? where, int atLeast, int within) : Node
    {
        public override bool Eval(in Env env)
        {
            int found = 0;
            var world = env.World;
            Env sub = env;
            // Идущий (или только что сыгранный) тик — записи в журнале мира с возрастом 1…StagesDone.
            for (int i = 0, n = world.JournalLength(journal); i < n; i++)
            {
                var bytes = world.JournalEntry(journal, i, out long age);
                if (age < 1 || age > env.StagesDone) continue;
                sub.Record = bytes;
                if (where is not null && !where.Eval(sub)) continue;
                if (++found >= atLeast) return true;
            }
            // Прошлые тики окна — из истории наблюдений.
            if (within > 1 && env.History is { } history)
                for (ulong k = 1; k < (ulong)within && k <= env.Current; k++)
                {
                    ulong tick = env.Current - k;
                    for (int i = 0, n = history.CountAt(tick); i < n; i++)
                    {
                        if (history.RecordAt(tick, i, out var bytes).Journal != journal) continue;
                        sub.Record = bytes;
                        if (where is not null && !where.Eval(sub)) continue;
                        if (++found >= atLeast) return true;
                    }
                }
            return false;
        }
    }

    /// <summary>
    /// Окно тиков: внутреннее условие держится <c>n</c> вычислений подряд — по одному на тик. Серию считает
    /// <see cref="Update"/> — у всех окон условия до вычисления корня (<see cref="Evaluate"/>), поэтому <c>all</c>/<c>any</c>,
    /// которые останавливаются на первом решающем узле, окно не пропускают; <see cref="Eval"/> — итог этого момента.
    /// </summary>
    private sealed class ForNode(int ticks, Node inner) : Node
    {
        private ulong _last = ulong.MaxValue;
        private string? _lastStage;
        private int _streak;

        public void Update(in Env env)
        {
            ulong now = env.Ctx.Tick;
            string? stage = env.Ctx.Stage;
            if (now == _last && stage == _lastStage) return;   // тот же момент — та же серия
            bool consecutive = _last != ulong.MaxValue && now == _last + 1 && stage == _lastStage;
            _streak = inner.Eval(env) ? (consecutive ? _streak + 1 : 1) : 0;
            _last = now;
            _lastStage = stage;
        }

        public override bool Eval(in Env env) => _streak >= ticks;
    }

    // ── компиляция ─────────────────────────────────────────────────────────

    private sealed class Compiler(ServiceContext ctx, Catalog catalog, IEntityNames? names, JournalInfo? record, IEntityFields? fields = null)
    {
        public int Nodes;
        public bool History, Stateful, PerEntity;
        public readonly List<ForNode> Windows = [];

        private readonly FieldLayout? _recordLayout = record is null ? null : FieldLayout.Of(record.EntryType);

        public Node Compile(JsonNode node, PredicateScope scope, string at, out bool usesEntity)
        {
            usesEntity = false;
            if (node is not JsonObject o || o.Count == 0) throw new ArgumentException($"{at}: ожидается объект-узел; {Syntax}");
            Nodes++;
            if (Nodes > 256) throw new ArgumentException($"{at}: условие больше 256 узлов");
            string form = FormOf(o, at);
            switch (form)
            {
                case "all":
                case "any":
                {
                    Only(o, at, form);
                    if (o[form] is not JsonArray items || items.Count == 0) throw new ArgumentException($"{at}.{form}: ожидается непустой массив узлов");
                    var children = new Node[items.Count];
                    for (int i = 0; i < items.Count; i++)
                    {
                        children[i] = Compile(items[i] ?? throw new ArgumentException($"{at}.{form}[{i}]: null"), scope, $"{at}.{form}[{i}]", out bool u);
                        usesEntity |= u;
                    }
                    return form == "all" ? new AllNode(children) : new AnyNode(children);
                }
                case "not":
                    Only(o, at, "not");
                    return new NotNode(Compile(o["not"] ?? throw new ArgumentException($"{at}.not: null"), scope, $"{at}.not", out usesEntity));
                case "component":
                {
                    Only(o, at, "component");
                    usesEntity = EntityScope(scope, at, "component");
                    return new HasComponentNode(catalog.Component(Str(o, "component", at)).Id);
                }
                case "entity":
                {
                    Only(o, at, "entity");
                    var target = ResolveEntity(Str(o, "entity", at), at);
                    if (scope == PredicateScope.Record)
                    {
                        int[] leaves = [.. _recordLayout!.Leaves.Select((l, i) => (l, i)).Where(x => x.l.Kind == FieldLayout.Kind.Entity).Select(x => x.i)];
                        if (leaves.Length == 0) throw new ArgumentException($"{at}: в записи {record!.Name} нет полей-сущностей");
                        return new RecordEntityNode(_recordLayout, leaves, EntityBits(target));
                    }
                    usesEntity = EntityScope(scope, at, "entity");
                    return new EntityIsNode(target);
                }
                case "field":
                {
                    string path = Str(o, "field", at);
                    if (scope == PredicateScope.Record)
                    {
                        var (layout, leaves, type) = RecordPath(path, at);
                        return new RecordFieldNode(layout, leaves, Checks(o, at, "field", layout, leaves, type));
                    }
                    usesEntity = EntityScope(scope, at, "field");
                    // 7з: вычисляемое поле игры (тело бойца вне ECS — Body.*) — если путь не поле компонента
                    if (fields is not null && !IsComponentPath(path) && fields.Compile(path, $"{at}.field") is { } read)
                        return new ComputedFieldNode(read, NumberChecks(o, at, "field"));
                    var (component, clayout, cleaves, ctype) = ComponentPath(path, at);
                    return new FieldNode(component.Id, clayout, cleaves, Checks(o, at, "field", clayout, cleaves, ctype));
                }
                case "resource":
                {
                    var (resource, layout, leaves, type) = ResourcePath(Str(o, "resource", at), at);
                    return new ResourceNode(resource, layout, leaves, Checks(o, at, "resource", layout, leaves, type));
                }
                case "sim":
                {
                    string which = Str(o, "sim", at);
                    if (which is not ("ticks" or "time")) throw new ArgumentException($"{at}.sim: «{which}» — ticks (сыграно тиков) или time (время симуляции, с)");
                    return new SimNode(which == "time", NumberChecks(o, at, "sim"));
                }
                case "stage":
                {
                    Only(o, at, "stage");
                    string stage = Str(o, "stage", at);
                    if (!ctx.World.Stages.Contains(stage))
                        throw new ArgumentException($"{at}.stage: нет стадии «{stage}»; есть: {string.Join(", ", ctx.World.Stages)}");
                    return new StageNode(stage);
                }
                case "event":
                {
                    Only(o, at, "event", "where", "atLeast", "within");
                    var journal = ResolveJournal(Str(o, "event", at), at);
                    Node? where = null;
                    if (o["where"] is { } w)
                    {
                        var inner = new Compiler(ctx, catalog, names, journal, fields);
                        where = inner.Compile(w, PredicateScope.Record, $"{at}.where", out _);
                        Nodes += inner.Nodes;
                    }
                    int atLeast = Int(o, "atLeast", 1, 1, 1_000_000, at), within = Int(o, "within", 1, 1, 100_000, at);
                    if (within > 1) History = true;
                    return new EventNode(journal.Id, where, atLeast, within);
                }
                case "exists":
                case "none":
                {
                    Only(o, at, form);
                    var inner = Compile(o[form] ?? throw new ArgumentException($"{at}.{form}: null"), PredicateScope.Entity, $"{at}.{form}", out _);
                    PerEntity = true;
                    return new ExistsNode(inner, [.. RequiredOf(inner)], none: form == "none");
                }
                case "count":
                {
                    Node? inner = o["count"] is JsonObject { Count: 0 } or null ? null
                        : Compile(o["count"]!, PredicateScope.Entity, $"{at}.count", out _);
                    PerEntity = true;
                    return new CountNode(inner, inner is null ? [] : [.. RequiredOf(inner)], NumberChecks(o, at, "count"));
                }
                case "min":
                case "max":
                case "sum":
                case "mean":
                {
                    var (component, layout, leaves, _) = ComponentPath(Str(o, form, at), at);
                    if (leaves.Length != 1) throw new ArgumentException($"{at}.{form}: путь — не лист (полей {leaves.Length}); нужно число, например {LeafExample(layout)}");
                    if (layout.Leaves[leaves[0]].Kind == FieldLayout.Kind.Entity) throw new ArgumentException($"{at}.{form}: сущность — не число");
                    Node? where = o["where"] is { } w ? Compile(w, PredicateScope.Entity, $"{at}.where", out _) : null;
                    PerEntity = true;
                    var op = form switch { "min" => Aggregate.Min, "max" => Aggregate.Max, "sum" => Aggregate.Sum, _ => Aggregate.Mean };
                    return new AggregateNode(op, component.Id, layout, leaves[0], where, where is null ? [] : [.. RequiredOf(where)],
                        NumberChecks(o, at, form, "where"));
                }
                case "for":
                {
                    Only(o, at, "for", "that");
                    if (scope != PredicateScope.World) throw new ArgumentException($"{at}.for: окно тиков — только в условии мира");
                    int ticks = Int(o, "for", 1, 1, 1_000_000, at);
                    var inner = Compile(o["that"] ?? throw new ArgumentException($"{at}: for без that"), PredicateScope.World, $"{at}.that", out bool u);
                    if (u)
                    {
                        inner = new ExistsNode(inner, [.. RequiredOf(inner)], none: false);
                        PerEntity = true;
                    }
                    Stateful = true;
                    var window = new ForNode(ticks, inner);
                    Windows.Add(window);   // после вложенных окон: обновляются изнутри наружу
                    return window;
                }
            }
            throw new InvalidOperationException(form);
        }

        /// <summary>Путь начинается с имени компонента мира (иначе он может быть вычисляемым полем игры).</summary>
        private bool IsComponentPath(string path)
        {
            int dot = path.IndexOf('.');
            string head = dot < 0 ? path : path[..dot];
            foreach (var c in ctx.World.Components)
                if (c.Type.Name == head || c.Type.FullName == head) return true;
            return false;
        }

        private static readonly string[] Forms =
            ["all", "any", "not", "component", "entity", "field", "resource", "sim", "stage", "event", "exists", "none", "count",
             "min", "max", "sum", "mean", "for"];

        private static string FormOf(JsonObject o, string at)
        {
            var found = o.Select(p => p.Key).Where(k => Forms.Contains(k)).ToList();
            if (found.Count == 1) return found[0];
            if (found.Count == 0)
                throw new ArgumentException($"{at}: нет узла ({string.Join(", ", o.Select(p => p.Key))}); узлы: {string.Join(", ", Forms)}. {Syntax}");
            throw new ArgumentException($"{at}: два узла сразу ({string.Join(", ", found)}) — оберните в all/any");
        }

        private static void Only(JsonObject o, string at, params string[] allowed)
        {
            foreach (var (key, _) in o)
                if (!allowed.Contains(key)) throw new ArgumentException($"{at}: лишний ключ «{key}» у узла {allowed[0]} (можно: {string.Join(", ", allowed)})");
        }

        private static bool EntityScope(PredicateScope scope, string at, string form) =>
            scope switch
            {
                PredicateScope.Record => throw new ArgumentException($"{at}: {form} — узел сущности, а условие на запись журнала (поля записи — field)"),
                _ => true,
            };

        private static string Str(JsonObject o, string key, string at) =>
            o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s ? s
                : throw new ArgumentException($"{at}.{key}: ожидается непустая строка");

        private static int Int(JsonObject o, string key, int fallback, int min, int max, string at)
        {
            if (o[key] is not { } n) return fallback;
            if (n.GetValueKind() != JsonValueKind.Number || n.GetValue<double>() is var d && (Math.Floor(d) != d || d < min || d > max))
                throw new ArgumentException($"{at}.{key}: целое {min}…{max}");
            return (int)n.GetValue<double>();
        }

        private Entity ResolveEntity(string text, string at)
        {
            if (text.Length > 0 && (char.IsDigit(text[0]) || text is "null" or "Entity.Null")) return FieldCodec.ParseEntity(text, at);
            return names?.Resolve(ctx, text) ?? throw new ArgumentException(
                $"{at}: нет сущности с именем «{text}»; имена: {string.Join(", ", names?.Known ?? [])} (или номер «12v3»)");
        }

        private static ulong EntityBits(Entity e) => unchecked((uint)e.Index | (ulong)(uint)e.Generation << 32);

        /// <summary>Журнал по имени: «Hit» — Event&lt;Hit&gt;, «Added&lt;Fighter&gt;» — срабатывание; короткое или полное имя.</summary>
        public JournalInfo ResolveJournal(string name, string at) => ObservationPredicate.ResolveJournal(ctx.World, name, at);

        // Компонент пути: самое длинное начало, которое называет компонент (имя может быть полным, с точками).
        private (DataTypeInfo Component, FieldLayout Layout, int[] Leaves, Type Type) ComponentPath(string path, string at)
        {
            var (component, rest) = SplitComponent(catalog, path, at);
            if (component.IsTag) throw new ArgumentException($"{at}: {component.Name} — метка без полей; наличие — {{component:…}}");
            var layout = FieldLayout.Of(component.Type);
            var leaves = LeavesOf(layout, rest, $"{at} ({path})");
            return (component, layout, leaves, component.Type);
        }

        private (int Resource, FieldLayout Layout, int[] Leaves, Type Type) ResourcePath(string path, string at)
        {
            var parts = path.Split('.');
            for (int n = parts.Length; n >= 1; n--)
            {
                ResourceInfo info;
                try
                {
                    info = catalog.Resource(string.Join('.', parts[..n]));
                }
                catch (ArgumentException) when (n > 1)
                {
                    continue;
                }
                if (info.Size == 0) throw new ArgumentException($"{at}: ресурс {info.Name} со ссылками — полями не читается");
                var layout = FieldLayout.Of(info.Type);
                return (info.Id, layout, LeavesOf(layout, string.Join('.', parts[n..]), $"{at} ({path})"), info.Type);
            }
            throw new ArgumentException($"{at}: нет ресурса в пути «{path}»");
        }

        private (FieldLayout Layout, int[] Leaves, Type Type) RecordPath(string path, string at)
        {
            var layout = _recordLayout!;
            var type = record!.EntryType;
            if (record.EntrySize == 0) throw new ArgumentException($"{at}: запись {record.Name} со ссылками — полями не читается");
            // «Walk» — поле значения события (Value.Walk); «PlayerCommand.Walk» — с именем типа; «Entity», «Value.X» — как есть.
            string data = type.IsGenericType ? Catalog.ShortName(type.GetGenericArguments()[0]) : "";
            var candidates = new List<string> { path, "Value." + path };
            if (data.Length > 0 && path.StartsWith(data + ".", StringComparison.OrdinalIgnoreCase)) candidates.Add("Value." + path[(data.Length + 1)..]);
            foreach (var c in candidates)
                if (TryLeaves(layout, c) is { } leaves) return (layout, leaves, type);
            throw new ArgumentException($"{at}: в записи {record.Name} нет поля «{path}»; есть: {string.Join(", ", layout.Leaves.Take(24).Select(l => l.Path))}");
        }

        private Check[] Checks(JsonObject o, string at, string subject, FieldLayout layout, int[] leaves, Type root)
        {
            var checks = new List<Check>();
            foreach (var (key, value) in o)
            {
                if (key == subject) continue;
                var op = Ops.FirstOrDefault(x => x.Name == key);
                if (op.Name is null) throw new ArgumentException($"{at}: «{key}» — не сравнение; есть: {string.Join(", ", Ops.Select(x => x.Name))}");
                if (leaves.Length != 1 && op.Op is not (Op.IsFinite or Op.IsNaN))
                    throw new ArgumentException($"{at}.{key}: путь — структура из {leaves.Length} полей ({string.Join(", ", leaves.Take(6).Select(l => layout.Leaves[l].Path))}…) — сравнивается лист; у структуры — isFinite, isNaN");
                var leaf = layout.Leaves[leaves[0]];
                if (leaves.Length == 1 && leaf.Kind == FieldLayout.Kind.Entity)
                {
                    if (op.Op is not (Op.Equals or Op.NotEquals)) throw new ArgumentException($"{at}.{key}: сущность сравнивается только equals/notEquals");
                    string text = value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>()
                        : throw new ArgumentException($"{at}.{key}: сущность — строкой «12v3» или именем");
                    checks.Add(new Check(op.Op, 0, 0, EntityBits(ResolveEntity(text, $"{at}.{key}"))));
                    continue;
                }
                var type = leaves.Length == 1 ? LeafType(root, leaf.Path) : null;
                checks.Add(ParseCheck(op.Op, value, type, $"{at}.{key}"));
            }
            if (checks.Count == 0) throw new ArgumentException($"{at}: нет сравнения ({string.Join(", ", Ops.Select(x => x.Name))})");
            return [.. checks];
        }

        private static Check[] NumberChecks(JsonObject o, string at, string subject, params string[] extra)
        {
            var checks = new List<Check>();
            foreach (var (key, value) in o)
            {
                if (key == subject || extra.Contains(key)) continue;
                var op = Ops.FirstOrDefault(x => x.Name == key);
                if (op.Name is null) throw new ArgumentException($"{at}: «{key}» — не сравнение; есть: {string.Join(", ", Ops.Select(x => x.Name))}");
                checks.Add(ParseCheck(op.Op, value, typeof(double), $"{at}.{key}"));
            }
            if (checks.Count == 0) throw new ArgumentException($"{at}: нет сравнения ({string.Join(", ", Ops.Select(x => x.Name))})");
            return [.. checks];
        }

        private static Check ParseCheck(Op op, JsonNode? value, Type? type, string at)
        {
            switch (op)
            {
                case Op.IsFinite or Op.IsNaN:
                    return value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                        ? new Check(op, b.GetValue<bool>() ? 1 : 0, 0, 0)
                        : throw new ArgumentException($"{at}: ожидается true или false");
                case Op.Between:
                    if (value is not JsonArray { Count: 2 } range) throw new ArgumentException($"{at}: ожидается [от, до]");
                    double lo = Operand(range[0], type, at), hi = Operand(range[1], type, at);
                    if (!(lo <= hi)) throw new ArgumentException($"{at}: от {lo} больше до {hi}");
                    return new Check(op, lo, hi, 0);
                default:
                    return new Check(op, Operand(value, type, at), 0, 0);
            }
        }

        /// <summary>Значение сравнения по типу листа: число, bool, имя перечисления, символ.</summary>
        private static double Operand(JsonNode? value, Type? type, string at)
        {
            if (value is not JsonValue v) throw new ArgumentException($"{at}: ожидается значение, а не {value?.ToJsonString() ?? "null"}");
            var kind = v.GetValueKind();
            if (kind is JsonValueKind.True or JsonValueKind.False) return v.GetValue<bool>() ? 1 : 0;
            if (kind == JsonValueKind.Number) return v.GetValue<double>();
            string s = v.GetValue<string>();
            if (type is { IsEnum: true })
            {
                if (!Enum.TryParse(type, s, ignoreCase: true, out var e) || !Enum.IsDefined(type, e!))
                    throw new ArgumentException($"{at}: нет значения «{s}» у {type.Name}; есть: {string.Join(", ", Enum.GetNames(type))}");
                return Convert.ToDouble(Convert.ChangeType(e, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            if (type == typeof(char) && s.Length == 1) return s[0];
            return s switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                _ => throw new ArgumentException($"{at}: «{s}» — не число" + (type is { IsEnum: true } ? "" : " (строкой — только имя перечисления, символ, NaN, Infinity)")),
            };
        }

        public static IEnumerable<int> RequiredOf(Node node) => node switch
        {
            HasComponentNode h => [h.Component],
            FieldNode f => [f.Component],
            AllNode a => a.Children.SelectMany(RequiredOf).Distinct(),
            _ => [],
        };
    }

    // ── пути полей ─────────────────────────────────────────────────────────

    /// <summary>Компонент в начале пути (<c>Transform.Position.X</c>) и остаток пути внутри него.</summary>
    public static (DataTypeInfo Component, string Inner) SplitComponent(Catalog catalog, string path, string at)
    {
        var parts = path.Split('.');
        for (int n = parts.Length; n >= 1; n--)
        {
            try
            {
                return (catalog.Component(string.Join('.', parts[..n])), string.Join('.', parts[n..]));
            }
            catch (ArgumentException) when (n > 1)
            {
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"{at}: {e.Message}");
            }
        }
        throw new ArgumentException($"{at}: нет компонента в пути «{path}»");
    }

    /// <summary>
    /// Листья под путём <paramref name="rest"/> (без учёта регистра): лист, структура (все листья под ней) или всё значение
    /// (пустой путь). Нет такого — ошибка с перечнем листьев.
    /// </summary>
    public static int[] LeavesOf(FieldLayout layout, string rest, string at) =>
        TryLeaves(layout, rest) ?? throw new ArgumentException(
            $"{at}: нет поля «{rest}»; поля: {string.Join(", ", layout.Leaves.Take(24).Select(l => l.Path.Length == 0 ? "(значение)" : l.Path))}" +
            (layout.Leaves.Count > 24 ? "…" : ""));

    private static int[]? TryLeaves(FieldLayout layout, string rest)
    {
        if (rest.Length == 0) return layout.Leaves.Count > 0 ? [.. Enumerable.Range(0, layout.Leaves.Count)] : null;
        var exact = new List<int>();
        var under = new List<int>();
        for (int i = 0; i < layout.Leaves.Count; i++)
        {
            string p = layout.Leaves[i].Path;
            if (string.Equals(p, rest, StringComparison.OrdinalIgnoreCase)) exact.Add(i);
            else if (p.Length > rest.Length && p.StartsWith(rest, StringComparison.OrdinalIgnoreCase) && p[rest.Length] is '.' or '[') under.Add(i);
        }
        return exact.Count > 0 ? [.. exact] : under.Count > 0 ? [.. under] : null;
    }

    private static string LeafExample(FieldLayout layout) => layout.Leaves.FirstOrDefault(l => l.Kind is FieldLayout.Kind.F32 or FieldLayout.Kind.F64).Path ?? "";

    /// <summary>Тип CLR листа по пути (<c>Position.X</c>, <c>Arr[1]</c>) — для имён перечислений.</summary>
    internal static Type LeafType(Type root, string leafPath)
    {
        var t = root;
        if (leafPath.Length == 0) return t;
        foreach (string segment in leafPath.Split('.'))
        {
            int bracket = segment.IndexOf('[');
            string name = bracket >= 0 ? segment[..bracket] : segment;
            if (name.Length > 0) t = FieldCodec.Fields(t).First(f => f.Name == name).Type;
            if (bracket >= 0) t = FieldCodec.Fields(t).First().Type;   // элемент встроенного массива
        }
        return t;
    }

    /// <summary>Журнал мира по имени: «Hit» — <c>Event&lt;Hit&gt;</c>; «Added&lt;Fighter&gt;» — срабатывание (короткое или полное имя).</summary>
    public static JournalInfo ResolveJournal(WorldReader world, string name, string at)
    {
        var journals = world.Journals;
        string wanted = name.Contains('<') ? name : $"Event<{name}>";
        var match = journals.Where(j => string.Equals(j.Name, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Catalog.ShortName(j.EntryType), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 1) return match[0];
        if (match.Count > 1) throw new ArgumentException($"{at}: «{name}» неоднозначно: {string.Join(", ", match.Select(j => j.Name))}");
        throw new ArgumentException($"{at}: журнала «{wanted}» нет — у события нет потребителей в мире (без потребителя записи не " +
            $"хранятся), или имя другое; журналы: {string.Join(", ", journals.Select(j => Catalog.ShortName(j.EntryType)))}");
    }
}
