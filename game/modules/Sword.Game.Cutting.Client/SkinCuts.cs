using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Cutting;

/// <summary>
/// Поле разреза меша по записи отделения: расстояние до плоскости в позе тика; вершины без веса на ветви — всегда на стороне тела (A),
/// вершины потомков сегмента в ветви — на стороне куска (B); у суставов граница мягкая — доля веса суставов вне ветви сдвигает вершину к
/// телу, доля потомков — к куску (на долю · <c>reach</c>): кожа у плеча, ушедшего с куском, не остаётся у культи. Рваный край (отрыв) —
/// шум по позиции с амплитудой <c>ragged</c>.
/// </summary>
public static class CutField
{
    /// <summary>Вершины вне ветви и потомков — далеко от плоскости, м.</summary>
    public const float Far = 1;

    public static void Compute(CutMesh mesh, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<int> jointSegment, ushort branch, int segment,
        Vector3 normal, float distance, float reach, float ragged, Span<float> field, Span<Vector3> posed)
    {
        var v = mesh.Vertices;
        for (int i = 0; i < v.Length; i++)
            Vertex(v[i], palette, jointSegment, branch, segment, normal, distance, reach, ragged, out field[i], out posed[i]);
    }

    private static void Vertex(in CutVertex v, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<int> jointSegment, ushort branch, int segment,
        Vector3 normal, float distance, float reach, float ragged, out float field, out Vector3 posed)
    {
        posed = Posed(v, palette);
        float inBranch = 0, below = 0;
        for (int k = 0; k < 4; k++)
        {
            float w = Weight(v, k);
            if (!(w > 0)) continue;
            int j = (int)(v.Joints >> (8 * k) & 0xFF);
            int s = (uint)j < (uint)jointSegment.Length ? jointSegment[j] : -1;
            if (s < 0 || (branch & (1 << s)) == 0) continue;
            inBranch += w;
            if (s != segment) below += w;
        }
        if (inBranch < 1e-3f) field = -Far;
        else if (below > 0.5f) field = Far;
        else field = Vector3.Dot(normal, posed) - distance - (1 - inBranch) * reach + below * reach + ragged * Noise(posed * 40);
    }

    /// <summary>Вершина в позе по палитре (InverseBind · сустав в мире).</summary>
    public static Vector3 Posed(in CutVertex v, ReadOnlySpan<Matrix4x4> palette)
    {
        var p = Vector3.Zero;
        for (int k = 0; k < 4; k++)
        {
            float w = Weight(v, k);
            int j = (int)(v.Joints >> (8 * k) & 0xFF);
            if (w > 0 && (uint)j < (uint)palette.Length) p += w * Vector3.Transform(v.Position, palette[j]);
        }
        return p;
    }

    public static float Weight(in CutVertex v, int k) =>
        k switch { 0 => v.Weights.X, 1 => v.Weights.Y, 2 => v.Weights.Z, _ => Math.Clamp(1 - v.Weights.X - v.Weights.Y - v.Weights.Z, 0, 1) };

    /// <summary>Гладкий шум −1…1 (сумма синусов): одинаков для сваренных вершин — одна позиция, одно значение.</summary>
    public static float Noise(Vector3 p) =>
        (MathF.Sin(p.X * 1.7f + MathF.Sin(p.Y * 2.3f)) + MathF.Sin(p.Y * 2.1f + MathF.Sin(p.Z * 1.9f)) + MathF.Sin(p.Z * 2.7f + MathF.Sin(p.X * 1.3f))) / 3;
}

/// <summary>Меши частей: выгрузка и освобождение (рендер — производные от меша персонажа; тесты — на CPU).</summary>
public interface ICutMeshes
{
    int Add(string name, CutMesh mesh);

    void Remove(int mesh);

    long Bytes(int mesh);
}

/// <summary>Меши частей у рендера: производные от меша персонажа <paramref name="baseMesh"/>.</summary>
public sealed class RendererCutMeshes(SkinnedMeshes meshes, int baseMesh) : ICutMeshes
{
    public int Add(string name, CutMesh mesh) => meshes.AddDerivedMesh(name, baseMesh, MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()), mesh.Indices);

    public void Remove(int mesh) => meshes.RemoveMesh(mesh);

    public long Bytes(int mesh) => meshes.MeshBytes(mesh);
}

