using System.Runtime.CompilerServices;
using Sword.Engine.Content;
using Sword.Engine.Rhi;

namespace Sword.Engine.Render;

/// <summary>Кадр, который строит сцена: граф, цели сцены, константы кадра.</summary>
public sealed class RenderFrame
{
    private readonly Renderer _renderer;

    internal RenderFrame(Renderer renderer) => _renderer = renderer;

    public RenderGraph Graph => _renderer.Graph;

    /// <summary>Глубина и нормали сцены.</summary>
    public SceneTargets Scene => _renderer.Scene;

    public int Width => _renderer.Device.BackBufferWidth;
    public int Height => _renderer.Device.BackBufferHeight;

    /// <summary>Номер кадра с начала работы.</summary>
    public ulong Number => _renderer.Device.FrameNumber;

    /// <summary>Константы кадра (<c>register(b0)</c>) для всех проходов, добавленных после вызова.</summary>
    public void SetFrameConstants<T>(in T value) where T : unmanaged
    {
        if (Unsafe.SizeOf<T>() > Renderer.MaxFrameConstantsBytes)
            throw new ArgumentException($"{typeof(T).Name}: {Unsafe.SizeOf<T>()} Б при потолке {Renderer.MaxFrameConstantsBytes} Б");
        _renderer.Graph.SetFrameConstants(_renderer.Constants.Write(value));
    }
}

/// <summary>
/// Что снять с кадра: цвет (задний буфер, с интерфейсом) в PNG (<c>.bmp</c> — BMP) и глубину сцены в PNG (ближе —
/// светлее). null — не снимать.
/// </summary>
public sealed record FrameCapture(string? ColorPath, string? DepthPath = null);

/// <summary>Итог снимка: размер кадра, файлы, диапазон глубины (reversed-Z: больше — ближе) и доля пикселей с геометрией.</summary>
public sealed record CaptureResult(int Width, int Height, string? ColorPath, string? DepthPath, float DepthMin, float DepthMax, float DepthCoverage);

/// <summary>Снимок кадра не записан в файл (каталог недоступен, диск полон, неверный путь); сам кадр показан.</summary>
public sealed class CaptureException(string message, Exception inner) : IOException(message, inner);

/// <summary>
/// Как создать рендер: окно (HWND), начальный размер, отладочный слой D3D12, каталог шейдеров; GPU-валидация — проверка
/// состояний ресурсов, взятых шейдерами из bindless-кучи (медленно, только со слоем отладки).
/// </summary>
public sealed record RendererOptions(nint WindowHandle, int Width, int Height, bool DebugLayer, string ShaderRoot, bool GpuValidation = false);

/// <summary>
/// Рендер движка: устройство D3D12, шейдеры, конвейеры, рендер-граф, цели сцены, скинированные меши. Кадр строит сцена
/// игры (<see cref="Render"/>, делегат получает <see cref="RenderFrame"/>), рендер исполняет граф и показывает кадр.
/// Публичный API — без типов нижних слоёв (Rhi, Vortice, Formats, Content): клиент и игра работают через граф,
/// <see cref="RenderPassContext"/>, данные GPU (<see cref="GpuData"/>, <see cref="GpuIndices"/>, <see cref="FrameData{T}"/>)
/// и пути ассетов. Это проверяет архитектурный тест публичной поверхности сборок.
/// </summary>
public sealed class Renderer : IDisposable
{
    /// <summary>Потолок структуры констант кадра, байт.</summary>
    public const int MaxFrameConstantsBytes = 1024;

    private const int FrameConstantSlots = 8;

    private readonly RenderFrame _frame;
    private PakSource? _content;
    private UiPass? _ui;

