namespace Sword.Engine.Ecs;

/// <summary>Запись журнала срабатываний с причиной (<see cref="CauseId"/>): <c>Added</c>, <c>Removed</c>, <c>Changed</c>, <c>Event</c>, <c>ResourceChanged</c>.</summary>
public interface ICaused
{
    CauseId Cause { get; }
}

/// <summary>Вид записи журнала причинности (раздел 8 плана, <c>state.why</c>).</summary>
public enum CauseKind : byte
{
    /// <summary>Команда тика — корень: игрока, арены, врага, пробы удара, агента (любая зарегистрированная команда).</summary>
    Command,

    /// <summary>Решение системы без команды — корень (ИИ врага бьёт по таймеру).</summary>
    Decision,

    /// <summary>Шаг непрерывного производителя с явным родителем (начало удара, возрождение по времени).</summary>
    Step,

    Event,
    Create,
    Destroy,
    Add,
    Remove,
    Set,
    SetResource,
}

/// <summary>
/// Запись журнала причинности: что сделано (<see cref="Kind"/>, сущность, тип), когда (тик, стадия), кем (регистрация системы),
/// где в коде (<see cref="File"/>:<see cref="Line"/> вызова команды) и почему (<see cref="Parent"/> — родительская запись; пусто
/// — корень). <see cref="Type"/> — номер компонента (Add/Set/Remove), события (Event, Command) или слота ресурса (SetResource),
/// −1 — нет. Значения до и после (Set, Add, Remove, событие) — <see cref="CausalityLog.Before"/>/<see cref="CausalityLog.After"/>.
/// </summary>
public readonly record struct CauseRecord
{
    public CauseId Id { get; init; }
    public CauseId Parent { get; init; }
    public ulong Tick { get; init; }

    /// <summary>Номер стадии мира; −1 — вне тика (немедленная операция).</summary>
    public int Stage { get; init; }

    /// <summary>Номер регистрации системы (<see cref="SystemInfo.RegistrationId"/>); 0 — немедленная операция.</summary>
    public int Registration { get; init; }

    public CauseKind Kind { get; init; }
    public Entity Entity { get; init; }
    public int Type { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }

    /// <summary>Пояснение (решение, шаг; у команды — откуда: клиент, агент, реплей); null — нет.</summary>
    public string? Note { get; init; }

    /// <summary>Число шага (у начала удара — номер удара <c>AttackSerial</c>).</summary>
    public long Arg { get; init; }

    /// <summary>Есть ли значение до (Set, Remove) и после (Set, Add, событие) — даже если кольцо значений его уже вытеснило.</summary>
    public bool HasBefore { get; init; }
    public bool HasAfter { get; init; }

    internal long ValueAt { get; init; }
    internal int BeforeSize { get; init; }
    internal int AfterSize { get; init; }
}

/// <summary>
/// Журнал причинности — диагностический режим <c>state.why</c> (раздел 8 плана; 6д). Выключен по умолчанию: мир без журнала
/// (<see cref="World.Causality"/> = null) пишет в слот <see cref="CauseId"/> записей то, что задала система (обычно пусто), и не
/// тратит ничего, кроме проверки на null. Включённый (<see cref="World.SetCausality"/>, между тиками) — каждая команда,
/// событие, команда тика, решение и шаг системы получают свою запись: причина (номер записи — он и идёт в <c>CauseId</c>
/// журналов), родитель (<see cref="CommandBuffer.Cause"/> на момент записи), тик, стадия, регистрация, источник
/// (<c>[CallerFilePath]</c>/<c>[CallerLineNumber]</c>), вид, сущность, тип; у <c>Set</c>/<c>Add</c>/<c>Remove</c> и событий — значения
/// до и после в кольце байтов. Кольца заранее выделены и ограничены: старое вытесняется (<see cref="TryGet"/> — нет записи).
/// Хеш состояния от журнала не зависит: причина — вне хеша записей (участник <c>ecs</c>), журнал — не ресурс мира.
/// Побочная таблица связей (<see cref="Link"/>) — родитель для непрерывных производителей: проход клинка берёт причину начала
/// удара этого <c>AttackSerial</c>; грубый учёт записи столбцов (<see cref="LastWrite"/>) — какая регистрация писала
/// непрерывный компонент в каком тике. Живёт на потоке симуляции; наблюдателям — только чтение в точке обслуживания.
/// </summary>
public sealed class CausalityLog
{
    /// <summary>По умолчанию: 65 536 записей и 4 МиБ значений.</summary>
    public const int DefaultCapacity = 1 << 16, DefaultValueBytes = 1 << 22;

    /// <summary>Самое большое значение (до или после), которое кладётся в кольцо, байт; больше — запись без значения.</summary>
    public const int MaxValueBytes = 1024;

