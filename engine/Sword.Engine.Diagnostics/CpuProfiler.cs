using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Diagnostics;

/// <summary>Период профайлера: кадр клиента или тик симуляции.</summary>
public enum ProfilePeriod
{
    Frame,
    Tick,
}

/// <summary>
/// Вид зоны (точка Пв): обычная работа, ожидание GPU (забор кадра в полёте, показ) или простой (ограничитель кадров). По виду
/// захват аномалий делит лишнее время периода на «код» и «ожидание GPU»; итогов вид не меняет. Точка Пг: <see cref="Gpu"/> —
/// GPU-время прохода, которое пишет рендер (<see cref="CpuProfiler.RecordGpu"/>), а не <c>Begin/End</c>: вне дерева CPU, в
/// разбор времени кадра и в сводку F2 зон CPU не входит; в отчёте — своя секция <c>gpu</c>.
/// </summary>
public enum ProfileZoneKind : byte
{
    Work,
    GpuWait,
    Idle,
    Gpu,
}

/// <summary>
/// Заголовок периода в кольце профайлера: номер кадра или тика, тики кадра (первый и сколько), доля тика кадра, время и
/// выделенные байты периода, служебный ли кадр; у тика — кадр, в котором он сыгран (<see cref="ulong.MaxValue"/> — вне кадра);
/// счётчики среды за период (точка Пб: такты потока, сборки и пауза GC, выделения процесса, JIT). Время — в отсчётах
/// <see cref="Stopwatch"/>. Точка Пг: <paramref name="GpuFrame"/> — кадр, чьи GPU-времена записаны в этот кадр (зоны вида
/// <see cref="ProfileZoneKind.Gpu"/>; обычно этот минус 3 — кадры в полёте), <see cref="ulong.MaxValue"/> — не записаны.
/// После Пв (решение пользователя): <paramref name="Tools"/> — кадр инструментов (точка обслуживания выполняла запросы агента):
/// служебный (<paramref name="Service"/> тоже истина) — в кольце с этой пометкой, не в итогах и не судится как кадр.
/// </summary>
public readonly record struct ProfileHeader(ulong Number, ulong FirstTick, int Ticks, float Alpha, long Time, long Bytes, bool Service,
    ulong Frame, ProfileCounters Counters, ulong GpuFrame = ulong.MaxValue, bool Tools = false);

/// <summary>
/// Профайлер CPU (раздел 3 плана, точка Па): зоны с номерами, заведёнными заранее (<see cref="Zone"/> — при сборке мира,
/// графа, реестра инвариантов), и стабильными именами; начало и конец зоны (<see cref="Begin"/>/<see cref="End"/>) — время
/// (<see cref="Stopwatch.GetTimestamp"/>) и выделенные байты потока (<see cref="GC.GetAllocatedBytesForCurrentThread"/>);
/// иерархия — стек открытых зон; агрегат на кадр (<see cref="BeginFrame"/>/<see cref="EndFrame"/>) и на тик
/// (<see cref="BeginTick"/>/<see cref="EndTick"/>) — мс, байты, вызовы каждой зоны — в заранее выделенных массивах; кольцо
/// последних периодов с заголовками (тики кадра, доля тика). Итоги — <see cref="Stats"/>, <see cref="Report"/>.
///
/// Выключен по умолчанию. Выключенный стоит проверку поля <c>bool</c> на вызов: <see cref="Begin"/> и <see cref="End"/>
/// встраиваются (<see cref="MethodImplOptions.AggressiveInlining"/>), работа — за флагом в отдельном невстраиваемом методе.
/// Поле — обычное, не <c>static readonly</c>: такое JIT уровня 1 сворачивает в константу, и выключенный стоил бы ровно 0, но
/// тогда его не включить на ходу (<c>profile.stages</c>). Включённый в установившемся режиме не выделяет памяти (кольцо —
/// при включении, зоны — при регистрации).
///
/// Однопоточный: зоны, периоды, включение и чтение итогов — на потоке владельца (главный поток App, поток симуляции
/// Headless; агент — через точку обслуживания). Включение и выключение действуют с ближайшей границы верхнего периода
/// (<see cref="SetEnabled"/>): кадр или тик, открытый раньше, не бывает наполовину замеренным. Несогласованный
/// <see cref="End"/> — исключение в Debug, в Release — счётчик <see cref="Mismatches"/> и раскрутка стека до этой зоны.
/// Симуляцию не трогает: только читает часы и счётчики (в хеш и реплей не входит).
/// </summary>
public sealed partial class CpuProfiler
{
    /// <summary>Зона кадра (корень кадра) и зона тика — номера заведены всегда.</summary>
    public const int FrameZone = 0, TickZone = 1;

