using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// Итог условия остановки: выполнилось ли, после скольких тиков (посреди тика — номер идущего) и на какой стадии;
/// у остановки на инварианте — нарушения, на которых встали.
/// </summary>
public sealed record ConditionHit(bool Met, ulong Tick, string? Stage, string Reason, IReadOnlyList<InvariantViolation>? Violations = null);

/// <summary>
/// Наблюдение агента за симуляцией (раздел 8 плана): наблюдатель тиков (<see cref="ISimulationObserver"/>) — после каждого
/// тика снимает историю (<see cref="History"/>) и строки наблюдений (<see cref="Watches"/>), проверяет условие остановки
/// (<c>time.runUntil</c> — после тика, <c>time.break</c> — после барьера своей стадии) и останавливает время на его тике
/// (<see cref="TimeControl.Halt"/>). Условие остановки может быть и нарушением инварианта (<c>time.breakOnInvariant</c>):
/// нарушения тика узел кладёт в историю. Инварианты агента (<c>invariant.define</c>) и включения инвариантов агентом узел
/// переносит в каждую новую симуляцию, как наблюдения. Всё — на потоке симуляции; агент читает копиями в точке
/// обслуживания. Мир не меняет, в хеш состояния не входит.
/// </summary>
public sealed class ObservationHub : ISimulationObserver
{
    private readonly TimeControl _time;
    private readonly object _gate = new();
    private readonly List<Watch> _watches = [];
    private Armed? _armed;
    private int _nextWatch = 1;
    private Catalog? _catalog;
    private IEntityNames? _names;

    /// <summary>
    /// Вычисляемые поля игры (<see cref="IEntityFields"/>): наблюдения (<c>watch.add</c>, <c>replay.observe</c>) пишут их
    /// по тикам, как листья компонентов (<c>Body.MaxSpeed</c>, <c>Body.foot.l.Position.Y</c>); null — только компоненты.
    /// </summary>
    public IEntityFields? Fields { get; set; }

    private sealed class Armed(ObservationPredicate? predicate, int? stage, string? stageName, ulong limit, HashSet<string>? invariants = null)
    {
        public readonly ObservationPredicate? Predicate = predicate;
        public readonly int? Stage = stage;
        public readonly string? StageName = stageName;
        public readonly ulong Limit = limit;
        public readonly TaskCompletionSource<ConditionHit> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Остановка на нарушении инварианта (<see cref="Predicate"/> нет): какие Id (null — любой).</summary>
        public readonly bool OnInvariant = predicate is null;
        public readonly HashSet<string>? Invariants = invariants;
        public readonly List<InvariantViolation> Hits = [];
    }

