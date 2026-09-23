using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Вызовы Windows для счётчиков машины (точка Пб): время потока в тактах, время системы и процесса, частота CPU по данным ОС.
/// Прецедент kernel32 в нейтральной сборке — <c>PakWriter</c>. Сигнатуры блиттабельные (<c>out long</c>, BOOL — int): вызов
/// не выделяет памяти. Вне Windows — нули (счётчики машины пусты, вердикт — без них).
/// </summary>
internal static class MachineNative
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int QueryThreadCycleTime(nint thread, out ulong cycles);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern unsafe int CallNtPowerInformation(int level, void* input, uint inputLength, void* output, uint outputLength);

    /// <summary><c>PROCESSOR_POWER_INFORMATION</c> (уровень <c>ProcessorInformation</c> = 11).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorPowerInformation
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    public const int ProcessorInformation = 11;
}

/// <summary>
/// Время потока на CPU (точка Пб): <c>QueryThreadCycleTime</c> — такты счётчика TSC, пока поток на процессоре (ядро и
/// пользователь). Выбран вместо <c>GetThreadTimes</c>: тот копит время шагом тика таймера (~15,6 мс) и для кадра 0,4 мс и тика
/// 0,1 мс даёт 0 или 15,6 мс; такты — точны до сотен наносекунд. Цена — ~0,25 мкс на вызов (системный вызов). Такты TSC идут
/// с постоянной частотой (invariant TSC — номинальная частота, не текущая частота ядра): перевод в миллисекунды — по частоте
/// TSC, измеренной один раз на процесс (<see cref="CyclesPerSecond"/>).
/// </summary>
public static class ThreadClock
{
    private static double _hz;

    /// <summary>Такты TSC текущего потока с его начала (0 вне Windows).</summary>
    public static ulong Cycles()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        MachineNative.QueryThreadCycleTime(MachineNative.GetCurrentThread(), out ulong c);
        return c;
    }

    /// <summary>
    /// Частота TSC, тактов в секунду: при первом обращении — шестнадцать отрезков по 0,5 мс в холостом цикле, наибольшее
    /// отношение тактов потока к <see cref="Stopwatch"/> (поток могли снять с процессора — такой отрезок даёт меньше; больше
    /// настоящей частоты отношение быть не может). ~8 мс один раз на процесс — не в горячем пути (отчёты, конец замера).
    /// </summary>
    public static double CyclesPerSecond => _hz > 0 ? _hz : _hz = Calibrate();

    private static double Calibrate()
    {
        if (!OperatingSystem.IsWindows()) return 1;
        double best = 0;
        for (int i = 0; i < 16; i++)
        {
            ulong c0 = Cycles();
            long t0 = Stopwatch.GetTimestamp(), until = t0 + Stopwatch.Frequency / 2000, t1;
            while ((t1 = Stopwatch.GetTimestamp()) < until) { }
            ulong c1 = Cycles();
            best = Math.Max(best, (c1 - c0) / ((t1 - t0) / (double)Stopwatch.Frequency));
        }
        // Под полной нагрузкой поток могли снять со всех отрезков — частота занижена, и время потока завышалось бы. Сверка с
        // номинальной частотой из реестра (~MHz — её Windows меряет при загрузке; TSC идёт с ней, у 7800X3D 4192 против
        // измеренных ~4190): ниже на 3 % и больше — берётся номинальная.
        double nominal = NominalHz();
        if (nominal > 0 && best < 0.97 * nominal) best = nominal;
        return best > 0 ? best : 1;
    }

    /// <summary>Номинальная частота процессора 0 по реестру (<c>~MHz</c>), Гц; 0 — нет.</summary>
    private static double NominalHz()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 0;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz * 1e6 : 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    /// <summary>Такты TSC в миллисекунды.</summary>
    public static double Ms(long cycles) => cycles * 1000.0 / CyclesPerSecond;
}

