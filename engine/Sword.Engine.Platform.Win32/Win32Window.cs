using System.Runtime.InteropServices;

namespace Sword.Engine.Platform.Win32;

/// <summary>
/// Окно на голом Win32: от окна нужен только HWND для свопчейна и поток сообщений, лишняя нативная зависимость ни к чему.
/// </summary>
public sealed class Win32Window : IWindow
{
    /// <summary>
    /// Имя класса окна своё у каждого окна (номер к общему имени): класс хранит указатель на делегат конкретного экземпляра,
    /// и с общим именем второе окно слало бы сообщения в делегат первого.
    /// </summary>
    private static int _classSeq;

    private readonly string _className;

    /// <summary>Имя класса в неуправляемой памяти: Windows читает по указателю, пока класс зарегистрирован.</summary>
    private nint _classNamePtr;

    // Делегат обязан жить столько же, сколько окно: Win32 держит на него голый указатель, и сборщик мусора об этой
    // ссылке не знает.
    private readonly WndProc _wndProcDelegate;
    private readonly nint _hInstance;
    private bool _disposed;

    /// <summary>Окно с рамой и изменяемым размером, а не игровое окно без рамы.</summary>
    private readonly bool _resizable;

    private readonly uint _style;

    public nint Handle { get; private set; }
    public nint NativeHandle => Handle;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool ShouldClose { get; private set; }
    public bool Resized { get; private set; }

    /// <summary>Окно свёрнуто: рисовать в него нечего, без признака цикл крутился бы впустую на всю мощность видеокарты.</summary>
    public bool Minimized { get; private set; }

    /// <summary>Курсор внутри клиентской области (уход Win32 сообщает только по заказу <c>TrackMouseEvent</c>).</summary>
    public bool MouseInside { get; private set; }

    public int Dpi { get; private set; } = 96;

    /// <summary>
    /// Частота обновления монитора окна, Гц (0 — не узнать). Перечитывается при создании и при любой возможной смене монитора
    /// или режима: <c>WM_DISPLAYCHANGE</c>, <c>WM_DPICHANGED</c>, <c>WM_EXITSIZEMOVE</c>, <c>WM_MOVE</c> со сменой монитора.
    /// </summary>
    public int RefreshHz { get; private set; }

    /// <summary>Монитор, чья частота в <see cref="RefreshHz"/> (<c>MonitorFromWindow</c> не выделяет — сверка на каждом WM_MOVE).</summary>
    private nint _monitor;

    /// <summary>Масштаб монитора окна: 1 при 100 %, 1,5 при 150 %.</summary>
    public float DpiScale => Dpi / 96f;

    /// <summary>Наименьшая клиентская часть изменяемого окна, в физических пикселях.</summary>
    public int MinClientWidth { get; set; } = 320;
    public int MinClientHeight { get; set; } = 240;

    private bool _trackingLeave;

    public InputState Input { get; } = new();

    public event Action? LiveResize;

    /// <summary>Идёт системный цикл перетаскивания рамки или заголовка (между WM_ENTERSIZEMOVE и WM_EXITSIZEMOVE).</summary>
    private bool _inSizeMove;