    /// <param name="contract">Преамбула контракта: движок (<see cref="Contribute"/>) и игра.</param>
    public Renderer(RendererOptions options, ShaderContract contract)
    {
        Device = new GraphicsDevice(options.WindowHandle, options.Width, options.Height, options.DebugLayer, options.GpuValidation);
        try
        {
            Shaders = new ShaderLibrary(options.ShaderRoot, contract.Build(), contract.Names);
            Pipelines = new PipelineCache(Device, Shaders);
            Constants = new ConstantRing(Device, "Frame.Constants", MaxFrameConstantsBytes, FrameConstantSlots);
            Graph = new RenderGraph(Device, Pipelines);
            Scene = new SceneTargets(Device, Graph);
            Skinned = new SkinnedMeshes(this);
            _frame = new RenderFrame(this);
        }
        catch
        {
            Skinned?.Dispose();
            Scene?.Dispose();
            Constants?.Dispose();
            Pipelines?.Dispose();
            Device.Dispose();
            throw;
        }
    }

    /// <summary>Числа движка для всех шейдеров.</summary>
    public static void Contribute(ShaderContract c)
    {
        c.Section("движок (Sword.Engine.Render)");
        c.Add("PASS_COUNT", RootLayout.ConstantCount, "столько gPassN объявляет pass.hlsli");
        SkinnedMeshes.Contribute(c);
        UiPass.Contribute(c);
    }

    internal GraphicsDevice Device { get; }

    public string AdapterName => Device.AdapterName;
    public ulong AdapterVramMb => Device.AdapterVramBytes / 1048576;

    /// <summary>Размер заднего буфера, пиксели.</summary>
    public int Width => Device.BackBufferWidth;
    public int Height => Device.BackBufferHeight;

    /// <summary>Пересоздать задние буферы под новый размер окна (ждёт GPU); тот же или нулевой размер — ничего.</summary>
    public void Resize(int width, int height) => Device.Resize(width, height);

    /// <summary>Дождаться, пока GPU доделает всё отправленное, и освободить отложенное.</summary>
    public void WaitForIdle() => Device.WaitForIdle();
    internal ShaderLibrary Shaders { get; }
    internal PipelineCache Pipelines { get; }
    internal ConstantRing Constants { get; }
    public RenderGraph Graph { get; }
    public SceneTargets Scene { get; }
    public SkinnedMeshes Skinned { get; }

    /// <summary>
    /// Контент, из которого рендер загружает ассеты по пути (<see cref="SkinnedMeshes.LoadMesh"/>): пак из
    /// <see cref="ContentLocator"/>, открывается при первой загрузке. Двоичные форматы ассетов знает только рендер.
    /// Когда контент начнёт открывать Runtime (точка 1в), рендер получит тот же источник от композиции.
    /// </summary>
    internal IContentSource Content => _content ??= ContentLocator.Open();

    /// <summary>
    /// UI-проход и шрифты интерфейса: создаётся при первом обращении (шрифты — из пака контента, <see cref="Content"/>).
    /// Рендер без интерфейса шрифтов не читает.
    /// </summary>
    public UiPass Ui => _ui ??= new UiPass(this);

    /// <summary>Ожидание GPU в начале последнего кадра (забор кадра в полёте), мс.</summary>
    public double LastFenceWaitMs => Device.LastFenceWaitMs;

    /// <summary>Запись последнего кадра на CPU, мс: построение графа и его исполнение в список команд, без ожидания GPU и показа.</summary>
    public double LastRecordMs { get; private set; }

    /// <summary>Конвейеров собрано (каждый — компиляция шейдеров при первом использовании).</summary>
    public int PipelineCount => Pipelines.PipelineCount;

    public GpuData CreateData<T>(string name, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (data.IsEmpty) throw new ArgumentException($"{name}: пустые данные", nameof(data));
        return new GpuData(StaticBuffer.Create(Device, name, data));
    }

    public GpuIndices CreateIndices(string name, ReadOnlySpan<uint> indices)
    {
        if (indices.IsEmpty) throw new ArgumentException($"{name}: нет индексов", nameof(indices));
        return new GpuIndices(StaticBuffer.Create(Device, name, indices, withSrv: false));
    }

    public FrameData<T> CreateFrameData<T>(string name, uint capacity) where T : unmanaged => new(Device, name, capacity);

