using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// CPU-данные меша персонажа для клиентских модулей (сервис клиента): вершины в привязке (система персонажа: +Z вперёд, +X влево, Y вверх,
/// метры), нормали в привязке, индексы треугольников, на вершину — 4 сустава (упакованы: j0 | j1 ≪ 8 | j2 ≪ 16 | j3 ≪ 24) и 4 веса
/// (четвёртый — <c>saturate(1 − сумма трёх)</c>, как в шейдере),
/// обратные матрицы привязки и сегмент тела каждого сустава. Скелет — тот же, что у поз (<see cref="IFighterPoses.Joints"/>). Загружается
/// клиентом игры вместе с рендером: до этого <see cref="Loaded"/> false и списки пусты (модуль читает его в своей инициализации).
/// </summary>
public interface ISkinMesh
{
    /// <summary>Меш загружен.</summary>
    bool Loaded { get; }

    ReadOnlySpan<Vector3> Positions { get; }

    ReadOnlySpan<Vector3> Normals { get; }

    /// <summary>Индексы треугольников (по три).</summary>
    ReadOnlySpan<uint> Indices { get; }

    /// <summary>Суставы вершины, упакованные по байту (<see cref="SkinMath.Joint"/>).</summary>
    ReadOnlySpan<uint> Joints { get; }

    /// <summary>Веса суставов вершины (x — сустав 0 … w — сустав 3).</summary>
    ReadOnlySpan<Vector4> Weights { get; }

    /// <summary>Обратные матрицы привязки по суставам: палитра скина — <c>InverseBind[j] · сустав в мире</c>.</summary>
    ReadOnlySpan<Matrix4x4> InverseBind { get; }

    /// <summary>Имена суставов скелета (<c>spine_04</c>, <c>head</c>…) — для выбора опорного сустава по имени.</summary>
    ReadOnlySpan<string> JointNames { get; }

    /// <summary>Сегмент тела (<see cref="BodySegs"/>) сустава <paramref name="joint"/> — как у скина трупа (<see cref="IBodyView.JointSegment"/>).</summary>
    int JointSegment(int joint);
}

/// <summary>Скин на CPU — та же формула, что в вершинном шейдере (<c>skin.hlsli</c>): Σ wₖ · (v · InverseBind[jₖ] · сустав[jₖ]).</summary>
public static class SkinMath
{
    /// <summary>Сустав <paramref name="k"/> (0…3) из упакованных.</summary>
    public static int Joint(uint packed, int k) => (int)(packed >> (8 * k) & 0xFF);

    /// <summary>Вершина <paramref name="vertex"/> меша в мире по суставам позы <paramref name="joints"/> (мировые матрицы).</summary>
    public static Vector3 Position(ISkinMesh mesh, int vertex, ReadOnlySpan<Matrix4x4> joints) =>
        Position(mesh.Positions[vertex], mesh.Joints[vertex], mesh.Weights[vertex], mesh.InverseBind, joints);

    /// <summary>Точка в привязке <paramref name="bind"/> в мире по суставам и весам.</summary>
    public static Vector3 Position(Vector3 bind, uint packed, Vector4 weights, ReadOnlySpan<Matrix4x4> inverseBind, ReadOnlySpan<Matrix4x4> joints)
    {
        var p = Vector3.Zero;
        for (int k = 0; k < 4; k++)
        {
            float w = k switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
            if (!(w > 0)) continue;
            int j = Joint(packed, k);
            p += w * Vector3.Transform(bind, inverseBind[j] * joints[j]);
        }
        return p;
    }
}
