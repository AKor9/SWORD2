using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// 3D-газ на GPU (README.md): сетка в мире, выровненная по осям, едет за целью целыми ячейками. Поля — хранилища <see cref="GpuStorage"/>
/// half-битов: V, V₁ (скорость, 8 Б на ячейку), S, S₁, S₂ (ρ и T, 4 Б), два Aux (давление и служебное t, 4 Б; меняются ролями каждый шаг).
/// Шаг — вычислительные проходы фазы <see cref="FramePhase.Simulation"/>: <c>gas.sources</c> (сдвиг окна и источники) → <c>gas.curl</c> →
/// <c>gas.forces</c> → <c>gas.advect.velocity</c> → <c>gas.divergence</c> → <c>gas.pressure.red</c>/<c>.black</c> ×<see cref="Fluid3DParams.PressureIterations"/>
/// → <c>gas.project</c> → [<c>gas.advect.forward</c>] → <c>gas.advect.scalars</c> → <c>gas.stats.slices</c> → <c>gas.stats</c>. Состояние
/// после шага — в V и S. Кадр — 0 Б: делегат прохода один, аргументы проходов — массив сима по порядку регистрации. Вне кадра —
/// <see cref="ReadFields"/>, <see cref="Load"/>, <see cref="ReadStats"/>, <see cref="Snapshot"/>.
/// </summary>
public sealed class Fluid3DSim : IDisposable
{
    public const string ShaderFile = "fluid3d/fluid3d.hlsl";

    /// <summary>Шагов за кадр не больше — элементов кольца констант на кадр.</summary>
    public const int MaxStepsPerFrame = 64;

    /// <summary>Источников за шаг не больше.</summary>
    public const int MaxSources = Fluid3DConstants.MaxSources;

    internal const int GroupX = 8, GroupY = 4, GroupZ = 4, StatsThreads = 256;
    private const int SlotConstants = 0, SlotIndex = 1, SlotIn0 = 2, SlotIn1 = 3, SlotIn2 = 4, SlotOut0 = 5, SlotOut1 = 6, SlotOut2 = 7,
        SlotOut3 = 8, SlotPass = 9, SlotIn3 = 10;

    private enum Entry { Clear, Sources, Curl, Forces, AdvectVelocity, Divergence, Pressure, Project, AdvectScalars, AdvectForward, AdvectMacCormack, StatsSlices, Stats }

    private static readonly string[] Entries = ["CsClear", "CsSources", "CsCurl", "CsForces", "CsAdvectVelocity", "CsDivergence", "CsPressure",
        "CsProject", "CsAdvectScalars", "CsAdvectForward", "CsAdvectMacCormack", "CsStatsSlices", "CsStats"];

    /// <summary>Числа и слоты шейдера газа в контракт (вызывает тот, кто собирает контракт рендера: модуль — <c>ClientBuilder.Shaders</c>).</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fluid3D: 3D gas core (Sword.Engine.Fluid3D)")
        .AddSize<Fluid3DConstants>("FLUID3D_CONSTANTS_SIZE")
        .AddSize<StatsPartial>("FLUID3D_STATS_PARTIAL_SIZE")
        .Add("FLUID3D_MAX_SOURCES", MaxSources)
        .Add("FLUID3D_GROUP_X", GroupX, "numthreads of cell passes (8x4x4)")
        .Add("FLUID3D_GROUP_Y", GroupY)
        .Add("FLUID3D_GROUP_Z", GroupZ)
        .Add("FLUID3D_STATS_THREADS", StatsThreads, "threads of a stats group (one z slice)")
        .AddSlot("gGasConstants", SlotConstants)
        .AddSlot("gGasIndex", SlotIndex)
        .AddSlot("gGasIn0", SlotIn0)
        .AddSlot("gGasIn1", SlotIn1)
        .AddSlot("gGasIn2", SlotIn2)
        .AddSlot("gGasOut0", SlotOut0)
        .AddSlot("gGasOut1", SlotOut1)
        .AddSlot("gGasOut2", SlotOut2)
        .AddSlot("gGasOut3", SlotOut3)
        .AddSlot("gGasPass", SlotPass)
        .AddSlot("gGasIn3", SlotIn3);

    [StructLayout(LayoutKind.Sequential)]
    internal struct StatsPartial
    {
        public float Density, Temperature, SumX, SumY, SumZ, MaxSpeed, MaxDensity, MaxTemperature, DivBefore, DivAfter, Enstrophy;
        public uint NonFinite;
    }

