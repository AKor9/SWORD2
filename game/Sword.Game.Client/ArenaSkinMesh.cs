using System.Numerics;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Меш персонажа на CPU для клиентских модулей (<see cref="ISkinMesh"/>): тот же файл, что рисует клиент арены
/// (<see cref="ArenaClient.MannyMesh"/>), читает рендер (<see cref="SkinnedMeshes.ReadCpu"/>) при первом обращении после того, как клиент
/// арены дал источник (<see cref="Attach"/>, в его инициализации): без потребителя меш в память не грузится.
/// </summary>
public sealed class ArenaSkinMesh : ISkinMesh
{
    private Func<SkinnedMeshCpu>? _source;
    private SkinnedMeshCpu? _mesh;
    private int[] _segments = [];

    /// <summary>Источник меша и сегменты суставов скелета (<see cref="IBodyView.JointSegment"/>) — когда рендер готов.</summary>
    internal void Attach(Func<SkinnedMeshCpu> source, IBodyView bodies)
    {
        _source = source;
        _mesh = null;
        _segments = new int[bodies.JointCount];
        for (int j = 0; j < _segments.Length; j++) _segments[j] = bodies.JointSegment(j);
    }

    public bool Loaded => Mesh is not null;

    public ReadOnlySpan<Vector3> Positions => Mesh?.Positions;

    public ReadOnlySpan<Vector3> Normals => Mesh?.Normals;

    public ReadOnlySpan<uint> Indices => Mesh?.Indices;

    public ReadOnlySpan<uint> Joints => Mesh?.Joints;

    public ReadOnlySpan<Vector4> Weights => Mesh?.Weights;

    public ReadOnlySpan<Matrix4x4> InverseBind => Mesh?.InverseBind;

    public ReadOnlySpan<string> JointNames => Mesh?.JointNames;

    public int JointSegment(int joint) => (uint)joint < (uint)_segments.Length
        ? _segments[joint]
        : throw new ArgumentOutOfRangeException(nameof(joint), joint, $"суставов {_segments.Length}");

    private SkinnedMeshCpu? Mesh
    {
        get
        {
            if (_mesh is null && _source is not null)
            {
                var mesh = _source();
                if (mesh.InverseBind.Length != _segments.Length)
                    throw new InvalidDataException($"{ArenaClient.MannyMesh}: суставов меша {mesh.InverseBind.Length}, скелета бойца {_segments.Length}");
                _mesh = mesh;
            }
            return _mesh;
        }
    }
}
