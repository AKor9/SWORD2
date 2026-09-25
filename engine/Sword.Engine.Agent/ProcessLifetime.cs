using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sword.Engine.Agent;

/// <summary>
/// Время жизни процесса сервера агента: сервер stdio живёт, пока жив его родитель (обёртка клиента MCP:
/// клиент → <c>cmd /c agent-headless.cmd</c> → <c>Sword.Headless --agent</c>) — клиент мог убить только обёртку, а вход
/// (stdin) держать открытым; дочерние процессы инструментов (Sword.exe видов и бюджетов фич) — в задании Windows (Job
/// Object) с <c>KILL_ON_JOB_CLOSE</c>: умирают вместе с процессом, как бы тот ни закончился (выход, исключение, снятие).
/// </summary>
public static class ProcessLifetime
{
    /// <summary>Номер родителя процесса <paramref name="process"/> (NtQueryInformationProcess); null — не узнать.</summary>
    public static int? ParentId(Process process)
    {
        try
        {
            int status = NtQueryInformationProcess(process.SafeHandle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Задача, которая завершается, когда родитель текущего процесса закончился (сразу — если его уже нет: номер мог
    /// достаться процессу, запущенному позже нас). Родителя не узнать или к нему нет доступа — задача не завершается
    /// никогда (сервер не выходит от незнания); <paramref name="watching"/> — за каким процессом следим или почему нет.
    /// </summary>
    public static Task WhenParentExited(out string watching, CancellationToken cancel = default)
    {
        using var self = Process.GetCurrentProcess();
        if (ParentId(self) is not { } parent)
        {
            watching = "родитель не известен — не следим";
            return Task.Delay(Timeout.Infinite, cancel);
        }
        return WhenExited(parent, self.StartTime, out watching, cancel);
    }

    /// <summary>
    /// Задача, которая завершается, когда процесс <paramref name="pid"/>, запущенный не позже
    /// <paramref name="startedBy"/>, закончился; процесса нет или номер занят более поздним процессом — завершена сразу.
    /// Нет доступа к процессу — не завершается (см. <paramref name="watching"/>).
    /// </summary>
    public static Task WhenExited(int pid, DateTime startedBy, out string watching, CancellationToken cancel = default)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            watching = $"процесса {pid} уже нет";
            return Task.CompletedTask;
        }
        try
        {
            if (process.StartTime > startedBy)
            {
                watching = $"номер {pid} занят процессом {process.ProcessName}, запущенным позже — прежнего уже нет";
                process.Dispose();
                return Task.CompletedTask;
            }
            watching = $"{process.ProcessName} ({pid})";
            return Wait(process, cancel);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Процесс закончился между вопросами — или к нему нет доступа (другой пользователь, повышенные права).
            bool gone;
            try
            {
                gone = process.HasExited;
            }
            catch (Exception again) when (again is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                gone = false;
            }
            process.Dispose();
            watching = gone ? $"процесса {pid} уже нет" : $"к процессу {pid} нет доступа ({e.Message}) — не следим";
            return gone ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancel);
        }

        static async Task Wait(Process p, CancellationToken c)
        {
            using (p) await p.WaitForExitAsync(c).ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, out ProcessBasicInformation info, int size, out int returned);
}

/// <summary>
/// Задание Windows (Job Object) с <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: процессы, добавленные в него, завершаются,
/// когда закрыта его последняя ручка — <see cref="Dispose"/> или конец процесса-владельца любым путём (ОС закрывает
/// ручки умершего процесса). Дети добавленного процесса попадают в задание сами (если не отпущены из него явно).
/// </summary>
public sealed class KillOnCloseJob : IDisposable
{
    private static readonly Lazy<KillOnCloseJob?> SharedJob = new(Create);
    private readonly SafeFileHandle _handle;

    private KillOnCloseJob(SafeFileHandle handle) => _handle = handle;

    /// <summary>
    /// Задание процесса — живёт до конца процесса (ручка закрывается ОС вместе с ним); null — Windows не дала создать.
    /// </summary>
    public static KillOnCloseJob? Shared => SharedJob.Value;

    /// <summary>Новое задание; null — не создать (ошибка — в журнал вызывающего по <see cref="LastError"/>).</summary>
    public static KillOnCloseJob? Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            LastError = Marshal.GetLastPInvokeErrorMessage();
            handle.Dispose();
            return null;
        }
        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            LastError = Marshal.GetLastPInvokeErrorMessage();
            handle.Dispose();
            return null;
        }
        return new KillOnCloseJob(handle);
    }

    /// <summary>Последняя ошибка Windows при создании или добавлении.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Добавить процесс (и его будущих детей); false — не удалось (процесс уже закончился, запрет вложения).</summary>
    public bool Add(Process process)
    {
        try
        {
            if (AssignProcessToJobObject(_handle, process.SafeHandle)) return true;
            LastError = Marshal.GetLastPInvokeErrorMessage();
            return false;
        }
        catch (InvalidOperationException e)
        {
            LastError = e.Message;
            return false;
        }
    }

    /// <summary>Закрыть задание: его процессы завершаются.</summary>
    public void Dispose() => _handle.Dispose();

    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, int size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
