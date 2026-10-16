using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Прозрачное на снимке: цель ½ до композита — яркость премультиплицированного цвета × экспозиция, покрытие, NaN/∞.
/// </summary>
/// <param name="Coverage">Средняя альфа цели ½: 0 — ничего не нарисовано, 1 — всё закрыто.</param>
public sealed record TransparentCapture(int Width, int Height, float MaxLuminance, float MeanLuminance, float Coverage, int NonFinite)
{
    /// <summary>Цвет цели ½ в ложных цветах той же шкалы, что карта HDR (<see cref="HdrCapture.Path"/>); null — не заказана.</summary>
    public string? Path { get; init; }

    /// <summary>Сырая цель ½ рядом с картой (<c>.bin</c>: RGBA half на пиксель, строки сверху вниз, без экспозиции).</summary>
    public string? RawPath { get; init; }
}

/// <summary>
/// Цель ½ прозрачного: цвет RGBA16F (премультиплицированный, очищен нулём — проходы не чистят) и глубина сцены ½
/// (D32, ближайшая глубина блока 2×2; только чтение). <see cref="ViewDepth"/> — параметры для <c>SceneViewDepth</c>.
/// </summary>
public readonly record struct HalfResTarget(RenderTexture Color, RenderTexture Depth, int ColorSrv, int DepthSrv, int Width, int Height,
    Vector2 ViewDepth);

/// <summary>CPU-эталон <c>common/gbuffer.hlsli</c> (<c>SceneViewDepth</c>, <c>DepthFade</c>) и <c>common/halfres.hlsl</c>.</summary>
public static class TransparencyMath
{
    /// <summary>
    /// Параметры линейной глубины k перспективной проекции <paramref name="projection"/> (строки-векторы System.Numerics, обратный Z,
    /// конечная или бесконечная дальняя плоскость): глубина d = z/w → расстояние вдоль оси камеры k.y / (d − k.x).
    /// </summary>
    public static Vector2 ViewDepthParams(in Matrix4x4 projection)
    {
        if (projection.M34 == 0) throw new ArgumentException("не перспективная проекция (M34 = 0)", nameof(projection));
        return new Vector2(projection.M33 / projection.M34, projection.M43);
    }

    /// <summary>Расстояние вдоль оси камеры (м) по глубине <paramref name="depth"/>; пусто при бесконечной проекции — очень далеко, конечно.</summary>
    public static float ViewDepth(float depth, Vector2 k) => k.Y / MathF.Max(depth - k.X, 1e-20f);

    /// <summary>Мягкий край: saturate((сцена − пиксель) / <paramref name="fadeDistance"/>) по линейным глубинам.</summary>
    public static float DepthFade(float sceneViewDepth, float pixelViewDepth, float fadeDistance) =>
        Math.Clamp((sceneViewDepth - pixelViewDepth) / fadeDistance, 0, 1);

    /// <summary>Сторона цели ½: половина с округлением вверх (последний тексел нечётного кадра — один столбец или строка).</summary>
    public static int HalfSize(int size) => (size + 1) / 2;

    /// <summary>
    /// Тексели ½ апсемпла вдоль оси для полного пикселя <paramref name="x"/>: центр пикселя в текселях ½ — x/2 − 1/4, соседи
    /// <c>I0</c>, <c>I1</c> (зажаты в цель <paramref name="halfSize"/>), вес <c>I1</c> — 3/4 у чётного x, 1/4 у нечётного.
    /// </summary>
    public static (int I0, int I1, float W1) Taps(int x, int halfSize)
    {
        int i0 = (x - 1) >> 1;
        return (Math.Clamp(i0, 0, halfSize - 1), Math.Clamp(i0 + 1, 0, halfSize - 1), (x & 1) == 0 ? 0.75f : 0.25f);
    }