    private readonly List<JsonObject> _defined = [];
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);

    public ObservationHub(TimeControl time, int historyTicks = ObservationHistory.DefaultTicks)
    {
        _time = time;
        History = new ObservationHistory(historyTicks);
    }

    public ObservationHistory History { get; }

    /// <summary>Симуляция, за которой идёт наблюдение; null — ещё не подключено.</summary>
    public Simulation? Simulation { get; private set; }

    /// <summary>
    /// Останов посреди тика (<c>time.break</c>): хозяин цикла обслуживает очередь агента, пока время не разрешит идти
    /// дальше, — только Headless (<c>SimulationLoop</c>). null — останова посреди тика нет (живая игра).
    /// </summary>
    public Action<Simulation, string>? BreakHandler { get; set; }

    /// <summary>Наблюдения (поток симуляции).</summary>
    public IReadOnlyList<Watch> Watches => _watches;

    /// <summary>
    /// Подключить к симуляции (поток симуляции; до её первого тика — история полная): прежняя отключается, история
    /// очищается, наблюдения привязываются заново (не привязалось — снимается с предупреждением), условие остановки
    /// снимается («симуляция сменилась»).
    /// </summary>
    public void Attach(Simulation simulation, Catalog catalog, IEntityNames? names)
    {
        Simulation?.Unobserve(this);
        Simulation = simulation;
        _catalog = catalog;
        _names = names;
        History.Reset(simulation.World);
        Finish(null, new ConditionHit(false, simulation.Tick, null, "симуляция сменилась (session.start, time.seek назад)"));
        var ctx = new ServiceContext(simulation);
        foreach (var d in _defined.ToList())
            try
            {
                simulation.Invariants.Add(AgentInvariant.Create(d, ctx, catalog, names, History), AgentInvariant.Owner);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                _defined.Remove(d);
                Log.Warning("agent", $"инвариант {d["id"]} снят: в новой симуляции не собрался — {e.Message}");
            }
        foreach (var (id, enabled) in _enabled.ToList())
            try
            {
                simulation.Invariants.SetEnabled(id, enabled);
            }
            catch (ArgumentException)
            {
                _enabled.Remove(id);
                Log.Warning("agent", $"включение инварианта {id} не перенесено: в новой симуляции его нет");
            }
        foreach (var w in _watches.ToList())
            try
            {
                w.Bind(ctx, catalog, names, Fields);
            }
            catch (ArgumentException e)
            {
                _watches.Remove(w);
                Log.Warning("agent", $"наблюдение {w.Id} снято: в новой симуляции не привязалось — {e.Message}");
            }
        simulation.Observe(this);
    }

    // ── условие остановки ──────────────────────────────────────────────────

    /// <summary>
    /// Поставить условие остановки (поток симуляции, в точке обслуживания): проверяется после каждого следующего тика
    /// (<paramref name="stage"/> null) или после барьера стадии <paramref name="stage"/>; выполнилось — время встаёт на этом
    /// тике. Не выполнилось, когда сыграно <paramref name="limit"/> тиков, — итог «не выполнилось». Прежнее условие
    /// снимается (его итог — «заменено»).
    /// </summary>
    public Task<ConditionHit> Arm(in ServiceContext ctx, ObservationPredicate predicate, string? stage, ulong limit)
    {
        int? index = null;
        if (stage is not null)
        {
            if (BreakHandler is null) throw new InvalidOperationException("останов посреди тика — только Sword.Headless --agent");
            int i = ctx.World.Stages.ToList().IndexOf(stage);
            if (i < 0) throw new ArgumentException($"stage: нет стадии «{stage}»; есть: {string.Join(", ", ctx.World.Stages)}");
            index = i;
        }
        return Replace(new Armed(predicate, index, stage, limit), ctx);
    }

    /// <summary>
    /// Встать на нарушении инварианта (<c>time.breakOnInvariant</c>; поток симуляции, точка обслуживания): <paramref name="ids"/>
    /// (null — любого). Headless встаёт посреди тика после нарушившего барьера (<see cref="BreakHandler"/>), живая игра — после
    /// тика. Прежнее условие остановки снимается.
    /// </summary>
    public Task<ConditionHit> ArmInvariant(in ServiceContext ctx, IReadOnlyCollection<string>? ids, ulong limit)
    {
        var invariants = ctx.Simulation.Invariants;
        if (ids is not null)
            foreach (string id in ids)
                if (invariants.Find(id) is null)
                    throw new ArgumentException($"нет инварианта «{id}»; есть: {string.Join(", ", invariants.List().Select(i => i.Id))}");
        return Replace(new Armed(null, null, null, limit, ids is null ? null : [.. ids]), ctx);
    }

    private Task<ConditionHit> Replace(Armed armed, in ServiceContext ctx)
    {
        Armed? previous;
        lock (_gate)
        {
            previous = _armed;
            _armed = armed;
        }
        previous?.Done.TrySetResult(new ConditionHit(false, ctx.Tick, ctx.Stage, "заменено новым условием"));
        return armed.Done.Task;
    }

    /// <summary>Снять условие (таймаут ожидания): итог — «снято».</summary>
    public void Disarm(Task<ConditionHit> which, string reason)
    {
        Armed? armed;
        lock (_gate)
        {
            armed = _armed?.Done.Task == which ? _armed : null;
            if (armed is not null) _armed = null;
        }
        armed?.Done.TrySetResult(new ConditionHit(false, Simulation?.Tick ?? 0, null, reason));
    }

    /// <summary>Итог условия <paramref name="armed"/> (null — текущего, какое есть); стоит оно ещё — снимается.</summary>
    private void Finish(Armed? armed, ConditionHit hit)
    {
        lock (_gate)
        {
            armed ??= _armed;
            if (_armed == armed) _armed = null;
        }
        armed?.Done.TrySetResult(hit);
    }

    // ── наблюдатель тиков ──────────────────────────────────────────────────

    public void OnViolation(Simulation simulation, in InvariantViolation violation)
    {
        History.AddViolation(violation);
        var armed = Volatile.Read(ref _armed);
        if (armed is { OnInvariant: true } && (armed.Invariants is null || armed.Invariants.Contains(violation.Id))) armed.Hits.Add(violation);
    }

    public void AfterStage(Simulation simulation, int stage)
    {
        var armed = Volatile.Read(ref _armed);
        if (armed is { OnInvariant: true })
        {
            // Нарушение этого барьера: Headless встаёт здесь же, посреди тика; живая игра — после тика (AfterTick).
            if (armed.Hits.Count == 0 || BreakHandler is null) return;
            string name = simulation.World.Stages[stage];
            _time.Halt(simulation.Tick);
            Finish(armed, new ConditionHit(true, simulation.Tick, name, "нарушен инвариант", [.. armed.Hits]));
            BreakHandler(simulation, name);
            return;
        }
        if (armed?.Stage != stage) return;
        var ctx = new ServiceContext(simulation);
        if (!Evaluate(armed, ctx)) return;
        _time.Halt(simulation.Tick);
        Finish(armed, new ConditionHit(true, simulation.Tick, armed.StageName, "выполнилось"));
        BreakHandler?.Invoke(simulation, armed.StageName!);
    }

    public void AfterTick(Simulation simulation)
    {
        var ctx = new ServiceContext(simulation);
        History.Capture(simulation.World, simulation.Tick - 1);
        foreach (var w in _watches) w.Sample(ctx, History);
        var armed = Volatile.Read(ref _armed);
        if (armed is null) return;
        if (armed.OnInvariant && armed.Hits.Count > 0)
        {
            _time.Halt(simulation.Tick);
            Finish(armed, new ConditionHit(true, simulation.Tick, null, "нарушен инвариант", [.. armed.Hits]));
        }
        else if (!armed.OnInvariant && armed.Stage is null && Evaluate(armed, ctx))
        {
            _time.Halt(simulation.Tick);
            Finish(armed, new ConditionHit(true, simulation.Tick, null, "выполнилось"));
        }
        else if (simulation.Tick >= armed.Limit)
            Finish(armed, new ConditionHit(false, simulation.Tick, null, $"не выполнилось за отведённые тики (сыграно {simulation.Tick})"));
    }

    private bool Evaluate(Armed armed, in ServiceContext ctx)
    {
        try
        {
            return armed.Predicate!.Evaluate(ctx, History);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            // Ошибка вычисления — ответ агенту, а не падение тика.
            Finish(armed, new ConditionHit(false, ctx.Tick, ctx.Stage, $"ошибка условия: {e.Message}"));
            return false;
        }
    }

    // ── наблюдения ─────────────────────────────────────────────────────────

    /// <summary>Добавить наблюдение (поток симуляции, в точке обслуживания).</summary>
    public Watch AddWatch(in ServiceContext ctx, JsonObject definition)
    {
        if (_catalog is null) throw new InvalidOperationException("наблюдение не подключено к симуляции");
        if (_watches.Count >= MaxWatches) throw new InvalidOperationException($"наблюдений не больше {MaxWatches} — снимите лишние (watch.remove)");
        string id = definition["id"]?.GetValue<string>() is { Length: > 0 } given ? given : $"w{_nextWatch}";
        if (_watches.Any(w => w.Id == id)) throw new ArgumentException($"наблюдение {id} уже есть");
        var watch = new Watch(id, definition);
        watch.Bind(ctx, _catalog, _names, Fields);
        _watches.Add(watch);
        _nextWatch++;
        return watch;
    }

    public bool RemoveWatch(string id) => _watches.RemoveAll(w => w.Id == id) > 0;

    // ── инварианты агента ──────────────────────────────────────────────────

    /// <summary>Определения инвариантов агента (переносятся в новую симуляцию).</summary>
    public IReadOnlyList<JsonObject> DefinedInvariants => _defined;

    /// <summary>
    /// Инвариант агента (<c>invariant.define</c>; поток симуляции, точка обслуживания): тот же id агента заменяется; кодовый
    /// id — ошибка.
    /// </summary>
    public AgentInvariant DefineInvariant(in ServiceContext ctx, JsonObject definition)
    {
        if (_catalog is null) throw new InvalidOperationException("наблюдение не подключено к симуляции");
        var invariant = AgentInvariant.Create(definition, ctx, _catalog, _names, History);
        var invariants = ctx.Simulation.Invariants;
        if (invariants.Find(invariant.Id) is { } existing)
        {
            if (existing.Owner != AgentInvariant.Owner) throw new ArgumentException($"инвариант {invariant.Id} — {existing.Owner}: id занят");
            invariants.Remove(invariant.Id, AgentInvariant.Owner);
            _defined.RemoveAll(d => (string?)d["id"] == invariant.Id);
        }
        invariants.Add(invariant, AgentInvariant.Owner);
        _defined.Add(invariant.Definition);
        _enabled.Remove(invariant.Id);
        return invariant;
    }

    /// <summary>Убрать инвариант агента.</summary>
    public bool RemoveInvariant(in ServiceContext ctx, string id)
    {
        _enabled.Remove(id);
        _defined.RemoveAll(d => (string?)d["id"] == id);
        return ctx.Simulation.Invariants.Remove(id, AgentInvariant.Owner);
    }

    /// <summary>Включить или выключить инвариант (и в следующих симуляциях сеанса).</summary>
    public void SetInvariantEnabled(in ServiceContext ctx, string id, bool enabled)
    {
        ctx.Simulation.Invariants.SetEnabled(id, enabled);
        _enabled[id] = enabled;
    }

    public void ClearWatches() => _watches.Clear();

    public const int MaxWatches = 16;
}

