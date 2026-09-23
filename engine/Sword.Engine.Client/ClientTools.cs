using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Render;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>
/// Ворота тиков: инструмент (агент, DevTools) решает, сколько тиков сыграть в кадре, вместо часов — пауза, шаги.
/// </summary>
public interface ITickGate
{
    /// <summary>
    /// null — тики по часам (или заданные <see cref="ClientOptions.TicksPerFrame"/>); число — ровно столько тиков в этом
    /// кадре (пауза — 0, шаги — запас), часы при этом стоят: накопитель обнуляется, кадр показывает последний тик.
    /// </summary>
    int? Override(int maxPerFrame);

    /// <summary>Тики кадра сыграны: всего сыграно <paramref name="tick"/>.</summary>
    void Ran(ulong tick);

    /// <summary>
    /// После каждого тика кадра: true — остальные тики кадра не играть (пауза или условие инструмента встали на этом
    /// тике, а не в конце кадра). Накопитель часов при этом обнуляется.
    /// </summary>
    bool StopAfterTick(ulong tick) => false;
}

/// <summary>
/// Инструмент клиента (раздел 8: агент, позже DevTools) — на потоке кадра, в его шагах: точка обслуживания после тиков
/// кадра (мир согласован; и на паузе), снимок на границе кадра и итог показанного кадра. Клиент об агенте не знает.
/// </summary>
public interface IClientTool
{
    /// <summary>
    /// Точка обслуживания: тики кадра сыграны, мир согласован. <paramref name="frame"/> — номер кадра (с 1). Истина — инструмент
    /// выполнил работу (запросы агента): кадр — кадр инструментов, служебный (после Пв, решение пользователя: не в среднем и
    /// бюджете кадра, профайлер не судит его как кадр). Ложь — только проверил очередь.
    /// </summary>
    bool Serve(Simulation simulation, ulong frame);

    /// <summary>Снять ли этот кадр: null — нет.</summary>
    FrameCapture? Capture(ulong frame);

    /// <summary>
    /// Кадр показан (каждый, в том числе из перетаскивания рамки): рендер и <c>simulation.Tick</c> — этого кадра.
    /// <paramref name="capture"/> — снятое по заказу этого инструмента (null — он ничего не заказывал в этом кадре),
    /// <paramref name="result"/> — итог (null — снимок не записан, причина в журнале).
    /// </summary>
    void Presented(Simulation simulation, ulong frame, FrameCapture? capture, CaptureResult? result);
}

/// <summary>Камера кадра для наблюдения: матрица вида-проекции, глаз, точка взгляда, параметры RTS-камеры.</summary>
public readonly record struct CameraView(Matrix4x4 ViewProj, Vector3 Eye, Vector3 Target, float Yaw, float Pitch, float Distance, bool Follow);

/// <summary>Видимое в мире: сущность и её рамка в мире (ось-выровненный параллелепипед).</summary>
public readonly record struct VisibleEntity(Entity Entity, string Kind, Vector3 Min, Vector3 Max);

/// <summary>
/// Код ID-буфера снимка словами: сущность, её вид («player», «enemy»), часть (сегмент тела — «forearm.l»; null — у кода
/// нет части) и название для людей («левое предплечье врага 3»).
/// </summary>
public readonly record struct IdMeaning(Entity Entity, string Kind, string? Part, string Title);

/// <summary>
/// Наблюдаемость клиентской части игры для инструментов (пакет наблюдения <c>capture.bundle</c>): камера показанного
/// кадра, что в нём видно, какие оверлеи игра снимает слоем и что значат коды ID-буфера. Необязательна: игра без неё
/// даёт снимок без сущностей.
/// </summary>
public interface IClientObservable
{
    /// <summary>Камера последнего кадра.</summary>
    CameraView Camera { get; }

    /// <summary>Видимые сущности последнего кадра — в показанном (интерполированном) положении.</summary>
    void Visible(List<VisibleEntity> into);

    /// <summary>
    /// Оверлеи, которые игра рисует слоем снимка (<c>RenderFrame.CaptureLayer</c>): имена для <c>capture.bundle</c> и видов
    /// манифеста фич (<c>views[].overlays</c>). Пусто — оверлеев нет.
    /// </summary>
    IReadOnlyList<string> Overlays => [];

    /// <summary>
    /// Что значит код ID-буфера последнего кадра (<c>SkinnedMeshes.IdOf</c>: владелец и часть); код, которого игра не
    /// выдавала, — null. Зовётся на потоке кадра сразу после показа снятого кадра.
    /// </summary>
    IdMeaning? DescribeId(uint id) => null;
}