    private struct PassArgs
    {
        public Entry Entry;
        public int Constants, In0, In1, In2, In3, Out0, Out1, Out2, Out3, Pass;
        public uint GroupsX, GroupsY, GroupsZ;
    }

    /// <summary>Источник-карта шага: плоскость и буфер карты (клетки <see cref="Fluid3DMapTexel"/> с номера <see cref="Offset"/>).</summary>
    private readonly record struct PlaneFeed(Fluid3DPlane Plane, GpuStorage Map, int Offset);

    private readonly Renderer _renderer;
    private readonly FrameData<Fluid3DConstants> _constants;
    private readonly GpuStorage _stats, _partial;
    private GpuStorage _velocity, _velocityScratch, _scalars, _scalarScratch, _scalarForward;
    private readonly GpuStorage[] _aux = new GpuStorage[2];
    private int _auxCurrent;
    private bool _clearPending = true;
    private Fluid3DParams _params;
    private Fluid3DWindow? _window;

    private readonly Action<RenderPassContext> _execute;
    private PassArgs[] _args = new PassArgs[32];
    private int _argCount, _argNext, _slots;
    private ulong _frame = ulong.MaxValue;

    public Fluid3DSim(Renderer renderer, string name, Fluid3DParams parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        parameters.Validate();
        _renderer = renderer;
        _params = parameters;
        Name = name;
        uint cells = (uint)parameters.Cells;
        _constants = renderer.CreateFrameData<Fluid3DConstants>($"gas.{name}.constants", MaxStepsPerFrame + 1);
        _velocity = renderer.CreateStorage<ulong>($"gas.{name}.velocity", cells);
        _velocityScratch = renderer.CreateStorage<ulong>($"gas.{name}.velocity1", cells);
        _scalars = renderer.CreateStorage<uint>($"gas.{name}.scalars", cells);
        _scalarScratch = renderer.CreateStorage<uint>($"gas.{name}.scalars1", cells);
        _scalarForward = renderer.CreateStorage<uint>($"gas.{name}.scalars2", cells);
        _aux[0] = renderer.CreateStorage<uint>($"gas.{name}.aux0", cells);
        _aux[1] = renderer.CreateStorage<uint>($"gas.{name}.aux1", cells);
        _partial = renderer.CreateStorage<StatsPartial>($"gas.{name}.stats.partial", (uint)parameters.Nz);
        _stats = renderer.CreateStorage<uint>($"gas.{name}.stats", Fluid3DStats.Bytes / sizeof(uint));
        _execute = Execute;
    }

    public string Name { get; }

    /// <summary>Параметры — на ходу, со следующего шага; размеры сетки не меняются.</summary>
    public Fluid3DParams Params
    {
        get => _params;
        set
        {
            value.Validate();
            if (value.Nx != _params.Nx || value.Ny != _params.Ny || value.Nz != _params.Nz)
                throw new ArgumentException($"сетка {_params.Nx}×{_params.Ny}×{_params.Nz} не меняется — нужен новый сим");
            _params = value;
        }
    }

    /// <summary>Окно после последнего шага; до первого шага (и после сброса) — null.</summary>
    public Fluid3DWindow? Window => _window;

    /// <summary>Шагов с создания или сброса.</summary>
    public ulong Steps { get; private set; }

    /// <summary>Номер последнего шага; до первого — null.</summary>
    public ulong? LastStep { get; private set; }

    /// <summary>Сырые данные статистики последнего шага (<see cref="Fluid3DStats.Bytes"/> байт; <see cref="Fluid3DStats.Parse"/>).</summary>
    public GpuStorage StatsStorage => _stats;

    /// <summary>
    /// Плотность и температура после последнего шага — показу (<c>StructuredBuffer&lt;uint&gt;</c>: half ρ в младших 16 битах, T — в старших;
    /// ячейка (x, y, z) окна <see cref="Window"/> — элемент x + Nx·(y + Ny·z)). Читать — проходом после шагов кадра; <see cref="Load"/> меняет хранилище.
    /// </summary>
    public GpuStorage Scalars => _scalars;

    /// <summary>
    /// Скорость после последнего шага — показу (<c>StructuredBuffer&lt;uint2&gt;</c>: half vx и vy в первом слове, vz — во втором, м/с; ячейки —
    /// как у <see cref="Scalars"/>). Читать — проходом после шагов кадра.
    /// </summary>
    public GpuStorage Velocity => _velocity;

