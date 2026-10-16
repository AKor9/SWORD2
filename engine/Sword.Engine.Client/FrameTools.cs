using Sword.Engine.Diagnostics;

namespace Sword.Engine.Client;

/// <summary>Числа кадра для HUD — прошлого, законченного кадра (этот ещё идёт).</summary>
/// <param name="Fps">Сглажено: <c>fps += (1/dt − fps)·0,08</c>, с 60.</param>
/// <param name="FrameMsMax">Худший шаг кадра за прошлое окно из 60 кадров, мс.</param>
/// <param name="CpuMs">Работа CPU прошлого кадра без ожидания GPU и показа, мс.</param>
/// <param name="WaitMs">Ожидание GPU в начале прошлого кадра (кадр в полёте), мс.</param>
/// <param name="GpuMs">GPU-время последнего разобранного кадра (на 3 раньше прошлого), мс; 0 — ещё нет.</param>
/// <param name="GpuPassesMs">Сумма проходов того же кадра на GPU без барьеров между ними, мс.</param>
public readonly record struct FrameStats(double Fps, double FrameMs, double FrameMsMax, double CpuMs, double WaitMs, int Ticks, bool Vsync,
    double GpuMs = 0, double GpuPassesMs = 0);

/// <summary>Счётчик <see cref="FrameStats"/>: сглаживание FPS 0,08 с начальных 60, максимум шага — за прошлое окно
/// из 60 кадров.</summary>
public sealed class FrameStatsMeter
{
    public const double Smoothing = 0.08, InitialFps = 60;
    public const int MaxWindow = 60;

    private double _fps = InitialFps, _max, _window;
    private int _frame;

    /// <summary>Итог кадра: его шаг, работа CPU и ожидание GPU, мс; GPU-время последнего разобранного кадра и его проходов.</summary>
    public void Frame(double frameMs, double cpuMs, double waitMs, double gpuMs = 0, double gpuPassesMs = 0)
    {
        GpuMs = gpuMs;
        GpuPassesMs = gpuPassesMs;
        _fps += (1 / Math.Max(frameMs / 1000, 1e-4) - _fps) * Smoothing;
        _window = Math.Max(_window, frameMs);
        if (_frame % MaxWindow == 0)
        {
            _max = _window;
            _window = 0;
        }
        FrameMs = frameMs;
        CpuMs = cpuMs;
        WaitMs = waitMs;
        _frame++;
    }

    public double Fps => _fps;
    public double FrameMs { get; private set; }
    public double FrameMsMax => _max;
    public double CpuMs { get; private set; }
    public double WaitMs { get; private set; }
    public double GpuMs { get; private set; }
    public double GpuPassesMs { get; private set; }

    public FrameStats Stats(int ticks, bool vsync) => new(_fps, FrameMs, _max, CpuMs, WaitMs, ticks, vsync, GpuMs, GpuPassesMs);
}

/// <summary>
/// Бенч кадра (<c>--bench N</c>) по ИГРОВОМУ времени: <see cref="Warmup"/> с прогрев, затем шаги кадров до N с; итог —
/// среднее, перцентили, худший кадр, CPU, ожидание GPU, GPU-время и проходы (мс на кадр по разобранным кадрам GPU).
/// </summary>
public sealed class FrameBench
{
    /// <summary>Прогрев, с игрового времени.</summary>
    public const double Warmup = 4.0;

    private readonly List<double> _frames = [];
    private readonly List<double> _gpu = [];
    private readonly Dictionary<string, double> _passes = new(StringComparer.Ordinal);
    private double _cpu, _wait;
    private bool _gpuMeasuring;

    /// <param name="seconds">Конец замера, с игрового времени; больше <see cref="Warmup"/>.</param>
    public FrameBench(double seconds)
    {
        if (!(seconds > Warmup) || !double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"бенч — больше {Warmup} с игрового времени (прогрев)");
        Seconds = seconds;
        // Запас на минуту замера при ~1000 кадрах/с: список не растёт посреди замера (рост — выделение в кадре).
        _frames.Capacity = 1 << 16;
        _gpu.Capacity = 1 << 16;
    }

    public double Seconds { get; }

    public int Frames => _frames.Count;

    /// <summary>Разобранных кадров GPU в замере.</summary>
    public int GpuFrames => _gpu.Count;

    public static bool Measuring(double simTime) => simTime >= Warmup;

    /// <summary>Замер окончен: этот кадр последний (по нему же снимок), его шаг в замер не идёт.</summary>
    public bool Done(double simTime) => simTime >= Seconds && _frames.Count > 0;

    /// <summary>В конце кадра: игровое время после его тиков, шаг кадра, CPU, ожидание GPU.</summary>
    public void Frame(double simTime, double frameMs, double cpuMs, double waitMs)
    {
        if (!Measuring(simTime)) return;
        _frames.Add(frameMs);
        _cpu += cpuMs;
        _wait += waitMs;
    }

    /// <summary>Новый разобранный кадр GPU, мс (раз на новые данные); его проходы — <see cref="GpuPass"/> следом. В
    /// прогреве — не в счёт.</summary>
    public void GpuFrame(double simTime, double frameMs)
    {
        _gpuMeasuring = Measuring(simTime);
        if (_gpuMeasuring) _gpu.Add(frameMs);
    }