    /// <summary>Глубже — зона не открывается (<see cref="Overflows"/>).</summary>
    public const int MaxDepth = 64;

    /// <summary>Периодов в кольце по умолчанию (кадров и тиков — каждых).</summary>
    public const int DefaultHistory = 1024;

    /// <summary>
    /// Общий выключенный профайлер — «профайлера нет»: зоны не заводятся (номер 0), включить нельзя. Им инициализируются
    /// места замера, пока владелец не подключил свой.
    /// </summary>
    public static readonly CpuProfiler Off = new(0, off: true);

    private const int NoParent = -1, Unseen = -2, Several = -3;

    private readonly bool _off;
    private readonly int _history;
    private bool _enabled, _pending, _wanted;

    private readonly List<string> _names = [];
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private int[] _parent = [];
    private byte[] _kind = [];
    private int _capacity;

    private readonly int[] _stackZone = new int[MaxDepth];
    private readonly long[] _stackTime = new long[MaxDepth], _stackBytes = new long[MaxDepth];
    private int _depth;

    private readonly Series _frames, _ticks;
    /// <summary>Место зоны кадра и тика в стеке; −1 — период не открыт.</summary>
    private int _frameAt = -1, _tickAt = -1;

    /// <param name="history">Периодов в кольце кадров и в кольце тиков (0 — без кольца: только средние и максимумы).</param>
    public CpuProfiler(int history = DefaultHistory) : this(history, off: false)
    {
    }

