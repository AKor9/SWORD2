namespace Sword.Engine.Platform;

/// <summary>
/// Как создать окно.
/// </summary>
/// <param name="Activate">
/// Окно выходит на передний план и забирает фокус. Ложь — для проверочных прогонов: окно показано, но лежит в самом
/// низу порядка перекрытия и фокус не отбирает, чтобы не мешать тому, в чём в это время работают. Показано, а не
/// скрыто: скрытое окно графический стек считает перекрытым.
/// </param>
/// <param name="Fullscreen">Окно без рамы на весь монитор, поверх панели задач. Ложь — окно заданного размера по центру.</param>
/// <param name="Resizable">
/// Окно с рамой, заголовком и изменяемым размером (инструменты, отладка). Только вместе с
/// <paramref name="Fullscreen"/> = ложь. Игровое окно рамы не имеет: за неё платит вся картинка.
/// </param>
/// <param name="Minimized">
/// Окно показано сразу свёрнутым и без фокуса (проверка, что проверочный прогон идёт и в свёрнутом окне); размер клиентской
/// части — заданный, как у развёрнутого.
/// </param>
public sealed record WindowOptions(
    string Title,
    int Width = 1920,
    int Height = 1080,
    bool Activate = true,
    bool Fullscreen = false,
    bool Resizable = false,
    bool Minimized = false);

/// <summary>
/// Окно платформы. Клиент движка работает только с этим интерфейсом; конкретную платформу (Win32) создаёт exe
/// и передаёт сюда (раздел 4 ARCHITECTURE_PLAN.md: Platform.Win32 — только из exe).
/// </summary>
public interface IWindow : IDisposable
{
    /// <summary>Системный дескриптор окна для свопчейна (HWND на Windows).</summary>
    nint NativeHandle { get; }

    /// <summary>Размер клиентской области в физических пикселях.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>Точек на дюйм монитора окна; 96 — масштаб 100 %.</summary>
    int Dpi { get; }

    /// <summary>
    /// Частота обновления монитора окна, Гц; 0 — неизвестна. Обновляется при прокачке сообщений (смена режима экрана, окно
    /// перенесли на другой монитор), читать можно каждый кадр — это поле. Клиент отдаёт её профайлеру: интервал показа — порог
    /// «заметной» аномалии кадра.
    /// </summary>
    int RefreshHz => 0;

    /// <summary>Размер изменился за последнюю прокачку сообщений.</summary>
    bool Resized { get; }

    /// <summary>Окно свёрнуто: рисовать в него нечего.</summary>
    bool Minimized { get; }

    /// <summary>Курсор внутри клиентской области.</summary>
    bool MouseInside { get; }

    /// <summary>Окно попросили закрыть.</summary>
    bool ShouldClose { get; }

    /// <summary>Ввод кадра: обновляется в <see cref="PumpMessages"/>.</summary>
    InputState Input { get; }

    /// <summary>
    /// Размер меняется прямо сейчас — пользователь тянет край или заголовок окна. Пока мышь не отпущена, система
    /// крутит собственный цикл сообщений внутри <see cref="PumpMessages"/>, и главный цикл кадра стоит; подписчик
    /// успевает перерисовать кадр под новый размер, иначе картинка замирает до отпускания.
    /// </summary>
    event Action? LiveResize;

    /// <summary>Выгребает очередь сообщений. Возвращает false, когда окно закрыли.</summary>
    bool PumpMessages();

    /// <summary>Спать, пока не придёт сообщение, но не дольше срока (отрицательный — без срока). Для окон, которые рисуют по событию.</summary>
    void WaitForEvents(int timeoutMs);

    /// <summary>Разбудить <see cref="WaitForEvents"/> из любого потока.</summary>
    void Wake();

    void SetTitle(string title);

    /// <summary>Задать размер клиентской области. Рама, если есть, добавляется снаружи.</summary>
    void SetClientSize(int width, int height);

    /// <summary>Захват курсора: скрыт и возвращается в центр окна после каждой прокачки — мышь даёт чистые дельты.</summary>
    bool CursorLock { get; set; }

    /// <summary>Применить захват. Зовётся после чтения ввода кадра.</summary>
    void ApplyCursorLock();

    /// <summary>Форма курсора над клиентской областью.</summary>
    CursorShape Cursor { get; set; }

    /// <summary>Текст из буфера обмена; нет текста или буфер занят — null.</summary>
    string? GetClipboardText();

    /// <summary>Положить текст в буфер обмена; буфер занят — молча ничего.</summary>
    void SetClipboardText(string text);
}
