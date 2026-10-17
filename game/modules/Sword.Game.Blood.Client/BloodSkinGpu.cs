using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Game.Blood;

/// <summary>Элемент загрузки изменений: элемент буфера слота и два слова (<c>blood_skin.hlsl</c>, <c>CsScatter</c>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BloodSkinUpload
{
    public uint Element, X, Y, Pad;
}

/// <summary>
/// Кровь на коже на GPU: буфер на слот бойца (<c>uint2</c> × 2V: вид вершин меша для шейдера скина игры, затем сырое — толщина и рождение),
/// выделены при старте. Кадр (фаза Simulation): обнулить снятые слоты → разнести изменения CPU (<see cref="BloodSkin.Gather"/>, только
/// изменённые вершины, через кольцо кадра) → вид по функции свежести (<see cref="BloodLook"/>) у занятых слотов, если шаг крови сменился или
/// пришли изменения → все буферы в чтение шейдером. Кадр — 0 Б: аргументы — массивы, делегаты — поля.
/// </summary>
public sealed class BloodSkinGpu : IDisposable
{
    public const string ShaderFile = "blood/blood_skin.hlsl";
    public const int Group = 64;
    private const int SlotTarget = 23, SlotCount = 24, SlotEntries = 25, SlotFirst = 26, SlotNow = 27;   // после чисел вида (0…18)

    private readonly Renderer _renderer;
    private readonly BloodSkin _skin;
    private readonly GpuStorage[] _slots;
    private readonly int[] _srv;
    private FrameData<BloodSkinUpload> _upload;
    private readonly (int Uav, int First, int Count)[] _clearArgs, _scatterArgs, _lookArgs;
    private int _clears, _scatters, _looks;
    private readonly RenderBuffer[] _imported;
    private readonly int[] _slotFirst, _slotCount;
    private readonly Action<RenderPassContext> _clear, _scatter, _look, _ready;
    private BloodLookParams _params;
    private float _now;
    private long _lookedStep = -1;
    private long _builds;
    private bool _warm;

    public BloodSkinGpu(Renderer renderer, BloodSkin skin)
    {
        _renderer = renderer;
        _skin = skin;
        int n = skin.SlotCount;
        _slots = new GpuStorage[n];
        for (int s = 0; s < n; s++) _slots[s] = renderer.CreateStorage<ulong>($"blood.skin.{s}", (uint)(2 * skin.VertexCount));
        _srv = new int[n];
        Array.Fill(_srv, -1);
        _upload = renderer.CreateFrameData<BloodSkinUpload>("blood.skin.upload", 16384);
        _clearArgs = new (int, int, int)[n];
        _scatterArgs = new (int, int, int)[n];
        _lookArgs = new (int, int, int)[n];
        _imported = new RenderBuffer[n];
        _slotFirst = new int[n];
        _slotCount = new int[n];
        _clear = Clear;
        _scatter = Scatter;
        _look = Look;
        _ready = static _ => { };
    }

    /// <summary>Видеопамяти под буферы, Б.</summary>
    public long Bytes => (long)_slots.Length * 2 * _skin.VertexCount * 8;

    /// <summary>Слоты, у которых в последнем кадре пересчитан вид; загружено элементов в последнем кадре.</summary>
    public int LastLooks { get; private set; }
    public int LastUploads { get; private set; }

    /// <summary>Буфер слота (тесты, чтение на CPU между кадрами).</summary>
    public GpuStorage Storage(int slot) => _slots[slot];

    /// <summary>SRV вида слота для экземпляра скина; до первого кадра — −1.</summary>
    public int Srv(int slot) => (uint)slot < (uint)_srv.Length ? _srv[slot] : -1;

