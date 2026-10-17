using System.Runtime.CompilerServices;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Formats;
using Sword.Engine.Rhi;
using Sword.Engine.Ui;

namespace Sword.Engine.Render;

/// <summary>Кадр, который строит сцена: граф, цели сцены, константы кадра.</summary>
public sealed class RenderFrame
{
    private readonly Renderer _renderer;

    internal RenderFrame(Renderer renderer) => _renderer = renderer;

    public RenderGraph Graph => _renderer.Graph;

    /// <summary>Глубина и нормали сцены.</summary>
    public SceneTargets Scene => _renderer.Scene;

    /// <summary>Точечные источники этого кадра (<see cref="PointLights"/>): добавляет тот, кто строит кадр; SRV буфера — для констант кадра.</summary>
    public PointLights Lights => _renderer.Lights;

    /// <summary>Скинированные меши этого кадра (<see cref="SkinnedMeshes"/>): экземпляры, скиннинг кадра, чтение его буфера проходами.</summary>
    public SkinnedMeshes Skinned => _renderer.Skinned;

    /// <summary>Тени от солнца этого кадра (<see cref="SunShadow"/>): карта, матрица, проход заслоняющих.</summary>
    public SunShadow Shadow => _renderer.Shadow;

    /// <summary>Затенение рассеянного света этого кадра (<see cref="AmbientOcclusion"/>): цели и проходы GTAO.</summary>
    public AmbientOcclusion Ao => _renderer.Ao;

    /// <summary>Прозрачное в ½ разрешения этого кадра (<see cref="HalfResTransparency"/>): слой ½ и его композит в <see cref="SceneColor"/>.</summary>
    public HalfResTransparency HalfRes => _renderer.HalfRes;

    /// <summary>HDR в этом кадре: сцена пишет линейный цвет в <see cref="SceneColor"/>, задний буфер пишет тонмап в фазе Post.</summary>
    public bool Hdr => _renderer.FrameHdr;

    /// <summary>
    /// Цвет HDR-сцены: постоянная цель RGBA16F размера кадра, линейный цвет; чистится только <see cref="Renderer.SceneColorClear"/>.
    /// Только с HDR.
    /// </summary>
    public RenderTexture SceneColor => _renderer.FrameHdr ? _renderer.HdrScene.Color
        : throw new InvalidOperationException("HDR выключен (Renderer.Hdr): цели SceneColor нет, сцена пишет задний буфер");

    public int Width => _renderer.Device.BackBufferWidth;
    public int Height => _renderer.Device.BackBufferHeight;

    public ulong Number => _renderer.Device.FrameNumber;

    /// <summary>Что снимается с этого кадра (null — ничего): по нему модуль кладёт рядом со снимком свои данные (поля огня).</summary>
    public FrameCapture? Capture { get; internal set; }

    /// <summary>Константы кадра (<c>register(b0)</c>) для всех проходов, добавленных после вызова.</summary>
    public void SetFrameConstants<T>(in T value) where T : unmanaged
    {
        if (Unsafe.SizeOf<T>() > Renderer.MaxFrameConstantsBytes)
            throw new ArgumentException($"{typeof(T).Name}: {Unsafe.SizeOf<T>()} Б при потолке {Renderer.MaxFrameConstantsBytes} Б");
        _renderer.Graph.SetFrameConstants(_renderer.Constants.Write(value));
    }
}

/// <summary>
/// Что снять с кадра: цвет (задний буфер, с интерфейсом) в PNG (<c>.bmp</c> — BMP), глубину сцены в PNG (ближе — светлее) и карты
/// HDR, bloom и прозрачного ½. null — не снимать.
/// </summary>
public sealed record FrameCapture(string? ColorPath, string? DepthPath = null)
{
    /// <summary>Карта HDR-сцены в ложных цветах и сырой цвет рядом (<c>.bin</c>); null — не снимать. Сводку яркости снимок с HDR даёт всегда.</summary>
    public string? HdrPath { get; init; }

    /// <summary>Карта добавки bloom (PNG ½ экрана) и сырая добавка рядом (<c>.bin</c>); null — не снимать.</summary>
    public string? BloomPath { get; init; }

