using System.Runtime.CompilerServices;
using Sword.Engine.Formats;
using Sword.Engine.Rhi;
using Sword.Engine.Ui;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Sword.Engine.Render;

/// <summary>
/// UI-проход: квады <see cref="UiDrawList"/> поверх цели — фигуры полем расстояний и GPU-векторный текст
/// (<c>common/ui.hlsl</c>). Без вершинного буфера: записи в кольце кадра, вершины по SV_VertexID. Вызов рисования рвётся
/// на смене конвейера (фигуры / текст) и обрезки (scissor). Шрифты склеиваются при создании и живут в видеопамяти до конца.
/// </summary>
public sealed class UiPass : IDisposable
{
    /// <summary>Слоты корневых констант прохода (имена — в контракте шейдеров, <see cref="Contribute"/>).</summary>
    private const int SlotQuads = 0, SlotBase = 1, SlotWidth = 2, SlotHeight = 3, SlotGamma = 4,
        SlotGlyphs = 5, SlotCurves = 6, SlotBands = 7, SlotIndices = 8;

    /// <summary>Проходов интерфейса в кадре не больше: работа прохода — заранее созданные делегаты, кадр без выделений.</summary>
    public const int MaxPassesPerFrame = 4;

    private const string Shader = "common/ui.hlsl";
    private static readonly GraphicsPipelineDesc Shapes = new(Shader, "VsUi", "PsUi", BlendMode.Alpha);
    private static readonly GraphicsPipelineDesc VectorText = new(Shader, "VsUi", "PsVectorText", BlendMode.Alpha);

    private readonly Renderer _renderer;
    private readonly FontBuffer _glyphs, _curves, _bands, _indices;
    private FrameData<UiQuad> _quads;
    private int _used;
    private int _passes;
    private readonly Work[] _work = new Work[MaxPassesPerFrame];
    private readonly Action<RenderPassContext>[] _record = new Action<RenderPassContext>[MaxPassesPerFrame];
    private bool _disposed;

    /// <summary>Работа одного прохода кадра: кусок кольца и отрезки «один конвейер, одна обрезка».</summary>
    private sealed class Work
    {
        public int Srv;
        public float Gamma;
        public int Base;
        public readonly List<UiBatch> Batches = [];
    }

    /// <param name="fonts">Шрифты от вызывающего (<see cref="RendererOptions.UiFonts"/>, проход берёт их во владение); null — из пака.</param>
    internal UiPass(Renderer renderer, UiFonts? fonts)
    {
        if (Unsafe.SizeOf<UiQuad>() != UiQuad.Stride)
            throw new InvalidOperationException($"шаг записи квада {Unsafe.SizeOf<UiQuad>()}, ожидается {UiQuad.Stride}");
        _renderer = renderer;
        var gd = renderer.Device;
        Fonts = fonts ?? ReadFonts(renderer.Content);
        FontBuffer? glyphs = null, curves = null, bands = null, indices = null;
        try
        {
            var t = Fonts.Tables;
            glyphs = FontBuffer.Create(gd, "Ui.VectorGlyphs", t.Glyphs);
            curves = FontBuffer.Create(gd, "Ui.VectorCurves", t.Curves);
            bands = FontBuffer.Create(gd, "Ui.VectorBands", t.Bands);
            indices = FontBuffer.Create(gd, "Ui.VectorIndices", t.Indices);
            _quads = renderer.CreateFrameData<UiQuad>("Ui.Quads", 4096);
        }
        catch
        {
            indices?.Dispose();
            bands?.Dispose();
            curves?.Dispose();
            glyphs?.Dispose();
            Fonts.Dispose();
            throw;
        }
        (_glyphs, _curves, _bands, _indices) = (glyphs, curves, bands, indices);
        for (int i = 0; i < MaxPassesPerFrame; i++)
        {
            var work = _work[i] = new Work();
            _record[i] = ctx => Record(ctx, work);
        }
    }

    /// <summary>Шрифты интерфейса из пака: все <c>fonts/*.swfont</c>.</summary>
    private static UiFonts ReadFonts(Sword.Engine.Content.IContentSource content)
    {
        var files = content.List(UiVectorFontFormat.Dir, UiVectorFontFormat.Extension);
        if (files.Count == 0)
            throw new FileNotFoundException(
                $"нет шрифтов интерфейса {content.Describe(UiVectorFontFormat.Dir)}/*{UiVectorFontFormat.Extension}", content.Describe(UiVectorFontFormat.Dir));
        var fonts = new List<UiFontFile>(files.Count);
        foreach (var file in files)
            using (var stream = content.OpenRead(file))
                fonts.Add(UiFontFile.Read(stream, content.Describe(file)));
        return new UiFonts(fonts);
    }

