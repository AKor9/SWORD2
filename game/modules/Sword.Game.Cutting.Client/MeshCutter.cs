using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Game.Cutting;

/// <summary>Разрез скиннинговой меши скалярным полем на две замкнутые части с крышками.</summary>
public static class MeshCutter
{
    // Класс треугольника: целиком в A, целиком в B, пересечён с одиночной вершиной в A или в B.
    private const byte WholeA = 0, WholeB = 1, LoneA = 2, LoneB = 3;

    // Разрез — редкое событие: многоуровневый JIT не успевает поднять его код, а на tier-0 он в 2–5 раз
    // медленнее; PGO ему не помогает. Поэтому методы пути разреза — AggressiveOptimization.

    /// <summary>
    /// Режет меша по полю: вершины с field &lt; 0 — сторона A, остальные — B. Пересечённые треугольники
    /// отсекаются, по контурам разреза строятся крышки. Входная меша не меняется.
    /// </summary>
    /// <param name="mesh">Исходная меша (позиции и нормали в позе привязки).</param>
    /// <param name="field">Значение поля на вершину.</param>
    /// <param name="posed">Позиция вершины в мировой позе; по ней строятся контуры и крышки.</param>
    /// <param name="cap">Параметры крышки.</param>
    public static CutResult Cut(CutMesh mesh, ReadOnlySpan<float> field, ReadOnlySpan<Vector3> posed, in CapSpec cap)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        CutVertex[] verts = mesh.Vertices;
        uint[] idx = mesh.Indices;
        if (field.Length < verts.Length || posed.Length < verts.Length)
            throw new ArgumentException("field и posed должны покрывать все вершины меши.");

        int triCount = idx.Length / 3;
        var cls = new byte[triCount];
        var remap = new int[verts.Length];
        var crossing = new Buf<int>(64);
        Classify(idx, field, cls, remap, crossing, out int wholeA, out int wholeB);
        Remap(field, remap, out int countA, out int countB);

        var state = new CutState(verts, field, posed, cap);
        for (int k = 0; k < crossing.Count; k++)
            state.Clip(idx, crossing.Items[k]);
        var result = new CutResult();
        state.BuildCaps(result);

        int varCount = state.Variants.Count;
        int capVertCount = state.CapNodes.Count;
        int capTriCount = state.CapTris.Count / 3;
        var a = new CutMesh
        {
            Vertices = new CutVertex[countA + varCount + capVertCount],
            Indices = new uint[(wholeA + capTriCount) * 3 + state.PiecesA.Count],
        };
        var b = new CutMesh
        {
            Vertices = new CutVertex[countB + varCount + capVertCount],
            Indices = new uint[(wholeB + capTriCount) * 3 + state.PiecesB.Count],
        };

        CopyVertices(verts, field, remap, a.Vertices, b.Vertices);
        Array.Copy(state.Variants.Items, 0, a.Vertices, countA, varCount);
        Array.Copy(state.Variants.Items, 0, b.Vertices, countB, varCount);
        state.WriteCapVertices(a.Vertices, countA + varCount, b.Vertices, countB + varCount);

        int tailA = WriteTriangles(idx, cls, remap, state.PiecesA.Items, state.PiecesB.Items,
            (uint)countA, (uint)countB, a.Indices, b.Indices, out int tailB);
        state.WriteCapTriangles(a.Indices, tailA, (uint)(countA + varCount), b.Indices, tailB, (uint)(countB + varCount));