/// <summary>
/// Разрез меша бойца по записям отделений (клиент): часть меша, где больше всего вершин сегмента, режется в фоне (<see cref="MeshCutter"/>).
/// Разрез и перелом — сторона тела остаётся частью с прежней позой, сторона куска — новая часть с позой куска. Отрыв — по суставу с рваным
/// краем, оторванное рисует поза бойца (тело сегмента само на физике), а влияние сегмента на стороне тела переходит на сустав родителя.
/// Клочья — область сегмента режется ячейками Вороного (<see cref="ShatterCells"/>), каждая — часть с позой своего куска. Меши рендера —
/// производные от меша персонажа (вершины основы на своих местах — слой крови по вершинам читается как прежде). Разрезы бойца — по очереди.
/// </summary>
public sealed class SkinCuts(FighterSkinParts parts, IFighterPoses poses, ISkinMesh skin, ISeverFeed feed)
{
    private sealed record Part(CutMesh Mesh, int Gpu, int Piece);

    private sealed record Job(LimbDetached Event, Matrix4x4[] Joints, SegmentBody Body);

    /// <summary>Итог разреза: часть <see cref="Target"/> заменяется частями <see cref="Parts"/>; кусок −2 — поза прежней части.</summary>
    private sealed record Done(int Target, List<(CutMesh Mesh, int Piece)> Parts);

    private sealed class Fighter
    {
        public readonly List<Part> Parts = [];
        public readonly Queue<Job> Queue = new();
        public Task<Done?>? Running;
        public bool Published;
    }

    /// <summary>Амплитуда рваного края отрыва, м.</summary>
    public const float Ragged = 0.012f;

    private readonly Dictionary<ulong, Fighter> _fighters = [];
    private readonly List<int> _dropped = [];
    private ICutMeshes? _meshes;
    private CutMesh? _base;
    private int[] _jointSegment = [];
    private int[] _keyJoint = [];
    private Matrix4x4[] _inverse = [];

    /// <summary>Разрезов готово, мешей рендера и их байт сейчас, последний разрез в фоне, мс (диагностика).</summary>
    public int Cuts { get; private set; }

    public int Meshes { get; private set; }

    public long Bytes { get; private set; }

    public double LastMs { get; private set; }

    /// <summary>Разрезов в очереди и в фоне нет.</summary>
    public bool Idle
    {
        get
        {
            foreach (var f in _fighters.Values)
                if (f.Running is not null || f.Queue.Count > 0) return false;
            return true;
        }
    }

    public void Init(Renderer renderer)
    {
        if (parts.BaseMesh < 0 || parts.MeshPath.Length == 0 || !skin.Loaded) return;
        Init(renderer.Skinned.ReadCpu(parts.MeshPath), new RendererCutMeshes(renderer.Skinned, parts.BaseMesh));
    }

