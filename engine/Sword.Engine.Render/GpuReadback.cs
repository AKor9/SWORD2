using Sword.Engine.Rhi;
using Vortice.Direct3D12;

namespace Sword.Engine.Render;

/// <summary>Данные асинхронного чтения: байты хранилища, записанные кадром <paramref name="frame"/> (номер кадра рендера).</summary>
public delegate void ReadbackHandler(ReadOnlySpan<byte> data, ulong frame);

/// <summary>
/// Асинхронное чтение хранилища на CPU — для представления и диагностики (статистика GPU-симуляции), не для симуляции: в конце каждого
/// кадра рендер копирует первые <see cref="Bytes"/> байт <see cref="Source"/> в буфер чтения своего слота кольца кадров, а в начале кадра
/// того же слота — когда GPU его доделал, без ожидания — отдаёт их обработчику. Задержка — <see cref="Latency"/> кадров; кадр, брошенный
/// до показа, данных не даёт; смена размера отдаёт ждущие данные сразу, по порядку кадров. Создаёт <see cref="Renderer.CreateReadback"/>;
/// освобождение — отложенное (GPU мог ещё писать).
/// </summary>
public sealed class GpuReadback : IDisposable
{
    /// <summary>Через сколько кадров приходят данные кадра: кадры в полёте.</summary>
    public const int Latency = GraphicsDevice.FrameCount;

    private readonly GraphicsDevice _device;
    private readonly ID3D12Resource _buffer;
    private readonly unsafe byte* _mapped;
    private readonly ulong _stride;
    private readonly ulong[] _frames = new ulong[GraphicsDevice.FrameCount];
    private readonly bool[] _open = new bool[GraphicsDevice.FrameCount], _pending = new bool[GraphicsDevice.FrameCount];
    private readonly ReadbackHandler _ready;
    private readonly Action<GpuReadback> _removed;
    private bool _disposed;

    internal unsafe GpuReadback(GraphicsDevice device, GpuStorage source, uint bytes, ReadbackHandler ready, Action<GpuReadback> removed)
    {
        ulong size = (ulong)source.Count * source.Stride;
        if (bytes == 0 || bytes > size) throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"{source.Name}: читать 1…{size} Б");
        _device = device;
        Source = source;
        Bytes = bytes;
        _ready = ready;
        _removed = removed;
        _stride = (bytes + 255UL) & ~255UL;
        // Буфер чтения создаётся в CopyDest и в нём остаётся; отображён всё время жизни (кучу readback так читают).
        _buffer = device.Device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer(_stride * GraphicsDevice.FrameCount), ResourceStates.CopyDest);
        _buffer.Name = source.Name + ".Readback";
        _mapped = _buffer.Map<byte>(0);
    }

    /// <summary>Что читается.</summary>
    public GpuStorage Source { get; }

    /// <summary>Сколько байт с начала хранилища.</summary>
    public uint Bytes { get; }

    /// <summary>Номер кадра рендера последних отданных данных; 0 — ещё не было.</summary>
    public ulong LastFrame { get; private set; }

    /// <summary>Сколько раз данные отданы.</summary>
    public long Delivered { get; private set; }

    /// <summary>Копия кадра <paramref name="frame"/> в слот <paramref name="slot"/> — последней командой кадра, после всех проходов.</summary>
    internal void Record(ID3D12GraphicsCommandList list, int slot, ulong frame)
    {
        var r = Source.Resource;
        if (r.State != ResourceStates.CopySource) list.ResourceBarrierTransition(r.Handle!, r.State, ResourceStates.CopySource);
        r.State = ResourceStates.CopySource;
        r.UavPending = false;
        list.CopyBufferRegion(_buffer, (ulong)slot * _stride, r.Handle!, 0, Bytes);
        _frames[slot] = frame;
        _open[slot] = true;
        _pending[slot] = false;
    }

    /// <summary>Кадр слота отправлен: его данные ждут забора.</summary>
    internal void Commit(int slot)
    {
        if (!_open[slot]) return;
        _open[slot] = false;
        _pending[slot] = true;
    }

    /// <summary>Слот доделан GPU (забор кадра дождались): отдать данные.</summary>
    internal unsafe void Collect(int slot)
    {
        if (_disposed || !_pending[slot]) return;
        _pending[slot] = false;
        LastFrame = _frames[slot];
        Delivered++;
        _ready(new ReadOnlySpan<byte>(_mapped + (ulong)slot * _stride, (int)Bytes), _frames[slot]);
    }

    /// <summary>
    /// GPU простаивает, а номер слота кадра сейчас собьётся (смена размера: задние буферы — с нуля): все ждущие данные — сразу, по порядку
    /// кадров, иначе слоты отдали бы их не по порядку.
    /// </summary>
    internal void CollectAll()
    {
        while (true)
        {
            int oldest = -1;
            for (int s = 0; s < _pending.Length; s++)
                if (_pending[s] && (oldest < 0 || _frames[s] < _frames[oldest])) oldest = s;
            if (oldest < 0 || _disposed) return;
            Collect(oldest);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _removed(this);
        _buffer.Unmap(0);
        _device.DeferRelease(_buffer);
    }
}
