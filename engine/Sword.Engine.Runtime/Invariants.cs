using System.Diagnostics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>Чего касается инвариант (для людей и инструментов): сущности, системы, подсистемы, мира, клиента.</summary>
public enum InvariantScope { Entity, System, Subsystem, World, Client }

/// <summary>
/// Класс стоимости инварианта: каждый тик или раз в N тиков. Период действует только в живой игре
/// (<see cref="SimulationInvariants.LivePeriods"/>); в тестах, verify, Headless и сверке реплея — каждый тик.
/// </summary>
public readonly record struct InvariantCost
{
    private InvariantCost(int period) => Period = period;

    /// <summary>Период в тиках.</summary>
    public int Period { get; }

    public static InvariantCost EveryTick => new(1);

    /// <summary>Период тяжёлых кодовых инвариантов в живой игре, тиков: нарушение, которое держится, видно не позже чем через 3 тика.</summary>
    public const int HeavyPeriod = 4;

    /// <summary>Тяжёлый кодовый инвариант (от ~2 % тика толпы): в живой игре — раз в <see cref="HeavyPeriod"/> тика.</summary>
    public static InvariantCost Heavy => new(HeavyPeriod);

    public static InvariantCost Every(int ticks) =>
        ticks >= 1 ? new(ticks) : throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "период — от 1 тика");

    /// <summary>Проверять на тике <paramref name="tick"/> (номер идущего тика), если включён.</summary>
    public bool Due(ulong tick) => Period <= 1 || tick % (ulong)Period == 0;

    public override string ToString() => Period switch
    {
        1 => "каждый тик",
        _ => $"раз в {Period} {(Period % 10 is >= 2 and <= 4 && Period % 100 is < 12 or > 14 ? "тика" : "тиков")}",
    };
}

/// <summary>Мир после барьера стадии <see cref="Stage"/> тика <see cref="Tick"/> (согласован, записи стадии в
/// журналах). Только чтение.</summary>
public readonly struct InvariantContext(WorldReader world, ulong tick, string stage, float dt)
{
    public WorldReader World { get; } = world;

    /// <summary>Номер идущего тика (как у команд и событий).</summary>
    public ulong Tick { get; } = tick;

    /// <summary>Стадия, после барьера которой идёт проверка; <see cref="Simulation.ExportStage"/> — конец тика.</summary>
    public string Stage { get; } = stage;

    /// <summary>Шаг тика, с.</summary>
    public float Dt { get; } = dt;
}

/// <summary>
/// Инвариант — свойство, которое держится всегда. Проверяется после барьера стадии <see cref="After"/> на потоке
/// симуляции, ничего не меняет и в хеш не входит; без нарушений не выделяет памяти.
/// </summary>
public interface IInvariant
{
    /// <summary>Стабильный Id: <c>ecs.finite</c>, <c>animation.owners</c> — <see cref="IdRule"/>.</summary>
    string Id { get; }

    InvariantCost Cost { get; }

    InvariantScope Scope { get; }

    /// <summary>Стадия (игры или служебная), после барьера которой проверяется; <see cref="Simulation.ExportStage"/> — конец тика.</summary>
    string After { get; }

    /// <summary>Проверить мир; нарушения — в <paramref name="violations"/> (сущность и подробности для людей).</summary>
    void Evaluate(in InvariantContext ctx, ref ViolationSink violations);

    const string IdRule = "части из латиницы, цифр и '_' через точку (fighter.deadNotKinematic), первой — строчная буква";

    static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] is < 'a' or > 'z' || id[^1] == '.') return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (c == '.' && id[i - 1] != '.') continue;
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')) return false;
        }
        return true;
    }
}

/// <summary>Нарушение инварианта — структурированный факт: какой, на каком тике, после какой стадии, чья сущность, что именно.</summary>
public readonly record struct InvariantViolation(string Id, ulong Tick, string Stage, Entity? Entity, string Details)
{
    public override string ToString() =>
        $"{Id}: тик {Tick} после {Stage}" + (Entity is { } e ? $", сущность {e}" : "") + $" — {Details}";
}

/// <summary>Приёмник нарушений: не больше <see cref="Limit"/> подробностей за проверку, остальные только считаются.</summary>
public ref struct ViolationSink
{
    /// <summary>Подробностей за одну проверку не больше.</summary>
    public const int Limit = 8;

    private readonly List<InvariantViolation>? _into;
    private readonly string _id, _stage;
    private readonly ulong _tick;

    internal ViolationSink(List<InvariantViolation> into, string id, ulong tick, string stage)
    {
        _into = into;
        _id = id;
        _tick = tick;
        _stage = stage;
    }

    /// <summary>Нарушений за проверку (и сверх <see cref="Limit"/>).</summary>
    public int Count { get; private set; }

    /// <summary>Подробности не записываются — дальше только подсчёт.</summary>
    public readonly bool Full => Count >= Limit;

    public void Report(string details) => Add(null, details);

    public void Report(Entity entity, string details) => Add(entity, details);

    private void Add(Entity? entity, string details)
    {
        if (Count++ < Limit) _into?.Add(new InvariantViolation(_id, _tick, _stage, entity, details));
    }
}