    /// <summary>Карта цели ½ прозрачного (PNG ½ экрана) и сырая цель рядом (<c>.bin</c>); null — не снимать.</summary>
    public string? TransparentPath { get; init; }

    /// <summary>Скорости сцены (<see cref="SceneTargets.Velocity"/>) сырыми: half x, y на пиксель, строки сверху вниз (<c>.bin</c>); null — не снимать.</summary>
    public string? VelocityPath { get; init; }
}

/// <summary>Итог снимка: размер кадра, файлы, диапазон глубины (reversed-Z: больше — ближе) и доля пикселей с геометрией.</summary>
public sealed record CaptureResult(int Width, int Height, string? ColorPath, string? DepthPath, float DepthMin, float DepthMax, float DepthCoverage)
{
    /// <summary>Что заказано, но не снято: карта HDR или bloom без них в кадре.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>HDR-кадр: экспозиция и яркость входа тонмапа (и карта, если заказана); null — HDR в кадре выключен.</summary>
    public HdrCapture? Hdr { get; init; }
}

/// <summary>Снимок кадра не записан в файл (каталог недоступен, диск полон, неверный путь); сам кадр показан.</summary>
public sealed class CaptureException(string message, Exception inner) : IOException(message, inner);

/// <summary>
/// Как создать рендер: окно (HWND), начальный размер, отладочный слой D3D12, каталог шейдеров; GPU-валидация — проверка
/// состояний ресурсов, взятых шейдерами из bindless-кучи (медленно, только со слоем отладки).
/// </summary>
public sealed record RendererOptions(nint WindowHandle, int Width, int Height, bool DebugLayer, string ShaderRoot, bool GpuValidation = false)
{
    /// <summary>
    /// Шрифты UI-прохода от вызывающего (окно инструмента печёт себе системные шрифты); рендер берёт их во владение.
    /// Null — <c>fonts/*.swfont</c> из пака контента (игра).
    /// </summary>
    public UiFonts? UiFonts { get; init; }
}

/// <summary>
/// Рендер движка: кадр строит сцена игры (<see cref="Render"/> через <see cref="RenderFrame"/>), рендер исполняет граф
/// и показывает кадр. Публичный API — без типов нижних слоёв (Rhi, Vortice, Formats, Content); проверяет архитектурный тест.
/// </summary>
public sealed class Renderer : IDisposable
{
    /// <summary>Потолок структуры констант кадра, байт.</summary>
    public const int MaxFrameConstantsBytes = 1024;

    /// <summary>
    /// Экспозиция по умолчанию: средний серый 0.18 после «экспозиция → Filmic → sRGB» даёт тот же байт (117), что
    /// <c>pow(0.18, 1/2.2)</c> без HDR.
    /// </summary>
    public const float DefaultExposure = 0.99f;

    private const int FrameConstantSlots = 8;

    private readonly RenderFrame _frame;
    private PakSource? _content;
    private UiPass? _ui;

    /// <summary>Шрифты интерфейса от вызывающего, пока их не взял UI-проход (<see cref="RendererOptions.UiFonts"/>).</summary>
    private UiFonts? _uiFonts;

