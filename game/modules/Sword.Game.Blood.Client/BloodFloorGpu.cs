using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Fluid2D;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>Константы проходов пола и капель — <c>FloorConstants</c> шейдера (строки по 16 байт; размер сверяет контракт).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BloodFloorConstants
{
    public Matrix4x4 ViewProj, InvViewProj;
    public Vector3 Eye; public float InvWidth;
    public Vector3 SunDir; public float InvHeight;
    public Vector3 Sun; public float FogStart;
    public Vector3 Sky; public float FogRange;
    public Vector3 FogColour; public float Now;
    public Vector2 WindowMin; public float CosMax, HeightBand;
    public Vector2 MapMin; public float ThickMmPerRho, NormalScale;
    public uint Field, Age, Map, Depth;
    public uint Normal, Terrain, TerrainCells; public float TerrainHalf;
    public uint HasField, HasMap, HasTerrain; public float F0;
    public float Drag, Stretch, DropThickMm, Pad0;
    public float FogDensity, FogFalloff, Pad1, Pad2;
}

/// <summary>Летящая капля для шейдера: отрыв (точка, скорость), время полёта в кадре, с, радиус, м.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BloodDropGpu
{
    public Vector3 X0; public float T;
    public Vector3 V0; public float Radius;
}

/// <summary>Что кадру пола нужно от модуля: камера, солнце и небо, туман, рельеф, ручки.</summary>
public sealed class BloodFloorView
{
    public Func<CameraView> Camera = () => default;
    public ISceneSky? Sky;
    public ISceneFog? Fog;
    public ITerrainView? Terrain;
    public required BloodTuning Tuning;
}

/// <summary>
/// Кровь на полу на GPU (<see cref="IGpuSimulation"/> <c>blood.floor</c>): <see cref="Fluid2DSim"/> пресета <see cref="BloodFloor.Params"/>
/// в окне 32 м у игрока, высоты — буфер <see cref="ITerrainView"/>; шаги — по заявкам <see cref="BloodFloorStep"/> (60 Гц по <c>TickId</c>):
/// сброс → перенос уходящей при сдвиге полосы в карту (<c>blood.floor.transfer</c>) → штампы и шаг сима со сдвигом. Карта засохших пятен —
/// <see cref="BloodFloor.MapN"/>² uint (half ρ | half рождение), 64 МБ. Кадр: Opaque — летящие капли (<c>blood.drops</c>), Composite — плёнка
/// поверх приёмников (<c>blood.floor</c>). Ресурсы — при создании; кадр — 0 Б.
/// </summary>
public sealed class BloodFloorGpu : IGpuSimulation
{
    public const string ShaderFile = "blood/blood_floor.hlsl";
    public const string TransferPass = "blood.floor.transfer", MapClearPass = "blood.map.clear", FloorPass = "blood.floor", DropsPass = "blood.drops";
    private const int SlotC = 18, SlotAge = 19, SlotMap = 20, SlotShiftX = 21, SlotShiftZ = 22, SlotMapX = 23, SlotMapZ = 24, SlotNow = 25;
    private const int SlotData = 18, SlotDrops = 19;
    private const int Group = 8;

    private static readonly GraphicsPipelineDesc FloorPipeline = new(ShaderFile, "VsFull", "PsFloor", BlendMode.Premultiplied);
    private static readonly GraphicsPipelineDesc DropPipeline = new(ShaderFile, "VsDrop", "PsDrop", BlendMode.Opaque, DepthMode.TestWrite);
    private static readonly ComputePipelineDesc TransferPipeline = new(ShaderFile, "CsTransfer");
    private static readonly ComputePipelineDesc ClearPipeline = new(ShaderFile, "CsMapClear");

    private readonly BloodFloor _floor;
    private readonly BloodFloorView _view;
    private Renderer? _renderer;
    private Fluid2DSim? _sim;
    private Fluid2DStamps? _stamps;
    private GpuStorage? _map;
    private FrameData<BloodFloorConstants>? _constants;
    private FrameData<BloodDropGpu>? _drops;
    private readonly Action<RenderPassContext> _transfer, _clear, _drawFloor, _drawDrops;
    // Переносы кадра по очереди: в кадре до 8 шагов, проход исполняется позже записи — аргументы каждого шага свои.
    private (int C, int Age, int Map, int ShiftX, int ShiftZ, int MapX, int MapZ, float Now)[] _transferArgs = new (int, int, int, int, int, int, int, float)[8];
    private int _transferCount, _transferNext;
    private ulong _transferFrame = ulong.MaxValue;
    private int _data, _dropsSrv, _dropCount;
    private bool _floorReady;
    private int _centerX, _centerZ;
    private float _now;
    private double _displayTick;

