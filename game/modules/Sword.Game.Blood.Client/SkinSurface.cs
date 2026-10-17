using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Кожа персонажа для крови — строится раз при загрузке из <see cref="ISkinMesh"/> (CPU): треугольники по сегментам тела (сегмент —
/// у доминирующего сустава треугольника), позиции и нормали в привязке, сетка поиска ближайшего треугольника на сегмент, сварка вершин по
/// позиции (ε <see cref="WeldEpsilon"/>: у меша скина вершины разрезаны по швам UV и нормалей) и 3D-смежность рёбер сваренной сетки, опорные
/// суставы перевода точки мира в привязку (WR §3.2) и скин вершины на CPU (формула <c>skin.hlsli</c> с заранее перемноженной
/// <c>v · InverseBind[j]</c>). Только чтение после постройки.
/// </summary>
public sealed class SkinSurface
{
    /// <summary>Сварка вершин: одна точка, если координаты ближе, м.</summary>
    public const float WeldEpsilon = 1e-4f;

    /// <summary>Ячейка сетки поиска треугольников сегмента, м.</summary>
    public const float GridCell = 0.03f;

    private const int GridMaxCells = 48;

    private readonly int[] _tri;          // 3·T: вершины меша
    private readonly int[] _triWeld;      // 3·T: сваренные вершины
    private readonly int[] _neighbor;     // 3·T: сосед через ребро e (вершины e → e+1), −1 — края нет
    private readonly sbyte[] _triSegment; // сегмент треугольника, −1 — не плоть или вырожден
    private readonly Vector3[] _faceBind; // нормаль грани в привязке, наружу
    private readonly float[] _faceSign;   // знак: наружная нормаль мира = знак · (W1 − W0) × (W2 − W0)
    private readonly Vector3[] _bindPos, _bindNormal;
    private readonly SkinVertex[] _skin;  // на вершину одна строка кэша: v · InverseBind[jₖ] и вес
    private readonly uint[] _skinJoints;  // суставы вершины по байту
    private readonly int[] _weldOf, _weldStart, _weldVertices;
    private readonly Matrix4x4[] _bind;
    private readonly int[] _refJoint = new int[BodySegs.Count];
    private readonly int[][] _forearmJoints = new int[BodySegs.Count][];
    private readonly SegmentGrid?[] _grids = new SegmentGrid?[BodySegs.Count];
    private readonly int[][] _segmentTriangles = new int[BodySegs.Count][];

    /// <summary>Треугольников меша (номер треугольника — номер тройки индексов меша).</summary>
    public int TriangleCount { get; }

    public int VertexCount { get; }

    /// <summary>Сваренных вершин.</summary>
    public int WeldCount { get; }

    /// <summary>Рёбер без пары после сварки (края меша: горловина, разрезы, которых сварка не закрыла).</summary>
    public int OpenEdges { get; }

    public int JointCount => _bind.Length;

    public SkinSurface(ISkinMesh mesh)
    {
        if (!mesh.Loaded) throw new InvalidOperationException("меш персонажа не загружен");
        var positions = mesh.Positions;
        var normals = mesh.Normals;
        var indices = mesh.Indices;
        VertexCount = positions.Length;
        TriangleCount = indices.Length / 3;
        _bindPos = positions.ToArray();
        _bindNormal = normals.ToArray();
        _tri = new int[TriangleCount * 3];
        for (int i = 0; i < _tri.Length; i++) _tri[i] = (int)indices[i];

        var inverseBind = mesh.InverseBind;
        _bind = new Matrix4x4[inverseBind.Length];
        for (int j = 0; j < _bind.Length; j++)
            if (!Matrix4x4.Invert(inverseBind[j], out _bind[j])) _bind[j] = Matrix4x4.Identity;

        var skinJoint = new int[VertexCount * 4];
        var skinWeight = new float[VertexCount * 4];
        _skin = new SkinVertex[VertexCount];
        _skinJoints = mesh.Joints.ToArray();
        var joints = mesh.Joints;
        var weights = mesh.Weights;
        for (int v = 0; v < VertexCount; v++)
            for (int k = 0; k < 4; k++)
            {
                float w = k switch { 0 => weights[v].X, 1 => weights[v].Y, 2 => weights[v].Z, _ => weights[v].W };
                int j = SkinMath.Joint(joints[v], k);
                bool used = w > 0 && j < inverseBind.Length;
                skinJoint[v * 4 + k] = used ? j : -1;
                skinWeight[v * 4 + k] = used ? w : 0;
                _skin[v][k] = used ? new Vector4(Vector3.Transform(positions[v], inverseBind[j]), w) : default;
            }

        (_weldOf, WeldCount) = Weld(positions);
        (_weldStart, _weldVertices) = Copies(_weldOf, WeldCount);
        _triWeld = new int[_tri.Length];
        for (int i = 0; i < _tri.Length; i++) _triWeld[i] = _weldOf[_tri[i]];

        _triSegment = new sbyte[TriangleCount];
        _faceBind = new Vector3[TriangleCount];
        _faceSign = new float[TriangleCount];
        Span<float> sum = stackalloc float[Math.Max(1, inverseBind.Length)];
        for (int t = 0; t < TriangleCount; t++)
        {
            int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            bool degenerate = n.LengthSquared() < 1e-14f || _triWeld[3 * t] == _triWeld[3 * t + 1] || _triWeld[3 * t + 1] == _triWeld[3 * t + 2]
                || _triWeld[3 * t] == _triWeld[3 * t + 2];
            float sign = Vector3.Dot(n, normals[a] + normals[b] + normals[c]) < 0 ? -1 : 1;
            _faceSign[t] = sign;
            _faceBind[t] = degenerate ? Vector3.Zero : Vector3.Normalize(n) * sign;
            // Доминирующий сустав — наибольший суммарный вес на трёх вершинах (при равенстве — меньший номер).
            sum.Clear();
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 4; k++)
                {
                    int j = skinJoint[_tri[3 * t + i] * 4 + k];
                    if (j >= 0) sum[j] += skinWeight[_tri[3 * t + i] * 4 + k];
                }
            int best = 0;
            for (int j = 1; j < sum.Length; j++) if (sum[j] > sum[best]) best = j;
            int segment = mesh.JointSegment(best);
            _triSegment[t] = degenerate || !BodySegs.IsFlesh(segment) ? (sbyte)-1 : (sbyte)segment;
        }

        _neighbor = new int[_tri.Length];
        OpenEdges = Adjacency(_triWeld, _triSegment, _neighbor);

        var names = mesh.JointNames;
        for (int s = 0; s < BodySegs.Count; s++)
        {
            var list = new List<int>();
            for (int t = 0; t < TriangleCount; t++) if (_triSegment[t] == s) list.Add(t);
            _segmentTriangles[s] = [.. list];
            _grids[s] = list.Count == 0 ? null : new SegmentGrid(this, _segmentTriangles[s]);
            _refJoint[s] = ReferenceJoint(mesh, names, s, _segmentTriangles[s]);
            _forearmJoints[s] = s is BodySegs.FArmL or BodySegs.FArmR ? ForearmJoints(names, s == BodySegs.FArmL ? "_l" : "_r") : [];
        }
    }

    /// <summary>Вершины меша треугольника <paramref name="triangle"/> (<paramref name="corner"/> 0…2).</summary>
    public int Vertex(int triangle, int corner) => _tri[3 * triangle + corner];

    /// <summary>Сваренная вершина угла треугольника.</summary>
    public int WeldVertex(int triangle, int corner) => _triWeld[3 * triangle + corner];

    /// <summary>Сосед через ребро <paramref name="edge"/> (углы edge → edge+1); −1 — края нет.</summary>
    public int Neighbor(int triangle, int edge) => _neighbor[3 * triangle + edge];

    /// <summary>Сегмент треугольника; −1 — не плоть или вырожденный.</summary>
    public int Segment(int triangle) => _triSegment[triangle];

    /// <summary>Сваренная вершина вершины меша.</summary>
    public int WeldOf(int vertex) => _weldOf[vertex];

    /// <summary>Все вершины меша одной сваренной (копии на шве) — штамп крови в каждую.</summary>
    public ReadOnlySpan<int> WeldCopies(int weld) => _weldVertices.AsSpan(_weldStart[weld], _weldStart[weld + 1] - _weldStart[weld]);

    /// <summary>Треугольники сегмента.</summary>
    public ReadOnlySpan<int> SegmentTriangles(int segment) => (uint)segment < BodySegs.Count ? _segmentTriangles[segment] : [];

    /// <summary>Нормаль грани в привязке (наружу).</summary>
    public Vector3 FaceNormalBind(int triangle) => _faceBind[triangle];

    /// <summary>Знак наружной нормали мира к <c>(W1 − W0) × (W2 − W0)</c>.</summary>
    public float FaceSign(int triangle) => _faceSign[triangle];

    public Vector3 BindPosition(int vertex) => _bindPos[vertex];

    /// <summary>Точка треугольника в привязке по барицентрикам.</summary>
    public Vector3 BindPoint(int triangle, Vector3 bary) =>
        bary.X * _bindPos[_tri[3 * triangle]] + bary.Y * _bindPos[_tri[3 * triangle + 1]] + bary.Z * _bindPos[_tri[3 * triangle + 2]];

    /// <summary>Нормаль вершин в точке треугольника, привязка.</summary>
    public Vector3 BindNormal(int triangle, Vector3 bary)
    {
        var n = bary.X * _bindNormal[_tri[3 * triangle]] + bary.Y * _bindNormal[_tri[3 * triangle + 1]] + bary.Z * _bindNormal[_tri[3 * triangle + 2]];
        return n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : _faceBind[triangle];
    }

    /// <summary>Матрица привязки сустава (обратная <c>InverseBind</c>).</summary>
    public Matrix4x4 Bind(int joint) => _bind[joint];

    /// <summary>Вершина в мире по мировым матрицам суставов позы.</summary>
    public Vector3 Skin(int vertex, ReadOnlySpan<Matrix4x4> joints)
    {
        ref readonly var v = ref _skin[vertex];
        uint packed = _skinJoints[vertex];
        var p = v.L0.W * Vector3.Transform(new Vector3(v.L0.X, v.L0.Y, v.L0.Z), joints[(int)(packed & 0xFF)]);
        if (v.L1.W > 0) p += v.L1.W * Vector3.Transform(new Vector3(v.L1.X, v.L1.Y, v.L1.Z), joints[(int)(packed >> 8 & 0xFF)]);
        if (v.L2.W > 0) p += v.L2.W * Vector3.Transform(new Vector3(v.L2.X, v.L2.Y, v.L2.Z), joints[(int)(packed >> 16 & 0xFF)]);
        if (v.L3.W > 0) p += v.L3.W * Vector3.Transform(new Vector3(v.L3.X, v.L3.Y, v.L3.Z), joints[(int)(packed >> 24)]);
        return p;
    }

    /// <summary>Три вершины треугольника в мире.</summary>
    public void SkinTriangle(int triangle, ReadOnlySpan<Matrix4x4> joints, out Vector3 w0, out Vector3 w1, out Vector3 w2)
    {
        w0 = Skin(_tri[3 * triangle], joints);
        w1 = Skin(_tri[3 * triangle + 1], joints);
        w2 = Skin(_tri[3 * triangle + 2], joints);
    }

    /// <summary>Точка треугольника в мире по барицентрикам.</summary>
    public Vector3 SkinPoint(int triangle, Vector3 bary, ReadOnlySpan<Matrix4x4> joints)
    {
        SkinTriangle(triangle, joints, out var w0, out var w1, out var w2);
        return bary.X * w0 + bary.Y * w1 + bary.Z * w2;
    }

    /// <summary>
    /// Опорный сустав сегмента для перевода точки <paramref name="world"/> (мир) в привязку: торс — <c>spine_04</c>, голова — <c>head</c>,
    /// предплечье — ближайший к точке из <c>lowerarm_twist_*</c> и кисти, остальные — ключевой сустав (WR §3.2); −1 — сегмента нет.
    /// </summary>
    public int ReferenceJoint(int segment, Vector3 world, ReadOnlySpan<Matrix4x4> joints)
    {
        if ((uint)segment >= BodySegs.Count) return -1;
        var candidates = _forearmJoints[segment];
        if (candidates.Length == 0) return _refJoint[segment];
        int best = _refJoint[segment];
        float bestD = float.MaxValue;
        foreach (int j in candidates)
        {
            if (j >= joints.Length) continue;
            float d = Vector3.DistanceSquared(joints[j].Translation, world);
            if (d < bestD) (bestD, best) = (d, j);
        }
        return best;
    }

    /// <summary>Перевод мир → привязка через сустав <paramref name="joint"/>: <c>p_bind = p_world · J⁻¹ · Bind[j]</c>.</summary>
    public Matrix4x4 WorldToBind(int joint, ReadOnlySpan<Matrix4x4> joints) =>
        Matrix4x4.Invert(joints[joint], out var inverse) ? inverse * _bind[joint] : Matrix4x4.Identity;

    /// <summary>Ближайший к точке привязки треугольник сегмента: треугольник, барицентрики, расстояние; нет треугольников — −1.</summary>
    public int Nearest(int segment, Vector3 bind, out Vector3 bary, out float distance)
    {
        bary = default;
        distance = float.MaxValue;
        return (uint)segment < BodySegs.Count && _grids[segment] is { } grid ? grid.Nearest(this, bind, out bary, out distance) : -1;
    }

    /// <summary>
    /// Уточнение в позе: среди треугольников сегмента в шаре привязки (<paramref name="bind"/>, <paramref name="radius"/>) — ближайший к точке
    /// мира <paramref name="world"/> по скинированным вершинам (ошибку перевода через один сустав снимает). Нет — −1.
    /// </summary>
    public int NearestSkinned(int segment, Vector3 bind, float radius, Vector3 world, ReadOnlySpan<Matrix4x4> joints, out Vector3 bary,
        out float distance)
    {
        bary = default;
        distance = float.MaxValue;
        return (uint)segment < BodySegs.Count && _grids[segment] is { } grid ? grid.NearestSkinned(this, bind, radius, world, joints, out bary, out distance) : -1;
    }

    /// <summary>
    /// Первое пересечение луча (привязка) с лицевой стороной треугольников сегмента (нормаль против луча): треугольник, барицентрики,
    /// параметр; нет — −1. Перебор всех треугольников сегмента — только для редких событий (проба удара).
    /// </summary>
    public int Raycast(int segment, Vector3 origin, Vector3 dir, out Vector3 bary, out float hit)
    {
        bary = default;
        hit = float.MaxValue;
        int found = -1;
        foreach (int t in SegmentTriangles(segment))
        {
            if (Vector3.Dot(_faceBind[t], dir) >= 0) continue;
            var a = _bindPos[_tri[3 * t]];
            var e1 = _bindPos[_tri[3 * t + 1]] - a;
            var e2 = _bindPos[_tri[3 * t + 2]] - a;
            var p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) continue;
            float inv = 1 / det;
            var s = origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * inv;
            if (v < 0 || u + v > 1) continue;
            float d = Vector3.Dot(e2, q) * inv;
            if (d < 0 || d >= hit) continue;
            hit = d;
            bary = new Vector3(1 - u - v, u, v);
            found = t;
        }
        return found;
    }

    /// <summary>Ближайшая точка треугольника к <paramref name="p"/> (Ericson, RTCD 5.1.5): барицентрики.</summary>
    internal static Vector3 ClosestBary(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return new Vector3(1, 0, 0);
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return new Vector3(0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v = d1 / (d1 - d3);
            return new Vector3(1 - v, v, 0);
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return new Vector3(0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w = d2 / (d2 - d6);
            return new Vector3(1 - w, 0, w);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return new Vector3(0, 1 - w, w);
        }
        float denom = 1 / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return new Vector3(1 - vv - ww, vv, ww);
    }

    private static (int[] WeldOf, int Count) Weld(ReadOnlySpan<Vector3> positions)
    {
        var weldOf = new int[positions.Length];
        var cells = new Dictionary<(long, long, long), int>();
        var first = new List<int>();   // вершина-представитель сваренной
        for (int v = 0; v < positions.Length; v++)
        {
            var p = positions[v];
            var key = Key(p);
            int found = -1;
            for (int dx = -1; dx <= 1 && found < 0; dx++)
                for (int dy = -1; dy <= 1 && found < 0; dy++)
                    for (int dz = -1; dz <= 1 && found < 0; dz++)
                        if (cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out int w)
                            && Vector3.DistanceSquared(positions[first[w]], p) <= WeldEpsilon * WeldEpsilon)
                            found = w;
            if (found < 0)
            {
                found = first.Count;
                first.Add(v);
                cells.TryAdd(key, found);
            }
            weldOf[v] = found;
        }
        return (weldOf, first.Count);
    }

    private static (long, long, long) Key(Vector3 p) =>
        ((long)MathF.Floor(p.X / WeldEpsilon), (long)MathF.Floor(p.Y / WeldEpsilon), (long)MathF.Floor(p.Z / WeldEpsilon));

    private static (int[] Start, int[] Vertices) Copies(int[] weldOf, int count)
    {
        var start = new int[count + 1];
        foreach (int w in weldOf) start[w + 1]++;
        for (int i = 0; i < count; i++) start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var vertices = new int[weldOf.Length];
        for (int v = 0; v < weldOf.Length; v++) vertices[fill[weldOf[v]]++] = v;
        return (start, vertices);
    }

    /// <summary>Смежность по сваренным рёбрам (третий треугольник на ребре не связывается); вернёт рёбер без пары.</summary>
    private static int Adjacency(int[] triWeld, sbyte[] segment, int[] neighbor)
    {
        Array.Fill(neighbor, -1);
        var edges = new Dictionary<(int, int), int>();   // ребро → 3·треугольник + ребро
        int triangles = triWeld.Length / 3;
        for (int t = 0; t < triangles; t++)
        {
            if (segment[t] < 0) continue;
            for (int e = 0; e < 3; e++)
            {
                int a = triWeld[3 * t + e], b = triWeld[3 * t + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (edges.TryGetValue(key, out int other))
                {
                    if (other >= 0 && neighbor[other] < 0)
                    {
                        neighbor[other] = t;
                        neighbor[3 * t + e] = other / 3;
                        edges[key] = -1;   // пара есть
                    }
                }
                else edges.Add(key, 3 * t + e);
            }
        }
        int open = 0;
        foreach (int v in edges.Values) if (v >= 0) open++;
        return open;
    }

    private int ReferenceJoint(ISkinMesh mesh, ReadOnlySpan<string> names, int segment, int[] triangles)
    {
        string? name = segment switch
        {
            BodySegs.Pelvis => "pelvis",
            BodySegs.Torso => "spine_04",
            BodySegs.Head => "head",
            BodySegs.UArmL => "upperarm_l",
            BodySegs.FArmL => "lowerarm_l",
            BodySegs.UArmR => "upperarm_r",
            BodySegs.FArmR => "lowerarm_r",
            BodySegs.ThighL => "thigh_l",
            BodySegs.ShinL => "calf_l",
            BodySegs.ThighR => "thigh_r",
            BodySegs.ShinR => "calf_r",
            BodySegs.FootL => "foot_l",
            BodySegs.FootR => "foot_r",
            _ => null,
        };
        for (int j = 0; j < names.Length; j++)
            if (string.Equals(names[j], name, StringComparison.OrdinalIgnoreCase) && mesh.JointSegment(j) == segment) return j;
        if (triangles.Length == 0) return -1;
        // Имени нет — сустав сегмента, ближайший к середине его кожи в привязке.
        var centre = Vector3.Zero;
        foreach (int t in triangles) centre += _bindPos[_tri[3 * t]];
        centre /= triangles.Length;
        int best = -1;
        float bestD = float.MaxValue;
        for (int j = 0; j < _bind.Length; j++)
        {
            if (mesh.JointSegment(j) != segment) continue;
            float d = Vector3.DistanceSquared(_bind[j].Translation, centre);
            if (d < bestD) (bestD, best) = (d, j);
        }
        return best;
    }

    private static int[] ForearmJoints(ReadOnlySpan<string> names, string side)
    {
        var list = new List<int>();
        for (int j = 0; j < names.Length; j++)
        {
            string n = names[j];
            if (n.EndsWith(side, StringComparison.OrdinalIgnoreCase)
                && (n.StartsWith("lowerarm_twist", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "hand" + side, StringComparison.OrdinalIgnoreCase)))
                list.Add(j);
        }
        return [.. list];
    }

    /// <summary>Скин вершины: на сустав k — точка в осях сустава (xyz) и вес (w); вес 0 — сустава нет (у первого — вес 0 даёт 0).</summary>
    private struct SkinVertex
    {
        public Vector4 L0, L1, L2, L3;

        public Vector4 this[int k]
        {
            readonly get => k switch { 0 => L0, 1 => L1, 2 => L2, _ => L3 };
            set
            {
                if (k == 0) L0 = value;
                else if (k == 1) L1 = value;
                else if (k == 2) L2 = value;
                else L3 = value;
            }
        }
    }

    /// <summary>Сетка поиска сегмента в привязке: треугольник — во всех ячейках, которые задевает его рамка.</summary>
    private sealed class SegmentGrid
    {
        private readonly Vector3 _min;
        private readonly float _cell;
        private readonly int _nx, _ny, _nz;
        private readonly int[] _start, _items;

        public SegmentGrid(SkinSurface s, int[] triangles)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (int t in triangles)
                for (int i = 0; i < 3; i++)
                {
                    min = Vector3.Min(min, s._bindPos[s._tri[3 * t + i]]);
                    max = Vector3.Max(max, s._bindPos[s._tri[3 * t + i]]);
                }
            var size = max - min;
            _cell = MathF.Max(GridCell, MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / GridMaxCells);
            _min = min;
            _nx = Math.Max(1, (int)MathF.Ceiling(size.X / _cell));
            _ny = Math.Max(1, (int)MathF.Ceiling(size.Y / _cell));
            _nz = Math.Max(1, (int)MathF.Ceiling(size.Z / _cell));
            var counts = new int[_nx * _ny * _nz + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    for (int i = 0; i < counts.Length - 1; i++) counts[i + 1] += counts[i];
                    _start = (int[])counts.Clone();
                    _items = new int[counts[^1]];
                }
                foreach (int t in triangles)
                {
                    var a = s._bindPos[s._tri[3 * t]];
                    var b = s._bindPos[s._tri[3 * t + 1]];
                    var c = s._bindPos[s._tri[3 * t + 2]];
                    Cell(Vector3.Min(a, Vector3.Min(b, c)), out int x0, out int y0, out int z0);
                    Cell(Vector3.Max(a, Vector3.Max(b, c)), out int x1, out int y1, out int z1);
                    for (int z = z0; z <= z1; z++)
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++)
                            {
                                int cell = (z * _ny + y) * _nx + x;
                                if (pass == 0) counts[cell + 1]++;
                                else _items![counts[cell]++] = t;
                            }
                }
            }
            _start ??= [];
            _items ??= [];
        }

        private void Cell(Vector3 p, out int x, out int y, out int z)
        {
            var q = (p - _min) / _cell;
            x = Math.Clamp((int)MathF.Floor(q.X), 0, _nx - 1);
            y = Math.Clamp((int)MathF.Floor(q.Y), 0, _ny - 1);
            z = Math.Clamp((int)MathF.Floor(q.Z), 0, _nz - 1);
        }

        public int NearestSkinned(SkinSurface s, Vector3 bind, float radius, Vector3 world, ReadOnlySpan<Matrix4x4> joints, out Vector3 bary,
            out float distance)
        {
            Cell(bind - new Vector3(radius), out int x0, out int y0, out int z0);
            Cell(bind + new Vector3(radius), out int x1, out int y1, out int z1);
            int best = -1;
            float bestD2 = float.MaxValue;
            bary = default;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = (z * _ny + y) * _nx + x;
                        for (int i = _start[cell]; i < _start[cell + 1]; i++)
                        {
                            int t = _items[i];
                            if (t == best) continue;
                            s.SkinTriangle(t, joints, out var a, out var b, out var c);
                            var w = ClosestBary(world, a, b, c);
                            float d2 = Vector3.DistanceSquared(world, w.X * a + w.Y * b + w.Z * c);
                            if (d2 < bestD2 || d2 == bestD2 && t < best) (bestD2, best, bary) = (d2, t, w);
                        }
                    }
            distance = best >= 0 ? MathF.Sqrt(bestD2) : float.MaxValue;
            return best;
        }

        public int Nearest(SkinSurface s, Vector3 p, out Vector3 bary, out float distance)
        {
            Cell(p, out int cx, out int cy, out int cz);
            // Расстояние от точки до клетки-старта (точка вне сетки — до её края): нижняя граница кольца r — max(это, (r − 1)·ячейка).
            var lo = _min + new Vector3(cx, cy, cz) * _cell;
            float outside = Vector3.Distance(p, Vector3.Clamp(p, lo, lo + new Vector3(_cell)));
            int best = -1;
            float bestD2 = float.MaxValue;
            bary = default;
            int rings = Math.Max(_nx, Math.Max(_ny, _nz));
            for (int r = 0; r <= rings; r++)
            {
                if (best >= 0)
                {
                    float bound = MathF.Max(outside, (r - 1) * _cell);
                    if (bound > 0 && bound * bound > bestD2) break;
                }
                for (int z = cz - r; z <= cz + r; z++)
                    for (int y = cy - r; y <= cy + r; y++)
                        for (int x = cx - r; x <= cx + r; x++)
                        {
                            if (Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) != r) continue;
                            if ((uint)x >= (uint)_nx || (uint)y >= (uint)_ny || (uint)z >= (uint)_nz) continue;
                            int cell = (z * _ny + y) * _nx + x;
                            for (int i = _start[cell]; i < _start[cell + 1]; i++)
                            {
                                int t = _items[i];
                                var a = s._bindPos[s._tri[3 * t]];
                                var b = s._bindPos[s._tri[3 * t + 1]];
                                var c = s._bindPos[s._tri[3 * t + 2]];
                                var w = ClosestBary(p, a, b, c);
                                float d2 = Vector3.DistanceSquared(p, w.X * a + w.Y * b + w.Z * c);
                                if (d2 < bestD2 || d2 == bestD2 && t < best) (bestD2, best, bary) = (d2, t, w);
                            }
                        }
            }
            distance = best >= 0 ? MathF.Sqrt(bestD2) : float.MaxValue;
            return best;
        }
    }
}