    /// <param name="contract">Преамбула контракта: движок (<see cref="Contribute"/>) и игра.</param>
    public Renderer(RendererOptions options, ShaderContract contract)
    {
        _uiFonts = options.UiFonts;
        Device = new GraphicsDevice(options.WindowHandle, options.Width, options.Height, options.DebugLayer, options.GpuValidation);
        try
        {
            Shaders = new ShaderLibrary(options.ShaderRoot, contract.Build(), contract.Names);
            Pipelines = new PipelineCache(Device, Shaders);
            Constants = new ConstantRing(Device, "Frame.Constants", MaxFrameConstantsBytes, FrameConstantSlots);
            Graph = new RenderGraph(Device, Pipelines);
            Scene = new SceneTargets(Device, Graph);
            Ao = new AmbientOcclusion(Device, Scene);
            HdrScene = new HdrScene(Device, Graph);
            Skinned = new SkinnedMeshes(this);
            Debug = new DebugDraw(this);
            Lights = new PointLights(this);
            HalfRes = new HalfResTransparency(this);
            Gpu = new GpuFrameTimes(Device);
            _frame = new RenderFrame(this);
            AutoExposure = new AutoExposure(this);
            Taa = new TemporalAa(Device, Graph, Scene);
        }
        catch
        {
            Lights?.Dispose();
            Debug?.Dispose();
            Skinned?.Dispose();
            HdrScene?.Dispose();
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
        PointLights.Contribute(c);
        HalfResTransparency.Contribute(c);
        HdrScene.Contribute(c);
        SunShadow.Contribute(c);
        AmbientOcclusion.Contribute(c);
        AutoExposure.Contribute(c);
        TemporalAa.Contribute(c);
        FilmicTonemap.Contribute(c);
    }

    internal GraphicsDevice Device { get; }

    public string AdapterName => Device.AdapterName;
    public ulong AdapterVramMb => Device.AdapterVramBytes / 1048576;

    /// <summary>Размер заднего буфера, пиксели.</summary>
    public int Width => Device.BackBufferWidth;
    public int Height => Device.BackBufferHeight;

    /// <summary>Пересоздать задние буферы под новый размер окна (ждёт GPU); тот же или нулевой размер — ничего.</summary>
    public void Resize(int width, int height)
    {
        // Смена размера сбрасывает номер слота кадра: чтения, ждущие забора, отдаются до неё — по порядку кадров (обход с конца: обработчик
        // может снять чтение).
        if (_readbacks.Count > 0 && width > 0 && height > 0 && (width != Device.BackBufferWidth || height != Device.BackBufferHeight))
        {
            Device.WaitForIdle();
            for (int i = _readbacks.Count - 1; i >= 0; i--)
                if (i < _readbacks.Count) _readbacks[i].CollectAll();
        }
        Device.Resize(width, height);
    }

    /// <summary>Дождаться, пока GPU доделает всё отправленное, и освободить отложенное.</summary>
    public void WaitForIdle() => Device.WaitForIdle();
    internal ShaderLibrary Shaders { get; }
    internal PipelineCache Pipelines { get; }
    internal ConstantRing Constants { get; }
    public RenderGraph Graph { get; }
    public SceneTargets Scene { get; }
    public SkinnedMeshes Skinned { get; }

    /// <summary>Цель HDR-сцены и тонмап.</summary>
    internal HdrScene HdrScene { get; }

    /// <summary>HDR этого кадра — снимок <see cref="Hdr"/> на начало кадра (переключение посреди кадра — со следующего).</summary>
    internal bool FrameHdr { get; private set; }

    /// <summary>
    /// HDR: сцена рисуется линейным цветом в <see cref="RenderFrame.SceneColor"/>, рендер добавляет в фазу Post проход
    /// <c>post.tonemap</c> (× <see cref="Exposure"/> → Filmic → sRGB); интерфейс и отладка — после него, в LDR.
    /// По умолчанию выключен; действует со следующего кадра.
    /// </summary>
    public bool Hdr { get; set; }

    /// <summary>Множитель цвета сцены перед тонмапом (<see cref="DefaultExposure"/>).</summary>
    public float Exposure { get; set; } = DefaultExposure;

    /// <summary>
    /// Bloom (UE «сумма гауссиан», <see cref="BloomKernel"/>) между HDR-сценой и тонмапом; только с HDR, действует со следующего кадра.
    /// </summary>
    public BloomSettings Bloom { get; set; } = BloomSettings.Default;

    /// <summary>
    /// Сглаживание SMAA 1x (<see cref="SmaaMath"/>) после тонмапа: три прохода <c>post.smaa.*</c>, итог — в задний буфер. Только с HDR,
    /// действует со следующего кадра; по умолчанию выключено.
    /// </summary>
    public bool Smaa { get; set; }

    /// <summary>Тени от солнца (<see cref="SunShadow"/>): настройки и карта кадра; по умолчанию выключены.</summary>
    public SunShadow Shadow { get; } = new();

    /// <summary>
    /// Временное сглаживание и размытие движения (<see cref="TemporalAa"/>): только с HDR, по умолчанию выключено. Сцену рисует матрица
    /// со сдвигом <see cref="TemporalAa.Jitter"/>, вид задаёт <see cref="TemporalAa.SetView"/>.
    /// </summary>
    public TemporalAa Taa { get; } = null!;

    /// <summary>Постобработка (<see cref="PostEffects"/>): цветокоррекция, виньетка, резкость; только с HDR, по умолчанию нейтральна.</summary>
    public PostEffects Post { get; } = new();

    /// <summary>Автоэкспозиция (<see cref="Render.AutoExposure"/>): средняя яркость сцены на GPU, экспозиция — к ней; только с HDR, по умолчанию выключена.</summary>
    public AutoExposure AutoExposure { get; } = null!;

    /// <summary>Затенение рассеянного света (<see cref="AmbientOcclusion"/>, GTAO): настройки и карта кадра; по умолчанию выключено.</summary>
    public AmbientOcclusion Ao { get; }

    /// <summary>Значение очистки <see cref="RenderFrame.SceneColor"/>; смена пересоздаёт цель в начале следующего кадра.</summary>
    public System.Numerics.Vector4 SceneColorClear { get; set; } = new(0, 0, 0, 1);

    /// <summary>Отладочное рисование (фаза Debug): отрезки, капсулы, текст в мире — поверх сцены.</summary>
    public DebugDraw Debug { get; }

    /// <summary>Точечные источники кадра (<c>common/lights.hlsli</c>); отчёт и оверлей — последнего кадра.</summary>
    public PointLights Lights { get; }

    /// <summary>Прозрачное в ½ разрешения (фаза Transparent, только с HDR).</summary>
    public HalfResTransparency HalfRes { get; }

    /// <summary>Контент, из которого рендер загружает ассеты по пути; открывается при первой загрузке.</summary>
    internal IContentSource Content => _content ??= ContentLocator.Open();

    /// <summary>UI-проход и шрифты интерфейса; создаётся при первом обращении (без интерфейса шрифты не читаются).</summary>
    public UiPass Ui => _ui ??= new UiPass(this, TakeUiFonts());

    /// <summary>Шрифты вызывающего переходят проходу; дальше ими владеет он.</summary>
    private UiFonts? TakeUiFonts()
    {
        var fonts = _uiFonts;
        _uiFonts = null;
        return fonts;
    }

    /// <summary>Ожидание GPU в начале последнего кадра (забор кадра в полёте), мс.</summary>
    public double LastFenceWaitMs => Device.LastFenceWaitMs;

    /// <summary>Запись последнего кадра на CPU, мс: построение графа и его исполнение в список команд, без ожидания GPU и показа.</summary>
    public double LastRecordMs { get; private set; }

    /// <summary>
    /// GPU-время кадра и проходов: кадр N − 3 разбирается после ожидания его слота (без стойла), после <see cref="WaitForIdle"/> —
    /// последний отправленный.
    /// </summary>
    public GpuFrameTimes Gpu { get; }

    /// <summary>Кадров отправлено GPU с начала работы (номер следующего кадра рендера, <see cref="RenderFrame.Number"/>).</summary>
    public ulong FrameNumber => Device.FrameNumber;

    private CpuProfiler _profiler = CpuProfiler.Off;
    private (int FenceWait, int Build, int Record, int Present, int Capture) _zones;

    /// <summary>Зона GPU-времени кадра в профайлере; зоны проходов — у графа (<c>gpu.&lt;проход&gt;</c>).</summary>
    private int _gpuFrameZone;
    private long _gpuRecorded;

    /// <summary>
    /// Профайлер CPU кадра (зоны <c>frame.*</c> и <c>render.&lt;проход&gt;</c>). Задаёт клиент движка до
    /// <c>IClientGame.Initialize</c>; по умолчанию <see cref="CpuProfiler.Off"/>.
    /// </summary>
    public CpuProfiler Profiler
    {
        get => _profiler;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            // Ожидание забора кадра и показ — ожидание GPU (вид зоны в отчёте).
            _zones = (value.Zone("frame.fenceWait", ProfileZoneKind.GpuWait), value.Zone("frame.build"), value.Zone("frame.record"),
                value.Zone("frame.present", ProfileZoneKind.GpuWait), value.Zone("frame.capture"));
            Graph.Profiler = value;
            _gpuFrameZone = value.Zone(CpuProfiler.GpuFrameName, ProfileZoneKind.Gpu);
            _profiler = value;
        }
    }

