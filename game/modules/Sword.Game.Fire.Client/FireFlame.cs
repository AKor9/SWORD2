using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Fire;

/// <summary>
/// Числа пламени — материал <c>MI_Torch4</c> LIVE-2 (FSR §3.7): рампа Color2 → Color1 (HDR) по k = FlowDensityMult·n^FlowDensityPow·ρ·
/// ColorMult·ρ^ColorPow, двухфазная flow-map шума по скорости сима, синусная маска краёв, мягкий край по глубине.
/// </summary>
public sealed record FlameParams
{
    /// <summary>Цвет при k = 1 и «нулевой» цвет рампы (линейные, HDR; без насыщения).</summary>
    public Vector3 Colour1 { get; init; } = new(10f, 0.5f, 0f);
    public Vector3 Colour2 { get; init; } = new(0.0573f, 0.0403f, 0.0380f);

    /// <summary>Сила flow-map (<c>FlowStregth</c>) и зажим скорости поля перед ней (±5).</summary>
    public float FlowStrength { get; init; } = 0.5f;
    public float VelocityClamp { get; init; } = 5f;

    /// <summary>Фаза flow-map — frac(FlowSpeed·t), t — время кадра, с.</summary>
    public float FlowSpeed { get; init; } = 3f;

    /// <summary>Повтор шума на площадке (<c>FlowMapUVTile</c>): uvF = Tile·(uv + WorldOffset).</summary>
    public float FlowTile { get; init; } = 4f;

    public float FlowDensityMult { get; init; } = 10f;
    public float FlowDensityPow { get; init; } = 3f;
    public float ColorMult { get; init; } = 2f;
    public float ColorPow { get; init; } = 5f;

    /// <summary>Мягкий край у непрозрачной сцены (<c>DepthFadeDistance</c> 10 см), м.</summary>
    public float DepthFadeDistance { get; init; } = 0.1f;

    /// <summary>Синусная маска краёв площадки sin(πu)·sin(πv) (<c>EdgeMask</c> 1).</summary>
    public bool EdgeMask { get; init; } = true;

    /// <summary>
    /// Срез хвоста: альфа × saturate(k/это) — слабый хвост поля (k ≈ 0, вуаль цвета Color2) гаснет, светится только пламя; дым — 3D-газ.
    /// 0 — выкл. (как <c>MI_Torch4</c>: альфа = ρ во всём поле).
    /// </summary>
    public float GlowCut { get; init; } = 0.1f;

    /// <summary>Факел LIVE-2 (<c>MI_Torch4</c>).</summary>
    public static FlameParams Torch4 { get; } = new();

    /// <summary>Множитель k при n = ρ = 1 и степень ρ в k.</summary>
    public float DensityGain => FlowDensityMult * ColorMult;
    public float DensityPower => 1f + ColorPow;
}

/// <summary>
/// CPU-эталон пламени (<c>fire/fire_flame.hlsl</c>): те же шаги и числа, что шейдер, — фаза, координаты шума двух фаз, эмиссия, альфа,
/// туман; премультиплицированный вклад пикселя.
/// </summary>
public static class FlameMath
{
    /// <summary>Фаза flow-map frac(FlowSpeed·t) — в double: время кадра растёт без потери дробной части.</summary>
    public static float Phase(double seconds, float flowSpeed)
    {
        double x = flowSpeed * seconds;
        return (float)(x - Math.Floor(x));
    }

