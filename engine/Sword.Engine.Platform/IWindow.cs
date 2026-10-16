namespace Sword.Engine.Platform;

/// <summary>Как создать окно.</summary>
/// <param name="Activate">
/// Окно забирает фокус. Ложь — для проверочных прогонов: окно показано, но в самом низу порядка перекрытия и без фокуса
/// (скрытое окно графический стек считает перекрытым).
/// </param>
/// <param name="Fullscreen">Окно без рамы на весь монитор, поверх панели задач. Ложь — окно заданного размера по центру.</param>
/// <param name="Resizable">Окно с рамой и изменяемым размером (инструменты); только без <paramref name="Fullscreen"/>.</param>
/// <param name="Minimized">Окно сразу свёрнуто и без фокуса; размер клиентской части — заданный.</param>
public sealed record WindowOptions(
    string Title,
    int Width = 1920,
    int Height = 1080,
    bool Activate = true,
    bool Fullscreen = false,
    bool Resizable = false,
    bool Minimized = false);

/// <summary>Окно платформы: клиент движка работает только с ним, конкретную платформу (Win32) создаёт exe.</summary>
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
    /// Частота обновления монитора окна, Гц; 0 — неизвестна. Обновляется при прокачке сообщений; клиент пишет её в журнал старта и итог.
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
    /// Размер меняется прямо сейчас: пока край тянут, система крутит свой цикл внутри <see cref="PumpMessages"/>, и главный
    /// цикл стоит — подписчик перерисовывает кадр, иначе картинка замирает.
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