    /// <summary>Новые GPU-времена — в кадр профайлера, вместе с отставанием данных в кадрах.</summary>
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

    /// <summary>
    /// Постоянная картинка RGBA8 (строки подряд) с цепочкой мипов или без — для <see cref="UiDrawList.Image"/>.
    /// Ждёт GPU: только на загрузке. Освобождается до рендера.
    /// </summary>
    public GpuImage CreateImage(string name, int width, int height, ReadOnlySpan<byte> rgba, bool mips = true, bool srgb = false) =>
        GpuImage.Create(Device, name, width, height, rgba, mips, srgb);

    /// <summary>Картинка с готовой цепочкой мипов (мип i — max(1, w≫i) × max(1, h≫i) RGBA8, строки подряд).</summary>
    /// <exception cref="ArgumentException">Мипов нет, больше полной цепочки или размер мипа не тот.</exception>
    public GpuImage CreateImage(string name, int width, int height, IReadOnlyList<byte[]> mips, bool srgb = false) =>
        GpuImage.Create(Device, name, width, height, mips, srgb);

    private readonly List<GpuTexture> _textures = [];

    /// <summary>Загруженные и ещё не освобождённые текстуры (<see cref="LoadTexture(string)"/>) — для отчётов.</summary>
    public IReadOnlyList<GpuTexture> Textures => _textures;

