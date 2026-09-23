using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Лента потока-владельца профайлера для привязки событий среды (диагностический режим, точка Пб): начало и конец каждой
/// зоны, открытие и закрытие кадра и тика — время <see cref="Stopwatch"/>, номер зоны, глубина стека после записи. Пишет
/// только владелец (запись — 24 байта в заранее выделенный массив и публикация счётчика), читает поток слушателя событий
/// (<see cref="Find"/>): событие приходит с задержкой (~15 мс), его место — по времени в ленте. Кольцо: старое затирается;
/// событие старше ленты — «опоздало» (<see cref="TimelinePlace.Late"/>).
/// </summary>
public sealed class ProfileTimeline
{
    /// <summary>Записей по умолчанию (12 МБ): ~0,25 с кадров App при ~400 записях на кадр и 2700 кадрах/с — с запасом над задержкой событий.</summary>
    public const int DefaultCapacity = 1 << 19;

    internal enum Kind : short
    {
        Begin = 1,
        End,
        FrameOpen,
        FrameClose,
        TickOpen,
        TickClose,
        Depth,
    }

    private struct Entry
    {
        public long Time, Arg;
        public int Zone;
        public short Depth, Kind;
    }

    private readonly Entry[] _entries;
    private readonly long _mask;
    private long _written;

    public ProfileTimeline(int capacity = DefaultCapacity)
    {
        if (capacity < 1024 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity), "степень двойки от 1024");
        _entries = new Entry[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _entries.Length;

    /// <summary>Записано всего (с кругами кольца).</summary>
    public long Written => Volatile.Read(ref _written);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(long time, int zone, int depth, Kind kind, long arg = 0)
    {
        long n = _written;
        ref var e = ref _entries[n & _mask];
        e.Time = time;
        e.Arg = arg;
        e.Zone = zone;
        e.Depth = (short)depth;
        e.Kind = (short)kind;
        Volatile.Write(ref _written, n + 1);
    }

    /// <summary>
    /// Где был поток-владелец в момент <paramref name="time"/>: кадр (или «между кадрами после N»), тик, путь зон (номера, от
    /// корня). Читает с другого потока: записи, которые владелец мог затереть за время чтения, не используются (запас — 1/8
    /// кольца), и после чтения проверяется, что их не затёрли.
    /// </summary>
    public TimelinePlace Find(long time)
    {
        long w = Written;
        if (w == 0) return TimelinePlace.Unknown;
        long lo = Math.Max(0, w - _entries.Length + _entries.Length / 8);
        // Раньше начала ленты — событие до включения (место неизвестно); раньше её затёртого края — опоздало.
        if (At(lo).Time > time) return lo == 0 ? TimelinePlace.Unknown : TimelinePlace.Late;
        // последняя запись не позже time
        long a = lo, b = w - 1;
        while (a < b)
        {
            long m = (a + b + 1) / 2;
            if (At(m).Time <= time) a = m;
            else b = m - 1;
        }
        long k = a, lowest = k;
        int depth = At(k).Depth;
        var path = new int[Math.Max(0, depth)];
        long cursor = k;
        for (int d = depth; d >= 1; d--)
        {
            while (cursor >= lo && !(At(cursor).Kind == (short)Kind.Begin && At(cursor).Depth == d)) cursor--;
            if (cursor < lo)
            {
                path = path[d..];   // начало пути — за краем ленты
                break;
            }
            path[d - 1] = At(cursor).Zone;
            cursor--;
        }
        lowest = Math.Min(lowest, Math.Max(lo, cursor));
        ulong? frame = null, afterFrame = null, tick = null;
        bool frameKnown = false, tickKnown = false;
        // Назад — до ближайших отметок кадра и тика. Поиск ограничен одним кадром или тиком: без этих остановок лента без
        // кадров (Headless) или кадры без тиков (App на паузе) читались бы до края — ~0,5 мс и 11 МБ на событие.
        for (long i = k; i >= lo && !(frameKnown && tickKnown); i--)
        {
            var e = At(i);
            if (!frameKnown && e.Kind is (short)Kind.FrameOpen or (short)Kind.FrameClose)
            {
                frameKnown = true;
                if (e.Kind == (short)Kind.FrameOpen) frame = (ulong)e.Arg;
                else afterFrame = (ulong)e.Arg;
                // Тик внутри кадра был бы позже этой отметки — его нет: событие не в тике.
                tickKnown = true;
            }
            if (!tickKnown && e.Kind is (short)Kind.TickOpen or (short)Kind.TickClose)
            {
                tickKnown = true;
                if (e.Kind == (short)Kind.TickOpen) tick = (ulong)e.Arg;
                // Отметка тика на глубине 0 — тик вне кадра (Headless, шаги агента при свёрнутом окне): кадра вокруг нет.
                if (e.Depth == 0) frameKnown = true;
            }
            lowest = i;
        }
        // Владелец мог затереть прочитанное за время чтения — тогда место неизвестно.
        if (Written - _entries.Length > lowest) return TimelinePlace.Unknown;
        return new TimelinePlace(true, false, frame, afterFrame, tick, path);
    }

    private ref readonly Entry At(long i) => ref _entries[i & _mask];
}

/// <summary>
/// Место события в ленте владельца: известно ли (лента есть и событие не старше её), кадр (или «после кадра N» — между
/// кадрами), тик, путь зон от корня (номера профайлера; последняя — самая внутренняя).
/// </summary>
public readonly record struct TimelinePlace(bool Known, bool IsLate, ulong? Frame, ulong? AfterFrame, ulong? Tick, int[] Path)
{
    public static readonly TimelinePlace Unknown = new(false, false, null, null, null, []);
    public static readonly TimelinePlace Late = new(false, true, null, null, null, []);
}

/// <summary>Событие среды с местом (диагностический режим): выделение, сборка, пауза, компиляция.</summary>
public sealed record RuntimeEvent
{
    /// <summary><c>alloc</c> — выборка выделений (<c>GCAllocationTick</c>: ~каждые 100 КБ, тип выделенного на выборке), <c>gc</c> —
    /// сборка (начало — конец), <c>pause</c> — остановка потоков (<c>SuspendEE</c> — <c>RestartEE</c>), <c>jit</c> — компиляция метода.</summary>
    public required string Kind { get; init; }

