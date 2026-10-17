using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Game.Cutting;
using Xunit.Abstractions;

namespace Sword.Game.Tests.Cutting;

public sealed class MeshCutterTests(ITestOutputHelper output)
{
    private const byte JointA = 2, JointB = 3;

    private static CapSpec Spec(Vector3 n, Vector3 o, float radius = 2f) => new(n, o, radius, n, -n, JointA, JointB);

    private static CutResult CutByPlane(CutMesh m, Vector3 n, Vector3 o, float radius = 2f)
    {
        var f = new float[m.Vertices.Length];
        var posed = new Vector3[m.Vertices.Length];
        for (int i = 0; i < f.Length; i++)
        {
            posed[i] = m.Vertices[i].Position;
            f[i] = Vector3.Dot(posed[i] - o, n);
        }
        return MeshCutter.Cut(m, f, posed, Spec(n, o, radius));
    }

    private static Vector3 RandomUnit(Random rng)
    {
        while (true)
        {
            var v = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1);
            float l = v.Length();
            if (l > 0.2f && l <= 1f)
                return v / l;
        }
    }

    private static bool IsCap(in CutVertex v) => v.Uv.X > 4;

    // Каждый треугольник крышки этого разреза (вершины крышки с нормалью dir) смотрит в сторону dir;
    // крышки прошлых разрезов имеют другую нормаль.
    private static int AssertCapsFace(CutMesh m, Vector3 dir)
    {
        int count = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            CutVertex a = m.Vertices[m.Indices[t]], b = m.Vertices[m.Indices[t + 1]], c = m.Vertices[m.Indices[t + 2]];
            if (!IsCap(a) || !IsCap(b) || !IsCap(c) || a.Normal != dir || b.Normal != dir || c.Normal != dir)
                continue;
            count++;
            float d = Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), dir);
            // Допуск — на почти вырожденные щепки от почти совпадающих точек контура.
            Assert.True(d >= -1e-5f, $"треугольник крышки смотрит не туда: {d}");
        }
        return count;
    }

    private static void AssertValid(CutMesh m)
    {
        Assert.Equal(0, m.Indices.Length % 3);
        foreach (uint i in m.Indices)
            Assert.True(i < m.Vertices.Length);
    }

    private static void AssertSplit(CutMesh input, CutResult r, Vector3 n, float volume)
    {
        AssertValid(r.A);
        AssertValid(r.B);
        Assert.Equal(0, MeshCutter.OpenEdges(r.A));
        Assert.Equal(0, MeshCutter.OpenEdges(r.B));
        float va = MeshCutter.Volume(r.A), vb = MeshCutter.Volume(r.B);
        Assert.True(va >= -1e-6f * volume, $"объём A {va}");
        Assert.True(vb >= -1e-6f * volume, $"объём B {vb}");
        Assert.True(MathF.Abs(va + vb - volume) <= 0.01f * volume, $"{va} + {vb} != {volume}");
        Assert.False(r.FellBack);
        Assert.Equal(r.CapTrianglesA, AssertCapsFace(r.A, n));
        Assert.Equal(r.CapTrianglesB, AssertCapsFace(r.B, -n));
        Assert.True(r.A.Vertices.Length + r.B.Vertices.Length >= input.Vertices.Length - Unreferenced(input));
    }

    private static int Unreferenced(CutMesh m)
    {
        var used = new bool[m.Vertices.Length];
        foreach (uint i in m.Indices)
            used[i] = true;
        return used.Count(u => !u);
    }

    [Fact]
    public void InputTubeIsClosedAndPositive()
    {
        CutMesh tube = TestMeshes.Tube();
        Assert.Equal(0, MeshCutter.OpenEdges(tube));
        Assert.True(MeshCutter.Volume(tube) > 0);
        CutMesh thick = TestMeshes.ThickTube();
        Assert.Equal(0, MeshCutter.OpenEdges(thick));
        Assert.True(MeshCutter.Volume(thick) > 0);
    }

    [Fact]
    public void RandomPlanesGiveClosedPartsWithMatchingVolume()
    {
        CutMesh tube = TestMeshes.Tube();
        float volume = MeshCutter.Volume(tube);
        var rng = new Random(1234);
        int split = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            Vector3 n = RandomUnit(rng);
            var o = new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() * 2f);
            CutResult r = CutByPlane(tube, n, o);
            AssertSplit(tube, r, n, volume);
            if (r.A.Indices.Length > 0 && r.B.Indices.Length > 0)
            {
                split++;
                Assert.True(r.CapTrianglesA > 0);
                Assert.Equal(1, r.Loops);
            }
        }
        Assert.True(split > 100, $"разрезов {split}");
    }

    [Fact]
    public void PlaneThroughVertexRingStaysClosed()
    {
        CutMesh tube = TestMeshes.Tube();
        float volume = MeshCutter.Volume(tube);
        // Поле точно 0 на кольце вершин (z = h·k / rings вычисляется так же, как в меше).
        var n = Vector3.UnitZ;
        var o = new Vector3(0, 0, 2f * 5 / 12);
        CutResult r = CutByPlane(tube, n, o);
        AssertSplit(tube, r, n, volume);
        Assert.True(r.CapTrianglesA > 0);

        // Наклонная плоскость через центр торца и вершину кольца.
        n = Vector3.Normalize(new Vector3(0.3f, 0, 1));
        r = CutByPlane(tube, n, new Vector3(0, 0, 1));
        AssertSplit(tube, r, n, volume);
    }

    [Fact]
    public void TwoDisjointTubesGiveTwoLoops()
    {
        var b = new TestMeshes.Builder();
        TestMeshes.AddTube(b, new Vector3(-1, 0, 0), 0.5f, 2f, 24, 12, true);
        TestMeshes.AddTube(b, new Vector3(1, 0, 0), 0.4f, 2f, 20, 10, true);
        CutMesh m = b.Build();
        float volume = MeshCutter.Volume(m);
        var n = Vector3.Normalize(new Vector3(0.1f, 0.05f, 1));
        CutResult r = CutByPlane(m, n, new Vector3(0, 0, 1.05f), 3f);
        AssertSplit(m, r, n, volume);
        Assert.Equal(2, r.Loops);
        Assert.Equal(0, r.Holes);
    }

    [Fact]
    public void ThickTubeCapHasHole()
    {
        const float rOut = 0.5f, rIn = 0.3f;
        const int radial = 24;
        CutMesh m = TestMeshes.ThickTube(rOut, rIn, 2f, radial, 12);
        float volume = MeshCutter.Volume(m);
        var n = Vector3.UnitZ;
        CutResult r = CutByPlane(m, n, new Vector3(0, 0, 0.77f));
        AssertSplit(m, r, n, volume);
        Assert.Equal(2, r.Loops);
        Assert.Equal(1, r.Holes);

        // Площадь крышки — кольцо, а не круг: дыра не закрыта.
        float expected = 0.5f * radial * MathF.Sin(2 * MathF.PI / radial) * (rOut * rOut - rIn * rIn);
        Assert.Equal(expected, CapArea(r.A, n), 3);

        var rng = new Random(77);
        for (int trial = 0; trial < 50; trial++)
        {
            n = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, 2f));
            r = CutByPlane(m, n, new Vector3(0, 0, 0.5f + rng.NextSingle()));
            AssertSplit(m, r, n, volume);
            Assert.Equal(1, r.Holes);
        }
    }

    private static float CapArea(CutMesh m, Vector3 n)
    {
        float s = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            CutVertex a = m.Vertices[m.Indices[t]], b = m.Vertices[m.Indices[t + 1]], c = m.Vertices[m.Indices[t + 2]];
            if (IsCap(a) && IsCap(b) && IsCap(c))
                s += 0.5f * Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), n);
        }
        return s;
    }

    [Fact]
    public void OpenMeshIsHandled()
    {
        CutMesh m = TestMeshes.Tube(capped: false);
        int open = MeshCutter.OpenEdges(m);
        Assert.Equal(2 * 24, open);

        // Поперёк: край открытой трубы сохраняется, крышка закрывает разрез.
        CutResult r = CutByPlane(m, Vector3.UnitZ, new Vector3(0, 0, 1.1f));
        Assert.Equal(24, MeshCutter.OpenEdges(r.A));
        Assert.Equal(24, MeshCutter.OpenEdges(r.B));
        AssertCapsFace(r.A, Vector3.UnitZ);

        // Вдоль оси: открытые цепочки, замыкаются напрямую.
        var n = Vector3.UnitX;
        r = CutByPlane(m, n, new Vector3(0.1f, 0, 0));
        AssertValid(r.A);
        AssertValid(r.B);
        Assert.True(r.Loops >= 2);
        Assert.True(r.CapTrianglesA > 0);
        Assert.True(r.A.Indices.Length > 0 && r.B.Indices.Length > 0);
    }

    [Fact]
    public void RepeatedCutsLikeGibsStayClosed()
    {
        // Цепочка из 6 разрезов через центр куска со случайным выбором стороны — как осколки Вороного.
        var rng = new Random(42);
        int cuts = 0, fellBack = 0;
        CutMesh[] bases = [TestMeshes.Tube(), TestMeshes.ThickTube()];
        for (int trial = 0; trial < 300; trial++)
        {
            CutMesh piece = bases[trial % 2];
            for (int step = 0; step < 6; step++)
            {
                float volume = MeshCutter.Volume(piece);
                if (volume < 1e-5f)
                    break;
                Vector3 c = Vector3.Zero;
                foreach (CutVertex v in piece.Vertices)
                    c += v.Position;
                c /= piece.Vertices.Length;
                Vector3 n = RandomUnit(rng);
                var o = c + 0.05f * new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f);
                CutResult r = CutByPlane(piece, n, o);
                cuts++;
                fellBack += r.FellBack ? 1 : 0;
                Assert.Equal(0, MeshCutter.OpenEdges(r.A));
                Assert.Equal(0, MeshCutter.OpenEdges(r.B));
                float va = MeshCutter.Volume(r.A), vb = MeshCutter.Volume(r.B);
                Assert.True(va >= -1e-6f && vb >= -1e-6f, $"{va} {vb}");
                Assert.True(MathF.Abs(va + vb - volume) <= 0.01f * volume, $"{va} + {vb} != {volume}");
                piece = rng.Next(2) == 0 ? r.A : r.B;
            }
        }
        output.WriteLine($"разрезов {cuts}, веером {fellBack}");
        Assert.True(fellBack * 100 <= cuts, $"веером {fellBack} из {cuts}");
    }

    [Fact]
    public void RecutKeepsPartsClosed()
    {
        CutMesh tube = TestMeshes.Tube();
        float volume = MeshCutter.Volume(tube);
        var rng = new Random(99);
        for (int trial = 0; trial < 60; trial++)
        {
            Vector3 n1 = RandomUnit(rng);
            CutResult r1 = CutByPlane(tube, n1, new Vector3(0, 0, 1));
            AssertSplit(tube, r1, n1, volume);
            float va = MeshCutter.Volume(r1.A);

            Vector3 n2 = RandomUnit(rng);
            var o2 = new Vector3(rng.NextSingle() * 0.6f - 0.3f, rng.NextSingle() * 0.6f - 0.3f, 0.5f + rng.NextSingle());
            CutResult r2 = CutByPlane(r1.A, n2, o2);
            AssertSplit(r1.A, r2, n2, va);

            // Третий разрез куска — как при осколках Вороного.
            float vaa = MeshCutter.Volume(r2.B);
            if (vaa > 1e-4f)
            {
                Vector3 n3 = RandomUnit(rng);
                CutResult r3 = CutByPlane(r2.B, n3, o2 + 0.1f * n2);
                AssertSplit(r2.B, r3, n3, vaa);
            }
        }
    }

    [Fact]
    public void SkinOfNewVerticesIsValid()
    {
        CutMesh tube = TestMeshes.Tube();
        var rng = new Random(5);
        for (int trial = 0; trial < 30; trial++)
        {
            Vector3 n = RandomUnit(rng);
            CutResult r = CutByPlane(tube, n, new Vector3(0, 0, 0.3f + 1.4f * rng.NextSingle()));
            AssertSkin(r.A, JointA, n);
            AssertSkin(r.B, JointB, -n);
        }
    }

    private static void AssertSkin(CutMesh m, byte capJoint, Vector3 capNormal)
    {
        foreach (CutVertex v in m.Vertices)
        {
            for (int k = 0; k < 4; k++)
                Assert.True((v.Joints >> (8 * k) & 0xFF) < 4);
            Assert.True(v.Weights.X >= 0 && v.Weights.Y >= 0 && v.Weights.Z >= 0);
            Assert.True(v.Weights.X + v.Weights.Y + v.Weights.Z <= 1 + 1e-5f);
            if (IsCap(v))
            {
                Assert.Equal(capJoint, (byte)(v.Joints & 0xFF));
                Assert.Equal(Vector3.UnitX, v.Weights);
                Assert.Equal(capNormal, v.Normal);
            }
        }
    }

    [Fact]
    public void EdgeSkinIsInterpolated()
    {
        // Кольца ниже z = 1 — сустав 0, с z = 1 — сустав 1; плоскость z = 0.9 между кольцами 5/6 и 1.
        static (uint, Vector3) Step(float z) => (z < 1f ? 0u : 1u, Vector3.UnitX);
        CutMesh tube = TestMeshes.Tube(skin: Step);
        CutResult r = CutByPlane(tube, Vector3.UnitZ, new Vector3(0, 0, 0.9f));
        float t = (0.9f - 2f * 5 / 12) / (2f / 12);
        int checkedCount = 0;
        foreach (CutVertex v in r.A.Vertices)
        {
            if (IsCap(v) || MathF.Abs(v.Position.Z - 0.9f) > 1e-5f)
                continue;
            float w0 = 0, w1 = 0;
            float[] w = [v.Weights.X, v.Weights.Y, v.Weights.Z, 1 - v.Weights.X - v.Weights.Y - v.Weights.Z];
            for (int k = 0; k < 4; k++)
            {
                uint j = v.Joints >> (8 * k) & 0xFF;
                if (j == 0)
                    w0 += w[k];
                else if (j == 1)
                    w1 += w[k];
            }
            Assert.Equal(1 - t, w0, 4);
            Assert.Equal(t, w1, 4);
            checkedCount++;
        }
        Assert.True(checkedCount >= 24);
    }

    [Fact]
    public void CapUvMarksCapsAndGivesRadialDistance()
    {
        CutMesh tube = TestMeshes.Tube();
        var rng = new Random(11);
        for (int trial = 0; trial < 30; trial++)
        {
            Vector3 n = RandomUnit(rng);
            var o = new Vector3(0, 0, 0.5f + rng.NextSingle());
            const float radius = 0.5f;
            CutResult r = CutByPlane(tube, n, o, radius);
            int caps = 0;
            foreach (CutMesh m in new[] { r.A, r.B })
                foreach (CutVertex v in m.Vertices)
                {
                    if (!IsCap(v))
                    {
                        Assert.True(v.Uv.X <= 4);
                        continue;
                    }
                    caps++;
                    float dx = v.Uv.X - 8, dy = v.Uv.Y;
                    float expected = (v.Position - o).Length() / radius;
                    Assert.Equal(expected, MathF.Sqrt(dx * dx + dy * dy), 3);
                }
            if (r.CapTrianglesA > 0)
                Assert.True(caps > 0);
        }
    }

    [Fact]
    public void CutIsDeterministicAndInputUntouched()
    {
        CutMesh tube = TestMeshes.Tube();
        CutVertex[] v0 = (CutVertex[])tube.Vertices.Clone();
        uint[] i0 = (uint[])tube.Indices.Clone();
        var n = Vector3.Normalize(new Vector3(0.3f, -0.2f, 1));
        CutResult r1 = CutByPlane(tube, n, new Vector3(0.05f, 0, 1));
        CutResult r2 = CutByPlane(tube, n, new Vector3(0.05f, 0, 1));
        Assert.True(Bytes(tube.Vertices).SequenceEqual(Bytes(v0)));
        Assert.Equal(i0, tube.Indices);
        Assert.True(Bytes(r1.A.Vertices).SequenceEqual(Bytes(r2.A.Vertices)));
        Assert.True(Bytes(r1.B.Vertices).SequenceEqual(Bytes(r2.B.Vertices)));
        Assert.Equal(r1.A.Indices, r2.A.Indices);
        Assert.Equal(r1.B.Indices, r2.B.Indices);
        Assert.Equal(48, Marshal.SizeOf<CutVertex>());
    }

    private static ReadOnlySpan<byte> Bytes(CutVertex[] v) => MemoryMarshal.AsBytes(v.AsSpan());

    [Fact]
    public void PerfSmokeDenseTube()
    {
        CutMesh m = TestMeshes.Tube(0.1f, 1.8f, 300, 150);
        int tris = m.Indices.Length / 3;
        var f = new float[m.Vertices.Length];
        var posed = new Vector3[m.Vertices.Length];
        var n = Vector3.Normalize(new Vector3(0.2f, 0.1f, 1));
        var o = new Vector3(0, 0, 0.93f);
        for (int i = 0; i < f.Length; i++)
        {
            posed[i] = m.Vertices[i].Position;
            f[i] = Vector3.Dot(posed[i] - o, n);
        }
        CapSpec spec = Spec(n, o, 0.1f);

        // Первый вызов (с JIT), затем серия из 20 и серия после прогрева (многоуровневый JIT успел оптимизировать).
        long c0 = Stopwatch.GetTimestamp();
        CutResult r = MeshCutter.Cut(m, f, posed, spec);
        double cold = Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
        double early = Median(m, f, posed, spec, 20);
        for (int k = 0; k < 300; k++)
            MeshCutter.Cut(m, f, posed, spec);
        Thread.Sleep(500);
        double warm = Median(m, f, posed, spec, 20);
        output.WriteLine($"verts {m.Vertices.Length}, tris {tris}, cap tris {r.CapTrianglesA}: " +
            $"first {cold:F3} ms, next 20 median {early:F3} ms, warm median {warm:F3} ms");
        Assert.Equal(0, MeshCutter.OpenEdges(r.A));
        Assert.Equal(0, MeshCutter.OpenEdges(r.B));
        Assert.True(early < 50);
    }

    private static double Median(CutMesh m, float[] f, Vector3[] posed, CapSpec spec, int count)
    {
        var times = new double[count];
        for (int k = 0; k < count; k++)
        {
            long t0 = Stopwatch.GetTimestamp();
            MeshCutter.Cut(m, f, posed, spec);
            times[k] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        Array.Sort(times);
        return times[count / 2];
    }
}
