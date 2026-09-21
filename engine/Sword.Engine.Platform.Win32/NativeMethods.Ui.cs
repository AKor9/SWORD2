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

/// <summary>
/// Вызовы Win32 для окон инструментов: рама, ожидание событий вместо кадров на всю мощность,
/// форма курсора, буфер обмена, DPI. Отдельно от <see cref="Win32"/>: игре они не нужны.
/// </summary>
internal static class Win32Ui
{
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