    /// <summary>Время начала, отметка <see cref="Stopwatch"/>.</summary>
    public long Time { get; init; }

    /// <summary>Длительность, мс (сборка, пауза, компиляция); выборка выделений — 0.</summary>
    public double Ms { get; init; }

    /// <summary>Поток ОС и главный ли он (поток-владелец профайлера).</summary>
    public uint Thread { get; init; }
    public bool Main { get; init; }

    /// <summary>Тип (выделение), метод (JIT), причина (сборка, пауза).</summary>
    public string Name { get; init; } = "";

    /// <summary>Байты выборки выделений (выделено с прошлой выборки — приписываются типу этой).</summary>
    public long Bytes { get; init; }

    /// <summary>Поколение сборки; −1 — не сборка.</summary>
    public int Gen { get; init; } = -1;

    /// <summary>Уровень JIT: tier0, tier0-instrumented, tier1, tier1-instrumented, osr, optimized, minopts.</summary>
    public string? Tier { get; init; }

    public TimelinePlace Place { get; init; }

    /// <summary>Самая внутренняя зона и путь зон именами (<c>frame > frame.simulate > tick > …</c>).</summary>
    public string? Zone { get; init; }
    public string? PathText { get; init; }
}

/// <summary>
/// События среды .NET в диагностическом режиме (точка Пб): <see cref="EventListener"/> на <c>Microsoft-Windows-DotNETRuntime</c>
/// — выделения по типам (<c>GCAllocationTick</c>), сборки (<c>GCStart/GCEnd</c>: поколение, причина), паузы
/// (<c>GCSuspendEEBegin</c> — <c>GCRestartEEEnd</c>), JIT (<c>MethodJittingStarted</c> — <c>MethodLoadVerbose</c>: метод,
/// уровень — tier0, tier1, OSR, фоновый ли поток). Место события — по времени и потоку ОС в ленте владельца
/// (<see cref="ProfileTimeline"/>): кадр, тик, зона.
///
/// Цена и оговорки: выключен по умолчанию; подписка на JIT уровня Verbose удорожает каждую компиляцию (имена методов в
/// событиях); события приходят на поток слушателя с задержкой ~15 мс (EventPipe), поэтому место ищется по ленте, а отчёт
/// перед записью ждёт хвост (<see cref="Flush"/>). Время события — в часах сеанса EventPipe (<see cref="DateTime"/>): сдвиг к
/// <see cref="Stopwatch"/> меряется при старте — компиляцией своего метода между двумя отметками (точность ~мкс); без
/// динамического кода — приблизительно (~0,5 мс). Выделения слушателя — на его потоке (главный поток не выделяет), но его
/// сборки видны как сборки процесса — наблюдатель меняет наблюдаемое.
/// </summary>
public sealed class RuntimeEvents : IDisposable
{
    public const string SourceName = "Microsoft-Windows-DotNETRuntime";