/// <summary>Инвариант наружу: описание, включён ли, итоги проверок; <paramref name="Every"/> — нынешний период
/// проверки, тиков (1 — каждый тик).</summary>
public sealed record InvariantInfo(string Id, string Owner, InvariantScope Scope, InvariantCost Cost, string After, bool Enabled,
    long Evaluations, long Violations, double MeanMicroseconds, InvariantViolation? Last, int Every = 1);

/// <summary>
/// Инварианты симуляции: реестр, проверка после барьеров, учёт нарушений и стоимости — на потоке симуляции. При
/// <c>sim.invariants = false</c> (бенч) кодовые выключены, кроме включённых явно.
/// </summary>
public sealed class SimulationInvariants
{
    /// <summary>Недавних нарушений в кольце.</summary>
    public const int RecentCapacity = 256;

    /// <summary>Нарушений одного инварианта в журнале (дальше — только счёт).</summary>
    private const int LoggedPerInvariant = 5;

    private sealed class Entry(IInvariant invariant, string owner, int stage, bool enabled)
    {
        public readonly IInvariant Invariant = invariant;
        public readonly string Owner = owner;
        public readonly int Stage = stage;
        public readonly InvariantCost Cost = invariant.Cost;
        public bool Enabled = enabled;
        public long Evaluations, Violations, Ticks;
        public int Logged;
        public InvariantViolation? Last;
        /// <summary>Зона профайлера CPU <c>invariant.&lt;id&gt;</c> (<see cref="AttachProfiler"/>).</summary>
        public int Zone;
    }

    private CpuProfiler _profiler = CpuProfiler.Off;

    /// <summary>Профайлер CPU: каждая проверка — зона <c>invariant.&lt;id&gt;</c>, заведённая здесь, а не в тике.</summary>
    internal void AttachProfiler(CpuProfiler profiler)
    {
        foreach (var e in _entries) e.Zone = profiler.Zone("invariant." + e.Invariant.Id);
        _profiler = profiler;
    }

    private readonly WorldReader _world;
    private readonly List<Entry> _entries = [];
    private Entry[][] _byStage;
    private readonly List<InvariantViolation> _found = [];
    private readonly InvariantViolation[] _recent = new InvariantViolation[RecentCapacity];
    private int _recentStart, _recentCount;

    internal SimulationInvariants(WorldReader world, bool enabled, IEnumerable<(IInvariant Invariant, string Owner)> registered)
    {
        _world = world;
        EnabledByConfig = enabled;
        _byStage = new Entry[world.Stages.Count][];
        foreach (var (invariant, owner) in registered) AddEntry(invariant, owner, enabled);
        Rebuild();
    }

    /// <summary>
    /// Живая игра: кодовые инварианты с периодом проверяются раз в период; false (тесты, Headless, сверка реплея) — каждый
    /// тик. На состояние и хеш не влияет; нарушение короче периода может не попасть в проверку.
    /// </summary>
    public bool LivePeriods { get; set; }

    /// <summary>Как часто проверяется инвариант сейчас, тиков (1 — каждый).</summary>
    private int EveryOf(Entry e) => LivePeriods ? e.Cost.Period : 1;

    /// <summary><c>sim.invariants</c>: кодовые инварианты включены по умолчанию.</summary>
    public bool EnabledByConfig { get; }

    /// <summary>Есть ли что проверять после барьеров.</summary>
    public bool Active { get; private set; }