    /// <summary>
    /// Координаты выборок шума двух фаз и вес второй: A — (uvF + (ph − 0.5)·vel + 1)/2, B — (uvF + (frac(ph + 0.5) − 0.5)·vel + 1)/2,
    /// vel = −clamp(s.rg, ±VelocityClamp)·FlowStrength, uvF = FlowTile·(uv + WorldOffset); n = lerp(A, B, |2ph − 1|).
    /// </summary>
    public static (Vector2 A, Vector2 B, float WeightB) Flow(Vector2 uv, Vector2 worldOffset, Vector2 fieldVelocity, float phase, FlameParams p)
    {
        var vel = -Vector2.Clamp(fieldVelocity, new Vector2(-p.VelocityClamp), new Vector2(p.VelocityClamp)) * p.FlowStrength;
        var uvF = p.FlowTile * (uv + worldOffset);
        float second = phase + 0.5f;
        second -= MathF.Floor(second);
        var a = (uvF + (phase - 0.5f) * vel + Vector2.One) * 0.5f;
        var b = (uvF + (second - 0.5f) * vel + Vector2.One) * 0.5f;
        return (a, b, MathF.Abs(2 * phase - 1));
    }

    /// <summary>Плотность поля в формуле: min(s.b, 1), снизу 0 (степени отрицательного — NaN).</summary>
    public static float Density(float fieldDensity) => Math.Clamp(fieldDensity, 0f, 1f);

    /// <summary>Эмиссия max(Color2 + k·(Color1 − Color2), 0), k = DensityGain·n^FlowDensityPow·ρ^DensityPower.</summary>
    public static Vector3 Emission(float noise, float density, FlameParams p)
    {
        float k = K(noise, density, p);
        return Vector3.Max(p.Colour2 + k * (p.Colour1 - p.Colour2), Vector3.Zero);
    }

    /// <summary>k = DensityGain·n^FlowDensityPow·ρ^DensityPower — доля рампы Color2 → Color1.</summary>
    public static float K(float noise, float density, FlameParams p) =>
        p.DensityGain * MathF.Pow(MathF.Max(noise, 0f), p.FlowDensityPow) * MathF.Pow(density, p.DensityPower);

    /// <summary>Срез хвоста: saturate(k/GlowCut), при GlowCut = 0 — 1.</summary>
    public static float Glow(float k, FlameParams p) => p.GlowCut > 0 ? Math.Clamp(k / p.GlowCut, 0f, 1f) : 1f;

    /// <summary>Альфа saturate(ρ·sin(πu)·sin(πv)·fade): fade — мягкий край по глубине (0…1).</summary>
    public static float Alpha(float density, Vector2 uv, float fade, FlameParams p)
    {
        float edge = p.EdgeMask ? MathF.Sin(MathF.PI * uv.X) * MathF.Sin(MathF.PI * uv.Y) : 1f;
        return Math.Clamp(density * edge * fade, 0f, 1f);
    }

    /// <summary>
    /// Премультиплицированный вклад пикселя (rgb — цвет·α, w — α): поле <paramref name="field"/> (v.xy, ρ, wet), шум двух фаз
    /// <paramref name="noiseA"/>, <paramref name="noiseB"/>, доля тумана <paramref name="fog"/> его цветом <paramref name="fogColour"/>
    /// (emis·(1 − f) + туман·f — как lerp у <c>Shade</c>).
    /// </summary>
    public static Vector4 Pixel(Vector4 field, float noiseA, float noiseB, float phase, Vector2 uv, float fade, float fog, Vector3 fogColour,
        FlameParams p)
    {
        float rho = Density(field.Z);
        float n = noiseA + (noiseB - noiseA) * MathF.Abs(2 * phase - 1);
        var colour = Emission(n, rho, p) * (1 - fog) + fogColour * fog;
        float alpha = Alpha(rho, uv, fade, p) * Glow(K(n, rho, p), p);
        return new Vector4(colour * alpha, alpha);
    }

    /// <summary>
    /// Параметры линейной глубины k (<c>SceneViewDepth</c>: d = k.y / (глубина − k.x)) перспективы <paramref name="viewProj"/> по двум
    /// точкам на оси камеры (<paramref name="eye"/>, <paramref name="forward"/>) — проекции отдельно камера не отдаёт.
    /// </summary>
    public static Vector2 ViewDepthParams(in Matrix4x4 viewProj, Vector3 eye, Vector3 forward)
    {
        const float d1 = 1f, d2 = 100f;
        float z1 = Depth(viewProj, eye + forward * d1), z2 = Depth(viewProj, eye + forward * d2);
        float ky = (z1 - z2) / (1 / d1 - 1 / d2);
        return new Vector2(z1 - ky / d1, ky);
    }