    /// <summary>Меш персонажа <paramref name="cpu"/> (основа частей) и хранилище мешей частей.</summary>
    public void Init(SkinnedMeshCpu cpu, ICutMeshes meshes)
    {
        _meshes = meshes;
        var vertices = new CutVertex[cpu.Positions.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new CutVertex
            {
                Position = cpu.Positions[i], Normal = cpu.Normals[i], Uv = i < cpu.Uvs.Length ? cpu.Uvs[i] : default, Joints = cpu.Joints[i],
                Weights = new Vector3(cpu.Weights[i].X, cpu.Weights[i].Y, cpu.Weights[i].Z),
            };
        _base = new CutMesh { Vertices = vertices, Indices = cpu.Indices };
        _inverse = cpu.InverseBind;
        _jointSegment = new int[cpu.InverseBind.Length];
        for (int j = 0; j < _jointSegment.Length; j++) _jointSegment[j] = skin.JointSegment(j);
        _keyJoint = KeyJoints(cpu, _jointSegment);
        _ = Task.Run(Warm);   // первый разрез ждал бы JIT десятки мс
    }

    /// <summary>Сустав сегмента с наибольшим суммарным весом — к нему крепится крышка.</summary>
    private static int[] KeyJoints(SkinnedMeshCpu cpu, int[] jointSegment)
    {
        var weight = new float[jointSegment.Length];
        for (int i = 0; i < cpu.Joints.Length; i++)
            for (int k = 0; k < 4; k++)
            {
                int j = (int)(cpu.Joints[i] >> (8 * k) & 0xFF);
                if ((uint)j < (uint)weight.Length) weight[j] += cpu.Weights[i][k];
            }
        var key = new int[BodySegs.Count];
        var best = new float[BodySegs.Count];
        for (int j = 0; j < weight.Length; j++)
        {
            int s = jointSegment[j];
            if ((uint)s < BodySegs.Count && weight[j] > best[s]) (best[s], key[s]) = (weight[j], j);
        }
        return key;
    }

    private static void Warm()
    {
        var mesh = new CutMesh
        {
            Vertices = [new() { Position = new(0, 0, 0) }, new() { Position = new(1, 0, 0) }, new() { Position = new(0, 1, 0) }, new() { Position = new(0, 0, 1) }],
            Indices = [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3],
        };
        Span<float> field = [-1, 1, 1, 1];
        Span<Vector3> posed = [mesh.Vertices[0].Position, mesh.Vertices[1].Position, mesh.Vertices[2].Position, mesh.Vertices[3].Position];
        MeshCutter.Cut(mesh, field, posed, new CapSpec(Vector3.UnitX, new Vector3(0.5f, 0, 0), 1, Vector3.UnitX, -Vector3.UnitX, 0, 0));
    }

    /// <summary>Наблюдатель тиков: отделения тика — в очередь бойца с позой ровно этого тика.</summary>
    public void AfterTick(ulong tick)
    {
        if (_base is null) return;
        var detached = feed.Detached;
        if (detached.IsEmpty || poses.Tick != feed.Tick) return;
        foreach (var (key, e) in detached)
        {
            int index = poses.Find(key);
            if (index < 0) continue;
            var joints = poses.Joints(index);
            var segments = poses.Segments(index);
            if (joints.Length != _jointSegment.Length || segments.Length <= e.Segment) continue;
            // новая жизнь бойца — части с нуля: прежние части сняла арена (боец построен заново) или первый кусок, а у бойца уже есть части
            // кусков (их номера с прошлой жизни); отрывы кусков не заводят — первый кусок после отрывов той же жизни части не сбрасывает
            if (!_fighters.TryGetValue(key, out var f) || f.Published && !parts.TryGet(key, out _) || e.Piece == 0 && HasPieces(f))
                Reset(key, f = new Fighter());
            f.Queue.Enqueue(new Job(e, joints.ToArray(), segments[e.Segment]));
        }
    }

    private static bool HasPieces(Fighter f)
    {
        foreach (var p in f.Parts)
            if (p.Piece >= 0) return true;
        return false;
    }

    /// <summary>
    /// Части бойца — с нуля. Прежние меши освобождаются ровно раз и не раньше, чем арена перестанет их рисовать: опубликованные снимаются
    /// с публикации (освобождение — через <see cref="FighterSkinParts.TakeDropped"/>), неопубликованных нет — их уже сняла арена.
    /// </summary>
    private void Reset(ulong key, Fighter fresh)
    {
        if (_fighters.Remove(key, out _) && parts.TryGet(key, out _)) parts.Drop(key);
        fresh.Parts.Add(new Part(_base!, parts.BaseMesh, -1));
        _fighters[key] = fresh;
    }

    /// <summary>Кадр: готовые разрезы — в меши рендера и части бойца; следующий разрез бойца — в фон; снятые части — освободить.</summary>
    public void Update()
    {
        if (_meshes is null) return;
        parts.TakeDropped(_dropped);
        foreach (int mesh in _dropped) Release(mesh);
        _dropped.Clear();
        List<ulong>? gone = null;
        foreach (var (key, f) in _fighters)
        {
            if (f.Running is { IsCompleted: true } done)
            {
                f.Running = null;
                if (done.IsCompletedSuccessfully && done.Result is { } r) Apply(key, f, r);
                else if (done.IsFaulted) Log.Warning("cutting", $"разрез меша бойца {key}: {done.Exception?.GetBaseException().Message}");
            }
            if (f.Running is null && f.Queue.TryDequeue(out var job)) f.Running = Start(f, job);
            // Арена сняла части (боец построен заново) — состояние бойца больше не нужно.
            if (f.Published && f.Running is null && f.Queue.Count == 0 && !parts.TryGet(key, out _)) (gone ??= []).Add(key);
        }
        if (gone is not null)
            foreach (var key in gone) _fighters.Remove(key);
    }

    private Task<Done?> Start(Fighter f, Job job)
    {
        var e = job.Event;
        int target = PartOf(f, e.Segment);
        var mesh = f.Parts[target].Mesh;
        return Task.Run<Done?>(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var palette = new Matrix4x4[_inverse.Length];
            for (int j = 0; j < palette.Length; j++) palette[j] = _inverse[j] * job.Joints[j];
            var done = e.Mode switch
            {
                SeverMode.Shatter => Shatter(target, mesh, palette, job),
                SeverMode.Tear => Tear(target, mesh, palette, job),
                _ => Split(target, mesh, palette, job),
            };
            LastMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return done;
        });
    }