    public BloodFloorGpu(BloodFloor floor, BloodFloorView view)
    {
        _floor = floor;
        _view = view;
        _transfer = Transfer;
        _clear = ClearMap;
        _drawFloor = DrawFloor;
        _drawDrops = DrawDrops;
    }

    public string Name => "blood.floor";

    public Fluid2DSim? Sim => _sim;
    public GpuStorage? Map => _map;

    /// <summary>Шагов на GPU, переносов в карту (сдвигов окна), сбросов.</summary>
    public long GpuSteps { get; private set; }
    public long Transfers { get; private set; }
    public long Resets { get; private set; }

    /// <summary>Статистика последнего прочитанного шага (асинхронно).</summary>
    public Fluid2DStats? Stats { get; private set; }

    /// <summary>Капель, нарисованных в последнем кадре.</summary>
    public int DrawnDrops => _dropCount;

    /// <summary>Выход (до освобождения GPU): хеш полей, мл в окне и на карте, площадь крови в окне, м².</summary>
    public ulong? FieldHash { get; private set; }
    public double? FinalWindowMl { get; private set; }
    public double? FinalMapMl { get; private set; }
    public double? FinalAreaM2 { get; private set; }

    public void Create(GpuSimContext context) => Create(context.Renderer, r => context.Own(r), context.Readback);

    /// <summary>Ресурсы без хоста модулей (тесты).</summary>
    public void Create(Renderer renderer, Func<IDisposable, IDisposable> own, Action<GpuStorage, uint>? readback = null)
    {
        _renderer = renderer;
        var p = BloodFloor.Params;
        _sim = (Fluid2DSim)own(new Fluid2DSim(renderer, Name, p));
        _stamps = new Fluid2DStamps(p, 4096, 65535);
        _map = (GpuStorage)own(renderer.CreateStorage<uint>("blood.map", (uint)(BloodFloor.MapN * BloodFloor.MapN)));
        _constants = (FrameData<BloodFloorConstants>)own(renderer.CreateFrameData<BloodFloorConstants>("blood.floor.constants", 1));
        _drops = (FrameData<BloodDropGpu>)own(renderer.CreateFrameData<BloodDropGpu>("blood.drops", 1024));
        readback?.Invoke(_sim.StatsStorage, Fluid2DStats.Bytes);
        own(new Closer(this));
    }

    public void AddPasses(RenderFrame frame, in SimFrame sim)
    {
        if (_sim is null) return;
        foreach (ref readonly var step in sim.Requests<BloodFloorStep>())
            Step(frame, step, _floor.Stamps.Slice(step.StampFirst, step.StampCount));
    }