    private static float Depth(in Matrix4x4 viewProj, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        return c.Z / c.W;
    }
}

/// <summary>Площадка пламени в кадре: центр и полуоси в мире (u — вправо, v — вверх; uv.y растёт вниз), накопленный WorldOffset, выход поля.</summary>
internal struct FlameQuad
{
    public Vector3 Center, U, V;
    public Vector2 WorldOffset;
    public RenderTexture Field;
}

/// <summary>Камера и окружение пламени кадра: вид-проекция, глаз, k линейной глубины, туман, фаза flow-map.</summary>
internal readonly record struct FlameView(Matrix4x4 ViewProj, Vector3 Eye, Vector2 ViewDepth, SceneFog Fog, float Phase);

/// <summary>Константы площадки пламени — <c>FlameConstants</c> шейдера (строки по 16 байт; размер сверяет контракт).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct FlameConstants
{
    public Matrix4x4 ViewProj;
    public Vector3 Center; public float Phase;
    public Vector3 U; public float WorldOffsetX;
    public Vector3 V; public float WorldOffsetY;
    public Vector3 Eye; public float FadeDistance;
    public Vector3 FogColour; public float FogStart;
    public Vector3 Colour1; public float FogRange;
    public Vector3 Colour2; public float FlowStrength;
    public float VelocityClamp, FlowTile, DensityGain, NoisePower;
    public float DensityPower, EdgeMask; public Vector2 ViewDepth;
    public uint Field, Noise, NoiseLayer, SceneDepth;
    public float GlowCut, Pad0, Pad1, Pad2;
}

/// <summary>
/// Пламя огня (фаза Transparent, только с HDR): площадка сима — плоскость к камере в показанном центре огня, формула <c>MI_Torch4</c>
/// (<see cref="FlameParams"/>, CPU-эталон — <see cref="FlameMath"/>) по выходу поля кадра и шуму <c>crumple</c>; смешивание
/// «премультиплицированная альфа» поверх HDR-сцены, тест глубины сцены без записи, мягкий край по глубине сцены (SRV). Один проход на все
/// площадки (<see cref="PassName"/>); оверлей — тот же вклад в LDR (цвет x/(1 + x), альфа). Кадр — 0 Б: массивы и делегат — поля.
/// </summary>
internal sealed class FireFlame
{
    public const string ShaderFile = "fire/fire_flame.hlsl", PassName = "fire.flame";

    private const int SlotData = 0, SlotIndex = 1;

    private static readonly GraphicsPipelineDesc Pipeline = new(ShaderFile, "VsFlame", "PsFlame", BlendMode.Premultiplied, DepthMode.Test);
    private static readonly GraphicsPipelineDesc LayerPipeline = Pipeline with { PsEntry = "PsFlameLayer" };

    private readonly FrameData<FlameConstants> _constants;
    private readonly int _noise, _noiseLayer;
    private readonly Action<RenderPassContext> _record, _recordLayer;
    private readonly RenderTexture[] _fields;
    private int _count, _data;
    private ulong _frame = ulong.MaxValue;

    /// <param name="noiseSrv">SRV шума flow-map (<c>Texture2DArray</c>, канал r, линейный — sRGB декодирует выборка) и его слой.</param>
    public FireFlame(Renderer renderer, int noiseSrv, int noiseLayer, int capacity)
    {
        _constants = renderer.CreateFrameData<FlameConstants>("fire.flame.constants", (uint)Math.Max(capacity, 1));
        _fields = new RenderTexture[Math.Max(capacity, 1)];
        _noise = noiseSrv;
        _noiseLayer = noiseLayer;
        _record = ctx => Record(ctx, Pipeline);
        _recordLayer = ctx => Record(ctx, LayerPipeline);
    }

    public FlameParams Params { get; set; } = FlameParams.Torch4;

