using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Formats;
using Sword.Engine.Rhi;

namespace Sword.Engine.Render;

/// <summary>Строки матрицы 3×4 для HLSL: world = p.x·R0.xyz + p.y·R1.xyz + p.z·R2.xyz + (R0.w, R1.w, R2.w).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuMat34
{
    public Vector4 R0, R1, R2;

    public static readonly GpuMat34 Identity = From(Matrix4x4.Identity);

    public static GpuMat34 From(in Matrix4x4 m) => new()
    {
        R0 = new Vector4(m.M11, m.M12, m.M13, m.M41),
        R1 = new Vector4(m.M21, m.M22, m.M23, m.M42),
        R2 = new Vector4(m.M31, m.M32, m.M33, m.M43),
    };
}

/// <summary>Экземпляр скинированного меша на GPU (<c>SkinInstance</c> в skin.hlsli).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuSkinInstance
{
    /// <summary>Первая матрица палитры экземпляра и их число (суставов меша).</summary>
    public uint Palette, Joints;

    /// <summary>Владелец (код игры, 0 — нет) и флаги — их читают шейдеры игры и модулей.</summary>
    public uint Owner, Flags;

    public Vector3 Colour;
    public float Glow;

    /// <summary>Первая вершина экземпляра в буфере вершин кадра (<see cref="SkinnedMeshes.Prepare"/>).</summary>
    public uint VertexBase;
    public uint Pad0, Pad1, Pad2;
}

/// <summary>Вершина экземпляра в мире (<c>SkinnedVertex</c> в skin.hlsli): точка и нормаль (не нормирована).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SkinnedVertex
{
    public Vector3 Position, Normal;
}

/// <summary>Часть меша, загруженного по секциям (<see cref="SkinnedMeshes.LoadMeshSections"/>): номер меша и имя материала секции.</summary>
public readonly record struct MeshPart(int Mesh, string Material);

/// <summary>
/// Скинированные меши: вершины и индексы — один раз в память GPU; каждый кадр в кольца пишутся палитры (мировые
/// матрицы скиннинга) и экземпляры, один <c>DrawIndexedInstanced</c> на меш. Скиннинг — common/skin.hlsli: с <see cref="Prepare"/> —
/// один раз за кадр вычислительным проходом <c>skin.vertices</c> в буфер вершин кадра (точка и нормаль в мире), проходы читают его;
/// без него — в вершинном шейдере каждого прохода.
/// </summary>
public sealed class SkinnedMeshes : IDisposable
{
    /// <summary>Матриц палитры и экземпляров на кадр.</summary>
    public const uint MaxPalette = 1 << 16, MaxInstances = 512;

    /// <summary>SRV «буфера вершин кадра нет» (<c>SKIN_NONE</c>): шейдер скинирует сам.</summary>
    public const uint None = uint.MaxValue;

    public const int SlotVerts = 13, SlotPalette = 14, SlotInstances = 15, SlotInstanceBase = 16, SlotPrevPalette = 17, SlotSkinned = 18,
        SlotVertexCount = 19;

    private static readonly ComputePipelineDesc Skinning = new("common/skin_vertices.hlsl", "CsSkin");

