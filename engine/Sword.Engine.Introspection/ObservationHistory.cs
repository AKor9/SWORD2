using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// История наблюдений (раздел 8 плана, <c>ObservationHistory</c>): ограниченное кольцо последних тиков — записи журналов
/// мира каждого тика (события, команды, срабатывания <c>Added/Removed/Changed</c>) с тиком, стадией и байтами. Копия
/// снимается после тика (<see cref="Capture"/>) прямо из журналов по возрасту записей — без своего курсора и подписки:
/// мир, его журналы и хеш состояния от истории не зависят. Видны журналы, которые в мире есть: событие без потребителя
/// журнала не имеет и в историю не попадает. Нарушения инвариантов тика (<see cref="AddViolation"/>) — рядом с записями
/// журналов. Живёт на потоке симуляции; наружу — копиями в точке обслуживания.
/// </summary>
public sealed class ObservationHistory
{
    /// <summary>По умолчанию — 1200 тиков (10 с при 120 Гц).</summary>
    public const int DefaultTicks = 1200;

    private sealed class Slot
    {
        public ulong Tick;
        public byte[] Data = new byte[256];
        public int Bytes;
        public Rec[] Records = new Rec[16];
        public int Count;
        public InvariantViolation[] Violations = [];
        public int ViolationCount;
    }

    private readonly record struct Rec(int Journal, int Stage, int Offset, int Length);

    private readonly Slot[] _slots;
    private ulong _oldest, _newest;
    private bool _any;
    private readonly List<InvariantViolation> _pending = [];

    public ObservationHistory(int ticks = DefaultTicks)
    {
        if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "история — хотя бы 1 тик");
        _slots = new Slot[ticks];
        for (int i = 0; i < ticks; i++) _slots[i] = new Slot();
    }

    /// <summary>Сколько тиков держит кольцо.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Самый старый и самый новый тик в истории (номер тика — как у команд: тик, в котором запись сделана); null — пусто.</summary>
    public ulong? Oldest => _any ? _oldest : null;
    public ulong? Newest => _any ? _newest : null;

    /// <summary>Журналы мира, к которому привязана история (номер записи — индекс здесь).</summary>
    public IReadOnlyList<JournalInfo> Journals { get; private set; } = [];

    /// <summary>Стадии мира (номер стадии записи — индекс здесь).</summary>
    public IReadOnlyList<string> Stages { get; private set; } = [];

    /// <summary>Новый мир (смена симуляции): история очищается.</summary>
    public void Reset(WorldReader world)
    {
        _any = false;
        _pending.Clear();
        Journals = world.Journals;
        Stages = world.Stages;
    }

    /// <summary>
    /// После тика <paramref name="tick"/> (номер сыгранного тика): записи журналов этого тика — возраст 1…число стадий
    /// (запись стадии s сделана на её барьере; немедленные операции перед тиком — наполнение мира, время тика — попадают
    /// в первую стадию). Разрыв в номерах (перемотка) начинает историю заново.
    /// </summary>
    public void Capture(WorldReader world, ulong tick)
    {
        int stages = world.Stages.Count;
        if (_any && tick != _newest + 1) _any = false;
        var slot = _slots[tick % (ulong)_slots.Length];
        slot.Tick = tick;
        slot.Bytes = 0;
        slot.Count = 0;
        slot.ViolationCount = 0;
        foreach (var v in _pending)
        {
            if (v.Tick != tick) continue;
            if (slot.ViolationCount == slot.Violations.Length) Array.Resize(ref slot.Violations, Math.Max(4, slot.Violations.Length * 2));
            slot.Violations[slot.ViolationCount++] = v;
        }
        _pending.Clear();
        for (int j = 0, journals = Journals.Count; j < journals; j++)
            for (int i = 0, n = world.JournalLength(j); i < n; i++)
            {
                var bytes = world.JournalEntry(j, i, out long age);
                if (age < 1 || age > stages) continue;
                Append(slot, j, stages - (int)age, bytes);
            }
        if (!_any)
        {
            _oldest = tick;
            _any = true;
        }
        _newest = tick;
        if (_newest - _oldest >= (ulong)_slots.Length) _oldest = _newest - (ulong)_slots.Length + 1;
    }

    private static void Append(Slot slot, int journal, int stage, ReadOnlySpan<byte> bytes)
    {
        if (slot.Bytes + bytes.Length > slot.Data.Length) Array.Resize(ref slot.Data, Math.Max(slot.Data.Length * 2, slot.Bytes + bytes.Length));
        if (slot.Count == slot.Records.Length) Array.Resize(ref slot.Records, slot.Records.Length * 2);
        bytes.CopyTo(slot.Data.AsSpan(slot.Bytes));
        slot.Records[slot.Count++] = new Rec(journal, stage, slot.Bytes, bytes.Length);
        slot.Bytes += bytes.Length;
    }

    /// <summary>Нарушение инварианта идущего тика (поток симуляции, после барьера): в историю — вместе с тиком (<see cref="Capture"/>).</summary>
    public void AddViolation(in InvariantViolation violation) => _pending.Add(violation);

    /// <summary>Нарушения идущего тика, ещё не снятые в историю (посреди тика — до этой стадии).</summary>
    public IReadOnlyList<InvariantViolation> Pending => _pending;

    /// <summary>Нарушения инвариантов тика (нет тика в истории — пусто).</summary>
    public ReadOnlySpan<InvariantViolation> ViolationsAt(ulong tick)
    {
        if (!Has(tick)) return [];
        var slot = _slots[tick % (ulong)_slots.Length];
        return slot.Violations.AsSpan(0, slot.ViolationCount);
    }

    /// <summary>Тик <paramref name="tick"/> есть в истории.</summary>
    public bool Has(ulong tick) => _any && tick >= _oldest && tick <= _newest;

    /// <summary>Записей тика (нет тика в истории — 0).</summary>
    public int CountAt(ulong tick) => Has(tick) ? _slots[tick % (ulong)_slots.Length].Count : 0;

    /// <summary>Запись <paramref name="i"/> тика: журнал, стадия и байты (у записи со ссылками — пусто). Годна до следующего тика.</summary>
    public (int Journal, int Stage) RecordAt(ulong tick, int i, out ReadOnlySpan<byte> bytes)
    {
        if (!Has(tick)) throw new ArgumentOutOfRangeException(nameof(tick), tick, "тика нет в истории");
        var slot = _slots[tick % (ulong)_slots.Length];
        if ((uint)i >= (uint)slot.Count) throw new ArgumentOutOfRangeException(nameof(i));
        var r = slot.Records[i];
        bytes = slot.Data.AsSpan(r.Offset, r.Length);
        return (r.Journal, r.Stage);
    }
}
