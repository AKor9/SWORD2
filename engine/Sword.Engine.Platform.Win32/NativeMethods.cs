using System.Runtime.InteropServices;

namespace Sword.Engine.Platform.Win32;

internal delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEX
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public int ptX;
    public int ptY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

/// <summary>Сведения о мониторе: <c>rcMonitor</c> — экран целиком, <c>rcWork</c> — без панели задач.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}

internal static class Win32
{
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const long SC_KEYMENU = 0xF100;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_MBUTTONDOWN = 0x0207;

    /// <summary>Потеря фокуса клавиатуры и деактивация приложения.</summary>
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_ACTIVATEAPP = 0x001C;

    /// <summary>Захват мыши отобрала система.</summary>
    public const uint WM_CAPTURECHANGED = 0x0215;

    public const uint WM_MBUTTONUP = 0x0208;

    /// <summary>Курсор покинул клиентскую область; приходит один раз на каждый заказ <c>TrackMouseEvent</c>.</summary>
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint TME_LEAVE = 0x00000002;

    /// <summary>wParam у <c>WM_SIZE</c>: окно свернули.</summary>
    public const int SIZE_MINIMIZED = 1;

    /// <summary>Флаги <c>SetThreadExecutionState</c>: дисплей не гаснет от бездействия, пока не отменят.</summary>
    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    /// <summary>Ожидание объекта ядра (waitable object свопчейна); <c>WAIT_FAILED</c> — причина в <c>GetLastWin32Error</c>.</summary>
    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObjectEx(nint hHandle, uint dwMilliseconds,
        [MarshalAs(UnmanagedType.Bool)] bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    public const uint WS_VISIBLE = 0x10000000;
    public const uint PM_REMOVE = 0x0001;
    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_OWNDC = 0x0020;

    public const int IDC_ARROW = 32512;

    /// <summary>
    /// Низ порядка перекрытия: показ без активации фокус не отбирает, но кладёт окно наверх, поэтому фоновое окно
    /// показывается через <c>SetWindowPos</c> с этим значением.
    /// </summary>
    public static readonly nint HWND_BOTTOM = 1;

    /// <summary>
    /// Окно без рамы. Окно с рамой оболочка Windows не признаёт полноэкранным, и панель задач остаётся поверх него.
    /// </summary>
    public const uint WS_POPUP = 0x80000000;

    /// <summary>Ближайший монитор, а нет такого — основной.</summary>
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    /// <summary>
    /// Прямоугольник основного монитора целиком (<c>rcMonitor</c>, а не <c>rcWork</c>: иначе панель задач видна),
    /// в физических пикселях — осведомлённость о DPI объявлена до первого вызова.
    /// </summary>
    /// <returns>false и пустой прямоугольник, если монитора нет: вызывающий остаётся при размере из ключей.</returns>
    public static bool TryPrimaryMonitorRect(out RECT rect)
    {
        rect = default;

        nint monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        rect = info.rcMonitor;
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>Показать свёрнутым, не активируя (окно проверочного прогона, показанное свёрнутым — <c>--minimized</c>).</summary>
    public const int SW_SHOWMINNOACTIVE = 7;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    /// <summary>
    /// Захват мыши: без него отпускание кнопки за клиентской областью уходит чужому окну, и кнопка залипает.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowText(nint hWnd, string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int SetProcessDpiAwarenessContext(nint value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();
}
