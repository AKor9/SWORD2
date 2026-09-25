using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Среда и машина за период профайлера: счётчики на кадр и тик (<see cref="ProfileCounters"/>: такты потока,
/// сборки и пауза GC, выделения процесса, JIT главного потока и процесса), итоги (<see cref="RuntimeStats"/>), окно машины
/// с последнего сброса (<see cref="Machine"/>) и диагностический режим (<see cref="SetEvents"/>: события среды с местом в
/// кадре, тике и зоне).
/// </summary>
public sealed record ProfileRuntimeStats(int Periods, double WallMs, double CpuMs, double OnCpu, ProfileCounters Totals, int GcPeriods,
    int JitPeriods, int JitMainPeriods, double MaxPauseMs, double MaxJitMs, double ProcessBytes, double ThreadBytes);

public sealed partial class CpuProfiler
{
    private ProfileTimeline? _timeline, _wantedTimeline;
    private RuntimeEvents? _events;
    private MachineSample _windowStart;
    private bool _windowStarted;

    /// <summary>
    /// Опросов счётчиков среды (по два на период включённого: начало и конец). Выключенный не опрашивает ничего — проверка
    /// «цена ~0» (тест).
    /// </summary>
    public long CounterReads { get; private set; }

    /// <summary>События среды (диагностический режим, <see cref="SetEvents"/>); null — выключен.</summary>
    public RuntimeEvents? Events => _events;

    /// <summary>Лента для мест событий действует (включён профайлер и диагностический режим).</summary>
    public bool Timeline => _timeline is not null;

    /// <summary>
    /// Где был поток-владелец в момент <paramref name="timestamp"/> (отметка <see cref="System.Diagnostics.Stopwatch"/>): кадр,
    /// тик, путь зон — по ленте диагностического режима (так ставятся места событий среды); ленты нет — неизвестно. С любого
    /// потока; не горячий путь.
    /// </summary>
    public TimelinePlace Place(long timestamp) => _timeline?.Find(timestamp) ?? TimelinePlace.Unknown;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ProfileCounters ReadCounters()
    {
        CounterReads++;
        var clock = _clock;
        return clock is null ? ProfileCounters.Read() : clock.Counters();   // поддельные часы (тесты) — их счётчики
    }

    /// <summary>Стек раскручен не концом зоны (исключение, несогласованный конец): лента знает новую глубину.</summary>
    private void MarkDepth() => _timeline?.Write(Now(), 0, _depth, ProfileTimeline.Kind.Depth);

    /// <summary>
    /// Диагностический режим: <paramref name="on"/> — слушатель событий среды (<see cref="RuntimeEvents"/>:
    /// выделения по типам, сборки, паузы, JIT с уровнями) и лента зон для их мест; включает и профайлер. Слушатель создаётся
    /// сразу (не горячий путь: сеанс EventPipe, калибровка часов — миллисекунды), лента начинает писаться с границы верхнего
    /// периода, как включение. Выключение — слушатель закрывается, итоги событий пропадают. Только на потоке владельца.
    /// </summary>
    /// <exception cref="InvalidOperationException">Это <see cref="Off"/>.</exception>
    public void SetEvents(bool on, int timelineCapacity = ProfileTimeline.DefaultCapacity)
    {
        if (_off) throw new InvalidOperationException("CpuProfiler.Off не включается: подключите свой профайлер");
        if (on)
        {
            if (_events is null)
            {
                _wantedTimeline = new ProfileTimeline(timelineCapacity);
                _events = new RuntimeEvents(_wantedTimeline, SharedName, Diagnostics.Machine.CurrentThreadId());
            }
            SetEnabled(true);
            return;
        }
        _events?.Dispose();
        _events = null;
        _wantedTimeline = null;
        SetEnabled(_wanted);
    }

    /// <summary>Заказ отличается от действующего: включение или лента.</summary>
    private bool Changed => _wanted != _enabled || !ReferenceEquals(_timeline, _wanted ? _wantedTimeline : null);

    /// <summary>Имя зоны для другого потока (слушатель событий).</summary>
    internal string SharedName(int zone)
    {
        lock (_names) return (uint)zone < (uint)_names.Count ? _names[zone] : $"#{zone}";
    }

    /// <summary>Начало окна машины — на включении и сбросе (граница замера, не кадр).</summary>
    private void StartWindow()
    {
        _windowStart = MachineSample.Take();
        _windowStarted = true;
    }

    /// <summary>
    /// Машина с начала окна (включение или последний <see cref="Reset"/>) до сейчас: снимается на вызывающем потоке — только
    /// на потоке владельца. Доля CPU для вердикта — тиков без кадров (Headless: счётная работа); у App — нет: кадр ждёт GPU,
    /// а тики внутри кадров на CPU ~91 % и на спокойной машине (время ядра уходит на прерывания видеодрайвера при тысячах
    /// кадров в секунду — их такты потоку не начисляются). null — профайлер не включался.
    /// </summary>
    public MachineReport? Machine()
    {
        if (!_windowStarted) return null;
        var ticks = RuntimeStats(ProfilePeriod.Tick);
        bool frames = _frames.Count > 0 || _frames.Service > 0;
        return MachineReport.Between(_windowStart, MachineSample.Take(), computeBound: false,
            computeOnCpu: !frames && ticks.Periods > 0 && ticks.WallMs > 0 ? ticks.OnCpu : null);
    }

    /// <summary>Среда за период (без служебных, с последнего сброса): среднее настенное и на CPU, доля CPU, счётчики.</summary>
    public ProfileRuntimeStats RuntimeStats(ProfilePeriod period)
    {
        var s = Of(period);
        int root = period == ProfilePeriod.Frame ? FrameZone : TickZone;
        if (s.Count == 0 || s.TotalTime.Length <= root) return new ProfileRuntimeStats(0, 0, 0, 0, default, 0, 0, 0, 0, 0, 0, 0);
        double wall = Ms(s.TotalTime[root]), cpu = s.Totals.CpuMs;
        return new ProfileRuntimeStats(s.Count, wall / s.Count, cpu / s.Count, wall > 0 ? Math.Min(1, cpu / wall) : 0, s.Totals, s.GcPeriods,
            s.JitPeriods, s.JitMainPeriods, s.MaxPauseTicks / (double)TimeSpan.TicksPerMillisecond, s.MaxJitTicks / (double)TimeSpan.TicksPerMillisecond,
            s.Totals.ProcessBytes / (double)s.Count, s.TotalBytes[root] / (double)s.Count);
    }

    /// <summary>Среда за период — JSON отчёта.</summary>
    public static JsonObject RuntimeJson(ProfileRuntimeStats r)
    {
        var (g0, g1, g2) = r.Totals.Exclusive;
        return new JsonObject
        {
            ["wallMs"] = Math.Round(r.WallMs, 5),
            ["cpuMs"] = Math.Round(r.CpuMs, 5),
            ["onCpuPct"] = Math.Round(r.OnCpu * 100, 1),
            ["gc"] = new JsonObject
            {
                ["collections"] = r.Totals.Collections, ["gen0"] = g0, ["gen1"] = g1, ["gen2"] = g2, ["periods"] = r.GcPeriods,
                ["pauseMs"] = Math.Round(r.Totals.PauseMs, 3), ["maxPauseMs"] = Math.Round(r.MaxPauseMs, 3),
            },
            ["jit"] = new JsonObject
            {
                ["methods"] = r.Totals.JitProcess, ["mainThread"] = r.Totals.JitMethods, ["ms"] = Math.Round(r.Totals.JitProcessMs, 3),
                ["mainThreadMs"] = Math.Round(r.Totals.JitMs, 3), ["periods"] = r.JitPeriods, ["mainThreadPeriods"] = r.JitMainPeriods,
                ["maxMs"] = Math.Round(r.MaxJitMs, 3),
            },
            ["processBytes"] = Math.Round(r.ProcessBytes, 1),
            ["threadBytes"] = Math.Round(r.ThreadBytes, 1),
        };
    }

    /// <summary>
    /// Дождаться хвоста событий среды (они приходят с задержкой ~15 мс) — перед отчётом. Без диагностического режима — сразу.
    /// </summary>
    public bool FlushEvents(TimeSpan? timeout = null) => _events?.Flush(timeout ?? TimeSpan.FromSeconds(2)) ?? true;
}