    /// <summary>Обнулить поля первым проходом следующего шага (<c>gas.clear</c>); окно встанет у цели следующего шага.</summary>
    public void Reset()
    {
        _clearPending = true;
        _window = null;
        Steps = 0;
        LastStep = null;
    }

    /// <summary>
    /// Проходы одного шага <paramref name="step"/> в кадр <paramref name="frame"/>: окно едет к <paramref name="target"/> (мир, м) целыми ячейками,
    /// затем источники <paramref name="sources"/> (не больше <see cref="MaxSources"/>; копируются сразу) и физика шага.
    /// </summary>
    public void Step(RenderFrame frame, ulong step, Vector3 target, ReadOnlySpan<Fluid3DSource> sources) =>
        AddStep(frame, step, target, sources, null, null);

    /// <summary>Шаг с источниками по траектории: номер шага и источники — готового шага <paramref name="emitters"/> (после <see cref="Fluid3DEmitters.End"/>).</summary>
    public void Step(RenderFrame frame, Vector3 target, Fluid3DEmitters emitters)
    {
        if (!emitters.Ready) throw new InvalidOperationException($"газ {Name}: шаг источников не готов (Begin → Add → End)");
        AddStep(frame, emitters.Step, target, emitters.Sources, null, null);
    }

    /// <summary>
    /// Шаг с источниками по траектории и источником-картой <paramref name="plane"/>: клетки карты — в <paramref name="map"/> (<c>uint4</c>
    /// <see cref="Fluid3DMapTexel"/>) с номера <paramref name="mapOffset"/>; проход источников читает буфер после проходов, которые его пишут
    /// раньше в кадре (граф — по порядку регистрации).
    /// </summary>
    public void Step(RenderFrame frame, Vector3 target, Fluid3DEmitters emitters, in Fluid3DPlane plane, GpuStorage map, int mapOffset)
    {
        if (!emitters.Ready) throw new InvalidOperationException($"газ {Name}: шаг источников не готов (Begin → Add → End)");
        AddStep(frame, emitters.Step, target, emitters.Sources, null, new PlaneFeed(plane, map, mapOffset));
    }

    /// <summary>Одна стадия (проверка стадий): буферы — как у <see cref="Fluid3DStage"/>; окно сдвигается только стадией Sources; статистики нет.</summary>
    internal void AddStage(RenderFrame frame, ulong step, Vector3 target, ReadOnlySpan<Fluid3DSource> sources, Fluid3DStage stage) =>
        AddStep(frame, step, target, sources, stage, null);

    /// <summary>Стадия с источником-картой (проверка стадии Sources).</summary>
    internal void AddStage(RenderFrame frame, ulong step, Vector3 target, ReadOnlySpan<Fluid3DSource> sources, Fluid3DStage stage,
        in Fluid3DPlane plane, GpuStorage map, int mapOffset) => AddStep(frame, step, target, sources, stage, new PlaneFeed(plane, map, mapOffset));

    // GPU за концом буфера читает нули: карта молча пропала бы, поэтому размер проверяется до регистрации проходов.
    private void CheckMap(in PlaneFeed feed)
    {
        long need = (long)feed.Offset + (long)feed.Plane.Side * feed.Plane.Side;
        if (feed.Map.Stride != 16 || feed.Offset < 0 || need > feed.Map.Count)
            throw new ArgumentException($"газ {Name}: карта {feed.Map.Name} — {feed.Map.Count} × {feed.Map.Stride} Б, нужно ≥ {feed.Offset} + " +
                $"{feed.Plane.Side}² клеток × 16 Б (Fluid3DMapTexel)", "map");
    }