    /// <summary>Шаг пола: сброс, перенос уходящей полосы в карту, штампы и шаг сима со сдвигом.</summary>
    public void Step(RenderFrame frame, in BloodFloorStep step, ReadOnlySpan<BloodFloorStamp> stamps)
    {
        var sim = _sim!;
        var g = frame.Graph;
        var map = g.Import(_map!);
        if (step.Reset != 0)
        {
            sim.Reset();
            Resets++;
            _clearUav = g.UavOf(map);
            g.AddComputePass(MapClearPass, FramePhase.Simulation).Write(map).Execute(_clear);
        }
        else if ((step.ShiftX != 0 || step.ShiftZ != 0) && GpuSteps > 0)
        {
            var c = g.Import(sim.Composite);
            var age = g.Import(sim.Age!);
            if (_transferFrame != frame.Number) (_transferFrame, _transferCount, _transferNext) = (frame.Number, 0, 0);
            if (_transferCount == _transferArgs.Length) Array.Resize(ref _transferArgs, 2 * _transferArgs.Length);
            _transferArgs[_transferCount++] = (g.SrvOf(c), g.SrvOf(age), g.UavOf(map), step.ShiftX, step.ShiftZ,
                step.CenterX - step.ShiftX + BloodFloor.MapOffset, step.CenterZ - step.ShiftZ + BloodFloor.MapOffset, step.Now);
            g.AddComputePass(TransferPass, FramePhase.Simulation).Read(c).Read(age).Write(map).Execute(_transfer);
            Transfers++;
        }
        var center = new Vector3(step.CenterX * BloodFloor.Texel, 0, step.CenterZ * BloodFloor.Texel);
        var rotation = PlaneRotation;
        var input = new Fluid2DStepInput(step.Step, Vector2.Zero, Vector2.Zero, rotation)
        {
            ShiftTexels = (step.ShiftX, step.ShiftZ),
            WindowCenter = new Vector2(center.X, center.Z),
        };
        if (_view.Terrain is { HeightsSrv: >= 0 } terrain)
            sim.Heights = new Fluid2DHeightField(terrain.HeightsSrv, terrain.Grid.Cells, terrain.Grid.Half);
        var st = _stamps!;
        st.Begin(step.Step, center, rotation);
        foreach (ref readonly var s in stamps) st.Add(new Vector3(s.Xz.X, 0, s.Xz.Y), 0, new Vector2(s.HalfAxis), s.Volume);
        st.End();
        sim.Step(frame, input, (GpuStorage?)null, st);
        _centerX = step.CenterX;
        _centerZ = step.CenterZ;
        _now = step.Now;
        _floorReady = true;
        GpuSteps++;
    }

