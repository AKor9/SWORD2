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

    /// <summary>
    /// Владелец (код игры, 0 — нет: ID-буфер снимка — <see cref="SkinnedMeshes.IdOf"/>) и флаги — их читают шейдеры игры и
    /// модулей.
    /// </summary>
    public uint Owner, Flags;

    public Vector3 Colour;
    public float Glow;
}

/// <summary>Часть меша, загруженного по секциям (<see cref="SkinnedMeshes.LoadMeshSections"/>): номер меша и имя материала секции.</summary>
public readonly record struct MeshPart(int Mesh, string Material);

/// <summary>
/// Скинированные меши — движковое подмножество <c>Render/SkinnedRenderer.cs</c> SWORD 2eab13b: вершины
/// и индексы меша — один раз в память GPU; каждый кадр в кольца пишутся палитры (мировые матрицы скиннинга, уже
/// интерполированные на CPU) и экземпляры; один <c>DrawIndexedInstanced</c> на меш. Меш с секциями разного цвета — по мешу на
/// секцию (<see cref="LoadMeshSections"/>). Разрезы, материалы секций с текстурами, модули поверхности придут со своими фичами.
///
/// Слоты корневых констант и раскладки — <see cref="Contribute"/>; скиннинг в шейдере — common/skin.hlsli.
/// </summary>
public sealed class SkinnedMeshes : IDisposable
{
    /// <summary>Матриц палитры и экземпляров на кадр (как в SWORD: 33 бойца с мечами и ~370 частей Manny).</summary>
    public const uint MaxPalette = 1 << 16, MaxInstances = 512;

    public const int SlotVerts = 13, SlotPalette = 14, SlotInstances = 15, SlotInstanceBase = 16, SlotJointParts = 17;

    /// <summary>
    /// ID-буфер снимка (раздел 8 плана, диагностический режим): код пикселя — <c>владелец &lt;&lt; IdPartBits | часть</c>
    /// (<see cref="IdOf"/>); владелец — <see cref="GpuSkinInstance.Owner"/> экземпляра, часть — по суставу вершины с наибольшим
    /// весом (<see cref="SetJointParts"/>). 0 — не скинированный меш или экземпляр без владельца.
    /// </summary>
    public const int IdPartBits = 8;

    /// <summary>Часть сустава, у которого части нет (меш без таблицы частей).</summary>
    public const uint NoPart = (1u << IdPartBits) - 1;

    /// <summary>
    /// Наибольший владелец, который помещается в код ID-буфера (2²⁴ − 1): старшие биты больших при сдвиге теряются, и код
    /// совпал бы с кодом другого владельца — такому экземпляру владелец не даётся (0).
    /// </summary>
    public const uint MaxOwner = (1u << (32 - IdPartBits)) - 1;

    /// <summary>Код пикселя ID-буфера: владелец (1 … <see cref="MaxOwner"/>) и часть (0 … <see cref="NoPart"/>).</summary>
    public static uint IdOf(uint owner, uint part) => owner == 0 ? 0 : owner << IdPartBits | (part & NoPart);

    /// <summary>Владелец и часть кода пикселя ID-буфера (0 — ничего).</summary>
    public static (uint Owner, uint Part) SplitId(uint id) => (id >> IdPartBits, id & NoPart);