    /// <summary>Система кости сегмента в мире на тике отделения: тело сегмента после отделения — со сдвигом культи по оси.</summary>
    private static (Vector3 Center, Vector3 Axis, Quaternion Rotation) Bone(Job job, float shift)
    {
        var axis = Vector3.Transform(Vector3.UnitY, job.Body.Rotation);
        return (job.Body.Position - axis * shift, axis, job.Body.Rotation);
    }

    /// <summary>Поле и позы части по плоскости (мир) с мягкой границей у сустава.</summary>
    private (float[] Field, Vector3[] Posed) Field(CutMesh mesh, Matrix4x4[] palette, ushort branch, int segment, Vector3 normal, float distance,
        float reach, float ragged)
    {
        var field = new float[mesh.Vertices.Length];
        var posed = new Vector3[mesh.Vertices.Length];
        CutField.Compute(mesh, palette, _jointSegment, branch, segment, normal, distance, reach, ragged, field, posed);
        return (field, posed);
    }

    private static Vector3 BindNormal(Matrix4x4[] palette, int joint, Vector3 normal)
    {
        Matrix4x4.Invert(palette[joint], out var toBind);
        return Vector3.Normalize(Vector3.TransformNormal(normal, toBind));
    }

    /// <summary>Разрез и перелом: плоскость записи; кусок — новая часть с позой куска.</summary>
    private Done Split(int target, CutMesh mesh, Matrix4x4[] palette, Job job)
    {
        var e = job.Event;
        var (center, axis, rotation) = Bone(job, e.StumpShift);
        var normal = Vector3.Normalize(Vector3.Transform(e.Normal, rotation));
        float distance = Vector3.Dot(normal, center) + e.Offset;
        var origin = center + axis * (e.Normal.Y != 0 ? e.Offset / e.Normal.Y : 0);
        // с куском — сегмент и дети, ушедшие с ним (их сустав по сторону куска); дети на культе — сторона тела
        ushort below = (ushort)(e.Carried & ~(1 << e.Segment)), branch = (ushort)(below | 1 << e.Segment);
        var (field, posed) = Field(mesh, palette, branch, e.Segment, normal, distance, e.StumpRadius * 2.5f, 0);
        int key = _keyJoint[e.Segment];
        var bind = BindNormal(palette, key, normal);
        var cut = MeshCutter.Cut(mesh, field, posed, new CapSpec(normal, origin, MathF.Max(e.PieceRadius, 0.02f), bind, -bind, (byte)key, (byte)key));
        var a = Expand(mesh, cut.A);
        var b = Expand(mesh, cut.B);
        // У сустава вершины весят на обе стороны: у культи ушедшие дети, у куска чужие суставы — на сустав сегмента, иначе кожа тянется за
        // ушедшей частью.
        Rebind(a, below, key);
        Rebind(b, (ushort)~branch, key);
        return new Done(target, [(a, -2), (b, e.Piece)]);
    }

