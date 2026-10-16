using System.Runtime.InteropServices;

namespace Sword.Engine.Platform.Win32;

/// <summary>Ограничения размера окна (<c>WM_GETMINMAXINFO</c>), размеры окна вместе с рамой.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

/// <summary>Сведения о мониторе с именем устройства (<c>MONITORINFOEXW</c>): имя нужно <c>EnumDisplaySettingsW</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

/// <summary>
/// Режим экрана (<c>DEVMODEW</c>, 220 байт): нужны только размер структуры и частота обновления — остальные поля не читаются,
/// поэтому разметка явная по смещениям Windows SDK.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 220)]
internal struct DEVMODE
{
    [FieldOffset(68)] public ushort dmSize;
    [FieldOffset(184)] public uint dmDisplayFrequency;
}

/// <summary>Вызовы Win32 для окон инструментов (рама, ожидание событий, курсор, буфер обмена, DPI); игре не нужны.</summary>
internal static class Win32Ui
{
    /// <summary>Сменился режим экрана (разрешение, частота) — частоту монитора окна перечитать.</summary>
    public const uint WM_DISPLAYCHANGE = 0x007E;

    /// <summary>Окно перемещено (в том числе на другой монитор без перетаскивания: Win+Shift+стрелка).</summary>
    public const uint WM_MOVE = 0x0003;

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>Текущий режим экрана у <c>EnumDisplaySettingsW</c>.</summary>
    public const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoEx(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    /// <summary>
    /// Частота обновления монитора, Гц; 0 — не узнать (нет монитора, драйвер вернул 0 или 1). Выделяет: только на событиях окна.
    /// </summary>
    public static int RefreshRate(nint monitor)
    {
        if (monitor == nint.Zero) return 0;
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        if (!GetMonitorInfoEx(monitor, ref info) || string.IsNullOrEmpty(info.szDevice)) return 0;
        var mode = new DEVMODE { dmSize = 220 };
        if (!EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref mode)) return 0;
        return mode.dmDisplayFrequency > 1 ? (int)mode.dmDisplayFrequency : 0;
    }

    public const uint WM_NULL = 0x0000;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_DPICHANGED = 0x02E0;

    /// <summary>Начало и конец системного цикла перетаскивания рамки или заголовка окна.</summary>
    public const uint WM_ENTERSIZEMOVE = 0x0231;
    public const uint WM_EXITSIZEMOVE = 0x0232;

    /// <summary>Курсор над клиентской областью (младшее слово lParam у <c>WM_SETCURSOR</c>).</summary>
    public const int HTCLIENT = 1;

    /// <summary>Обычное окно: заголовок, системное меню, изменяемые края, кнопки свернуть и развернуть.</summary>
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;

    public const uint SWP_NOZORDER = 0x0004;

    public const uint QS_ALLINPUT = 0x04FF;
    /// <summary>Вернуться и тогда, когда ввод уже лежит в очереди, но его ещё никто не забрал.</summary>
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint INFINITE = 0xFFFFFFFF;

    public const int IDC_ARROW = 32512, IDC_IBEAM = 32513, IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646, IDC_HAND = 32649;

    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint nCount, nint pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    public static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    public static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalFree(nint hMem);
}