    /// <summary>Площадка окна: горизонтальна, u — вдоль +X, v — вдоль +Z (поворот −90° вокруг X).</summary>
    public static Quaternion PlaneRotation { get; } = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);

    private void Transfer(RenderPassContext ctx)
    {
        var a = _transferArgs[_transferNext++];
        ctx.SetPipeline(TransferPipeline);
        ctx.SetConstant(SlotC, a.C);
        ctx.SetConstant(SlotAge, a.Age);
        ctx.SetConstant(SlotMap, a.Map);
        ctx.SetConstant(SlotShiftX, a.ShiftX);
        ctx.SetConstant(SlotShiftZ, a.ShiftZ);
        ctx.SetConstant(SlotMapX, a.MapX);
        ctx.SetConstant(SlotMapZ, a.MapZ);
        ctx.SetConstant(SlotNow, a.Now);
        uint groups = BloodFloor.N / Group;
        ctx.Dispatch(groups, groups);
    }

    private int _clearUav;

    private void ClearMap(RenderPassContext ctx)
    {
        ctx.SetPipeline(ClearPipeline);
        ctx.SetConstant(SlotMap, _clearUav);
        uint groups = BloodFloor.MapN / Group;
        ctx.Dispatch(groups, groups);
    }

    /// <summary>Время показа кадра в тиках (последний тик − 1 + доля): капли рисуются на нём.</summary>
    public void SetDisplayTick(double tick) => _displayTick = tick;

    /// <summary>Кадр, фаза Opaque: константы кадра пола и капли в полёте (<c>blood.drops</c>).</summary>
    public void DrawDrops(RenderFrame frame, float tickDt)
    {
        if (_sim is null || _constants is null) return;
        var g = frame.Graph;
        var t = _view.Tuning;
        var camera = _view.Camera();
        Matrix4x4.Invert(camera.ViewProj, out var inv);
        var sky = _view.Sky?.Sky ?? SceneSky.None;
        var fog = _view.Fog?.Fog ?? SceneFog.None;
        var terrain = _view.Terrain;
        var p = BloodFloor.Params;
        var c = g.Import(_sim.Composite);
        var age = g.Import(_sim.Age!);
        var map = g.Import(_map!);
        var window = new Vector2(_centerX * BloodFloor.Texel - 0.5f * BloodFloor.Size, _centerZ * BloodFloor.Texel - 0.5f * BloodFloor.Size);
        _constants.Slot[0] = new BloodFloorConstants
        {
            ViewProj = camera.ViewProj,
            InvViewProj = inv,
            Eye = camera.Eye,
            InvWidth = 1f / Math.Max(1, frame.Width),
            InvHeight = 1f / Math.Max(1, frame.Height),
            SunDir = sky.SunDirection == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(sky.SunDirection),
            Sun = sky.Sun,
            Sky = sky.Sky,
            FogStart = fog.Start,
            FogRange = fog.Range,
            FogColour = fog.Colour,
            FogDensity = fog.HeightDensity,
            FogFalloff = fog.HeightFalloff,
            Now = _now,
            WindowMin = window,
            CosMax = MathF.Cos(t.FloorMaxSlope * MathF.PI / 180),
            HeightBand = t.FloorHeightBand,
            MapMin = new Vector2(-0.5f * BloodFloor.MapSize),
            ThickMmPerRho = p.FilmThicknessPerDensity * 1000,
            NormalScale = t.FloorNormalScale,
            Field = (uint)g.SrvOf(c),
            Age = (uint)g.SrvOf(age),
            Map = (uint)g.SrvOf(map),
            Depth = (uint)g.SrvOf(frame.Scene.Depth),
            Normal = (uint)g.SrvOf(frame.Scene.Normal),
            Terrain = (uint)Math.Max(0, terrain?.HeightsSrv ?? 0),
            TerrainCells = (uint)(terrain?.Grid.Cells ?? 0),
            TerrainHalf = terrain?.Grid.Half ?? 1,
            HasField = _floorReady ? 1u : 0,
            HasMap = _floorReady ? 1u : 0,
            HasTerrain = terrain is { HeightsSrv: >= 0 } ? 1u : 0,
            F0 = t.FloorF0,
            Drag = t.DropDrag,
            Stretch = t.DropStretch,
            DropThickMm = t.DropThickMm,
        };
        _data = _constants.Srv;

        // Капли в полёте: время полёта на момент показа.
        var flying = _floor.Flying;
        if (flying.Length > _drops!.Capacity)
        {
            _drops.Dispose();
            _drops = _renderer!.CreateFrameData<BloodDropGpu>("blood.drops", (uint)Math.Max(2 * flying.Length, 2 * (int)_drops.Capacity));
        }
        var slot = _drops.Slot;
        _dropCount = 0;
        for (int i = 0; i < flying.Length; i++) PutDrop(slot, flying[i], tickDt, t.DropRadiusScale);
        _dropsSrv = _drops.Srv;
        g.AddPass(DropsPass, FramePhase.Opaque).Color(frame.SceneColor).Color(frame.Scene.Normal).Depth(frame.Scene.Depth, DepthAccess.Write)
            .Execute(_drawDrops);
        _framed = frame.Number;
    }

    private ulong _framed = ulong.MaxValue;

    /// <summary>Кадр, фаза Composite: плёнка пола с картой пятен поверх приёмников (<c>blood.floor</c>), константы — из <see cref="DrawDrops"/>.</summary>
    public void DrawFilm(RenderFrame frame)
    {
        if (_sim is null || _framed != frame.Number) return;
        var g = frame.Graph;
        g.AddPass(FloorPass, FramePhase.Composite).Color(frame.SceneColor).Read(frame.Scene.Depth).Read(frame.Scene.Normal)
            .Read(g.Import(_sim.Composite)).Read(g.Import(_sim.Age!)).Read(g.Import(_map!)).Execute(_drawFloor);
    }

    /// <summary>Числа шейдера пола в контракт.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Blood: floor film, map of dried stains, drops (Sword.Game.Blood.Client)")
        .Add("BLOOD_FLOOR_N", BloodFloor.N, "texels of the floor window per side")
        .Add("BLOOD_FLOOR_SIZE", BloodFloor.Size, "size of the floor window, m")
        .Add("BLOOD_MAP_N", BloodFloor.MapN, "texels of the map of dried stains per side")
        .AddSize<BloodFloorConstants>("BLOOD_FLOOR_CONSTANTS_SIZE")
        .AddSize<BloodDropGpu>("BLOOD_DROP_SIZE")
        .AddSlot("gFloorC", SlotC).AddSlot("gFloorAge", SlotAge).AddSlot("gFloorMap", SlotMap).AddSlot("gFloorShiftX", SlotShiftX)
        .AddSlot("gFloorShiftZ", SlotShiftZ).AddSlot("gFloorMapX", SlotMapX).AddSlot("gFloorMapZ", SlotMapZ).AddSlot("gFloorNow", SlotNow)
        .AddSlot("gFloorData", SlotData).AddSlot("gFloorDrops", SlotDrops);

    private void PutDrop(Span<BloodDropGpu> slot, in BloodFlying f, float tickDt, float radiusScale)
    {
        float time = (float)((_displayTick - f.Tick0) * tickDt);   // double: float тика к часам игры теряет долю кадра
        if (time < 0 || time >= f.LandTime) return;
        float radius = radiusScale * MathF.Cbrt(3 * f.Volume * 1e-6f / (4 * MathF.PI));
        slot[_dropCount++] = new BloodDropGpu { X0 = f.X0, V0 = f.V0, T = time, Radius = radius };
    }

    private void SetLook(RenderPassContext ctx)
    {
        Span<float> k = stackalloc float[BloodLookParams.Slots];
        BloodLook.Write(_view.Tuning.Look, k);
        for (int i = 0; i < k.Length; i++) ctx.SetConstant(i, k[i]);
    }

    private void DrawFloor(RenderPassContext ctx)
    {
        if (!_floorReady) return;
        ctx.SetPipeline(FloorPipeline);
        SetLook(ctx);
        ctx.SetConstant(SlotData, _data);
        ctx.Draw(3);
    }

    private void DrawDrops(RenderPassContext ctx)
    {
        if (_dropCount == 0) return;
        ctx.SetPipeline(DropPipeline);
        SetLook(ctx);
        ctx.SetConstant(SlotData, _data);
        ctx.SetConstant(SlotDrops, _dropsSrv);
        ctx.Draw(6, (uint)_dropCount);
    }

    public void OnReadback(ReadOnlySpan<byte> data)
    {
        var stats = Fluid2DStats.Parse(data);
        if (stats.Resolution == 0) return;
        Stats = stats;
    }

    /// <summary>Карта с GPU (ожидание GPU; между кадрами): half ρ | half рождение на тексель.</summary>
    public uint[] ReadMap()
    {
        var data = new uint[BloodFloor.MapN * BloodFloor.MapN];
        _renderer!.ReadStorage(_map!, data.AsSpan());
        return data;
    }

    /// <summary>Σρ карты, ρ·м² (из half).</summary>
    public static double MapDensity(ReadOnlySpan<uint> map)
    {
        double s = 0;
        foreach (uint u in map) s += (float)BitConverter.UInt16BitsToHalf((ushort)u);
        return s * BloodFloor.Texel * BloodFloor.Texel;
    }

    /// <summary>Σρ окна, ρ·м² (float ρ сима).</summary>
    public static double WindowDensity(Fluid2DFields fields)
    {
        double s = 0;
        foreach (float r in fields.DensityComposite) s += r;
        return s * BloodFloor.Texel * BloodFloor.Texel;
    }

    /// <summary>Хеш полей пола: C (биты half) и ρ (float) — FNV-1a 64.</summary>
    public static ulong Hash(Fluid2DFields fields)
    {
        ulong h = 14695981039346656037UL;
        h = Fnv(MemoryMarshal.AsBytes(fields.Composite.AsSpan()), h);
        return Fnv(MemoryMarshal.AsBytes(fields.DensityComposite.AsSpan()), h);
    }

    private static ulong Fnv(ReadOnlySpan<byte> data, ulong h)
    {
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    private void Closing()
    {
        if (_sim is null || GpuSteps == 0) return;
        var fields = _sim.ReadFields();
        FieldHash = Hash(fields);
        FinalWindowMl = BloodFloor.Millilitres(WindowDensity(fields));
        FinalMapMl = BloodFloor.Millilitres(MapDensity(ReadMap()));
        int wet = 0;
        float min = 0.01e-3f / BloodFloor.Params.FilmThicknessPerDensity;   // толще 0.01 мм
        foreach (float r in fields.DensityComposite) if (r > min) wet++;
        FinalAreaM2 = wet * BloodFloor.Texel * BloodFloor.Texel;
    }

    private sealed class Closer(BloodFloorGpu gpu) : IDisposable
    {
        public void Dispose()
        {
            gpu.Closing();
            gpu._drops?.Dispose();   // выросший буфер капель не отдан хосту (own) — освобождается здесь
        }
    }
}