    /// <summary>Связей в побочной таблице (<see cref="Link"/>).</summary>
    public const int LinkSlots = 4096;

    private const int LinkProbe = 8;

    private readonly CauseRecord[] _ring;
    private readonly int _mask;
    private long _next = 1;
    private readonly byte[] _values;
    private long _valuesNext;

    private readonly Entity[] _linkEntity = new Entity[LinkSlots];
    private readonly int[] _linkChannel = new int[LinkSlots];
    private readonly long[] _linkKey = new long[LinkSlots];
    private readonly CauseId[] _linkCause = new CauseId[LinkSlots];

    private ulong[] _writes = [];
    private int _registrations;

    /// <param name="capacity">Записей в кольце — степень двойки от 16 до 2³⁰.</param>
    /// <param name="valueBytes">Байт в кольце значений (до и после), не меньше 4 × <see cref="MaxValueBytes"/>.</param>
    public CausalityLog(int capacity = DefaultCapacity, int valueBytes = DefaultValueBytes)
    {
        if (capacity < 16 || capacity > 1 << 30 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "записей — степень двойки 16…2³⁰");
        if (valueBytes < 4 * MaxValueBytes) throw new ArgumentOutOfRangeException(nameof(valueBytes), valueBytes, $"значений — не меньше {4 * MaxValueBytes} Б");
        _ring = new CauseRecord[capacity];
        _mask = capacity - 1;
        _values = new byte[valueBytes];
    }

    /// <summary>Записей в кольце всего.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Байт в кольце значений.</summary>
    public int ValueCapacity => _values.Length;

    /// <summary>Записей сделано с включения.</summary>
    public long Written => _next - 1 - Skipped;

    /// <summary>Номер следующей записи по порядку и самой старой, ещё лежащей в кольце (<see cref="TryAt"/>).</summary>
    public long NextSequence => _next;
    public long FirstSequence => Math.Max(1, _next - _ring.Length);

    private long Skipped => (_next - 1) >> 31;

    /// <summary>Запись по причине; вытеснена или не было — false.</summary>
    public bool TryGet(CauseId id, out CauseRecord record)
    {
        record = _ring[id.Value & _mask];
        if (id.Value > 0 && record.Id == id) return true;
        record = default;
        return false;
    }

    /// <summary>Запись по номеру по порядку (<see cref="FirstSequence"/> … <see cref="NextSequence"/> − 1).</summary>
    public bool TryAt(long sequence, out CauseRecord record)
    {
        record = default;
        if (sequence < FirstSequence || sequence >= _next) return false;
        record = _ring[(int)(sequence & _mask)];
        return record.Id.Value == (int)(sequence & int.MaxValue) && record.Id.Value != 0;
    }

    /// <summary>Значение до (Set, Remove): байты на месте, годны до следующей записи; вытеснено или нет — пусто.</summary>
    public ReadOnlySpan<byte> Before(in CauseRecord r) => r.HasBefore ? Value(r.ValueAt, r.BeforeSize) : default;

    /// <summary>Значение после (Set, Add, событие) — как <see cref="Before"/>.</summary>
    public ReadOnlySpan<byte> After(in CauseRecord r) => r.HasAfter ? Value(r.ValueAt + Math.Max(0, r.BeforeSize), r.AfterSize) : default;

    /// <summary>Значения записи ещё в кольце (не вытеснены).</summary>
    public bool ValuesKept(in CauseRecord r) =>
        (!r.HasBefore && !r.HasAfter)
        || ((!r.HasBefore || r.BeforeSize >= 0) && (!r.HasAfter || r.AfterSize >= 0) && r.ValueAt >= _valuesNext - _values.Length);

    private ReadOnlySpan<byte> Value(long at, int size)
    {
        if (size <= 0 || at < _valuesNext - _values.Length) return default;
        return _values.AsSpan((int)(at % _values.Length), size);
    }

    // ── запись (поток симуляции) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Мир включил журнал: таблица записи столбцов — по его типам и регистрациям.</summary>
    internal void Attach(int types, int registrations)
    {
        _registrations = registrations + 1;
        if (_writes.Length != types * _registrations) _writes = new ulong[types * _registrations];
        else Array.Clear(_writes);
    }

    internal CauseId Append(CauseKind kind, CauseId parent, ulong tick, int stage, int registration, Entity entity, int type, string? file,
        int line, string? note, long arg)
    {
        long seq = _next++;
        int id = (int)(seq & int.MaxValue);
        if (id == 0)
        {
            seq = _next++;
            id = (int)(seq & int.MaxValue);
        }
        _ring[id & _mask] = new CauseRecord
        {
            Id = new CauseId(id), Parent = parent, Tick = tick, Stage = stage, Registration = registration, Kind = kind, Entity = entity,
            Type = type, File = file is { Length: > 0 } ? file : null, Line = line, Note = note, Arg = arg, BeforeSize = -1, AfterSize = -1,
        };
        return new CauseId(id);
    }

