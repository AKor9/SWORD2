using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Engine.Fluid2D;

/// <summary>
/// 2D-сим LIVE-2 на GPU (README.md): постоянные гриды — хранилища <see cref="GpuStorage"/> half-битов (C, A — по 8 Б на тексель, два буфера
/// давления — по 4 Б, два грида кисти — по 16 Б), шаг — вычислительные проходы фазы <see cref="FramePhase.Simulation"/> (<c>fluid.painter</c>
/// при кисти <see cref="Fluid2DBrush"/> → <c>fluid.composite</c> → <c>fluid.advection</c> → <c>fluid.divergence</c> → <c>fluid.pressure.x</c> →
/// <c>fluid.pressure.y</c> → <c>fluid.stats.rows</c> → <c>fluid.stats</c>), выход — временная текстура RGBA16F кадра (<see cref="Export"/>).
/// Кадр — 0 Б: делегат прохода один, аргументы проходов — в массиве сима, по порядку исполнения. Вне кадра — синхронные
/// <see cref="ReadFields"/>, <see cref="Load"/>, <see cref="ReadBrush"/>, <see cref="LoadBrush"/>, <see cref="ReadStats"/>, <see cref="Snapshot"/>.
/// </summary>
public sealed class Fluid2DSim : IDisposable
{
    public const string ShaderFile = "fluid2d/fluid2d.hlsl";

    /// <summary>Шагов (и экспортов) за кадр не больше — элементов кольца констант на кадр.</summary>
    public const int MaxStepsPerFrame = 64;

    internal const int Group = 8, StatsThreads = 256, StatsRows = 8;
    private const int SlotConstants = 0, SlotIndex = 1, SlotIn0 = 2, SlotIn1 = 3, SlotIn2 = 4, SlotOut0 = 5, SlotOut1 = 6, SlotOut2 = 7,
        SlotOut3 = 8, SlotPass = 9;

    private enum Entry { Composite, Advection, Divergence, Pressure, Export, Clear, StatsRows, Stats, Painter, PainterClear }

    private static readonly string[] Entries =
        ["CsComposite", "CsAdvection", "CsDivergence", "CsPressure", "CsExport", "CsClear", "CsStatsRows", "CsStats", "CsPainter", "CsPainterClear"];

    /// <summary>Числа и слоты шейдера сима в контракт (вызывает тот, кто собирает контракт рендера: модуль — <c>ClientBuilder.Shaders</c>).</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fluid2D: 2D fluid core (Sword.Engine.Fluid2D)")
        .AddSize<Fluid2DConstants>("FLUID2D_CONSTANTS_SIZE")
        .AddSize<StatsPartial>("FLUID2D_STATS_PARTIAL_SIZE")
        .Add("FLUID2D_GROUP", Group, "numthreads grid passes (8x8)")
        .Add("FLUID2D_STATS_THREADS", StatsThreads)
        .Add("FLUID2D_STATS_ROWS", StatsRows, "rows of C per stats group")
        .Add("FLUID2D_EXPORT_INVERT", Fluid2DConstants.ExportInvert)
        .Add("FLUID2D_EXPORT_INVERTED_ALPHA", Fluid2DConstants.ExportInvertedAlpha)
        .Add("FLUID2D_POINT_WORDS", Fluid2DBrushPoint.Words, "uint words of a brush point (Fluid2DBrushPoint)")
        .Add("FLUID2D_PAINTER_PAINTER_MODE", Fluid2DConstants.PainterPainterMode)
        .Add("FLUID2D_PAINTER_ADDITIVE", Fluid2DConstants.PainterAdditive)
        .Add("FLUID2D_PAINTER_EDGE_STEP", Fluid2DConstants.PainterEdgeStep)
        .Add("FLUID2D_PAINTER_NOISE", Fluid2DConstants.PainterNoise)
        .Add("FLUID2D_PAINTER_BITMAP", Fluid2DConstants.PainterBitmap)
        .Add("FLUID2D_PAINTER_BITMAP_DENSITY_ONLY", Fluid2DConstants.PainterBitmapDensityOnly)
        .AddSlot("gFluidConstants", SlotConstants)
        .AddSlot("gFluidIndex", SlotIndex)
        .AddSlot("gFluidIn0", SlotIn0)
        .AddSlot("gFluidIn1", SlotIn1)
        .AddSlot("gFluidIn2", SlotIn2)
        .AddSlot("gFluidOut0", SlotOut0)
        .AddSlot("gFluidOut1", SlotOut1)
        .AddSlot("gFluidOut2", SlotOut2)
        .AddSlot("gFluidOut3", SlotOut3)
        .AddSlot("gFluidPass", SlotPass);

