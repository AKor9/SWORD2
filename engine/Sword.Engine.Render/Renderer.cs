using System.Runtime.CompilerServices;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
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

    /// <summary>
    /// Что снимается с этого кадра (null — ничего). По нему игра регистрирует диагностические проходы только тогда, когда
    /// их заказали (раздел 8 плана): ID-буфер (<see cref="FrameCapture.IdsPath"/> → <see cref="CaptureIds"/>) и слои
    /// (<see cref="FrameCapture.Layers"/> → <see cref="CaptureLayer"/>).
    /// </summary>
    public FrameCapture? Capture { get; internal set; }

    /// <summary>
    /// ID-буфер этого кадра для снимка: цель <see cref="TextureFormat.R32Uint"/> размера кадра, записанная проходом кадра
    /// (коды — <see cref="SkinnedMeshes.IdOf"/>; 0 — ничего). Только когда он заказан (<see cref="FrameCapture.IdsPath"/>).
    /// </summary>
    public void CaptureIds(RenderTexture ids)
    {
        if (Capture?.IdsPath is null) throw new InvalidOperationException("ID-буфер в этом кадре не заказан (FrameCapture.IdsPath)");
        var r = _renderer.Graph.Get(ids.Index, buffer: false);
        if (r.TargetFormat != Vortice.DXGI.Format.R32_UInt || r.Width != Width || r.Height != Height)
            throw new ArgumentException($"{r.Name}: ID-буфер — цель R32Uint размера кадра {Width}×{Height}", nameof(ids));
        _renderer.CapturedIds = ids;
    }

    /// <summary>
    /// Слой снимка <paramref name="name"/> этого кадра: цель <see cref="TextureFormat.Rgba8Unorm"/> размера кадра, очищенная
    /// прозрачным и нарисованная с альфа-смешиванием (цвет — умноженный на альфу). В файл слоя идёт снимок цвета с
    /// наложенным слоем (без цвета — слой на чёрном). Только заказанный слой (<see cref="FrameCapture.Layers"/>).
    /// </summary>
    public void CaptureLayer(string name, RenderTexture layer)
    {
        if (!WantsLayer(name))
            throw new InvalidOperationException($"слой {name} в этом кадре не заказан (FrameCapture.Layers)");
        var r = _renderer.Graph.Get(layer.Index, buffer: false);
        if (r.TargetFormat != Vortice.DXGI.Format.R8G8B8A8_UNorm || r.Width != Width || r.Height != Height)
            throw new ArgumentException($"{r.Name}: слой снимка — цель Rgba8Unorm размера кадра {Width}×{Height}", nameof(layer));
        _renderer.CapturedLayers.Add((name, layer));
    }

    /// <summary>Заказан ли в этом кадре слой снимка <paramref name="name"/> (циклом, без замыкания: зовётся каждый кадр).</summary>
    public bool WantsLayer(string name) => Capture?.WantsLayer(name) == true;

    /// <summary>
    /// Заметка игры к снимку этого кадра (7и): почему заказанное снято не полностью — например, слой контактов пуст, потому что
    /// трасса физики выключена. Идёт в <see cref="CaptureResult.Notes"/> вслед за заметками рендера. Кадр без снимка — ничего.
    /// </summary>
    public void CaptureNote(string note)
    {
        if (Capture is not null) _renderer.CaptureNotes.Add(note);
    }

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
/// светлее); в диагностическом режиме — ID-буфер (<see cref="IdsPath"/>) и слои (<see cref="Layers"/>), их проходы
/// регистрирует игра, увидев заказ в <see cref="RenderFrame.Capture"/>. null — не снимать.
/// </summary>
public sealed record FrameCapture(string? ColorPath, string? DepthPath = null)
{
    /// <summary>
    /// ID-буфер: PNG для глаз (код → свой цвет, 0 — чёрный) по этому пути и сырые коды рядом (<c>.bin</c>: uint32 на пиксель,
    /// строки сверху вниз); null — не снимать.
    /// </summary>
    public string? IdsPath { get; init; }

    /// <summary>Слои снимка (оверлеи игры: капсулы тел и т. п.) — каждый в свой PNG поверх цвета кадра.</summary>
    public IReadOnlyList<CaptureLayer> Layers { get; init; } = [];