    /// <summary>Сущность записи стала известна на барьере (черновик → живая).</summary>
    internal void Resolve(CauseId id, Entity entity)
    {
        ref var r = ref _ring[id.Value & _mask];
        if (id.Value > 0 && r.Id == id) r = r with { Entity = entity };
    }

    /// <summary>Значения записи на барьере: до (<paramref name="hasBefore"/>) и после (<paramref name="hasAfter"/>).</summary>
    internal void Values(CauseId id, ReadOnlySpan<byte> before, bool hasBefore, ReadOnlySpan<byte> after, bool hasAfter)
    {
        ref var r = ref _ring[id.Value & _mask];
        if (id.Value <= 0 || r.Id != id) return;
        int b = hasBefore ? before.Length : 0, a = hasAfter ? after.Length : 0;
        if (b > MaxValueBytes || a > MaxValueBytes)
        {
            r = r with { HasBefore = hasBefore, HasAfter = hasAfter };   // значение есть, но не хранится
            return;
        }
        long at = Reserve(b + a);
        var dst = _values.AsSpan((int)(at % _values.Length), b + a);
        if (hasBefore) before.CopyTo(dst);
        if (hasAfter) after.CopyTo(dst[b..]);
        r = r with { HasBefore = hasBefore, HasAfter = hasAfter, ValueAt = at, BeforeSize = hasBefore ? b : -1, AfterSize = hasAfter ? a : -1 };
    }

    /// <summary>Место под значения подряд: не помещается до конца кольца — с начала (хвост пропадает).</summary>
    private long Reserve(int n)
    {
        long at = _valuesNext;
        int offset = (int)(at % _values.Length);
        if (offset + n > _values.Length) at += _values.Length - offset;
        _valuesNext = at + n;
        return at;
    }

    /// <summary>Регистрация <paramref name="registration"/> пишет колонку типа <paramref name="type"/> на месте в тике (грубо: столбец, не сущность).</summary>
    internal void NoteWrite(int type, int registration, ulong tick)
    {
        int i = type * _registrations + registration;
        if ((uint)i < (uint)_writes.Length) _writes[i] = tick + 1;
    }

    /// <summary>Последний тик, в котором регистрация писала колонку компонента на месте (с включения журнала); null — не писала.</summary>
    public ulong? LastWrite(int type, int registration)
    {
        int i = type * _registrations + registration;
        return (uint)i < (uint)_writes.Length && _writes[i] > 0 ? _writes[i] - 1 : null;
    }

    // ── побочная таблица связей ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Запомнить причину для непрерывного производителя: по (сущность, канал) — последняя причина и её ключ (у начала удара —
    /// <c>AttackSerial</c>). Таблица ограничена (<see cref="LinkSlots"/>): переполнение вытесняет — <see cref="Linked"/> тогда пусто.
    /// Только диагностика: в хеш и поведение не входит.
    /// </summary>
    public void Link(Entity entity, int channel, CauseId cause, long key = 0)
    {
        int home = Home(entity, channel);
        int free = -1;
        for (int k = 0; k < LinkProbe; k++)
        {
            int i = (home + k) & (LinkSlots - 1);
            if (_linkEntity[i] == entity && _linkChannel[i] == channel)
            {
                Put(i, entity, channel, cause, key);
                return;
            }
            if (free < 0 && _linkCause[i].Value == 0) free = i;
        }
        Put(free >= 0 ? free : home, entity, channel, cause, key);
    }

    /// <summary>Причина, запомненная <see cref="Link"/> для (сущность, канал) с этим ключом; нет или ключ другой — пусто.</summary>
    public CauseId Linked(Entity entity, int channel, long key = 0)
    {
        int home = Home(entity, channel);
        for (int k = 0; k < LinkProbe; k++)
        {
            int i = (home + k) & (LinkSlots - 1);
            if (_linkEntity[i] == entity && _linkChannel[i] == channel && _linkCause[i].Value != 0)
                return _linkKey[i] == key ? _linkCause[i] : CauseId.None;
        }
        return CauseId.None;
    }

    private void Put(int i, Entity entity, int channel, CauseId cause, long key)
    {
        _linkEntity[i] = entity;
        _linkChannel[i] = channel;
        _linkKey[i] = key;
        _linkCause[i] = cause;
    }

    private static int Home(Entity e, int channel) =>
        (int)((uint)(e.Index * -1640531535 ^ e.Generation * 40503 ^ channel * 97) & (LinkSlots - 1));
}