    private CpuProfiler(int history, bool off)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(history);
        _off = off;
        _history = history;
        _frames = new Series(this);
        _ticks = new Series(this);
        if (off) return;
        Zone("frame");
        Zone("tick");
    }

    /// <summary>Зоны замеряются.</summary>
    public bool Enabled => _enabled;

    /// <summary>Что заказано <see cref="SetEnabled"/> (действует с ближайшей границы верхнего периода).</summary>
    public bool Requested => !_off && _wanted;

    /// <summary>Несогласованных <see cref="End"/> (в Release; в Debug — исключение).</summary>
    public long Mismatches { get; private set; }

    /// <summary>Зон, не открытых из-за глубины стека больше <see cref="MaxDepth"/>.</summary>
    public long Overflows { get; private set; }

    /// <summary>Периодов, брошенных недоконченными (исключение посреди тика или кадра: следующий начался, а этот не кончился).</summary>
    public long Abandoned { get; private set; }

    /// <summary>Глубина стека открытых зон (для раскрутки после исключения — <see cref="UnwindTo"/>).</summary>
    public int Depth => _depth;

    /// <summary>Периодов в кольце.</summary>
    public int History => _history;

    private double _displayHz;

    /// <summary>
    /// Частота обновления монитора, на котором показываются кадры, Гц; 0 — неизвестна (Headless, окно не сказало). Задаёт владелец
    /// кадров (клиент — по окну). По ней захват аномалий судит «заметность» аномалии кадра (после Пв, решение пользователя):
    /// кадр не короче интервала показа <c>1000 / Гц</c> мс — хотя бы одно обновление экрана показало прежний кадр; неизвестна —
    /// <see cref="ProfileAnomalies.DefaultDisplayIntervalMs"/> (60 Гц). Менять — на потоке владельца.
    /// </summary>
    public double DisplayHz
    {
        get => _displayHz;
        set
        {
            _displayHz = double.IsFinite(value) && value > 0 ? value : 0;
            _anomalies?.SetDisplay(_displayHz);
        }
    }

    /// <summary>Заведённые зоны по номерам.</summary>
    public IReadOnlyList<string> Zones => _names;

    /// <summary>Имя зоны по номеру.</summary>
    public string NameOf(int zone) => (uint)zone < (uint)_names.Count ? _names[zone] : $"#{zone}";

    /// <summary>
    /// Номер зоны по стабильному имени: есть — тот же, нет — заводится (не горячий путь: выделяет память). У
    /// <see cref="Off"/> — всегда 0, без регистрации.
    /// </summary>
    public int Zone(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_off) return 0;
        if (_ids.TryGetValue(name, out int id)) return id;
        id = _names.Count;
        lock (_names) _names.Add(name);   // имена читает и поток слушателя событий среды (SharedName)
        _ids.Add(name, id);
        if (id >= _capacity) Grow(Math.Max(64, _capacity * 2));
        _parent[id] = Unseen;
        return id;
    }

    /// <summary>Номер зоны с видом (точка Пв: ожидание GPU, простой); вид задаётся при первой регистрации с ним.</summary>
    public int Zone(string name, ProfileZoneKind kind)
    {
        int id = Zone(name);
        if (!_off && kind != ProfileZoneKind.Work) _kind[id] = (byte)kind;
        return id;
    }

    /// <summary>Вид зоны.</summary>
    public ProfileZoneKind KindOf(int zone) => (uint)zone < (uint)_names.Count ? (ProfileZoneKind)_kind[zone] : ProfileZoneKind.Work;

    /// <summary>Номер зоны, если она заведена; иначе −1.</summary>
    public int Find(string name) => _ids.TryGetValue(name, out int id) ? id : -1;

    /// <summary>
    /// Родитель зоны — зона, под которой она открывалась: номер; −1 — корень (не открывалась ни под какой), −2 — не встречалась,
    /// −3 — под разными.
    /// </summary>
    public int ParentOf(int zone) => (uint)zone < (uint)_names.Count ? _parent[zone] : Unseen;

    /// <summary>Это общий выключенный <see cref="Off"/> (его не включить).</summary>
    public bool IsOff => _off;

    /// <summary>
    /// Включить или выключить. Действует сразу, если ни кадр, ни тик не открыт; иначе — с начала следующего верхнего
    /// периода (кадра, а без кадров — тика). Кольцо выделяется при первом включении.
    /// </summary>
    /// <exception cref="InvalidOperationException">Это <see cref="Off"/>.</exception>
    public void SetEnabled(bool enabled)
    {
        if (_off) throw new InvalidOperationException("CpuProfiler.Off не включается: подключите свой профайлер");
        _wanted = enabled;
        // Кадр или тик идёт (и у выключенного: он помнит, что период начат) — включение ждёт его конца.
        if (_depth == 0 && !_inFrame && !_inTick) Apply();
        else _pending = Changed;
    }

    /// <summary>
    /// Идёт кадр или тик — отмечается и выключенным профайлером (одна запись поля на период, не на зону): иначе включение
    /// посреди кадра (агент в точке обслуживания) застало бы открытые снаружи зоны без начала. У <see cref="Off"/> не
    /// пишется: он общий для всех потоков (ветки <c>experiment.fork</c> идут параллельно) и не включается.
    /// </summary>
    private bool _inFrame, _inTick;

    /// <summary>Открытый кадр или тик прерван ожиданием (<see cref="Interrupt"/>): закроется служебным.</summary>
    private bool _interruptedFrame, _interruptedTick;

    /// <summary>
    /// Открытые кадр и тик прерваны ожиданием вне кода (останов агента посреди тика — <c>time.break</c>, точка Пв): закроются
    /// служебными — в кольце, но не в итогах и не судятся захватом аномалий (иначе минуты ожидания агента были бы «аномалией»
    /// и испортили бы средние и максимум). У <see cref="Off"/> и без открытого периода — ничего.
    /// </summary>
    public void Interrupt()
    {
        if (_off) return;
        if (_inFrame) _interruptedFrame = true;
        if (_inTick) _interruptedTick = true;
    }

    private void Apply()
    {
        _pending = false;
        if (_wanted && _frames.Headers is null && _history > 0)
        {
            _frames.Allocate(_history, _capacity);
            _ticks.Allocate(_history, _capacity);
        }
        if (_wanted && !_enabled) StartWindow();
        _enabled = _wanted;
        _timeline = _enabled ? _wantedTimeline : null;
        if (_enabled) _anomalies?.RequestWarm();   // точка Пв: писатель аномалий компилирует свой код сейчас, а не посреди прогона
    }

    /// <summary>
    /// Забыть итоги и кольца (после прогрева); открытый период досчитывается и попадает в новые итоги. Включённый — ещё начало
    /// окна машины (<see cref="Machine"/>) и итогов событий среды.
    /// </summary>
    public void Reset()
    {
        _frames.Reset();
        _ticks.Reset();
        Mismatches = Overflows = Abandoned = 0;
        if (_enabled) StartWindow();
        _events?.Reset();
        _anomalies?.OnReset();   // список аномалий не забывается: новая эпоха (аномалии замера — после сброса)
    }

    // ── зоны ───────────────────────────────────────────────────────────────

    /// <summary>Открыть зону (номер — из <see cref="Zone"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Begin(int zone)
    {
        if (_enabled) Push(zone);
    }

    /// <summary>Закрыть зону — ту же, что открыта последней.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void End(int zone)
    {
        if (_enabled) Pop(zone);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Push(int zone)
    {
        int d = _depth;
        if (d == MaxDepth)
        {
            Overflows++;
            return;
        }
        _stackZone[d] = zone;
        _stackBytes[d] = GC.GetAllocatedBytesForCurrentThread();
        long now = Stopwatch.GetTimestamp();
        _stackTime[d] = now;
        _depth = d + 1;
        _timeline?.Write(now, zone, d + 1, ProfileTimeline.Kind.Begin);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Pop(int zone)
    {
        long now = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        int d = _depth - 1;
        if (d < 0 || _stackZone[d] != zone)
        {
            if (!Mismatch(zone)) return;
            d = _depth - 1;
        }
        Close(d, now - _stackTime[d], bytes - _stackBytes[d]);
        _timeline?.Write(now, zone, d, ProfileTimeline.Kind.End);
    }

    /// <summary>Зона на месте <paramref name="d"/> стека закрыта: в период(ы), родитель — зона под ней.</summary>
    private void Close(int d, long time, long bytes)
    {
        int zone = _stackZone[d];
        _depth = d;
        int parent = d > 0 ? _stackZone[d - 1] : NoParent;
        int known = _parent[zone];
        if (known != parent) _parent[zone] = known == Unseen ? parent : Several;
        if (_frameAt >= 0) _frames.Add(zone, time, bytes);
        if (_tickAt >= 0) _ticks.Add(zone, time, bytes);
    }

    /// <summary>
    /// <see cref="End"/> не той зоны. Debug — исключение (ошибка разметки зон); Release — счётчик и раскрутка до этой зоны
    /// (true — она найдена и лежит наверху), иначе вызов пропускается.
    /// </summary>
    private bool Mismatch(int zone)
    {
#if DEBUG
        throw new InvalidOperationException($"профайлер: End({NameOf(zone)}), а открыта {(_depth > 0 ? NameOf(_stackZone[_depth - 1]) : "ни одна зона")}");
#else
        Mismatches++;
        for (int i = _depth - 1; i >= 0; i--)
            if (_stackZone[i] == zone)
            {
                if (i <= Math.Max(_frameAt, _tickAt)) return false;   // зону периода закрывает только его конец
                _depth = i + 1;
                MarkDepth();
                return true;
            }
        return false;
#endif
    }

    /// <summary>
    /// Раскрутить стек до глубины <paramref name="depth"/> (<see cref="Depth"/> до участка, который бросил исключение): зоны
    /// выше не замеряются. Периоды не трогает: зону открытого кадра или тика не снимает.
    /// </summary>
    public void UnwindTo(int depth)
    {
        int floor = Math.Max(_frameAt, _tickAt) + 1;
        if (depth < floor) depth = floor;
        if (depth < _depth)
        {
            _depth = depth;
            MarkDepth();
        }
    }

    // ── периоды ────────────────────────────────────────────────────────────

    /// <summary>
    /// Начало кадра <paramref name="number"/> (зона <c>frame</c>). Недоконченный прошлый кадр бросается
    /// (<see cref="Abandoned"/>); здесь же вступает в силу <see cref="SetEnabled"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginFrame(ulong number)
    {
        if (_off) return;   // общий Off только читается: его делят потоки (ветки experiment.fork)
        _inFrame = true;
        _inTick = false;
        if (_enabled | _pending) OpenFrame(number);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenFrame(ulong number)
    {
        if (_frameAt >= 0 || _tickAt >= 0)
        {
            Abandoned++;
            DropFrame();
        }
        _depth = 0;
        _interruptedFrame = false;
        if (_pending) Apply();
        if (!_enabled) return;
        _frames.Open(number);
        _frameAt = 0;
        _timeline?.Write(Stopwatch.GetTimestamp(), FrameZone, 0, ProfileTimeline.Kind.FrameOpen, (long)number);
        _frames.Start = ReadCounters();
        Push(FrameZone);
        _frames.StartTs = _stackTime[0];
    }

    /// <summary>Конец кадра: доля тика кадра и служебный ли он (служебные в средние и перцентили не входят).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndFrame(float alpha, bool service)
    {
        if (_off) return;
        _inFrame = false;
        if (_enabled) CloseFrame(alpha, service, tools: false);
    }

    /// <summary>
    /// Конец кадра; <paramref name="tools"/> — кадр инструментов (после Пв, решение пользователя: точка обслуживания выполняла
    /// запросы агента): служебный — в кольце с пометкой <see cref="ProfileHeader.Tools"/>, не в средних и перцентилях, как кадр не
    /// судится; аномальный тик этого кадра — судится (тики сыграны до точки обслуживания, работа агента их не удлиняет).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndFrame(float alpha, bool service, bool tools)
    {
        if (_off) return;
        _inFrame = false;
        if (_enabled) CloseFrame(alpha, service, tools);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CloseFrame(float alpha, bool service, bool tools)
    {
        if (_frameAt < 0) return;
        if (_tickAt >= 0)
        {
            Abandoned++;
            DropTick();
        }
        if (_depth != _frameAt + 1) Unbalanced(FrameZone);
        Pop(FrameZone);
        service |= _interruptedFrame;
        _interruptedFrame = false;
        var counters = ReadCounters().Since(_frames.Start);
        _timeline?.Write(Stopwatch.GetTimestamp(), FrameZone, _depth, ProfileTimeline.Kind.FrameClose, (long)_frames.Number);
        if (_anomalies is not null) FrameAnomaly(alpha, service, tools, counters);   // точка Пв: аномалия кадра или его тика
        _frames.Record(alpha, service | tools, ulong.MaxValue, counters, tools);
        _frameAt = -1;
    }

    /// <summary>Бросить открытый кадр (выход из цикла посреди кадра, свёрнутое окно): в итоги он не идёт.</summary>
    public void AbandonFrame()
    {
        if (_off) return;
        _inFrame = _inTick = false;
        if (_frameAt >= 0) DropFrame();
    }

    private void DropFrame()
    {
        if (_tickAt >= 0) DropTick();
        if (_frameAt >= 0)
        {
            _depth = _frameAt;
            _timeline?.Write(Stopwatch.GetTimestamp(), FrameZone, _depth, ProfileTimeline.Kind.FrameClose, (long)_frames.Number);
        }
        _frameAt = -1;
        _frames.Clear();
        _anomalies?.DropPending();   // аномальный тик брошенного кадра — без кадра не пишется
    }

    /// <summary>
    /// Начало тика <paramref name="tick"/> (зона <c>tick</c>): внутри кадра — тик кадра; без кадров (Headless) — верхний
    /// период, и здесь вступает в силу <see cref="SetEnabled"/>. Недоконченный прошлый тик (исключение в системе) бросается.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginTick(ulong tick)
    {
        if (_off) return;   // общий Off только читается: его делят потоки (ветки experiment.fork)
        _inTick = true;
        if (_enabled | _pending) OpenTick(tick);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenTick(ulong tick)
    {
        if (_tickAt >= 0)
        {
            Abandoned++;
            DropTick();
        }
        // Без кадров тик — верхний период: здесь вступает в силу включение (если снаружи нет открытых зон).
        if (!_inFrame && _depth == 0 && _pending) Apply();
        _interruptedTick = false;
        if (!_enabled) return;
        if (_frameAt >= 0) _frames.NoteTick(tick);
        _ticks.Open(tick);
        _tickAt = _depth;
        _timeline?.Write(Stopwatch.GetTimestamp(), TickZone, _depth, ProfileTimeline.Kind.TickOpen, (long)tick);
        _ticks.Start = ReadCounters();
        Push(TickZone);
        _ticks.StartTs = _stackTime[_tickAt];
    }

    /// <summary>Конец тика.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndTick()
    {
        if (_off) return;
        _inTick = false;
        if (_enabled) CloseTick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CloseTick()
    {
        if (_tickAt < 0) return;
        if (_depth != _tickAt + 1) Unbalanced(TickZone);
        Pop(TickZone);
        bool interrupted = _interruptedTick;
        _interruptedTick = false;
        var counters = ReadCounters().Since(_ticks.Start);
        _timeline?.Write(Stopwatch.GetTimestamp(), TickZone, _depth, ProfileTimeline.Kind.TickClose, (long)_ticks.Number);
        // Точка Пв: аномалия тика (в кадре App — пишется с кадром); тик, прерванный ожиданием агента, — служебный, не судится.
        if (_anomalies is not null && !interrupted) TickAnomaly(counters);
        _ticks.Record(1, service: interrupted, _frameAt >= 0 ? _frames.Number : ulong.MaxValue, counters);
        _tickAt = -1;
    }

    private void DropTick()
    {
        _depth = _tickAt;
        _tickAt = -1;
        _ticks.Clear();
        _timeline?.Write(Stopwatch.GetTimestamp(), TickZone, _depth, ProfileTimeline.Kind.TickClose, (long)_ticks.Number);
    }

    /// <summary>В периоде остались открытые зоны: Debug — исключение, Release — счётчик и раскрутка до зоны периода.</summary>
    private void Unbalanced(int period)
    {
#if DEBUG
        string open = string.Join(", ", Enumerable.Range(0, _depth).Select(i => NameOf(_stackZone[i])));
        throw new InvalidOperationException($"профайлер: конец периода {NameOf(period)}, а открыты зоны: {open}");
#else
        Mismatches++;
        _depth = (period == FrameZone ? _frameAt : _tickAt) + 1;
#endif
    }

    // ── итоги ──────────────────────────────────────────────────────────────

    /// <summary>Периодов в итогах (без служебных) и служебных.</summary>
    public (int Periods, int Service) Count(ProfilePeriod period)
    {
        var s = Of(period);
        return (s.Count, s.Service);
    }

    /// <summary>Кадров инструментов с последнего сброса (после Пв: часть служебных — <see cref="ProfileHeader.Tools"/>).</summary>
    public int ToolFrames => _frames.ToolFrames;

    /// <summary>Последние <paramref name="count"/> периодов кольца (служебные — тоже), от старых к новым.</summary>
    public IReadOnlyList<ProfileHeader> Recent(ProfilePeriod period, int count)
    {
        var s = Of(period);
        int n = Math.Min(count, s.Filled);
        var list = new ProfileHeader[n];
        for (int i = 0; i < n; i++) list[i] = s.Headers![(s.Next - n + i + s.Capacity) % s.Capacity];
        return list;
    }

    /// <summary>Время одной зоны в периоде кольца: мс, байты, вызовы (<paramref name="back"/> — 0: последний период).</summary>
    public (double Ms, long Bytes, int Calls) InRecent(ProfilePeriod period, int back, int zone)
    {
        var s = Of(period);
        if (back < 0 || back >= s.Filled || (uint)zone >= (uint)_names.Count) return (0, 0, 0);
        int slot = (s.Next - 1 - back + s.Capacity) % s.Capacity;
        int at = slot * s.Stride + zone;
        return (Ms(s.RingTime![at]), s.RingBytes![at], s.RingCalls![at]);
    }

    /// <summary>
    /// Зоны за период: среднее, максимум, вызовы, байты и доля периодов с зоной — по всем периодам с последнего
    /// <see cref="Reset"/> (без служебных); медиана и 99-й перцентиль — по окну кольца; своё время — среднее минус средние
    /// дочерних зон (родитель — зона, под которой открывалась; разные родители — зона ни к кому не причисляется).
    /// Порядок — по среднему, от большего.
    /// </summary>
    public IReadOnlyList<ProfileZoneStats> Stats(ProfilePeriod period)
    {
        var s = Of(period);
        int zones = _names.Count;
        var result = new List<ProfileZoneStats>();
        if (s.Count == 0) return result;
        var window = new List<int>();
        for (int i = 0; i < s.Filled; i++)
        {
            int slot = (s.Next - s.Filled + i + s.Capacity) % s.Capacity;
            if (!s.Headers![slot].Service) window.Add(slot);
        }
        var children = new double[zones];
        for (int z = 0; z < zones; z++)
            if (s.Hits[z] > 0 && _parent[z] >= 0) children[_parent[z]] += Ms(s.TotalTime[z]) / s.Count;
        var values = new double[window.Count];
        for (int z = 0; z < zones; z++)
        {
            if (s.Hits[z] == 0) continue;
            double median = double.NaN, p99 = double.NaN;
            if (window.Count > 0)
            {
                for (int i = 0; i < window.Count; i++) values[i] = Ms(s.RingTime![window[i] * s.Stride + z]);
                Array.Sort(values);
                median = values[values.Length / 2];
                p99 = values[Math.Min(values.Length - 1, (int)(values.Length * 0.99))];
            }
            double mean = Ms(s.TotalTime[z]) / s.Count;
            result.Add(new ProfileZoneStats(_names[z], _parent[z] >= 0 ? _names[_parent[z]] : null, mean, median, p99, Ms(s.MaxTime[z]),
                Math.Max(0, mean - children[z]), s.TotalBytes[z] / (double)s.Count, s.TotalCalls[z] / (double)s.Count, s.Hits[z] / (double)s.Count,
                (ProfileZoneKind)_kind[z]));
        }
        result.Sort((a, b) => b.MeanMs.CompareTo(a.MeanMs));
        return result;
    }

    /// <summary>Суммы времени зон по кадрам с последнего сброса (без служебных) и число кадров — для сводки HUD (<see cref="ProfileDigest"/>).</summary>
    internal (long[] Total, int Count) FrameTotals() => (_frames.TotalTime, _frames.Count);

    /// <summary>Отсчёты <see cref="Stopwatch"/> в миллисекунды.</summary>
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private Series Of(ProfilePeriod period) => period == ProfilePeriod.Frame ? _frames : _ticks;

    private void Grow(int capacity)
    {
        Array.Resize(ref _parent, capacity);
        Array.Resize(ref _kind, capacity);
        _frames.Grow(capacity);
        _ticks.Grow(capacity);
        GrowAnomalies(capacity);
        _capacity = capacity;
    }

    /// <summary>
    /// Ряд периодов одного вида: накопители открытого периода, итоги с последнего сброса (без служебных) и кольцо
    /// последних периодов (строка на период — время, байты, вызовы каждой зоны).
    /// </summary>
    private sealed class Series(CpuProfiler owner)
    {
        public long[] Time = [], Bytes = [];
        public int[] Calls = [];
        public long[] TotalTime = [], TotalBytes = [], TotalCalls = [], MaxTime = [];
        public int[] Hits = [];
        public int Count, Service, ToolFrames;

        /// <summary>Счётчики среды на начале открытого периода и итоги с последнего сброса (без служебных; точка Пб).</summary>
        public ProfileCounters Start, Totals;
        public int GcPeriods, JitPeriods, JitMainPeriods;
        public long MaxPauseTicks, MaxJitTicks;

        public int Capacity, Stride, Next, Filled;
        public ProfileHeader[]? Headers;
        public long[]? RingTime, RingBytes;
        public int[]? RingCalls;

        /// <summary>Начало периода в кольце — отметка <see cref="Stopwatch"/> (точка Пв: Chrome Trace, окно аномалии).</summary>
        public long[]? RingStart;

        public ulong Number, FirstTick;
        public int Ticks;

        /// <summary>Кадр, чьи GPU-времена записаны в открытый период (точка Пг); <see cref="ulong.MaxValue"/> — нет.</summary>
        public ulong GpuFrame = ulong.MaxValue;

        /// <summary>Начало открытого периода — отметка <see cref="Stopwatch"/> открытия его зоны.</summary>
        public long StartTs;

        /// <summary>Место периода <paramref name="back"/> назад в кольце (0 — последний записанный).</summary>
        public int Slot(int back) => (Next - 1 - back + Capacity) % Capacity;

        public void Allocate(int capacity, int stride)
        {
            Capacity = capacity;
            Stride = stride;
            Headers = new ProfileHeader[capacity];
            RingStart = new long[capacity];
            RingTime = new long[capacity * stride];
            RingBytes = new long[capacity * stride];
            RingCalls = new int[capacity * stride];
            Next = Filled = 0;
        }

        public void Grow(int capacity)
        {
            Array.Resize(ref Time, capacity);
            Array.Resize(ref Bytes, capacity);
            Array.Resize(ref Calls, capacity);
            Array.Resize(ref TotalTime, capacity);
            Array.Resize(ref TotalBytes, capacity);
            Array.Resize(ref TotalCalls, capacity);
            Array.Resize(ref MaxTime, capacity);
            Array.Resize(ref Hits, capacity);
            if (Headers is null) return;
            // Строки кольца — шире: каждая переносится на новое место (регистрация зоны — не горячий путь).
            var time = new long[Capacity * capacity];
            var bytes = new long[Capacity * capacity];
            var calls = new int[Capacity * capacity];
            for (int slot = 0; slot < Capacity; slot++)
            {
                Array.Copy(RingTime!, slot * Stride, time, slot * capacity, Stride);
                Array.Copy(RingBytes!, slot * Stride, bytes, slot * capacity, Stride);
                Array.Copy(RingCalls!, slot * Stride, calls, slot * capacity, Stride);
            }
            RingTime = time;
            RingBytes = bytes;
            RingCalls = calls;
            Stride = capacity;
        }

        public void Open(ulong number)
        {
            Number = number;
            FirstTick = ulong.MaxValue;
            GpuFrame = ulong.MaxValue;
            Ticks = 0;
        }

        public void NoteTick(ulong tick)
        {
            if (Ticks == 0) FirstTick = tick;
            Ticks++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int zone, long time, long bytes)
        {
            Time[zone] += time;
            Bytes[zone] += bytes;
            Calls[zone]++;
        }

        public void Record(float alpha, bool service, ulong frame, in ProfileCounters counters, bool tools = false)
        {
            int zones = owner._names.Count;
            int root = ReferenceEquals(this, owner._frames) ? FrameZone : TickZone;
            if (!service)
            {
                Totals = Totals.Plus(counters);
                if (counters.Collections > 0) GcPeriods++;
                if (counters.JitProcess > 0) JitPeriods++;
                if (counters.JitMethods > 0) JitMainPeriods++;
                if (counters.PauseTicks > MaxPauseTicks) MaxPauseTicks = counters.PauseTicks;
                if (counters.JitProcessTicks > MaxJitTicks) MaxJitTicks = counters.JitProcessTicks;
            }
            if (Headers is not null)
            {
                int slot = Next;
                Headers[slot] = new ProfileHeader(Number, FirstTick, Ticks, alpha, Time[root], Bytes[root], service, frame, counters, GpuFrame, tools);
                RingStart![slot] = StartTs;
                Array.Copy(Time, 0, RingTime!, slot * Stride, zones);
                Array.Copy(Bytes, 0, RingBytes!, slot * Stride, zones);
                Array.Copy(Calls, 0, RingCalls!, slot * Stride, zones);
                Next = (slot + 1) % Capacity;
                if (Filled < Capacity) Filled++;
            }
            if (tools) ToolFrames++;
            if (service) Service++;
            else
            {
                Count++;
                for (int z = 0; z < zones; z++)
                {
                    int calls = Calls[z];
                    if (calls == 0) continue;
                    TotalTime[z] += Time[z];
                    TotalBytes[z] += Bytes[z];
                    TotalCalls[z] += calls;
                    Hits[z]++;
                    if (Time[z] > MaxTime[z]) MaxTime[z] = Time[z];
                }
            }
            Clear();
        }

        public void Clear()
        {
            int zones = owner._names.Count;
            Array.Clear(Time, 0, zones);
            Array.Clear(Bytes, 0, zones);
            Array.Clear(Calls, 0, zones);
        }

        public void Reset()
        {
            Array.Clear(TotalTime);
            Array.Clear(TotalBytes);
            Array.Clear(TotalCalls);
            Array.Clear(MaxTime);
            Array.Clear(Hits);
            Count = Service = ToolFrames = 0;
            Next = Filled = 0;
            Totals = default;
            GcPeriods = JitPeriods = JitMainPeriods = 0;
            MaxPauseTicks = MaxJitTicks = 0;
        }
    }
}

/// <summary>
/// Зона за период (кадр или тик), мс: среднее, медиана, 99-й перцентиль, максимум; своё время (без дочерних), байты и
/// вызовы в среднем на период, доля периодов, где зона была; вид зоны (точка Пг: <see cref="ProfileZoneKind.Gpu"/> — GPU-время
/// прохода, мс на кадр, вне дерева CPU).
/// </summary>
public sealed record ProfileZoneStats(string Name, string? Parent, double MeanMs, double MedianMs, double P99Ms, double MaxMs,
    double SelfMs, double Bytes, double Calls, double Presence, ProfileZoneKind Kind = ProfileZoneKind.Work);