/// <summary>
/// Наблюдение (<c>watch.add</c>): после каждого <see cref="Every"/>-го тика — строка на каждую подходящую сущность (заданные
/// или все, для которых выполняется условие <c>where</c>) со значениями листьев полей <c>fields</c>. Строки — в кольце
/// на <see cref="Capacity"/> строк; тик строки — <c>tickId</c> (сыграно тиков), как в квитанциях. Поле может быть
/// вычисляемым полем игры (<see cref="IEntityFields"/>: капсулы тела <c>Body.*</c>) — лист <c>F64</c>, нет значения (NaN) — null.
/// </summary>
public sealed class Watch
{
    public const int DefaultCapacity = 20_000, MaxCapacity = 200_000;

    internal Watch(string id, JsonObject definition)
    {
        Id = id;
        // Через текст: узлы, собранные в памяти, хранят значение своего типа (int не читается как double).
        Definition = (JsonObject)JsonNode.Parse(definition.ToJsonString())!;
        definition = Definition;
        Every = definition["every"] is { } e ? checked((int)e.GetValue<double>()) : 1;
        if (Every < 1 || Every > 1_000_000) throw new ArgumentException("every: 1…1000000");
        Capacity = definition["capacity"] is { } c ? checked((int)c.GetValue<double>()) : DefaultCapacity;
        if (Capacity < 1 || Capacity > MaxCapacity) throw new ArgumentException($"capacity: 1…{MaxCapacity}");
    }