    /// <summary>Буфер-хранилище из <paramref name="count"/> элементов <typeparamref name="T"/>, заполненный нулями.</summary>
    public GpuStorage CreateStorage<T>(string name, uint count) where T : unmanaged
    {
        var storage = new GpuStorage(Device, name, (uint)Unsafe.SizeOf<T>(), count);
        Upload(storage, new T[count].AsSpan());
        return storage;
    }

    /// <summary>Буфер-хранилище с начальными данными (загрузка с ожиданием GPU — на старте, не в кадре).</summary>
    public GpuStorage CreateStorage<T>(string name, ReadOnlySpan<T> initial) where T : unmanaged
    {
        if (initial.IsEmpty) throw new ArgumentException($"{name}: пустые данные", nameof(initial));
        var storage = new GpuStorage(Device, name, (uint)Unsafe.SizeOf<T>(), (uint)initial.Length);
        Upload(storage, initial);
        return storage;
    }

    private unsafe void Upload<T>(GpuStorage storage, ReadOnlySpan<T> data) where T : unmanaged
    {
        ulong bytes = (ulong)data.Length * (ulong)sizeof(T);
        using var upload = Device.CreateUploadBuffer(bytes, storage.Name + ".Upload");
        void* mapped = upload.Map<byte>(0);
        data.CopyTo(new Span<T>(mapped, data.Length));
        upload.Unmap(0);
        var r = storage.Resource;
        Device.ExecuteImmediate(list =>
        {
            if (r.State != Vortice.Direct3D12.ResourceStates.CopyDest)
                list.ResourceBarrierTransition(r.Handle!, r.State, Vortice.Direct3D12.ResourceStates.CopyDest);
            list.CopyBufferRegion(r.Handle!, 0, upload, 0, bytes);
        });
        r.State = Vortice.Direct3D12.ResourceStates.CopyDest;
        r.UavPending = false;
    }