    /// <summary>
    /// Апсемпл цели ½ в полный пиксель: из четырёх билинейных соседей — согласованные по глубине (в пределах
    /// <paramref name="tolerance"/> × глубина пикселя) с перенормированными весами; ни одного — сосед с ближайшей глубиной.
    /// </summary>
    public static Vector4 Upsample(ReadOnlySpan<Vector4> color, ReadOnlySpan<float> halfViewDepth, int halfWidth, int halfHeight, int x, int y,
        float viewDepth, float tolerance)
    {
        var (x0, x1, wx) = Taps(x, halfWidth);
        var (y0, y1, wy) = Taps(y, halfHeight);
        Span<int> texel = [y0 * halfWidth + x0, y0 * halfWidth + x1, y1 * halfWidth + x0, y1 * halfWidth + x1];
        Span<float> weight = [(1 - wx) * (1 - wy), wx * (1 - wy), (1 - wx) * wy, wx * wy];
        Vector4 sum = default, nearest = default;
        float total = 0, best = float.MaxValue;
        for (int n = 0; n < 4; n++)
        {
            var c = color[texel[n]];
            float dz = MathF.Abs(halfViewDepth[texel[n]] - viewDepth);
            if (dz <= tolerance * viewDepth)
            {
                sum += weight[n] * c;
                total += weight[n];
            }
            if (dz < best)
            {
                best = dz;
                nearest = c;
            }
        }
        return total > 0 ? sum / total : nearest;
    }
}

/// <summary>
/// Прозрачное в ½ разрешения, фаза Transparent, раз за кадр, только с HDR: <see cref="Begin"/> — глубина ½ и очищенная цель,
/// проходы вызывающего пишут в цель (<see cref="BlendMode.Premultiplied"/>), <see cref="Composite"/> — апсемпл по глубине
/// поверх <see cref="RenderFrame.SceneColor"/>. Что зарегистрировано после композита — ложится поверх слоя.
/// </summary>
/// <remarks>
/// Глубина ½ — ближайшая: марш не проходит сквозь передний план у края блока. Цена — светлый контур у силуэта, а не ореол.
/// Кадр — 0 Б: делегаты проходов созданы один раз.
/// </remarks>
public sealed class HalfResTransparency
{
    /// <summary>Допуск апсемпла по умолчанию — доля линейной глубины пикселя.</summary>
    public const float DefaultDepthTolerance = 0.05f;

    public const string DepthPass = "transparent.halfres.depth", CompositePass = "transparent.halfres.composite";
    public const string ColorName = "halfres.color", DepthName = "halfres.depth";

    /// <summary>Корневые константы уменьшения глубины: SRV глубины сцены, размер кадра.</summary>
    internal const int SlotSceneDepth = 0, SlotWidth = 1, SlotHeight = 2;

    /// <summary>Корневые константы композита: SRV цели ½, её глубины и глубины сцены, размер ½, k, допуск.</summary>
    internal const int SlotColor = 0, SlotHalfDepth = 1, SlotScene = 2, SlotHalfWidth = 3, SlotHalfHeight = 4, SlotK0 = 5, SlotK1 = 6,
        SlotTolerance = 7;