    /// <summary>Ключевые слова: GC (0x1) и JIT (0x10), уровень Verbose (<c>GCAllocationTick</c>, <c>MethodJittingStarted</c>, <c>MethodLoadVerbose</c>).</summary>
    public const long Keywords = 0x1 | 0x10;

    /// <summary>Последних событий в кольце (итоги по типам и уровням — за всё время с <see cref="Reset"/>).</summary>
    public const int DefaultCapacity = 4096;

    private const string MarkPrefix = "sword.profile.mark.";

    private readonly Listener _listener;
    private readonly ProfileTimeline? _timeline;
    private readonly Func<int, string> _zoneName;
    private readonly uint _mainThread;
    private readonly object _gate = new();
    private readonly RuntimeEvent[] _ring;
    private int _next, _filled;

    // Сдвиг времени: DateTime события → отметка Stopwatch.
    private readonly DateTime _syncUtc;
    private readonly long _syncTs;
    private long _correction;
    private bool _calibrated;
    private readonly List<EventWrittenEventArgs> _pending = [];
    private static int _marks;
    private int _calibrations;
    private long _lower = long.MinValue, _upper = long.MaxValue;

    /// <summary>Метка: отметки до и после вызова (компиляция — между ними), пришедшее начало компиляции, кого разбудить.</summary>
    private sealed class MarkInfo(ManualResetEventSlim? seen)
    {
        public long Before, After = long.MaxValue;
        public long? Started;
        public readonly ManualResetEventSlim? Seen = seen;
    }

    /// <summary>Метки, ещё не пришедшие событиями (конец компиляции).</summary>
    private readonly Dictionary<string, MarkInfo> _waits = [];
    private volatile bool _disposed;
    private uint _listenerThread;

    // Пары начало — конец.
    private readonly Dictionary<ulong, (long Ts, uint Thread)> _jitStarts = [];
    private (long Ts, uint Thread, string Reason)? _suspend;
    private readonly Dictionary<long, (long Ts, int Gen, string Reason, uint Thread)> _gcStarts = [];

    // Итоги с Reset.
    private readonly Dictionary<string, TypeStats> _types = new(StringComparer.Ordinal);
    private long _received, _late, _unknown, _allocSamples, _allocBytes, _jitCount, _jitMain, _gcCount, _pauses;
    private double _jitMs, _jitMainMs, _pauseMs, _maxPauseMs;
    private readonly Dictionary<string, int> _tiers = new(StringComparer.Ordinal);

    private sealed class TypeStats
    {
        public long Samples, Bytes, Main;
        public readonly Dictionary<string, long> Zones = new(StringComparer.Ordinal);
    }

