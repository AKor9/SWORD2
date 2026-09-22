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

    /// <summary>Владелец (сущность) и флаги — их читают шейдеры игры и модулей.</summary>
    public uint Owner, Flags;

    public Vector3 Colour;
    public float Glow;
}

/// <summary>
/// Скинированные меши — движковое подмножество <c>Render/SkinnedRenderer.cs</c> SWORD 2eab13b (точка 1б): вершины
/// и индексы меша — один раз в память GPU; каждый кадр в кольца пишутся палитры (мировые матрицы скиннинга, уже
/// интерполированные на CPU) и экземпляры; один <c>DrawIndexedInstanced</c> на меш. Разрезы, материалы секций,
/// модули поверхности придут со своими фичами.
///
/// Слоты корневых констант и раскладки — <see cref="Contribute"/>; скиннинг в шейдере — common/skin.hlsli.
/// </summary>
public sealed class SkinnedMeshes : IDisposable
{
    /// <summary>Матриц палитры и экземпляров на кадр (как в SWORD: 33 бойца с мечами и ~370 частей Manny).</summary>
    public const uint MaxPalette = 1 << 16, MaxInstances = 512;

    public const int SlotVerts = 13, SlotPalette = 14, SlotInstances = 15, SlotInstanceBase = 16;

    private sealed class Mesh(string name, GpuData vertices, GpuIndices indices, int jointCount, Matrix4x4[] inverseBind) : IDisposable
    {
        public readonly string Name = name;
        public readonly GpuData Vertices = vertices;
        public readonly GpuIndices Indices = indices;
        public readonly int JointCount = jointCount;
        /// <summary>Обратные bind-матрицы суставов (у статического меша — пусто).</summary>
        public readonly Matrix4x4[] InverseBind = inverseBind;
        public readonly List<int> Pending = [];

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
        }
    }

    private readonly Renderer _renderer;
    private readonly List<Mesh> _meshes = [];
    /// <summary>Загруженные по пути: ключ — нормализованный путь контента без учёта регистра, как ищет сам контент.</summary>
    private readonly Dictionary<string, int> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameData<GpuMat34> _palette;
    private readonly FrameData<GpuSkinInstance> _instances;
    private readonly GpuSkinInstance[] _cpuInstances = new GpuSkinInstance[MaxInstances];
    private int _paletteUsed, _instanceCount;
    private bool _overflowReported;

    internal SkinnedMeshes(Renderer renderer)
    {
        _renderer = renderer;
        _palette = renderer.CreateFrameData<GpuMat34>("Skin.Palette", MaxPalette);
        _instances = renderer.CreateFrameData<GpuSkinInstance>("Skin.Instances", MaxInstances);
    }

    /// <summary>Числа скиннинга для шейдеров.</summary>
    public static void Contribute(ShaderContract c)
    {
        c.Section("скиннинг (Sword.Engine.Render.SkinnedMeshes)");
        c.AddSlot("R_SKINVERTS", SlotVerts, "вершины меша");
        c.AddSlot("R_SKINPAL", SlotPalette, "палитры");
        c.AddSlot("R_SKININST", SlotInstances, "экземпляры");
        c.AddSlot("R_SKINBASE", SlotInstanceBase, "первый экземпляр вызова");
        c.AddSize<SkinVertex>("SKIN_VERTEX_SIZE");
        c.AddSize<GpuMat34>("SKIN_MAT_SIZE");
        c.AddSize<GpuSkinInstance>("SKIN_INSTANCE_SIZE");
    }

    /// <summary>Экземпляров в текущем кадре.</summary>
    public int InstanceCount => _instanceCount;

    /// <summary>
    /// Меш из контента по пути ассета (.swmesh, например «characters/manny/mesh.swmesh»): прочитать, проверить,
    /// загрузить в память GPU; вернёт номер для <see cref="Add"/>. Повторная загрузка того же файла — тот же номер, как бы
    /// ни был записан путь («A\b.swmesh», «a/B.swmesh»).
    /// Формат файла знает только рендер: вызывающему он не нужен.
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
    /// Готовый меш в память GPU (сгенерированные и производные меши движка); вернёт номер для <see cref="Add"/>.
    /// Снаружи меши грузят по пути — <see cref="LoadMesh"/>: формат меша наружу не выходит.
    /// </summary>
    internal int AddMesh(string name, SkinnedMeshData data)
    {
        var mesh = new Mesh(name,
            _renderer.CreateData<SkinVertex>(name + ".Vertices", data.Vertices),
            _renderer.CreateIndices(name + ".Indices", data.Indices),
            Math.Max(data.JointCount, 1), data.InverseBind);
        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    public int JointCount(int mesh) => _meshes[mesh].JointCount;

    /// <summary>Начало кадра: экземпляры прошлого кадра забыты.</summary>
    public void Begin()
    {
        _paletteUsed = _instanceCount = 0;
        foreach (var m in _meshes) m.Pending.Clear();
    }

    /// <summary>
    /// Экземпляр меша; вернёт палитру для заполнения (матрица на сустав меша: InverseBind·Component·World) или пусто,
    /// если места в кадре нет.
    /// </summary>
    public Span<GpuMat34> Add(int mesh, Vector3 colour, uint owner = 0, float glow = 0, uint flags = 0)
    {
        var m = _meshes[mesh];
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
        m.Pending.Add(_instanceCount++);
        var span = _palette.Slot.Slice(_paletteUsed, m.JointCount);
        _paletteUsed += m.JointCount;
        return span;
    }

    /// <summary>
    /// Экземпляр скинированного меша в позе: <paramref name="model"/> — модельные матрицы суставов (вектор слева, порядок
    /// суставов меша), <paramref name="world"/> — мир экземпляра; палитра — InverseBind·Model·World
    /// (<c>SkinnedCharacters.AddInstance</c> SWORD). Обратные bind-матрицы знает только рендер. false — места в кадре нет.
    /// </summary>
    /// <exception cref="ArgumentException">Суставов в позе не столько, сколько в меше.</exception>
    public bool AddPosed(int mesh, ReadOnlySpan<Matrix4x4> model, in Matrix4x4 world, Vector3 colour, uint owner = 0, float glow = 0, uint flags = 0)
    {
        var m = _meshes[mesh];
        if (model.Length != m.InverseBind.Length)
            throw new ArgumentException($"{m.Name}: суставов в позе {model.Length}, в меше {m.InverseBind.Length}", nameof(model));
        var palette = Add(mesh, colour, owner, glow, flags);
        if (palette.IsEmpty) return false;
        var inv = m.InverseBind;
        for (int j = 0; j < inv.Length; j++) palette[j] = GpuMat34.From(inv[j] * model[j] * world);
        return true;
    }

    /// <summary>Нарисовать всё добавленное конвейером <paramref name="pipeline"/> (вершинный шейдер — через skin.hlsli).</summary>
    public void Record(RenderPassContext ctx, in GraphicsPipelineDesc pipeline)
    {
        if (_instanceCount == 0) return;
        // экземпляры в кольце — подряд по мешам, чтобы каждому мешу хватило одного вызова
        var gpu = _instances.Slot;
        int written = 0;
        ctx.SetPipeline(pipeline);
        ctx.SetConstant(SlotPalette, _palette.Srv);
        ctx.SetConstant(SlotInstances, _instances.Srv);
        foreach (var m in _meshes)
        {
            if (m.Pending.Count == 0) continue;
            int start = written;
            foreach (int i in m.Pending) gpu[written++] = _cpuInstances[i];
            ctx.SetConstant(SlotVerts, m.Vertices.Srv);
            ctx.SetConstant(SlotInstanceBase, start);
            ctx.DrawIndexed(m.Indices, (uint)m.Pending.Count);
        }
    }

    public void Dispose()
    {
        foreach (var m in _meshes) m.Dispose();
        _palette.Dispose();
        _instances.Dispose();
    }
}
