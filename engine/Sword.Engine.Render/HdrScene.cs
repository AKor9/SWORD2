using System.Numerics;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Sword.Engine.Render;

/// <summary>
/// HDR-кадр на снимке: экспозиция и яркость входа тонмапа (Rec.709) по кадру — максимум, среднее, доля ярче 1,
/// пикселей с NaN или бесконечностью (в среднее и максимум не входят).
/// </summary>
public sealed record HdrCapture(float Exposure, float MaxLuminance, float MeanLuminance, float OverOne, int NonFinite)
{
    /// <summary>
    /// Карта яркости входа тонмапа в ложных цветах (PNG; <see cref="FrameCapture.HdrPath"/>): по ступеням log2 — чёрный до 1/256,
    /// тёмно-синий 1/64, синий 1/16, зелёный 0.18 (средний серый), жёлто-зелёный 0.5, жёлтый 1, красный 4, пурпурный 16, белый от 64;
    /// NaN и бесконечность — голубые. Null — не заказана.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>Сырой цвет сцены рядом с картой (<c>.bin</c>: RGBA half на пиксель, строки сверху вниз, без экспозиции).</summary>
    public string? RawPath { get; init; }

    /// <summary>Добавка bloom к этому входу (тонмап видит сцену + bloom); null — bloom в кадре выключен.</summary>
    public BloomCapture? Bloom { get; init; }

    /// <summary>Цель ½ прозрачного до композита (<see cref="HalfResTransparency"/>); null — слоя ½ в кадре не было.</summary>
    public TransparentCapture? Transparent { get; init; }
}

/// <summary>
/// Цель HDR-сцены <c>scene.color</c> (RGBA16F, линейный цвет) и тонмап: в фазе Post — bloom и <c>post.tonemap</c>
/// ((цвет + bloom) × экспозиция → Filmic → sRGB), с SMAA — затем его проходы (<see cref="SmaaPasses"/>). Выключенный HDR цели не держит.
/// </summary>
internal sealed class HdrScene : IDisposable
{
    internal const Format ColorFormat = Format.R16G16B16A16_Float;
    private const string Shader = "common/post.hlsl";
    private static readonly GraphicsPipelineDesc Tonemap = new(Shader, "VsFullscreen", "PsTonemap");

    // Кривая рендера — умолчания UE 5.8: точки стыка считаются один раз и уходят в шейдер корневыми константами.
    private static readonly FilmicCurve.Constants Curve = FilmicCurve.Default.Derive();

    /// <summary>Корневые константы тонмапа: SRV цели сцены, экспозиция, кривая, SRV итога bloom (или BLOOM_NONE).</summary>
    public const int SlotScene = 0, SlotExposure = 1, SlotSlope = 2, SlotToeMatch = 3, SlotStraightMatch = 4, SlotShoulderMatch = 5,
        SlotToeScale = 6, SlotShoulderScale = 7, SlotBlackClip = 8, SlotWhiteClip = 9, SlotInvSpan = 10, SlotBloom = 11, SlotExposureBuffer = 12,
        SlotGrade = 13, SlotWbR = 14, SlotWbG = 15, SlotWbB = 16, SlotSaturation = 17, SlotContrast = 18, SlotVignette = 19;

    private readonly GraphicsDevice _gd;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly GraphResource _color = new("scene.color", isBuffer: false) { TargetFormat = ColorFormat };
    private readonly Action<RenderPassContext> _record;
    private readonly BloomChain _bloom = new();
    private readonly SmaaPasses _smaa = new();
    private float _exposure;
    private int _bloomSrv = -1;
    private uint _exposureSrv = AutoExposure.None;
    private PostEffects.Grade _grade;
    private int _inputSrv;

    internal HdrScene(GraphicsDevice gd, RenderGraph graph)
    {
        _gd = gd;
        _rtvHeap = gd.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        _color.Rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        Color = graph.ImportPersistent(_color);
        _record = Record;
    }