    /// <param name="timeline">Лента владельца (места событий); null — событий без мест.</param>
    /// <param name="zoneName">Имя зоны по номеру (потокобезопасно).</param>
    /// <param name="mainThread">Поток ОС владельца.</param>
    public RuntimeEvents(ProfileTimeline? timeline, Func<int, string> zoneName, uint mainThread, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _timeline = timeline;
        _zoneName = zoneName;
        _mainThread = mainThread;
        _ring = new RuntimeEvent[capacity];
        _syncUtc = DateTime.UtcNow;
        _syncTs = Stopwatch.GetTimestamp();
        _listener = new Listener();
        _listener.Start(this);
        Calibrate();
    }

    /// <summary>Сдвиг часов измерен компиляцией своего метода (иначе — события ждут в <c>pending</c>).</summary>
    public bool Calibrated
    {
        get
        {
            lock (_gate) return _calibrated;
        }
    }

    /// <summary>
    /// Подождать хвост: скомпилировать свой метод-метку и дождаться его события (всё, что владелец сделал раньше, уже пришло).
    /// false — не дождались за <paramref name="timeout"/>.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || _disposed) return false;
        using var seen = new ManualResetEventSlim();
        string name = Mark(seen);
        bool ok = seen.Wait(timeout);
        lock (_gate) _waits.Remove(name);
        return ok;
    }

    /// <summary>Меток калибровки, пришедших целиком (каждая сужает сдвиг часов).</summary>
    public int Calibrations
    {
        get
        {
            lock (_gate) return _calibrations;
        }
    }

    /// <summary>Граница сдвига часов, мкс: насколько событие может стоять позже настоящего (ширина окна последней оценки).</summary>
    public double CalibrationSpreadUs
    {
        get
        {
            lock (_gate) return _calibrations == 0 || _upper == long.MaxValue ? double.NaN : Math.Max(0, _upper - _lower) * 1e6 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// Калибровка часов — в фоне: сеанс EventPipe начинает принимать события не сразу после подписки, поэтому метки — каждые
    /// 20 мс (до ~2 с), пока не придут 8. Сдвиг: у метки начало компиляции — после отметки «до вызова», конец — до отметки
    /// «после»; значит сдвиг не меньше (конец − после) и не больше (начало − до); каждая метка сужает окно, берётся нижняя
    /// граница (после компиляции до возврата — единицы микросекунд, до неё — десятки: пролог вызова). События до первой метки
    /// ждут в <c>pending</c>. Метки компилирует фоновый поток — главный не трогается; методов JIT процесса больше на число меток.
    /// </summary>
    private void Calibrate()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            lock (_gate)
            {
                _calibrated = true;   // без динамического кода — приблизительно, по DateTime.UtcNow (~0,5 мс)
                ProcessPending();
            }
            return;
        }
        var thread = new Thread(() =>
        {
            for (int i = 0; i < 100 && !_disposed && Calibrations < 8; i++)
            {
                Mark(null);
                Thread.Sleep(20);
            }
        })
        { IsBackground = true, Name = "sword.profile.calibrate" };
        thread.Start();
    }

    /// <summary>Скомпилировать новый метод-метку: JIT — на этом потоке, между отметками «до» и «после» вызова.</summary>
    private string Mark(ManualResetEventSlim? seen)
    {
        string name = MarkPrefix + Interlocked.Increment(ref _marks);
        var method = new DynamicMethod(name, typeof(int), Type.EmptyTypes, typeof(RuntimeEvents).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        var call = method.CreateDelegate<Func<int>>();
        var info = new MarkInfo(seen);
        lock (_gate) _waits[name] = info;
        long before = Stopwatch.GetTimestamp();
        call();
        long after = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            info.Before = before;
            info.After = after;
        }
        return name;
    }

