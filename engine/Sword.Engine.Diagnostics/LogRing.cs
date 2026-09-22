namespace Sword.Engine.Diagnostics;

/// <summary>Запись кольца журнала: номер по порядку (с 1) и сама запись.</summary>
public readonly record struct LogRecord(long Sequence, LogEntry Entry);

/// <summary>
/// Кольцо последних записей журнала — для агента (<c>log.tail</c>) и инструментов: приёмник копит не больше
/// <see cref="Capacity"/> записей с номерами по порядку; читатель просит записи после номера и узнаёт, сколько он
/// пропустил (вытеснены). Потокобезопасно: пишет любой поток, читает любой.
/// </summary>
public sealed class LogRing(int capacity = 2048) : ILogSink
{
    private readonly LogEntry[] _entries = new LogEntry[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private readonly object _gate = new();
    private long _written;

    public int Capacity => _entries.Length;

    /// <summary>Номер последней записи (0 — записей не было).</summary>
    public long Last
    {
        get
        {
            lock (_gate) return _written;
        }
    }

    public void Write(in LogEntry entry)
    {
        lock (_gate)
        {
            _entries[_written % _entries.Length] = entry;
            _written++;
        }
    }

    /// <summary>
    /// Записи с номером больше <paramref name="after"/> (не больше <paramref name="limit"/>, с уровня
    /// <paramref name="minimum"/>, категории <paramref name="category"/> — null: любой) — в порядке записи.
    /// <paramref name="missed"/> — сколько записей после <paramref name="after"/> уже вытеснено из кольца.
    /// </summary>
    public List<LogRecord> Tail(long after, int limit, LogLevel minimum, string? category, out long missed)
    {
        var result = new List<LogRecord>();
        lock (_gate)
        {
            if (after > _written) after = 0;   // номер из другого процесса (журнал начат заново): читать с начала кольца
            long first = Math.Max(_written - _entries.Length, 0) + 1;   // номер самой старой записи в кольце
            long from = Math.Max(after + 1, first);
            missed = Math.Max(first - (after + 1), 0);
            for (long seq = from; seq <= _written && result.Count < limit; seq++)
            {
                var e = _entries[(seq - 1) % _entries.Length];
                if (e.Level < minimum || (category is not null && !string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new LogRecord(seq, e));
            }
        }
        return result;
    }

    /// <summary>
    /// Последние <paramref name="limit"/> записей с уровня <paramref name="minimum"/> и категории
    /// <paramref name="category"/> (null — любой) среди всех, что ещё в кольце, — в порядке записи.
    /// <paramref name="last"/> — номер последней записи кольца в этот момент (продолжение — после него).
    /// </summary>
    public List<LogRecord> Latest(int limit, LogLevel minimum, string? category, out long last)
    {
        var result = new List<LogRecord>();
        lock (_gate)
        {
            last = _written;
            long first = Math.Max(_written - _entries.Length, 0) + 1;
            for (long seq = _written; seq >= first && result.Count < limit; seq--)
            {
                var e = _entries[(seq - 1) % _entries.Length];
                if (e.Level < minimum || (category is not null && !string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new LogRecord(seq, e));
            }
        }
        result.Reverse();
        return result;
    }
}
