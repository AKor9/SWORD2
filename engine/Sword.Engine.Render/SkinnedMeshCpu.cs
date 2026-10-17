using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Скинированный меш на CPU (<see cref="SkinnedMeshes.ReadCpu"/>) — для эффектов, которым нужна геометрия кожи (якоря, потёки): вершины и
/// нормали в привязке, индексы, суставы вершины (по байту: j0 | j1 ≪ 8 | j2 ≪ 16 | j3 ≪ 24), веса (четвёртый — <c>saturate(1 − сумма трёх)</c>,
/// как в <c>skin.hlsli</c>), обратные матрицы привязки и имена суставов. Массивы — общие с вызывающим: не менять.
/// </summary>
public sealed class SkinnedMeshCpu
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required uint[] Indices { get; init; }
    public required uint[] Joints { get; init; }
    public required Vector4[] Weights { get; init; }
    public required Matrix4x4[] InverseBind { get; init; }
    public required string[] JointNames { get; init; }
}