    private void AddStep(RenderFrame frame, ulong step, Vector3 target, ReadOnlySpan<Fluid3DSource> sources, Fluid3DStage? only, PlaneFeed? feed)
    {
        if (feed is { } checkFeed) CheckMap(checkFeed);
        BeginFrame(frame);
        var g = frame.Graph;
        var next = Fluid3DWindow.Around(target, _params);
        var shift = _window is { } w ? next.ShiftFrom(w) : (0, 0, 0);
        int k = PushConstants(Fluid3DConstants.Build(_params, next, shift, step, sources, feed?.Plane, feed?.Offset ?? 0));
        var v = g.Import(_velocity);
        var v1 = g.Import(_velocityScratch);
        var s = g.Import(_scalars);
        var s1 = g.Import(_scalarScratch);
        var s2 = g.Import(_scalarForward);
        var auxPrev = g.Import(_aux[_auxCurrent]);
        var auxNext = g.Import(_aux[1 - _auxCurrent]);
        if (_clearPending) AddClear(g, k, v, s, auxPrev, auxNext);
        var (gx, gy, gz) = Groups();

        if (only is null or Fluid3DStage.Sources)
        {
            var pass = g.AddComputePass("gas.sources", FramePhase.Simulation).Read(v).Read(s).Read(auxPrev);
            int mapSrv = 0;
            if (feed is { } f)
            {
                var map = g.Import(f.Map);
                pass = pass.Read(map);
                mapSrv = g.SrvOf(map);
            }
            pass.Write(v1).Write(s1).Write(auxNext).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Sources, Constants = k, In0 = g.SrvOf(v), In1 = g.SrvOf(s), In2 = g.SrvOf(auxPrev), In3 = mapSrv,
                Out0 = g.UavOf(v1), Out1 = g.UavOf(s1), Out2 = g.UavOf(auxNext), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
            _auxCurrent = 1 - _auxCurrent;
            _window = next;
        }
        var aux = only == Fluid3DStage.Sources || only is null ? auxNext : auxPrev;
        if (only is null or Fluid3DStage.Curl)
        {
            g.AddComputePass("gas.curl", FramePhase.Simulation).Read(v1).Write(aux).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Curl, Constants = k, In0 = g.SrvOf(v1), Out0 = g.UavOf(aux), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        if (only is null or Fluid3DStage.Forces)
        {
            g.AddComputePass("gas.forces", FramePhase.Simulation).Read(v1).Read(s1).Read(aux).Write(v).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Forces, Constants = k, In0 = g.SrvOf(v1), In1 = g.SrvOf(s1), In2 = g.SrvOf(aux), Out0 = g.UavOf(v),
                GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        if (only is null or Fluid3DStage.AdvectVelocity)
        {
            g.AddComputePass("gas.advect.velocity", FramePhase.Simulation).Read(v).Write(v1).Execute(_execute);
            Push(new PassArgs { Entry = Entry.AdvectVelocity, Constants = k, In0 = g.SrvOf(v), Out0 = g.UavOf(v1), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        if (only is null or Fluid3DStage.Divergence)
        {
            g.AddComputePass("gas.divergence", FramePhase.Simulation).Read(v1).Write(aux).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Divergence, Constants = k, In0 = g.SrvOf(v1), Out0 = g.UavOf(aux), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        if (only is null)
            for (int i = 0; i < _params.PressureIterations; i++) AddPressure(g, k, aux, i & 1);
        else if (only is Fluid3DStage.PressureRed or Fluid3DStage.PressureBlack) AddPressure(g, k, aux, only == Fluid3DStage.PressureRed ? 0 : 1);
        if (only is null or Fluid3DStage.Project)
        {
            g.AddComputePass("gas.project", FramePhase.Simulation).Read(v1).Read(aux).Write(v).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Project, Constants = k, In0 = g.SrvOf(v1), In1 = g.SrvOf(aux), Out0 = g.UavOf(v), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        bool macCormack = _params.ScalarAdvection == Fluid3DAdvection.MacCormack;
        if ((only is null && macCormack) || only == Fluid3DStage.AdvectForward)
        {
            g.AddComputePass("gas.advect.forward", FramePhase.Simulation).Read(v).Read(s1).Write(s2).Execute(_execute);
            Push(new PassArgs { Entry = Entry.AdvectForward, Constants = k, In0 = g.SrvOf(v), In1 = g.SrvOf(s1), Out0 = g.UavOf(s2), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        if (only is null or Fluid3DStage.AdvectScalars)
        {
            var pass = g.AddComputePass("gas.advect.scalars", FramePhase.Simulation).Read(v).Read(s1);
            if (macCormack) pass = pass.Read(s2);
            pass.Write(s).Execute(_execute);
            Push(new PassArgs { Entry = macCormack ? Entry.AdvectMacCormack : Entry.AdvectScalars, Constants = k, In0 = g.SrvOf(v), In1 = g.SrvOf(s1),
                In2 = macCormack ? g.SrvOf(s2) : 0, Out0 = g.UavOf(s), GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        }
        if (only is null)
        {
            AddStats(g, k, v, s, aux);
            Steps++;
            LastStep = step;
        }
    }

    private void AddPressure(RenderGraph g, int k, RenderBuffer aux, int parity)
    {
        var (_, gy, gz) = Groups();
        uint gx = (uint)(((_params.Nx + 1) / 2 + GroupX - 1) / GroupX);
        g.AddComputePass(parity == 0 ? "gas.pressure.red" : "gas.pressure.black", FramePhase.Simulation).Write(aux).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Pressure, Constants = k, Out0 = g.UavOf(aux), Pass = parity, GroupsX = gx, GroupsY = gy, GroupsZ = gz });
    }

    private void AddStats(RenderGraph g, int k, RenderBuffer v, RenderBuffer s, RenderBuffer aux)
    {
        var partial = g.Import(_partial);
        var stats = g.Import(_stats);
        g.AddComputePass("gas.stats.slices", FramePhase.Simulation).Read(v).Read(s).Read(aux).Write(partial).Execute(_execute);
        Push(new PassArgs { Entry = Entry.StatsSlices, Constants = k, In0 = g.SrvOf(v), In1 = g.SrvOf(s), In2 = g.SrvOf(aux), Out0 = g.UavOf(partial),
            GroupsX = (uint)_params.Nz, GroupsY = 1, GroupsZ = 1 });
        g.AddComputePass("gas.stats", FramePhase.Simulation).Read(partial).Write(stats).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Stats, Constants = k, In0 = g.SrvOf(partial), Out0 = g.UavOf(stats), GroupsX = 1, GroupsY = 1, GroupsZ = 1 });
    }

    private void AddClear(RenderGraph g, int k, RenderBuffer v, RenderBuffer s, RenderBuffer a0, RenderBuffer a1)
    {
        var (gx, gy, gz) = Groups();
        g.AddComputePass("gas.clear", FramePhase.Simulation).Write(v).Write(s).Write(a0).Write(a1).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Clear, Constants = k, Out0 = g.UavOf(v), Out1 = g.UavOf(s), Out2 = g.UavOf(a0), Out3 = g.UavOf(a1),
            GroupsX = gx, GroupsY = gy, GroupsZ = gz });
        _clearPending = false;
    }

    private (uint X, uint Y, uint Z) Groups() => ((uint)((_params.Nx + GroupX - 1) / GroupX), (uint)((_params.Ny + GroupY - 1) / GroupY),
        (uint)((_params.Nz + GroupZ - 1) / GroupZ));

    // ── вне кадра: тесты и отладка ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Поля с GPU (ожидание GPU; между кадрами). После <see cref="Reset"/> без шага — нули.</summary>
    public Fluid3DFields ReadFields()
    {
        var f = new Fluid3DFields(_params.Nx, _params.Ny, _params.Nz);
        if (_clearPending) return f;
        _renderer.ReadStorage(_velocity, MemoryMarshal.Cast<Half, ulong>(f.Velocity.AsSpan()));
        _renderer.ReadStorage(_velocityScratch, MemoryMarshal.Cast<Half, ulong>(f.VelocityScratch.AsSpan()));
        _renderer.ReadStorage(_scalars, MemoryMarshal.Cast<Half, uint>(f.Scalars.AsSpan()));
        _renderer.ReadStorage(_scalarScratch, MemoryMarshal.Cast<Half, uint>(f.ScalarScratch.AsSpan()));
        _renderer.ReadStorage(_scalarForward, MemoryMarshal.Cast<Half, uint>(f.ScalarForward.AsSpan()));
        _renderer.ReadStorage(_aux[_auxCurrent], MemoryMarshal.Cast<Half, uint>(f.Aux.AsSpan()));
        _renderer.ReadStorage(_aux[1 - _auxCurrent], MemoryMarshal.Cast<Half, uint>(f.AuxPrevious.AsSpan()));
        return f;
    }

    /// <summary>
    /// Загрузить поля и окно (загрузка с ожиданием GPU; между кадрами): хранилища пересоздаются. <paramref name="window"/> null — окно встанет у
    /// цели следующего шага без сдвига.
    /// </summary>
    public void Load(Fluid3DFields fields, Fluid3DWindow? window)
    {
        if (fields.Nx != _params.Nx || fields.Ny != _params.Ny || fields.Nz != _params.Nz)
            throw new ArgumentException($"поля {fields.Nx}×{fields.Ny}×{fields.Nz}, а сим {_params.Nx}×{_params.Ny}×{_params.Nz}", nameof(fields));
        Replace(ref _velocity, _renderer.CreateStorage<ulong>(_velocity.Name, MemoryMarshal.Cast<Half, ulong>(fields.Velocity.AsSpan())));
        Replace(ref _velocityScratch, _renderer.CreateStorage<ulong>(_velocityScratch.Name, MemoryMarshal.Cast<Half, ulong>(fields.VelocityScratch.AsSpan())));
        Replace(ref _scalars, _renderer.CreateStorage<uint>(_scalars.Name, MemoryMarshal.Cast<Half, uint>(fields.Scalars.AsSpan())));
        Replace(ref _scalarScratch, _renderer.CreateStorage<uint>(_scalarScratch.Name, MemoryMarshal.Cast<Half, uint>(fields.ScalarScratch.AsSpan())));
        Replace(ref _scalarForward, _renderer.CreateStorage<uint>(_scalarForward.Name, MemoryMarshal.Cast<Half, uint>(fields.ScalarForward.AsSpan())));
        var a0 = _renderer.CreateStorage<uint>(_aux[0].Name, MemoryMarshal.Cast<Half, uint>(fields.Aux.AsSpan()));
        var a1 = _renderer.CreateStorage<uint>(_aux[1].Name, MemoryMarshal.Cast<Half, uint>(fields.AuxPrevious.AsSpan()));
        _aux[0].Dispose();
        _aux[1].Dispose();
        _aux[0] = a0;
        _aux[1] = a1;
        _auxCurrent = 0;
        _clearPending = false;
        _window = window;
    }

    private static void Replace(ref GpuStorage slot, GpuStorage next)
    {
        slot.Dispose();
        slot = next;
    }

    /// <summary>Статистика последнего шага (ожидание GPU; между кадрами).</summary>
    public Fluid3DStats ReadStats()
    {
        Span<byte> raw = stackalloc byte[Fluid3DStats.Bytes];
        _renderer.ReadStorage(_stats, raw);
        return Fluid3DStats.Parse(raw);
    }

    /// <summary>Снимок для отладки (ожидание GPU; между кадрами): поля, окно, статистика по полю на CPU; срезы — <see cref="Fluid3DSnapshot.SaveSlices"/>.</summary>
    public Fluid3DSnapshot Snapshot()
    {
        var fields = ReadFields();
        var window = _window ?? Fluid3DWindow.Around(Vector3.Zero, _params);
        return new Fluid3DSnapshot(fields, window, Fluid3DStats.Compute(fields, window, LastStep ?? 0), _params.MaxSpeed);
    }

    public void Dispose()
    {
        _constants.Dispose();
        _velocity.Dispose();
        _velocityScratch.Dispose();
        _scalars.Dispose();
        _scalarScratch.Dispose();
        _scalarForward.Dispose();
        _aux[0].Dispose();
        _aux[1].Dispose();
        _partial.Dispose();
        _stats.Dispose();
    }

    // ── кадр ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void BeginFrame(RenderFrame frame)
    {
        if (frame.Number == _frame) return;
        _frame = frame.Number;
        _argCount = _argNext = _slots = 0;
    }

    private int PushConstants(in Fluid3DConstants constants)
    {
        if (_slots == _constants.Capacity) throw new InvalidOperationException($"газ {Name}: больше {MaxStepsPerFrame} шагов за кадр");
        _constants.Slot[_slots] = constants;
        return _slots++;
    }

    private void Push(in PassArgs args)
    {
        if (_argCount == _args.Length) Array.Resize(ref _args, _args.Length * 2);
        _args[_argCount++] = args;
    }

    /// <summary>Проход газа при исполнении графа: проходы идут в порядке регистрации — аргументы берутся по очереди.</summary>
    private void Execute(RenderPassContext ctx)
    {
        ref readonly var a = ref _args[_argNext++];
        ctx.SetPipeline(new ComputePipelineDesc(ShaderFile, Entries[(int)a.Entry]));
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotIndex, a.Constants);
        ctx.SetConstant(SlotIn0, a.In0);
        ctx.SetConstant(SlotIn1, a.In1);
        ctx.SetConstant(SlotIn2, a.In2);
        ctx.SetConstant(SlotIn3, a.In3);
        ctx.SetConstant(SlotOut0, a.Out0);
        ctx.SetConstant(SlotOut1, a.Out1);
        ctx.SetConstant(SlotOut2, a.Out2);
        ctx.SetConstant(SlotOut3, a.Out3);
        ctx.SetConstant(SlotPass, a.Pass);
        ctx.Dispatch(a.GroupsX, a.GroupsY, a.GroupsZ);
    }
}
