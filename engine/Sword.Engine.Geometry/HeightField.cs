using System.Numerics;

namespace Sword.Engine.Geometry;

/// <summary>
/// Поле высот — одна правда о рельефе: физика и IK читают его на CPU, шейдеры — те же узлы буфером. Сетка
/// <see cref="Nodes"/>² над [−<see cref="Half"/>, +Half]² по x и z, высоты по строкам z: <c>Heights[k·Nodes + i]</c>;
/// вне области — у ближайшего края. Между узлами высота линейна: уступы задают примитивами рядом.
/// </summary>
public sealed class HeightField
{
    /// <summary>Ячеек на сторону.</summary>
    public int Cells { get; }

    /// <summary>Узлов на сторону: <see cref="Cells"/> + 1.</summary>
    public int Nodes => Cells + 1;

    /// <summary>Полуразмер области, м.</summary>
    public float Half { get; }

    /// <summary>Шаг сетки, м.</summary>
    public float Step { get; }

    private readonly float[] _heights;

    /// <summary>Высоты узлов по строкам z (<see cref="Nodes"/>² значений).</summary>
    public ReadOnlySpan<float> Heights => _heights;

    public HeightField(int cells, float half, float[] heights)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);
        if (!(half > 0)) throw new ArgumentOutOfRangeException(nameof(half), half, "полуразмер должен быть больше нуля");
        int nodes = cells + 1;
        if (heights.Length != nodes * nodes)
            throw new ArgumentException($"ждали {nodes}×{nodes} = {nodes * nodes} высот, дано {heights.Length}", nameof(heights));
        Cells = cells;
        Half = half;
        Step = 2 * half / cells;
        _heights = heights;
    }

    /// <summary>Поле из функции высоты: значение в каждом узле, больше ничего.</summary>
    public static HeightField FromFunction(int cells, float half, Func<float, float, float> height)
    {
        int nodes = cells + 1;
        float step = 2 * half / cells;
        var h = new float[nodes * nodes];
        for (int k = 0; k < nodes; k++)
            for (int i = 0; i < nodes; i++)
                h[k * nodes + i] = height(NodeCoord(i, half, step), NodeCoord(k, half, step));
        return new HeightField(cells, half, h);
    }

    /// <summary>Координата узла — одним умножением без накопления шага, как в вершинном шейдере сетки.</summary>
    private static float NodeCoord(int i, float half, float step) => -half + i * step;

    /// <summary>Координата узла <paramref name="i"/> по x или z, м.</summary>
    public float NodeCoord(int i) => NodeCoord(i, Half, Step);

    /// <summary>Высота узла (i по x, k по z).</summary>
    public float Node(int i, int k) => _heights[k * Nodes + i];

    /// <summary>
    /// Высота в точке. Ячейка делится диагональю (0, 0)–(1, 1), как в вершинном шейдере, — CPU и картинка сходятся и на склонах.
    /// </summary>
    public float Sample(float x, float z)
    {
        float fx = Math.Clamp((x + Half) / Step, 0, Cells), fz = Math.Clamp((z + Half) / Step, 0, Cells);
        int i = Math.Min((int)fx, Cells - 1), k = Math.Min((int)fz, Cells - 1);
        float u = fx - i, v = fz - k;
        float h00 = Node(i, k), h11 = Node(i + 1, k + 1);
        return u >= v
            ? h00 + u * (Node(i + 1, k) - h00) + v * (h11 - Node(i + 1, k))
            : h00 + v * (Node(i, k + 1) - h00) + u * (h11 - Node(i, k + 1));
    }

    /// <summary>Нормаль треугольника сетки под точкой (вверх, единичная).</summary>
    public Vector3 Normal(float x, float z)
    {
        float fx = Math.Clamp((x + Half) / Step, 0, Cells), fz = Math.Clamp((z + Half) / Step, 0, Cells);
        int i = Math.Min((int)fx, Cells - 1), k = Math.Min((int)fz, Cells - 1);
        float u = fx - i, v = fz - k;
        float h00 = Node(i, k), h11 = Node(i + 1, k + 1);
        float dx, dz;
        if (u >= v)
        {
            float h10 = Node(i + 1, k);
            dx = h10 - h00;
            dz = h11 - h10;
        }
        else
        {
            float h01 = Node(i, k + 1);
            dx = h11 - h01;
            dz = h01 - h00;
        }
        return Vector3.Normalize(new Vector3(-dx / Step, 1, -dz / Step));
    }

    /// <summary>Наименьшая и наибольшая высота узлов.</summary>
    public (float Min, float Max) Range()
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (float h in _heights)
        {
            min = MathF.Min(min, h);
            max = MathF.Max(max, h);
        }
        return (min, max);
    }
}