    public string Id { get; }
    public JsonObject Definition { get; }
    public int Every { get; }
    public int Capacity { get; }

    /// <summary>Листья полей: путь для людей (<c>Transform.Position.X</c>), компонент (−1 — вычисляемое поле игры), вид.</summary>
    public IReadOnlyList<(string Path, int Component, FieldLayout.Kind Kind)> Fields => _fieldInfo;

    /// <summary>Условие отбора; null — все сущности с полями (или заданные).</summary>
    public ObservationPredicate? Where { get; private set; }

    /// <summary>Заданные сущности; null — перебор всех.</summary>
    public IReadOnlyList<Entity>? Entities => _entities;

    private Entity[]? _entities;
    private (int Component, FieldLayout? Layout, int Leaf, EntityFieldReader? Computed)[] _fields = [];
    private (string, int, FieldLayout.Kind)[] _fieldInfo = [];
    private int[] _components = [];
    private ulong[] _ticks = [], _values = [];
    private Entity[] _rows = [];
    private bool[] _present = [];
    private int _start, _count;

    /// <summary>Строк в кольце.</summary>
    public int Rows => _count;

    /// <summary>Строк добавлено за всё время (кольцо держит последние <see cref="Capacity"/>).</summary>
    public long Appended { get; private set; }

    public ulong? OldestTick => _count > 0 ? _ticks[_start] : null;
    public ulong? NewestTick => _count > 0 ? _ticks[(_start + _count - 1) % Capacity] : null;

