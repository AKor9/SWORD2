using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Постобработка кадра (<see cref="Renderer.Post"/>): цветокоррекция и виньетка — в тонмапе до кривой (линейный Rec.709 после
/// экспозиции), резкость — проходом <c>post.sharpen</c> после тонмапа и SMAA (LDR). По умолчанию всё нейтрально — кадр прежний бит в бит.
/// </summary>
public sealed class PostEffects
{
    /// <summary>Цвет для шейдера (<c>post.hlsl</c>): множители баланса белого, насыщенность, контраст, виньетка.</summary>
    internal readonly record struct Grade(Vector3 WhiteBalance, float Saturation, float Contrast, float Vignette, bool Neutral);

    public const int SlotSharpenSource = 0, SlotSharpenAmount = 1;

    private const string Shader = "common/post.hlsl";
    private static readonly GraphicsPipelineDesc SharpenPipeline = new(Shader, "VsFullscreen", "PsSharpen");

    private float _saturation = 1, _contrast = 1, _temperature, _vignette, _sharpen;
    private readonly Action<RenderPassContext> _recordSharpen;
    private int _sharpenSrv;
    private float _frameSharpen;

    public PostEffects() => _recordSharpen = RecordSharpen;

    /// <summary>Насыщенность: 0 — серое, 1 — как есть, 2 — вдвое (0…2).</summary>
    public float Saturation
    {
        get => _saturation;
        set => _saturation = value is >= 0 and <= 2 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "насыщенность 0…2");
    }

    /// <summary>Контраст вокруг среднего серого 0.18 в логарифме: 1 — как есть (0.5…2).</summary>
    public float Contrast
    {
        get => _contrast;
        set => _contrast = value is >= 0.5f and <= 2 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "контраст 0.5…2");
    }

    /// <summary>Баланс белого: −1 — холоднее, +1 — теплее (красный и синий ±15 % при той же яркости), 0 — как есть.</summary>
    public float Temperature
    {
        get => _temperature;
        set => _temperature = value is >= -1 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "баланс −1…1");
    }

    /// <summary>Виньетка: затемнение углов кадра (0 — нет, 1 — углы чёрные).</summary>
    public float Vignette
    {
        get => _vignette;
        set => _vignette = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "виньетка 0…1");
    }

    /// <summary>Резкость (адаптивная к контрасту): 0 — прохода нет, 1 — сильно.</summary>
    public float Sharpen
    {
        get => _sharpen;
        set => _sharpen = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "резкость 0…1");
    }

    /// <summary>Резкость в кадре.</summary>
    internal bool SharpenActive => _sharpen > 0;

    /// <summary>Числа цвета для тонмапа этого кадра.</summary>
    internal Grade FrameGrade()
    {
        // Тёплее — красный вверх, синий вниз; деление на яркость множителей оставляет серый той же яркости.
        var wb = new Vector3(1 + 0.15f * _temperature, 1, 1 - 0.15f * _temperature);
        wb /= Vector3.Dot(wb, BloomKernel.Luma);
        bool neutral = _saturation == 1 && _contrast == 1 && _temperature == 0 && _vignette == 0;
        return new Grade(wb, _saturation, _contrast, _vignette, neutral);
    }

    /// <summary>Проход резкости в фазу Post: <paramref name="source"/> (LDR) → <paramref name="target"/>.</summary>
    internal void AddSharpen(RenderGraph graph, RenderTexture source, RenderTexture target)
    {
        _sharpenSrv = graph.SrvOf(source);
        _frameSharpen = _sharpen;
        graph.AddPass("post.sharpen", FramePhase.Post).Read(source).Color(target).Execute(_recordSharpen);
    }

    private void RecordSharpen(RenderPassContext ctx)
    {
        ctx.SetPipeline(SharpenPipeline);
        ctx.SetConstant(SlotSharpenSource, _sharpenSrv);
        ctx.SetConstant(SlotSharpenAmount, _frameSharpen);
        ctx.Draw(3);
    }

    internal static void Contribute(ShaderContract c)
    {
        c.AddSlot("SHARPEN_SOURCE", SlotSharpenSource, "SRV кадра после тонмапа (и SMAA)");
        c.AddSlot("SHARPEN_AMOUNT", SlotSharpenAmount, "резкость 0..1, float");
    }
}