    /// <summary>Площадок в последнем кадре с пламенем.</summary>
    public int LastQuads { get; private set; }

    /// <summary>Площадок не больше.</summary>
    public int Capacity => (int)_constants.Capacity;

    /// <summary>Слоты и размер констант в контракт шейдеров.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fire: flame (Sword.Game.Fire.Client)")
        .AddSlot("gFlameData", SlotData, "SRV констант площадок кадра")
        .AddSlot("gFlameIndex", SlotIndex, "номер площадки")
        .AddSize<FlameConstants>("FIRE_FLAME_CONSTANTS_SIZE");

    /// <summary>Освободить кольцо констант (вместе с модулем).</summary>
    public IDisposable Resources => _constants;

    /// <summary>Проход пламени в HDR-сцену кадра (<see cref="PassName"/>, фаза Transparent). Площадок нет — ничего.</summary>
    public void Add(RenderFrame frame, in FlameView view, ReadOnlySpan<FlameQuad> quads)
    {
        LastQuads = 0;
        _frame = frame.Number;
        int count = Math.Min(quads.Length, Capacity);
        if (count == 0) return;
        var g = frame.Graph;
        var p = Params;
        var slot = _constants.Slot;
        for (int i = 0; i < count; i++)
        {
            ref readonly var q = ref quads[i];
            _fields[i] = q.Field;
            slot[i] = new FlameConstants
            {
                ViewProj = view.ViewProj,
                Center = q.Center, Phase = view.Phase,
                U = q.U, WorldOffsetX = q.WorldOffset.X,
                V = q.V, WorldOffsetY = q.WorldOffset.Y,
                Eye = view.Eye, FadeDistance = p.DepthFadeDistance,
                FogColour = view.Fog.Colour, FogStart = view.Fog.Start,
                Colour1 = p.Colour1, FogRange = view.Fog.Range,
                Colour2 = p.Colour2, FlowStrength = p.FlowStrength,
                VelocityClamp = p.VelocityClamp, FlowTile = p.FlowTile, DensityGain = p.DensityGain, NoisePower = p.FlowDensityPow,
                DensityPower = p.DensityPower, EdgeMask = p.EdgeMask ? 1 : 0, ViewDepth = view.ViewDepth,
                Field = (uint)g.SrvOf(q.Field), Noise = (uint)_noise, NoiseLayer = (uint)_noiseLayer, SceneDepth = (uint)frame.Scene.DepthSrv,
                GlowCut = p.GlowCut,
            };
        }
        _count = count;
        _data = _constants.Srv;
        Pass(frame, PassName, FramePhase.Transparent, frame.SceneColor, null).Execute(_record);
        LastQuads = count;
    }

    /// <summary>
    /// Тот же вклад площадок этого кадра (после <see cref="Add"/>) в LDR-цель <paramref name="target"/>: цвет x/(1 + x), альфа —
    /// экран поверх кадра (фаза Debug). Пламени в кадре нет — ничего.
    /// </summary>
    public void AddLayer(RenderFrame frame, RenderTexture target, FramePhase phase, string name, Vector4? clear)
    {
        if (_frame != frame.Number || LastQuads == 0) return;
        Pass(frame, name, phase, target, clear).Execute(_recordLayer);
    }

    private RenderGraph.RenderPassBuilder Pass(RenderFrame frame, string name, FramePhase phase, RenderTexture target, Vector4? clear)
    {
        var depth = frame.Scene.Depth;
        var pass = frame.Graph.AddPass(name, phase).Color(target, clear).Depth(depth, DepthAccess.Read).Read(depth);
        for (int i = 0; i < _count; i++) pass.Read(_fields[i]);
        return pass;
    }

    private void Record(RenderPassContext ctx, in GraphicsPipelineDesc pipeline)
    {
        ctx.SetPipeline(pipeline);
        ctx.SetConstant(SlotData, _data);
        for (int i = 0; i < _count; i++)
        {
            ctx.SetConstant(SlotIndex, i);
            ctx.Draw(6);
        }
    }
}
