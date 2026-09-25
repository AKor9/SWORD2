using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Bake.Game.Anim;

/// <summary>Замкнутость сетки: прореживание анатомии не порвало меш (строка лога выпечки).</summary>
internal static class MeshEdges
{
    /// <summary>
    /// Незамкнутые рёбра треугольников <paramref name="indices"/>: 0 — сетка замкнута. Вершины свариваются
    /// по точной позиции (швы нормалей и UV — отдельные вершины, но не дыры); ребро открыто, если встречных
    /// ему не столько же, сколько его самого. Вырожденный треугольник рёбер не даёт.
    /// </summary>
    public static int Open(ReadOnlySpan<uint> indices, Func<uint, Vector3> position)
    {
        var ids = new Dictionary<Vector3, int>();
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = Id(indices[t]), b = Id(indices[t + 1]), c = Id(indices[t + 2]);
            if (a == b || b == c || a == c) continue;
            Count((a, b));
            Count((b, c));
            Count((c, a));
        }
        int open = 0;
        foreach (var ((x, y), count) in edges)
            if (edges.GetValueOrDefault((y, x)) != count) open++;
        return open;

        int Id(uint i)
        {
            var p = position(i);
            if (!ids.TryGetValue(p, out int id)) ids[p] = id = ids.Count;
            return id;
        }

        void Count((int, int) edge) => edges[edge] = edges.GetValueOrDefault(edge) + 1;
    }

    /// <summary>Незамкнутые рёбра меша.</summary>
    public static int Open(SkinnedMeshData mesh) => Open(mesh.Indices, i => mesh.Vertices[i].Position);
}
