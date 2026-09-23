using System.Runtime.InteropServices;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Частота монитора окна (<see cref="Win32Window.RefreshHz"/>, доводка после Пв: порог «заметной» аномалии кадра) на настоящем
/// окне: равна текущему режиму экрана его монитора, прочитанному здесь независимо — полной последовательной разметкой
/// <c>DEVMODEW</c> (у окна — явные смещения двух полей); перенос окна без перетаскивания (<c>SetWindowPos</c> — как
/// Win+Shift+стрелка) на каждый монитор даёт частоту этого монитора (<c>WM_MOVE</c> со сменой монитора). Нужен рабочий стол —
/// категория та же, что у тестов с окном и GPU.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class WindowRefreshGpuTests
{
    [Fact]
    public void RefreshHz_IsCurrentModeOfWindowMonitor_FollowsMoveToOtherMonitor()
    {
        using var window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests.Refresh", 64, 64, Activate: false));
        Assert.Equal(Expected(MonitorFromWindow(window.NativeHandle, 2)), window.RefreshHz);
        Assert.InRange(window.RefreshHz, 0, 1000);

        var monitors = new List<(nint Handle, RECT Work)>();
        EnumDisplayMonitors(0, 0, (m, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(m, ref info)) monitors.Add((m, info.Work));
            return true;
        }, 0);
        Assert.NotEmpty(monitors);
        // На каждый монитор и обратно на первый: при одном мониторе — перенос в пределах него, частота та же.
        foreach (var (monitor, work) in monitors.Append(monitors[0]))
        {
            Assert.True(SetWindowPos(window.NativeHandle, 0, work.Left + 8, work.Top + 8, 0, 0, 0x0001 | 0x0004 | 0x0010));   // NOSIZE | NOZORDER | NOACTIVATE
            window.PumpMessages();
            Assert.Equal(monitor, MonitorFromWindow(window.NativeHandle, 2));
            Assert.Equal(Expected(monitor), window.RefreshHz);
        }
    }

    /// <summary>Частота текущего режима экрана монитора; 0 и 1 («по умолчанию железа») — 0, как у окна.</summary>
    private static int Expected(nint monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
        Assert.True(GetMonitorInfoExW(monitor, ref info));
        var mode = new DevModeW { Size = (ushort)Marshal.SizeOf<DevModeW>() };
        Assert.Equal(220, mode.Size);
        Assert.True(EnumDisplaySettingsW(info.Device, -1, ref mode));
        return mode.DisplayFrequency > 1 ? (int)mode.DisplayFrequency : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RECT Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RECT Monitor, Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    /// <summary><c>DEVMODEW</c> целиком, по порядку полей Windows SDK (объединения — вариантом для экрана).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TtOption, Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoExW(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevModeW devMode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
