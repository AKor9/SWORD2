using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Sword.Engine.Diagnostics;

/// <summary>Класс причины аномального кадра или тика (точка Пв): куда ушло лишнее время периода.</summary>
public enum AnomalyClass : byte
{
    /// <summary>Поток на CPU и не компилировал: время — за код (главная зона — где).</summary>
    Code,

    /// <summary>Компиляция JIT на главном потоке (многоуровневая компиляция, OSR, первый вызов).</summary>
    Jit,

    /// <summary>Пауза сборки мусора.</summary>
    Gc,

    /// <summary>Ожидание GPU: забор кадра в полёте, показ (зоны вида <see cref="ProfileZoneKind.GpuWait"/>).</summary>
    GpuWait,

    /// <summary>Поток сняли с процессора вне ожиданий (другие процессы, ОС): время потока на CPU меньше настенного.</summary>
    Machine,
}

/// <summary>Судьба файла аномалии: в очереди, записан, ошибка записи; не пишется — лимит за прогон, частота, писатель занят.</summary>
public enum AnomalyFile : byte
{
    Queued,
    Written,
    Failed,
    Limit,
    Rate,
    Busy,
}

/// <summary>
/// Разбор лишнего времени периода, мс (точка Пв): <see cref="ExcessMs"/> — сверх медианы; из него JIT главного потока, пауза GC,
/// ожидание GPU сверх обычного, время вне CPU вне ожиданий (<see cref="MachineMs"/>, без паузы GC) и остаток — код.
/// </summary>
public readonly record struct AnomalyCauses(double ExcessMs, double JitMs, double GcMs, double GpuWaitMs, double MachineMs, double CodeMs)
{
    /// <summary>Класс — наибольшая доля (при равенстве: JIT, GC, ожидание GPU, машина, код); всё по нулям — код.</summary>
    public AnomalyClass Class
    {
        get
        {
            var best = AnomalyClass.Code;
            double v = CodeMs;
            if (MachineMs > 0 && MachineMs >= v) (best, v) = (AnomalyClass.Machine, MachineMs);
            if (GpuWaitMs > 0 && GpuWaitMs >= v) (best, v) = (AnomalyClass.GpuWait, GpuWaitMs);
            if (GcMs > 0 && GcMs >= v) (best, v) = (AnomalyClass.Gc, GcMs);
            if (JitMs > 0 && JitMs >= v) best = AnomalyClass.Jit;
            return best;
        }
    }

    /// <summary>Класс словом (журнал, HUD, файл).</summary>
    public static string Word(AnomalyClass c) => c switch
    {
        AnomalyClass.Jit => "JIT",
        AnomalyClass.Gc => "GC",
        AnomalyClass.GpuWait => "ожидание GPU",
        AnomalyClass.Machine => "машина",
        _ => "код",
    };

    /// <summary>Класс ключом JSON.</summary>
    public static string Key(AnomalyClass c) => c switch
    {
        AnomalyClass.Jit => "jit",
        AnomalyClass.Gc => "gc",
        AnomalyClass.GpuWait => "gpu",
        AnomalyClass.Machine => "machine",
        _ => "code",
    };

    public JsonObject ToJson() => new()
    {
        ["excessMs"] = Math.Round(ExcessMs, 4), ["jitMs"] = Math.Round(JitMs, 4), ["gcPauseMs"] = Math.Round(GcMs, 4),
        ["gpuWaitMs"] = Math.Round(GpuWaitMs, 4), ["offCpuMs"] = Math.Round(MachineMs, 4), ["codeMs"] = Math.Round(CodeMs, 4),
    };
}

/// <summary>
/// Настройки захвата аномалий (точка Пв). Порог периода: абсолютный (<see cref="AbsoluteMs"/> &gt; 0 — дольше стольких мс) или
/// по медиане последних <see cref="Window"/> периодов своего вида: <c>медиана + max(MinExcess, RelativeExcess·медиана)</c>.
/// Первые <see cref="MinSamples"/> периодов не судятся. Файлов за прогон — не больше <see cref="MaxFiles"/>, частота — ведро
/// жетонов (<see cref="Burst"/> сразу, дальше <see cref="FilesPerSecond"/>).
/// </summary>
public sealed record AnomalyOptions
{
    /// <summary>Каталог файлов аномалий (создаётся при первой записи): у exe — <c>artifacts/profile/&lt;прогон&gt;</c>.</summary>
    public required string Directory { get; init; }

