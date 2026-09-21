using Sword.Engine.Render;

namespace Sword.Engine.Client;

/// <summary>
/// Клиентская часть игры, которую ведёт <see cref="ClientHost"/>. Кадр (раздел 7 плана): <see cref="Input"/> — ввод и
/// камера, намерения игрока на тики кадра → тики, перед каждым — <see cref="BeforeTick"/> (команды) → <see cref="Extract"/>
/// — мир представления из симуляции и очереди кадра, с интерполяцией → <see cref="BuildFrame"/> — проходы кадра только из
/// мира представления.
/// </summary>
public interface IClientGame : IDisposable
{
    /// <summary>Действия ввода игры с клавишами по умолчанию; привязки — секция конфига <c>input</c>.</summary>
    IReadOnlyList<InputAction> Actions { get; }

    /// <summary>Числа игры для шейдеров — до компиляции первого шейдера.</summary>
    void ContributeShaders(ShaderContract contract);

    /// <summary>Контент и ресурсы GPU; вызывается один раз, когда рендер готов. <paramref name="simulation"/> — null без симуляции.</summary>
    void Initialize(Renderer renderer, SimulationLink? simulation);

    /// <summary>Ввод кадра, до его тиков: камера, намерения игрока на тики этого кадра.</summary>
    void Input(in FrameInput input);

    /// <summary>
    /// Перед каждым тиком кадра: команды на этот тик. Удерживаемое — в каждый тик (из <see cref="Input"/>); нажатия и
    /// отпускания — из <paramref name="input"/>: они достаются ровно одному тику, первому после кадра, где случились.
    /// </summary>
    void BeforeTick(SimulationLink simulation, in TickInput input);

    /// <summary>После тиков кадра: мир представления — из симуляции и очереди кадра, с интерполяцией по доле тика.</summary>
    void Extract(in FrameView view);

    /// <summary>Проходы кадра: константы кадра и проходы сцены в граф — из мира представления.</summary>
    void BuildFrame(RenderFrame frame);
}