/// <summary>
/// Счётчики среды на момент (точка Пб; без событий, без выделений): такты потока (<see cref="ThreadClock"/>), сборки GC по
/// поколениям (<see cref="GC.CollectionCount"/> — сколько раз собиралось поколение: сборка поколения 1 считается и в 0),
/// суммарная пауза GC (<see cref="GC.GetTotalPauseDuration"/>, тики <see cref="TimeSpan"/>), выделения процесса
/// (<see cref="GC.GetTotalAllocatedBytes"/> неточно — шаг ~8 КБ на поток), методы и время JIT главного (текущего) потока и
/// процесса (<see cref="JitInfo"/>). Разность двух моментов — <see cref="Since"/>: то же за период.
/// </summary>
public readonly record struct ProfileCounters(long ThreadCycles, int Gen0, int Gen1, int Gen2, long PauseTicks, long ProcessBytes,
    long JitMethods, long JitProcess, long JitTicks, long JitProcessTicks)
{
    /// <summary>Прочитать сейчас; <paramref name="cycles"/> — ещё такты потока (системный вызов, ~0,25 мкс; остальное — ~15 нс).</summary>
    public static ProfileCounters Read(bool cycles = true) => new(
        cycles ? (long)ThreadClock.Cycles() : 0,
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
        GC.GetTotalPauseDuration().Ticks, GC.GetTotalAllocatedBytes(false),
        JitInfo.GetCompiledMethodCount(currentThread: true), JitInfo.GetCompiledMethodCount(currentThread: false),
        JitInfo.GetCompilationTime(currentThread: true).Ticks, JitInfo.GetCompilationTime(currentThread: false).Ticks);

    /// <summary>За время с <paramref name="start"/> до этого момента.</summary>
    public ProfileCounters Since(in ProfileCounters start) => new(ThreadCycles - start.ThreadCycles, Gen0 - start.Gen0, Gen1 - start.Gen1,
        Gen2 - start.Gen2, PauseTicks - start.PauseTicks, ProcessBytes - start.ProcessBytes, JitMethods - start.JitMethods,
        JitProcess - start.JitProcess, JitTicks - start.JitTicks, JitProcessTicks - start.JitProcessTicks);

    /// <summary>Сумма двух периодов.</summary>
    public ProfileCounters Plus(in ProfileCounters o) => new(ThreadCycles + o.ThreadCycles, Gen0 + o.Gen0, Gen1 + o.Gen1, Gen2 + o.Gen2,
        PauseTicks + o.PauseTicks, ProcessBytes + o.ProcessBytes, JitMethods + o.JitMethods, JitProcess + o.JitProcess,
        JitTicks + o.JitTicks, JitProcessTicks + o.JitProcessTicks);

    /// <summary>Сборок за период (каждая собирает поколение 0).</summary>
    public int Collections => Math.Max(Gen0, Math.Max(Gen1, Gen2));

    /// <summary>Сборок только поколения 0, до 1 и до 2 (в <see cref="Gen0"/> входят и старшие).</summary>
    public (int Gen0, int Gen1, int Gen2) Exclusive => (Math.Max(0, Gen0 - Gen1), Math.Max(0, Gen1 - Gen2), Gen2);

    public double PauseMs => PauseTicks / (double)TimeSpan.TicksPerMillisecond;
    public double JitMs => JitTicks / (double)TimeSpan.TicksPerMillisecond;
    public double JitProcessMs => JitProcessTicks / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Время потока на CPU, мс (такты TSC — по <see cref="ThreadClock.CyclesPerSecond"/>).</summary>
    public double CpuMs => ThreadClock.Ms(ThreadCycles);
}

/// <summary>
/// Машина на момент — граница замера (точка Пб): время, такты текущего потока, время системы (<c>GetSystemTimes</c>: простой,
/// ядро с простоем, пользователь — сумма по всем логическим процессорам, 100 нс) и процесса (<c>GetProcessTimes</c>),
/// счётчики среды (<see cref="ProfileCounters"/>), байты текущего потока, частота CPU. Снимать на потоке, который меряется
/// (такты — текущего потока). Без выделений; ~3 мкс без пробы частоты, ~50 мкс с ней — только на границах замера, не каждый
/// кадр.
/// </summary>
public readonly record struct MachineSample(long Time, long SystemIdle, long SystemKernel, long SystemUser, long ProcessTime,
    ProfileCounters Runtime, long ThreadBytes, double ProbeMhz, int BaseMhz, int CurrentMhz, int LimitMhz)
{
    /// <summary>Снять сейчас; <paramref name="probe"/> — ещё проба частоты ядра (<see cref="Machine.ProbeMhz"/>, ~50 мкс).</summary>
    public static MachineSample Take(bool probe = true)
    {
        double mhz = probe ? Machine.ProbeMhz() : 0;
        var (baseMhz, current, limit) = Machine.Power();
        long idle = 0, kernel = 0, user = 0, process = 0;
        if (OperatingSystem.IsWindows())
        {
            MachineNative.GetSystemTimes(out idle, out kernel, out user);
            MachineNative.GetProcessTimes(MachineNative.GetCurrentProcess(), out _, out _, out long pk, out long pu);
            process = pk + pu;
        }
        var runtime = ProfileCounters.Read();
        return new MachineSample(Stopwatch.GetTimestamp(), idle, kernel, user, process, runtime, GC.GetAllocatedBytesForCurrentThread(),
            mhz, baseMhz, current, limit);
    }
}

