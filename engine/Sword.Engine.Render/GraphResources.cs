using System.Numerics;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Sword.Engine.Render;

/// <summary>Текстура кадра в рендер-графе: номер внутри графа на этот кадр. Получают у <see cref="RenderGraph"/>.</summary>
public readonly record struct RenderTexture(int Index);

/// <summary>Структурированный буфер кадра в рендер-графе: номер внутри графа на этот кадр.</summary>
public readonly record struct RenderBuffer(int Index);

/// <summary>Вид прохода: графический (цели, отрисовка) или вычислительный (dispatch на той же graphics-очереди).</summary>
public enum PassKind { Graphics, Compute }

/// <summary>Форматы временных текстур графа.</summary>
public enum TextureFormat { Rgba8Unorm, Rgba16Float, Rg16Float, R32Float, R32Uint, Rgb10A2Unorm, Depth32Float }

/// <summary>Чем временная текстура бывает кроме чтения шейдером (читать можно всегда).</summary>
[Flags]
public enum TextureUsage
{
    Sampled = 0,
    ColorTarget = 1,
    DepthTarget = 2,
    /// <summary>Запись из шейдера (UAV): вычислительные проходы, <c>RWTexture2D</c>.</summary>
    Storage = 4,
}

/// <summary>
/// Временная текстура: формат, назначение, размер (0 — заднего буфера) и значение очистки. Цель создаётся с ним как
/// оптимальным, поэтому проход чистит её только им; глубина — <see cref="ClearDepth"/> (обратный Z).
/// </summary>
public readonly record struct TextureDesc(
    TextureFormat Format, TextureUsage Usage, int Width = 0, int Height = 0, Vector4 ClearColor = default, float ClearDepth = 0);

/// <summary>Ресурс графа со своим состоянием D3D12; состояние живёт между кадрами, граф ставит переходы от него.</summary>
internal sealed class GraphResource(string name, bool isBuffer)
{
    public string Name = name;
    public readonly bool IsBuffer = isBuffer;
    public ID3D12Resource? Handle;
    public ResourceStates State;
    /// <summary>Формат RTV/DSV (для целей).</summary>
    public Format TargetFormat;
    public CpuDescriptorHandle? Rtv, Dsv, ReadOnlyDsv;
    /// <summary>Оптимальное значение очистки, с которым создана цель; null — не задано (задний буфер).</summary>
    public Vector4? ClearColor;
    public float? ClearDepth;
    public int Srv = -1, Uav = -1;
    public int Width, Height;
    /// <summary>Кто-то записал или очистил ресурс в этом кадре (сбрасывается, когда ресурс входит в кадр).</summary>
    public bool Written;
    /// <summary>Содержимое переживает кадр (буферы-хранилища): читать его можно и без записи в этом кадре.</summary>
    public bool Persistent;
    /// <summary>Последний доступ — запись UAV без барьера после неё: следующему доступу UAV нужен барьер UAV.</summary>
    public bool UavPending;
}

/// <summary>
/// Пул временных ресурсов графа: в кадре одна запись — одному запросу (без алиасинга). Запись, не выданная
/// <see cref="KeepFrames"/> кадров подряд, освобождается отложенно — после смены размера окна старые цели уходят.
/// </summary>
internal sealed class TransientPool(GraphicsDevice device) : IDisposable
{
    /// <summary>Сколько кадров подряд невостребованная запись живёт в пуле.</summary>
    public const int KeepFrames = GraphicsDevice.FrameCount * 2 + 1;

    /// <summary>Ключ записи пула — значением, без упаковки: поиск идёт каждый кадр.</summary>
    private readonly record struct Key(
        bool Buffer, TextureFormat Format, TextureUsage Usage, int Width, int Height, Vector4 Clear, float ClearDepth,
        uint Stride, uint Count);

    private sealed class Entry
    {
        public required GraphResource Resource;
        public required Key Key;
        public required List<IDisposable> Owned;
        public int DescriptorBase;
        public int DescriptorCount;
        public ulong LastUsedFrame;
        public bool InUse;
    }

    private readonly List<Entry> _entries = [];

    public int Count => _entries.Count;