    public static void Contribute(ShaderContract c)
    {
        c.AddSlot("POST_SCENE", SlotScene, "SRV scene.color");
        c.AddSlot("POST_EXPOSURE", SlotExposure, "экспозиция, float");
        c.AddSlot("POST_SLOPE", SlotSlope);
        c.AddSlot("POST_TOE_MATCH", SlotToeMatch);
        c.AddSlot("POST_STRAIGHT_MATCH", SlotStraightMatch);
        c.AddSlot("POST_SHOULDER_MATCH", SlotShoulderMatch);
        c.AddSlot("POST_TOE_SCALE", SlotToeScale);
        c.AddSlot("POST_SHOULDER_SCALE", SlotShoulderScale);
        c.AddSlot("POST_BLACK_CLIP", SlotBlackClip);
        c.AddSlot("POST_WHITE_CLIP", SlotWhiteClip);
        c.AddSlot("POST_INV_SPAN", SlotInvSpan);
        c.AddSlot("POST_BLOOM", SlotBloom, "SRV итога bloom или BLOOM_NONE");
        c.AddSlot("POST_EXPOSURE_BUFFER", SlotExposureBuffer, "SRV буфера автоэкспозиции или EXPOSURE_NONE");
        c.AddSlot("POST_GRADE", SlotGrade, "цветокоррекция и виньетка: 0 - нейтрально (без них)");
        c.AddSlot("POST_WB_R", SlotWbR);
        c.AddSlot("POST_WB_G", SlotWbG);
        c.AddSlot("POST_WB_B", SlotWbB);
        c.AddSlot("POST_SATURATION", SlotSaturation);
        c.AddSlot("POST_CONTRAST", SlotContrast);
        c.AddSlot("POST_VIGNETTE", SlotVignette);
        PostEffects.Contribute(c);
        BloomKernel.Contribute(c);
        BloomChain.Contribute(c);
        SmaaPasses.Contribute(c);
    }

    /// <summary>Цвет сцены (линейный, RGBA16F); как текстура — <c>Texture2D&lt;float4&gt;</c>.</summary>
    public RenderTexture Color { get; }

    /// <summary>Цель как ресурс графа — для снимка.</summary>
    internal GraphResource Resource => _color;

    /// <summary>Итог bloom этого кадра (½ экрана, читает тонмап); null — bloom в кадре нет.</summary>
    internal RenderTexture? BloomOutput { get; private set; }

    /// <summary>Настройки bloom этого кадра (на момент регистрации тонмапа).</summary>
    internal BloomSettings FrameBloom { get; private set; }

    /// <summary>SMAA в этом кадре (на момент регистрации тонмапа).</summary>
    internal bool FrameSmaa { get; private set; }

    /// <summary>Цель под размер заднего буфера и очистку <paramref name="clear"/>; выключенный HDR — освободить.</summary>
    internal void Ensure(bool enabled, Vector4 clear)
    {
        if (!enabled)
        {
            Release();
            return;
        }
        int w = _gd.BackBufferWidth, h = _gd.BackBufferHeight;
        if (_color.Handle is not null && w == _color.Width && h == _color.Height && _color.ClearColor == clear) return;
        Release();
        var device = _gd.Device;
        var color = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(ColorFormat, (uint)w, (uint)h, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource, new ClearValue(ColorFormat, new Color4(clear.X, clear.Y, clear.Z, clear.W)));
        color.Name = "Sword.SceneColor";
        device.CreateRenderTargetView(color, null, _color.Rtv!.Value);
        _color.Srv = _gd.Bindless.Allocate();
        device.CreateShaderResourceView(color, null, _gd.Bindless.Cpu(_color.Srv));
        _color.Handle = color;
        _color.State = ResourceStates.PixelShaderResource;
        _color.Width = w;
        _color.Height = h;
        _color.ClearColor = clear;
    }