    /// <summary>
    /// Отрыв: у проксимального сустава, край рваный; оторванное — позой бойца; у стороны тела влияние суставов ветви — на сустав
    /// родителя (иначе кожа у плеча тянулась бы за оторванной рукой).
    /// </summary>
    private Done Tear(int target, CutMesh mesh, Matrix4x4[] palette, Job job)
    {
        var e = job.Event;
        var (center, axis, _) = Bone(job, 0);
        float distance = Vector3.Dot(axis, center) + e.Offset;
        var origin = center + axis * e.Offset;
        ushort branch = Branch(e.Segment);
        var (field, posed) = Field(mesh, palette, branch, e.Segment, axis, distance, e.PieceRadius * 1.5f, Ragged);
        int parent = BodySegs.Parent(e.Segment), keyParent = _keyJoint[Math.Max(parent, 0)], key = _keyJoint[e.Segment];
        var bindA = BindNormal(palette, keyParent, axis);
        var bindB = BindNormal(palette, key, -axis);
        var cut = MeshCutter.Cut(mesh, field, posed, new CapSpec(axis, origin, MathF.Max(e.PieceRadius, 0.02f), bindA, bindB, (byte)keyParent, (byte)key));
        var a = Expand(mesh, cut.A);
        Rebind(a, branch, keyParent);
        var b = Expand(mesh, cut.B);
        Rebind(b, (ushort)~branch, key);
        return new Done(target, [(a, -2), (b, -1)]);
    }

    /// <summary>
    /// Клочья: область сегмента от <see cref="LimbDetached.From"/> отрезается плоскостью, потомки — плоскостью у <see cref="LimbDetached.To"/>
    /// (они оторваны, рисуются позой бойца); область режется на ячейки: ячейка i — пересечение полупространств ближе к центру i, чем к
    /// остальным.
    /// </summary>
    private Done Shatter(int target, CutMesh mesh, Matrix4x4[] palette, Job job)
    {
        var e = job.Event;
        var (center, axis, rotation) = Bone(job, e.StumpShift);
        int key = _keyJoint[e.Segment];
        float radius = MathF.Max(e.PieceRadius, 0.02f);
        ushort branch = Branch(e.Segment);
        var result = new List<(CutMesh, int)>();
        // 1. область сегмента — прочь от культи
        float d0 = Vector3.Dot(axis, center) + e.From;
        var (f0, p0) = Field(mesh, palette, branch, e.Segment, axis, d0, e.StumpRadius * 2.5f, Ragged);
        var bind = BindNormal(palette, key, axis);
        var first = MeshCutter.Cut(mesh, f0, p0, new CapSpec(axis, center + axis * e.From, radius, bind, -bind, (byte)key, (byte)key));
        ushort below = (ushort)(branch & ~(1 << e.Segment));
        var rest = Expand(mesh, first.A);
        Rebind(rest, below, key);
        result.Add((rest, -2));
        var region = Expand(mesh, first.B);
        // 2. потомки — прочь от области: они оторваны и рисуются позой бойца
        if ((branch & ~(1 << e.Segment)) != 0)
        {
            float d1 = Vector3.Dot(axis, center) + e.To;
            var (f1, p1) = Field(region, palette, branch, e.Segment, axis, d1, 0, Ragged);
            var split = MeshCutter.Cut(region, f1, p1, new CapSpec(axis, center + axis * e.To, radius, bind, -bind, (byte)key, (byte)key));
            if (split.B.Indices.Length > 0)
            {
                // оторванные потомки — только на своих суставах
                var torn = Expand(region, split.B);
                Rebind(torn, (ushort)~below, _keyJoint[FirstChild(e.Segment)]);
                result.Add((torn, -1));
            }
            region = Expand(region, split.A);
        }
        // 3. ячейки
        int count = Math.Min(e.Count, Severance.MaxPieces);
        var local = new Vector3[Severance.MaxPieces];
        ShatterCells.Sites(e.Seed, count, e.From, e.To, e.PieceRadius, local);
        var sites = new Vector3[count];
        for (int i = 0; i < count; i++) sites[i] = center + Vector3.Transform(local[i], rotation);
        // ячейки режутся по сжатой области (только её вершины), затем вершины основы — на прежние места
        var compact = Compact(region);
        for (int i = 0; i < count; i++)
        {
            var cell = Cell(compact, palette, sites, i, radius, key);
            if (cell.Indices.Length == 0) continue;
            var expanded = Expand(region, cell);
            Rebind(expanded, (ushort)~(1 << e.Segment), key);   // клочок жёсткий — весь на своём теле
            result.Add((expanded, e.Piece + i));
        }
        return new Done(target, result);
    }

