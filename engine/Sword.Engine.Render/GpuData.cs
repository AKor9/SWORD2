using System.Runtime.CompilerServices;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Sword.Engine.Render;

/// <summary>
/// Неизменяемые данные для шейдеров (<c>StructuredBuffer&lt;T&gt;</c>): вершины мешей, поле высот, таблицы. Шейдер
/// берёт их по <see cref="Srv"/> из bindless-кучи. Создаёт <see cref="Renderer.CreateData{T}"/>.
/// </summary>
public sealed class GpuData : IDisposable
{
    private readonly StaticBuffer _buffer;

    internal GpuData(StaticBuffer buffer) => _buffer = buffer;

    /// <summary>Индекс SRV в bindless-куче — его проход кладёт в корневую константу.</summary>
    public int Srv => _buffer.Srv;

    public uint Count => _buffer.ElementCount;

    public ulong SizeBytes => _buffer.SizeBytes;

    public void Dispose() => _buffer.Dispose();
}

/// <summary>Индексы треугольников (uint32) для <see cref="RenderPassContext.DrawIndexed"/>.</summary>
public sealed class GpuIndices : IDisposable
{
    private readonly StaticBuffer _buffer;

    internal GpuIndices(StaticBuffer buffer)
    {
        _buffer = buffer;
        View = new IndexBufferView(buffer.Resource.GPUVirtualAddress, (uint)buffer.SizeBytes, Format.R32_UInt);
    }

    internal IndexBufferView View { get; }

    public uint Count => _buffer.ElementCount;

    public void Dispose() => _buffer.Dispose();
}

/// <summary>
/// Данные, которые CPU переписывает каждый кадр (палитры, экземпляры): слот на кадр в полёте, постоянно отображён,
/// шейдер читает его напрямую. Слот текущего кадра — <see cref="Slot"/>, его SRV — <see cref="Srv"/>.
/// </summary>
public sealed class FrameData<T> : IDisposable where T : unmanaged
{
    private readonly MappedStructuredRing _ring;
    private readonly GraphicsDevice _device;

    internal FrameData(GraphicsDevice device, string name, uint capacity)
    {
        _device = device;
        _ring = new MappedStructuredRing(device, name, capacity, (uint)Unsafe.SizeOf<T>());
    }

    public uint Capacity => _ring.Capacity;

    /// <summary>Слот текущего кадра: пишется между <c>BeginFrame</c> и отправкой списка команд.</summary>
    public Span<T> Slot => _ring.Slot<T>(_device.FrameIndex);

    /// <summary>SRV слота текущего кадра.</summary>
    public int Srv => _ring.Srv(_device.FrameIndex);

    public void Dispose() => _ring.Dispose();
}