    /// <summary>
    /// Проходы кадра: <paramref name="now"/> — время шага крови, с; <paramref name="step"/> — номер шага (вид пересчитывается, когда он сменился
    /// или пришли изменения); <paramref name="look"/> — числа функции свежести.
    /// </summary>
    public void Build(RenderFrame frame, float now, long step, in BloodLookParams look)
    {
        var g = frame.Graph;
        int n = _slots.Length;
        // Первый кадр (служебный): конвейеры проходов собираются сразу (пустой вызов), а не при первой крови посреди боя.
        _warm = _builds++ == 0;
        for (int s = 0; s < n; s++)
        {
            _imported[s] = g.Import(_slots[s]);
            if (_srv[s] < 0) _srv[s] = g.SrvOf(_imported[s]);
        }
        _clears = _scatters = _looks = 0;
        for (int s = 0; s < n; s++)
            if (_skin.PendingClear(s, take: true)) _clearArgs[_clears++] = (s, 0, 2 * _skin.VertexCount);
        // Проходы — каждый кадр одни и те же (пустые — без работы): имена проходов в зонах профайлера и таймерах GPU заводятся при
        // старте, а не посреди боя.
        var clear = g.AddComputePass("blood.skin.clear", FramePhase.Simulation);
        for (int i = 0; i < _clears; i++)
        {
            int s = _clearArgs[i].Uav;
            clear = clear.Write(_imported[s]);
            _clearArgs[i].Uav = g.UavOf(_imported[s]);
        }
        clear.Execute(_clear);

        var entries = _skin.Gather();
        LastUploads = entries.Length;
        if (entries.Length > _upload.Capacity)
        {
            _upload.Dispose();
            _upload = _renderer.CreateFrameData<BloodSkinUpload>("blood.skin.upload", (uint)Math.Max(2 * entries.Length, 2 * (int)_upload.Capacity));
        }
        bool changed = entries.Length > 0;
        var scatter = g.AddComputePass("blood.skin.scatter", FramePhase.Simulation);
        if (changed)
        {
            var slot = _upload.Slot;
            Array.Clear(_slotCount);
            for (int i = 0; i < entries.Length; i++) Put(slot, entries, i);
            for (int s = 0; s < n; s++)
            {
                if (_slotCount[s] == 0) continue;
                scatter = scatter.Write(_imported[s]);
                _scatterArgs[_scatters++] = (g.UavOf(_imported[s]), _slotFirst[s], _slotCount[s]);
            }
        }
        scatter.Execute(_scatter);

        if (changed || step != _lookedStep || _clears > 0)
        {
            _lookedStep = step;
            _params = look;
            _now = now;
            for (int s = 0; s < n; s++)
                if (_skin.Used(s)) _lookArgs[_looks++] = (s, 0, _skin.VertexCount);
        }
        var pass = g.AddComputePass("blood.skin.look", FramePhase.Simulation);
        for (int i = 0; i < _looks; i++)
        {
            int s = _lookArgs[i].Uav;
            pass = pass.Write(_imported[s]);
            _lookArgs[i].Uav = g.UavOf(_imported[s]);
        }
        pass.Execute(_look);
        LastLooks = _looks;

        // Все буферы — в чтение шейдером до сцены (скин игры читает их bindless, граф о нём не знает).
        var ready = g.AddComputePass("blood.skin.ready", FramePhase.Simulation);
        for (int s = 0; s < n; s++) ready = ready.Read(_imported[s]);
        ready.Execute(_ready);
    }

    /// <summary>Записи по слотам подряд (<see cref="BloodSkin.Gather"/>): начало и число записей слота.</summary>
    private void Put(Span<BloodSkinUpload> slot, ReadOnlySpan<BloodSkinEntry> entries, int i)
    {
        var e = entries[i];
        if (_slotCount[e.Slot]++ == 0) _slotFirst[e.Slot] = i;
        slot[i] = new BloodSkinUpload { Element = (uint)e.Element, X = e.X, Y = e.Y };
    }

    private void Clear(RenderPassContext ctx)
    {
        if (_clears == 0 && !_warm) return;
        ctx.SetPipeline(new ComputePipelineDesc(ShaderFile, "CsClear"));
        for (int i = 0; i < _clears; i++)
        {
            ctx.SetConstant(SlotTarget, _clearArgs[i].Uav);
            ctx.SetConstant(SlotCount, _clearArgs[i].Count);
            ctx.Dispatch((uint)((_clearArgs[i].Count + Group - 1) / Group), 1);
        }
    }

    private void Scatter(RenderPassContext ctx)
    {
        if (_scatters == 0 && !_warm) return;
        ctx.SetPipeline(new ComputePipelineDesc(ShaderFile, "CsScatter"));
        ctx.SetConstant(SlotEntries, _upload.Srv);
        for (int i = 0; i < _scatters; i++)
        {
            ctx.SetConstant(SlotTarget, _scatterArgs[i].Uav);
            ctx.SetConstant(SlotFirst, _scatterArgs[i].First);
            ctx.SetConstant(SlotCount, _scatterArgs[i].Count);
            ctx.Dispatch((uint)((_scatterArgs[i].Count + Group - 1) / Group), 1);
        }
    }

    private void Look(RenderPassContext ctx)
    {
        if (_looks == 0 && !_warm) return;
        ctx.SetPipeline(new ComputePipelineDesc(ShaderFile, "CsLook"));
        Span<float> k = stackalloc float[BloodLookParams.Slots];
        BloodLook.Write(_params, k);
        for (int i = 0; i < k.Length; i++) ctx.SetConstant(i, k[i]);
        ctx.SetConstant(SlotNow, _now);
        for (int i = 0; i < _looks; i++)
        {
            ctx.SetConstant(SlotTarget, _lookArgs[i].Uav);
            ctx.SetConstant(SlotCount, _lookArgs[i].Count);
            ctx.Dispatch((uint)((_lookArgs[i].Count + Group - 1) / Group), 1);
        }
    }

    /// <summary>Числа шейдера в контракт.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Blood: blood on skin (Sword.Game.Blood.Client)")
        .Add("BLOOD_SKIN_GROUP", Group, "numthreads of the skin passes")
        .AddSlot("gSkinTarget", SlotTarget).AddSlot("gSkinCount", SlotCount).AddSlot("gSkinEntries", SlotEntries)
        .AddSlot("gSkinFirst", SlotFirst).AddSlot("gSkinNow", SlotNow);

    public void Dispose()
    {
        foreach (var s in _slots) s.Dispose();
        _upload.Dispose();
    }
}