/// <summary>Вердикт замера: время — за код, за среду .NET (JIT, GC во время замера) или за машину (загрузка, частота).</summary>
public enum MachineVerdict
{
    Code,
    Runtime,
    Machine,
}

/// <summary>
/// Машина за окно замера (точка Пб): настенное время и время потока на CPU, загрузка системы — всей, своего процесса и других
/// процессов (доля всех логических процессоров), сборки и паузы GC, методы и время JIT, выделения процесса и потока, частота
/// CPU (проба на границах и данные ОС) — и вердикт «код или машина» с причинами (<see cref="Reasons"/>, <see cref="Text"/>).
///
/// Критерии (константы ниже): машина занята — другие процессы заняли не меньше <see cref="OthersBusyLimit"/> всех логических
/// процессоров, или счётный поток (тики, замер бюджета) был на CPU меньше <see cref="OnCpuLimit"/> своего настенного времени
/// (его снимали с процессора), или частота ядра ниже <see cref="FrequencyLimit"/> базовой (энергосбережение, перегрев), или
/// ОС ограничила частоту; среда — JIT или GC во время замера (любой: метод, скомпилированный посреди замера, — пауза или
/// смена кода на ходу). Иначе — код: время замера принадлежит коду. Причины машины важнее причин среды.
///
/// Точность: загрузка системы и процесса — по счётчикам ОС с шагом тика таймера (~15,6 мс на процессор): окно короче ~0,5 с —
/// грубо (см. <see cref="Note"/>); время потока — такты TSC, точно; частота — проба цепочкой умножений (оценка: задержка
/// умножения 3 такта — так у Zen и Intel Core; на ядрах с другой задержкой — со сдвигом).
/// </summary>
[JsonConverter(typeof(MachineReportConverter))]
public sealed class MachineReport
{
    /// <summary>Другие процессы заняли столько всех логических процессоров — машина занята.</summary>
    public const double OthersBusyLimit = 0.25;

    /// <summary>Счётный поток был на CPU меньше этой доли настенного времени — его снимали с процессора.</summary>
    public const double OnCpuLimit = 0.90;

    /// <summary>Частота ядра (проба) ниже этой доли базовой — частота снижена.</summary>
    public const double FrequencyLimit = 0.90;

    /// <summary>Окно короче — загрузка системы грубая (шаг счётчиков ОС ~15,6 мс).</summary>
    public const double PreciseWindowMs = 500;

    /// <summary>
    /// Окно короче — загрузка системы в вердикт не идёт: один тик таймера одного процессора в окне 10 мс на 8 процессорах —
    /// уже ~20 % (замер бюджета `movement` — ~5 мс: «52 %» при спокойной машине).
    /// </summary>
    public const double MinLoadWindowMs = 100;

    public long WallTicks { get; init; }
    public long ThreadCycles { get; init; }
    public long SystemBusy { get; init; }
    public long SystemTotal { get; init; }
    public long ProcessTime { get; init; }
    public int Processors { get; init; } = Environment.ProcessorCount;
    public ProfileCounters Runtime { get; init; }
    public long ThreadBytes { get; init; }
    public double ProbeMhzStart { get; init; }
    public double ProbeMhzEnd { get; init; }

    /// <summary>Частота окна для вердикта: большая из проб начала и конца (у суммы окон — наименьшая из таких).</summary>
    public double ProbeMhz { get; init; }
    public int BaseMhz { get; init; }
    public int CurrentMhz { get; init; }
    public int LimitMhz { get; init; }