    private sealed class Mesh(string name, GpuData vertices, GpuIndices indices, int jointCount, Matrix4x4[] inverseBind, GpuData parts) : IDisposable
    {
        public readonly string Name = name;
        public readonly GpuData Vertices = vertices;
        public readonly GpuIndices Indices = indices;
        public readonly int JointCount = jointCount;
        /// <summary>Обратные bind-матрицы суставов (у статического меша — пусто).</summary>
        public readonly Matrix4x4[] InverseBind = inverseBind;
        /// <summary>Часть на сустав для ID-буфера (<see cref="SetJointParts"/>; по умолчанию — <see cref="NoPart"/>).</summary>
        public GpuData Parts = parts;
        public readonly List<int> Pending = [];

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
            Parts.Dispose();
        }
    }

    private readonly Renderer _renderer;
    private readonly List<Mesh> _meshes = [];
    /// <summary>Загруженные по пути: ключ — нормализованный путь контента без учёта регистра, как ищет сам контент.</summary>
    private readonly Dictionary<string, int> _loaded = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Загруженные по секциям (<see cref="LoadMeshSections"/>): ключ — как у <see cref="_loaded"/>.</summary>
    private readonly Dictionary<string, MeshPart[]> _loadedParts = new(StringComparer.OrdinalIgnoreCase);
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
        c.AddSlot("R_SKINPARTS", SlotJointParts, "часть тела на сустав меша (ID-буфер)");
        c.Add("SKIN_ID_PART_BITS", IdPartBits, "ID-буфер: код = владелец << бит | часть");
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
    /// Меш из контента по секциям материалов (оружие и реквизит, у которых части разного цвета): по мешу на секцию — вершины
    /// меша целиком, индексы секции; цвет — у экземпляра каждой части (<see cref="Add"/>, <see cref="AddRigid"/>). Меш без секций —
    /// одна часть без имени материала. Вершины копируются в каждую часть — для небольших мешей. Повторная загрузка того же файла —
    /// те же номера.
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
    /// Готовый меш в память GPU (сгенерированные и производные меши движка); вернёт номер для <see cref="Add"/>.
    /// Снаружи меши грузят по пути — <see cref="LoadMesh"/>: формат меша наружу не выходит.
    /// </summary>
    internal int AddMesh(string name, SkinnedMeshData data)
    {
        int joints = Math.Max(data.JointCount, 1);
        var parts = new uint[joints];
        Array.Fill(parts, NoPart);
        var mesh = new Mesh(name,
            _renderer.CreateData<SkinVertex>(name + ".Vertices", data.Vertices),
            _renderer.CreateIndices(name + ".Indices", data.Indices),
            joints, data.InverseBind, _renderer.CreateData<uint>(name + ".JointParts", parts));
        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    public int JointCount(int mesh) => _meshes[mesh].JointCount;

    /// <summary>
    /// Часть на каждый сустав меша для ID-буфера (у бойца — сегмент тела; 0 … <see cref="NoPart"/>): пиксель скинированного
    /// меша получает часть сустава своей вершины с наибольшим весом. На старте, не в кадре (загрузка в память GPU).
    /// </summary>
    /// <exception cref="ArgumentException">Частей не столько, сколько суставов у меша, или часть больше <see cref="NoPart"/>.</exception>
    public void SetJointParts(int mesh, ReadOnlySpan<uint> parts)
    {
        var m = _meshes[mesh];
        if (parts.Length != m.JointCount) throw new ArgumentException($"{m.Name}: частей {parts.Length}, суставов {m.JointCount}", nameof(parts));
        foreach (uint p in parts)
            if (p > NoPart) throw new ArgumentException($"{m.Name}: часть {p} больше {NoPart}", nameof(parts));
        var fresh = _renderer.CreateData<uint>(m.Name + ".JointParts", parts);
        m.Parts.Dispose();   // отложенно: кадры в полёте ещё читают прежнюю таблицу
        m.Parts = fresh;
    }

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

    /// <summary>
    /// Экземпляр статического меша (без суставов: оружие, реквизит) — жёсткое вложение: палитра из одной матрицы — мир меша
    /// (<c>SkinnedCharacters.Submit</c> SWORD 2eab13b: меч — <c>sword[0] = WeaponWorld</c>). Вершины статического меша
    /// ведёт единственная матрица палитры при любых весах. false — места в кадре нет.
    /// </summary>
    /// <exception cref="ArgumentException">У меша есть суставы: его рисуют позой (<see cref="AddPosed"/>).</exception>
    public bool AddRigid(int mesh, in Matrix4x4 world, Vector3 colour, uint owner = 0, float glow = 0, uint flags = 0)
    {
        var m = _meshes[mesh];
        if (m.InverseBind.Length != 0)
            throw new ArgumentException($"{m.Name}: суставов {m.InverseBind.Length} — жёстко вкладывается только статический меш", nameof(mesh));
        var palette = Add(mesh, colour, owner, glow, flags);
        if (palette.IsEmpty) return false;
        palette[0] = GpuMat34.From(world);
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
            ctx.SetConstant(SlotJointParts, m.Parts.Srv);
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