    /// <summary>Время события в часах сеанса → отметка <see cref="Stopwatch"/> без поправки.</summary>
    private long Raw(DateTime utc) => _syncTs + (long)((utc - _syncUtc).Ticks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));

    private long ToStopwatch(DateTime utc) => Raw(utc) - _correction;

    /// <summary>Событие метки (начало или конец её компиляции); true — это была метка.</summary>
    private bool OnMark(EventWrittenEventArgs e)
    {
        string? name = e.EventName;
        bool start = name?.StartsWith("MethodJittingStarted", StringComparison.Ordinal) == true;
        if (!start && name?.StartsWith("MethodLoadVerbose", StringComparison.Ordinal) != true) return false;
        if (Payload(e, "MethodName") is not string m || !m.StartsWith(MarkPrefix, StringComparison.Ordinal)) return false;
        if (!_waits.TryGetValue(m, out var info)) return true;
        long raw = Raw(e.TimeStamp);
        if (start)
        {
            info.Started = raw;
            return true;
        }
        // Конец компиляции: окно сдвига [конец − после, начало − до] (события метки приходят позже возврата вызова — отметки уже есть).
        if (info.After != long.MaxValue && info.Started is { } started)
        {
            _lower = Math.Max(_lower, raw - info.After);
            _upper = Math.Min(_upper, started - info.Before);
            _correction = _lower;
            _calibrations++;
            if (!_calibrated)
            {
                _calibrated = true;
                ProcessPending();
            }
        }
        _waits.Remove(m);
        info.Seen?.Set();
        return true;
    }

    // ── поток слушателя ───────────────────────────────────────────────────

    private void OnEvent(EventWrittenEventArgs e)
    {
        lock (_gate)
        {
            if (_listenerThread == 0) _listenerThread = Machine.CurrentThreadId();   // поток слушателя: его выделения — наблюдателя
            // Свои метки: калибровка часов и хвост для Flush — в события не идут.
            if (OnMark(e)) return;
            if (!_calibrated)
            {
                if (_pending.Count < 1 << 16) _pending.Add(e);
                return;
            }
            Handle(e);
        }
    }

    private void ProcessPending()
    {
        foreach (var p in _pending) Handle(p);
        _pending.Clear();
    }

    private void Handle(EventWrittenEventArgs e)
    {
        string? name = e.EventName;
        if (name is null) return;
        long ts = ToStopwatch(e.TimeStamp);
        uint thread = (uint)e.OSThreadId;
        if (name.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            _received++;
            long amount = Convert.ToInt64(Payload(e, "AllocationAmount64") ?? Payload(e, "AllocationAmount") ?? 0L);
            string type = Payload(e, "TypeName") as string ?? "?";
            var ev = Place(new RuntimeEvent { Kind = "alloc", Time = ts, Thread = thread, Main = thread == _mainThread, Name = type, Bytes = amount });
            _allocSamples++;
            _allocBytes += amount;
            if (!_types.TryGetValue(type, out var stats)) _types[type] = stats = new TypeStats();
            stats.Samples++;
            stats.Bytes += amount;
            if (ev.Main) stats.Main++;
            string zone = thread == _listenerThread ? "(слушатель событий — наблюдатель)"
                : !ev.Main ? "(другие потоки)" : ev.Zone ?? (ev.Place.IsLate ? "(опоздало)" : "(вне зон)");
            stats.Zones[zone] = stats.Zones.GetValueOrDefault(zone) + amount;
            Add(ev);
        }
        else if (name.StartsWith("GCStart", StringComparison.Ordinal))
        {
            long count = Convert.ToInt64(Payload(e, "Count") ?? 0);
            _gcStarts[count] = (ts, Convert.ToInt32(Payload(e, "Depth") ?? -1), GcReason(Convert.ToInt32(Payload(e, "Reason") ?? -1)), thread);
        }
        else if (name.StartsWith("GCEnd", StringComparison.Ordinal))
        {
            long count = Convert.ToInt64(Payload(e, "Count") ?? 0);
            if (!_gcStarts.Remove(count, out var s)) return;
            _received++;
            _gcCount++;
            Add(Place(new RuntimeEvent
            {
                Kind = "gc", Time = s.Ts, Ms = Ms(ts - s.Ts), Thread = s.Thread, Main = s.Thread == _mainThread, Name = s.Reason, Gen = s.Gen,
            }));
        }
        else if (name.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal))
            _suspend = (ts, thread, SuspendReason(Convert.ToInt32(Payload(e, "Reason") ?? -1)));
        else if (name.StartsWith("GCRestartEEEnd", StringComparison.Ordinal))
        {
            if (_suspend is not { } s) return;
            _suspend = null;
            _received++;
            _pauses++;
            double ms = Ms(ts - s.Ts);
            _pauseMs += ms;
            _maxPauseMs = Math.Max(_maxPauseMs, ms);
            Add(Place(new RuntimeEvent { Kind = "pause", Time = s.Ts, Ms = ms, Thread = s.Thread, Main = s.Thread == _mainThread, Name = s.Reason }));
        }
        else if (name.StartsWith("MethodJittingStarted", StringComparison.Ordinal))
        {
            if (Payload(e, "MethodName") is string m && m.StartsWith(MarkPrefix, StringComparison.Ordinal)) return;
            _jitStarts[Convert.ToUInt64(Payload(e, "MethodID") ?? 0UL)] = (ts, thread);
        }
        else if (name.StartsWith("MethodLoadVerbose", StringComparison.Ordinal))
        {
            ulong id = Convert.ToUInt64(Payload(e, "MethodID") ?? 0UL);
            uint flags = Convert.ToUInt32(Payload(e, "MethodFlags") ?? 0u);
            string method = $"{Payload(e, "MethodNamespace")}::{Payload(e, "MethodName")}";
            if (method.Contains(MarkPrefix, StringComparison.Ordinal)) return;
            bool started = _jitStarts.Remove(id, out var s);
            if (!started && (flags & 0x8) == 0) return;   // не JIT (готовый код R2R)
            if (!started) s = (ts, thread);
            _received++;
            string tier = Tier(flags);
            double ms = Ms(ts - s.Ts);
            _jitCount++;
            _jitMs += ms;
            if (s.Thread == _mainThread)
            {
                _jitMain++;
                _jitMainMs += ms;
            }
            _tiers[tier] = _tiers.GetValueOrDefault(tier) + 1;
            Add(Place(new RuntimeEvent { Kind = "jit", Time = s.Ts, Ms = ms, Thread = s.Thread, Main = s.Thread == _mainThread, Name = method, Tier = tier }));
        }
    }

    private RuntimeEvent Place(RuntimeEvent ev)
    {
        if (_timeline is null)
        {
            _unknown++;
            return ev;
        }
        var place = _timeline.Find(ev.Time);
        if (place.IsLate) _late++;
        else if (!place.Known) _unknown++;
        string? zone = place.Path.Length > 0 ? _zoneName(place.Path[^1]) : null;
        string? path = place.Path.Length > 0 ? string.Join(" > ", place.Path.Select(_zoneName)) : null;
        return ev with { Place = place, Zone = zone, PathText = path };
    }

    private void Add(RuntimeEvent ev)
    {
        _ring[_next] = ev;
        _next = (_next + 1) % _ring.Length;
        if (_filled < _ring.Length) _filled++;
    }

    private static double Ms(long ticks) => Math.Max(0, ticks) * 1000.0 / Stopwatch.Frequency;

    private static object? Payload(EventWrittenEventArgs e, string name)
    {
        int i = e.PayloadNames?.IndexOf(name) ?? -1;
        return i >= 0 && e.Payload is { } p && i < p.Count ? p[i] : null;
    }

    /// <summary>Уровень компиляции из <c>MethodFlags</c> (биты 7–9 — <c>JitOptimizationTier</c> среды; проверено опытом: OSR — 5).</summary>
    public static string Tier(uint flags) => ((flags >> 7) & 7) switch
    {
        1 => "minopts",
        2 => "optimized",
        3 => "tier0",
        4 => "tier1",
        5 => "osr",
        6 => "tier0-instrumented",
        7 => "tier1-instrumented",
        _ => "?",
    };

    private static string GcReason(int r) => r switch
    {
        0 => "alloc-small",
        1 => "induced",
        2 => "low-memory",
        3 => "empty",
        4 => "alloc-large",
        5 => "oos-small",
        6 => "oos-large",
        7 => "induced-noforce",
        8 => "stress",
        9 => "low-memory-blocking",
        10 => "induced-compacting",
        11 => "low-memory-host",
        12 => "pm-full-gc",
        13 => "low-memory-host-blocking",
        _ => $"reason-{r}",
    };

    private static string SuspendReason(int r) => r switch
    {
        0 => "other",
        1 => "gc",
        2 => "appdomain-shutdown",
        3 => "code-pitching",
        4 => "shutdown",
        5 => "debugger",
        6 => "gc-prep",
        7 => "debugger-sweep",
        8 => "profiler",
        9 => "rejit",
        _ => $"reason-{r}",
    };

    // ── чтение (любой поток) ───────────────────────────────────────────────

    /// <summary>Забыть итоги и кольцо (после прогрева, вместе со сбросом профайлера).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _types.Clear();
            _tiers.Clear();
            _received = _late = _unknown = _allocSamples = _allocBytes = _jitCount = _jitMain = _gcCount = _pauses = 0;
            _jitMs = _jitMainMs = _pauseMs = _maxPauseMs = 0;
            Array.Clear(_ring);
            _next = _filled = 0;
        }
    }

    /// <summary>Последние события кольца (от старых к новым), по виду или все.</summary>
    public IReadOnlyList<RuntimeEvent> Recent(string? kind = null, int count = int.MaxValue)
    {
        lock (_gate)
        {
            var list = new List<RuntimeEvent>();
            for (int i = 0; i < _filled; i++)
            {
                var e = _ring[(_next - _filled + i + _ring.Length) % _ring.Length];
                if (kind is null || e.Kind == kind) list.Add(e);
            }
            return list.Count > count ? list[^count..] : list;
        }
    }

    /// <summary>
    /// Итоги JSON: принято, опоздало (старше ленты), без места; выделения по типам (выборок, байт, на главном потоке, зоны с
    /// байтами), JIT (методов, на главном, мс, уровни), сборки и паузы; последние события каждого вида с местами.
    /// </summary>
    public JsonObject Summary(int types = 20, int recent = 20)
    {
        lock (_gate)
        {
            var json = new JsonObject
            {
                ["calibrated"] = _calibrated,
                ["calibrations"] = _calibrations,
                ["placeErrorUs"] = double.IsFinite(CalibrationSpreadUs) ? Math.Round(CalibrationSpreadUs, 1) : null,
                ["received"] = _received,
                ["late"] = _late,
                ["unplaced"] = _unknown,
                ["pending"] = _pending.Count,
                ["alloc"] = new JsonObject
                {
                    ["samples"] = _allocSamples, ["bytes"] = _allocBytes,
                    ["note"] = "выборка GCAllocationTick: ~каждые 100 КБ выделений процесса, байты — выделенные с прошлой выборки",
                    ["types"] = new JsonArray([.. _types.OrderByDescending(p => p.Value.Bytes).Take(types).Select(p => (JsonNode?)new JsonObject
                    {
                        ["type"] = p.Key, ["samples"] = p.Value.Samples, ["bytes"] = p.Value.Bytes, ["mainThread"] = p.Value.Main,
                        ["zones"] = new JsonObject([.. p.Value.Zones.OrderByDescending(z => z.Value).Take(5)
                            .Select(z => KeyValuePair.Create(z.Key, (JsonNode?)z.Value))]),
                    })]),
                },
                ["jit"] = new JsonObject
                {
                    ["methods"] = _jitCount, ["mainThread"] = _jitMain, ["ms"] = Math.Round(_jitMs, 3), ["mainThreadMs"] = Math.Round(_jitMainMs, 3),
                    ["tiers"] = new JsonObject([.. _tiers.OrderByDescending(t => t.Value).Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value))]),
                },
                ["gc"] = new JsonObject
                {
                    ["collections"] = _gcCount, ["pauses"] = _pauses, ["pauseMs"] = Math.Round(_pauseMs, 3), ["maxPauseMs"] = Math.Round(_maxPauseMs, 3),
                },
            };
            json["recent"] = new JsonObject
            {
                ["gc"] = Events("gc", recent),
                ["pause"] = Events("pause", recent),
                ["jit"] = Events("jit", recent),
                // Компиляции на главном потоке внутри кадра или тика — пауза периода (фоновые — только смена кода).
                ["jitMain"] = Events("jit", recent, e => e.Main && (e.Place.Frame is not null || e.Place.Tick is not null)),
                ["alloc"] = Events("alloc", recent),
            };
            return json;
        }
    }

    private JsonArray Events(string kind, int count, Func<RuntimeEvent, bool>? filter = null)
    {
        var list = new List<RuntimeEvent>();
        for (int i = _filled - 1; i >= 0 && list.Count < count; i--)
        {
            var e = _ring[(_next - _filled + i + _ring.Length) % _ring.Length];
            if (e.Kind == kind && (filter is null || filter(e))) list.Add(e);
        }
        list.Reverse();
        return new JsonArray([.. list.Select(e => (JsonNode?)ToJson(e))]);
    }

    /// <summary>Событие — JSON: вид, имя, мс, байты, поколение, уровень, поток (главный ли), кадр или «после кадра», тик, зона и путь.</summary>
    public static JsonObject ToJson(RuntimeEvent e)
    {
        var json = new JsonObject { ["kind"] = e.Kind, ["name"] = e.Name };
        if (e.Ms > 0) json["ms"] = Math.Round(e.Ms, 4);
        if (e.Bytes > 0) json["bytes"] = e.Bytes;
        if (e.Gen >= 0) json["gen"] = e.Gen;
        if (e.Tier is not null) json["tier"] = e.Tier;
        json["thread"] = e.Main ? "main" : e.Thread.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (e.Place.Frame is { } f) json["frame"] = f;
        if (e.Place.AfterFrame is { } af) json["afterFrame"] = af;
        if (e.Place.Tick is { } t) json["tick"] = t;
        if (e.Zone is not null) json["zone"] = e.Zone;
        if (e.PathText is not null) json["path"] = e.PathText;
        if (e.Place.IsLate) json["late"] = true;
        return json;
    }

    public void Dispose()
    {
        _disposed = true;
        _listener.Dispose();
    }

    /// <summary>
    /// Слушатель: <see cref="EventListener.OnEventSourceCreated"/> зовётся и из конструктора базового класса (до полей
    /// наследника) — источник запоминается, подписка — в <see cref="Start"/>.
    /// </summary>
    private sealed class Listener : EventListener
    {
        // Инициализатор поля наследника выполняется до конструктора базового класса — замок готов и там.
        private readonly object _lock = new();
        private RuntimeEvents? _owner;
        private EventSource? _source;

        public void Start(RuntimeEvents owner)
        {
            EventSource? source;
            lock (_lock)
            {
                _owner = owner;
                source = _source;
            }
            if (source is not null) EnableEvents(source, EventLevel.Verbose, (EventKeywords)Keywords);
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name != SourceName) return;
            bool ready;
            lock (_lock)
            {
                _source = source;
                ready = _owner is not null;
            }
            if (ready) EnableEvents(source, EventLevel.Verbose, (EventKeywords)Keywords);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e) => _owner?.OnEvent(e);
    }
}