    /// <summary>Счётный поток: доля времени на CPU проверяется (тики Headless, бюджеты, перебор); кадр App ждёт GPU — нет.</summary>
    public bool ComputeBound { get; init; }

    /// <summary>Доля CPU счётной работы, если её мерили отдельно (тики профайлера); иначе — поток за всё окно.</summary>
    public double? ComputeOnCpu { get; init; }

    /// <summary>Окон в сумме (<see cref="Combine"/>).</summary>
    public int Windows { get; init; } = 1;

    public double WallMs => WallTicks * 1000.0 / Stopwatch.Frequency;
    public double ThreadCpuMs => ThreadClock.Ms(ThreadCycles);
    public double ThreadOnCpu => WallTicks > 0 ? Math.Min(1, ThreadCpuMs / WallMs) : 0;
    public double SystemBusyShare => SystemTotal > 0 ? SystemBusy / (double)SystemTotal : 0;
    public double ProcessBusyShare => SystemTotal > 0 ? Math.Min(1, ProcessTime / (double)SystemTotal) : 0;
    public double OthersBusyShare => Math.Max(0, SystemBusyShare - ProcessBusyShare);

    /// <summary>Замер машины: окно между <paramref name="a"/> и <paramref name="b"/> (оба — на потоке замера).</summary>
    /// <param name="computeBound">Поток всё окно считал (не ждал GPU и агента): доля CPU — критерий вердикта.</param>
    public static MachineReport Between(in MachineSample a, in MachineSample b, bool computeBound, double? computeOnCpu = null)
    {
        long busy = (b.SystemKernel - a.SystemKernel) - (b.SystemIdle - a.SystemIdle) + (b.SystemUser - a.SystemUser);
        return new MachineReport
        {
            WallTicks = b.Time - a.Time,
            ThreadCycles = b.Runtime.ThreadCycles - a.Runtime.ThreadCycles,
            SystemBusy = Math.Max(0, busy),
            SystemTotal = (b.SystemKernel - a.SystemKernel) + (b.SystemUser - a.SystemUser),
            ProcessTime = b.ProcessTime - a.ProcessTime,
            Runtime = b.Runtime.Since(a.Runtime),
            ThreadBytes = b.ThreadBytes - a.ThreadBytes,
            ProbeMhzStart = a.ProbeMhz,
            ProbeMhzEnd = b.ProbeMhz,
            // Для вердикта — большая из двух проб: разовый провал частоты на одной границе (ядро только что проснулось) — не
            // снижение; снижена — если низко на обеих.
            ProbeMhz = Math.Max(a.ProbeMhz, b.ProbeMhz),
            BaseMhz = b.BaseMhz,
            CurrentMhz = b.CurrentMhz,
            LimitMhz = b.LimitMhz,
            ComputeBound = computeBound,
            ComputeOnCpu = computeOnCpu,
        };
    }

    /// <summary>Сумма окон (повторы бюджета, перебора): времена и счётчики складываются, частота — наименьшая.</summary>
    public static MachineReport? Combine(IEnumerable<MachineReport?> reports)
    {
        MachineReport? sum = null;
        foreach (var r in reports)
        {
            if (r is null) continue;
            if (sum is null)
            {
                sum = r;
                continue;
            }
            sum = new MachineReport
            {
                WallTicks = sum.WallTicks + r.WallTicks,
                ThreadCycles = sum.ThreadCycles + r.ThreadCycles,
                SystemBusy = sum.SystemBusy + r.SystemBusy,
                SystemTotal = sum.SystemTotal + r.SystemTotal,
                ProcessTime = sum.ProcessTime + r.ProcessTime,
                Processors = r.Processors,
                Runtime = sum.Runtime.Plus(r.Runtime),
                ThreadBytes = sum.ThreadBytes + r.ThreadBytes,
                ProbeMhzStart = Min(sum.ProbeMhzStart, r.ProbeMhzStart),
                ProbeMhzEnd = Min(sum.ProbeMhzEnd, r.ProbeMhzEnd),
                ProbeMhz = Min(sum.ProbeMhz, r.ProbeMhz),
                BaseMhz = Math.Max(sum.BaseMhz, r.BaseMhz),
                CurrentMhz = r.CurrentMhz,
                LimitMhz = sum.LimitMhz > 0 && r.LimitMhz > 0 ? Math.Min(sum.LimitMhz, r.LimitMhz) : Math.Max(sum.LimitMhz, r.LimitMhz),
                ComputeBound = sum.ComputeBound && r.ComputeBound,
                Windows = sum.Windows + r.Windows,
            };
        }
        return sum;
    }