    /// <summary>Привязать к миру (новая симуляция): поля, сущности, условие; строки очищаются.</summary>
    internal void Bind(in ServiceContext ctx, Catalog catalog, IEntityNames? names, IEntityFields? computed = null)
    {
        var paths = Definition["fields"] switch
        {
            JsonArray a when a.Count > 0 => a.Select((n, i) => n?.GetValue<string>() ?? throw new ArgumentException($"fields[{i}]: null")).ToList(),
            _ => throw new ArgumentException("fields: непустой массив путей («Transform.Position», «Transform»)"),
        };
        var fields = new List<(int, FieldLayout?, int, EntityFieldReader?)>();
        var info = new List<(string, int, FieldLayout.Kind)>();
        foreach (string path in paths)
        {
            if (computed?.Compile(path, $"fields ({path})") is { } reader)
            {
                if (info.Any(i => i.Item1 == path)) continue;
                fields.Add((-1, null, 0, reader));
                info.Add((path, -1, FieldLayout.Kind.F64));
                continue;
            }
            var (component, rest) = ObservationPredicate.SplitComponent(catalog, path, "fields");
            string display = catalog.DisplayName(component);
            if (component.IsTag) throw new ArgumentException($"fields: {component.Name} — метка без полей");
            var layout = FieldLayout.Of(component.Type);
            foreach (int leaf in ObservationPredicate.LeavesOf(layout, rest, $"fields ({path})"))
            {
                if (fields.Any(f => f.Item1 == component.Id && f.Item3 == leaf)) continue;
                fields.Add((component.Id, layout, leaf, null));
                string leafPath = layout.Leaves[leaf].Path;
                info.Add((leafPath.Length == 0 ? display : $"{display}.{leafPath}", component.Id, layout.Leaves[leaf].Kind));
            }
        }
        if (fields.Count > 64) throw new ArgumentException($"fields: листьев {fields.Count} — не больше 64 (сузьте пути)");
        _fields = [.. fields];
        _fieldInfo = [.. info];
        _components = [.. fields.Where(f => f.Item1 >= 0).Select(f => f.Item1).Distinct()];

        _entities = null;
        if (Definition["entities"] is JsonArray list)
        {
            _entities = new Entity[list.Count];
            for (int i = 0; i < list.Count; i++)
                _entities[i] = ResolveEntity(ctx, list[i]?.GetValue<string>() ?? throw new ArgumentException($"entities[{i}]: null"), names);
        }
        Where = Definition["where"] is { } where ? ObservationPredicate.Compile(where, ctx, catalog, names, PredicateScope.Entity) : null;

        _ticks = new ulong[Capacity];
        _rows = new Entity[Capacity];
        _values = new ulong[Capacity * _fields.Length];
        _present = new bool[Capacity * _fields.Length];
        _start = _count = 0;
        Appended = 0;
    }