    /// <summary>Начало кадра: всё свободно; давно не выдававшееся — освободить.</summary>
    public void BeginFrame(ulong frame)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            e.InUse = false;
            if (frame - e.LastUsedFrame <= KeepFrames) continue;
            Release(e);
            _entries.RemoveAt(i);
        }
    }

    public GraphResource Texture(string name, in TextureDesc desc, int width, int height, ulong frame)
    {
        var key = new Key(false, desc.Format, desc.Usage, width, height, desc.ClearColor, desc.ClearDepth, 0, 0);
        var e = Take(key, frame) ?? Add(key, CreateTexture(desc, width, height), frame);
        e.Resource.Name = name;
        return e.Resource;
    }

    public GraphResource Buffer(string name, uint stride, uint count, ulong frame)
    {
        var key = new Key(true, default, default, 0, 0, default, 0, stride, count);
        var e = Take(key, frame) ?? Add(key, CreateBuffer(device, stride, count), frame);
        e.Resource.Name = name;
        return e.Resource;
    }

    private Entry? Take(Key key, ulong frame)
    {
        foreach (var e in _entries)
        {
            if (e.InUse || e.Key != key) continue;
            e.InUse = true;
            e.LastUsedFrame = frame;
            return e;
        }
        return null;
    }

    private Entry Add(Key key, (GraphResource Resource, List<IDisposable> Owned, int Base, int Count) created, ulong frame)
    {
        var e = new Entry
        {
            Resource = created.Resource, Key = key, Owned = created.Owned, DescriptorBase = created.Base,
            DescriptorCount = created.Count, LastUsedFrame = frame, InUse = true,
        };
        _entries.Add(e);
        return e;
    }

    private (GraphResource, List<IDisposable>, int, int) CreateTexture(in TextureDesc desc, int width, int height)
    {
        bool depth = desc.Format == TextureFormat.Depth32Float;
        if (depth != desc.Usage.HasFlag(TextureUsage.DepthTarget))
            throw new ArgumentException($"текстура {desc.Format}: глубина — это формат Depth32Float и назначение DepthTarget вместе");
        if (depth && (desc.Usage & (TextureUsage.ColorTarget | TextureUsage.Storage)) != 0)
            throw new ArgumentException("текстура глубины не бывает целью цвета или хранилищем");

        var format = FormatOf(desc.Format);
        var flags = ResourceFlags.None;
        if (desc.Usage.HasFlag(TextureUsage.ColorTarget)) flags |= ResourceFlags.AllowRenderTarget;
        if (desc.Usage.HasFlag(TextureUsage.DepthTarget)) flags |= ResourceFlags.AllowDepthStencil;
        if (desc.Usage.HasFlag(TextureUsage.Storage)) flags |= ResourceFlags.AllowUnorderedAccess;

        var d = device.Device;
        var c = desc.ClearColor;
        ClearValue? clear = depth ? new ClearValue(Format.D32_Float, desc.ClearDepth, 0)
            : desc.Usage.HasFlag(TextureUsage.ColorTarget) ? new ClearValue(format, new Vortice.Mathematics.Color4(c.X, c.Y, c.Z, c.W))
            : null;
        var resource = d.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(depth ? Format.R32_Typeless : format, (uint)width, (uint)height, 1, 1, flags: flags),
            ResourceStates.Common, clear);
        resource.Name = $"Sword.Transient.{desc.Format}.{width}x{height}";
        var r = new GraphResource("", isBuffer: false)
        {
            Handle = resource, State = ResourceStates.Common, TargetFormat = depth ? Format.D32_Float : format,
            Width = width, Height = height,
            ClearColor = desc.Usage.HasFlag(TextureUsage.ColorTarget) ? desc.ClearColor : null,
            ClearDepth = depth ? desc.ClearDepth : null,
        };
        var owned = new List<IDisposable>();

        bool storage = desc.Usage.HasFlag(TextureUsage.Storage);
        int count = storage ? 2 : 1;
        int first = device.Bindless.Allocate(count);
        r.Srv = first;
        d.CreateShaderResourceView(resource, new ShaderResourceViewDescription
        {
            Format = depth ? Format.R32_Float : format, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, device.Bindless.Cpu(r.Srv));
        if (storage)
        {
            r.Uav = first + 1;
            d.CreateUnorderedAccessView(resource, null, new UnorderedAccessViewDescription
            {
                Format = format, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, device.Bindless.Cpu(r.Uav));
        }
        if (desc.Usage.HasFlag(TextureUsage.ColorTarget))
        {
            var heap = d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
            owned.Add(heap);
            r.Rtv = heap.GetCPUDescriptorHandleForHeapStart();
            d.CreateRenderTargetView(resource, null, r.Rtv.Value);
        }
        if (depth)
        {
            var heap = d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 2));
            owned.Add(heap);
            r.Dsv = heap.GetCPUDescriptorHandleForHeapStart();
            r.ReadOnlyDsv = r.Dsv.Value + (int)d.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
            d.CreateDepthStencilView(resource, new DepthStencilViewDescription
            {
                Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D,
            }, r.Dsv.Value);
            d.CreateDepthStencilView(resource, new DepthStencilViewDescription
            {
                Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D, Flags = DepthStencilViewFlags.ReadOnlyDepth,
            }, r.ReadOnlyDsv.Value);
        }
        return (r, owned, first, count);
    }

    /// <summary>Буфер в памяти GPU со SRV и UAV подряд в bindless-куче: временный у графа, хранилище у <see cref="GpuStorage"/>.</summary>
    internal static (GraphResource, List<IDisposable>, int, int) CreateBuffer(GraphicsDevice device, uint stride, uint count)
    {
        if (stride == 0 || count == 0) throw new ArgumentException($"буфер {count} × {stride} Б: пустой");
        if (stride % 4 != 0) throw new ArgumentException($"шаг буфера {stride} Б: должен делиться на 4");
        var d = device.Device;
        ulong size = (ulong)stride * count;
        var resource = d.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Buffer(size, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);
        resource.Name = $"Sword.Buffer.{count}x{stride}";
        int first = device.Bindless.Allocate(2);
        d.CreateShaderResourceView(resource, new ShaderResourceViewDescription
        {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = count, StructureByteStride = stride },
        }, device.Bindless.Cpu(first));
        d.CreateUnorderedAccessView(resource, null, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = count, StructureByteStride = stride },
        }, device.Bindless.Cpu(first + 1));
        var r = new GraphResource("", isBuffer: true) { Handle = resource, State = ResourceStates.Common, Srv = first, Uav = first + 1 };
        return (r, [], first, 2);
    }

    internal static Format FormatOf(TextureFormat f) => f switch
    {
        TextureFormat.Rgba8Unorm => Format.R8G8B8A8_UNorm,
        TextureFormat.Rgba16Float => Format.R16G16B16A16_Float,
        TextureFormat.Rg16Float => Format.R16G16_Float,
        TextureFormat.R32Float => Format.R32_Float,
        TextureFormat.R32Uint => Format.R32_UInt,
        TextureFormat.Rgb10A2Unorm => Format.R10G10B10A2_UNorm,
        TextureFormat.Depth32Float => Format.D32_Float,
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    private void Release(Entry e)
    {
        device.DeferRelease(e.Resource.Handle!, e.DescriptorBase, e.DescriptorCount);
        foreach (var o in e.Owned) device.DeferRelease(o);
        e.Resource.Handle = null;
    }

    public void Dispose()
    {
        foreach (var e in _entries) Release(e);
        _entries.Clear();
    }
}

/// <summary>
/// Буфер-хранилище (<c>RWStructuredBuffer&lt;T&gt;</c>) между кадрами: содержимое переживает кадр, читать можно и без записи
/// в этом кадре. В кадр входит через <see cref="RenderGraph.Import(GpuStorage)"/>.
/// </summary>
public sealed class GpuStorage : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly int _descriptors;
    private bool _disposed;

    internal GpuStorage(GraphicsDevice device, string name, uint stride, uint count)
    {
        _device = device;
        var (resource, _, first, _) = TransientPool.CreateBuffer(device, stride, count);
        resource.Name = name;
        resource.Persistent = true;
        resource.Handle!.Name = name;
        Resource = resource;
        _descriptors = first;
        Stride = stride;
        Count = count;
    }

    internal GraphResource Resource { get; }

    public string Name => Resource.Name;
    public uint Stride { get; }
    public uint Count { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.DeferRelease(Resource.Handle!, _descriptors, 2);
    }
}