    private sealed class Mesh(string name, GpuData vertices, GpuIndices indices, GpuIndices? shadowIndices, int jointCount, Matrix4x4[] inverseBind,
        int vertexCount, Vector4 bounds, float skinMargin) : IDisposable
    {
        public readonly string Name = name;
        public readonly GpuData Vertices = vertices;
        public readonly GpuIndices Indices = indices;
        /// <summary>Меш теней — прореженный по тем же вершинам (<see cref="SkinnedMeshData.ShadowIndices"/>); null — нет.</summary>
        public readonly GpuIndices? ShadowIndices = shadowIndices;
        public readonly int JointCount = jointCount;
        /// <summary>Обратные bind-матрицы суставов (у статического меша — пусто).</summary>
        public readonly Matrix4x4[] InverseBind = inverseBind;
        public readonly int VertexCount = vertexCount;
        /// <summary>Шар вершин в осях меша (xyz — центр, w — радиус): границы жёсткого экземпляра.</summary>
        public readonly Vector4 Bounds = bounds;
        /// <summary>Наибольшее расстояние вершины от её главного сустава в позе привязки, м: запас шара позы к суставам.</summary>
        public readonly float SkinMargin = skinMargin;
        public readonly List<int> Pending = [];
        /// <summary>Первый экземпляр меша в буфере экземпляров кадра (порядок — по мешам).</summary>
        public int First;

        public long Bytes => (long)Vertices.SizeBytes + (long)Indices.Count * 4;

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
            ShadowIndices?.Dispose();
        }
    }

    private readonly Renderer _renderer;
    /// <summary>Меши по номерам; освобождённый производный — null (номер переиспользуется).</summary>
    private readonly List<Mesh?> _meshes = [];
    /// <summary>Загруженные по пути: ключ — нормализованный путь контента без учёта регистра, как ищет сам контент.</summary>
    private readonly Dictionary<string, int> _loaded = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Загруженные по секциям (<see cref="LoadMeshSections"/>): ключ — как у <see cref="_loaded"/>.</summary>
    private readonly Dictionary<string, MeshPart[]> _loadedParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameData<GpuMat34> _palette, _prevPalette;
    private readonly FrameData<GpuSkinInstance> _instances;
    private readonly GpuSkinInstance[] _cpuInstances = new GpuSkinInstance[MaxInstances];
    // Палитры кадра — сперва на CPU (в буфер кадра — Flush): палитра прошлого кадра того же ключа движения кладётся рядом.
    private GpuMat34[] _cpuPalette = new GpuMat34[MaxPalette], _lastPalette = new GpuMat34[MaxPalette];
    private readonly uint[] _motionKeys = new uint[MaxInstances];
    private Dictionary<uint, (int Offset, int Count)> _keys = new((int)MaxInstances), _lastKeys = new((int)MaxInstances);
    /// <summary>Шар экземпляра в мире (xyz — центр, w — радиус; бесконечный — границ нет) — отсев в <see cref="Record"/>.</summary>
    private readonly Vector4[] _bounds = new Vector4[MaxInstances];
    /// <summary>Экземпляр на месте i буфера экземпляров кадра (по мешам).</summary>
    private readonly int[] _order = new int[MaxInstances];
    private readonly Action<RenderPassContext> _recordSkinning;
    private int _paletteUsed, _instanceCount, _vertexTotal, _skinnedSrv = -1, _skinnedUav = -1;
    private bool _overflowReported, _laidOut;
    private RenderBuffer _skinned;

    internal SkinnedMeshes(Renderer renderer)
    {
        _renderer = renderer;
        _palette = renderer.CreateFrameData<GpuMat34>("Skin.Palette", MaxPalette);
        _prevPalette = renderer.CreateFrameData<GpuMat34>("Skin.PrevPalette", MaxPalette);
        _instances = renderer.CreateFrameData<GpuSkinInstance>("Skin.Instances", MaxInstances);
        _recordSkinning = RecordSkinning;
    }

    /// <summary>Скиннинг один раз за кадр (<see cref="Prepare"/>); выключено — в вершинном шейдере каждого прохода. Со следующего кадра.</summary>
    public bool PreSkin { get; set; } = true;

    /// <summary>
    /// Проходы теней (<see cref="Record"/> с <c>shadow</c>) рисуют меш теней, если он есть (прореженный по тем же вершинам); выключено —
    /// полный меш. Со следующего кадра.
    /// </summary>
    public bool ShadowMeshes { get; set; } = true;

    /// <summary>Экземпляров, пропущенных отсевом (<see cref="Record"/> с границами), за последний кадр — сумма по проходам.</summary>
    public int Culled { get; private set; }

    private int _culled;

    /// <summary>Числа скиннинга для шейдеров.</summary>
    public static void Contribute(ShaderContract c)
    {
        c.Section("скиннинг (Sword.Engine.Render.SkinnedMeshes)");
        c.AddSlot("R_SKINVERTS", SlotVerts, "вершины меша");
        c.AddSlot("R_SKINPAL", SlotPalette, "палитры");
        c.AddSlot("R_SKININST", SlotInstances, "экземпляры");
        c.AddSlot("R_SKINBASE", SlotInstanceBase, "первый экземпляр вызова");
        c.AddSlot("R_SKINPREV", SlotPrevPalette, "палитры прошлого кадра (то же место, что у этого)");
        c.AddSlot("R_SKINNED", SlotSkinned, "буфер вершин кадра: SRV в проходах, UAV в skin.vertices; SKIN_NONE - нет");
        c.AddSlot("R_SKINCOUNT", SlotVertexCount, "вершин меша (skin.vertices)");
        c.Add("SKIN_NONE", None, "буфера вершин кадра нет - скиннинг в вершинном шейдере");
        c.AddSize<SkinVertex>("SKIN_VERTEX_SIZE");
        c.AddSize<GpuMat34>("SKIN_MAT_SIZE");
        c.AddSize<GpuSkinInstance>("SKIN_INSTANCE_SIZE");
        c.AddSize<SkinnedVertex>("SKINNED_VERTEX_SIZE");
    }

    /// <summary>
    /// Меш из контента по пути (.swmesh); вернёт номер для <see cref="Add"/>. Повторная загрузка того же файла
    /// (путь без учёта регистра и разделителей) — тот же номер.
    /// </summary>
    /// <exception cref="ArgumentException">Путь не путь контента.</exception>
    /// <exception cref="FileNotFoundException">Нет пака или ассета.</exception>
    /// <exception cref="InvalidDataException">Файл не .swmesh или испорчен.</exception>
    public int LoadMesh(string name, string path)
    {
        path = ContentPath.Normalize(path);
        if (_loaded.TryGetValue(path, out int id)) return id;
        var content = _renderer.Content;
        SkinnedMeshData data;
        using (var s = content.OpenRead(path))
            data = SkinnedMeshData.Read(s, content.Describe(path));
        id = AddMesh(name, data);
        _loaded[path] = id;
        Log.Info("skin", $"{name}: {content.Describe(path)} — вершин {data.Vertices.Length}, треугольников " +
            $"{data.Indices.Length / 3}, суставов {data.JointCount}");
        return id;
    }

    /// <summary>
    /// Меш по секциям материалов: по мешу на секцию (вершины целиком, индексы секции), цвет — у экземпляра части.
    /// Меш без секций — одна часть без имени материала. Вершины копируются в каждую часть — для небольших мешей.
    /// </summary>
    /// <exception cref="ArgumentException">Путь не путь контента.</exception>
    /// <exception cref="FileNotFoundException">Нет пака или ассета.</exception>
    /// <exception cref="InvalidDataException">Файл не .swmesh или испорчен.</exception>
    public IReadOnlyList<MeshPart> LoadMeshSections(string name, string path)
    {
        path = ContentPath.Normalize(path);
        if (_loadedParts.TryGetValue(path, out var loaded)) return loaded;
        var content = _renderer.Content;
        SkinnedMeshData data;
        using (var s = content.OpenRead(path))
            data = SkinnedMeshData.Read(s, content.Describe(path));
        MeshPart[] parts;
        if (data.Sections.Length == 0) parts = [new MeshPart(AddMesh(name, data), "")];
        else
        {
            parts = new MeshPart[data.Sections.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var section = data.Sections[i];
                var part = new SkinnedMeshData
                {
                    Vertices = data.Vertices, Indices = data.Indices.AsSpan(section.Start, section.Count).ToArray(),
                    JointNames = data.JointNames, JointParents = data.JointParents, InverseBind = data.InverseBind,
                };
                parts[i] = new MeshPart(AddMesh($"{name}.{section.Material}", part), section.Material);
            }
        }
        _loadedParts[path] = parts;
        Log.Info("skin", $"{name}: {content.Describe(path)} — вершин {data.Vertices.Length}, треугольников {data.Indices.Length / 3}, " +
            $"частей {parts.Length} ({string.Join(", ", parts.Select(p => p.Material.Length > 0 ? p.Material : "—"))})");
        return parts;
    }

    /// <summary>
    /// Меш из контента на CPU (тот же файл, что <see cref="LoadMesh"/>; читается заново, в GPU не идёт) — геометрия кожи для эффектов.
    /// </summary>
    /// <exception cref="ArgumentException">Путь не путь контента.</exception>
    /// <exception cref="FileNotFoundException">Нет пака или ассета.</exception>
    /// <exception cref="InvalidDataException">Файл не .swmesh или испорчен.</exception>
    public SkinnedMeshCpu ReadCpu(string path)
    {
        path = ContentPath.Normalize(path);
        var content = _renderer.Content;
        SkinnedMeshData data;
        using (var s = content.OpenRead(path))
            data = SkinnedMeshData.Read(s, content.Describe(path));
        var v = data.Vertices;
        var positions = new Vector3[v.Length];
        var normals = new Vector3[v.Length];
        var joints = new uint[v.Length];
        var weights = new Vector4[v.Length];
        var uvs = new Vector2[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            uvs[i] = v[i].Uv;
            positions[i] = v[i].Position;
            normals[i] = v[i].Normal;
            joints[i] = v[i].Joints;
            var w = v[i].Weights;
            weights[i] = new Vector4(w, Math.Clamp(1 - w.X - w.Y - w.Z, 0, 1));
        }
        return new SkinnedMeshCpu
        {
            Positions = positions, Normals = normals, Uvs = uvs, Indices = data.Indices, Joints = joints, Weights = weights,
            InverseBind = data.InverseBind, JointNames = data.JointNames,
        };
    }

    /// <summary>Готовый меш в память GPU (сгенерированные меши движка); вернёт номер для <see cref="Add"/>.</summary>
    internal int AddMesh(string name, SkinnedMeshData data)
    {
        var mesh = new Mesh(name,
            _renderer.CreateData<SkinVertex>(name + ".Vertices", data.Vertices),
            _renderer.CreateIndices(name + ".Indices", data.Indices),
            data.ShadowIndices.Length > 0 ? _renderer.CreateIndices(name + ".ShadowIndices", data.ShadowIndices) : null,
            Math.Max(data.JointCount, 1), data.InverseBind, data.Vertices.Length, BoundsOf(data.Vertices), SkinMarginOf(data));
        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    /// <summary>
    /// Производный меш (разрез): вершины — записи вершины скина по 48 байт (позиция, нормаль, UV, 4 сустава, 3 веса — как в .swmesh),
    /// индексы — треугольники; суставы и обратные bind-матрицы — меша <paramref name="baseMesh"/>. Вернёт номер для <see cref="AddPosed"/>;
    /// освобождает <see cref="RemoveMesh"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Вершин не целое число записей, индекс вне вершин или треугольник неполный.</exception>
    public int AddDerivedMesh(string name, int baseMesh, ReadOnlySpan<byte> vertices, ReadOnlySpan<uint> indices)
    {
        if (vertices.Length % SkinVertex.Size != 0) throw new ArgumentException($"вершин {vertices.Length} байт — не кратно {SkinVertex.Size}", nameof(vertices));
        if (indices.Length % 3 != 0 || indices.Length == 0) throw new ArgumentException($"индексов {indices.Length} — не треугольники", nameof(indices));
        int count = vertices.Length / SkinVertex.Size;
        foreach (uint i in indices)
            if (i >= (uint)count) throw new ArgumentException($"индекс {i} вне {count} вершин", nameof(indices));
        var source = _meshes[baseMesh] ?? throw new ArgumentException($"меша {baseMesh} нет", nameof(baseMesh));
        var data = MemoryMarshal.Cast<byte, SkinVertex>(vertices);
        var array = data.ToArray();
        var mesh = new Mesh(name, _renderer.CreateData<SkinVertex>(name + ".Vertices", array), _renderer.CreateIndices(name + ".Indices", indices.ToArray()),
            null, source.JointCount, source.InverseBind, count, BoundsOf(array), source.SkinMargin);
        int free = _meshes.IndexOf(null);
        if (free >= 0)
        {
            _meshes[free] = mesh;
            return free;
        }
        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    /// <summary>Освободить меш (производный): буферы GPU — после кадров, которые его ещё рисуют; номер освобождается для новых.</summary>
    public void RemoveMesh(int mesh)
    {
        var m = _meshes[mesh];
        if (m is null) return;
        _meshes[mesh] = null;
        m.Dispose();   // буферы сами откладывают освобождение до забора кадров, которые их рисуют
    }

    /// <summary>Байт GPU-памяти меша (вершины и индексы); нет меша — 0.</summary>
    public long MeshBytes(int mesh) => _meshes[mesh] is { } m ? m.Bytes : 0;

    /// <summary>Шар вершин: центр рамки, радиус — до самой дальней вершины.</summary>
    private static Vector4 BoundsOf(SkinVertex[] vertices)
    {
        if (vertices.Length == 0) return Vector4.Zero;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var v in vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        var center = (min + max) * 0.5f;
        float radius = 0;
        foreach (var v in vertices) radius = MathF.Max(radius, Vector3.Distance(v.Position, center));
        return new Vector4(center, radius);
    }

    /// <summary>Наибольшее расстояние вершины от её главного сустава (наибольший вес) в позе привязки; без суставов — 0.</summary>
    private static float SkinMarginOf(SkinnedMeshData data)
    {
        var inv = data.InverseBind;
        if (inv.Length == 0) return 0;
        var joints = new Vector3[inv.Length];
        for (int j = 0; j < inv.Length; j++) joints[j] = Matrix4x4.Invert(inv[j], out var bind) ? bind.Translation : Vector3.Zero;
        float margin = 0;
        foreach (var v in data.Vertices)
        {
            var w = v.Weights;
            float w3 = 1 - w.X - w.Y - w.Z;
            int k = w.X >= w.Y && w.X >= w.Z && w.X >= w3 ? 0 : w.Y >= w.Z && w.Y >= w3 ? 1 : w.Z >= w3 ? 2 : 3;
            int j = Math.Min((int)((v.Joints >> (8 * k)) & 0xFF), inv.Length - 1);
            margin = MathF.Max(margin, Vector3.Distance(v.Position, joints[j]));
        }
        return margin;
    }

    public int JointCount(int mesh) => _meshes[mesh]!.JointCount;

    /// <summary>Начало кадра: экземпляры прошлого кадра забыты.</summary>
    public void Begin()
    {
        _paletteUsed = _instanceCount = _vertexTotal = 0;
        _laidOut = false;
        _skinnedSrv = _skinnedUav = -1;
        Culled = _culled;
        _culled = 0;
        foreach (var m in _meshes) m?.Pending.Clear();
    }

    /// <summary>
    /// Экземпляр меша; вернёт палитру для заполнения (матрица на сустав меша: InverseBind·Component·World) или пусто,
    /// если места в кадре нет.
    /// </summary>
    /// <remarks>
    /// <paramref name="motionKey"/> — ключ движения экземпляра (одинаковый из кадра в кадр, 0 — нет): палитра прошлого кадра с тем же
    /// ключом и числом суставов — для скоростей (<c>SkinPreviousPoint</c>); без пары — палитра этого кадра (движется только камера).
    /// </remarks>
    public Span<GpuMat34> Add(int mesh, Vector3 colour, uint owner = 0, float glow = 0, uint flags = 0, uint motionKey = 0)
    {
        if (_laidOut) throw new InvalidOperationException("скин: экземпляр после Prepare — буфер вершин кадра уже размечен");
        var m = _meshes[mesh] ?? throw new ArgumentException($"меша {mesh} нет (освобождён)", nameof(mesh));
        if (_instanceCount >= MaxInstances || _paletteUsed + m.JointCount > MaxPalette)
        {
            if (!_overflowReported)
            {
                _overflowReported = true;
                Log.Warning("skin", $"кадру не хватило места: экземпляров {_instanceCount}/{MaxInstances}, " +
                    $"палитра {_paletteUsed}/{MaxPalette} — лишнее не рисуется");
            }
            return default;
        }
        _cpuInstances[_instanceCount] = new GpuSkinInstance
        {
            Palette = (uint)_paletteUsed, Joints = (uint)m.JointCount, Owner = owner, Flags = flags, Colour = colour, Glow = glow,
        };
        _motionKeys[_instanceCount] = motionKey;
        _bounds[_instanceCount] = new Vector4(Vector3.Zero, float.PositiveInfinity);   // палитру заполняет вызывающий — границ нет
        if (motionKey != 0) _keys.TryAdd(motionKey, (_paletteUsed, m.JointCount));
        m.Pending.Add(_instanceCount++);
        var span = _cpuPalette.AsSpan(_paletteUsed, m.JointCount);
        _paletteUsed += m.JointCount;
        return span;
    }

    /// <summary>
    /// Экземпляр меша в позе: <paramref name="model"/> — модельные матрицы суставов (вектор слева), палитра —
    /// InverseBind·Model·World. false — места в кадре нет.
    /// </summary>
    /// <exception cref="ArgumentException">Суставов в позе не столько, сколько в меше.</exception>
    public bool AddPosed(int mesh, ReadOnlySpan<Matrix4x4> model, in Matrix4x4 world, Vector3 colour, uint owner = 0, float glow = 0, uint flags = 0,
        uint motionKey = 0)
    {
        var m = _meshes[mesh] ?? throw new ArgumentException($"меша {mesh} нет (освобождён)", nameof(mesh));
        if (model.Length != m.InverseBind.Length)
            throw new ArgumentException($"{m.Name}: суставов в позе {model.Length}, в меше {m.InverseBind.Length}", nameof(model));
        var palette = Add(mesh, colour, owner, glow, flags, motionKey);
        if (palette.IsEmpty) return false;
        var inv = m.InverseBind;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int j = 0; j < inv.Length; j++)
        {
            palette[j] = GpuMat34.From(inv[j] * model[j] * world);
            var joint = Vector3.Transform(model[j].Translation, world);
            min = Vector3.Min(min, joint);
            max = Vector3.Max(max, joint);
        }
        // Шар суставов позы с запасом меша: вершина не дальше SkinMargin от своего главного сустава.
        if (inv.Length > 0) _bounds[_instanceCount - 1] = new Vector4((min + max) * 0.5f, Vector3.Distance(min, max) * 0.5f + m.SkinMargin);
        return true;
    }

    /// <summary>
    /// Экземпляр статического меша (оружие, реквизит): палитра из одной матрицы — мир меша. false — места в кадре нет.
    /// </summary>
    /// <exception cref="ArgumentException">У меша есть суставы: его рисуют позой (<see cref="AddPosed"/>).</exception>
    public bool AddRigid(int mesh, in Matrix4x4 world, Vector3 colour, uint owner = 0, float glow = 0, uint flags = 0, uint motionKey = 0)
    {
        var m = _meshes[mesh] ?? throw new ArgumentException($"меша {mesh} нет (освобождён)", nameof(mesh));
        if (m.InverseBind.Length != 0)
            throw new ArgumentException($"{m.Name}: суставов {m.InverseBind.Length} — жёстко вкладывается только статический меш", nameof(mesh));
        var palette = Add(mesh, colour, owner, glow, flags, motionKey);
        if (palette.IsEmpty) return false;
        palette[0] = GpuMat34.From(world);
        // Шар меша в мире: центр — образ центра, радиус — с наибольшим растяжением осей.
        var b = m.Bounds;
        float scale = MathF.Sqrt(MathF.Max(new Vector3(world.M11, world.M12, world.M13).LengthSquared(),
            MathF.Max(new Vector3(world.M21, world.M22, world.M23).LengthSquared(), new Vector3(world.M31, world.M32, world.M33).LengthSquared())));
        _bounds[_instanceCount - 1] = new Vector4(Vector3.Transform(new Vector3(b.X, b.Y, b.Z), world), b.W * scale);
        return true;
    }

    /// <summary>
    /// Скиннинг кадра одним проходом: после всех экземпляров кадра и до проходов, которые их рисуют (они читают буфер —
    /// <see cref="ReadBy"/>). Проход <c>skin.vertices</c> (фаза Simulation) пишет точку и нормаль каждой вершины каждого экземпляра в
    /// буфер вершин кадра; <see cref="Record"/> отдаёт его шейдерам (<c>SkinWorld</c>). Экземпляры после — ошибка. Выключено
    /// (<see cref="PreSkin"/>) или экземпляров нет — ничего.
    /// </summary>
    public void Prepare(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Layout();
        if (!PreSkin || _vertexTotal == 0) return;
        var g = frame.Graph;
        // Ёмкость — степенью двойки: буфер из пула графа переживает рождение и гибель бойцов без новых выделений.
        _skinned = g.CreateBuffer("skin.vertices", (uint)Marshal.SizeOf<SkinnedVertex>(), BitOperations.RoundUpToPowerOf2((uint)_vertexTotal));
        _skinnedSrv = g.SrvOf(_skinned);
        _skinnedUav = g.UavOf(_skinned);
        g.AddComputePass("skin.vertices", FramePhase.Simulation).Write(_skinned).Execute(_recordSkinning);
    }

    /// <summary>Проход рисует экземпляры из буфера вершин кадра (<see cref="Prepare"/> в этом кадре) — чтение для графа; без него — ничего.</summary>
    public void ReadBy(RenderGraph.RenderPassBuilder pass)
    {
        if (_skinnedSrv >= 0) pass.Read(_skinned);
    }

    /// <summary>Разметка кадра: экземпляры по мешам (порядок буфера экземпляров), первая вершина каждого в буфере вершин кадра.</summary>
    private void Layout()
    {
        if (_laidOut) return;
        _laidOut = true;
        int written = 0, vertices = 0;
        foreach (var m in _meshes)
        {
            if (m is null) continue;   // освобождённый производный
            m.First = written;
            foreach (int i in m.Pending)
            {
                _order[written++] = i;
                _cpuInstances[i].VertexBase = (uint)vertices;
                vertices += m.VertexCount;
            }
        }
        _vertexTotal = vertices;
    }

    private void RecordSkinning(RenderPassContext ctx)
    {
        ctx.SetPipeline(Skinning);
        ctx.SetConstant(SlotPalette, _palette.Srv);
        ctx.SetConstant(SlotInstances, _instances.Srv);
        ctx.SetConstant(SlotSkinned, _skinnedUav);
        foreach (var m in _meshes)
        {
            if (m is null || m.Pending.Count == 0) continue;
            ctx.SetConstant(SlotVerts, m.Vertices.Srv);
            ctx.SetConstant(SlotInstanceBase, m.First);
            ctx.SetConstant(SlotVertexCount, m.VertexCount);
            ctx.Dispatch((uint)(m.VertexCount + 63) / 64, (uint)m.Pending.Count);
        }
    }

    /// <summary>
    /// Нарисовать всё добавленное конвейером <paramref name="pipeline"/> (вершинный шейдер — через skin.hlsli). <paramref name="visible"/> —
    /// отсев по шару экземпляра (xyz — центр, w — радиус; бесконечный — у экземпляра без границ): пропущенные не рисуются, подряд идущие
    /// видимые — одним вызовом. <paramref name="shadow"/> — проход теней: меш теней вместо полного, где он есть (<see cref="ShadowMeshes"/>).
    /// </summary>
    public void Record(RenderPassContext ctx, in GraphicsPipelineDesc pipeline, Func<Vector4, bool>? visible = null, bool shadow = false)
    {
        if (_instanceCount == 0) return;
        ctx.SetPipeline(pipeline);
        ctx.SetConstant(SlotPalette, _palette.Srv);
        ctx.SetConstant(SlotPrevPalette, _prevPalette.Srv);
        ctx.SetConstant(SlotInstances, _instances.Srv);
        ctx.SetConstant(SlotSkinned, _skinnedSrv >= 0 ? (uint)_skinnedSrv : None);
        foreach (var m in _meshes)
        {
            if (m is null) continue;
            int count = m.Pending.Count;
            if (count == 0) continue;
            ctx.SetConstant(SlotVerts, m.Vertices.Srv);
            var indices = shadow && ShadowMeshes && m.ShadowIndices is { } lod ? lod : m.Indices;
            if (visible is null)
            {
                ctx.SetConstant(SlotInstanceBase, m.First);
                ctx.DrawIndexed(indices, (uint)count);
                continue;
            }
            RecordVisible(ctx, m, indices, visible);
        }
    }

    /// <summary>Видимые экземпляры меша — вызовами по подряд идущим.</summary>
    private void RecordVisible(RenderPassContext ctx, Mesh m, GpuIndices indices, Func<Vector4, bool> visible)
    {
        int count = m.Pending.Count, run = -1;
        for (int k = 0; k <= count; k++)
        {
            bool shown = k < count && visible(_bounds[_order[m.First + k]]);
            if (k < count && !shown) _culled++;
            if (shown && run < 0) run = k;
            else if (!shown && run >= 0)
            {
                ctx.SetConstant(SlotInstanceBase, m.First + run);
                ctx.DrawIndexed(indices, (uint)(k - run));
                run = -1;
            }
        }
    }

    /// <summary>
    /// После построения кадра: экземпляры (по мешам) и палитры в буферы кадра, рядом — палитры прошлого кадра по ключам движения; этот
    /// кадр становится прошлым.
    /// </summary>
    internal void Flush()
    {
        Layout();
        var gpu = _instances.Slot;
        for (int k = 0; k < _instanceCount; k++) gpu[k] = _cpuInstances[_order[k]];
        var current = _palette.Slot;
        var previous = _prevPalette.Slot;
        _cpuPalette.AsSpan(0, _paletteUsed).CopyTo(current);
        for (int i = 0; i < _instanceCount; i++) CopyPrevious(i, previous);
        (_cpuPalette, _lastPalette) = (_lastPalette, _cpuPalette);
        (_keys, _lastKeys) = (_lastKeys, _keys);
        _keys.Clear();
    }

    private void CopyPrevious(int instance, Span<GpuMat34> previous)
    {
        ref readonly var inst = ref _cpuInstances[instance];
        int offset = (int)inst.Palette, count = (int)inst.Joints;
        uint key = _motionKeys[instance];
        var source = key != 0 && _lastKeys.TryGetValue(key, out var last) && last.Count == count
            ? _lastPalette.AsSpan(last.Offset, count)
            : _cpuPalette.AsSpan(offset, count);
        source.CopyTo(previous.Slice(offset, count));
    }

    public void Dispose()
    {
        foreach (var m in _meshes) m?.Dispose();
        _palette.Dispose();
        _prevPalette.Dispose();
        _instances.Dispose();
    }
}