    /// <summary>Числа прохода для всех шейдеров: режимы квадов, полосы glyph'а, слоты, размеры записей.</summary>
    public static void Contribute(ShaderContract c)
    {
        c.Section("интерфейс (UiPass, Sword.Engine.Ui)");
        c.Add("UI_BOX", (uint)UiMode.Box);
        c.Add("UI_TRI", (uint)UiMode.Triangle);
        c.Add("UI_SPRITE", (uint)UiMode.Sprite);
        c.Add("UI_VECTOR_BANDS", UiVectorFontFormat.BandsPerAxis, "полос на ось у glyph'а");
        c.AddSlot("UI_QUADS", SlotQuads, "записи квадов (кольцо кадра)");
        c.AddSlot("UI_BASE", SlotBase, "первый квад вызова");
        c.AddSlot("UI_WIDTH", SlotWidth, "ширина цели, пиксели");
        c.AddSlot("UI_HEIGHT", SlotHeight, "высота цели, пиксели");
        c.AddSlot("UI_GAMMA", SlotGamma, "гамма покрытия текста (float битами)");
        c.AddSlot("UI_GLYPHS", SlotGlyphs, "glyph'ы шрифтов");
        c.AddSlot("UI_CURVES", SlotCurves, "кривые контуров");
        c.AddSlot("UI_BANDS", SlotBands, "полосы glyph'ов");
        c.AddSlot("UI_INDICES", SlotIndices, "номера кривых полос");
        c.AddSize<UiQuad>("UI_QUAD_SIZE");
        c.AddSize<VectorGlyph>("UI_GLYPH_SIZE");
        c.AddSize<VectorCurve>("UI_CURVE_SIZE");
        c.AddSize<VectorBand>("UI_BAND_SIZE");
    }

    /// <summary>Шрифты интерфейса: для списков квадов (<see cref="CreateDrawList"/>) и измерений текста.</summary>
    public UiFonts Fonts { get; }

    /// <summary>Список квадов с этими шрифтами. Список — вызывающего: он чистит и наполняет его каждый кадр.</summary>
    public UiDrawList CreateDrawList() => new(Fonts);

    /// <summary>Квадов, отправленных последним <see cref="Add"/>.</summary>
    public int LastQuads { get; private set; }

    /// <summary>Отрезков (вызовов рисования) последнего <see cref="Add"/>.</summary>
    public int LastBatches { get; private set; }

    /// <summary>Ёмкость кольца квадов, записей на кадр (растёт по нужде).</summary>
    public uint QuadCapacity => _quads.Capacity;

    public string Report() => $"UI: {Fonts.Report()}, GPU {(_glyphs.Bytes + _curves.Bytes + _bands.Bytes + _indices.Bytes) / 1024.0 / 1024.0:F2} МБ";

    /// <summary>
    /// Проход интерфейса поверх уже нарисованного: квады копируются в кольцо кадра сейчас (список можно менять сразу),
    /// рисуются, когда граф дойдёт до прохода. Пустой список — прохода нет. Фаза — <see cref="FramePhase.Ui"/> (LDR).
    /// </summary>
    public void Add(RenderFrame frame, UiDrawList draw, string name = "ui", RenderTexture? target = null, FramePhase phase = FramePhase.Ui)
    {
        LastQuads = draw.Count;
        LastBatches = 0;
        if (draw.Count == 0) return;
        if (draw.HasFonts && !ReferenceEquals(draw.Fonts, Fonts))
            throw new ArgumentException("список квадов с чужими шрифтами: номера glyph'ов — этого прохода", nameof(draw));
        if (_passes == MaxPassesPerFrame)
            throw new InvalidOperationException($"проходов интерфейса в кадре больше {MaxPassesPerFrame}");

        if (_used + draw.Count > _quads.Capacity)
        {
            // Кольцо мало — новое вдвое больше; старое освобождается отложенно (проходы кадра читают его на GPU).
            // Сначала новое: не создалось — старое остаётся рабочим, кадр бросается целиком.
            uint capacity = Math.Max(_quads.Capacity * 2, (uint)(draw.Count + 1024));
            var grown = _renderer.CreateFrameData<UiQuad>("Ui.Quads", capacity);
            _quads.Dispose();
            _quads = grown;
            _used = 0;
        }
        draw.Quads.CopyTo(_quads.Slot[_used..]);

        var work = _work[_passes];
        work.Srv = _quads.Srv;
        work.Gamma = draw.TextGamma;
        work.Base = _used;
        draw.Split(frame.Width, frame.Height, work.Batches);
        _used += draw.Count;
        LastBatches = work.Batches.Count;

        frame.Graph.AddPass(name, phase).Color(target ?? frame.Graph.BackBuffer).Execute(_record[_passes]);
        _passes++;
    }