    [StructLayout(LayoutKind.Sequential)]
    internal struct StatsPartial
    {
        public float Sum, SumX, SumY, MaxSpeed, MaxDensity, SumWet;
        public uint NonFinite, Pad;
    }

    private struct PassArgs
    {
        public Entry Entry;
        public int Constants, In0, In1, In2, Out0, Out1, Out2, Out3, Pass;
        public uint GroupsX, GroupsY;
    }

    private readonly Renderer _renderer;
    private readonly FrameData<Fluid2DConstants> _constants;
    private readonly Fluid2DNoise _density, _amp, _brushNoise, _bitmap;
    private readonly string _outputName;
    private readonly GpuStorage _stats, _partial;
    private GpuStorage _composite, _advected;
    private readonly GpuStorage[] _pressure = new GpuStorage[2];
    private readonly GpuStorage?[] _painter = new GpuStorage?[2];
    private FrameData<uint>? _brushData;
    private int _current, _painterCurrent, _brushUsed;
    private bool _clearPending, _painterClearPending = true;
    private Fluid2DParams _params;

    private readonly Action<RenderPassContext> _execute;
    private PassArgs[] _args = new PassArgs[16];
    private int _argCount, _argNext, _slots;
    private ulong _frame = ulong.MaxValue;

    /// <param name="densityNoise">Шум плотности (<c>T_Noise3</c>): нужен при <see cref="Fluid2DParams.DensityNoiseEnabled"/>.</param>
    /// <param name="ampNoise">Amp-шум скорости (<c>T_NoiseTemplate1</c>): нужен при <see cref="Fluid2DParams.VeloAmplitudeNoiseEnabled"/>.</param>
    /// <param name="brushNoise">Шум кисти (<c>T_NoiseTemplate2</c>, RG линейные): нужен Painter при <see cref="Fluid2DParams.BrushNoiseUsed"/>.</param>
    /// <param name="bitmap">Битмап кисти (слои 4–6, RGB); без него слои 4–6 — 0.</param>
    public Fluid2DSim(Renderer renderer, string name, Fluid2DParams parameters, Fluid2DNoise densityNoise = default, Fluid2DNoise ampNoise = default,
        Fluid2DNoise brushNoise = default, Fluid2DNoise bitmap = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        parameters.Validate();
        _renderer = renderer;
        _params = parameters;
        _density = densityNoise;
        _amp = ampNoise;
        _brushNoise = brushNoise;
        _bitmap = bitmap;
        Name = name;
        CheckNoises(parameters);
        int n = parameters.Resolution;
        _outputName = $"fluid.{name}.output";
        _constants = renderer.CreateFrameData<Fluid2DConstants>($"fluid.{name}.constants", MaxStepsPerFrame + 2);
        _composite = renderer.CreateStorage<ulong>($"fluid.{name}.composite", (uint)(n * n));
        _advected = renderer.CreateStorage<ulong>($"fluid.{name}.advected", (uint)(n * n));
        _pressure[0] = renderer.CreateStorage<uint>($"fluid.{name}.pressure0", (uint)(n * n));
        _pressure[1] = renderer.CreateStorage<uint>($"fluid.{name}.pressure1", (uint)(n * n));
        _partial = renderer.CreateStorage<StatsPartial>($"fluid.{name}.stats.partial", (uint)StatsGroups(n));
        _stats = renderer.CreateStorage<uint>($"fluid.{name}.stats", Fluid2DStats.Bytes / sizeof(uint));
        _execute = Execute;
    }

    public string Name { get; }

    public int Resolution => _params.Resolution;

