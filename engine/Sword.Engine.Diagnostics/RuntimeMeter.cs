using System.Diagnostics;
using System.Runtime;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Строка среды для HUD (точка Пб) за прошлое окно <see cref="RuntimeMeter.WindowSeconds"/>: методов JIT процесса, сборок GC
/// и их пауза, загрузка системы другими процессами (доля всех логических процессоров). <see cref="Ready"/> — окно уже было.
/// </summary>
public readonly record struct RuntimeLine(int Jit, int Gc, double PauseMs, double OthersBusy, bool Ready);

/// <summary>
/// Дешёвые счётчики для HUD (точка Пб): раз в окно (<see cref="WindowSeconds"/>) — сборки GC, пауза, методы JIT процесса,
/// время системы и процесса (<c>GetSystemTimes</c>/<c>GetProcessTimes</c>); в остальных кадрах — сравнение времени. Работает
/// и без профайлера: один опрос в секунду (~3 мкс) — ниже шума кадра, а человеку «JIT, GC, фоновая нагрузка» нужны без
/// включения чего-либо. Без выделений; однопоточный (поток кадра).
/// </summary>
public sealed class RuntimeMeter
{
    public const double WindowSeconds = 1.0;

    private long _start;
    private bool _started;
    private int _gc;
    private long _pause, _jit, _idle, _kernel, _user, _process;

    /// <summary>Строка прошлого окна.</summary>
    public RuntimeLine Line { get; private set; }

    /// <summary>Опросов счётчиков (проверка цены: в кадре без конца окна — ни одного).</summary>
    public long Samples { get; private set; }

    /// <summary>Кадр: <paramref name="now"/> — отметка <see cref="Stopwatch"/>. Окно кончилось — опрос и новая строка.</summary>
    public void Frame(long now)
    {
        if (_started && now - _start < (long)(WindowSeconds * Stopwatch.Frequency)) return;
        Sample(now);
    }

    private void Sample(long now)
    {
        int gc = GC.CollectionCount(0);
        long pause = GC.GetTotalPauseDuration().Ticks, jit = JitInfo.GetCompiledMethodCount(currentThread: false);
        long idle = 0, kernel = 0, user = 0, process = 0;
        if (OperatingSystem.IsWindows())
        {
            MachineNative.GetSystemTimes(out idle, out kernel, out user);
            MachineNative.GetProcessTimes(MachineNative.GetCurrentProcess(), out _, out _, out long pk, out long pu);
            process = pk + pu;
        }
        if (_started)
        {
            long total = kernel - _kernel + (user - _user);
            long busy = total - (idle - _idle);
            double others = total > 0 ? Math.Max(0, (busy - (process - _process)) / (double)total) : 0;
            Line = new RuntimeLine((int)(jit - _jit), gc - _gc, (pause - _pause) / (double)TimeSpan.TicksPerMillisecond, others, true);
        }
        _start = now;
        _started = true;
        _gc = gc;
        _pause = pause;
        _jit = jit;
        _idle = idle;
        _kernel = kernel;
        _user = user;
        _process = process;
        Samples++;
    }
}