    private const string Shader = "common/halfres.hlsl";
    private static readonly GraphicsPipelineDesc Downsample = new(Shader, "VsFullscreen", "PsDownsampleDepth", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Upsample = new(Shader, "VsFullscreen", "PsComposite", BlendMode.Premultiplied);

    private readonly Renderer _renderer;
    private readonly Action<RenderPassContext> _recordDepth, _recordComposite;
    private HalfResTarget _target;
    private bool _begun, _composited;
    private int _sceneDepthSrv, _width, _height;
    private float _tolerance;

    internal HalfResTransparency(Renderer renderer)
    {
        _renderer = renderer;
        _recordDepth = RecordDepth;
        _recordComposite = RecordComposite;
    }

    internal static void Contribute(ShaderContract c)
    {
        c.Section("прозрачное в 1/2 (Sword.Engine.Render.HalfResTransparency)");
        c.AddSlot("HALFRES_SCENE_DEPTH", SlotSceneDepth, "SRV глубины сцены (уменьшение)");
        c.AddSlot("HALFRES_WIDTH", SlotWidth, "ширина кадра");
        c.AddSlot("HALFRES_HEIGHT", SlotHeight, "высота кадра");
        c.AddSlot("HALFRES_COLOR", SlotColor, "SRV цели 1/2 (композит)");
        c.AddSlot("HALFRES_HALF_DEPTH", SlotHalfDepth, "SRV глубины 1/2");
        c.AddSlot("HALFRES_SCENE", SlotScene, "SRV глубины сцены");
        c.AddSlot("HALFRES_HALF_WIDTH", SlotHalfWidth);
        c.AddSlot("HALFRES_HALF_HEIGHT", SlotHalfHeight);
        c.AddSlot("HALFRES_K0", SlotK0, "k.x линейной глубины, float");
        c.AddSlot("HALFRES_K1", SlotK1, "k.y линейной глубины, float");
        c.AddSlot("HALFRES_TOLERANCE", SlotTolerance, "допуск глубины, доля, float");
    }

    /// <summary>Допуск апсемпла — доля линейной глубины пикселя; бесконечность — чистый билинейный.</summary>
    public float DepthTolerance { get; set; } = DefaultDepthTolerance;

    /// <summary>Цель ½ этого кадра; null — <see cref="Begin"/> в кадре не звали.</summary>
    public HalfResTarget? Current => _begun ? _target : null;

    /// <summary>
    /// Начать слой ½: проход уменьшения глубины и цель ½. Глубину сцены должен записать непрозрачный проход.
    /// </summary>
    /// <exception cref="InvalidOperationException">HDR в кадре выключен или слой ½ уже начат.</exception>
    public HalfResTarget Begin(Vector2 viewDepth)
    {
        if (!_renderer.FrameHdr) throw new InvalidOperationException("прозрачное в 1/2 — только с HDR (Renderer.Hdr): композит пишет RenderFrame.SceneColor");
        if (_begun) throw new InvalidOperationException("слой 1/2 прозрачного в этом кадре уже начат (Begin — раз за кадр)");
        var graph = _renderer.Graph;
        var scene = _renderer.Scene;
        _width = graph.Width;
        _height = graph.Height;
        int w = TransparencyMath.HalfSize(_width), h = TransparencyMath.HalfSize(_height);
        var color = graph.CreateTexture(ColorName, new TextureDesc(TextureFormat.Rgba16Float, TextureUsage.ColorTarget, w, h));
        var depth = graph.CreateTexture(DepthName, new TextureDesc(TextureFormat.Depth32Float, TextureUsage.DepthTarget, w, h));
        _sceneDepthSrv = scene.DepthSrv;
        graph.AddPass(DepthPass, FramePhase.Transparent).Depth(depth, DepthAccess.Write, 0).Color(color, Vector4.Zero).Read(scene.Depth)
            .Execute(_recordDepth);
        _target = new HalfResTarget(color, depth, graph.SrvOf(color), graph.SrvOf(depth), w, h, viewDepth);
        _begun = true;
        return _target;
    }

    /// <summary>Смешать слой ½ со сценой — проход композита в порядке регистрации фазы Transparent.</summary>
    /// <exception cref="InvalidOperationException">Слой не начат или уже смешан.</exception>
    public void Composite()
    {
        if (!_begun) throw new InvalidOperationException("композит 1/2 без Begin в этом кадре");
        if (_composited) throw new InvalidOperationException("слой 1/2 прозрачного в этом кадре уже смешан");
        _tolerance = DepthTolerance;
        var graph = _renderer.Graph;
        var scene = _renderer.Scene.Depth;
        graph.AddPass(CompositePass, FramePhase.Transparent).Color(_renderer.HdrScene.Color).Depth(scene, DepthAccess.Read).Read(scene)
            .Read(_target.Color).Read(_target.Depth).Execute(_recordComposite);
        _composited = true;
    }

    internal void BeginFrame() => _begun = _composited = false;

    /// <summary>После построения кадра: начатый и не смешанный слой — ошибка кадра (его содержимое пропало бы молча).</summary>
    internal void EndFrame()
    {
        if (_begun && !_composited)
            throw new InvalidOperationException("слой 1/2 прозрачного начат (HalfRes.Begin), а композита нет (HalfRes.Composite)");
    }

    private void RecordDepth(RenderPassContext ctx)
    {
        ctx.SetPipeline(Downsample);
        ctx.SetConstant(SlotSceneDepth, _sceneDepthSrv);
        ctx.SetConstant(SlotWidth, _width);
        ctx.SetConstant(SlotHeight, _height);
        ctx.Draw(3);
    }

    private void RecordComposite(RenderPassContext ctx)
    {
        ctx.SetPipeline(Upsample);
        ctx.SetConstant(SlotColor, _target.ColorSrv);
        ctx.SetConstant(SlotHalfDepth, _target.DepthSrv);
        ctx.SetConstant(SlotScene, _sceneDepthSrv);
        ctx.SetConstant(SlotHalfWidth, _target.Width);
        ctx.SetConstant(SlotHalfHeight, _target.Height);
        ctx.SetConstant(SlotK0, _target.ViewDepth.X);
        ctx.SetConstant(SlotK1, _target.ViewDepth.Y);
        ctx.SetConstant(SlotTolerance, _tolerance);
        ctx.Draw(3);
    }
}
