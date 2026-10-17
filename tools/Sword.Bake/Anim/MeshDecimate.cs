using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Bake.Anim;

/// <summary>
/// Прореживание меша до бюджета треугольников: схлопывание рёбер по квадрикам ошибки (Garland–Heckbert).
///
/// Схлопывание — «половинное»: вершина u уходит в соседа v, и v остаётся на месте со своими атрибутами
/// (UV, нормаль, веса), поэтому новых вершин нет и скиннинг не портится. Цена — ошибка квадрики u+v в точке v.
/// Сетка сваривается по позиции (швы UV и нормалей — разные вершины с одной позицией), и неподвижными
/// остаются вершины на шве, на краю сетки и на границе секций материалов: развёртка, дыры и секции
/// переживают прореживание как есть. Схлопывание отменяется, если перевернёт грань или сломает
/// многообразие (у ребра больше двух общих соседей) — замкнутая сетка остаётся замкнутой.
/// </summary>
public static class MeshDecimate
{
    /// <summary>Грань после схлопывания не должна развернуться сильнее этого (косинус к прежней нормали).</summary>
    private const float MinFlipCos = 0.2f;

    /// <returns>Треугольников в итоге.</returns>
    public static int Run(SkinnedMeshData mesh, int maxTriangles) => Decimate(mesh, maxTriangles, keepSeams: true, out _);