    /// <summary>Меньшая из двух частот; нет одной — другая.</summary>
    private static double Min(double x, double y) => x > 0 && y > 0 ? Math.Min(x, y) : Math.Max(x, y);

    /// <summary>Доля CPU, по которой судят о снятии с процессора: отдельная (тики) или потока за окно; null — не судят.</summary>
    public double? OnCpuForVerdict => ComputeOnCpu ?? (ComputeBound ? ThreadOnCpu : null);

    /// <summary>Причины машины (занята, частота) — по критериям класса.</summary>
    public IReadOnlyList<string> MachineReasons
    {
        get
        {
            var list = new List<string>();
            bool others = WallMs >= MinLoadWindowMs && OthersBusyShare >= OthersBusyLimit, preempted = OnCpuForVerdict is { } oc && oc < OnCpuLimit;
            if (others || preempted)
            {
                var parts = new List<string>();
                parts.Add($"загрузка системы другими процессами {Pct(OthersBusyShare)} %");
                if (OnCpuForVerdict is { } on) parts.Add($"{(ComputeOnCpu is null ? "поток" : "тики")} на CPU {Pct(on)} % времени");
                list.Add("машина занята: " + string.Join(", ", parts));
            }
            if (ProbeMhz > 0 && BaseMhz > 0 && ProbeMhz < FrequencyLimit * BaseMhz)
                list.Add($"частота CPU {ProbeMhz:0} МГц — ниже базовой {BaseMhz} МГц (энергосбережение или перегрев)");
            if (LimitMhz > 0 && BaseMhz > 0 && LimitMhz < BaseMhz)
                list.Add($"ОС ограничила частоту CPU: {LimitMhz} из {BaseMhz} МГц");
            return list;
        }
    }

    /// <summary>Причины среды: JIT и GC во время замера.</summary>
    public IReadOnlyList<string> RuntimeReasons
    {
        get
        {
            var list = new List<string>();
            var r = Runtime;
            if (r.JitProcess > 0)
                list.Add($"JIT во время замера: {r.JitProcess} {Methods(r.JitProcess)} (на главном потоке — {r.JitMethods}), {F(r.JitProcessMs, 2)} мс");
            if (r.Collections > 0)
            {
                var (g0, g1, g2) = r.Exclusive;
                list.Add($"GC во время замера: сборок {r.Collections} (gen0 {g0}, gen1 {g1}, gen2 {g2}), пауза {F(r.PauseMs, 2)} мс");
            }
            return list;
        }
    }

    public MachineVerdict Verdict => MachineReasons.Count > 0 ? MachineVerdict.Machine : RuntimeReasons.Count > 0 ? MachineVerdict.Runtime : MachineVerdict.Code;

    /// <summary>Все причины: машины, затем среды.</summary>
    public IReadOnlyList<string> Reasons => [.. MachineReasons, .. RuntimeReasons];

    /// <summary>Вердикт словами: «машина: …», «среда: …» или «код: машина спокойна (…)».</summary>
    public string Text
    {
        get
        {
            string quiet = $"другие процессы {Pct(OthersBusyShare)} %" + (OnCpuForVerdict is { } on ? $", {(ComputeOnCpu is null ? "поток" : "тики")} на CPU {Pct(on)} %" : "") +
                (ProbeMhz > 0 ? $", {ProbeMhz:0} МГц" : "");
            return Verdict switch
            {
                MachineVerdict.Machine => "машина: " + string.Join("; ", Reasons),
                MachineVerdict.Runtime => "среда: " + string.Join("; ", Reasons) + $" (машина спокойна: {quiet})",
                _ => $"код: машина спокойна ({quiet}), JIT и GC нет",
            };
        }
    }

    /// <summary>Окно коротко — загрузка системы по счётчикам ОС грубая; null — окно достаточно.</summary>
    public string? Note => WallMs < MinLoadWindowMs
        ? $"окно {F(WallMs, 0)} мс: загрузка системы — по счётчикам ОС с шагом ~15,6 мс, в вердикт не идёт"
        : WallMs < PreciseWindowMs ? $"окно {F(WallMs, 0)} мс: загрузка системы — по счётчикам ОС с шагом ~15,6 мс, грубо" : null;