    private static Entity ResolveEntity(in ServiceContext ctx, string text, IEntityNames? names)
    {
        if (text.Length > 0 && char.IsDigit(text[0])) return FieldCodec.ParseEntity(text, "entities");
        return names?.Resolve(ctx, text) ?? throw new ArgumentException($"entities: нет сущности «{text}» (номер «12v3» или имя: {string.Join(", ", names?.Known ?? [])})");
    }

    /// <summary>После тика (поток симуляции): строки подходящих сущностей.</summary>
    internal void Sample(in ServiceContext ctx, ObservationHistory history)
    {
        ulong tickId = ctx.Tick;
        if (tickId % (ulong)Every != 0) return;
        var world = ctx.World;
        if (_entities is not null)
        {
            foreach (var e in _entities)
                if (world.IsAlive(e) && (Where is null || Where.Matches(ctx, e, history))) Append(ctx, tickId, e);
            return;
        }
        for (int i = 0, n = world.EntitySlots; i < n; i++)
        {
            var e = world.EntityAt(i);
            if (e.IsNull || !HasAny(ctx, e)) continue;
            if (Where is not null && !Where.Matches(ctx, e, history)) continue;
            Append(ctx, tickId, e);
        }
    }

    private bool HasAny(in ServiceContext ctx, Entity e)
    {
        foreach (int c in _components)
            if (ctx.World.TryRead(e, c, out _)) return true;
        foreach (var f in _fields)
            if (f.Computed is { } read && !double.IsNaN(read(ctx, e))) return true;
        return false;
    }

    private void Append(in ServiceContext ctx, ulong tickId, Entity e)
    {
        var world = ctx.World;
        int row;
        if (_count < Capacity) row = (_start + _count++) % Capacity;
        else
        {
            row = _start;
            _start = (_start + 1) % Capacity;
        }
        Appended++;
        _ticks[row] = tickId;
        _rows[row] = e;
        int at = row * _fields.Length;
        for (int f = 0; f < _fields.Length; f++)
        {
            var (component, layout, leaf, computedField) = _fields[f];
            if (computedField is not null)
            {
                double v = computedField(ctx, e);
                _present[at + f] = !double.IsNaN(v);
                _values[at + f] = BitConverter.DoubleToUInt64Bits(v);
                continue;
            }
            bool has = world.TryRead(e, component, out var bytes);
            _present[at + f] = has;
            _values[at + f] = has ? layout!.Bits(bytes, leaf) : 0;
        }
    }

    /// <summary>Строки сущности <paramref name="entity"/> (null — все) с тика <paramref name="from"/> по <paramref name="to"/>.</summary>
    public IEnumerable<(ulong TickId, Entity Entity, ulong?[] Values)> Read(Entity? entity, ulong from, ulong to)
    {
        for (int i = 0; i < _count; i++)
        {
            int row = (_start + i) % Capacity;
            if (_ticks[row] < from || _ticks[row] > to) continue;
            if (entity is { } only && _rows[row] != only) continue;
            yield return RowAt(row);
        }
    }

    /// <summary>Последние <paramref name="count"/> строк по порядку.</summary>
    public IEnumerable<(ulong TickId, Entity Entity, ulong?[] Values)> Tail(int count)
    {
        count = Math.Min(count, _count);
        for (int i = _count - count; i < _count; i++) yield return RowAt((_start + i) % Capacity);
    }

    private (ulong TickId, Entity Entity, ulong?[] Values) RowAt(int row)
    {
        var values = new ulong?[_fields.Length];
        for (int f = 0; f < _fields.Length; f++)
            values[f] = _present[row * _fields.Length + f] ? _values[row * _fields.Length + f] : null;
        return (_ticks[row], _rows[row], values);
    }

    /// <summary>Есть ли строки сущности <paramref name="entity"/>.</summary>
    public bool Covers(Entity entity)
    {
        if (_entities is not null) return _entities.Contains(entity);
        for (int i = 0; i < _count; i++)
            if (_rows[(_start + i) % Capacity] == entity) return true;
        return false;
    }
}