        result.A = a;
        result.B = b;
        result.CapTrianglesA = capTriCount;
        result.CapTrianglesB = capTriCount;
        return result;
    }

    /// <summary>Число открытых рёбер после сварки вершин по точной позиции привязки; 0 — меша замкнута.</summary>
    public static int OpenEdges(CutMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        CutVertex[] verts = mesh.Vertices;
        var weldOf = new Dictionary<PosKey, int>(verts.Length);
        var weld = new int[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            ref int w = ref CollectionsMarshal.GetValueRefOrAddDefault(weldOf, PosKey.Of(verts[i].Position), out bool exists);
            if (!exists)
                w = weldOf.Count - 1;
            weld[i] = w;
        }

        // Ориентированное ребро u→v даёт +1 ключу (min, max) при u < v и −1 иначе; у замкнутой меши все суммы нулевые.
        var edges = new Dictionary<long, int>();
        uint[] idx = mesh.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int w0 = weld[idx[t]], w1 = weld[idx[t + 1]], w2 = weld[idx[t + 2]];
            CountEdge(edges, w0, w1);
            CountEdge(edges, w1, w2);
            CountEdge(edges, w2, w0);
        }

        int open = 0;
        foreach (int c in edges.Values)
            open += Math.Abs(c);
        return open;
    }

    /// <summary>Знаковый объём по позициям привязки (теорема о дивергенции); у наружу ориентированной замкнутой меши &gt; 0.</summary>
    public static float Volume(CutMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        CutVertex[] v = mesh.Vertices;
        uint[] idx = mesh.Indices;
        double sum = 0;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 p0 = v[idx[t]].Position, p1 = v[idx[t + 1]].Position, p2 = v[idx[t + 2]].Position;
            sum += Vector3.Dot(p0, Vector3.Cross(p1, p2));
        }
        return (float)(sum / 6.0);
    }

    private static void CountEdge(Dictionary<long, int> edges, int u, int v)
    {
        if (u == v)
            return;
        long key = u < v ? Pack(u, v) : Pack(v, u);
        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(edges, key, out _);
        c += u < v ? 1 : -1;
    }

    private static long Pack(int lo, int hi) => ((long)lo << 32) | (uint)hi;

    // Классы треугольников, отметка используемых вершин (remap = 1) и список пересечённых треугольников.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Classify(uint[] idx, ReadOnlySpan<float> field, byte[] cls, int[] remap, Buf<int> crossing,
        out int wholeA, out int wholeB)
    {
        int na = 0, nb = 0;
        for (int t = 0; t < cls.Length; t++)
        {
            uint i0 = idx[t * 3], i1 = idx[t * 3 + 1], i2 = idx[t * 3 + 2];
            remap[i0] = 1;
            remap[i1] = 1;
            remap[i2] = 1;
            int m = (field[(int)i0] < 0 ? 1 : 0) | (field[(int)i1] < 0 ? 2 : 0) | (field[(int)i2] < 0 ? 4 : 0);
            if (m == 7)
            {
                cls[t] = WholeA;
                na++;
            }
            else if (m == 0)
            {
                cls[t] = WholeB;
                nb++;
            }
            else
            {
                cls[t] = m == 1 || m == 2 || m == 4 ? LoneA : LoneB;
                crossing.Add(t);
            }
        }
        wholeA = na;
        wholeB = nb;
    }

    // Новый номер используемой вершины внутри своей стороны; порядок вершин сохраняется.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Remap(ReadOnlySpan<float> field, int[] remap, out int countA, out int countB)
    {
        int na = 0, nb = 0;
        for (int i = 0; i < remap.Length; i++)
        {
            if (remap[i] == 0)
                remap[i] = -1;
            else
                remap[i] = field[i] < 0 ? na++ : nb++;
        }
        countA = na;
        countB = nb;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CopyVertices(CutVertex[] src, ReadOnlySpan<float> field, int[] remap, CutVertex[] dstA, CutVertex[] dstB)
    {
        for (int i = 0; i < src.Length; i++)
        {
            int r = remap[i];
            if (r < 0)
                continue;
            if (field[i] < 0)
                dstA[r] = src[i];
            else
                dstB[r] = src[i];
        }
    }

    // Треугольники в исходном порядке; куски пересечённых — на месте исходного треугольника.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int WriteTriangles(uint[] idx, byte[] cls, int[] remap, int[] piecesA, int[] piecesB,
        uint varBaseA, uint varBaseB, uint[] outA, uint[] outB, out int tailB)
    {
        int oa = 0, ob = 0, pa = 0, pb = 0;
        for (int t = 0; t < cls.Length; t++)
        {
            int i = t * 3;
            switch (cls[t])
            {
                case WholeA:
                    outA[oa] = (uint)remap[idx[i]];
                    outA[oa + 1] = (uint)remap[idx[i + 1]];
                    outA[oa + 2] = (uint)remap[idx[i + 2]];
                    oa += 3;
                    break;
                case WholeB:
                    outB[ob] = (uint)remap[idx[i]];
                    outB[ob + 1] = (uint)remap[idx[i + 1]];
                    outB[ob + 2] = (uint)remap[idx[i + 2]];
                    ob += 3;
                    break;
                case LoneA:
                    CopyPieces(piecesA, ref pa, 3, remap, varBaseA, outA, ref oa);
                    CopyPieces(piecesB, ref pb, 6, remap, varBaseB, outB, ref ob);
                    break;
                default:
                    CopyPieces(piecesA, ref pa, 6, remap, varBaseA, outA, ref oa);
                    CopyPieces(piecesB, ref pb, 3, remap, varBaseB, outB, ref ob);
                    break;
            }
        }
        tailB = ob;
        return oa;
    }

    // Код куска: ≥ 0 — исходная вершина, иначе ~номер новой вершины на ребре.
    private static void CopyPieces(int[] pieces, ref int p, int n, int[] remap, uint varBase, uint[] dst, ref int o)
    {
        for (int k = 0; k < n; k++)
        {
            int e = pieces[p + k];
            dst[o + k] = e >= 0 ? (uint)remap[e] : varBase + (uint)~e;
        }
        p += n;
        o += n;
    }

    /// <summary>Ключ сварки: биты позиции, −0 приравнен к +0.</summary>
    private readonly record struct PosKey(int X, int Y, int Z)
    {
        public static PosKey Of(Vector3 p) => new(Bits(p.X), Bits(p.Y), Bits(p.Z));

        private static int Bits(float f) => f == 0f ? 0 : BitConverter.SingleToInt32Bits(f);
    }

    /// <summary>Растущий массив без лишних проверок.</summary>
    internal sealed class Buf<T>(int capacity)
    {
        public T[] Items = new T[Math.Max(capacity, 4)];
        public int Count;

        public void Add(T value)
        {
            if (Count == Items.Length)
                Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = value;
        }
    }

    // Состояние одного разреза: точки на рёбрах, контуры, крышки.
    private ref struct CutState
    {
        private readonly CutVertex[] _v;
        private readonly ReadOnlySpan<float> _f;
        private readonly ReadOnlySpan<Vector3> _posed;
        private readonly CapSpec _cap;

        // Сварка вершин пересечённых треугольников по точной позиции: номер сварки и её представитель.
        private readonly Dictionary<int, int> _weldOfVertex = new();
        private readonly Dictionary<PosKey, int> _weldOf = new();
        private readonly Buf<int> _weldRep = new(64);

        // Узел контура — точка на сваренном ребре; вариант — вершина на конкретном ребре со своими атрибутами.
        private readonly Dictionary<long, int> _nodeOf = new();
        private readonly Dictionary<long, int> _variantOf = new();
        private readonly Buf<Vector3> _nodePos = new(64), _nodePosed = new(64);
        private readonly Buf<float> _nodeT = new(64);
        private readonly Buf<int> _variantNode = new(64);
        private readonly Buf<int> _segFrom = new(64), _segTo = new(64);
        private int _windingVote;

        public readonly Buf<CutVertex> Variants = new(64);
        public readonly Buf<int> PiecesA = new(192), PiecesB = new(192);
        public readonly Buf<int> CapTris = new(192);
        public readonly Buf<int> CapNodes = new(64);
        private Vector2[] _capUv = [];

        public CutState(CutVertex[] v, ReadOnlySpan<float> f, ReadOnlySpan<Vector3> posed, in CapSpec cap)
        {
            _v = v;
            _f = f;
            _posed = posed;
            _cap = cap;
        }

        // Отсекает пересечённый треугольник: одиночная вершина даёт 1 треугольник, другая сторона — 2.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Clip(uint[] idx, int tri)
        {
            int i0 = (int)idx[tri * 3], i1 = (int)idx[tri * 3 + 1], i2 = (int)idx[tri * 3 + 2];
            int m = (_f[i0] < 0 ? 1 : 0) | (_f[i1] < 0 ? 2 : 0) | (_f[i2] < 0 ? 4 : 0);
            bool loneA = m == 1 || m == 2 || m == 4;
            int loneBit = loneA ? m : ~m & 7;
            int l, p, q;
            if (loneBit == 1)
                (l, p, q) = (i0, i1, i2);
            else if (loneBit == 2)
                (l, p, q) = (i1, i2, i0);
            else
                (l, p, q) = (i2, i0, i1);

            int e1 = EdgeVertex(l, p), e2 = EdgeVertex(l, q);
            Buf<int> lone = loneA ? PiecesA : PiecesB, other = loneA ? PiecesB : PiecesA;
            lone.Add(l);
            lone.Add(~e1);
            lone.Add(~e2);
            other.Add(~e1);
            other.Add(p);
            other.Add(q);
            other.Add(~e1);
            other.Add(q);
            other.Add(~e2);

            // Отрезок контура в направлении обхода крышки A (обратном краю части A).
            int n1 = _variantNode.Items[e1], n2 = _variantNode.Items[e2];
            if (n1 != n2)
            {
                _segFrom.Add(loneA ? n2 : n1);
                _segTo.Add(loneA ? n1 : n2);
            }

            // Голос за соглашение об обходе: знак dot(cross(e1, e2), нормаль).
            ref readonly CutVertex v0 = ref _v[i0];
            ref readonly CutVertex v1 = ref _v[i1];
            ref readonly CutVertex v2 = ref _v[i2];
            float d = Vector3.Dot(Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position),
                v0.Normal + v1.Normal + v2.Normal);
            _windingVote += d > 0 ? 1 : d < 0 ? -1 : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int Weld(int v)
        {
            if (_weldOfVertex.TryGetValue(v, out int w))
                return w;
            ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_weldOf, PosKey.Of(_v[v].Position), out bool exists);
            if (!exists)
            {
                slot = _weldRep.Count;
                _weldRep.Add(v);
            }
            w = slot;
            _weldOfVertex.Add(v, w);
            return w;
        }

        // Вершина на ребре (a, b): позиция общая для сваренного ребра (бит в бит), атрибуты — свои у каждого ребра.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int EdgeVertex(int a, int b)
        {
            long key = a < b ? Pack(a, b) : Pack(b, a);
            if (_variantOf.TryGetValue(key, out int k))
                return k;

            int wa = Weld(a), wb = Weld(b);
            if (wb < wa || (wa == wb && b < a))
            {
                (a, b) = (b, a);
                (wa, wb) = (wb, wa);
            }
            ref int nodeSlot = ref CollectionsMarshal.GetValueRefOrAddDefault(_nodeOf, Pack(wa, wb), out bool exists);
            if (!exists)
                nodeSlot = AddNode(_weldRep.Items[wa], _weldRep.Items[wb]);
            int node = nodeSlot;
            float t = _nodeT.Items[node];

            ref readonly CutVertex va = ref _v[a];
            ref readonly CutVertex vb = ref _v[b];
            CutVertex r;
            r.Position = _nodePos.Items[node];
            Vector3 n = va.Normal + (vb.Normal - va.Normal) * t;
            float len = n.Length();
            r.Normal = len > 1e-20f ? n / len : va.Normal;
            r.Uv = va.Uv + (vb.Uv - va.Uv) * t;
            BlendSkin(va, vb, t, out r.Joints, out r.Weights);

            k = Variants.Count;
            Variants.Add(r);
            _variantNode.Add(node);
            _variantOf.Add(key, k);
            return k;
        }

        // Узел на сваренном ребре: t = f(a) / (f(a) − f(b)) по представителям сварки, порядок — по номеру сварки.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int AddNode(int a, int b)
        {
            float fa = _f[a], fb = _f[b];
            float d = fa - fb;
            float t = d != 0 ? fa / d : 0.5f;
            if (!(t >= 0))
                t = 0;
            else if (t > 1)
                t = 1;
            Vector3 pa = _v[a].Position, qa = _posed[a];
            _nodePos.Add(pa + (_v[b].Position - pa) * t);
            _nodePosed.Add(qa + (_posed[b] - qa) * t);
            _nodeT.Add(t);
            return _nodePos.Count - 1;
        }

        // Контуры из отрезков, классификация внешних контуров и дыр, триангуляция крышек.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void BuildCaps(CutResult result)
        {
            int nodeCount = _nodePos.Count, segCount = _segFrom.Count;
            if (segCount == 0)
                return;

            var loops = new Loops(nodeCount, segCount);
            loops.Link(_segFrom.Items, _segTo.Items, segCount);
            loops.Extract(_segFrom.Items, _segTo.Items, segCount);
            result.Loops = loops.Count;

            // Двумерные координаты узлов в базисе (u, v) плоскости крышки, u × v = Normal.
            Vector3 normal = _cap.Normal.LengthSquared() > 0 ? Vector3.Normalize(_cap.Normal) : Vector3.UnitZ;
            Vector3 helper = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 u = Vector3.Normalize(Vector3.Cross(helper, normal));
            Vector3 v = Vector3.Cross(normal, u);
            var x = new double[nodeCount];
            var y = new double[nodeCount];
            float invR = _cap.Radius > 1e-12f ? 1f / _cap.Radius : 1f;
            _capUv = new Vector2[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                Vector3 d = _nodePosed.Items[i] - _cap.Origin;
                float px = Vector3.Dot(d, u), py = Vector3.Dot(d, v);
                x[i] = px;
                y[i] = py;
                _capUv[i] = new Vector2(8f + px * invR, py * invR);
            }

            // Внешний контур крышки A обходится против часовой стрелки при взгляде с +Normal, если меша
            // ориентирована так (знак голоса); без голосов — по знаку наибольшего контура.
            var area = new double[loops.Count];
            int largest = -1;
            for (int l = 0; l < loops.Count; l++)
            {
                area[l] = SignedArea(loops.Points, loops.Start[l], loops.Length[l], x, y);
                if (loops.Length[l] >= 3 && (largest < 0 || Math.Abs(area[l]) > Math.Abs(area[largest])))
                    largest = l;
            }
            double sign = _windingVote > 0 ? 1 : _windingVote < 0 ? -1 : largest >= 0 && area[largest] < 0 ? -1 : 1;

            // Дыра принадлежит наименьшему внешнему контуру, который её содержит.
            var owner = new int[loops.Count];
            for (int l = 0; l < loops.Count; l++)
            {
                owner[l] = -1;
                if (loops.Length[l] < 3 || area[l] * sign >= 0)
                    continue;
                int p0 = loops.Points[loops.Start[l]];
                double best = double.MaxValue;
                for (int o = 0; o < loops.Count; o++)
                {
                    if (loops.Length[o] < 3 || area[o] * sign < 0 || Math.Abs(area[o]) >= best)
                        continue;
                    if (Inside(loops.Points, loops.Start[o], loops.Length[o], x, y, x[p0], y[p0]))
                    {
                        best = Math.Abs(area[o]);
                        owner[l] = o;
                    }
                }
            }

            var tri = new CapTriangulator(loops.Points, loops.Start, loops.Length, x, y);
            var holes = new Buf<int>(8);
            for (int l = 0; l < loops.Count; l++)
            {
                if (loops.Length[l] < 3)
                    continue;
                bool outer = area[l] * sign >= 0;
                if (!outer && owner[l] >= 0)
                    continue;
                holes.Count = 0;
                if (outer)
                    for (int h = 0; h < loops.Count; h++)
                        if (owner[h] == l)
                            holes.Add(h);
                tri.Triangulate(l, holes, area[l] < 0 ? -1 : 1, CapTris);
            }
            result.Holes = tri.Bridged;
            result.FellBack = tri.FellBack;

            // Вершины крышки — в порядке первого появления в треугольниках.
            var capVert = new int[nodeCount];
            Array.Fill(capVert, -1);
            for (int i = 0; i < CapTris.Count; i++)
            {
                int node = CapTris.Items[i];
                if (capVert[node] < 0)
                {
                    capVert[node] = CapNodes.Count;
                    CapNodes.Add(node);
                }
                CapTris.Items[i] = capVert[node];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public readonly void WriteCapVertices(CutVertex[] a, int baseA, CutVertex[] b, int baseB)
        {
            uint ja = _cap.JointA * 0x01010101u, jb = _cap.JointB * 0x01010101u;
            for (int i = 0; i < CapNodes.Count; i++)
            {
                int node = CapNodes.Items[i];
                CutVertex c;
                c.Position = _nodePos.Items[node];
                c.Uv = _capUv[node];
                c.Weights = Vector3.UnitX;
                c.Normal = _cap.BindNormalA;
                c.Joints = ja;
                a[baseA + i] = c;
                c.Normal = _cap.BindNormalB;
                c.Joints = jb;
                b[baseB + i] = c;
            }
        }

        // Крышка A — в порядке триангуляции, крышка B — с обратным обходом.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public readonly void WriteCapTriangles(uint[] a, int oa, uint baseA, uint[] b, int ob, uint baseB)
        {
            int[] t = CapTris.Items;
            for (int i = 0; i + 2 < CapTris.Count; i += 3)
            {
                a[oa++] = baseA + (uint)t[i];
                a[oa++] = baseA + (uint)t[i + 1];
                a[oa++] = baseA + (uint)t[i + 2];
                b[ob++] = baseB + (uint)t[i];
                b[ob++] = baseB + (uint)t[i + 2];
                b[ob++] = baseB + (uint)t[i + 1];
            }
        }
    }

    // Смесь двух наборов влияний с весами (1 − t) и t: до 8 суставов, 4 наибольших, нормировка.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlendSkin(in CutVertex a, in CutVertex b, float t, out uint joints, out Vector3 weights)
    {
        Span<int> j = stackalloc int[8];
        Span<float> w = stackalloc float[8];
        int n = AddInfluences(a, 1f - t, j, w, 0);
        n = AddInfluences(b, t, j, w, n);

        int keep = Math.Min(n, 4);
        for (int i = 0; i < keep; i++)
        {
            int best = i;
            for (int m = i + 1; m < n; m++)
                if (w[m] > w[best] || (w[m] == w[best] && j[m] < j[best]))
                    best = m;
            (j[i], j[best]) = (j[best], j[i]);
            (w[i], w[best]) = (w[best], w[i]);
        }
        float sum = 0;
        for (int i = 0; i < keep; i++)
            sum += w[i];
        if (keep == 0 || !(sum > 0))
        {
            joints = (a.Joints & 0xFF) * 0x01010101u;
            weights = Vector3.UnitX;
            return;
        }

        // Пустые слоты — тот же сустав с нулевым весом, чтобы индекс был заведомо допустимым.
        float inv = 1f / sum;
        uint j0 = (uint)j[0];
        uint j1 = keep > 1 ? (uint)j[1] : j0, j2 = keep > 2 ? (uint)j[2] : j0, j3 = keep > 3 ? (uint)j[3] : j0;
        joints = j0 | j1 << 8 | j2 << 16 | j3 << 24;
        weights = new Vector3(w[0] * inv, keep > 1 ? w[1] * inv : 0f, keep > 2 ? w[2] * inv : 0f);
    }

    private static int AddInfluences(in CutVertex v, float scale, Span<int> j, Span<float> w, int n)
    {
        if (!(scale > 0))
            return n;
        Vector3 vw = v.Weights;
        float w3 = 1f - vw.X - vw.Y - vw.Z;
        n = AddInfluence((int)(v.Joints & 0xFF), vw.X * scale, j, w, n);
        n = AddInfluence((int)(v.Joints >> 8 & 0xFF), vw.Y * scale, j, w, n);
        n = AddInfluence((int)(v.Joints >> 16 & 0xFF), vw.Z * scale, j, w, n);
        return AddInfluence((int)(v.Joints >> 24), w3 * scale, j, w, n);
    }

    private static int AddInfluence(int joint, float weight, Span<int> j, Span<float> w, int n)
    {
        if (!(weight > 1e-7f))
            return n;
        for (int i = 0; i < n; i++)
        {
            if (j[i] == joint)
            {
                w[i] += weight;
                return n;
            }
        }
        j[n] = joint;
        w[n] = weight;
        return n + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double SignedArea(int[] pts, int start, int len, double[] x, double[] y)
    {
        double s = 0;
        for (int i = 0, k = len - 1; i < len; k = i++)
        {
            int a = pts[start + k], b = pts[start + i];
            s += x[a] * y[b] - x[b] * y[a];
        }
        return s * 0.5;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Inside(int[] pts, int start, int len, double[] x, double[] y, double px, double py)
    {
        bool inside = false;
        for (int i = 0, k = len - 1; i < len; k = i++)
        {
            int a = pts[start + i], b = pts[start + k];
            if ((y[a] > py) != (y[b] > py) && px < (x[b] - x[a]) * (py - y[a]) / (y[b] - y[a]) + x[a])
                inside = !inside;
        }
        return inside;
    }

    // Связывание отрезков в контуры по узлам: сначала открытые цепочки (от узлов без входа), затем замкнутые.
    private sealed class Loops(int nodeCount, int segCount)
    {
        private readonly int[] _head = new int[nodeCount], _tail = new int[nodeCount], _cursor = new int[nodeCount];
        private readonly int[] _in = new int[nodeCount], _out = new int[nodeCount];
        private readonly int[] _next = new int[segCount];
        private readonly bool[] _used = new bool[segCount];
        private readonly Buf<int> _points = new(segCount + 8), _start = new(8), _length = new(8);

        public int Count => _start.Count;
        public int[] Points => _points.Items;
        public int[] Start => _start.Items;
        public int[] Length => _length.Items;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Link(int[] from, int[] to, int count)
        {
            Array.Fill(_head, -1);
            for (int s = 0; s < count; s++)
            {
                int f = from[s];
                _next[s] = -1;
                if (_head[f] < 0)
                    _head[f] = s;
                else
                    _next[_tail[f]] = s;
                _tail[f] = s;
                _out[f]++;
                _in[to[s]]++;
            }
            Array.Copy(_head, _cursor, _head.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Extract(int[] from, int[] to, int count)
        {
            for (int s = 0; s < count; s++)
                if (!_used[s] && _out[from[s]] > _in[from[s]])
                    Walk(s, from, to);
            for (int s = 0; s < count; s++)
                if (!_used[s])
                    Walk(s, from, to);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Walk(int s, int[] from, int[] to)
        {
            int start = from[s], first = _points.Count;
            while (true)
            {
                _used[s] = true;
                _points.Add(from[s]);
                int end = to[s];
                if (end == start)
                    break;
                s = NextUnused(end);
                if (s < 0)
                {
                    // Открытая цепочка: последний узел добавляется, крышка замыкает её напрямую.
                    _points.Add(end);
                    break;
                }
            }
            _start.Add(first);
            _length.Add(_points.Count - first);
        }

        private int NextUnused(int node)
        {
            int s = _cursor[node];
            while (s >= 0 && _used[s])
                s = _next[s];
            _cursor[node] = s;
            return s;
        }
    }
}