    /// <summary>Заказан ли слой <paramref name="name"/> (циклом, без замыкания).</summary>
    public bool WantsLayer(string name)
    {
        for (int i = 0; i < Layers.Count; i++)
            if (Layers[i].Name == name) return true;
        return false;
    }
}

/// <summary>Слой снимка: имя (его знает игра — «capsules») и PNG.</summary>
public sealed record CaptureLayer(string Name, string Path);

/// <summary>Код ID-буфера на снимке: пикселей и рамка на экране [x0, x1) × [y0, y1) (начало — левый верхний угол).</summary>
public readonly record struct IdCoverage(uint Id, int Pixels, int X0, int Y0, int X1, int Y1);

/// <summary>Итог снимка: размер кадра, файлы, диапазон глубины (reversed-Z: больше — ближе) и доля пикселей с геометрией.</summary>
public sealed record CaptureResult(int Width, int Height, string? ColorPath, string? DepthPath, float DepthMin, float DepthMax, float DepthCoverage)
{
    /// <summary>ID-буфер: PNG и сырые коды; null — не заказан или кадр его не дал (<see cref="Notes"/>).</summary>
    public string? IdsPath { get; init; }
    public string? IdsRawPath { get; init; }

    /// <summary>Коды ID-буфера на кадре (кроме 0) по возрастанию: пикселей и рамка.</summary>
    public IReadOnlyList<IdCoverage> Ids { get; init; } = [];

    /// <summary>Снятые слои.</summary>
    public IReadOnlyList<CaptureLayer> Layers { get; init; } = [];

    /// <summary>Что заказано, но не снято: кадр не дал ID-буфер или слой (игра не умеет его).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

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

