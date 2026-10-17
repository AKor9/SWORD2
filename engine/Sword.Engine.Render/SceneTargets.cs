using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Sword.Engine.Render;

/// <summary>
/// Цели сцены размера заднего буфера: глубина (D32, обратный Z), нормали (R10G10B10A2: нормаль мира · 0.5 + 0.5,
/// a — приёмник декалей) и скорости (RG16F: перемещение точки на экране с прошлого кадра, доли экрана, без сдвига TAA — пишет
/// предпроход игры, читают TAA и размытие движения). Пишет непрозрачная фаза, читают фазы поверх; в граф входят импортом.
/// </summary>
public sealed class SceneTargets : IDisposable
{
    internal const Format DepthFormat = Format.D32_Float;
    internal const Format NormalFormat = Format.R10G10B10A2_UNorm;
    internal const Format VelocityFormat = Format.R16G16_Float;

    /// <summary>Очистка нормалей: «вверх», не приёмник.</summary>
    public static readonly System.Numerics.Vector4 NormalClear = new(0.5f, 1f, 0.5f, 0f);

    /// <summary>Очистка скоростей: неподвижно.</summary>
    public static readonly System.Numerics.Vector4 VelocityClear = System.Numerics.Vector4.Zero;

    private readonly GraphicsDevice _gd;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly int _dsvIncrement;
    private readonly GraphResource _depth = new("scene.depth", isBuffer: false) { TargetFormat = DepthFormat };
    private readonly GraphResource _normal = new("scene.normal", isBuffer: false) { TargetFormat = NormalFormat };
    private readonly GraphResource _velocity = new("scene.velocity", isBuffer: false) { TargetFormat = VelocityFormat };

    internal SceneTargets(GraphicsDevice gd, RenderGraph graph)
    {
        _gd = gd;
        _rtvHeap = gd.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));
        _dsvHeap = gd.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 2));
        _dsvIncrement = (int)gd.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        _depth.Dsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        _depth.ReadOnlyDsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart() + _dsvIncrement;
        _normal.Rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        _velocity.Rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)gd.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        Depth = graph.ImportPersistent(_depth);
        Normal = graph.ImportPersistent(_normal);
        Velocity = graph.ImportPersistent(_velocity);
        EnsureSize();
    }

    /// <summary>Глубина сцены; как текстура — <c>Texture2D&lt;float&gt;</c> (0 — дальняя плоскость, пусто).</summary>
    public RenderTexture Depth { get; }

    /// <summary>Нормали сцены; как текстура — <c>Texture2D&lt;float4&gt;</c>.</summary>
    public RenderTexture Normal { get; }

    /// <summary>Скорости сцены (<c>Texture2D&lt;float2&gt;</c>, доли экрана за кадр, y вниз); пишет вызывающий, очистка <see cref="VelocityClear"/>.</summary>
    public RenderTexture Velocity { get; }

    /// <summary>SRV глубины и нормалей — для корневых констант проходов, которые их читают.</summary>
    public int DepthSrv => _depth.Srv;

    /// <summary>Скорости как ресурс графа — для снимка скоростей (<see cref="FrameCapture.VelocityPath"/>).</summary>
    internal GraphResource VelocityResource => _velocity;

    /// <summary>Глубина как ресурс графа — для снимка глубины (<see cref="Renderer.Render(System.Action{RenderFrame}, bool, FrameCapture?)"/>).</summary>
    internal GraphResource DepthResource => _depth;

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

        var velocity = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(VelocityFormat, (uint)w, (uint)h, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource, new ClearValue(VelocityFormat, new Color4(0, 0, 0, 0)));
        velocity.Name = "Sword.SceneVelocity";
        device.CreateRenderTargetView(velocity, null, _velocity.Rtv!.Value);
        _velocity.Srv = _gd.Bindless.Allocate();
        device.CreateShaderResourceView(velocity, null, _gd.Bindless.Cpu(_velocity.Srv));
        Set(_velocity, velocity, ResourceStates.PixelShaderResource, w, h);
        _velocity.ClearColor = VelocityClear;
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
        foreach (var t in (ReadOnlySpan<GraphResource>)[_depth, _normal, _velocity])
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
