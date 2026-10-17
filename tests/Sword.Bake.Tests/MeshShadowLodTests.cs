using System.Numerics;
using Sword.Bake.Anim;
using Sword.Engine.Formats;

namespace Sword.Bake.Tests;

/// <summary>
/// Меш теней (<see cref="MeshDecimate.ShadowLod"/>): индексы по исходным вершинам в пределах бюджета, исходный меш не меняется, шов
/// развёртки прореживанию не мешает, форма та же, повтор — те же индексы.
/// </summary>
public class MeshShadowLodTests
{
    /// <summary>Шар с развёрткой: шов по долготе 0 — вершины с той же точкой и другой UV; полюса — по одной вершине.</summary>
    private static SkinnedMeshData Sphere(int rings, int segments)
    {
        var vertices = new List<SkinVertex>();
        var indices = new List<uint>();
        vertices.Add(new SkinVertex { Position = Vector3.UnitY, Normal = Vector3.UnitY, Weights = new(1, 0, 0) });
        for (int r = 1; r < rings; r++)
        {
            float theta = MathF.PI * r / rings;
            for (int s = 0; s <= segments; s++)
            {
                float phi = 2 * MathF.PI * (s % segments) / segments;
                var p = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                vertices.Add(new SkinVertex { Position = p, Normal = p, Uv = new((float)s / segments, (float)r / rings), Weights = new(1, 0, 0) });
            }
        }
        int south = vertices.Count;
        vertices.Add(new SkinVertex { Position = -Vector3.UnitY, Normal = -Vector3.UnitY, Weights = new(1, 0, 0) });
        uint Ring(int r, int s) => (uint)(1 + (r - 1) * (segments + 1) + s);
        for (int s = 0; s < segments; s++)
        {
            indices.AddRange([0, Ring(1, s + 1), Ring(1, s)]);
            indices.AddRange([(uint)south, Ring(rings - 1, s), Ring(rings - 1, s + 1)]);
        }
        for (int r = 1; r < rings - 1; r++)
            for (int s = 0; s < segments; s++)
            {
                indices.AddRange([Ring(r, s), Ring(r, s + 1), Ring(r + 1, s + 1)]);
                indices.AddRange([Ring(r, s), Ring(r + 1, s + 1), Ring(r + 1, s)]);
            }
        return new SkinnedMeshData { Vertices = [.. vertices], Indices = [.. indices], JointNames = ["root"], JointParents = [-1], InverseBind = [Matrix4x4.Identity] };
    }

    [Fact]
    public void ShadowLod_SubsetOfVertices_WithinBudget_MeshUntouched_SameShape()
    {
        var mesh = Sphere(24, 48);
        var vertices = mesh.Vertices.ToArray();
        var indices = mesh.Indices.ToArray();
        int budget = indices.Length / 3 / 6;

        var lod = MeshDecimate.ShadowLod(mesh, budget);

        Assert.Equal(vertices, mesh.Vertices);
        Assert.Equal(indices, mesh.Indices);
        Assert.Equal(0, lod.Length % 3);
        Assert.InRange(lod.Length / 3, budget * 9 / 10, budget);
        Assert.All(lod, i => Assert.True(i < vertices.Length));
        // Форма: точки меша теней — на шаре, его треугольники не вырождены и смотрят наружу.
        for (int t = 0; t < lod.Length; t += 3)
        {
            Vector3 a = vertices[lod[t]].Position, b = vertices[lod[t + 1]].Position, c = vertices[lod[t + 2]].Position;
            var n = Vector3.Cross(b - a, c - a);
            Assert.True(n.Length() > 1e-6f, $"треугольник {t / 3} вырожден");
            Assert.True(Vector3.Dot(n, a + b + c) < 0 == Vector3.Dot(Vector3.Cross(
                vertices[indices[1]].Position - vertices[indices[0]].Position,
                vertices[indices[2]].Position - vertices[indices[0]].Position), vertices[indices[0]].Position) < 0, $"треугольник {t / 3} перевёрнут");
        }
        // Шов развёртки не держит: обычное прореживание (швы неподвижны) — не меньше.
        var kept = Sphere(24, 48);
        Assert.True(MeshDecimate.Run(kept, budget) >= lod.Length / 3);
        Assert.Equal(lod, MeshDecimate.ShadowLod(Sphere(24, 48), budget));
    }
}
