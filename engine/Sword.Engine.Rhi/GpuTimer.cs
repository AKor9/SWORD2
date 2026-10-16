using Vortice.Direct3D12;

namespace Sword.Engine.Rhi;

/// <summary>
/// GPU-время кадра и его проходов — таймстемпы D3D12 на graphics-очереди. Разбор — после ожидания слота кадра
/// (данные кадра N − 3, CPU не стоит). Замер 0 — кадр целиком. Публикуется только отправленный кадр, номер опубликованного
/// только растёт. Всегда включён; установившийся кадр памяти не выделяет.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    /// <summary>Замеров на кадр: кадр целиком (замер 0) и до <c>MaxScopes − 1</c> проходов.</summary>
    public const int MaxScopes = 256;

    private const int QueriesPerSlot = MaxScopes * 2;

    private readonly ID3D12QueryHeap _heap;
    private readonly ID3D12Resource _readback;
    private readonly Slot[] _slots = new Slot[GraphicsDevice.FrameCount];

    /// <summary>Слот кадра в полёте: имена замеров, их число, номер кадра; ждёт разбора.</summary>
    private sealed class Slot
    {
        public readonly string[] Names = new string[MaxScopes];
        public int Count, Dropped;
        public ulong Frame;
        public bool Pending;
    }

    private Slot? _open;
    private int _openIndex;

    private readonly string[] _names = new string[MaxScopes];
    private readonly double[] _ms = new double[MaxScopes];

    internal GpuTimer(ID3D12Device device, ID3D12CommandQueue queue)
    {
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
        queue.GetTimestampFrequency(out ulong frequency);
        Frequency = frequency;
        _heap = device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp,
            (uint)(QueriesPerSlot * GraphicsDevice.FrameCount)));
        _heap.Name = "Sword.GpuTimer.Timestamps";
        try
        {
            // Буфер readback создаётся в CopyDest и в нём остаётся (ResolveQueryData пишет в CopyDest).
            _readback = device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
                ResourceDescription.Buffer((ulong)QueriesPerSlot * GraphicsDevice.FrameCount * sizeof(ulong)), ResourceStates.CopyDest);
            _readback.Name = "Sword.GpuTimer.Readback";
        }
        catch
        {
            _heap.Dispose();
            throw;
        }
    }

    /// <summary>Частота таймстемпов очереди, Гц (тиков GPU в секунду).</summary>
    public ulong Frequency { get; }

    /// <summary>Замерять (по умолчанию — да); выключатель — для замера цены таймстемпов. Действует с ближайшего <see cref="BeginFrame"/>.</summary>
    public bool Enabled { get; set; } = true;

    // ── опубликованный кадр ────────────────────────────────────────────────

    /// <summary>Опубликованных кадров с начала работы (0 — ещё ни одного); растёт с каждым новым.</summary>
    public long Sequence { get; private set; }

    /// <summary>Номер кадра (<see cref="GraphicsDevice.FrameNumber"/> при его записи), чьи времена опубликованы.</summary>
    public ulong Frame { get; private set; }

    /// <summary>Кадр целиком на GPU, мс: от начала списка команд до его конца (проходы, барьеры между ними, копии снимка).</summary>
    public double FrameMs { get; private set; }

    /// <summary>Сумма проходов кадра, мс.</summary>
    public double PassesMs { get; private set; }

    /// <summary>Проходов в опубликованном кадре.</summary>
    public int Count { get; private set; }

    /// <summary>Проходов, не замеренных в том кадре: их было больше <c>MaxScopes − 1</c>.</summary>
    public int Dropped { get; private set; }

    public string NameOf(int pass) => _names[pass];

    public double MillisecondsOf(int pass) => _ms[pass];

    // ── кадр ───────────────────────────────────────────────────────────────

    /// <summary>Открыт список команд кадра в разобранном слоте: замер 0 — начало кадра.</summary>
    internal void BeginFrame(ID3D12GraphicsCommandList list, int slot, ulong frame)
    {
        _open = null;
        if (!Enabled) return;
        var s = _slots[slot];
        s.Pending = false;
        s.Frame = frame;
        s.Count = 1;
        s.Dropped = 0;
        s.Names[0] = "frame";
        _open = s;
        _openIndex = slot;
        list.EndQuery(_heap, QueryType.Timestamp, (uint)(slot * QueriesPerSlot));
    }

    /// <summary>Начало прохода <paramref name="name"/>; номер замера или −1 (не замеряется: выключен, нет кадра, их слишком много).</summary>
    public int Begin(ID3D12GraphicsCommandList list, string name)
    {
        var s = _open;
        if (s is null) return -1;
        if (s.Count == MaxScopes)
        {
            s.Dropped++;
            return -1;
        }
        int scope = s.Count++;
        s.Names[scope] = name;
        list.EndQuery(_heap, QueryType.Timestamp, (uint)(_openIndex * QueriesPerSlot + scope * 2));
        return scope;
    }

    /// <summary>Конец прохода с номером из <see cref="Begin"/> (−1 — ничего).</summary>
    public void End(ID3D12GraphicsCommandList list, int scope)
    {
        if (scope <= 0 || _open is null) return;
        list.EndQuery(_heap, QueryType.Timestamp, (uint)(_openIndex * QueriesPerSlot + scope * 2 + 1));
    }

    /// <summary>Конец кадра — до закрытия списка: замер 0 закрыт, все замеры кадра — в readback его слота.</summary>
    internal void EndFrame(ID3D12GraphicsCommandList list)
    {
        if (_open is not { } s) return;
        int at = _openIndex * QueriesPerSlot;
        list.EndQuery(_heap, QueryType.Timestamp, (uint)(at + 1));
        list.ResolveQueryData(_heap, QueryType.Timestamp, (uint)at, (uint)(s.Count * 2), _readback, (ulong)at * sizeof(ulong));
    }

    /// <summary>Кадр отправлен и забор за ним поставлен: слот ждёт разбора (после ожидания этого забора).</summary>
    internal void Commit()
    {
        if (_open is { } s) s.Pending = true;
        _open = null;
    }

    /// <summary>Кадр брошен (список закрыт без отправки): его замеров не было — слот не разбирается.</summary>
    internal void Abandon() => _open = null;

    /// <summary>Разобрать слот после ожидания его забора; публикуется, если кадр новее опубликованного.</summary>
    internal void Collect(int slot)
    {
        var s = _slots[slot];
        if (!s.Pending) return;
        s.Pending = false;
        if (Sequence > 0 && s.Frame <= Frame) return;
        Publish(s, slot);
    }

    /// <summary>Разобрать все отправленные слоты по порядку кадров — после ожидания простоя GPU.</summary>
    internal void CollectAll()
    {
        while (true)
        {
            int next = -1;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Pending && (next < 0 || _slots[i].Frame < _slots[next].Frame)) next = i;
            if (next < 0) return;
            Collect(next);
        }
    }

    private unsafe void Publish(Slot s, int slot)
    {
        if (Frequency == 0) return;
        double msPerTick = 1000.0 / Frequency;
        ulong* mapped = (ulong*)_readback.Map<byte>(0);
        try
        {
            var stamps = new ReadOnlySpan<ulong>(mapped + slot * QueriesPerSlot, s.Count * 2);
            FrameMs = Span(stamps[0], stamps[1], msPerTick);
            double passes = 0;
            int count = s.Count - 1;
            for (int i = 0; i < count; i++)
            {
                double ms = Span(stamps[(i + 1) * 2], stamps[(i + 1) * 2 + 1], msPerTick);
                _names[i] = s.Names[i + 1];
                _ms[i] = ms;
                passes += ms;
            }
            PassesMs = passes;
            Count = count;
            Dropped = s.Dropped;
            Frame = s.Frame;
            Sequence++;
        }
        finally
        {
            _readback.Unmap(0);
        }
    }

    private static double Span(ulong begin, ulong end, double msPerTick) => end > begin ? (end - begin) * msPerTick : 0;

    public void Dispose()
    {
        _readback.Dispose();
        _heap.Dispose();
    }
}
