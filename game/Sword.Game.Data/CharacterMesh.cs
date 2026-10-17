using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Game.Data;

/// <summary>Вершины меша персонажа для симуляции: позиция в привязке и сустав с наибольшим весом.</summary>
public sealed class CharacterMeshPoints
{
    public required Vector3[] Positions { get; init; }
    public required int[] DominantJoint { get; init; }
}

/// <summary>Меш персонажа (.swmesh) → точки для профиля ткани (<see cref="CharacterMeshPoints"/>).</summary>
public static class CharacterMesh
{
    /// <exception cref="InvalidDataException">Не .swmesh или испорчен.</exception>
    public static CharacterMeshPoints Read(Stream mesh, string name)
    {
        var data = SkinnedMeshData.Read(mesh, name);
        var v = data.Vertices;
        var positions = new Vector3[v.Length];
        var dominant = new int[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            positions[i] = v[i].Position;
            var w = v[i].Weights;
            float w3 = 1 - w.X - w.Y - w.Z;
            int best = 0;
            float max = w.X;
            if (w.Y > max) { max = w.Y; best = 1; }
            if (w.Z > max) { max = w.Z; best = 2; }
            if (w3 > max) best = 3;
            dominant[i] = (int)((v[i].Joints >> (8 * best)) & 0xFF);
        }
        return new CharacterMeshPoints { Positions = positions, DominantJoint = dominant };
    }
}