    /// <summary>
    /// Bloom, тонмап и SMAA в фазу Post — последними из зарегистрированных: bloom видит постобработку модулей. С SMAA тонмап
    /// пишет его вход, а задний буфер — проход смешивания SMAA.
    /// </summary>
    internal void AddTonemap(RenderGraph graph, float exposure, BloomSettings bloom, bool smaa, AutoExposure auto, PostEffects post, TemporalAa taa)
    {
        _grade = post.FrameGrade();
        // TAA (и размытие движения) — первыми: дальше вся постобработка читает их итог, а не сцену.
        var (input, inputSrv) = taa.Enabled ? taa.Add(graph, Color, _color.Srv) : (Color, _color.Srv);
        _inputSrv = inputSrv;
        _exposure = exposure;
        FrameBloom = bloom;
        FrameSmaa = smaa;
        if (auto.Enabled) auto.Add(graph, input, inputSrv, exposure);
        else auto.SkipFrame();
        _exposureSrv = auto.Srv;
        BloomOutput = bloom.Active ? _bloom.Add(graph, input, inputSrv, exposure, bloom, auto.Buffer) : null;
        _bloomSrv = BloomOutput is { } b ? graph.SrvOf(b) : -1;
        var pass = graph.AddPass("post.tonemap", FramePhase.Post).Read(input);
        if (BloomOutput is { } t) pass.Read(t);
        if (auto.Buffer is { } e) pass.Read(e);
        // Цепочка LDR до заднего буфера: тонмап → SMAA → резкость; последняя ступень пишет задний буфер.
        bool sharpen = post.SharpenActive;
        var target = smaa || sharpen ? graph.CreateTexture("post.ldr", SmaaPasses.Desc(graph)) : graph.BackBuffer;
        pass.Color(target).Execute(_record);
        if (smaa)
        {
            var output = sharpen ? graph.CreateTexture("post.smaa", SmaaPasses.Desc(graph)) : graph.BackBuffer;
            _smaa.Add(graph, target, output);
            target = output;
        }
        if (sharpen) post.AddSharpen(graph, target, graph.BackBuffer);
    }

    /// <summary>Кадр без HDR: ни тонмапа, ни bloom.</summary>
    internal void SkipFrame(AutoExposure auto)
    {
        auto.SkipFrame();
        BloomOutput = null;
        FrameSmaa = false;
    }

    private void Record(RenderPassContext ctx)
    {
        var k = Curve;
        ctx.SetPipeline(Tonemap);
        ctx.SetConstant(SlotScene, _inputSrv);
        ctx.SetConstant(SlotExposure, _exposure);
        ctx.SetConstant(SlotSlope, k.Slope);
        ctx.SetConstant(SlotToeMatch, k.ToeMatch);
        ctx.SetConstant(SlotStraightMatch, k.StraightMatch);
        ctx.SetConstant(SlotShoulderMatch, k.ShoulderMatch);
        ctx.SetConstant(SlotToeScale, k.ToeScale);
        ctx.SetConstant(SlotShoulderScale, k.ShoulderScale);
        ctx.SetConstant(SlotBlackClip, k.BlackClip);
        ctx.SetConstant(SlotWhiteClip, k.WhiteClip);
        ctx.SetConstant(SlotInvSpan, k.InvSpan);
        ctx.SetConstant(SlotBloom, _bloomSrv);
        ctx.SetConstant(SlotExposureBuffer, _exposureSrv);
        var g = _grade;
        ctx.SetConstant(SlotGrade, g.Neutral ? 0 : 1);
        ctx.SetConstant(SlotWbR, g.WhiteBalance.X);
        ctx.SetConstant(SlotWbG, g.WhiteBalance.Y);
        ctx.SetConstant(SlotWbB, g.WhiteBalance.Z);
        ctx.SetConstant(SlotSaturation, g.Saturation);
        ctx.SetConstant(SlotContrast, g.Contrast);
        ctx.SetConstant(SlotVignette, g.Vignette);
        ctx.Draw(3);
    }

    private void Release()
    {
        if (_color.Handle is null) return;
        _gd.DeferRelease(_color.Handle, _color.Srv);
        _color.Handle = null;
        _color.Srv = -1;
    }

    public void Dispose()
    {
        Release();
        _rtvHeap.Dispose();
    }
}