    /// <summary>Только вершины, на которые ссылаются треугольники, в прежнем порядке.</summary>
    public static CutMesh Compact(CutMesh mesh)
    {
        var map = new int[mesh.Vertices.Length];
        Array.Fill(map, -1);
        foreach (uint i in mesh.Indices) map[i] = 0;
        var vertices = new List<CutVertex>();
        for (int i = 0; i < map.Length; i++)
            if (map[i] == 0)
            {
                map[i] = vertices.Count;
                vertices.Add(mesh.Vertices[i]);
            }
        var indices = new uint[mesh.Indices.Length];
        for (int k = 0; k < indices.Length; k++) indices[k] = (uint)map[mesh.Indices[k]];
        return new CutMesh { Vertices = [.. vertices], Indices = indices };
    }

    /// <summary>Ячейка i: область, срезанная по серединным плоскостям ко всем другим центрам (сторона центра i).</summary>
    private static CutMesh Cell(CutMesh region, Matrix4x4[] palette, Vector3[] sites, int i, float radius, int key)
    {
        var cell = region;
        for (int j = 0; j < sites.Length && cell.Indices.Length > 0; j++)
        {
            if (j == i) continue;
            var n = Vector3.Normalize(sites[j] - sites[i]);
            var mid = (sites[i] + sites[j]) * 0.5f;
            var field = new float[cell.Vertices.Length];
            var posed = new Vector3[cell.Vertices.Length];
            for (int v = 0; v < field.Length; v++)
            {
                posed[v] = CutField.Posed(cell.Vertices[v], palette);
                field[v] = Vector3.Dot(posed[v] - mid, n) + Ragged * 0.5f * CutField.Noise(posed[v] * 40);
            }
            var nb = BindNormal(palette, key, n);
            var c = MeshCutter.Cut(cell, field, posed, new CapSpec(n, mid, radius, nb, -nb, (byte)key, (byte)key));
            cell = Expand(cell, c.A);
        }
        return cell;
    }

    /// <summary>Влияния суставов ветви — на сустав <paramref name="joint"/> (вершины стороны тела у оторванного сустава).</summary>
    private void Rebind(CutMesh mesh, ushort branch, int joint)
    {
        var v = mesh.Vertices;
        for (int i = 0; i < v.Length; i++)
        {
            uint packed = v[i].Joints;
            for (int k = 0; k < 4; k++)
            {
                int j = (int)(packed >> (8 * k) & 0xFF);
                int s = (uint)j < (uint)_jointSegment.Length ? _jointSegment[j] : -1;
                if (s >= 0 && (branch & (1 << s)) != 0) packed = packed & ~(0xFFu << (8 * k)) | (uint)joint << (8 * k);
            }
            v[i].Joints = packed;
        }
    }

    /// <summary>Часть бойца, где больше всего вершин с главным суставом сегмента.</summary>
    private int PartOf(Fighter f, int segment)
    {
        int best = 0, most = -1;
        for (int p = 0; p < f.Parts.Count; p++)
        {
            int n = CountSegment(f.Parts[p].Mesh, segment);
            if (n > most) (most, best) = (n, p);
        }
        return best;
    }