    /// <summary>Проход разобранного кадра GPU (после <see cref="GpuFrame"/>), мс; одноимённые в кадре складываются.</summary>
    public void GpuPass(string name, double ms)
    {
        if (!_gpuMeasuring) return;
        System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_passes, name, out _) += ms;
    }

    /// <summary>Итог; кадров нет — null. Перцентили: сортировка, p50 = [n/2], p99 = [⌊0,99·n⌋].</summary>
    public FrameBenchReport? Result()
    {
        if (_frames.Count == 0) return null;
        var sorted = _frames.ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        double avg = sorted.Average();
        double gpu = 0, gpuP99 = 0;
        int g = _gpu.Count;
        if (g > 0)
        {
            var gpuSorted = _gpu.ToArray();
            Array.Sort(gpuSorted);
            gpu = gpuSorted.Average();
            gpuP99 = gpuSorted[(int)(g * 0.99)];
        }
        return new FrameBenchReport(Seconds, n, Math.Round(avg, 3), Math.Round(1000 / avg, 1), Math.Round(sorted[n / 2], 3),
            Math.Round(sorted[(int)(n * 0.99)], 3), Math.Round(sorted[^1], 3), Math.Round(_cpu / n, 3), Math.Round(_wait / n, 3),
            Math.Round(gpu, 4), Math.Round(gpuP99, 4), g,
            g > 0 ? [.. _passes.Select(p => new BenchPass(p.Key, Math.Round(p.Value / g, 4)))] : []);
    }
}

/// <summary>Проход GPU в итоге бенча: сумма за замер / разобранных кадров GPU, мс на кадр.</summary>
public sealed record BenchPass(string Name, double Ms);

/// <summary>Итог бенча; времена — мс на кадр. GPU — по <paramref name="GpuFrames"/> разобранным кадрам замера,
/// <paramref name="Passes"/> — в порядке первой встречи.</summary>
public sealed record FrameBenchReport(double Seconds, int Frames, double AverageMs, double Fps, double P50Ms, double P99Ms,
    double MaxMs, double CpuMs, double WaitMs, double GpuMs = 0, double GpuP99Ms = 0, int GpuFrames = 0, IReadOnlyList<BenchPass>? Passes = null)
{
    public override string ToString() =>
        $"кадров {Frames}, кадр ср {AverageMs:F2} мс ({Fps:F0} FPS), p50 {P50Ms:F2}, p99 {P99Ms:F2}, макс {MaxMs:F2}; " +
        $"CPU {CpuMs:F2} мс, ожидание GPU {WaitMs:F2} мс" + (GpuFrames > 0 ? $"; GPU {GpuMs:F2} мс, p99 {GpuP99Ms:F2}" : "");
}

/// <summary>
/// Сторож кадров проверочного прогона: кадров нет дольше <c>seconds</c> — один раз зовёт <c>stalled</c> из своего
/// потока, чтобы бенч и снимки не висели часами. <see cref="Frame"/> — метка времени без выделений.
/// </summary>
public sealed class FrameWatchdog : IDisposable
{
    private readonly double _seconds;
    private readonly Func<bool> _minimized;
    private readonly Action<string> _stalled;
    private readonly Timer _timer;
    private long _last = System.Diagnostics.Stopwatch.GetTimestamp();
    private int _frames, _fired;

    /// <param name="seconds">Сколько секунд без кадра — «встал».</param>
    /// <param name="minimized">Свёрнуто ли окно сейчас (для слов сообщения).</param>
    /// <param name="stalled">Что делать, когда встал: сообщение словами; зовётся один раз, из потока таймера.</param>
    public FrameWatchdog(double seconds, Func<bool> minimized, Action<string> stalled)
    {
        if (!(seconds > 0) || !double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "секунды — больше 0");
        _seconds = seconds;
        _minimized = minimized;
        _stalled = stalled;
        var period = TimeSpan.FromSeconds(Math.Clamp(seconds / 4, 0.02, 1));
        _timer = new Timer(_ => Check(), null, period, period);
    }

    /// <summary>Сработал ли сторож.</summary>
    public bool Fired => Volatile.Read(ref _fired) != 0;

    /// <summary>Кадр готов (главный поток): отсчёт — заново.</summary>
    public void Frame()
    {
        Volatile.Write(ref _last, System.Diagnostics.Stopwatch.GetTimestamp());
        _frames++;
    }

    /// <summary>Начать отсчёт заново (перед главным циклом: загрузка и первая компиляция — не простой кадров).</summary>
    public void Restart() => Volatile.Write(ref _last, System.Diagnostics.Stopwatch.GetTimestamp());

    private void Check()
    {
        double idle = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _last)).TotalSeconds;
        if (idle < _seconds || Interlocked.Exchange(ref _fired, 1) != 0) return;
        bool minimized;
        try
        {
            minimized = _minimized();
        }
        catch (Exception)
        {
            minimized = false;
        }
        _stalled(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"нет кадров {_seconds:0.#} с (кадров до того {Volatile.Read(ref _frames)}; окно {(minimized ? "свёрнуто" : "не свёрнуто")}) — прогон снят"));
    }

    public void Dispose() => _timer.Dispose();
}
