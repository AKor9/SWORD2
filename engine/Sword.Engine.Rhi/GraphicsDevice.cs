using System.Diagnostics;
using SharpGen.Runtime;
using Sword.Engine.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace Sword.Engine.Rhi;

/// <summary>
/// Устройство D3D12, graphics-очередь, свопчейн и кольцо из <see cref="FrameCount"/> кадров.
///
/// Очередь одна: compute-проходы рендер-графа v0 идут dispatch'ем на ней же. Отдельная compute-очередь,
/// межочередные заборы и async compute появятся вместе с первой GPU-симуляцией (раздел 6 ARCHITECTURE_PLAN.md).
/// Логика кольца кадров, ожидания слота и отложенного освобождения — из SWORD.
///
/// Типы Vortice в открытых членах предназначены Render: остальным сборкам они при сборке не видны
/// (<c>PrivateAssets=compile</c>), им доступны только члены без типов D3D12.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    public const int FrameCount = 3;
    public const Format BackBufferFormat = Format.R8G8B8A8_UNorm;

    public ID3D12Device Device { get; }
    public ID3D12CommandQueue GraphicsQueue { get; }
    public IDXGIFactory6 Factory { get; }
    public IDXGISwapChain3 SwapChain { get; private set; }
    public BindlessHeap Bindless { get; }
    public string AdapterName { get; }
    public ulong AdapterVramBytes { get; }
    public bool TearingSupported { get; }

    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly int _rtvIncrement;
    private readonly ID3D12Resource[] _backBuffers = new ID3D12Resource[FrameCount];

    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[FrameCount];
    private readonly ID3D12GraphicsCommandList[] _lists = new ID3D12GraphicsCommandList[FrameCount];

    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private ulong _fenceValue;
    private readonly ulong[] _frameFenceValues = new ulong[FrameCount];
    private readonly ID3D12InfoQueue? _infoQueue;

    private struct Deferred
    {
        public ulong Frame;
        public ulong Fence;
        public IDisposable Resource;
    }

    private sealed class ResourceWithDescriptors(
        IDisposable resource, BindlessHeap heap, int descriptor, int descriptorCount) : IDisposable
    {
        public void Dispose()
        {
            try { resource.Dispose(); }
            finally { heap.Free(descriptor, descriptorCount); }
        }
    }

    private readonly List<Deferred> _deferredRelease = [];

    public int BackBufferWidth { get; private set; }
    public int BackBufferHeight { get; private set; }
    public int FrameIndex { get; private set; }
    public ulong FrameNumber { get; private set; }
    private readonly SwapChainFlags _swapChainFlags;

    /// <summary>Сколько CPU простоял в ожидании свободного слота кадра, мс.</summary>
    public double LastFenceWaitMs { get; private set; }

    /// <summary>
    /// GPU-время кадра и проходов: разбор кадра N − <see cref="FrameCount"/> — в <see cref="BeginFrame"/>, после
    /// ожидания его слота; после <see cref="WaitForIdle"/> — последнего отправленного.
    /// </summary>
    public GpuTimer Timer { get; }

    /// <param name="gpuValidation">
    /// GPU-валидация (только вместе с <paramref name="enableDebugLayer"/>): проверяет на GPU состояния ресурсов, взятых
    /// шейдером из bindless-кучи, — обычный слой отладки их не видит. Медленно: для тестов и проверочных прогонов.
    /// </param>
    public GraphicsDevice(nint hwnd, int width, int height, bool enableDebugLayer, bool gpuValidation = false)
    {
        AgilitySdk.Initialize();

        if (enableDebugLayer && D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug).Success && debug is not null)
        {
            debug.EnableDebugLayer();
            if (gpuValidation)
            {
                using var debug1 = debug.QueryInterfaceOrNull<ID3D12Debug1>()
                    ?? throw new InvalidOperationException("GPU-валидация недоступна: нет ID3D12Debug1");
                debug1.SetEnableGPUBasedValidation(true);
            }
            debug.Dispose();
        }
        else if (gpuValidation)
            throw new ArgumentException("GPU-валидация — только со слоем отладки D3D12", nameof(gpuValidation));

        try
        {
            Factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(enableDebugLayer);
        }
        catch (SharpGenException) when (enableDebugLayer)
        {
            Log.Warning("d3d12", "DXGI debug layer недоступен; фабрика создана без отладки.");
            Factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(false);
        }
        TearingSupported = Factory.PresentAllowTearing;

        ID3D12Device? device = null;
        string adapterName = string.Empty;
        ulong vram = 0;
        for (uint i = 0; Factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success; i++)
        {
            if (adapter is null) break;
            var description = adapter.Description1;
            if ((description.Flags & AdapterFlags.Software) == 0
                && D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_12_1, out device).Success
                && device is not null)
            {
                adapterName = description.Description;
                vram = description.DedicatedVideoMemory;
                adapter.Dispose();
                break;
            }
            device = null;
            adapter.Dispose();
        }

        if (device is null)
        {
            Factory.Dispose();
            throw new InvalidOperationException("Не найдено ни одного адаптера с Feature Level 12_1.");
        }

        Device = device;
        AdapterName = adapterName;
        AdapterVramBytes = vram;

        try
        {
            _infoQueue = enableDebugLayer ? Device.QueryInterfaceOrNull<ID3D12InfoQueue>() : null;
            RequireFeatures();

            GraphicsQueue = Device.CreateCommandQueue(CommandListType.Direct);
            GraphicsQueue.Name = "Sword.GraphicsQueue";

            Bindless = new BindlessHeap(Device, BindlessHeap.DefaultCapacity);

            _rtvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
            _rtvIncrement = (int)Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            BackBufferWidth = width;
            BackBufferHeight = height;
            _swapChainFlags = TearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None;
            SwapChain = CreateSwapChain(hwnd, width, height);
            CreateSizeDependent();

            for (int i = 0; i < FrameCount; i++)
            {
                _allocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
                _lists[i] = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocators[i], null);
                _lists[i].Close();
                _lists[i].Name = $"Sword.GraphicsList{i}";
            }

            _fence = Device.CreateFence(0, FenceFlags.None);
            Timer = new GpuTimer(Device, GraphicsQueue);
            FrameIndex = (int)SwapChain.CurrentBackBufferIndex;
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    /// <summary>Сообщения отладочного слоя D3D12 — в журнал (категория «d3d12»).</summary>
    public void DrainDebugMessages()
    {
        if (_infoQueue is null) return;
        ulong count = _infoQueue.NumStoredMessages;
        for (ulong i = 0; i < count; i++)
        {
            var message = _infoQueue.GetMessage(i);
            var level = message.Severity switch
            {
                MessageSeverity.Corruption or MessageSeverity.Error => LogLevel.Error,
                MessageSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Info,
            };
            Log.Write(level, "d3d12", $"{message.Severity}: {message.Description}");
        }
        if (count > 0) _infoQueue.ClearStoredMessages();
    }

    private void RequireFeatures()
    {
        var options = Device.CheckFeatureSupport<FeatureDataD3D12Options>(Vortice.Direct3D12.Feature.Options);
        if (options.ResourceBindingTier < ResourceBindingTier.Tier3)
            throw new NotSupportedException($"Нужен Resource Binding Tier 3, у адаптера {options.ResourceBindingTier}.");

        var highest = Device.CheckHighestShaderModel(ShaderModel.Model6_6);
        if (highest < ShaderModel.Model6_6)
            throw new NotSupportedException($"Нужна Shader Model 6.6 (ResourceDescriptorHeap), у адаптера {highest}.");
    }

    private IDXGISwapChain3 CreateSwapChain(nint hwnd, int width, int height)
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BackBufferFormat,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = FrameCount,
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _swapChainFlags,
        };
        using var chain1 = Factory.CreateSwapChainForHwnd(GraphicsQueue, hwnd, desc);
        Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        return chain1.QueryInterface<IDXGISwapChain3>();
    }

    private void CreateSizeDependent()
    {
        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i] = SwapChain.GetBuffer<ID3D12Resource>((uint)i);
            _backBuffers[i].Name = $"Sword.BackBuffer{i}";
            Device.CreateRenderTargetView(_backBuffers[i], null, RtvHandle(i));
        }
    }

    private CpuDescriptorHandle RtvHandle(int index) =>
        _rtvHeap.GetCPUDescriptorHandleForHeapStart() + index * _rtvIncrement;

    public ID3D12Resource CurrentBackBuffer => _backBuffers[FrameIndex];
    public CpuDescriptorHandle CurrentRtv => RtvHandle(FrameIndex);

    /// <summary>Список команд текущего кадра; открыт между <see cref="BeginFrame"/> и <see cref="EndFrame"/>.</summary>
    public ID3D12GraphicsCommandList GraphicsList => _lists[FrameIndex];

    /// <summary>
    /// Ждёт слот кадра (единственное ожидание CPU за кадр — ограничение очереди в <see cref="FrameCount"/> кадров)
    /// и открывает список команд.
    /// </summary>
    public void BeginFrame()
    {
        FrameIndex = (int)SwapChain.CurrentBackBufferIndex;

        LastFenceWaitMs = 0;
        ulong target = _frameFenceValues[FrameIndex];
        if (target != 0 && _fence.CompletedValue < target)
        {
            long started = Stopwatch.GetTimestamp();
            _fence.SetEventOnCompletion(target, _fenceEvent);
            _fenceEvent.WaitOne();
            LastFenceWaitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        FlushDeferredRelease();
        // Кадр этого слота GPU доделал (забор выше): его таймстемпы разбираются сейчас, CPU ради них не ждёт.
        Timer.Collect(FrameIndex);

        _allocators[FrameIndex].Reset();
        _lists[FrameIndex].Reset(_allocators[FrameIndex], null);
        _lists[FrameIndex].SetDescriptorHeaps(Bindless.Heap);
        Timer.BeginFrame(_lists[FrameIndex], FrameIndex, FrameNumber);
        FrameOpen = true;
    }

    /// <summary>Кадр открыт: <see cref="BeginFrame"/> был, <see cref="EndFrame"/> или <see cref="AbandonFrame"/> — нет.</summary>
    public bool FrameOpen { get; private set; }

    /// <summary>
    /// Бросить открытый кадр, не отправляя: список команд закрывается, GPU его не увидит. После ошибки посреди записи
    /// кадра — иначе следующий <see cref="BeginFrame"/> сбрасывал бы открытый список.
    /// </summary>
    public void AbandonFrame()
    {
        if (!FrameOpen) return;
        FrameOpen = false;
        Timer.Abandon();
        _lists[FrameIndex].Close();
    }

    /// <summary>Отправляет список кадра и показывает кадр. CPU не ждёт ничего.</summary>
    public void EndFrame(bool vsync)
    {
        var list = _lists[FrameIndex];
        FrameOpen = false;
        Timer.EndFrame(list);
        list.Close();
        GraphicsQueue.ExecuteCommandList(list);

        var flags = !vsync && TearingSupported ? PresentFlags.AllowTearing : PresentFlags.None;
        var presented = SwapChain.Present(vsync ? 1u : 0u, flags);
        if (presented.Failure)
            throw new InvalidOperationException($"Present отказал: {presented} (устройство: {Device.DeviceRemovedReason})");

        _frameFenceValues[FrameIndex] = ++_fenceValue;
        GraphicsQueue.Signal(_fence, _fenceValue).CheckError();
        Timer.Commit();   // слот ждёт разбора — после ожидания этого забора

        for (int i = 0; i < _deferredRelease.Count; i++)
        {
            var entry = _deferredRelease[i];
            if (entry.Fence != 0 || entry.Frame != FrameNumber) continue;
            entry.Fence = _fenceValue;
            _deferredRelease[i] = entry;
        }

        FrameNumber++;
    }

    /// <summary>Пересоздать задние буферы под новый размер; тот же размер или нулевой — ничего.</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == BackBufferWidth && height == BackBufferHeight) return;

        WaitForIdle();
        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i]?.Dispose();
            _backBuffers[i] = null!;
            _frameFenceValues[i] = 0;
        }

        // Vortice возвращает результат, а не бросает: без проверки размер записался бы новый, а буферы остались старые.
        var resized = SwapChain.ResizeBuffers(FrameCount, (uint)width, (uint)height, BackBufferFormat, _swapChainFlags);
        if (resized.Failure)
            throw new InvalidOperationException($"ResizeBuffers {width}×{height} отказал: {resized} (устройство: {Device.DeviceRemovedReason})");
        BackBufferWidth = width;
        BackBufferHeight = height;
        CreateSizeDependent();
        FrameIndex = (int)SwapChain.CurrentBackBufferIndex;
    }

    public void WaitForIdle()
    {
        GraphicsQueue.Signal(_fence, ++_fenceValue);
        if (_fence.CompletedValue < _fenceValue)
        {
            _fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
            _fenceEvent.WaitOne();
        }
        FlushDeferredRelease();
        Timer.CollectAll();   // GPU простаивает: отправленные кадры разобраны, опубликован последний
    }

    public void DeferRelease(IDisposable resource) =>
        _deferredRelease.Add(new Deferred { Frame = FrameNumber, Fence = 0, Resource = resource });

    /// <summary>Освобождает ресурс и возвращает его bindless-слоты после забора текущего кадра.</summary>
    public void DeferRelease(IDisposable resource, int descriptor, int descriptorCount = 1) =>
        DeferRelease(new ResourceWithDescriptors(resource, Bindless, descriptor, descriptorCount));

    public ID3D12Resource CreateUploadBuffer(ulong sizeBytes, string name = "Upload")
    {
        var resource = Device.CreateCommittedResource(
            HeapType.Upload, HeapFlags.None, ResourceDescription.Buffer(sizeBytes), ResourceStates.GenericRead);
        resource.Name = name;
        return resource;
    }

    /// <summary>Разовая запись с ожиданием: только загрузка на старте.</summary>
    public void ExecuteImmediate(Action<ID3D12GraphicsCommandList> record)
    {
        using var allocator = Device.CreateCommandAllocator(CommandListType.Direct);
        using var list = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
        record(list);
        list.Close();
        GraphicsQueue.ExecuteCommandList(list);
        WaitForIdle();
    }

    private void FlushDeferredRelease()
    {
        if (_deferredRelease.Count == 0) return;
        ulong completed = _fence.CompletedValue;
        for (int i = _deferredRelease.Count - 1; i >= 0; i--)
        {
            var entry = _deferredRelease[i];
            if (entry.Fence == 0 || entry.Fence > completed) continue;
            entry.Resource.Dispose();
            _deferredRelease.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        WaitForIdle();
        foreach (var entry in _deferredRelease) entry.Resource.Dispose();
        _deferredRelease.Clear();
        ReleaseAll();
    }

    private void ReleaseAll()
    {
        Timer?.Dispose();
        foreach (var l in _lists) l?.Dispose();
        foreach (var a in _allocators) a?.Dispose();
        foreach (var b in _backBuffers) b?.Dispose();
        _infoQueue?.Dispose();
        _fence?.Dispose();
        _fenceEvent.Dispose();
        _rtvHeap?.Dispose();
        Bindless?.Dispose();
        SwapChain?.Dispose();
        GraphicsQueue?.Dispose();
        Device.Dispose();
        Factory.Dispose();
    }
}