    private int CountSegment(CutMesh mesh, int segment)
    {
        // вершины, на которые ссылаются треугольники части (у производных мешей вершины основы лежат и неиспользованными)
        var used = new bool[mesh.Vertices.Length];
        foreach (uint i in mesh.Indices) used[i] = true;
        int n = 0;
        for (int i = 0; i < used.Length; i++)
            if (used[i] && Dominant(mesh.Vertices[i]) is var j && (uint)j < (uint)_jointSegment.Length && _jointSegment[j] == segment) n++;
        return n;
    }

    private static int Dominant(in CutVertex v)
    {
        int best = 0;
        float max = -1;
        for (int k = 0; k < 4; k++)
            if (CutField.Weight(v, k) is var w && w > max) (max, best) = (w, k);
        return (int)(v.Joints >> (8 * best) & 0xFF);
    }

    /// <summary>Первый ребёнок сегмента по дереву тела; детей нет — сам сегмент.</summary>
    private static int FirstChild(int segment)
    {
        for (int s = 0; s < BodySegs.Count; s++)
            if (s != BodySegs.Pelvis && BodySegs.Parent(s) == segment) return s;
        return segment;
    }

    /// <summary>Сегмент и все его потомки по дереву тела (маска).</summary>
    public static ushort Branch(int segment)
    {
        ushort mask = 0;
        for (int s = 0; s < BodySegs.Count; s++)
            for (int k = s; k >= 0; k = BodySegs.Parent(k))
                if (k == segment)
                {
                    mask |= (ushort)(1 << s);
                    break;
                }
        return mask;
    }

    private void Apply(ulong key, Fighter f, Done done)
    {
        var old = f.Parts[done.Target];
        var next = new List<Part>(f.Parts);
        next.RemoveAt(done.Target);
        foreach (var (mesh, piece) in done.Parts)
        {
            if (mesh.Indices.Length == 0) continue;
            var part = new Part(mesh, Upload($"Cut.{key}", mesh), piece == -2 ? old.Piece : piece);
            if (piece == -2) next.Insert(done.Target, part);
            else next.Add(part);
        }
        if (old.Gpu != parts.BaseMesh) Release(old.Gpu);
        f.Parts.Clear();
        f.Parts.AddRange(next);
        var published = new SkinPart[f.Parts.Count];
        for (int i = 0; i < published.Length; i++) published[i] = new SkinPart(f.Parts[i].Gpu, f.Parts[i].Piece);
        parts.Set(key, published);
        f.Published = true;
        Cuts++;
    }

    /// <summary>
    /// Часть разреза — вершины входа на прежних местах (слой крови по номеру вершины), новые — в конце: сохранённые вершины в выходе резака
    /// идут в том же порядке и совпадают с входом побайтно.
    /// </summary>
    public static CutMesh Expand(CutMesh input, CutMesh output)
    {
        var src = input.Vertices;
        var dst = output.Vertices;
        var map = new uint[dst.Length];
        int i = 0, o = 0;
        for (; o < dst.Length; o++)
        {
            while (i < src.Length && !Same(src[i], dst[o])) i++;
            if (i == src.Length) break;
            map[o] = (uint)i++;
        }
        int added = dst.Length - o;
        var vertices = new CutVertex[src.Length + added];
        src.CopyTo(vertices, 0);
        for (int k = 0; k < added; k++)
        {
            vertices[src.Length + k] = dst[o + k];
            map[o + k] = (uint)(src.Length + k);
        }
        var indices = new uint[output.Indices.Length];
        for (int k = 0; k < indices.Length; k++) indices[k] = map[output.Indices[k]];
        return new CutMesh { Vertices = vertices, Indices = indices };
    }

    private static bool Same(in CutVertex a, in CutVertex b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<CutVertex>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<CutVertex>(in b)));

    private int Upload(string name, CutMesh mesh)
    {
        int id = _meshes!.Add(name, mesh);
        Meshes++;
        Bytes += _meshes.Bytes(id);
        return id;
    }

    private void Release(int mesh)
    {
        if (_meshes is null || mesh == parts.BaseMesh || mesh < 0) return;
        Bytes -= _meshes.Bytes(mesh);
        _meshes.Remove(mesh);
        Meshes--;
    }
}