    /// <summary>Нарушений за всё время.</summary>
    public long Total { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Инварианты по порядку регистрации.</summary>
    public IReadOnlyList<InvariantInfo> List() => [.. _entries.Select(Info)];

    private InvariantInfo Info(Entry e) =>
        new(e.Invariant.Id, e.Owner, e.Invariant.Scope, e.Cost, e.Invariant.After, e.Enabled, e.Evaluations, e.Violations,
            e.Evaluations == 0 ? 0 : e.Ticks * 1e6 / Stopwatch.Frequency / e.Evaluations, e.Last, EveryOf(e));

    /// <summary>Последние нарушения (до <see cref="RecentCapacity"/>) по порядку.</summary>
    public IReadOnlyList<InvariantViolation> Recent()
    {
        var list = new InvariantViolation[_recentCount];
        for (int i = 0; i < _recentCount; i++) list[i] = _recent[(_recentStart + i) % RecentCapacity];
        return list;
    }

    /// <summary>Включить или выключить инвариант (владелец, между тиками) — например, в тесте, который подставляет данные
    /// мимо систем.</summary>
    /// <exception cref="ArgumentException">Нет такого.</exception>
    public void SetEnabled(string id, bool enabled)
    {
        Get(id).Enabled = enabled;
        Rebuild();
    }

    private Entry Get(string id) => _entries.FirstOrDefault(e => e.Invariant.Id == id)
        ?? throw new ArgumentException($"нет инварианта «{id}»; есть: {string.Join(", ", _entries.Select(e => e.Invariant.Id))}");

    private void AddEntry(IInvariant invariant, string owner, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(invariant);
        if (!IInvariant.IsValidId(invariant.Id)) throw new InvalidOperationException($"инвариант «{invariant.Id}»: нужно {IInvariant.IdRule}");
        if (_entries.Any(e => e.Invariant.Id == invariant.Id)) throw new InvalidOperationException($"инвариант «{invariant.Id}» дважды");
        int stage = IndexOf(_world.Stages, invariant.After);
        if (stage < 0)
            throw new InvalidOperationException($"инвариант {invariant.Id}: нет стадии «{invariant.After}»; есть: {string.Join(", ", _world.Stages)}");
        _entries.Add(new Entry(invariant, owner, stage, enabled) { Zone = _profiler.Zone("invariant." + invariant.Id) });
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private void Rebuild()
    {
        var byStage = new Entry[_world.Stages.Count][];
        for (int s = 0; s < byStage.Length; s++) byStage[s] = [.. _entries.Where(e => e.Enabled && e.Stage == s)];
        _byStage = byStage;
        Active = _entries.Any(e => e.Enabled);
    }

    /// <summary>После барьера стадии <paramref name="stage"/> тика <paramref name="tick"/>: включённые инварианты, чей срок.</summary>
    internal void AfterBarrier(int stage, ulong tick, float dt)
    {
        var entries = _byStage[stage];
        if (entries.Length == 0) return;
        string name = _world.Stages[stage];
        var ctx = new InvariantContext(_world, tick, name, dt);
        bool live = LivePeriods;
        foreach (var e in entries)
        {
            // Период — только в живой игре.
            if (live && !e.Cost.Due(tick)) continue;
            int found = Evaluate(e, ctx, _found);
            if (found == 0) continue;
            Record(e, found);
        }
    }

    /// <summary>Одна проверка: время, исключение — тоже нарушение (инвариант не роняет тик). Возвращает число нарушений.</summary>
    private int Evaluate(Entry e, in InvariantContext ctx, List<InvariantViolation> into)
    {
        into.Clear();
        var sink = new ViolationSink(into, e.Invariant.Id, ctx.Tick, ctx.Stage);
        var profiler = _profiler;
        int depth = profiler.Depth;
        profiler.Begin(e.Zone);
        long start = Stopwatch.GetTimestamp();
        try
        {
            e.Invariant.Evaluate(ctx, ref sink);
        }
        catch (Exception ex)
        {
            profiler.UnwindTo(depth + 1);   // зоны, открытые проверкой до исключения, — не её забота
            sink.Report($"исключение в проверке: {ex.GetType().Name}: {ex.Message}");
        }
        e.Ticks += Stopwatch.GetTimestamp() - start;
        profiler.End(e.Zone);
        e.Evaluations++;
        int count = sink.Count;
        if (count > into.Count && into.Count > 0)
        {
            // Сверх предела — числом в последней записи.
            var last = into[^1];
            into[^1] = last with { Details = $"{last.Details} (и ещё {count - into.Count})" };
        }
        return count;
    }

    private void Record(Entry e, int count)
    {
        e.Violations += count;
        Total += count;
        foreach (var v in _found)
        {
            e.Last = v;
            if (_recentCount < RecentCapacity) _recent[(_recentStart + _recentCount++) % RecentCapacity] = v;
            else
            {
                _recent[_recentStart] = v;
                _recentStart = (_recentStart + 1) % RecentCapacity;
            }
            if (e.Logged < LoggedPerInvariant)
            {
                Log.Warning("invariant", $"нарушен {v}");
                if (++e.Logged == LoggedPerInvariant)
                    Log.Warning("invariant", $"{e.Invariant.Id}: дальше нарушения — без журнала");
            }
        }
    }

    /// <summary>Проверить сейчас (между тиками) <paramref name="ids"/> (null — все, и выключенные). Нарушения только
    /// возвращаются, в историю не идут.</summary>
    public IReadOnlyList<(string Id, int Violations, IReadOnlyList<InvariantViolation> Details)> Run(IReadOnlyCollection<string>? ids,
        ulong tick, string stage, float dt)
    {
        var chosen = ids is null ? _entries : [.. ids.Select(Get)];
        var ctx = new InvariantContext(_world, tick, stage, dt);
        var result = new List<(string, int, IReadOnlyList<InvariantViolation>)>();
        var found = new List<InvariantViolation>();
        foreach (var e in chosen)
        {
            int count = Evaluate(e, ctx, found);
            result.Add((e.Invariant.Id, count, [.. found]));
        }
        return result;
    }
}