    public static string VerdictWord(MachineVerdict v) => v switch
    {
        MachineVerdict.Machine => "машина",
        MachineVerdict.Runtime => "среда",
        _ => "код",
    };

    /// <summary>Отчёт JSON (формат один для <c>--json</c>, <c>profile.json</c>, бюджетов, перебора и агента).</summary>
    public JsonObject ToJson()
    {
        var r = Runtime;
        var (g0, g1, g2) = r.Exclusive;
        var json = new JsonObject
        {
            ["verdict"] = VerdictWord(Verdict),
            ["kind"] = Verdict.ToString().ToLowerInvariant(),
            ["text"] = Text,
            ["reasons"] = new JsonArray([.. Reasons.Select(x => (JsonNode?)x)]),
            ["wallMs"] = Math.Round(WallMs, 3),
            ["threadCpuMs"] = Math.Round(ThreadCpuMs, 3),
            ["threadOnCpuPct"] = Math.Round(ThreadOnCpu * 100, 1),
        };
        if (ComputeOnCpu is { } c) json["ticksOnCpuPct"] = Math.Round(c * 100, 1);
        json["computeBound"] = ComputeBound;
        json["processors"] = Processors;
        json["systemBusyPct"] = Math.Round(SystemBusyShare * 100, 1);
        json["processBusyPct"] = Math.Round(ProcessBusyShare * 100, 1);
        json["othersBusyPct"] = Math.Round(OthersBusyShare * 100, 1);
        json["mhz"] = new JsonObject
        {
            ["probe"] = Math.Round(ProbeMhz), ["probeStart"] = Math.Round(ProbeMhzStart), ["probeEnd"] = Math.Round(ProbeMhzEnd),
            ["base"] = BaseMhz, ["os"] = CurrentMhz, ["limit"] = LimitMhz,
        };
        json["gc"] = new JsonObject { ["collections"] = r.Collections, ["gen0"] = g0, ["gen1"] = g1, ["gen2"] = g2, ["pauseMs"] = Math.Round(r.PauseMs, 3) };
        json["jit"] = new JsonObject
        {
            ["methods"] = r.JitProcess, ["mainThread"] = r.JitMethods, ["ms"] = Math.Round(r.JitProcessMs, 3), ["mainThreadMs"] = Math.Round(r.JitMs, 3),
        };
        json["bytes"] = new JsonObject { ["process"] = r.ProcessBytes, ["thread"] = ThreadBytes };
        if (Windows > 1) json["windows"] = Windows;
        if (Note is { } note) json["note"] = note;
        return json;
    }

    /// <summary>Коротко — для строк перебора: доли, частота, GC, JIT и вердикт.</summary>
    public JsonObject ToShortJson()
    {
        var json = new JsonObject
        {
            ["verdict"] = VerdictWord(Verdict),
            ["reasons"] = new JsonArray([.. Reasons.Select(x => (JsonNode?)x)]),
            ["threadOnCpuPct"] = Math.Round(ThreadOnCpu * 100, 1),
            ["othersBusyPct"] = Math.Round(OthersBusyShare * 100, 1),
            ["mhz"] = Math.Round(ProbeMhz),
            ["gc"] = Runtime.Collections,
            ["jit"] = Runtime.JitProcess,
        };
        return json;
    }

    public override string ToString() => Text;

    private static string Methods(long n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "метод",
        (2 or 3 or 4, not (12 or 13 or 14)) => "метода",
        _ => "методов",
    };

    internal static string Pct(double share) => (share * 100).ToString("0.#", CultureInfo.InvariantCulture);

    internal static string F(double v, int digits) => Math.Round(v, digits).ToString(CultureInfo.InvariantCulture);
}

/// <summary>JSON <see cref="MachineReport"/> — всегда <see cref="MachineReport.ToJson"/> (и в итоге App, где отчёт сериализуется целиком).</summary>
public sealed class MachineReportConverter : JsonConverter<MachineReport>
{
    public override MachineReport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("MachineReport только пишется");

    public override void Write(Utf8JsonWriter writer, MachineReport value, JsonSerializerOptions options) => value.ToJson().WriteTo(writer, options);
}