    /// <summary>
    /// Индексы меша теней: тот же меш, прореженный до <paramref name="maxTriangles"/> без неподвижных швов атрибутов и границ секций
    /// (тени нужны только точки), — по исходным вершинам <paramref name="mesh"/>: схлопывание половинное, оставшиеся вершины — его же.
    /// Сам меш не меняется; бюджет не меньше треугольников меша — его индексы.
    /// </summary>
    public static uint[] ShadowLod(SkinnedMeshData mesh, int maxTriangles)
    {
        var copy = new SkinnedMeshData
        {
            Vertices = mesh.Vertices, Indices = mesh.Indices, Sections = [],
            JointNames = mesh.JointNames, JointParents = mesh.JointParents, InverseBind = mesh.InverseBind,
        };
        if (Decimate(copy, maxTriangles, keepSeams: false, out var origin) == mesh.Indices.Length / 3 && origin is null)
            return (uint[])mesh.Indices.Clone();
        var indices = new uint[copy.Indices.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = (uint)origin![copy.Indices[i]];
        return indices;
    }

    /// <summary>
    /// <paramref name="keepSeams"/> — вершины на шве атрибутов и на границе секций неподвижны; <paramref name="origin"/> — исходная вершина
    /// каждой вершины итога (null — прореживать не пришлось).
    /// </summary>
    private static int Decimate(SkinnedMeshData mesh, int maxTriangles, bool keepSeams, out int[]? origin)
    {
        origin = null;
        var verts = mesh.Vertices;
        var idx = mesh.Indices;
        int faces = idx.Length / 3;
        if (faces <= maxTriangles) return faces;

        // ── сварка по позиции ──
        var weldOf = new int[verts.Length];
        var byPos = new Dictionary<Vector3, int>();
        var pos = new List<Vector3>();
        var attrs = new List<List<int>>();   // сварная вершина → её вершины-атрибуты
        for (int i = 0; i < verts.Length; i++)
        {
            if (!byPos.TryGetValue(verts[i].Position, out int w))
            {
                byPos[verts[i].Position] = w = pos.Count;
                pos.Add(verts[i].Position);
                attrs.Add([]);
            }
            weldOf[i] = w;
            attrs[w].Add(i);
        }
        int nw = pos.Count;

        // секция каждой грани
        var faceSection = new int[faces];
        for (int s = 0; s < mesh.Sections.Length; s++)
            for (int f = mesh.Sections[s].Start / 3; f < (mesh.Sections[s].Start + mesh.Sections[s].Count) / 3; f++)
                faceSection[f] = s;

        // грани: сварные углы и атрибуты углов
        var fw = new int[faces * 3];
        var fa = new int[faces * 3];
        var alive = new bool[faces];
        var vFaces = new List<int>[nw];
        for (int w = 0; w < nw; w++) vFaces[w] = [];
        for (int f = 0; f < faces; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                fa[f * 3 + k] = (int)idx[f * 3 + k];
                fw[f * 3 + k] = weldOf[idx[f * 3 + k]];
            }
            alive[f] = fw[f * 3] != fw[f * 3 + 1] && fw[f * 3 + 1] != fw[f * 3 + 2] && fw[f * 3] != fw[f * 3 + 2];
            if (!alive[f]) continue;
            for (int k = 0; k < 3; k++) vFaces[fw[f * 3 + k]].Add(f);
        }

        // ── неподвижные вершины: шов атрибутов, край сетки, граница секций ──
        var locked = new bool[nw];
        var edgeUse = new Dictionary<(int, int), int>();
        for (int f = 0; f < faces; f++)
        {
            if (!alive[f]) continue;
            for (int k = 0; k < 3; k++)
            {
                int a = fw[f * 3 + k], b = fw[f * 3 + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var (key, count) in edgeUse)
            if (count != 2) locked[key.Item1] = locked[key.Item2] = true;
        for (int w = 0; w < nw; w++)
        {
            var used = new HashSet<int>();
            int section = -1;
            foreach (int f in vFaces[w])
            {
                for (int k = 0; k < 3; k++) if (fw[f * 3 + k] == w) used.Add(fa[f * 3 + k]);
                if (keepSeams && section >= 0 && faceSection[f] != section) locked[w] = true;
                section = faceSection[f];
            }
            if (keepSeams && used.Count > 1) locked[w] = true;
        }

        // ── квадрики граней ──
        var q = new Quadric[nw];
        for (int f = 0; f < faces; f++)
        {
            if (!alive[f]) continue;
            var a = pos[fw[f * 3]];
            var n = Vector3.Cross(pos[fw[f * 3 + 1]] - a, pos[fw[f * 3 + 2]] - a);
            float area = n.Length();
            if (area < 1e-20f) continue;
            n /= area;
            var plane = Quadric.Plane(n, -Vector3.Dot(n, a)) * (area * 0.5f);
            for (int k = 0; k < 3; k++) q[fw[f * 3 + k]] += plane;
        }

        // ── очередь рёбер: (цена, u → v), устаревшие записи узнаются по версии вершины ──
        var version = new int[nw];
        var removed = new bool[nw];
        var heap = new PriorityQueue<(int U, int V, int Vu, int Vv), double>();
        void Push(int u, int v)
        {
            if (locked[u] || removed[u] || removed[v]) return;
            heap.Enqueue((u, v, version[u], version[v]), (q[u] + q[v]).Error(pos[v]));
        }
        foreach (var (a, b) in edgeUse.Keys)
        {
            Push(a, b);
            Push(b, a);
        }

        int live = alive.Count(x => x);
        var ring = new HashSet<int>();
        while (live > maxTriangles && heap.TryDequeue(out var e, out _))
        {
            var (u, v, vu, vv) = e;
            if (removed[u] || removed[v] || version[u] != vu || version[v] != vv) continue;
            if (!CanCollapse(u, v)) continue;

            // атрибут v, которым заменится атрибут u: из грани, общей у ребра
            int av = -1;
            foreach (int f in vFaces[u])
                for (int k = 0; k < 3; k++)
                    if (fw[f * 3 + k] == v) { av = fa[f * 3 + k]; break; }
            if (av < 0) continue;

            foreach (int f in vFaces[u])
            {
                if (!alive[f]) continue;
                bool hasV = fw[f * 3] == v || fw[f * 3 + 1] == v || fw[f * 3 + 2] == v;
                if (hasV)
                {
                    alive[f] = false;
                    live--;
                    for (int k = 0; k < 3; k++) if (fw[f * 3 + k] != u) vFaces[fw[f * 3 + k]].Remove(f);
                    continue;
                }
                for (int k = 0; k < 3; k++)
                    if (fw[f * 3 + k] == u)
                    {
                        fw[f * 3 + k] = v;
                        fa[f * 3 + k] = av;
                    }
                vFaces[v].Add(f);
            }
            vFaces[u].Clear();
            removed[u] = true;
            q[v] += q[u];
            version[v]++;
            ring.Clear();
            foreach (int f in vFaces[v])
                for (int k = 0; k < 3; k++) if (fw[f * 3 + k] != v) ring.Add(fw[f * 3 + k]);
            // квадрика поменялась только у v: пересчитать рёбра при ней, остальные записи в очереди в силе
            // (годность схлопывания всё равно проверяется при извлечении)
            foreach (int n in ring)
            {
                Push(v, n);
                Push(n, v);
            }
        }

        bool CanCollapse(int u, int v)
        {
            // многообразие: у ребра ровно две общие соседние вершины
            var nu = new HashSet<int>();
            foreach (int f in vFaces[u])
                for (int k = 0; k < 3; k++) nu.Add(fw[f * 3 + k]);
            int common = 0;
            var seen = new HashSet<int>();
            foreach (int f in vFaces[v])
                for (int k = 0; k < 3; k++)
                {
                    int w = fw[f * 3 + k];
                    if (w != u && w != v && nu.Contains(w) && seen.Add(w)) common++;
                }
            if (common != 2) return false;
            // грани u без v не должны перевернуться, когда u встанет в v
            foreach (int f in vFaces[u])
            {
                int a = fw[f * 3], b = fw[f * 3 + 1], c = fw[f * 3 + 2];
                if (a == v || b == v || c == v) continue;
                var before = Normal(pos[a], pos[b], pos[c]);
                var after = Normal(a == u ? pos[v] : pos[a], b == u ? pos[v] : pos[b], c == u ? pos[v] : pos[c]);
                if (before == Vector3.Zero || after == Vector3.Zero || Vector3.Dot(before, after) < MinFlipCos) return false;
            }
            return true;
        }

        // ── сборка: грани по секциям в прежнем порядке, неиспользуемые вершины уходят ──
        var remap = new int[verts.Length];
        Array.Fill(remap, -1);
        var outVerts = new List<SkinVertex>();
        var outOrigin = new List<int>();
        var outIdx = new List<uint>();
        var sections = new List<MeshSection>();
        int sectionCount = Math.Max(mesh.Sections.Length, 1);
        for (int s = 0; s < sectionCount; s++)
        {
            int start = outIdx.Count;
            for (int f = 0; f < faces; f++)
            {
                if (!alive[f] || (mesh.Sections.Length > 0 && faceSection[f] != s)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = fa[f * 3 + k];
                    if (remap[a] < 0)
                    {
                        remap[a] = outVerts.Count;
                        outVerts.Add(verts[a]);
                        outOrigin.Add(a);
                    }
                    outIdx.Add((uint)remap[a]);
                }
            }
            if (mesh.Sections.Length > 0) sections.Add(mesh.Sections[s] with { Start = start, Count = outIdx.Count - start });
        }
        mesh.Vertices = [.. outVerts];
        mesh.Indices = [.. outIdx];
        mesh.Sections = [.. sections];
        origin = [.. outOrigin];
        return live;
    }

    private static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        return len < 1e-20f ? Vector3.Zero : n / len;
    }

    /// <summary>Квадрика ошибки: сумма квадратов расстояний до плоскостей, симметричная 4×4 (10 чисел).</summary>
    private readonly record struct Quadric(double A, double B, double C, double D, double E, double F, double G, double H, double I, double J)
    {
        /// <summary>Плоскость n·x + d = 0.</summary>
        public static Quadric Plane(Vector3 n, float d) =>
            new(n.X * n.X, n.X * n.Y, n.X * n.Z, n.X * d, n.Y * n.Y, n.Y * n.Z, n.Y * d, n.Z * n.Z, n.Z * d, (double)d * d);

        public static Quadric operator +(Quadric a, Quadric b) =>
            new(a.A + b.A, a.B + b.B, a.C + b.C, a.D + b.D, a.E + b.E, a.F + b.F, a.G + b.G, a.H + b.H, a.I + b.I, a.J + b.J);

        public static Quadric operator *(Quadric a, float s) =>
            new(a.A * s, a.B * s, a.C * s, a.D * s, a.E * s, a.F * s, a.G * s, a.H * s, a.I * s, a.J * s);

        public double Error(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return A * x * x + 2 * B * x * y + 2 * C * x * z + 2 * D * x + E * y * y + 2 * F * y * z + 2 * G * y + H * z * z + 2 * I * z + J;
        }
    }
}