    /// <summary>Параметры — на ходу, со следующего шага; сторона сима и грида кисти не меняются.</summary>
    public Fluid2DParams Params
    {
        get => _params;
        set
        {
            value.Validate();
            if (value.Resolution != _params.Resolution || value.PaintBufferDownScaleFactor != _params.PaintBufferDownScaleFactor)
                throw new ArgumentException($"сторона сима {_params.Resolution}² (кисть {_params.BrushResolution}²) не меняется — нужен новый сим");
            CheckNoises(value);
            _params = value;
        }
    }

    /// <summary>Шагов с создания или сброса.</summary>
    public ulong Steps { get; private set; }

    /// <summary>Последний шаг сима (номер из <see cref="Fluid2DStepInput.Step"/>); до первого — null.</summary>
    public ulong? LastStep { get; private set; }

    /// <summary>Сырые данные статистики последнего шага (<see cref="Fluid2DStats.Bytes"/> байт; <see cref="Fluid2DStats.Parse"/>).</summary>
    public GpuStorage StatsStorage => _stats;

    /// <summary>
    /// Поле C после Composite последнего шага (<c>StructuredBuffer&lt;uint2&gt;</c>: half vx, vy, ρ, wet; тексель (x, y) — элемент x + N·y, ряд 0 —
    /// v = 0) — потребителю шага: читать проходом, зарегистрированным после шага; следующий шаг его перезапишет, <see cref="Load"/> меняет хранилище.
    /// </summary>
    public GpuStorage Composite => _composite;

    /// <summary>Обнулить поля и грид кисти: первым проходом следующего шага или экспорта (<c>fluid.clear</c>, <c>fluid.painter.clear</c>).</summary>
    public void Reset()
    {
        _clearPending = true;
        _painterClearPending = true;
        Steps = 0;
        LastStep = null;
    }

    /// <summary>
    /// Проходы одного шага в кадр <paramref name="frame"/>. <paramref name="brush"/> — грид кисти (N/PaintBufferDownScaleFactor)² ×
    /// <see cref="Fluid2DBrushTexel"/>, его пишет вызывающий до шага; null — без кисти.
    /// </summary>
    public void Step(RenderFrame frame, in Fluid2DStepInput input, GpuStorage? brush = null) => AddStep(frame, input, brush, null, null, false);

    /// <summary>
    /// Шаг с кистью: проход <c>fluid.painter</c> по точкам шага <paramref name="brush"/> (после <see cref="Fluid2DBrush.End"/> того же
    /// <see cref="Fluid2DStepInput.Step"/>; параметры кисти и сима совпадают) пишет грид кисти сима, его читают Composite и Divergence.
    /// Данные точек копируются в кадр сразу — кисть можно готовить к следующему шагу.
    /// </summary>
    public void Step(RenderFrame frame, in Fluid2DStepInput input, Fluid2DBrush brush) => AddStep(frame, input, null, brush, null, false);

    /// <summary>Одна стадия (проверка стадий): буферы — как у <see cref="Fluid2DStage"/>, давление ролями не меняется, статистики нет.</summary>
    internal void AddStage(RenderFrame frame, in Fluid2DStepInput input, GpuStorage? brush, Fluid2DStage stage) => AddStep(frame, input, brush, null, stage, false);

    /// <summary>Только Painter (проверка стадии): грид кисти сима → следующий грид кисти.</summary>
    internal void AddPainterStage(RenderFrame frame, in Fluid2DStepInput input, Fluid2DBrush brush) => AddStep(frame, input, null, brush, null, true);