    /// <summary>
    /// Прочитать хранилище на CPU: ждёт GPU (всё отправленное доделано) и копирует. Для проверок, инструментов и агента,
    /// не для кадра. Между кадрами: открытый кадр — ошибка.
    /// </summary>
    public unsafe void ReadStorage<T>(GpuStorage storage, Span<T> destination) where T : unmanaged
    {
        if (Device.FrameOpen) throw new InvalidOperationException("ReadStorage посреди кадра: читать можно только между кадрами");
        if ((ulong)destination.Length * (ulong)sizeof(T) > (ulong)storage.Count * storage.Stride)
            throw new ArgumentException($"{storage.Name}: {destination.Length} × {sizeof(T)} Б больше хранилища", nameof(destination));
        ulong bytes = (ulong)destination.Length * (ulong)sizeof(T);
        using var readback = Device.Device.CreateCommittedResource(Vortice.Direct3D12.HeapType.Readback,
            Vortice.Direct3D12.HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(bytes), Vortice.Direct3D12.ResourceStates.CopyDest);
        var r = storage.Resource;
        Device.ExecuteImmediate(list =>
        {
            if (r.State != Vortice.Direct3D12.ResourceStates.CopySource)
                list.ResourceBarrierTransition(r.Handle!, r.State, Vortice.Direct3D12.ResourceStates.CopySource);
            list.CopyBufferRegion(readback, 0, r.Handle!, 0, bytes);
        });
        r.State = Vortice.Direct3D12.ResourceStates.CopySource;
        r.UavPending = false;
        void* mapped = readback.Map<byte>(0);
        try
        {
            new Span<T>(mapped, destination.Length).CopyTo(destination);
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    /// <summary>Итог последнего снимка (<see cref="Render(Action{RenderFrame}, bool, FrameCapture?)"/>); null — не снимали.</summary>
    public CaptureResult? LastCapture { get; private set; }

    /// <summary>
    /// Кадр: <paramref name="build"/> регистрирует проходы, граф их исполняет, кадр показывается.
    /// <paramref name="capturePath"/> — снять этот кадр в PNG (ждёт GPU).
    /// </summary>
    public void Render(Action<RenderFrame> build, bool vsync, string? capturePath = null) =>
        Render(build, vsync, capturePath is null ? null : new FrameCapture(capturePath));

    /// <summary>
    /// Кадр со снимком <paramref name="capture"/>: цвет и (или) глубина сцены этого кадра — в файлы (ждёт GPU); итог —
    /// <see cref="LastCapture"/>. Файл не записался — <see cref="CaptureException"/> (кадр показан).
    /// </summary>
    public void Render(Action<RenderFrame> build, bool vsync, FrameCapture? capture)
    {
        if (capture is not null) LastCapture = null;   // итог — только этого снимка
        Device.BeginFrame();
        long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var list = Device.GraphicsList;
        try
        {
            Constants.BeginFrame(Device.FrameIndex);
            Scene.EnsureSize();
            Skinned.Begin();
            _ui?.BeginFrame();
            Graph.BeginFrame(0);

            build(_frame);

            Graph.Execute(list);
            LastRecordMs = System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
        }
        catch
        {
            // Кадр не собран (ошибка сцены или нарушен инвариант графа): список команд закрывается без отправки,
            // следующий кадр начнётся чисто. Время записи — этого кадра, а не прошлого удачного.
            LastRecordMs = System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
            Device.AbandonFrame();
            throw;
        }

        Vortice.Direct3D12.ID3D12Resource? readback = null, depthReadback = null;
        try
        {
            int w = 0, h = 0, pitch = 0, dw = 0, dh = 0, dpitch = 0;
            if (capture?.DepthPath is not null)
                depthReadback = Screenshot.RecordDepthCapture(Device, list, Scene.DepthResource, out dw, out dh, out dpitch);
            if (capture?.ColorPath is not null) readback = Screenshot.RecordCapture(Device, list, out w, out h, out pitch);

            Device.EndFrame(vsync);

            if (readback is not null || depthReadback is not null)
            {
                Device.WaitForIdle();
                string? current = null;
                try
                {
                    var depth = (Min: 0f, Max: 0f, Coverage: 0f);
                    if (readback is not null)
                    {
                        current = capture!.ColorPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        Screenshot.Save(readback, current, w, h, pitch);
                    }
                    if (depthReadback is not null)
                    {
                        current = capture!.DepthPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        depth = Screenshot.SaveDepth(depthReadback, current, dw, dh, dpitch);
                    }
                    LastCapture = new CaptureResult(Width, Height, readback is not null ? capture!.ColorPath : null,
                        depthReadback is not null ? capture!.DepthPath : null, depth.Min, depth.Max, depth.Coverage);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // Кадр показан; не записался только файл — своя ошибка, чтобы клиент отличил её от ошибки кадра.
                    throw new CaptureException($"снимок {current} не записан: {e.Message}", e);
                }
            }
        }
        catch (CaptureException)
        {
            Device.DrainDebugMessages();   // кадр показан: его сообщения D3D12 — сейчас, а не со следующим кадром
            throw;
        }
        finally
        {
            // Копии снимка записаны в список кадра: при ошибке после отправки GPU мог ещё писать в них — освобождение
            // отложенное, по забору кадра, а не сразу.
            if (readback is not null) Device.DeferRelease(readback);
            if (depthReadback is not null) Device.DeferRelease(depthReadback);
        }
        Device.DrainDebugMessages();
    }

    /// <summary>Ресурсы рендера и устройство; ресурсы сцены игры освобождаются раньше. Ждёт GPU.</summary>
    public void Dispose()
    {
        Device.WaitForIdle();
        _ui?.Dispose();
        Skinned.Dispose();
        Graph.Dispose();
        Scene.Dispose();
        Constants.Dispose();
        Pipelines.Dispose();
        _content?.Dispose();
        // Dispose устройства ждёт простоя и освобождает отложенное: и ресурсы выше, и ресурсы сцены игры.
        Device.Dispose();
    }
}
