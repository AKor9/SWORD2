namespace Sword.Engine.Ecs;

/// <summary>Журнал без типа записи — для обслуживания на барьере и описания.</summary>
public abstract class JournalBase
{
    /// <summary>Тип записи (<c>Added&lt;T&gt;</c>, <c>Event&lt;E&gt;</c>…).</summary>
    public abstract Type EntryType { get; }

    /// <summary>Номер следующей записи — конец журнала для курсоров.</summary>
    public abstract long End { get; }

    /// <summary>Записей в журнале сейчас.</summary>
    public abstract int Count { get; }

    /// <summary>Убрать записи, сделанные на барьере <paramref name="barrier"/> или раньше.</summary>
    internal abstract void TrimUpTo(long barrier);
}

/// <summary>
/// Журнал срабатываний одного вида: записи по порядку барьеров, у каждой — номер (для курсоров) и барьер, на котором
/// она сделана. Запись живёт один полный оборот тика: к следующему барьеру той же стадии её прочли все потребители
/// (раздел 5 плана); что нужно дольше — потребитель копирует в своё состояние. У каждого потребителя свой курсор —
/// номер первой непрочитанной записи; каждую запись он видит ровно один раз.
/// </summary>
public sealed class Journal<T> : JournalBase where T : struct
{
    private T[] _entries = new T[16];
    private long[] _barriers = new long[16];
    private int _start, _count;
    private long _firstSequence;

    public override Type EntryType => typeof(T);
    public override long End => _firstSequence + _count;
    public override int Count => _count;

    internal void Append(in T entry, long barrier)
    {
        if (_start + _count == _entries.Length) Grow();
        _entries[_start + _count] = entry;
        _barriers[_start + _count] = barrier;
        _count++;
    }

    /// <summary>Записи с номера <paramref name="cursor"/> до конца (всё, что потребитель ещё не видел).</summary>
    public ReadOnlySpan<T> Since(long cursor)
    {
        long from = Math.Max(cursor, _firstSequence);
        int skip = (int)(from - _firstSequence);
        return skip >= _count ? [] : _entries.AsSpan(_start + skip, _count - skip);
    }

    internal override void TrimUpTo(long barrier)
    {
        int n = 0;
        while (n < _count && _barriers[_start + n] <= barrier) n++;
        if (n == 0) return;
        _start += n;
        _count -= n;
        _firstSequence += n;
        if (_count == 0) _start = 0;
    }

    private void Grow()
    {
        // Сначала сдвиг к началу: память не растёт, пока записей за оборот тика не больше ёмкости.
        if (_start > 0 && _count < _entries.Length / 2)
        {
            Array.Copy(_entries, _start, _entries, 0, _count);
            Array.Copy(_barriers, _start, _barriers, 0, _count);
            _start = 0;
            return;
        }
        var entries = new T[_entries.Length * 2];
        var barriers = new long[_barriers.Length * 2];
        Array.Copy(_entries, _start, entries, 0, _count);
        Array.Copy(_barriers, _start, barriers, 0, _count);
        _entries = entries;
        _barriers = barriers;
        _start = 0;
    }
}