    private void AddStep(RenderFrame frame, in Fluid2DStepInput input, GpuStorage? brush, Fluid2DBrush? painter, Fluid2DStage? only, bool painterOnly)
    {
        if (painter is null) CheckBrush(brush);
        else CheckPainter(painter, input);
        BeginFrame(frame);
        var g = frame.Graph;
        var constants = Fluid2DConstants.Build(_params, input, brush is not null || painter is not null, _amp, _density);
        int dataSrv = 0;
        if (painter is not null)
        {
            uint wordBase = WriteBrush(painter);
            dataSrv = _brushData!.Srv;
            constants.SetPainter(_params, input, painter, wordBase, _brushNoise, _bitmap);
        }
        int k = PushConstants(constants);
        var c = g.Import(_composite);
        var a = g.Import(_advected);
        var p = g.Import(_pressure[_current]);
        var s = g.Import(_pressure[1 - _current]);
        if (_clearPending) AddClear(g, k, c, a, p, s);
        RenderBuffer b = default;
        int brushSrv = 0;
        if (painter is not null)
        {
            b = AddPainter(g, k, dataSrv);
            brushSrv = g.SrvOf(b);
            if (painterOnly) return;
        }
        else if (brush is not null)
        {
            b = g.Import(brush);
            brushSrv = g.SrvOf(b);
        }
        bool hasBrush = brush is not null || painter is not null;
        uint groups = (uint)((_params.Resolution + Group - 1) / Group);

        if (only is null or Fluid2DStage.Composite)
        {
            var pass = g.AddComputePass("fluid.composite", FramePhase.Simulation).Read(a).Read(p);
            if (hasBrush) pass = pass.Read(b);
            pass.Write(c).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Composite, Constants = k, In0 = g.SrvOf(a), In1 = g.SrvOf(p), In2 = brushSrv, Out0 = g.UavOf(c), GroupsX = groups, GroupsY = groups });
        }
        if (only is null or Fluid2DStage.Advection)
        {
            g.AddComputePass("fluid.advection", FramePhase.Simulation).Read(c).Write(a).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Advection, Constants = k, In0 = g.SrvOf(c), Out0 = g.UavOf(a), GroupsX = groups, GroupsY = groups });
        }
        if (only is null or Fluid2DStage.Divergence)
        {
            var pass = g.AddComputePass("fluid.divergence", FramePhase.Simulation).Read(a).Read(p);
            if (hasBrush) pass = pass.Read(b);
            pass.Write(s).Execute(_execute);
            Push(new PassArgs { Entry = Entry.Divergence, Constants = k, In0 = g.SrvOf(a), In1 = g.SrvOf(p), In2 = brushSrv, Out0 = g.UavOf(s), GroupsX = groups, GroupsY = groups });
        }
        if (only is null)
        {
            if (_params.CrudePressureSolver) AddPressure(g, k, groups, (input.Step & 1) != 0, s, p);
            else
            {
                AddPressure(g, k, groups, false, s, p);
                AddPressure(g, k, groups, true, p, s);
                _current = 1 - _current;
            }
            AddStats(g, k, c);
            Steps++;
            LastStep = input.Step;
        }
        else if (only == Fluid2DStage.PressureX) AddPressure(g, k, groups, false, s, p);
        else if (only == Fluid2DStage.PressureY) AddPressure(g, k, groups, true, p, s);
    }

    /// <summary>Painter: грид кисти сима (прошлый шаг) → второй грид; они меняются ролями. Возвращает записанный грид.</summary>
    private RenderBuffer AddPainter(RenderGraph g, int k, int dataSrv)
    {
        uint groups = (uint)((_params.BrushResolution + Group - 1) / Group);
        var source = g.Import(_painter[_painterCurrent]!);
        var target = g.Import(_painter[1 - _painterCurrent]!);
        if (_painterClearPending)
        {
            g.AddComputePass("fluid.painter.clear", FramePhase.Simulation).Write(source).Write(target).Execute(_execute);
            Push(new PassArgs { Entry = Entry.PainterClear, Constants = k, Out0 = g.UavOf(source), Out1 = g.UavOf(target), GroupsX = groups, GroupsY = groups });
            _painterClearPending = false;
        }
        g.AddComputePass("fluid.painter", FramePhase.Simulation).Read(source).Write(target).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Painter, Constants = k, In0 = g.SrvOf(source), In1 = dataSrv, Out0 = g.UavOf(target), GroupsX = groups, GroupsY = groups });
        _painterCurrent = 1 - _painterCurrent;
        return target;
    }

    /// <summary>
    /// Шаг кисти в буфер кадра (таблица ячеек, записи, точки подряд с <see cref="_brushUsed"/>). Буфер растёт при новом максимуме слов за
    /// кадр: прежний освобождается отложенно, уже записанные шаги кадра читают его.
    /// </summary>
    private uint WriteBrush(Fluid2DBrush brush)
    {
        int words = brush.Words;
        if (_brushData is null || _brushUsed + words > _brushData.Capacity)
        {
            int perStep = 1024 + _params.BrushMaxEntries + _params.BrushMaxPoints * Fluid2DBrushPoint.Words;
            _brushData?.Dispose();
            _brushData = _renderer.CreateFrameData<uint>($"fluid.{Name}.brush", (uint)Math.Max(2 * (_brushUsed + words), 4 * perStep));
            _brushUsed = 0;
        }
        var slot = _brushData.Slot;
        int o = _brushUsed;
        brush.Cells.CopyTo(slot[o..]);
        o += brush.Cells.Length;
        brush.Entries.CopyTo(slot[o..]);
        o += brush.Entries.Length;
        MemoryMarshal.Cast<Fluid2DBrushPoint, uint>(brush.Records).CopyTo(slot[o..]);
        uint wordBase = (uint)_brushUsed;
        _brushUsed += words;
        return wordBase;
    }

    private void AddPressure(RenderGraph g, int k, uint groups, bool vertical, RenderBuffer source, RenderBuffer destination)
    {
        g.AddComputePass(vertical ? "fluid.pressure.y" : "fluid.pressure.x", FramePhase.Simulation).Read(source).Write(destination).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Pressure, Constants = k, In0 = g.SrvOf(source), Out0 = g.UavOf(destination), Pass = vertical ? 1 : 0, GroupsX = groups, GroupsY = groups });
    }

    private void AddStats(RenderGraph g, int k, RenderBuffer composite)
    {
        var partial = g.Import(_partial);
        var stats = g.Import(_stats);
        g.AddComputePass("fluid.stats.rows", FramePhase.Simulation).Read(composite).Write(partial).Execute(_execute);
        Push(new PassArgs { Entry = Entry.StatsRows, Constants = k, In0 = g.SrvOf(composite), Out0 = g.UavOf(partial), GroupsX = (uint)StatsGroups(_params.Resolution), GroupsY = 1 });
        g.AddComputePass("fluid.stats", FramePhase.Simulation).Read(partial).Write(stats).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Stats, Constants = k, In0 = g.SrvOf(partial), Out0 = g.UavOf(stats), GroupsX = 1, GroupsY = 1 });
    }

    private void AddClear(RenderGraph g, int k, RenderBuffer c, RenderBuffer a, RenderBuffer p, RenderBuffer s)
    {
        uint groups = (uint)((_params.Resolution + Group - 1) / Group);
        g.AddComputePass("fluid.clear", FramePhase.Simulation).Write(c).Write(a).Write(p).Write(s).Execute(_execute);
        Push(new PassArgs { Entry = Entry.Clear, Constants = k, Out0 = g.UavOf(c), Out1 = g.UavOf(a), Out2 = g.UavOf(p), Out3 = g.UavOf(s), GroupsX = groups, GroupsY = groups });
        _clearPending = false;
    }

    /// <summary>
    /// Выход кадра <c>RT_Composite</c> — временная текстура RGBA16F N² (clamp-выборка — у читающего): (v·edge10, ρ^Contrast, max(wet·маски, 0.01)).
    /// Проход <c>fluid.export</c> читает C (состояние после Composite последнего шага), зовётся раз за кадр, после шагов кадра.
    /// </summary>
    public RenderTexture Export(RenderFrame frame)
    {
        BeginFrame(frame);
        var g = frame.Graph;
        int k = PushConstants(Fluid2DConstants.Build(_params, new Fluid2DStepInput(LastStep ?? 0, default, default), false, _amp, _density));
        var c = g.Import(_composite);
        if (_clearPending) AddClear(g, k, c, g.Import(_advected), g.Import(_pressure[_current]), g.Import(_pressure[1 - _current]));
        int n = _params.Resolution;
        var output = g.CreateTexture(_outputName, new TextureDesc(TextureFormat.Rgba16Float, TextureUsage.Storage, n, n));
        g.AddComputePass("fluid.export", FramePhase.Simulation).Read(c).Write(output).Execute(_execute);
        uint groups = (uint)((n + Group - 1) / Group);
        Push(new PassArgs { Entry = Entry.Export, Constants = k, In0 = g.SrvOf(c), Out0 = g.UavOf(output), GroupsX = groups, GroupsY = groups });
        return output;
    }

    // ── вне кадра: тесты и отладка ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Поля с GPU (ожидание GPU; между кадрами). После <see cref="Reset"/> без шага — нули.</summary>
    public Fluid2DFields ReadFields()
    {
        var f = new Fluid2DFields(_params.Resolution);
        if (_clearPending) return f;
        _renderer.ReadStorage(_composite, MemoryMarshal.Cast<Half, ulong>(f.Composite.AsSpan()));
        _renderer.ReadStorage(_advected, MemoryMarshal.Cast<Half, ulong>(f.Advected.AsSpan()));
        _renderer.ReadStorage(_pressure[_current], MemoryMarshal.Cast<Half, uint>(f.Pressure.AsSpan()));
        _renderer.ReadStorage(_pressure[1 - _current], MemoryMarshal.Cast<Half, uint>(f.Scratch.AsSpan()));
        return f;
    }

    /// <summary>Загрузить поля (загрузка с ожиданием GPU; между кадрами): хранилища пересоздаются, прежние освобождаются отложенно.</summary>
    public void Load(Fluid2DFields fields)
    {
        if (fields.N != _params.Resolution) throw new ArgumentException($"поля {fields.N}², а сим {_params.Resolution}²", nameof(fields));
        Replace(ref _composite, _renderer.CreateStorage<ulong>(_composite.Name, MemoryMarshal.Cast<Half, ulong>(fields.Composite.AsSpan())));
        Replace(ref _advected, _renderer.CreateStorage<ulong>(_advected.Name, MemoryMarshal.Cast<Half, ulong>(fields.Advected.AsSpan())));
        var p = _renderer.CreateStorage<uint>(_pressure[0].Name, MemoryMarshal.Cast<Half, uint>(fields.Pressure.AsSpan()));
        var s = _renderer.CreateStorage<uint>(_pressure[1].Name, MemoryMarshal.Cast<Half, uint>(fields.Scratch.AsSpan()));
        _pressure[0].Dispose();
        _pressure[1].Dispose();
        _pressure[0] = p;
        _pressure[1] = s;
        _current = 0;
        _clearPending = false;
    }

    /// <summary>Грид кисти после последнего Painter (ожидание GPU; между кадрами); кисти не было или после сброса — нули.</summary>
    public Fluid2DBrushTexel[] ReadBrush()
    {
        var grid = new Fluid2DBrushTexel[_params.BrushResolution * _params.BrushResolution];
        if (_painter[_painterCurrent] is { } storage && !_painterClearPending) _renderer.ReadStorage(storage, grid.AsSpan());
        return grid;
    }

    /// <summary>Загрузить грид кисти (прошлая краска следующего Painter; между кадрами).</summary>
    public void LoadBrush(ReadOnlySpan<Fluid2DBrushTexel> grid)
    {
        int bn = _params.BrushResolution;
        if (grid.Length != bn * bn) throw new ArgumentException($"грид кисти {grid.Length} текселей, а нужно {bn}²", nameof(grid));
        EnsurePainter();
        var next = _renderer.CreateStorage<Fluid2DBrushTexel>(_painter[_painterCurrent]!.Name, grid);
        _painter[_painterCurrent]!.Dispose();
        _painter[_painterCurrent] = next;
        _painterClearPending = false;
    }

    private static void Replace(ref GpuStorage slot, GpuStorage next)
    {
        slot.Dispose();
        slot = next;
    }

    /// <summary>Статистика последнего шага (ожидание GPU; между кадрами).</summary>
    public Fluid2DStats ReadStats()
    {
        Span<byte> raw = stackalloc byte[Fluid2DStats.Bytes];
        _renderer.ReadStorage(_stats, raw);
        return Fluid2DStats.Parse(raw);
    }

    /// <summary>Снимок полей C и грида кисти (если был Painter) для отладки (ожидание GPU; между кадрами): статистика — по полю на CPU.</summary>
    public Fluid2DSnapshot Snapshot()
    {
        var fields = ReadFields();
        var brush = _painter[0] is null ? null : ReadBrush();
        return new Fluid2DSnapshot(fields, Fluid2DStats.Compute(fields, LastStep ?? 0), _params.VelocityClamp, brush,
            _params.BrushVelocityClamp * _params.BrushVelocityMultXY);
    }

    public void Dispose()
    {
        _constants.Dispose();
        _brushData?.Dispose();
        _composite.Dispose();
        _advected.Dispose();
        _pressure[0].Dispose();
        _pressure[1].Dispose();
        _painter[0]?.Dispose();
        _painter[1]?.Dispose();
        _partial.Dispose();
        _stats.Dispose();
    }

    // ── кадр ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void BeginFrame(RenderFrame frame)
    {
        if (frame.Number == _frame) return;
        _frame = frame.Number;
        _argCount = _argNext = _slots = 0;
        _brushUsed = 0;
    }

    private int PushConstants(in Fluid2DConstants constants)
    {
        if (_slots == _constants.Capacity)
            throw new InvalidOperationException($"сим {Name}: больше {MaxStepsPerFrame} шагов за кадр");
        _constants.Slot[_slots] = constants;
        return _slots++;
    }

    private void Push(in PassArgs args)
    {
        if (_argCount == _args.Length) Array.Resize(ref _args, _args.Length * 2);
        _args[_argCount++] = args;
    }

    /// <summary>Проход сима при исполнении графа: проходы сима идут в порядке регистрации — аргументы берутся по очереди.</summary>
    private void Execute(RenderPassContext ctx)
    {
        ref readonly var a = ref _args[_argNext++];
        ctx.SetPipeline(new ComputePipelineDesc(ShaderFile, Entries[(int)a.Entry]));
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotIndex, a.Constants);
        ctx.SetConstant(SlotIn0, a.In0);
        ctx.SetConstant(SlotIn1, a.In1);
        ctx.SetConstant(SlotIn2, a.In2);
        ctx.SetConstant(SlotOut0, a.Out0);
        ctx.SetConstant(SlotOut1, a.Out1);
        ctx.SetConstant(SlotOut2, a.Out2);
        ctx.SetConstant(SlotOut3, a.Out3);
        ctx.SetConstant(SlotPass, a.Pass);
        ctx.Dispatch(a.GroupsX, a.GroupsY);
    }

    private void CheckBrush(GpuStorage? brush)
    {
        if (brush is null) return;
        int bn = _params.BrushResolution;
        if (brush.Stride != 16 || brush.Count != (uint)(bn * bn))
            throw new ArgumentException($"кисть {brush.Name}: {brush.Count} × {brush.Stride} Б, нужно {bn}² × 16 Б (Fluid2DBrushTexel)", nameof(brush));
    }

    private void CheckPainter(Fluid2DBrush brush, in Fluid2DStepInput input)
    {
        if (!brush.Ready || brush.Step != input.Step)
            throw new InvalidOperationException($"сим {Name}: кисть не готова к шагу {input.Step} (Begin/Add/End того же шага)");
        if (!Equals(brush.Params, _params)) throw new InvalidOperationException($"сим {Name}: параметры кисти и сима различаются");
        if (_params.BrushNoiseUsed && _brushNoise.Width <= 0) throw new InvalidOperationException($"сим {Name}: шум кисти включён, текстуры нет");
        EnsurePainter();
    }

    private void EnsurePainter()
    {
        if (_painter[0] is not null) return;
        int bn = _params.BrushResolution;
        _painter[0] = _renderer.CreateStorage<Fluid2DBrushTexel>($"fluid.{Name}.brush0", (uint)(bn * bn));
        _painter[1] = _renderer.CreateStorage<Fluid2DBrushTexel>($"fluid.{Name}.brush1", (uint)(bn * bn));
        _painterCurrent = 0;
        _painterClearPending = true;
    }

    private void CheckNoises(Fluid2DParams p)
    {
        if (p.VeloAmplitudeNoiseEnabled && _amp.Width <= 0) throw new ArgumentException($"сим {Name}: amp-шум включён, текстуры нет");
        if (p.DensityNoiseEnabled && _density.Width <= 0) throw new ArgumentException($"сим {Name}: шум плотности включён, текстуры нет");
    }

    private static int StatsGroups(int n) => (n + StatsRows - 1) / StatsRows;
}