    /// <summary>
    /// Текстура пака (<c>.swtex</c>) в видеопамять, SRV в bindless-куче. Ждёт GPU — на старте модуля, не в кадре.
    /// Каждый вызов — новая текстура; освобождает вызывающий, остальные — рендер при своём освобождении.
    /// </summary>
    /// <exception cref="FileNotFoundException">Файла нет в контенте (в сообщении — путь).</exception>
    /// <exception cref="InvalidDataException">Файл не текстура, обрезан или противоречив (в сообщении — путь).</exception>
    public GpuTexture LoadTexture(string path)
    {
        path = ContentPath.Normalize(path);
        var content = Content;
        string where = content.Describe(path);
        TextureData data;
        using (var s = content.OpenRead(path))
            data = TextureData.Read(s, where);
        return AddTexture(path, where, data);
    }

    /// <summary>Текстура из потока <c>.swtex</c> не из контента игры (инструменты); иначе — как <see cref="LoadTexture(string)"/>.</summary>
    /// <exception cref="InvalidDataException">Поток — не текстура, обрезан или противоречив (в сообщении — имя).</exception>
    public GpuTexture LoadTexture(string name, Stream file) => AddTexture(name, name, TextureData.Read(file, name));

    /// <summary>
    /// Текстуры пака одного формата, размера и числа мипов — одним массивом слоёв под именем <paramref name="name"/> (слои — по порядку
    /// путей, имена и соотношения сторон — из файлов). <paramref name="slices"/> — слоёв каждого файла, <paramref name="means"/> — среднее
    /// канала r мипа 0 каждого слоя массива (R8 и BC4; иначе NaN). Ждёт GPU — на старте модуля.
    /// </summary>
    /// <exception cref="InvalidDataException">Файлы не одного формата, размера или числа мипов.</exception>
    public GpuTexture LoadTextureArray(string name, IReadOnlyList<string> paths, out int[] slices, out float[] means)
    {
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);
        var content = Content;
        var parts = new TextureData[paths.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            string path = ContentPath.Normalize(paths[i]);
            using var s = content.OpenRead(path);
            parts[i] = TextureData.Read(s, content.Describe(path));
            if (parts[i].Format != parts[0].Format || parts[i].Width != parts[0].Width || parts[i].Height != parts[0].Height
                || parts[i].Mips != parts[0].Mips)
                throw new InvalidDataException($"{name}: {path} — {parts[i].Format} {parts[i].Width}×{parts[i].Height}, мипов {parts[i].Mips}, " +
                    $"а {paths[0]} — {parts[0].Format} {parts[0].Width}×{parts[0].Height}, мипов {parts[0].Mips}");
        }
        slices = [.. parts.Select(p => p.Slices)];
        var first = parts[0];
        var merged = new TextureData
        {
            Format = first.Format, Width = first.Width, Height = first.Height, Mips = first.Mips, Slices = parts.Sum(p => p.Slices),
            Subresources = [.. parts.SelectMany(p => p.Subresources)],
            Layers = [.. parts.SelectMany(p => Enumerable.Range(0, p.Slices).Select(k => k < p.Layers.Length ? p.Layers[k] : new TextureLayer("", 1)))],
        };
        merged.Validate(name);
        means = new float[merged.Slices];
        for (int k = 0; k < means.Length; k++) means[k] = LayerMean(merged, k);
        return AddTexture(name, name, merged);
    }

    /// <summary>Среднее канала r мипа 0 слоя: R8 — байты, BC4 — палитра блока (8 уровней или 6 + 0 и 1); иначе NaN.</summary>
    private static float LayerMean(TextureData data, int slice)
    {
        var bytes = data.Subresource(slice, 0);
        double sum = 0;
        if (data.Format == TexturePixelFormat.R8)
        {
            foreach (byte b in bytes) sum += b;
            return (float)(sum / 255 / bytes.Length);
        }
        if (data.Format != TexturePixelFormat.Bc4) return float.NaN;
        Span<float> palette = stackalloc float[8];
        for (int b = 0; b + 8 <= bytes.Length; b += 8)
        {
            float r0 = bytes[b] / 255f, r1 = bytes[b + 1] / 255f;
            palette[0] = r0;
            palette[1] = r1;
            for (int i = 1; i < 7; i++)
                palette[i + 1] = r0 > r1 ? ((7 - i) * r0 + i * r1) / 7 : i < 5 ? ((5 - i) * r0 + i * r1) / 5 : i == 5 ? 0 : 1;
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)bytes[b + 2 + i] << (8 * i);
            for (int t = 0; t < 16; t++) sum += palette[(int)(bits >> (3 * t)) & 7];
        }
        return (float)(sum / (16.0 * (bytes.Length / 8)));
    }

    /// <summary>Канал слоя 0, мип 0 текстуры пака на CPU — для CPU-эталонов и отчётов; только несжатые форматы.</summary>
    /// <exception cref="FileNotFoundException">Файла нет в контенте.</exception>
    /// <exception cref="InvalidDataException">Не текстура, обрезана или формат сжатый.</exception>
    public TextureTexels ReadTexels(string path, int channel = 0)
    {
        path = ContentPath.Normalize(path);
        var content = Content;
        string where = content.Describe(path);
        using var s = content.OpenRead(path);
        return TextureTexels.Read(s, where, channel);
    }

    private GpuTexture AddTexture(string path, string where, TextureData data)
    {
        var texture = GpuTexture.Create(Device, path, data, t => _textures.Remove(t));
        _textures.Add(texture);
        Log.Info("texture", $"{where}: {texture.Width}×{texture.Height}, слоёв {texture.Slices}, мипов {texture.Mips}, {texture.Format}" +
            $"{(texture.Srgb ? " sRGB" : "")}, {texture.Bytes / 1024.0:F0} КБ");
        return texture;
    }

    private readonly List<GpuReadback> _readbacks = [];

    /// <summary>
    /// Асинхронное чтение хранилища каждый кадр: данные кадра N приходят в <paramref name="ready"/> в начале кадра
    /// N + <see cref="GpuReadback.Latency"/> на потоке кадра, без ожидания GPU. Только представление и диагностика.
    /// </summary>
    public GpuReadback CreateReadback(GpuStorage source, uint bytes, ReadbackHandler ready)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ready);
        var readback = new GpuReadback(Device, source, bytes, ready, r => _readbacks.Remove(r));
        _readbacks.Add(readback);
        return readback;
    }

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

    /// <summary>Прочитать хранилище на CPU с ожиданием GPU — для проверок и инструментов; только между кадрами.</summary>
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
        var profiler = _profiler;
        int profileDepth = profiler.Depth;
        profiler.Begin(_zones.FenceWait);
        Device.BeginFrame();
        profiler.End(_zones.FenceWait);
        RecordGpu(profiler);   // времена кадра, забора которого ждали (или разобранного ожиданием простоя)
        // Чтения кадра этого слота: его забора дождались — данные готовы (обработчик может снять чтение — обход с конца).
        for (int i = _readbacks.Count - 1; i >= 0; i--) _readbacks[i].Collect(Device.FrameIndex);
        long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var list = Device.GraphicsList;
        try
        {
            Constants.BeginFrame(Device.FrameIndex);
            Scene.EnsureSize();
            FrameHdr = Hdr;
            HdrScene.Ensure(FrameHdr, SceneColorClear);
            Taa.Ensure(FrameHdr && Taa.Enabled);
            Skinned.Begin();
            Debug.BeginFrame();
            Lights.BeginFrame();
            HalfRes.BeginFrame();
            Shadow.BeginFrame();
            Ao.BeginFrame();
            _ui?.BeginFrame();
            Graph.BeginFrame(0);
            Graph.BackBufferFrom = FrameHdr ? FramePhase.Post : FramePhase.Simulation;

            profiler.Begin(_zones.Build);
            build(_frame);
            Skinned.Flush();
            Lights.Flush();
            HalfRes.EndFrame();
            profiler.End(_zones.Build);
            // Bloom, тонмап и SMAA — последними проходами фазы Post: модули регистрируют свою постобработку раньше, во время построения кадра.
            if (FrameHdr) HdrScene.AddTonemap(Graph, Exposure, Bloom, Smaa, AutoExposure, Post, Taa);
            else HdrScene.SkipFrame(AutoExposure);

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

        Vortice.Direct3D12.ID3D12Resource? readback = null, depthReadback = null, hdrReadback = null, bloomReadback = null, halfReadback = null,
            velocityReadback = null;
        var notes = capture is null ? null : new List<string>();   // обычный кадр — без выделений
        try
        {
            int w = 0, h = 0, pitch = 0, dw = 0, dh = 0, dpitch = 0, hw = 0, hh = 0, hpitch = 0, bw = 0, bh = 0, bpitch = 0, tw = 0, th = 0,
                tpitch = 0, vw = 0, vh = 0, vpitch = 0;
            if (capture?.VelocityPath is not null)
                velocityReadback = Screenshot.RecordTextureCapture(Device, list, Scene.VelocityResource, out vw, out vh, out vpitch);
            if (capture?.DepthPath is not null)
                depthReadback = Screenshot.RecordDepthCapture(Device, list, Scene.DepthResource, out dw, out dh, out dpitch);
            if (capture?.ColorPath is not null) readback = Screenshot.RecordCapture(Device, list, out w, out h, out pitch);
            // Цвет HDR-сцены — при любом снимке с HDR: сводка яркости входа тонмапа (и карта, если заказана).
            if (capture is not null && FrameHdr)
                hdrReadback = Screenshot.RecordTextureCapture(Device, list, HdrScene.Resource, out hw, out hh, out hpitch);
            else if (capture?.HdrPath is not null) notes!.Add("карта HDR заказана, а HDR в кадре выключен (Renderer.Hdr)");
            // Итог bloom — при любом снимке с bloom: сводка добавки (и карта, если заказана).
            if (capture is not null && FrameHdr && HdrScene.BloomOutput is { } bloomOutput)
                bloomReadback = Screenshot.RecordTextureCapture(Device, list, Graph.Get(bloomOutput.Index, buffer: false), out bw, out bh, out bpitch);
            else if (capture?.BloomPath is not null) notes!.Add("карта bloom заказана, а bloom в кадре нет (Renderer.Hdr, Renderer.Bloom)");
            // Цель ½ прозрачного — при любом снимке кадра со слоем ½ (до композита: то, что легло поверх сцены).
            if (capture is not null && FrameHdr && HalfRes.Current is { } half)
                halfReadback = Screenshot.RecordTextureCapture(Device, list, Graph.Get(half.Color.Index, buffer: false), out tw, out th, out tpitch);

            // Чтения — последними командами кадра, после всех проходов; ждут забора — только если кадр отправлен.
            foreach (var r in _readbacks) r.Record(list, Device.FrameIndex, Device.FrameNumber);
            profiler.Begin(_zones.Present);
            Device.EndFrame(vsync);
            profiler.End(_zones.Present);
            foreach (var r in _readbacks) r.Commit(Device.FrameIndex);

            if (capture is not null)
            {
                profiler.Begin(_zones.Capture);
                Device.WaitForIdle();
                string? current = null;
                try
                {
                    var depth = (Min: 0f, Max: 0f, Coverage: 0f);
                    if (readback is not null)
                    {
                        current = capture.ColorPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        Screenshot.Save(readback, current, w, h, pitch);
                    }
                    if (depthReadback is not null)
                    {
                        current = capture.DepthPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        depth = Screenshot.SaveDepth(depthReadback, current, dw, dh, dpitch);
                    }
                    if (velocityReadback is not null)
                    {
                        current = capture.VelocityPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        Screenshot.SaveRaw(velocityReadback, current, vw, vh, vpitch, 4);
                    }
                    HdrCapture? hdr = null;
                    if (hdrReadback is not null)
                    {
                        current = capture.HdrPath;
                        if (current is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        hdr = Screenshot.SaveHdr(hdrReadback, Exposure, hw, hh, hpitch, current, current is null ? null : Path.ChangeExtension(current, ".bin"),
                            out _);
                    }
                    if (bloomReadback is not null && hdr is not null)
                    {
                        current = capture.BloomPath;
                        if (current is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        var b = Screenshot.SaveHdr(bloomReadback, Exposure, bw, bh, bpitch, current, current is null ? null : Path.ChangeExtension(current, ".bin"),
                            out _);
                        hdr = hdr with
                        {
                            Bloom = new BloomCapture(HdrScene.FrameBloom, bw, bh, b.MaxLuminance, b.MeanLuminance, b.NonFinite)
                            {
                                Path = b.Path, RawPath = b.RawPath,
                            },
                        };
                    }
                    if (halfReadback is not null && hdr is not null)
                    {
                        current = capture.TransparentPath;
                        if (current is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        var t = Screenshot.SaveHdr(halfReadback, Exposure, tw, th, tpitch, current, current is null ? null : Path.ChangeExtension(current, ".bin"),
                            out float halfCoverage);
                        hdr = hdr with
                        {
                            Transparent = new TransparentCapture(tw, th, t.MaxLuminance, t.MeanLuminance, halfCoverage, t.NonFinite)
                            {
                                Path = t.Path, RawPath = t.RawPath,
                            },
                        };
                    }
                    if (readback is not null || depthReadback is not null || hdr is not null || notes!.Count > 0)
                        LastCapture = new CaptureResult(Width, Height, readback is not null ? capture.ColorPath : null,
                            depthReadback is not null ? capture.DepthPath : null, depth.Min, depth.Max, depth.Coverage)
                        {
                            Notes = notes!, Hdr = hdr,
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
            if (hdrReadback is not null) Device.DeferRelease(hdrReadback);
            if (bloomReadback is not null) Device.DeferRelease(bloomReadback);
            if (halfReadback is not null) Device.DeferRelease(halfReadback);
            if (velocityReadback is not null) Device.DeferRelease(velocityReadback);
        }
        Device.DrainDebugMessages();
    }

    /// <summary>Ресурсы рендера и устройство; ресурсы сцены игры освобождаются раньше. Ждёт GPU.</summary>
    public void Dispose()
    {
        Device.WaitForIdle();
        foreach (var readback in _readbacks.ToArray()) readback.Dispose();
        foreach (var texture in _textures.ToArray()) texture.Dispose();
        _ui?.Dispose();
        _uiFonts?.Dispose();   // проход не создавался — шрифты вызывающего остались у рендера
        Lights.Dispose();
        Debug.Dispose();
        Skinned.Dispose();
        Graph.Dispose();
        HdrScene.Dispose();
        AutoExposure?.Dispose();
        Taa?.Dispose();
        Ao.Dispose();
        Scene.Dispose();
        Constants.Dispose();
        Pipelines.Dispose();
        _content?.Dispose();
        // Dispose устройства ждёт простоя и освобождает отложенное: и ресурсы выше, и ресурсы сцены игры.
        Device.Dispose();
    }
}
