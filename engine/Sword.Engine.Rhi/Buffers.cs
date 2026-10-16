using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Sword.Engine.Rhi;

/// <summary>Неизменяемые данные в UPLOAD-куче со SRV: для нескольких килобайт копия в DEFAULT не нужна.</summary>
public sealed unsafe class StaticBuffer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private bool _disposed;
    public ID3D12Resource Resource { get; }
    public int Srv { get; }
    public uint ElementCount { get; }
    public ulong SizeBytes { get; }

    private StaticBuffer(GraphicsDevice gd, ID3D12Resource resource, int srv, uint count, ulong size)
    {
        _gd = gd;
        Resource = resource;
        Srv = srv;
        ElementCount = count;
        SizeBytes = size;
    }

    public static StaticBuffer Create<T>(GraphicsDevice gd, string name, ReadOnlySpan<T> data, bool withSrv = true)
        where T : unmanaged
    {
        uint stride = (uint)sizeof(T);
        ulong bytes = (ulong)data.Length * stride;
        var resource = gd.CreateUploadBuffer(bytes, name);
        void* mapped = resource.Map<byte>(0);
        data.CopyTo(new Span<T>(mapped, data.Length));
        resource.Unmap(0);

        int srv = -1;
        if (withSrv)
        {
            srv = gd.Bindless.Allocate();
            gd.Device.CreateShaderResourceView(resource, new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)data.Length, StructureByteStride = stride },
            }, gd.Bindless.Cpu(srv));
        }
        return new StaticBuffer(gd, resource, srv, (uint)data.Length, bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Srv >= 0) _gd.DeferRelease(Resource, Srv);
        else _gd.DeferRelease(Resource);
    }
}

/// <summary>Структурированный буфер в UPLOAD-куче со SRV, переписываемый каждый кадр: слот на кадр в полёте.</summary>
public sealed unsafe class MappedStructuredRing : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly ID3D12Resource[] _buffers = new ID3D12Resource[GraphicsDevice.FrameCount];
    private readonly byte*[] _mapped = new byte*[GraphicsDevice.FrameCount];
    private readonly int[] _srv = new int[GraphicsDevice.FrameCount];
    private bool _disposed;

    public uint Capacity { get; }
    public uint Stride { get; }

    public MappedStructuredRing(GraphicsDevice gd, string name, uint capacity, uint stride)
    {
        _gd = gd;
        Capacity = capacity;
        Stride = stride;
        for (int i = 0; i < _buffers.Length; i++)
        {
            _buffers[i] = gd.CreateUploadBuffer((ulong)capacity * stride, $"{name}{i}");
            _mapped[i] = _buffers[i].Map<byte>(0);
            _srv[i] = gd.Bindless.Allocate();
            gd.Device.CreateShaderResourceView(_buffers[i], new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = capacity, StructureByteStride = stride },
            }, gd.Bindless.Cpu(_srv[i]));
        }
    }

    public Span<T> Slot<T>(int frameIndex) where T : unmanaged
    {
        if (sizeof(T) != Stride) throw new ArgumentException($"{typeof(T).Name}: {sizeof(T)} Б при шаге {Stride} Б.");
        return new Span<T>(_mapped[frameIndex], (int)Capacity);
    }

    public int Srv(int frameIndex) => _srv[frameIndex];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < _buffers.Length; i++)
        {
            _buffers[i].Unmap(0);
            _gd.DeferRelease(_buffers[i], _srv[i]);
        }
    }
}

/// <summary>
/// Кольцо константных буферов в UPLOAD-куче: слоты × <see cref="GraphicsDevice.FrameCount"/>, чтобы CPU не писал
/// в память, которую читает прошлый кадр.
/// </summary>
public sealed unsafe class ConstantRing : IDisposable
{
    private const int Alignment = 256;

    private readonly ID3D12Resource _resource;
    private readonly byte* _mapped;
    private readonly int _slotSize;
    private readonly int _slotsPerFrame;
    private int _cursor;
    private int _frameIndex;

    public ConstantRing(GraphicsDevice gd, string name, int maxStructSize, int slotsPerFrame)
    {
        _slotSize = (maxStructSize + Alignment - 1) / Alignment * Alignment;
        _slotsPerFrame = slotsPerFrame;
        ulong total = (ulong)(_slotSize * slotsPerFrame * GraphicsDevice.FrameCount);
        _resource = gd.Device.CreateCommittedResource(
            HeapType.Upload, HeapFlags.None, ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        _resource.Name = name;
        _mapped = _resource.Map<byte>(0);
    }

    public void BeginFrame(int frameIndex)
    {
        _frameIndex = frameIndex;
        _cursor = frameIndex * _slotsPerFrame;
    }

    public ulong Write<T>(in T value) where T : unmanaged
    {
        if (_cursor >= (_frameIndex + 1) * _slotsPerFrame)
            throw new InvalidOperationException($"Кольцо констант исчерпано: {_slotsPerFrame} слотов на кадр.");
        int size = sizeof(T);
        if (size > _slotSize)
            throw new ArgumentException($"{typeof(T).Name} — {size} Б при слоте {_slotSize} Б.");

        int offset = _cursor++ * _slotSize;
        System.Runtime.CompilerServices.Unsafe.Write(_mapped + offset, value);
        return _resource.GPUVirtualAddress + (ulong)offset;
    }

    public void Dispose()
    {
        _resource.Unmap(0);
        _resource.Dispose();
    }
}