    /// <remarks>
    /// Неактивное окно показывается <c>SetWindowPos</c> с <c>HWND_BOTTOM</c>: показ без активации фокус не уводит, но кладёт
    /// окно наверх. У игрового окна рамы нет ни в одном режиме (<c>WS_POPUP</c>): окно с рамой оболочка не считает
    /// полноэкранным; полный экран — только размер (<c>rcMonitor</c>).
    /// </remarks>
    public Win32Window(WindowOptions options)
    {
        // Без этого Windows отдаст растянутый буфер на мониторе с масштабом.
        Win32.SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2

        _hInstance = Win32.GetModuleHandle(null);
        _wndProcDelegate = WindowProc;
        _resizable = options.Resizable && !options.Fullscreen;

        _className = $"SwordWindowClass.{Interlocked.Increment(ref _classSeq)}";
        _classNamePtr = Marshal.StringToHGlobalUni(_className);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = Win32.CS_HREDRAW | Win32.CS_VREDRAW | Win32.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = _hInstance,
            hCursor = Win32.LoadCursor(nint.Zero, Win32.IDC_ARROW),
            lpszClassName = _classNamePtr,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
        {
            Marshal.FreeHGlobal(_classNamePtr);
            _classNamePtr = nint.Zero;
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        // Размер и место считаются здесь, а не умолчанием системы: с CW_USEDEFAULT окно с рамой выходило больше экрана.
        // У игрового окна рамы нет; у изменяемого рама добавляется снаружи.
        int w = Math.Max(1, options.Width), h = Math.Max(1, options.Height);
        int x = 0, y = 0;
        _style = _resizable ? Win32Ui.WS_OVERLAPPEDWINDOW : Win32.WS_POPUP;

        // Монитора может не найтись — тогда заданный размер и угол экрана; полный экран не выдумывается.
        bool haveScreen = Win32.TryPrimaryMonitorRect(out var screen);
        if (haveScreen)
        {
            x = screen.Left;
            y = screen.Top;
            if (options.Fullscreen)
            {
                w = screen.Right - screen.Left;
                h = screen.Bottom - screen.Top;
            }
            else
            {
                x += ((screen.Right - screen.Left) - w) / 2;
                y += ((screen.Bottom - screen.Top) - h) / 2;
            }
        }

        if (_resizable)
        {
            var frame = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            Win32Ui.AdjustWindowRectExForDpi(ref frame, _style, false, 0, Win32Ui.GetDpiForSystem());
            int fw = frame.Right - frame.Left, fh = frame.Bottom - frame.Top;
            x -= (fw - w) / 2;
            y -= (fh - h) / 2;
            if (haveScreen) y = Math.Max(y, screen.Top);   // заголовок не уезжает за верхний край
            w = fw;
            h = fh;
        }

        // WS_VISIBLE — только активному окну (показ с активацией). Неактивное создаётся скрытым и показывается одним SetWindowPos
        // сразу в низ порядка перекрытия — без мелькания наверху.
        bool activate = options.Activate && !options.Minimized;
        Handle = Win32.CreateWindowEx(
            0, _className, options.Title,
            _style | (activate ? Win32.WS_VISIBLE : 0),
            x, y, w, h,
            nint.Zero, nint.Zero, _hInstance, nint.Zero);

        if (Handle == nint.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        // Размер клиентской части — до показа: у свёрнутого окна она нулевая (окно, показанное свёрнутым).
        Dpi = (int)Math.Max(96, Win32Ui.GetDpiForWindow(Handle));
        ReadRefreshRate(Handle);
        Win32.GetClientRect(Handle, out var client);
        Width = Math.Max(1, client.Right - client.Left);
        Height = Math.Max(1, client.Bottom - client.Top);

        // Свёрнутым и без фокуса (SW_SHOWMINNOACTIVE): WM_SIZE со SIZE_MINIMIZED ставит Minimized, размер остаётся заданным.
        if (options.Minimized) Win32.ShowWindow(Handle, Win32.SW_SHOWMINNOACTIVE);
        else if (!activate)
            Win32.SetWindowPos(Handle, Win32.HWND_BOTTOM, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        // Дисплей держит только активное игровое окно: проверочный прогон идёт в фоне, окну инструмента это незачем.
        _holdsDisplay = options.Activate && !_resizable;
        if (_holdsDisplay) Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS | Win32.ES_DISPLAY_REQUIRED);
    }

    private readonly bool _holdsDisplay;

    public void SetTitle(string title) => Win32.SetWindowText(Handle, title);

    public void SetClientSize(int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        if (_resizable)
        {
            var frame = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            Win32Ui.AdjustWindowRectExForDpi(ref frame, _style, false, 0, (uint)Dpi);
            w = frame.Right - frame.Left;
            h = frame.Bottom - frame.Top;
        }
        // WM_SIZE приходит прямо внутри вызова: Width, Height и Resized обновляет оконная процедура.
        Win32.SetWindowPos(Handle, nint.Zero, 0, 0, w, h,
            Win32.SWP_NOMOVE | Win32Ui.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    public bool PumpMessages()
    {
        Resized = false;
        Input.BeginFrame();

        while (Win32.PeekMessage(out var msg, nint.Zero, 0, 0, Win32.PM_REMOVE))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        return !ShouldClose;
    }

    /// <summary>Сообщения не забираются — их выгребает следующий <see cref="PumpMessages"/>.</summary>
    public void WaitForEvents(int timeoutMs) =>
        Win32Ui.MsgWaitForMultipleObjectsEx(0, nint.Zero, timeoutMs < 0 ? Win32Ui.INFINITE : (uint)timeoutMs,
            Win32Ui.QS_ALLINPUT, Win32Ui.MWMO_INPUTAVAILABLE);

    public void Wake() => Win32Ui.PostMessage(Handle, Win32Ui.WM_NULL, nint.Zero, nint.Zero);

    private bool _cursorHidden;

    /// <summary>Действует только пока окно на переднем плане.</summary>
    public bool CursorLock { get; set; }

    public void ApplyCursorLock()
    {
        bool want = CursorLock && Win32.GetForegroundWindow() == Handle;
        if (want != _cursorHidden)
        {
            Win32.ShowCursor(!want);
            _cursorHidden = want;
        }
        if (!want) return;
        var pt = new POINT { X = Width / 2, Y = Height / 2 };
        Win32.ClientToScreen(Handle, ref pt);
        Win32.SetCursorPos(pt.X, pt.Y);
        Input.AnchorMouse(Width / 2, Height / 2);
    }

    private CursorShape _cursor;
    private static readonly nint[] CursorHandles = new nint[Enum.GetValues<CursorShape>().Length];

    /// <summary>Ставится сразу, если курсор над окном, и на каждый <c>WM_SETCURSOR</c> — иначе класс возвращал бы стрелку.</summary>
    public CursorShape Cursor
    {
        get => _cursor;
        set
        {
            if (_cursor == value) return;
            _cursor = value;
            if (MouseInside && !_cursorHidden) Win32Ui.SetCursor(CursorHandle(value));
        }
    }

    private static nint CursorHandle(CursorShape shape)
    {
        ref nint h = ref CursorHandles[(int)shape];
        if (h == nint.Zero)
            h = Win32.LoadCursor(nint.Zero, shape switch
            {
                CursorShape.IBeam => Win32Ui.IDC_IBEAM,
                CursorShape.Hand => Win32Ui.IDC_HAND,
                CursorShape.SizeWE => Win32Ui.IDC_SIZEWE,
                CursorShape.SizeNS => Win32Ui.IDC_SIZENS,
                CursorShape.SizeAll => Win32Ui.IDC_SIZEALL,
                _ => Win32Ui.IDC_ARROW,
            });
        return h;
    }

    public string? GetClipboardText()
    {
        if (!Win32Ui.IsClipboardFormatAvailable(Win32Ui.CF_UNICODETEXT) || !Win32Ui.OpenClipboard(Handle)) return null;
        try
        {
            nint data = Win32Ui.GetClipboardData(Win32Ui.CF_UNICODETEXT);
            if (data == nint.Zero) return null;
            nint p = Win32Ui.GlobalLock(data);
            if (p == nint.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Win32Ui.GlobalUnlock(data); }
        }
        finally
        {
            Win32Ui.CloseClipboard();
        }
    }

    public void SetClipboardText(string text)
    {
        if (!Win32Ui.OpenClipboard(Handle)) return;
        try
        {
            Win32Ui.EmptyClipboard();
            nint mem = Win32Ui.GlobalAlloc(Win32Ui.GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
            if (mem == nint.Zero) return;
            nint p = Win32Ui.GlobalLock(mem);
            if (p == nint.Zero)
            {
                Win32Ui.GlobalFree(mem);
                return;
            }
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            Win32Ui.GlobalUnlock(mem);
            // Принятую память буфер обмена забирает себе; не принятую освобождаем сами.
            if (Win32Ui.SetClipboardData(Win32Ui.CF_UNICODETEXT, mem) == nint.Zero) Win32Ui.GlobalFree(mem);
        }
        finally
        {
            Win32Ui.CloseClipboard();
        }
    }

    /// <summary>Монитор окна и его частота (выделяет — только на событиях окна).</summary>
    private void ReadRefreshRate(nint hWnd)
    {
        _monitor = Win32Ui.MonitorFromWindow(hWnd, Win32Ui.MONITOR_DEFAULTTONEAREST);
        RefreshHz = Win32Ui.RefreshRate(_monitor);
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_CLOSE:
                ShouldClose = true;
                return nint.Zero;

            case Win32.WM_DESTROY:
                ShouldClose = true;
                Win32.PostQuitMessage(0);
                return nint.Zero;

            case Win32.WM_SIZE:
            {
                // Сворачивание приходит нулевым размером; размер не трогается: свопчейн в ноль не пересоздать.
                Minimized = wParam.ToInt64() == Win32.SIZE_MINIMIZED;

                int w = (int)(lParam.ToInt64() & 0xFFFF);
                int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (w > 0 && h > 0 && (w != Width || h != Height))
                {
                    Width = w;
                    Height = h;
                    Resized = true;
                    if (_inSizeMove) LiveResize?.Invoke();
                }
                return nint.Zero;
            }

            case Win32Ui.WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                return nint.Zero;

            case Win32Ui.WM_EXITSIZEMOVE:
                _inSizeMove = false;
                ReadRefreshRate(hWnd);   // окно могли перенести на другой монитор
                return nint.Zero;

            // Окно перенесли без перетаскивания (Win+Shift+стрелка, SetWindowPos): на другой монитор — перечитать частоту.
            case Win32Ui.WM_MOVE:
                if (Win32Ui.MonitorFromWindow(hWnd, Win32Ui.MONITOR_DEFAULTTONEAREST) != _monitor) ReadRefreshRate(hWnd);
                return nint.Zero;

            case Win32Ui.WM_DISPLAYCHANGE:
                ReadRefreshRate(hWnd);
                return Win32.DefWindowProc(hWnd, msg, wParam, lParam);

            // Фон окна рисует свопчейн; стирание системой кистью давало мелькание при изменении размера.
            case Win32Ui.WM_ERASEBKGND:
                return 1;

            case Win32Ui.WM_GETMINMAXINFO when _resizable:
            {
                var frame = new RECT { Right = MinClientWidth, Bottom = MinClientHeight };
                Win32Ui.AdjustWindowRectExForDpi(ref frame, Win32Ui.WS_OVERLAPPEDWINDOW, false, 0, Math.Max(96, Win32Ui.GetDpiForWindow(hWnd)));
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.ptMinTrackSize = new POINT { X = frame.Right - frame.Left, Y = frame.Bottom - frame.Top };
                Marshal.StructureToPtr(info, lParam, false);
                return nint.Zero;
            }

            // Окно перетащили на монитор с другим масштабом. Изменяемое окно принимает размер, предложенный системой
            // (тот же логический размер); игровое остаётся как было.
            case Win32Ui.WM_DPICHANGED:
            {
                Dpi = Math.Max(96, (int)(wParam.ToInt64() & 0xFFFF));
                ReadRefreshRate(hWnd);
                if (_resizable)
                {
                    var r = Marshal.PtrToStructure<RECT>(lParam);
                    Win32.SetWindowPos(hWnd, nint.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                        Win32Ui.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                }
                return nint.Zero;
            }

            case Win32Ui.WM_SETCURSOR when (lParam.ToInt64() & 0xFFFF) == Win32Ui.HTCLIENT && !_cursorHidden:
                Win32Ui.SetCursor(CursorHandle(_cursor));
                return 1;

            // Фокус потерян — состояние клавиатуры и мыши обнуляется. WM_KEYUP доставляется окну с фокусом: удерживая
            // W нажать Alt+Tab и отпустить W в другом приложении — отпускание уйдёт туда, и клавиша залипнет.
            case Win32.WM_KILLFOCUS:
            case Win32.WM_ACTIVATEAPP:
                Input.ForgetAll();
                return Win32.DefWindowProc(hWnd, msg, wParam, lParam);

            // Захвата больше нет: кнопки считаются отжатыми, но НАЖАТИЯ ЭТОГО КАДРА помнятся. Сюда приходит и
            // собственное освобождение захвата, то есть каждый обычный щелчок, — см. InputState.ForgetMouse.
            case Win32.WM_CAPTURECHANGED:
                Input.ForgetMouse();
                return nint.Zero;

            // Бит 30 lParam — клавиша уже была нажата: это автоповтор удержания.
            case Win32.WM_KEYDOWN:
            {
                int vk = (int)(wParam.ToInt64() & 0xFF);
                Input.SetKey(vk, true);
                if (((lParam.ToInt64() >> 30) & 1) != 0) Input.RepeatKey(vk);
                return nint.Zero;
            }

            case Win32.WM_KEYUP:
                Input.SetKey((int)(wParam.ToInt64() & 0xFF), false);
                return nint.Zero;

            // Системные клавиши записываются, но НЕ поглощаются: именно DefWindowProc превращает их в WM_SYSCOMMAND
            // (Alt+F4 → SC_CLOSE → WM_CLOSE, Alt+Space — системное меню).
            case Win32.WM_SYSKEYDOWN:
            {
                int vk = (int)(wParam.ToInt64() & 0xFF);
                Input.SetKey(vk, true);
                if (((lParam.ToInt64() >> 30) & 1) != 0) Input.RepeatKey(vk);
                return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
            }

            // Одиночный Alt или F10 (SC_KEYMENU без символа) открыл бы меню окна: его модальный цикл останавливает кадры и съедает
            // следующую клавишу. Alt+Space и Alt+F4 — как обычно.
            case Win32.WM_SYSCOMMAND when (wParam.ToInt64() & 0xFFF0) == Win32.SC_KEYMENU && lParam == nint.Zero:
                return nint.Zero;

            case Win32.WM_SYSKEYUP:
                Input.SetKey((int)(wParam.ToInt64() & 0xFF), false);
                return Win32.DefWindowProc(hWnd, msg, wParam, lParam);

            case Win32.WM_MOUSEMOVE:
            {
                short x = (short)(lParam.ToInt64() & 0xFFFF);
                short y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                Input.SetMousePosition(x, y);
                MouseInside = true;

                // Заказ на WM_MOUSELEAVE одноразовый и стоит системного вызова, поэтому делается один раз на вход
                // курсора, а не на каждое движение.
                if (!_trackingLeave)
                {
                    var track = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = Win32.TME_LEAVE,
                        hwndTrack = hWnd,
                    };
                    _trackingLeave = Win32.TrackMouseEvent(ref track);
                }
                return nint.Zero;
            }

            case Win32.WM_MOUSELEAVE:
                MouseInside = false;
                _trackingLeave = false;
                return nint.Zero;

            // Нажатие ЗАХВАТЫВАЕТ мышь, отпускание последней кнопки — отпускает: без захвата отпускание за пределами
            // клиентской области уходит другому окну, и кнопка залипает нажатой.
            case Win32.WM_LBUTTONDOWN: PressMouse(hWnd, MouseButton.Left); return nint.Zero;
            case Win32.WM_LBUTTONUP: ReleaseMouse(MouseButton.Left); return nint.Zero;
            case Win32.WM_RBUTTONDOWN: PressMouse(hWnd, MouseButton.Right); return nint.Zero;
            case Win32.WM_RBUTTONUP: ReleaseMouse(MouseButton.Right); return nint.Zero;
            case Win32.WM_MBUTTONDOWN: PressMouse(hWnd, MouseButton.Middle); return nint.Zero;
            case Win32.WM_MBUTTONUP: ReleaseMouse(MouseButton.Middle); return nint.Zero;

            case Win32.WM_CHAR:
                Input.AddTypedChar((char)(wParam.ToInt64() & 0xFFFF));
                return nint.Zero;

            case Win32.WM_MOUSEWHEEL:
                Input.AddWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF) / 120.0f);
                return nint.Zero;
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // Захват держится, пока зажата хоть одна кнопка, и «зажата» спрашивается у САМОГО состояния ввода, а не у своего
    // счётчика рядом: система отбирает захват и без отпускания, и счётчик навсегда застревал бы на единице.

    private void PressMouse(nint hWnd, MouseButton button)
    {
        if (!Input.AnyMouseDown()) Win32.SetCapture(hWnd);
        Input.SetMouseButton(button, true);
    }

    private void ReleaseMouse(MouseButton button)
    {
        Input.SetMouseButton(button, false);
        if (!Input.AnyMouseDown()) Win32.ReleaseCapture();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Удержание дисплея — состояние ПОТОКА, а не окна: без отмены машина не уснёт до конца процесса.
        if (_holdsDisplay) Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS);

        if (Handle != nint.Zero)
        {
            Win32.DestroyWindow(Handle);
            Handle = nint.Zero;
        }

        // Класс снимается ПОСЛЕ окна: UnregisterClass отказывает, пока существует хоть одно окно этого класса.
        if (_classNamePtr != nint.Zero)
        {
            Win32.UnregisterClass(_className, _hInstance);
            Marshal.FreeHGlobal(_classNamePtr);
            _classNamePtr = nint.Zero;
        }
    }
}