    /// <summary>Начало кадра (<see cref="Renderer.Render"/>): кольцо квадов этого кадра пусто, проходов нет.</summary>
    internal void BeginFrame()
    {
        _used = 0;
        _passes = 0;
    }

    private void Record(RenderPassContext ctx, Work work)
    {
        ctx.SetConstant(SlotQuads, work.Srv);
        ctx.SetConstant(SlotWidth, ctx.Width);
        ctx.SetConstant(SlotHeight, ctx.Height);
        ctx.SetConstant(SlotGamma, work.Gamma);
        ctx.SetConstant(SlotGlyphs, _glyphs.Srv);
        ctx.SetConstant(SlotCurves, _curves.Srv);
        ctx.SetConstant(SlotBands, _bands.Srv);
        ctx.SetConstant(SlotIndices, _indices.Srv);
        bool? vectorPipeline = null;
        (int, int, int, int) scissor = (0, 0, ctx.Width, ctx.Height);   // граф ставит всю цель в начале прохода
        foreach (var b in work.Batches)
        {
            if (scissor != (b.X0, b.Y0, b.X1, b.Y1))
            {
                ctx.SetScissor(b.X0, b.Y0, b.X1, b.Y1);
                scissor = (b.X0, b.Y0, b.X1, b.Y1);
            }
            if (vectorPipeline != b.Vector)
            {
                ctx.SetPipeline(b.Vector ? VectorText : Shapes);
                vectorPipeline = b.Vector;
            }
            ctx.SetConstant(SlotBase, work.Base + b.Start);
            ctx.Draw((uint)(b.Count * 6));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quads.Dispose();
        _glyphs.Dispose();
        _curves.Dispose();
        _bands.Dispose();
        _indices.Dispose();
        Fonts.Dispose();
    }

    /// <summary>Неизменяемые контуры шрифта в видеопамяти (DEFAULT-куча, SRV).</summary>
    private sealed class FontBuffer : IDisposable
    {
        private readonly GraphicsDevice _gd;
        private readonly ID3D12Resource _resource;
        private bool _disposed;

        public int Srv { get; }
        public ulong Bytes { get; }

        private FontBuffer(GraphicsDevice gd, ID3D12Resource resource, int srv, ulong bytes)
        {
            _gd = gd;
            _resource = resource;
            Srv = srv;
            Bytes = bytes;
        }

        /// <summary>Пустая таблица (шрифт без кривых) — один нулевой элемент: буфера нулевого размера D3D12 не создаёт.</summary>
        public static unsafe FontBuffer Create<T>(GraphicsDevice gd, string name, ReadOnlySpan<T> data) where T : unmanaged
        {
            if (data.IsEmpty) data = new T[1];
            uint stride = (uint)sizeof(T);
            ulong bytes = (ulong)data.Length * stride;
            var resource = gd.Device.CreateCommittedResource(HeapType.Default, HeapFlags.None, ResourceDescription.Buffer(bytes), ResourceStates.Common);
            resource.Name = name;
            int srv = -1;
            try
            {
                using var upload = gd.CreateUploadBuffer(bytes, name + ".Upload");
                void* mapped = upload.Map<byte>(0);
                data.CopyTo(new Span<T>(mapped, data.Length));
                upload.Unmap(0);
                gd.ExecuteImmediate(list =>
                {
                    list.CopyBufferRegion(resource, 0, upload, 0, bytes);
                    list.ResourceBarrierTransition(resource, ResourceStates.CopyDest,
                        ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
                });

                srv = gd.Bindless.Allocate();
                gd.Device.CreateShaderResourceView(resource, new ShaderResourceViewDescription
                {
                    Format = Format.Unknown,
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)data.Length, StructureByteStride = stride },
                }, gd.Bindless.Cpu(srv));
                return new FontBuffer(gd, resource, srv, bytes);
            }
            catch
            {
                if (srv >= 0) gd.DeferRelease(resource, srv);
                else resource.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gd.DeferRelease(_resource, Srv);
        }
    }
}
