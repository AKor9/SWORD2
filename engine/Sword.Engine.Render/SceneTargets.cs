using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Sword.Engine.Render;

/// <summary>
/// Цели сцены рядом с задним буфером (перенос <c>Graphics/SceneTargets.cs</c> SWORD 2eab13b): глубина (D32, обратный Z)
/// и нормали (R10G10B10A2: xyz — нормаль мира · 0.5 + 0.5, a — признак приёмника декалей мира). Непрозрачная фаза
/// пишет их, фазы поверх сцены читают. В граф они входят импортированными текстурами; переходы состояний ставит граф.
/// Размер — всегда заднего буфера.
/// </summary>
public sealed class SceneTargets : IDisposable
{
    internal const Format DepthFormat = Format.D32_Float;
    internal const Format NormalFormat = Format.R10G10B10A2_UNorm;

    /// <summary>Очистка нормалей: «вверх», не приёмник.</summary>
    public static readonly System.Numerics.Vector4 NormalClear = new(0.5f, 1f, 0.5f, 0f);

    private readonly GraphicsDevice _gd;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly int _dsvIncrement;
    private readonly GraphResource _depth = new("scene.depth", isBuffer: false) { TargetFormat = DepthFormat };
    private readonly GraphResource _normal = new("scene.normal", isBuffer: false) { TargetFormat = NormalFormat };

    internal SceneTargets(GraphicsDevice gd, RenderGraph graph)
    {
        _gd = gd;
        _rtvHeap = gd.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        _dsvHeap = gd.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 2));
        _dsvIncrement = (int)gd.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        _depth.Dsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        _depth.ReadOnlyDsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart() + _dsvIncrement;
        _normal.Rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        Depth = graph.ImportPersistent(_depth);
        Normal = graph.ImportPersistent(_normal);
        EnsureSize();
    }

    /// <summary>Глубина сцены; как текстура — <c>Texture2D&lt;float&gt;</c> (0 — дальняя плоскость, пусто).</summary>
    public RenderTexture Depth { get; }

    /// <summary>Нормали сцены; как текстура — <c>Texture2D&lt;float4&gt;</c>.</summary>
    public RenderTexture Normal { get; }

    /// <summary>SRV глубины и нормалей — для корневых констант проходов, которые их читают.</summary>
    public int DepthSrv => _depth.Srv;
    public int NormalSrv => _normal.Srv;

    /// <summary>Пересоздаёт цели под размер заднего буфера; прежние освобождаются после своего кадра.</summary>
    internal void EnsureSize()
    {
        int w = _gd.BackBufferWidth, h = _gd.BackBufferHeight;
        if (_depth.Handle is not null && w == _depth.Width && h == _depth.Height) return;
        Release();
        var device = _gd.Device;

        // Обратный Z: ближняя плоскость в единице, дальняя в нуле — очистка нулём, сравнение GreaterEqual.
        var depth = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R32_Typeless, (uint)w, (uint)h, 1, 1, flags: ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthRead, new ClearValue(DepthFormat, 0f, 0));
        depth.Name = "Sword.SceneDepth";
        device.CreateDepthStencilView(depth, new DepthStencilViewDescription
        {
            Format = DepthFormat, ViewDimension = DepthStencilViewDimension.Texture2D,
        }, _depth.Dsv!.Value);
        device.CreateDepthStencilView(depth, new DepthStencilViewDescription
        {
            Format = DepthFormat, ViewDimension = DepthStencilViewDimension.Texture2D, Flags = DepthStencilViewFlags.ReadOnlyDepth,
        }, _depth.ReadOnlyDsv!.Value);
        _depth.Srv = _gd.Bindless.Allocate();
        device.CreateShaderResourceView(depth, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, _gd.Bindless.Cpu(_depth.Srv));
        Set(_depth, depth, ResourceStates.DepthRead, w, h);
        _depth.ClearDepth = 0f;

        var normal = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(NormalFormat, (uint)w, (uint)h, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource,
            new ClearValue(NormalFormat, new Color4(NormalClear.X, NormalClear.Y, NormalClear.Z, NormalClear.W)));
        normal.Name = "Sword.SceneNormal";
        device.CreateRenderTargetView(normal, null, _normal.Rtv!.Value);
        _normal.Srv = _gd.Bindless.Allocate();
        device.CreateShaderResourceView(normal, null, _gd.Bindless.Cpu(_normal.Srv));
        Set(_normal, normal, ResourceStates.PixelShaderResource, w, h);
        _normal.ClearColor = NormalClear;
    }

    private static void Set(GraphResource t, ID3D12Resource resource, ResourceStates state, int w, int h)
    {
        t.Handle = resource;
        t.State = state;
        t.Width = w;
        t.Height = h;
    }

    private void Release()
    {
        foreach (var t in (ReadOnlySpan<GraphResource>)[_depth, _normal])
        {
            if (t.Handle is null) continue;
            _gd.DeferRelease(t.Handle, t.Srv);
            t.Handle = null;
            t.Srv = -1;
        }
    }

    public void Dispose()
    {
        Release();
        _rtvHeap.Dispose();
        _dsvHeap.Dispose();
    }
}
