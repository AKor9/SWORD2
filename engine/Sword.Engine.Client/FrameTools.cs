using Sword.Engine.Diagnostics;

namespace Sword.Engine.Client;

/// <summary>
/// Числа кадра для HUD и агента — прошлого, законченного кадра (этот ещё идёт). <c>HudData</c> SWORD 2eab13b.
/// </summary>
/// <param name="Fps">Кадров в секунду, сглажено (<c>fps += (1/dt − fps)·0,08</c>, с 60).</param>
/// <param name="FrameMs">Шаг прошлого кадра, мс (от конца позапрошлого до конца прошлого).</param>
/// <param name="FrameMsMax">Худший шаг кадра за прошлое окно из 60 кадров, мс.</param>
/// <param name="CpuMs">Работа CPU прошлого кадра, мс: ввод, тики, Extract, запись кадра — без ожидания GPU и показа.</param>
/// <param name="WaitMs">Ожидание GPU в начале прошлого кадра (забор кадра в полёте), мс.</param>
/// <param name="Ticks">Тиков симуляции в этом кадре.</param>
/// <param name="Vsync">Вертикальная синхронизация.</param>
/// <param name="Runtime">Среда за прошлую секунду (точка Пб, <see cref="RuntimeMeter"/>): JIT, GC, загрузка системы другими процессами.</param>
public readonly record struct FrameStats(double Fps, double FrameMs, double FrameMsMax, double CpuMs, double WaitMs, int Ticks, bool Vsync,
    RuntimeLine Runtime = default);

/// <summary>
/// Счётчик <see cref="FrameStats"/> по шагам кадров (<c>Program.cs</c> SWORD: сглаживание FPS 0,08 с начальных 60,
/// максимум шага — по окнам из 60 кадров, показывается максимум прошлого окна).
/// </summary>
public sealed class FrameStatsMeter
{
    public const double Smoothing = 0.08, InitialFps = 60;
    public const int MaxWindow = 60;

    private double _fps = InitialFps, _max, _window;
    private int _frame;

    /// <summary>Итог кадра: его шаг, работа CPU и ожидание GPU, мс.</summary>
    public void Frame(double frameMs, double cpuMs, double waitMs)
    {
        // SWORD считает в начале кадра по шагу прошлого; здесь то же в конце кадра, порядок тот же.
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
        Runtime.Frame(System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public double Fps => _fps;
    public double FrameMs { get; private set; }
    public double FrameMsMax => _max;
    public double CpuMs { get; private set; }
    public double WaitMs { get; private set; }

    public FrameStats Stats(int ticks, bool vsync) => new(_fps, FrameMs, _max, CpuMs, WaitMs, ticks, vsync, Runtime.Line);

    /// <summary>Строка среды для HUD (точка Пб): опрос раз в секунду, и без профайлера (<see cref="RuntimeMeter"/>).</summary>
    public RuntimeMeter Runtime { get; } = new();
}

/// <summary>
/// Простой бенч кадра (<c>--bench N</c>, <c>BenchRecorder</c> SWORD 2eab13b без метрик арены и GPU-профайлера): идёт по
/// ИГРОВОМУ времени — первые <see cref="Warmup"/> с прогрев, дальше копит шаги кадров до N с; итог — среднее, перцентили,
/// худший кадр, CPU и ожидание GPU.
/// </summary>
public sealed class FrameBench
{
    /// <summary>Прогрев, с игрового времени.</summary>
    public const double Warmup = 4.0;

    private readonly List<double> _frames = [];
    private double _cpu, _wait;

    /// <param name="seconds">Конец замера, с игрового времени; больше <see cref="Warmup"/>.</param>
    public FrameBench(double seconds)
    {
        if (!(seconds > Warmup) || !double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"бенч — больше {Warmup} с игрового времени (прогрев)");
        Seconds = seconds;
        // Запас на минуту замера при ~1000 кадрах/с: список не растёт посреди замера (рост — выделение в кадре).
        _frames.Capacity = 1 << 16;
    }

    public double Seconds { get; }

    public int Frames => _frames.Count;

    public static bool Measuring(double simTime) => simTime >= Warmup;

    /// <summary>
    /// Замер окончен: этот кадр последний (по нему же снимок). Его шаг в замер не идёт — клиент не зовёт для него
    /// <see cref="Frame"/> (в SWORD шаг кадра меряется в начале следующего, а после последнего цикл кончается).
    /// </summary>
    public bool Done(double simTime) => simTime >= Seconds && _frames.Count > 0;

    /// <summary>В конце кадра: игровое время после его тиков, шаг кадра, CPU, ожидание GPU.</summary>
    public void Frame(double simTime, double frameMs, double cpuMs, double waitMs)
    {
        if (!Measuring(simTime)) return;
        _frames.Add(frameMs);
        _cpu += cpuMs;
        _wait += waitMs;
    }

    /// <summary>Итог; кадров нет — null. Перцентили — как в SWORD: сортировка, p50 = [n/2], p99 = [⌊0,99·n⌋].</summary>
    public FrameBenchReport? Result()
    {
        if (_frames.Count == 0) return null;
        var sorted = _frames.ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        double avg = sorted.Average();
        return new FrameBenchReport(Seconds, n, Math.Round(avg, 3), Math.Round(1000 / avg, 1), Math.Round(sorted[n / 2], 3),
            Math.Round(sorted[(int)(n * 0.99)], 3), Math.Round(sorted[^1], 3), Math.Round(_cpu / n, 3), Math.Round(_wait / n, 3));
    }
}

/// <summary>Итог бенча кадра; времена — мс на кадр.</summary>
public sealed record FrameBenchReport(double Seconds, int Frames, double AverageMs, double Fps, double P50Ms, double P99Ms,
    double MaxMs, double CpuMs, double WaitMs)
{
    public override string ToString() =>
        $"кадров {Frames}, кадр ср {AverageMs:F2} мс ({Fps:F0} FPS), p50 {P50Ms:F2}, p99 {P99Ms:F2}, макс {MaxMs:F2}; " +
        $"CPU {CpuMs:F2} мс, ожидание GPU {WaitMs:F2} мс";
}
