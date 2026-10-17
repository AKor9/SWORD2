using System.Numerics;
using Sword.Game.Cutting;

namespace Sword.Game.Tests.Cutting;

/// <summary>Тестовые меши: трубы с UV-швом и отдельными вершинами торцов, ориентированные наружу (CCW).</summary>
internal static class TestMeshes
{
    public delegate (uint Joints, Vector3 Weights) Skin(float z);

    // Кость по высоте: суставы b и b + 1 с плавным переходом; всего 4 сустава.
    public static (uint, Vector3) DefaultSkin(float z)
    {
        float s = Math.Clamp(z, 0f, 2.999f);
        int b = Math.Min((int)s, 2);
        float a = s - b;
        return ((uint)b | (uint)(b + 1) << 8, new Vector3(1 - a, a, 0));
    }

    public sealed class Builder
    {
        public readonly List<CutVertex> V = new();
        public readonly List<uint> I = new();

        public int Add(Vector3 p, Vector3 n, Vector2 uv, Skin skin)
        {
            var (j, w) = skin(p.Z);
            V.Add(new CutVertex { Position = p, Normal = n, Uv = uv, Joints = j, Weights = w });
            return V.Count - 1;
        }

        public void Tri(int a, int b, int c)
        {
            I.Add((uint)a);
            I.Add((uint)b);
            I.Add((uint)c);
        }

        // Четырёхугольник a-b-c-d, обход против часовой стрелки.
        public void Quad(int a, int b, int c, int d)
        {
            Tri(a, b, c);
            Tri(a, c, d);
        }

        public CutMesh Build() => new() { Vertices = V.ToArray(), Indices = I.ToArray() };
    }

    private static Vector3 Ring(Vector3 center, float r, int j, int radial, float z)
    {
        // Угол по j % radial — позиции шва совпадают бит в бит.
        float ang = 2f * MathF.PI * (j % radial) / radial;
        return center + new Vector3(r * MathF.Cos(ang), r * MathF.Sin(ang), z);
    }

    private static float Z(int k, int rings, float height) => height * k / rings;

    // Боковая поверхность; inward — нормали и обход к оси (внутренняя стенка толстой трубы).
    private static int[,] Side(Builder b, Vector3 c, float r, float h, int radial, int rings, bool inward, Skin skin)
    {
        var id = new int[rings + 1, radial + 1];
        for (int k = 0; k <= rings; k++)
            for (int j = 0; j <= radial; j++)
            {
                Vector3 p = Ring(c, r, j, radial, Z(k, rings, h));
                Vector3 n = Vector3.Normalize(new Vector3(p.X - c.X, p.Y - c.Y, 0));
                id[k, j] = b.Add(p, inward ? -n : n, new Vector2((float)j / radial, (float)k / rings), skin);
            }
        for (int k = 0; k < rings; k++)
            for (int j = 0; j < radial; j++)
            {
                if (inward)
                    b.Quad(id[k, j], id[k + 1, j], id[k + 1, j + 1], id[k, j + 1]);
                else
                    b.Quad(id[k, j], id[k, j + 1], id[k + 1, j + 1], id[k + 1, j]);
            }
        return id;
    }

    public static void AddTube(Builder b, Vector3 c, float r, float h, int radial, int rings, bool capped, Skin? skin = null)
    {
        skin ??= DefaultSkin;
        Side(b, c, r, h, radial, rings, false, skin);
        if (!capped)
            return;
        for (int end = 0; end < 2; end++)
        {
            float z = end == 0 ? 0 : h;
            var n = new Vector3(0, 0, end == 0 ? -1 : 1);
            int center = b.Add(c + new Vector3(0, 0, z), n, new Vector2(0.5f, 0.5f), skin);
            var rim = new int[radial];
            for (int j = 0; j < radial; j++)
            {
                Vector3 p = Ring(c, r, j, radial, z);
                rim[j] = b.Add(p, n, new Vector2(0.5f + 0.5f * (p.X - c.X) / r, 0.5f + 0.5f * (p.Y - c.Y) / r), skin);
            }
            for (int j = 0; j < radial; j++)
            {
                int j1 = (j + 1) % radial;
                if (end == 0)
                    b.Tri(center, rim[j1], rim[j]);
                else
                    b.Tri(center, rim[j], rim[j1]);
            }
        }
    }

    public static CutMesh Tube(float r = 0.5f, float h = 2f, int radial = 24, int rings = 12, bool capped = true, Skin? skin = null)
    {
        var b = new Builder();
        AddTube(b, Vector3.Zero, r, h, radial, rings, capped, skin);
        return b.Build();
    }

    // Толстостенная труба: сечение — кольцо между радиусами rIn и rOut.
    public static CutMesh ThickTube(float rOut = 0.5f, float rIn = 0.3f, float h = 2f, int radial = 24, int rings = 12)
    {
        var b = new Builder();
        Skin skin = DefaultSkin;
        Side(b, Vector3.Zero, rOut, h, radial, rings, false, skin);
        Side(b, Vector3.Zero, rIn, h, radial, rings, true, skin);
        for (int end = 0; end < 2; end++)
        {
            float z = end == 0 ? 0 : h;
            var n = new Vector3(0, 0, end == 0 ? -1 : 1);
            var o = new int[radial];
            var i = new int[radial];
            for (int j = 0; j < radial; j++)
            {
                o[j] = b.Add(Ring(Vector3.Zero, rOut, j, radial, z), n, new Vector2(1, (float)j / radial), skin);
                i[j] = b.Add(Ring(Vector3.Zero, rIn, j, radial, z), n, new Vector2(0, (float)j / radial), skin);
            }
            for (int j = 0; j < radial; j++)
            {
                int j1 = (j + 1) % radial;
                if (end == 1)
                    b.Quad(o[j], o[j1], i[j1], i[j]);
                else
                    b.Quad(o[j], i[j], i[j1], o[j1]);
            }
        }
        return b.Build();
    }
}
