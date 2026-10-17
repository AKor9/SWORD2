using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Game.Cutting;

/// <summary>Вершина разрезаемой меши; побайтно совпадает с вершиной скиннинга движка (48 байт).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CutVertex
{
    /// <summary>Позиция в позе привязки.</summary>
    public Vector3 Position;

    /// <summary>Нормаль в позе привязки.</summary>
    public Vector3 Normal;

    /// <summary>Текстурные координаты; у вершин крышки Uv.x &gt; 4.</summary>
    public Vector2 Uv;

    /// <summary>Четыре индекса суставов: j0 | j1 &lt;&lt; 8 | j2 &lt;&lt; 16 | j3 &lt;&lt; 24.</summary>
    public uint Joints;

    /// <summary>Первые три веса; четвёртый — 1 − сумма.</summary>
    public Vector3 Weights;
}

/// <summary>Меша: вершины и список треугольников.</summary>
public sealed class CutMesh
{
    /// <summary>Вершины.</summary>
    public CutVertex[] Vertices = [];

    /// <summary>Индексы треугольников, по три на треугольник.</summary>
    public uint[] Indices = [];
}

/// <summary>Параметры крышки разреза.</summary>
/// <param name="Normal">Единичная нормаль плоскости крышки в мировой позе, от стороны A к стороне B.</param>
/// <param name="Origin">Мировая точка на плоскости (пересечение с осью кости) — начало UV крышки.</param>
/// <param name="Radius">Радиус конечности, м; UV крышки нормированы на него.</param>
/// <param name="BindNormalA">Нормаль крышки стороны A в позе привязки (смотрит к B).</param>
/// <param name="BindNormalB">Нормаль крышки стороны B в позе привязки (смотрит к A).</param>
/// <param name="JointA">Сустав, к которому целиком привязаны вершины крышки A.</param>
/// <param name="JointB">Сустав, к которому целиком привязаны вершины крышки B.</param>
public readonly record struct CapSpec(
    Vector3 Normal,
    Vector3 Origin,
    float Radius,
    Vector3 BindNormalA,
    Vector3 BindNormalB,
    byte JointA,
    byte JointB);

/// <summary>Результат разреза: две части и сведения о крышках.</summary>
public sealed class CutResult
{
    /// <summary>Часть со значением поля &lt; 0.</summary>
    public CutMesh A = new();

    /// <summary>Часть со значением поля ≥ 0.</summary>
    public CutMesh B = new();

    /// <summary>Число треугольников крышки части A.</summary>
    public int CapTrianglesA;

    /// <summary>Число треугольников крышки части B.</summary>
    public int CapTrianglesB;

    /// <summary>Число контуров разреза (замкнутых и открытых).</summary>
    public int Loops;

    /// <summary>Число контуров-дыр, присоединённых к внешним контурам.</summary>
    public int Holes;

    /// <summary>Отсечение ушей застряло, и часть крышки собрана веером.</summary>
    public bool FellBack;
}