    /// <summary>Абсолютный порог кадра и тика, мс (<c>--profile-threshold</c>); 0 — относительный.</summary>
    public double AbsoluteMs { get; init; }

    /// <summary>
    /// Лишнее сверх медианы — доля медианы: 0,75 — период в 1,75 раза дольше обычного. Важна при большой медиане: кадр с
    /// вертикальной синхронизацией 16,7 мс — пропущенный интервал (33,3 мс) ловится (порог 29,2), дрожание (17–18 мс) — нет.
    /// </summary>
    public double RelativeExcess { get; init; } = 0.75;

    /// <summary>
    /// Кадр: лишнее не меньше, мс. Кадр App — ~0,37 мс (32 врага, без vsync), худший кадр ровного бенча — 0,6…2,2 мс на
    /// нагруженной машине: порог ~2,4 мс ловит рывок в несколько мс и не ловит шум.
    /// </summary>
    public double FrameMinExcessMs { get; init; } = 2;

    /// <summary>
    /// Тик: лишнее не меньше, мс. Тик толпы — ~0,18 мс (медиана), p99 ~0,25: порог ~1,2 мс ловит компиляции OSR в тике (1,5 и
    /// 2,2 мс — Пб), не ловит шум.
    /// </summary>
    public double TickMinExcessMs { get; init; } = 1;

    /// <summary>Периодов в окне медианы (медиана пересчитывается каждые <see cref="RecomputeEvery"/>).</summary>
    public int Window { get; init; } = 128;

    public int RecomputeEvery { get; init; } = 32;

    /// <summary>Сколько периодов вида нужно до первого суда (первые кадры свежего процесса — компиляция всего).</summary>
    public int MinSamples { get; init; } = 32;

    /// <summary>Файлов аномалий за прогон, не больше.</summary>
    public int MaxFiles { get; init; } = 20;

    /// <summary>Файлов подряд без ожидания (ведро жетонов).</summary>
    public int Burst { get; init; } = 5;

    /// <summary>Жетонов ведра в секунду.</summary>
    public double FilesPerSecond { get; init; } = 1;

    /// <summary>Периодов до аномального (с ним) в файле: заголовки, строки зон, трасса.</summary>
    public int Recent { get; init; } = 32;

    /// <summary>Файл Chrome Trace рядом с файлом аномалии.</summary>
    public bool Trace { get; init; } = true;

    /// <summary>Поля прогона в файле аномалии (процесс, ключи запуска).</summary>
    public IReadOnlyDictionary<string, string>? Run { get; init; }
}

