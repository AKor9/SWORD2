using Sword.Engine.Render;

namespace Sword.Engine.Client;

/// <summary>
/// Клиентская часть игры, которую ведёт <see cref="ClientHost"/>: числа для шейдеров, ресурсы GPU, команды тиков,
/// кадр сцены. Точка 1в: команды тиков и чтение мира для кадра; ввод, Extract с интерполяцией и камера — точка 1г.
/// </summary>
public interface IClientGame : IDisposable
{
    /// <summary>Числа игры для шейдеров — до компиляции первого шейдера.</summary>
    void ContributeShaders(ShaderContract contract);

    /// <summary>Контент и ресурсы GPU; вызывается один раз, когда рендер готов. <paramref name="simulation"/> — null без симуляции.</summary>
    void Initialize(Renderer renderer, SimulationLink? simulation);

    /// <summary>Перед каждым тиком кадра: команды на этот тик (удерживаемые действия — в каждый тик).</summary>
    void BeforeTick(SimulationLink simulation);

    /// <summary>Проходы кадра: константы кадра и проходы сцены в граф.</summary>
    void BuildFrame(RenderFrame frame);
}
