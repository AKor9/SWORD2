namespace Sword.Engine.Render;

/// <summary>
/// Фаза кадра: граф исполняет проходы по фазам, внутри фазы — в порядке регистрации (<see cref="RenderGraph.AddPass(string, FramePhase, PassKind)"/>).
/// Так модуль вставляет свой проход в нужное место кадра, не зная, кто и когда регистрирует остальные. С HDR
/// (<see cref="Renderer.Hdr"/>) до <see cref="Post"/> сцена пишется в <see cref="RenderFrame.SceneColor"/> (линейный цвет), задний
/// буфер пишут только <see cref="Post"/> и позже.
/// </summary>
public enum FramePhase
{
    /// <summary>GPU-симуляции (вычислительные проходы) до сцены.</summary>
    Simulation,

    /// <summary>Непрозрачная сцена: цвет, нормали, глубина. Фаза прохода без явной фазы.</summary>
    Opaque,

    /// <summary>Поверх непрозрачного по целям сцены: декали, свет по глубине и нормалям.</summary>
    Composite,

    /// <summary>Прозрачное: смешивание поверх сцены с тестом глубины.</summary>
    Transparent,

    /// <summary>Постобработка: из линейного цвета сцены в задний буфер (тонмап — последним проходом фазы).</summary>
    Post,

    /// <summary>Интерфейс поверх кадра (LDR).</summary>
    Ui,

    /// <summary>Отладочное рисование и инструменты поверх всего.</summary>
    Debug,
}