/// <summary>
/// Захваченная аномалия (точка Пв): вид и номер периода (у кадра App, сработавшего по тику, — ещё тик), длительность, медиана
/// и порог сработавшего периода, разбор причин и класс, главная зона (наибольшее своё лишнее время), эпоха (число сбросов
/// профайлера до неё: 0 — прогрев, дальше — замер), судьба файла и путь.
/// </summary>
public readonly record struct ProfileAnomaly(long Sequence, ProfilePeriod Period, ulong Number, ulong? Tick, bool TickTrigger, double Ms,
    double MedianMs, double ThresholdMs, double PeriodMs, AnomalyCauses Causes, AnomalyClass Class, int Zone, string ZoneName, double ZoneMs,
    double ZoneExcessMs, int Epoch, AnomalyFile File, string? Path, long Timestamp)
{
    /// <summary>Коротко словами: «кадр 1234 (тик 383): 8.61 мс, медиана 0.37 — JIT; core.root-collision@Movement +7.90 мс».</summary>
    public string Text
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            string what = Period == ProfilePeriod.Frame
                ? TickTrigger ? $"тик {Tick} в кадре {Number}" : $"кадр {Number}"
                : $"тик {Number}";
            return string.Format(ci, "{0}: {1:0.###} мс (медиана {2:0.###}, порог {3:0.###}) — {4}; главная зона {5} {6:0.###} мс (+{7:0.###})",
                what, Ms, MedianMs, ThresholdMs, AnomalyCauses.Word(Class), ZoneName, ZoneMs, ZoneExcessMs);
        }
    }

    public JsonObject ToJson() => new()
    {
        ["sequence"] = Sequence,
        ["period"] = Period == ProfilePeriod.Frame ? "frame" : "tick",
        ["number"] = Number,
        ["tick"] = Tick,
        ["trigger"] = TickTrigger ? "tick" : Period == ProfilePeriod.Frame ? "frame" : "tick",
        ["ms"] = Math.Round(Ms, 4),
        ["medianMs"] = Math.Round(MedianMs, 4),
        ["thresholdMs"] = Math.Round(ThresholdMs, 4),
        ["periodMs"] = Math.Round(PeriodMs, 4),
        ["class"] = AnomalyCauses.Key(Class),
        ["classText"] = AnomalyCauses.Word(Class),
        ["causes"] = Causes.ToJson(),
        ["zone"] = ZoneName,
        ["zoneMs"] = Math.Round(ZoneMs, 4),
        ["zoneExcessMs"] = Math.Round(ZoneExcessMs, 4),
        ["epoch"] = Epoch,
        ["file"] = Path,
        ["state"] = File.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Захват аномалий профайлера (точка Пв): порог периода (<see cref="AnomalyOptions"/>), список захваченных, файлы аномалий на
/// своём потоке. Судит и копирует владелец профайлера (<see cref="CpuProfiler.CaptureAnomalies"/>: конец кадра или тика
/// включённого профайлера, без выделений — всё заранее выделено); файл, трассу и строку журнала (категория <c>profile</c>)
/// пишет фоновый поток писателя — главный поток не выделяет и не ждёт диска даже в кадре аномалии, а запись не рождает
/// цепочку аномалий. Читать список — на потоке владельца (<see cref="TryGetLast"/>, <see cref="List"/>).
/// </summary>
public sealed class ProfileAnomalies : IDisposable
{
    public const string FileFormat = "sword-profile-anomaly";

    /// <summary>Записей в списке (кольцо: старые вытесняются, счётчики — все).</summary>
    public const int Capacity = 256;

    /// <summary>Мест копирования (писатель пишет одно, владелец заполняет другое).</summary>
    public const int Slots = 3;

    /// <summary>Тиков App в окне файла — заголовки (кадры окна — со строками зон).</summary>
    public const int RecentTicksCapacity = 128;

    private readonly CpuProfiler _profiler;
    private readonly object _gate = new();
    private readonly ProfileAnomaly[] _records = new ProfileAnomaly[Capacity];
    private long _sequence, _sinceReset;

    /// <summary>Итоги за прогон (список — кольцо последних <see cref="Capacity"/>): по судьбе файла и по классу.</summary>
    private readonly long[] _byFile = new long[6], _byClass = new long[5];

    internal readonly Detector Frames, Ticks;
    internal readonly AnomalySlot[] SlotPool;
    private long _order;
    private int _capacity;

    // Файлы: лимит и ведро жетонов (владелец); пропуски по частоте — писателю в строку журнала.
    private int _files;
    private double _tokens;
    private long _refilled;
    private int _rateSkipped, _limitNoted;

    // Машина для окна аномалии: снимки раз в секунду на границе периода (без пробы частоты, ~3 мкс, 0 байт).
    private MachineSample _m0, _m1;
    private bool _has0, _has1;

    // Аномальный тик кадра App: пишется с кадром (худший тик кадра).
    internal PendingTick Pending;

    private readonly Thread _writer;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;

    internal ProfileAnomalies(CpuProfiler profiler, AnomalyOptions options, int capacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.Directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Window, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Recent, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AbsoluteMs);
        _profiler = profiler;
        Options = options;
        Directory = Path.GetFullPath(options.Directory);
        Frames = new Detector(options, options.FrameMinExcessMs);
        Ticks = new Detector(options, options.TickMinExcessMs);
        _capacity = capacity;
        SlotPool = new AnomalySlot[Slots];
        for (int i = 0; i < Slots; i++) SlotPool[i] = new AnomalySlot(capacity, options.Recent);
        Pending = new PendingTick(capacity);
        _tokens = options.Burst;
        _refilled = Stopwatch.GetTimestamp();
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "sword.profile.anomalies" };
        _writer.Start();
    }

    public AnomalyOptions Options { get; }

    /// <summary>Каталог файлов аномалий (полный путь).</summary>
    public string Directory { get; }

    /// <summary>Абсолютный порог, мс (0 — по медиане). Менять — на потоке владельца: действует со следующего периода.</summary>
    public double AbsoluteMs
    {
        get => Frames.AbsoluteMs;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Frames.SetAbsolute(value);
            Ticks.SetAbsolute(value);
        }
    }

    /// <summary>Захвачено аномалий (все, с файлом и без).</summary>
    public long Detected
    {
        get
        {
            lock (_gate) return _sequence;
        }
    }

    /// <summary>Эпоха — сбросов профайлера (<see cref="CpuProfiler.Reset"/>): аномалии с эпохой меньше — до сброса (прогрев).</summary>
    public int Epoch { get; private set; }

    /// <summary>Захвачено с последнего сброса профайлера (у бенча — за замер).</summary>
    public long SinceReset
    {
        get
        {
            lock (_gate) return _sinceReset;
        }
    }

    /// <summary>Аномалий за прогон с этой судьбой файла (в очереди — ещё не записанные).</summary>
    public long CountOf(AnomalyFile file)
    {
        lock (_gate) return _byFile[(int)file];
    }

    /// <summary>Аномалий за прогон этого класса.</summary>
    public long CountOf(AnomalyClass c)
    {
        lock (_gate) return _byClass[(int)c];
    }

    /// <summary>Итог одной строкой (журнал exe): сколько, после сброса, по классам, файлов, каталог.</summary>
    public string Summary()
    {
        var classes = Enum.GetValues<AnomalyClass>().Select(c => (c, n: CountOf(c))).Where(x => x.n > 0).OrderByDescending(x => x.n)
            .Select(x => $"{AnomalyCauses.Word(x.c)} {x.n}");
        return $"аномалий {Detected}" + (Epoch > 0 ? $" (после сброса итогов — {SinceReset})" : "") + $": {string.Join(", ", classes)}; " +
            $"файлов {CountOf(AnomalyFile.Written)} (не писались: по частоте {CountOf(AnomalyFile.Rate)}, сверх лимита {CountOf(AnomalyFile.Limit)}, " +
            $"писатель занят {CountOf(AnomalyFile.Busy)}) — {Directory}";
    }

    /// <summary>Порог кадра и тика сейчас, мс (NaN — окно медианы ещё не набрано).</summary>
    public (double FrameMs, double TickMs) Thresholds => (Frames.ThresholdMs, Ticks.ThresholdMs);

    /// <summary>Медианы окна сейчас, мс.</summary>
    public (double FrameMs, double TickMs) Medians => (Frames.MedianMs, Ticks.MedianMs);

    /// <summary>Последняя аномалия (без выделений — для HUD).</summary>
    public bool TryGetLast(out ProfileAnomaly last)
    {
        lock (_gate)
        {
            if (_sequence == 0)
            {
                last = default;
                return false;
            }
            last = _records[(_sequence - 1) % Capacity];
            return true;
        }
    }

    /// <summary>Последние <paramref name="max"/> аномалий, от старых к новым (выделяет — не для кадра).</summary>
    public IReadOnlyList<ProfileAnomaly> List(int max = Capacity)
    {
        lock (_gate)
        {
            long n = Math.Min(Math.Min(_sequence, Capacity), max);
            var list = new ProfileAnomaly[n];
            for (long i = 0; i < n; i++) list[i] = _records[(_sequence - n + i) % Capacity];
            return list;
        }
    }

    /// <summary>Итоги JSON: порог, медианы, сколько захвачено и с файлом, последние <paramref name="max"/>.</summary>
    public JsonObject ToJson(int max = 50)
    {
        var list = List(max);
        var (frameT, tickT) = Thresholds;
        var (frameM, tickM) = Medians;
        var json = new JsonObject
        {
            ["directory"] = Directory,
            ["threshold"] = new JsonObject
            {
                ["mode"] = AbsoluteMs > 0 ? "absolute" : "median",
                ["absoluteMs"] = AbsoluteMs,
                ["relativeExcess"] = Options.RelativeExcess,
                ["frameMinExcessMs"] = Options.FrameMinExcessMs,
                ["tickMinExcessMs"] = Options.TickMinExcessMs,
                ["window"] = Options.Window,
                ["frameMs"] = Num(frameT), ["tickMs"] = Num(tickT), ["frameMedianMs"] = Num(frameM), ["tickMedianMs"] = Num(tickM),
            },
            ["detected"] = Detected,
            ["sinceReset"] = SinceReset,
            ["epoch"] = Epoch,
            ["written"] = CountOf(AnomalyFile.Written),
            ["queued"] = CountOf(AnomalyFile.Queued),
            ["suppressed"] = new JsonObject
            {
                ["limit"] = CountOf(AnomalyFile.Limit), ["rate"] = CountOf(AnomalyFile.Rate),
                ["busy"] = CountOf(AnomalyFile.Busy), ["failed"] = CountOf(AnomalyFile.Failed),
            },
            ["maxFiles"] = Options.MaxFiles,
            ["byClass"] = new JsonObject([.. Enum.GetValues<AnomalyClass>().Select(c => (c, n: CountOf(c))).Where(x => x.n > 0)
                .OrderByDescending(x => x.n).Select(x => KeyValuePair.Create(AnomalyCauses.Key(x.c), (JsonNode?)x.n))]),
            ["list"] = new JsonArray([.. list.Select(a => (JsonNode?)a.ToJson())]),
        };
        return json;
    }

    private static JsonNode? Num(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;

    /// <summary>Дождаться, пока писатель запишет всё, что в очереди. false — не успел за <paramref name="timeout"/>.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (SlotPool.Any(s => Volatile.Read(ref s.State) != AnomalySlot.Free))
        {
            if (sw.Elapsed > timeout) return false;
            _wake.Set();
            Thread.Sleep(1);
        }
        return true;
    }

    public void Dispose()
    {
        if (_stop) return;
        Flush(TimeSpan.FromSeconds(5));
        _stop = true;
        _wake.Set();
        // Писатель не вышел (диск стоит) — событие не закрывается: его WaitOne на закрытом бросил бы исключение фонового потока и
        // закрыл бы процесс; фоновый поток умрёт с процессом.
        if (_writer.Join(TimeSpan.FromSeconds(5))) _wake.Dispose();
    }

    // ── владелец (поток кадра или тика) ────────────────────────────────────

    internal void OnReset()
    {
        lock (_gate)
        {
            Epoch++;
            _sinceReset = 0;
        }
    }

    internal void DropPending() => Pending.Active = false;

    /// <summary>Снимок машины раз в секунду (граница периода): начало окна машины аномалии.</summary>
    internal void NoteMachine(long now)
    {
        if (_has1 && now - _m1.Time < Stopwatch.Frequency) return;
        _m0 = _m1;
        _has0 = _has1;
        _m1 = MachineSample.Take(probe: false);
        _has1 = true;
    }

    /// <summary>Начало окна машины для аномалии в <paramref name="now"/>: самый свежий снимок старше 0,5 с.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // путь аномалии: см. CpuProfiler.WarmCapture
    internal bool Baseline(long now, out MachineSample sample)
    {
        long half = Stopwatch.Frequency / 2;
        if (_has1 && now - _m1.Time >= half)
        {
            sample = _m1;
            return true;
        }
        sample = _m0;
        return _has0;
    }

    /// <summary>Записать аномалию в список (без файла); возвращает записанное — с номером последовательности и эпохой.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // путь аномалии: см. CpuProfiler.WarmCapture
    internal ProfileAnomaly Record(in ProfileAnomaly a)
    {
        lock (_gate)
        {
            long seq = _sequence++;
            _sinceReset++;
            _byClass[(int)a.Class]++;
            _byFile[(int)AnomalyFile.Queued]++;   // судьба — следующим вызовом SetFile или писателем
            var stored = a with { Sequence = seq, Epoch = Epoch };
            _records[seq % Capacity] = stored;
            return stored;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // путь аномалии: см. CpuProfiler.WarmCapture
    internal void SetFile(long seq, AnomalyFile file, string? path)
    {
        lock (_gate)
        {
            _byFile[(int)AnomalyFile.Queued]--;
            _byFile[(int)file]++;
            ref var r = ref _records[seq % Capacity];
            if (r.Sequence == seq) r = r with { File = file, Path = path ?? r.Path };
        }
    }

    /// <summary>Можно ли писать файл: лимит за прогон, ведро жетонов. Queued — жетон взят.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // путь аномалии: см. CpuProfiler.WarmCapture
    internal AnomalyFile Admit(long now)
    {
        if (_files >= Options.MaxFiles)
        {
            if (Interlocked.Exchange(ref _limitNoted, 1) == 0) _wake.Set();   // писатель скажет в журнал один раз
            return AnomalyFile.Limit;
        }
        _tokens = Math.Min(Options.Burst, _tokens + (now - _refilled) / (double)Stopwatch.Frequency * Options.FilesPerSecond);
        _refilled = now;
        if (_tokens < 1)
        {
            Interlocked.Increment(ref _rateSkipped);
            return AnomalyFile.Rate;
        }
        _tokens -= 1;
        return AnomalyFile.Queued;
    }

    /// <summary>Свободное место копирования не уже <paramref name="zones"/>; null — все заняты писателем.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // путь аномалии: см. CpuProfiler.WarmCapture
    internal AnomalySlot? Acquire(int zones)
    {
        foreach (var s in SlotPool)
            if (Volatile.Read(ref s.State) == AnomalySlot.Free && s.Stride >= zones)
                return s;
        return null;
    }

    /// <summary>Отдать заполненное место писателю.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // путь аномалии: см. CpuProfiler.WarmCapture
    internal void Submit(AnomalySlot slot)
    {
        _files++;
        slot.Order = ++_order;
        Volatile.Write(ref slot.State, AnomalySlot.Filled);
        _wake.Set();
    }

    /// <summary>Зон стало больше (регистрация — не горячий путь): свободные места — шире сейчас, занятые — писатель после записи.</summary>
    internal void Grow(int capacity)
    {
        Volatile.Write(ref _capacity, capacity);
        Pending.Grow(capacity);
        foreach (var s in SlotPool)
            if (Volatile.Read(ref s.State) == AnomalySlot.Free && s.Stride < capacity)
                s.Allocate(capacity, Options.Recent);
    }

    // ── писатель (свой поток) ──────────────────────────────────────────────

    /// <summary>
    /// Прогреть писателя (владелец — при включении профайлера с захватом): без прогрева первый файл аномалии компилирует на потоке
    /// писателя ~290 методов (JSON, трасса, строки) — десятки мс в момент аномалии (места копирования заняты, следующие — «busy»).
    /// Прогрев — пробный файл в пустой поток сразу при включении, вместе с компиляцией старта (~260 методов; первый настоящий файл
    /// — ~25, ~6 мс). Фоновую перекомпиляцию tier1 горячего кода писателя (и BCL) при настоящих файлах он не убирает — см.
    /// «Цена» в md фичи. Без выделений: флаг и пробуждение писателя.
    /// </summary>
    internal void RequestWarm()
    {
        if (_warmWanted) return;
        _warmWanted = true;
        _wake.Set();
    }

    private volatile bool _warmWanted;
    private bool _warmed;   // только поток писателя

    private static readonly System.Text.Json.JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Пробный файл аномалии (кадр с тиком, два периода, машина) и его трасса — в пустой поток: код писателя скомпилирован.</summary>
    private void Warm()
    {
        try
        {
            var slot = new AnomalySlot(2, 2)
            {
                Zones = 2, Period = ProfilePeriod.Frame, Number = 1, PeriodHit = true, HasTick = true, TickNumber = 1, Recent = 2, RecentTicks = 1,
                HasBaseline = true,
            };
            slot.Parent[0] = -1;
            slot.Parent[1] = 0;
            slot.Calls[0] = slot.Calls[1] = slot.TickCalls[1] = slot.RecentCalls[0] = slot.RecentCalls[1] = 1;
            slot.Time[0] = slot.RecentTime[0] = 2;
            slot.Time[1] = slot.TickTime[1] = slot.RecentTime[1] = 1;
            slot.RecentTickHeaders[0] = slot.TickHeader = new ProfileHeader(1, 1, 1, 1, 1, 0, false, 1, default);
            slot.RecentHeaders[0] = slot.RecentHeaders[1] = slot.Header = new ProfileHeader(1, 1, 1, 1, 2, 0, false, ulong.MaxValue, default);
            slot.Baseline = slot.Now = MachineSample.Take(probe: false);
            slot.Record = new ProfileAnomaly(0, ProfilePeriod.Frame, 1, 1, false, 0, 0, 0, 0, default, AnomalyClass.Code, 1, "tick", 0, 0, 0,
                AnomalyFile.Queued, null, 0);
            Func<int, string> name = z => z == CpuProfiler.FrameZone ? "frame" : "tick";
            var json = AnomalyReport.Build(slot, slot.Record, name, Options, true, "warm.trace.json");
            _ = (json.ToJsonString(FileJson).ReplaceLineEndings("\r\n") + slot.Record.Text + Path.Combine(Directory, "warm.json")).Length;
            AnomalyReport.WriteTrace(slot, name, Stream.Null);
        }
        catch (Exception e)
        {
            Diagnostics.Log.Warning("profile", $"прогрев писателя аномалий: {e.GetType().Name}: {e.Message}");
        }
        _warmed = true;
    }

    private void WriterLoop()
    {
        while (true)
        {
            _wake.WaitOne(TimeSpan.FromMilliseconds(250));
            if (_warmWanted && !_warmed) Warm();
            if (Volatile.Read(ref _limitNoted) == 1)
            {
                Volatile.Write(ref _limitNoted, 2);
                Diagnostics.Log.Info("profile", $"аномалий больше {Options.MaxFiles} за прогон — файлы больше не пишутся; " +
                    "список — profile.json (anomalies) и profile.stages");
            }
            while (Next() is { } slot)
            {
                try
                {
                    Write(slot);
                }
                catch (Exception e)
                {
                    // Любая ошибка писателя — только эта аномалия без файла: необработанное исключение фонового потока закрыло бы
                    // игру из-за диагностики.
                    SetFile(slot.Sequence, AnomalyFile.Failed, null);
                    Diagnostics.Log.Warning("profile", $"файл аномалии не записан: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    if (slot.Stride < Volatile.Read(ref _capacity)) slot.Allocate(Volatile.Read(ref _capacity), Options.Recent);
                    slot.Events = null;
                    slot.Timeline = null;
                    Volatile.Write(ref slot.State, AnomalySlot.Free);
                }
            }
            if (_stop) return;
        }
    }

    private AnomalySlot? Next()
    {
        AnomalySlot? next = null;
        foreach (var s in SlotPool)
            if (Volatile.Read(ref s.State) == AnomalySlot.Filled && (next is null || s.Order < next.Order))
                next = s;
        return next;
    }

    private void Write(AnomalySlot slot)
    {
        // События среды приходят с задержкой ~15 мс: файл — после хвоста (метка компилируется здесь, на потоке писателя).
        bool flushed = slot.Events?.Flush(TimeSpan.FromSeconds(1)) ?? true;
        string kind = slot.Period == ProfilePeriod.Frame ? "frame" : "tick";
        string stem = $"anomaly-{kind}-{slot.Number.ToString(CultureInfo.InvariantCulture)}";
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, stem + ".json");
        string? trace = Options.Trace ? Path.Combine(Directory, stem + ".trace.json") : null;
        // Запись — из места копирования, не из списка: пока писатель ждал (хвост событий, прошлый файл), владелец мог записать
        // ещё больше Capacity аномалий, и место в кольце списка занято другой.
        var record = slot.Record;
        var json = AnomalyReport.Build(slot, record, _profiler.SharedName, Options, flushed, trace is null ? null : Path.GetFileName(trace));
        if (trace is not null) AnomalyReport.WriteTrace(slot, _profiler.SharedName, trace);
        File.WriteAllText(path, json.ToJsonString(FileJson).ReplaceLineEndings("\r\n") + "\r\n");
        SetFile(slot.Sequence, AnomalyFile.Written, path);
        int skipped = Interlocked.Exchange(ref _rateSkipped, 0);
        Diagnostics.Log.Info("profile", "аномалия: " + record.Text + $"; файл {path}" + (skipped > 0 ? $" (без файлов по частоте — ещё {skipped})" : ""));
    }

    // ── внутреннее устройство ──────────────────────────────────────────────

    /// <summary>
    /// Порог одного вида периодов: окно последних <see cref="AnomalyOptions.Window"/> длительностей, медиана — каждые
    /// <see cref="AnomalyOptions.RecomputeEvery"/> (сортировка копии окна в заранее выделенном массиве, без выделений).
    /// </summary>
    internal sealed class Detector
    {
        private readonly long[] _window, _sorted;
        private readonly int _minSamples, _every;
        private readonly double _relative;
        private readonly long _minExcess;
        private int _count, _next, _since;
        private long _absolute;

        public long Median { get; private set; }
        public long Threshold { get; private set; } = long.MaxValue;

        public Detector(AnomalyOptions o, double minExcessMs)
        {
            _window = new long[o.Window];
            _sorted = new long[o.Window];
            _minSamples = Math.Min(o.MinSamples, o.Window);
            _every = Math.Max(1, o.RecomputeEvery);
            _relative = o.RelativeExcess;
            _minExcess = (long)(minExcessMs * Stopwatch.Frequency / 1000);
            _absolute = Ticks(o.AbsoluteMs);
        }

        public double AbsoluteMs => CpuProfiler.Ms(_absolute);
        public double MedianMs => _count >= _minSamples ? CpuProfiler.Ms(Median) : double.NaN;
        public double ThresholdMs => _count >= _minSamples ? CpuProfiler.Ms(Threshold) : double.NaN;

        /// <summary>Абсолютный порог в отсчётах часов: больше нуля — не меньше одного отсчёта.</summary>
        private static long Ticks(double ms) => ms > 0 ? Math.Max(1, (long)(ms * Stopwatch.Frequency / 1000)) : 0;

        public void SetAbsolute(double ms)
        {
            _absolute = Ticks(ms);
            if (_count >= _minSamples) Recompute();
        }

        /// <summary>Период длительностью <paramref name="time"/> — аномалия (по порогу до него); затем он — в окно.</summary>
        public bool Push(long time)
        {
            bool hit = _count >= _minSamples && time > Threshold;
            _window[_next] = time;
            _next = (_next + 1) % _window.Length;
            if (_count < _window.Length) _count++;
            if (++_since >= _every || _count == _minSamples) Recompute();
            return hit;
        }

        private void Recompute()
        {
            _since = 0;
            Array.Copy(_window, _sorted, _count);
            var span = _sorted.AsSpan(0, _count);
            span.Sort();
            Median = span[_count / 2];
            Threshold = _absolute > 0 ? _absolute : Median + Math.Max(_minExcess, (long)(_relative * Median));
        }
    }

    /// <summary>Аномальный тик кадра App — ждёт конца кадра (пишется с ним).</summary>
    internal sealed class PendingTick(int capacity)
    {
        public bool Active;
        public ulong Number;
        public long Start, Time, Median, Threshold;
        public ProfileHeader Header;
        public AnomalyCauses Causes;
        public int Zone;
        public long ZoneTime, ZoneExcess;
        public long[] ZoneTimes = new long[capacity], Bytes = new long[capacity], Mean = new long[capacity];
        public int[] Calls = new int[capacity];

        public void Grow(int capacity)
        {
            if (ZoneTimes.Length >= capacity) return;
            Array.Resize(ref ZoneTimes, capacity);
            Array.Resize(ref Bytes, capacity);
            Array.Resize(ref Mean, capacity);
            Array.Resize(ref Calls, capacity);
        }
    }
}

/// <summary>
/// Место копирования аномалии: всё, что писателю нужно для файла, скопировано владельцем в заранее выделенные массивы — строка
/// зон аномального периода (время, байты, вызовы) и среднее зон, родители и виды зон, последние периоды (заголовки, начала,
/// строки зон), тик кадра App, снимки машины; ссылки на события среды и ленту (их читает писатель — они потокобезопасны).
/// </summary>
internal sealed class AnomalySlot
{
    public const int Free = 0, Filled = 1;

    public int State;
    public long Order, Sequence;

    /// <summary>Запись аномалии (как в списке в момент захвата: номер, эпоха, класс, главная зона).</summary>
    public ProfileAnomaly Record;
    public int Stride, Zones, Epoch;

    public ProfilePeriod Period;
    public ulong Number;
    public bool TickTrigger, PeriodHit;
    public ProfileHeader Header;
    public long Start, Median, Threshold;
    public AnomalyCauses Causes;
    public int MainZone;
    public double AbsoluteMs;

    public long[] Time = [], Bytes = [], Mean = [];
    public int[] Calls = [], Parent = [];
    public byte[] Kind = [];

    public bool HasTick;
    public ulong TickNumber;
    public ProfileHeader TickHeader;
    public long TickStart, TickMedian, TickThreshold;
    public AnomalyCauses TickCauses;
    public int TickZone;
    public long[] TickTime = [], TickBytes = [], TickMean = [];
    public int[] TickCalls = [];

    public int Recent;
    public ProfileHeader[] RecentHeaders = [];
    public long[] RecentStart = [], RecentTime = [];
    public int[] RecentCalls = [];

    public int RecentTicks;
    public ProfileHeader[] RecentTickHeaders = new ProfileHeader[ProfileAnomalies.RecentTicksCapacity];
    public long[] RecentTickStart = new long[ProfileAnomalies.RecentTicksCapacity];

    public MachineSample Baseline, Now;
    public bool HasBaseline, ComputeBound;

    public RuntimeEvents? Events;
    public ProfileTimeline? Timeline;

    public AnomalySlot(int stride, int recent) => Allocate(stride, recent);

    public void Allocate(int stride, int recent)
    {
        Stride = stride;
        Time = new long[stride];
        Bytes = new long[stride];
        Mean = new long[stride];
        Calls = new int[stride];
        Parent = new int[stride];
        Kind = new byte[stride];
        TickTime = new long[stride];
        TickBytes = new long[stride];
        TickMean = new long[stride];
        TickCalls = new int[stride];
        RecentHeaders = new ProfileHeader[recent];
        RecentStart = new long[recent];
        RecentTime = new long[recent * stride];
        RecentCalls = new int[recent * stride];
    }

    /// <summary>Конец аномального периода (отметка <see cref="Stopwatch"/>).</summary>
    public long End => Start + Header.Time;

    /// <summary>Начало окна — первого из последних периодов.</summary>
    public long WindowStart => Recent > 0 ? Math.Min(RecentStart[0], Start) : Start;
}
