using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Сетка высот рельефа на GPU: <see cref="Cells"/>×<see cref="Cells"/> ячеек на квадрате ±<see cref="Half"/> м, узлы по строкам z
/// (<c>height[k · Nodes + i]</c>, узел i по x — <c>−Half + i · Step</c>), значение узла — <see cref="ITerrainView.Height"/> в нём. В шейдерах
/// игры те же числа — <c>TERRAIN_CELLS</c>, <c>TERRAIN_HALF</c>.
/// </summary>
public readonly record struct TerrainGrid(int Cells, float Half)
{
    public int Nodes => Cells + 1;

    /// <summary>Шаг сетки, м.</summary>
    public float Step => 2 * Half / Cells;
}

/// <summary>
/// Рельеф арены для клиентских модулей (сервис клиента; форма — общая с постановкой стоп этапа 9): высота со ступенями (точная), гладкая
/// высота без ступеней с градиентом, составляющая ступеней, нормаль; вне участков рельефа — пол 0. И буфер высот узлов на GPU, по которому
/// рисуется рельеф, — шейдеру модуля (<see cref="HeightsSrv"/>, <see cref="Grid"/>). Только чтение, функции чистые.
/// </summary>
public interface ITerrainView
{
    /// <summary>Высота в точке (x, z), м — со ступенями.</summary>
    float Height(float x, float z);

    /// <summary>Высота без ступеней (непрерывная) и её градиент <c>(∂h/∂x, ∂h/∂z)</c>.</summary>
    float SmoothHeight(float x, float z, out Vector2 gradient);

    /// <summary>Составляющая ступеней в точке, м: кромка — её смена.</summary>
    float StepHeight(float x, float z);

    /// <summary>Нормаль рельефа (конечные разности высоты со ступенями: на ребре ступени — резкий наклон в полосе ±5 см).</summary>
    Vector3 Normal(float x, float z);

    /// <summary>Сетка буфера высот на GPU.</summary>
    TerrainGrid Grid { get; }

    /// <summary>Индекс SRV буфера высот узлов (<c>Arena.Heights</c>, float на узел) в общей таблице дескрипторов; −1 — рендера ещё нет.</summary>
    int HeightsSrv { get; }
}