/// <summary>
/// Частота CPU (точка Пб). Два источника, оба — только на границах замера:
/// <list type="bullet">
/// <item><see cref="Power"/> — <c>CallNtPowerInformation(ProcessorInformation)</c>: базовая частота, «текущая» и предел по
/// данным ОС (~0,5 мкс, без выделений). Текущая у многих процессоров стоит на базовой и турбо не показывает (у Ryzen 7 7800X3D —
/// 4201 всегда), зато предел ниже базовой — прямой признак ограничения (перегрев, план питания).</item>
/// <item><see cref="ProbeMhz"/> — проба: цепочка зависимых умножений (<c>x = x·C + 1</c>: 3 такта умножение и 1 сложение на
/// шаг) на ядре текущего потока, лучшая из трёх по ~15 мкс. Показывает настоящую частоту ядра с турбо (у 7800X3D — ~4,8 ГГц)
/// и её падение при нагрузке или энергосбережении. Оценка: на ядрах с другой задержкой умножения — со сдвигом.</item>
/// </list>
/// </summary>
public static class Machine
{
    /// <summary>Шагов цепочки в одной пробе (~15 мкс на 5 ГГц).</summary>
    public const int ProbeSteps = 20_000;

    /// <summary>Тактов на шаг цепочки (<c>imul</c> — 3, <c>add</c> — 1).</summary>
    public const int ProbeCyclesPerStep = 4;

    private static ulong _sink;

    /// <summary>
    /// Сборка Diagnostics оптимизирована JIT (Release): проба частоты верна только тогда — в Debug цепочка умножений идёт
    /// через стек, тактов на шаг больше, и проба занижала бы частоту втрое.
    /// </summary>
    public static readonly bool ProbeValid = typeof(Machine).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
        .OfType<DebuggableAttribute>().FirstOrDefault()?.IsJITOptimizerDisabled != true;

    /// <summary>Частота ядра текущего потока по пробе, МГц (лучшая из трёх; ~50 мкс); 0 — проба недействительна (Debug).</summary>
    public static double ProbeMhz()
    {
        if (!ProbeValid) return 0;
        double best = 0;
        for (int i = 0; i < 3; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            _sink += Chain(ProbeSteps, _sink);
            long t = Stopwatch.GetTimestamp() - t0;
            if (t > 0) best = Math.Max(best, ProbeSteps * (double)ProbeCyclesPerStep * Stopwatch.Frequency / t / 1e6);
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong Chain(int n, ulong seed)
    {
        ulong x = seed | 1;
        for (int i = 0; i < n; i++) x = x * 0x9E3779B97F4A7C15UL + 1;
        return x;
    }

    /// <summary>
    /// Частота по данным ОС, МГц: базовая (наибольшая <c>MaxMhz</c>), «текущая» (средняя <c>CurrentMhz</c>) и предел
    /// (наименьший <c>MhzLimit</c>) по логическим процессорам; нули — нет данных.
    /// </summary>
    public static unsafe (int Base, int Current, int Limit) Power()
    {
        if (!OperatingSystem.IsWindows()) return default;
        const int max = 256;   // буфер — на стеке; процессоров больше — данных нет
        var buffer = stackalloc MachineNative.ProcessorPowerInformation[max];
        new Span<MachineNative.ProcessorPowerInformation>(buffer, max).Clear();
        int status = MachineNative.CallNtPowerInformation(MachineNative.ProcessorInformation, null, 0, buffer,
            (uint)(sizeof(MachineNative.ProcessorPowerInformation) * max));
        if (status != 0) return default;
        int top = 0, limit = int.MaxValue, n = 0;
        long current = 0;
        for (int i = 0; i < max; i++)
        {
            var p = buffer[i];
            if (p.MaxMhz == 0) continue;
            top = Math.Max(top, (int)p.MaxMhz);
            limit = Math.Min(limit, (int)p.MhzLimit);
            current += p.CurrentMhz;
            n++;
        }
        return n == 0 ? default : (top, (int)(current / n), limit);
    }

    /// <summary>Номер потока ОС текущего потока (события среды несут его в <c>OSThreadId</c>).</summary>
    public static uint CurrentThreadId() => OperatingSystem.IsWindows() ? MachineNative.GetCurrentThreadId() : 0;
}