    /// <summary>ID-буфер и слои, которые игра отдала снимку этого кадра (<see cref="RenderFrame.CaptureIds"/>, <see cref="RenderFrame.CaptureLayer"/>).</summary>
    internal RenderTexture? CapturedIds;
    internal readonly List<(string Name, RenderTexture Texture)> CapturedLayers = [];
    internal readonly List<string> CaptureNotes = [];

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
            Debug = new DebugDraw(this);
            Gpu = new GpuFrameTimes(Device);
            _frame = new RenderFrame(this);
        }
        catch
        {
            Debug?.Dispose();
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
        DebugDraw.Contribute(c);
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

    /// <summary>Отладочное рисование (фаза Debug): отрезки, капсулы, текст в мире — поверх сцены или в слой снимка.</summary>
    public DebugDraw Debug { get; }

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

    /// <summary>
    /// GPU-время кадра и проходов (этап П, точка Пг): таймстемпы вокруг каждого прохода графа и кадра целиком, разбор кадра
    /// N − 3 после ожидания его слота (без стойла), после <see cref="WaitForIdle"/> — последнего отправленного. Всегда включено.
    /// </summary>
    public GpuFrameTimes Gpu { get; }

    /// <summary>Кадров отправлено GPU с начала работы (номер следующего кадра рендера, <see cref="RenderFrame.Number"/>).</summary>
    public ulong FrameNumber => Device.FrameNumber;

    private CpuProfiler _profiler = CpuProfiler.Off;
    private (int FenceWait, int Build, int Record, int Present, int Capture) _zones;

    /// <summary>Зона GPU-времени кадра в профайлере (точка Пг); зоны проходов — у графа (<c>gpu.&lt;проход&gt;</c>).</summary>
    private int _gpuFrameZone;
    private long _gpuRecorded;

    /// <summary>
    /// Профайлер CPU кадра (точка Па): зоны рендера — ожидание забора кадра в полёте <c>frame.fenceWait</c>, построение
    /// кадра игрой <c>frame.build</c>, запись графа в список команд <c>frame.record</c> (в ней — проход за проходом
    /// <c>render.&lt;проход&gt;</c>), показ <c>frame.present</c>, снимок <c>frame.capture</c>. Задаёт клиент движка до
    /// <c>IClientGame.Initialize</c> — игра заводит свои зоны в нём же; по умолчанию <see cref="CpuProfiler.Off"/>.
    /// </summary>
    public CpuProfiler Profiler
    {
        get => _profiler;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            // Ожидание забора кадра и показ — ожидание GPU (точка Пв: класс аномалии «ожидание GPU» — по этим зонам).
            _zones = (value.Zone("frame.fenceWait", ProfileZoneKind.GpuWait), value.Zone("frame.build"), value.Zone("frame.record"),
                value.Zone("frame.present", ProfileZoneKind.GpuWait), value.Zone("frame.capture"));
            Graph.Profiler = value;
            _gpuFrameZone = value.Zone(CpuProfiler.GpuFrameName, ProfileZoneKind.Gpu);
            _profiler = value;
        }
    }

    /// <summary>
    /// Новые GPU-времена — в кадр профайлера (точка Пг): зоны вида <see cref="ProfileZoneKind.Gpu"/> и отставание (кадр клиента
    /// данных — этот минус отставание). Раз на новые данные; выключенный профайлер — ничего.
    /// </summary>
    private void RecordGpu(CpuProfiler profiler)
    {
        var gpu = Gpu;
        if (!profiler.Enabled || gpu.Sequence == _gpuRecorded || !gpu.Valid) return;
        _gpuRecorded = gpu.Sequence;
        // Кадр рендера Device.FrameNumber записывается сейчас: данные — на столько кадров раньше него.
        profiler.NoteGpuFrame((int)(Device.FrameNumber - gpu.Frame));
        profiler.RecordGpu(_gpuFrameZone, gpu.FrameMs);
        // Зоны проходов заведены графом при первой встрече прохода (Graph.PassZone), здесь — только поиск.
        for (int i = 0; i < gpu.Count; i++) profiler.RecordGpu(Graph.GpuZone(gpu.NameOf(i)), gpu.MillisecondsOf(i));
    }

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
        _frame.Capture = capture;
        CapturedIds = null;
        CapturedLayers.Clear();
        CaptureNotes.Clear();
        var profiler = _profiler;
        int profileDepth = profiler.Depth;
        profiler.Begin(_zones.FenceWait);
        Device.BeginFrame();
        profiler.End(_zones.FenceWait);
        RecordGpu(profiler);   // точка Пг: времена кадра, забора которого ждали (или разобранного ожиданием простоя)
        long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var list = Device.GraphicsList;
        try
        {
            Constants.BeginFrame(Device.FrameIndex);
            Scene.EnsureSize();
            Skinned.Begin();
            Debug.BeginFrame();
            _ui?.BeginFrame();
            Graph.BeginFrame(0);

            profiler.Begin(_zones.Build);
            build(_frame);
            profiler.End(_zones.Build);

            profiler.Begin(_zones.Record);
            Graph.Execute(list);
            profiler.End(_zones.Record);
            LastRecordMs = System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
        }
        catch
        {
            profiler.UnwindTo(profileDepth);
            // Кадр не собран (ошибка сцены или нарушен инвариант графа): список команд закрывается без отправки,
            // следующий кадр начнётся чисто. Время записи — этого кадра, а не прошлого удачного.
            LastRecordMs = System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
            Device.AbandonFrame();
            // Причина отказа D3D12 (конвейер не создан и т. п.) — в журнал сейчас: процесс может не дожить до следующего кадра.
            Device.DrainDebugMessages();
            throw;
        }

        Vortice.Direct3D12.ID3D12Resource? readback = null, depthReadback = null, idsReadback = null;
        var layerReadbacks = capture is { Layers.Count: > 0 } ? new List<(CaptureLayer Layer, Vortice.Direct3D12.ID3D12Resource Readback)>() : null;
        var notes = capture is null ? null : new List<string>();   // обычный кадр — без выделений
        try
        {
            int w = 0, h = 0, pitch = 0, dw = 0, dh = 0, dpitch = 0, iw = 0, ih = 0, ipitch = 0, lpitch = 0;
            if (capture?.DepthPath is not null)
                depthReadback = Screenshot.RecordDepthCapture(Device, list, Scene.DepthResource, out dw, out dh, out dpitch);
            if (capture?.ColorPath is not null) readback = Screenshot.RecordCapture(Device, list, out w, out h, out pitch);
            if (capture?.IdsPath is not null)
            {
                if (CapturedIds is { } ids)
                    idsReadback = Screenshot.RecordTextureCapture(Device, list, Graph.Get(ids.Index, buffer: false), out iw, out ih, out ipitch);
                else notes!.Add("ID-буфер заказан, а кадр его не дал (игра не регистрирует ID-проход)");
            }
            if (layerReadbacks is not null)
                foreach (var layer in capture!.Layers)
                {
                    int k = CapturedLayers.FindIndex(l => l.Name == layer.Name);
                    if (k < 0)
                    {
                        notes!.Add($"слой {layer.Name} заказан, а кадр его не дал (игра не знает такого слоя)");
                        continue;
                    }
                    layerReadbacks.Add((layer, Screenshot.RecordTextureCapture(Device, list, Graph.Get(CapturedLayers[k].Texture.Index, buffer: false),
                        out _, out _, out lpitch)));
                }
            if (notes is not null) notes.AddRange(CaptureNotes);   // заметки игры (7и) — после заметок рендера

            profiler.Begin(_zones.Present);
            Device.EndFrame(vsync);
            profiler.End(_zones.Present);

            if (capture is not null)
            {
                profiler.Begin(_zones.Capture);
                Device.WaitForIdle();
                string? current = null;
                try
                {
                    var depth = (Min: 0f, Max: 0f, Coverage: 0f);
                    byte[]? rgb = null;
                    if (readback is not null)
                    {
                        current = capture.ColorPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        rgb = Screenshot.Save(readback, current, w, h, pitch);
                    }
                    if (depthReadback is not null)
                    {
                        current = capture.DepthPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        depth = Screenshot.SaveDepth(depthReadback, current, dw, dh, dpitch);
                    }
                    string? idsRaw = null;
                    IReadOnlyList<IdCoverage> coverage = [];
                    if (idsReadback is not null)
                    {
                        current = capture.IdsPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        idsRaw = Path.ChangeExtension(current, ".bin");
                        coverage = Screenshot.SaveIds(idsReadback, current, idsRaw, iw, ih, ipitch);
                    }
                    var layers = new List<CaptureLayer>();
                    if (layerReadbacks is not null)
                        foreach (var (layer, rb) in layerReadbacks)
                        {
                            current = layer.Path;
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                            Screenshot.SaveLayer(rb, current, Width, Height, lpitch, rgb is { } under && under.Length == Width * Height * 3 ? under : null);
                            layers.Add(layer);
                        }
                    if (readback is not null || depthReadback is not null || idsReadback is not null || layers.Count > 0 || notes!.Count > 0)
                        LastCapture = new CaptureResult(Width, Height, readback is not null ? capture.ColorPath : null,
                            depthReadback is not null ? capture.DepthPath : null, depth.Min, depth.Max, depth.Coverage)
                        {
                            IdsPath = idsReadback is not null ? capture.IdsPath : null, IdsRawPath = idsRaw, Ids = coverage, Layers = layers, Notes = notes!,
                        };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // Кадр показан; не записался только файл — своя ошибка, чтобы клиент отличил её от ошибки кадра.
                    throw new CaptureException($"снимок {current} не записан: {e.Message}", e);
                }
                profiler.End(_zones.Capture);
            }
        }
        catch (CaptureException)
        {
            Device.DrainDebugMessages();   // кадр показан: его сообщения D3D12 — сейчас, а не со следующим кадром
            throw;
        }
        finally
        {
            profiler.UnwindTo(profileDepth);   // ошибка показа или снимка: их зоны не закрыты — кадр клиента идёт дальше
            // Копии снимка записаны в список кадра: при ошибке после отправки GPU мог ещё писать в них — освобождение
            // отложенное, по забору кадра, а не сразу.
            if (readback is not null) Device.DeferRelease(readback);
            if (depthReadback is not null) Device.DeferRelease(depthReadback);
            if (idsReadback is not null) Device.DeferRelease(idsReadback);
            if (layerReadbacks is not null)
                foreach (var (_, rb) in layerReadbacks) Device.DeferRelease(rb);
        }
        Device.DrainDebugMessages();
    }

    /// <summary>Ресурсы рендера и устройство; ресурсы сцены игры освобождаются раньше. Ждёт GPU.</summary>
    public void Dispose()
    {
        Device.WaitForIdle();
        _ui?.Dispose();
        Debug.Dispose();
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
