using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Fire;

/// <summary>
/// Числа дыма — материал <c>MI_HVOL_SIM_TorchSmoke</c> LIVE-2 в объёме Heterogeneous Volumes (FSR §3.8): объём 3×3×2 м вокруг площадки
/// (z — вдоль взгляда, к зрителю), плотность выхода сима выдавлена в слой ±(0.1·ρ + смягчение), экстинкция 0.1·ρ^0.35·slab³ с маской краёв,
/// серое альбедо, «фальшивый ambient» неба как эмиссия, однократное рассеяние света огня (×0.15, фаза 1/4π, без light function) и солнца.
/// Единицы экстинкции LIVE-2 не установлены — множитель <see cref="Density"/> (ручка <c>fire.smokeDensity</c>).
/// </summary>
public sealed record SmokeParams
{
    /// <summary>Объём, м: x, y — площадка сима, z — толщина вдоль взгляда (<c>HVolumeExtentsZ</c> 2 м); пивот — центр.</summary>
    public Vector3 Extents { get; init; } = new(3f, 3f, 2f);

    /// <summary>Середина слоя по uvw.z (<c>BasePlaneHeight</c>).</summary>
    public float BasePlaneHeight { get; init; } = 0.5f;

    /// <summary>Выдавливание слоя: полная плотность до |uvw.z − середина| ≤ Extrude·ρ − Bias, спад на Soften (<c>Extrude</c>, <c>ExtrudeSoften</c>).</summary>
    public float Extrude { get; init; } = 0.1f;
    public float ExtrudeSoften { get; init; } = 0.1f;

    /// <summary>Постоянная 0.05 формулы слоя (CS <c>SM238_0000</c>).</summary>
    public float ExtrudeBias { get; init; } = 0.05f;

    /// <summary>Степень спада слоя (<c>ExtrudeSoftenPower</c>).</summary>
    public float ExtrudeSoftenPower { get; init; } = 3f;

    /// <summary>Маска краёв площадки: saturate((0.5 − |uv − 0.5|)·6) (<c>EdgeMaskMult</c>).</summary>
    public float EdgeMask { get; init; } = 6f;

    /// <summary>σ = DensityMult·ρ^DensityPow (<c>DensityMult</c>, <c>DensityPow</c>).</summary>
    public float DensityMult { get; init; } = 0.1f;
    public float DensityPow { get; init; } = 0.35f;

    /// <summary>Альбедо рассеяния (серое).</summary>
    public float Albedo { get; init; } = 0.3646f;

    /// <summary>Градиент эмиссии по uvw.z (<c>VerticalGradient</c>, <c>VerticalGradientSoften</c>): g = saturate(1 − VG·(верх − z)/ширина).</summary>
    public float VerticalGradient { get; init; } = 0.6f;
    public float VerticalGradientSoften { get; init; } = 0f;

    /// <summary>Фальшивый ambient: E = Mult·σ^Pow·(1 − σ^AoPow)·g·lerp(avg(небо), небо, SkyColour).</summary>
    public float AmbientMult { get; init; } = 0.3f;
    public float AmbientPow { get; init; } = 0.75f;
    public float AmbientAoPow { get; init; } = 1.4f;
    public float SkyColour { get; init; } = 0.5f;

    /// <summary>Шагов на луч не больше (<c>r.HeterogeneousVolumes.MaxStepCount</c>); шаг — диагональ объёма / это (2.4 см).</summary>
    public int MaxSteps { get; init; } = 196;

    /// <summary>
    /// Множитель экстинкции и эмиссии — 1/Scale инстанса HV: локальных единиц (см при Scale 1) на сантиметр пути. В LIVE-2 не установлен
    /// (FSR §3.8 «Единицы экстинкции»); 1 — σ «на см»: плотное ядро у кисти (ρ ≈ 1) — τ ≈ 3 по толщине слоя, дым выше (ρ ≲ 0.1) — τ ≲ 0.06.
    /// Умолчание 1.5 подобрано по кадрам LIVE-2: столб видимого дыма ≈1.9 высоты головы над пламенем стоя, в столбе τ ≲ 0.25.
    /// </summary>
    public float Density { get; init; } = 1.5f;

    /// <summary>Факел LIVE-2.</summary>
    public static SmokeParams Torch { get; } = new();

    /// <summary>Шаг марша, м: диагональ объёма / <see cref="MaxSteps"/>.</summary>
    public float StepLength => Extents.Length() / MaxSteps;

    /// <summary>Полутолщина слоя, м: дальше от середины σ = 0 при ρ ≤ 1 — марш идёт только по нему.</summary>
    public float SlabHalfDepth => (Extrude + ExtrudeSoften - ExtrudeBias) * Extents.Z;

    /// <summary>Единиц σ и эмиссии на метр пути: 100·<see cref="Density"/>.</summary>
    public float UnitsPerMetre => 100f * Density;

    /// <summary>Верх и ширина градиента g: середина + ширина/2 и Extrude + ExtrudeSoften + VerticalGradientSoften.</summary>
    public float GradientSpan => Extrude + ExtrudeSoften + VerticalGradientSoften;
    public float GradientTop => BasePlaneHeight + GradientSpan * 0.5f;
}

/// <summary>Источник рассеяния в дыме: позиция, излучение (цвет × интенсивность × угасание × рассеяние в объёмах), 1/R², показатель «экспоненты».</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SmokeLight
{
    public Vector3 Position; public float InvRadiusSq;
    public Vector3 Radiance; public float Exponent;

    /// <summary>Точечный источник в дыме: без light function (в HV её нет) и без дальности показа; множитель — <see cref="PointLight.VolumetricScattering"/>.</summary>
    public static SmokeLight From(in PointLight light) => new()
    {
        Position = light.Position,
        InvRadiusSq = light.Radius > 0 ? 1 / (light.Radius * light.Radius) : 0,
        Radiance = light.Color * (light.Intensity * light.VolumetricScattering),
        Exponent = light.Exponent,
    };
}

/// <summary>Объём дыма кадра: центр, оси (единичные: вправо, вверх, к зрителю), выход поля.</summary>
internal struct SmokeVolume
{
    public Vector3 Center, AxisU, AxisV, AxisW;
    public RenderTexture Field;
}

/// <summary>Камера и окружение дыма кадра: вид-проекция, глаз, ось взгляда, туман, солнце и небо (k линейной глубины — у цели ½).</summary>
internal readonly record struct SmokeView(Matrix4x4 ViewProj, Vector3 Eye, Vector3 Forward, SceneFog Fog, SceneSky Sky);

/// <summary>Результат марша одного луча: излучение (премультиплицированное), пропускание, шагов.</summary>
public readonly record struct SmokeSample(Vector3 Light, float Transmittance, int Steps)
{
    /// <summary>Непрозрачность 1 − T.</summary>
    public float Alpha => 1 - Transmittance;
}

/// <summary>
/// CPU-эталон дыма (<c>fire/fire_smoke.hlsl</c>): те же шаги и числа, что шейдер, — отрезок луча в слое, σ, эмиссия, рассеяние, интеграл HV
/// (L += T·(E + S)·Δ, T ·= exp(−σΔ)) по серединам равных шагов.
/// </summary>
public static class SmokeMath
{
    /// <summary>Фаза изотропного рассеяния 1/4π.</summary>
    public const float Phase = 1f / (4f * MathF.PI);

    /// <summary>σ материала в точке uvw объёма при плотности поля <paramref name="rho"/>: маска краёв · DensityMult·ρ^DensityPow · slab^Power.</summary>
    public static float Sigma(float rho, Vector3 uvw, SmokeParams p)
    {
        rho = Math.Clamp(rho, 0f, 1f);
        float slab = 1 - Math.Clamp((MathF.Abs(uvw.Z - p.BasePlaneHeight) - p.Extrude * rho + p.ExtrudeBias) / p.ExtrudeSoften, 0f, 1f);
        return Edge(new Vector2(uvw.X, uvw.Y), p) * p.DensityMult * MathF.Pow(rho, p.DensityPow) * MathF.Pow(slab, p.ExtrudeSoftenPower);
    }

    /// <summary>Маска краёв площадки: min по осям saturate((0.5 − |uv − 0.5|)·EdgeMask).</summary>
    public static float Edge(Vector2 uv, SmokeParams p) =>
        MathF.Min(Math.Clamp((0.5f - MathF.Abs(uv.X - 0.5f)) * p.EdgeMask, 0f, 1f), Math.Clamp((0.5f - MathF.Abs(uv.Y - 0.5f)) * p.EdgeMask, 0f, 1f));

    /// <summary>Небо фальшивого ambient: lerp(среднее каналов, небо, <see cref="SmokeParams.SkyColour"/>).</summary>
    public static Vector3 Ambient(Vector3 sky, SmokeParams p)
    {
        float avg = (sky.X + sky.Y + sky.Z) / 3;
        return new Vector3(avg) + (sky - new Vector3(avg)) * p.SkyColour;
    }

    /// <summary>Солнце как источник рассеяния: освещённость UE-света = π × множитель сцены, рассеяние в объёмах 1.</summary>
    public static Vector3 SunRadiance(Vector3 sun) => sun * MathF.PI;

    /// <summary>Эмиссия материала: Mult·σ^Pow·(1 − σ^AoPow)·g·ambient, g — градиент по uvw.z.</summary>
    public static Vector3 Emission(float sigma, float w, Vector3 ambient, SmokeParams p)
    {
        float g = Math.Clamp(1 - p.VerticalGradient * (p.GradientTop - w) / p.GradientSpan, 0f, 1f);
        return ambient * (p.AmbientMult * MathF.Pow(sigma, p.AmbientPow) * (1 - MathF.Pow(sigma, p.AmbientAoPow)) * g);
    }

    /// <summary>Излучение источников в точке <paramref name="world"/>: солнце + Σ излучение·затухание «экспоненты».</summary>
    public static Vector3 Incident(Vector3 world, Vector3 sun, ReadOnlySpan<SmokeLight> lights)
    {
        var sum = sun;
        foreach (ref readonly var l in lights)
        {
            float distSq = Vector3.DistanceSquared(l.Position, world);
            float b = 1 - Math.Clamp(distSq * l.InvRadiusSq, 0f, 1f);
            sum += l.Radiance * (b > 0 ? MathF.Pow(b, l.Exponent) : 0);
        }
        return sum;
    }

    /// <summary>
    /// Отрезок луча <paramref name="eye"/> + t·<paramref name="dir"/> в слое объёма (|x| ≤ E.x/2, |y| ≤ E.y/2, |z| ≤ полутолщина слоя в осях
    /// объёма), обрезанный камерой (t ≥ 0) и сценой (t ≤ <paramref name="sceneT"/>); шагов — ⌈длина/шаг⌉ не больше MaxSteps, шаги равные.
    /// </summary>
    public static (float T0, float T1, int Steps) Segment(Vector3 eye, Vector3 dir, float sceneT, Vector3 center, Vector3 u, Vector3 v, Vector3 w,
        SmokeParams p)
    {
        var rel = eye - center;
        var o = new Vector3(Vector3.Dot(rel, u), Vector3.Dot(rel, v), Vector3.Dot(rel, w));
        var d = new Vector3(Vector3.Dot(dir, u), Vector3.Dot(dir, v), Vector3.Dot(dir, w));
        var h = new Vector3(p.Extents.X * 0.5f, p.Extents.Y * 0.5f, p.SlabHalfDepth);
        var inv = new Vector3(1 / Safe(d.X), 1 / Safe(d.Y), 1 / Safe(d.Z));
        var ta = (-h - o) * inv;
        var tb = (h - o) * inv;
        var tmin = Vector3.Min(ta, tb);
        var tmax = Vector3.Max(ta, tb);
        float t0 = MathF.Max(MathF.Max(tmin.X, tmin.Y), MathF.Max(tmin.Z, 0f));
        float t1 = MathF.Min(MathF.Min(tmax.X, tmax.Y), MathF.Min(tmax.Z, sceneT));
        if (!(t1 > t0)) return (t0, t1, 0);
        int n = (int)MathF.Min(MathF.Ceiling((t1 - t0) / p.StepLength), p.MaxSteps);
        return (t0, t1, Math.Max(n, 1));
    }

    /// <summary>Компонента направления без нуля: деление на ноль дало бы NaN у луча вдоль грани.</summary>
    public static float Safe(float x) => MathF.Abs(x) < 1e-8f ? (x < 0 ? -1e-8f : 1e-8f) : x;

    /// <summary>
    /// Марш одного луча (как <c>MarchSmoke</c> шейдера): середины равных шагов отрезка <see cref="Segment"/>, uvw точки, ρ поля
    /// <paramref name="rho"/>(uv), σ, эмиссия и рассеяние; L += T·(E + S)·k, T ·= exp(−σ·k), k = шаг·<see cref="SmokeParams.UnitsPerMetre"/>.
    /// </summary>
    public static SmokeSample March(Vector3 eye, Vector3 dir, float sceneT, Vector3 center, Vector3 u, Vector3 v, Vector3 w, Func<Vector2, float> rho,
        Vector3 sunRadiance, Vector3 ambient, ReadOnlySpan<SmokeLight> lights, SmokeParams p)
    {
        var (t0, t1, n) = Segment(eye, dir, sceneT, center, u, v, w, p);
        if (n == 0) return new SmokeSample(Vector3.Zero, 1, 0);
        var rel = eye - center;
        var o = new Vector3(Vector3.Dot(rel, u), Vector3.Dot(rel, v), Vector3.Dot(rel, w));
        var d = new Vector3(Vector3.Dot(dir, u), Vector3.Dot(dir, v), Vector3.Dot(dir, w));
        float dt = (t1 - t0) / n;
        float k = p.UnitsPerMetre * dt;
        Vector3 light = Vector3.Zero;
        float transmittance = 1;
        for (int i = 0; i < n; i++)
        {
            float t = t0 + (i + 0.5f) * dt;
            var lp = o + d * t;
            var uvw = new Vector3(lp.X / p.Extents.X + 0.5f, 0.5f - lp.Y / p.Extents.Y, lp.Z / p.Extents.Z + 0.5f);
            float sigma = Sigma(rho(new Vector2(uvw.X, uvw.Y)), uvw, p);
            if (!(sigma > 0)) continue;
            var emission = Emission(sigma, uvw.Z, ambient, p);
            var scatter = Incident(eye + dir * t, sunRadiance, lights) * (p.Albedo * sigma * Phase);
            light += transmittance * (emission + scatter) * k;
            transmittance *= MathF.Exp(-sigma * k);
        }
        return new SmokeSample(light, transmittance, n);
    }

    /// <summary>Туман сцены поверх дыма (как у пламени — lerp к цвету тумана): премультиплицированный вклад (L·(1 − f) + туман·f·α, α).</summary>
    public static Vector4 Composite(SmokeSample s, float fog, Vector3 fogColour) =>
        new(s.Light * (1 - fog) + fogColour * (fog * s.Alpha), s.Alpha);
}

/// <summary>Константы объёма дыма — <c>SmokeConstants</c> шейдера (строки по 16 байт; размер сверяет контракт).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SmokeConstants
{
    public Matrix4x4 InvViewProj;
    public Vector3 Eye; public float Step;
    public Vector3 Forward; public uint MaxSteps;
    public Vector3 Center; public float SlabHalf;
    public Vector3 AxisU; public float ExtentX;
    public Vector3 AxisV; public float ExtentY;
    public Vector3 AxisW; public float ExtentZ;
    public Vector3 Sun; public float Albedo;
    public Vector3 Ambient; public float UnitsPerMetre;
    public Vector3 FogColour; public float Fog;
    public float EdgeMask, DensityMult, DensityPow, Extrude;
    public float ExtrudeBias, ExtrudeSoften, SoftenPower, BasePlane;
    public float GradientMult, GradientTop, GradientSpan, AmbientMult;
    public float AmbientPow, AmbientAoPow, InvWidth, InvHeight;
    public Vector2 ViewDepth; public uint Field; public uint SceneDepth;
    public uint Lights, LightCount, Pad0, Pad1;
}

/// <summary>Итог марша по сетке лучей кадра (<see cref="FireSmoke.Stats"/>): лучей, из них с дымом, шагов на такой луч, покрытие.</summary>
public readonly record struct SmokeStats(ulong Frame, int Rays, int Hit, double MeanSteps, int MaxSteps, double Coverage, double MeanAlpha, float MaxAlpha);

/// <summary>
/// Дым огня (фаза Transparent, только с HDR): объём на экземпляр в показанном центре огня — рэймарч по формуле HV LIVE-2
/// (<see cref="SmokeParams"/>, CPU-эталон — <see cref="SmokeMath"/>) по выходу поля кадра в цель ½ прозрачного (<see cref="HalfResTarget"/>,
/// премультиплицированно, дальние объёмы раньше); марш обрезает глубина ½ (ближайшая в блоке). Проход — полноэкранный треугольник на
/// объём: луч — из обратной вид-проекции по пикселю, отрезок — в слое объёма (вне слоя σ = 0). Без джиттера (TAA нет): середины равных
/// шагов. Агенту — проход <see cref="StatsPassName"/>: тот же марш по сетке 64×36 лучей кадра, асинхронное чтение → <see cref="Stats"/>;
/// слой снимка — вклад ½ в Rgba8 (цвет x/(1 + x), альфа). Кадр — 0 Б: массивы и делегаты — поля.
/// </summary>
internal sealed class FireSmoke : IDisposable
{
    public const string ShaderFile = "fire/fire_smoke.hlsl", PassName = "fire.smoke", StatsPassName = "fire.smoke.stats",
        LayerPassName = "overlay.fire.smoke";

    /// <summary>Сетка лучей статистики.</summary>
    public const int GridX = 64, GridY = 36, StatsGroup = 8;

    private const int SlotData = 0, SlotIndex = 1, SlotCount = 2, SlotOut = 3, SlotColor = 4;

    private static readonly GraphicsPipelineDesc Pipeline = new(ShaderFile, "VsSmoke", "PsSmoke", BlendMode.Premultiplied);
    private static readonly GraphicsPipelineDesc LayerPipeline = new(ShaderFile, "VsSmoke", "PsSmokeLayer", BlendMode.Premultiplied);
    private static readonly ComputePipelineDesc StatsPipeline = new(ShaderFile, "CsSmokeStats");

    private readonly FrameData<SmokeConstants> _constants;
    private readonly FrameData<SmokeLight> _lights;
    private readonly GpuStorage _stats;
    private readonly GpuReadback _readback;
    private readonly Action<RenderPassContext> _record, _recordStats, _recordLayer;
    private readonly RenderTexture[] _fields;
    private readonly int[] _order;
    private readonly float[] _distance;
    private readonly ulong[] _statsFrames = new ulong[8];
    private int _count, _data, _statsUav, _layerColor;
    private ulong _frame = ulong.MaxValue;

    public FireSmoke(Renderer renderer, int capacity, int maxLights)
    {
        capacity = Math.Max(capacity, 1);
        _constants = renderer.CreateFrameData<SmokeConstants>("fire.smoke.constants", (uint)capacity);
        _lights = renderer.CreateFrameData<SmokeLight>("fire.smoke.lights", (uint)Math.Max(maxLights, 1));
        _stats = renderer.CreateStorage<uint>("fire.smoke.stats", GridX * GridY * 2);
        _readback = renderer.CreateReadback(_stats, GridX * GridY * 8, OnStats);
        _fields = new RenderTexture[capacity];
        _order = new int[capacity];
        _distance = new float[capacity];
        _record = Record;
        _recordStats = RecordStats;
        _recordLayer = RecordLayer;
    }

    public SmokeParams Params { get; set; } = SmokeParams.Torch;

    /// <summary>Объёмов в последнем кадре с дымом; источников рассеяния в нём (без солнца).</summary>
    public int LastVolumes { get; private set; }
    public int LastLights { get; private set; }

    /// <summary>Объёмов не больше.</summary>
    public int Capacity => (int)_constants.Capacity;

    /// <summary>Последняя статистика марша по сетке (приходит через <see cref="GpuReadback.Latency"/> кадров); null — ещё не было.</summary>
    public SmokeStats? Stats { get; private set; }

    /// <summary>Солнце (как источник) и небо (ambient) последнего кадра — для итога.</summary>
    public Vector3 LastSun { get; private set; }
    public Vector3 LastAmbient { get; private set; }

    /// <summary>Слоты и размеры структур в контракт шейдеров.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fire: smoke (Sword.Game.Fire.Client)")
        .AddSlot("gSmokeData", SlotData, "SRV констант объёмов кадра")
        .AddSlot("gSmokeIndex", SlotIndex, "номер объёма")
        .AddSlot("gSmokeCount", SlotCount, "объёмов (статистика)")
        .AddSlot("gSmokeOut", SlotOut, "UAV статистики")
        .AddSlot("gSmokeColor", SlotColor, "SRV цели 1/2 (слой)")
        .AddSize<SmokeConstants>("FIRE_SMOKE_CONSTANTS_SIZE")
        .AddSize<SmokeLight>("FIRE_SMOKE_LIGHT_SIZE")
        .Add("FIRE_SMOKE_GRID_X", GridX)
        .Add("FIRE_SMOKE_GRID_Y", GridY)
        .Add("FIRE_SMOKE_GROUP", StatsGroup);

    /// <summary>
    /// Проходы дыма кадра в цель ½ <paramref name="half"/>: объёмы <paramref name="volumes"/> от дальнего к ближнему, источники рассеяния
    /// <paramref name="lights"/>; затем проход статистики. Объёмов нет — ничего.
    /// </summary>
    public void Add(RenderFrame frame, in HalfResTarget half, in SmokeView view, ReadOnlySpan<SmokeVolume> volumes, ReadOnlySpan<SmokeLight> lights)
    {
        LastVolumes = 0;
        _frame = frame.Number;
        int count = Math.Min(volumes.Length, Capacity);
        if (count == 0) return;
        var g = frame.Graph;
        var p = Params;
        Matrix4x4.Invert(view.ViewProj, out var inv);
        int lightCount = Math.Min(lights.Length, (int)_lights.Capacity);
        var lightSlot = _lights.Slot;
        for (int i = 0; i < lightCount; i++) lightSlot[i] = lights[i];
        LastLights = lightCount;
        var sun = SmokeMath.SunRadiance(view.Sky.Sun);
        var ambient = SmokeMath.Ambient(view.Sky.Sky, p);
        LastSun = sun;
        LastAmbient = ambient;

        // Дальние раньше: слой ½ смешивается «поверх» в порядке рисования.
        for (int i = 0; i < count; i++)
        {
            _order[i] = i;
            _distance[i] = Vector3.DistanceSquared(volumes[i].Center, view.Eye);
        }
        for (int i = 1; i < count; i++)
            for (int j = i; j > 0 && _distance[_order[j]] > _distance[_order[j - 1]]; j--)
                (_order[j], _order[j - 1]) = (_order[j - 1], _order[j]);

        var slot = _constants.Slot;
        for (int i = 0; i < count; i++)
        {
            ref readonly var v = ref volumes[_order[i]];
            _fields[i] = v.Field;
            slot[i] = new SmokeConstants
            {
                InvViewProj = inv,
                Eye = view.Eye, Step = p.StepLength,
                Forward = view.Forward, MaxSteps = (uint)p.MaxSteps,
                Center = v.Center, SlabHalf = p.SlabHalfDepth,
                AxisU = v.AxisU, ExtentX = p.Extents.X,
                AxisV = v.AxisV, ExtentY = p.Extents.Y,
                AxisW = v.AxisW, ExtentZ = p.Extents.Z,
                Sun = sun, Albedo = p.Albedo,
                Ambient = ambient, UnitsPerMetre = p.UnitsPerMetre,
                FogColour = view.Fog.Colour, Fog = view.Fog.Factor(Vector3.Distance(v.Center, view.Eye)),
                EdgeMask = p.EdgeMask, DensityMult = p.DensityMult, DensityPow = p.DensityPow, Extrude = p.Extrude,
                ExtrudeBias = p.ExtrudeBias, ExtrudeSoften = p.ExtrudeSoften, SoftenPower = p.ExtrudeSoftenPower, BasePlane = p.BasePlaneHeight,
                GradientMult = p.VerticalGradient, GradientTop = p.GradientTop, GradientSpan = p.GradientSpan, AmbientMult = p.AmbientMult,
                AmbientPow = p.AmbientPow, AmbientAoPow = p.AmbientAoPow, InvWidth = 1f / frame.Width, InvHeight = 1f / frame.Height,
                ViewDepth = half.ViewDepth, Field = (uint)g.SrvOf(v.Field), SceneDepth = (uint)half.DepthSrv,
                Lights = (uint)_lights.Srv, LightCount = (uint)lightCount,
            };
        }
        _count = count;
        _data = _constants.Srv;

        var pass = g.AddPass(PassName, FramePhase.Transparent).Color(half.Color).Read(half.Depth);
        for (int i = 0; i < count; i++) pass.Read(_fields[i]);
        pass.Execute(_record);

        var stats = g.Import(_stats);
        var statsPass = g.AddComputePass(StatsPassName, FramePhase.Transparent).Read(half.Depth).Write(stats);
        for (int i = 0; i < count; i++) statsPass.Read(_fields[i]);
        _statsUav = g.UavOf(stats);
        statsPass.Execute(_recordStats);
        _statsFrames[frame.Number % (ulong)_statsFrames.Length] = frame.Number;
        LastVolumes = count;
    }

    /// <summary>Кадр без дыма (выключен, огня нет): объёмов 0.</summary>
    public void Skip(RenderFrame frame)
    {
        LastVolumes = 0;
        _frame = frame.Number;
    }

    /// <summary>
    /// Вклад слоя ½ этого кадра (после <see cref="Add"/>) в LDR-цель <paramref name="target"/> полного размера: цвет x/(1 + x), альфа —
    /// слой снимка (очищен прозрачным) или экран поверх кадра. Дыма в кадре нет — ничего.
    /// </summary>
    public void AddLayer(RenderFrame frame, in HalfResTarget half, RenderTexture target, FramePhase phase, string name, Vector4? clear)
    {
        if (_frame != frame.Number || LastVolumes == 0) return;
        _layerColor = half.ColorSrv;
        frame.Graph.AddPass(name, phase).Color(target, clear).Read(half.Color).Execute(_recordLayer);
    }

    private void Record(RenderPassContext ctx)
    {
        ctx.SetPipeline(Pipeline);
        ctx.SetConstant(SlotData, _data);
        for (int i = 0; i < _count; i++)
        {
            ctx.SetConstant(SlotIndex, i);
            ctx.Draw(3);
        }
    }

    private void RecordStats(RenderPassContext ctx)
    {
        ctx.SetPipeline(StatsPipeline);
        ctx.SetConstant(SlotData, _data);
        ctx.SetConstant(SlotCount, _count);
        ctx.SetConstant(SlotOut, _statsUav);
        ctx.Dispatch((GridX + StatsGroup - 1) / StatsGroup, (GridY + StatsGroup - 1) / StatsGroup);
    }

    private void RecordLayer(RenderPassContext ctx)
    {
        ctx.SetPipeline(LayerPipeline);
        ctx.SetConstant(SlotColor, _layerColor);
        ctx.Draw(3);
    }

    /// <summary>Данные сетки кадра <paramref name="frame"/>: (шагов, альфа) на луч. Кадры без прохода статистики пропускаются.</summary>
    private void OnStats(ReadOnlySpan<byte> data, ulong frame)
    {
        if (_statsFrames[frame % (ulong)_statsFrames.Length] != frame) return;
        var cells = MemoryMarshal.Cast<byte, uint>(data);
        int rays = cells.Length / 2, hit = 0, maxSteps = 0, covered = 0;
        long steps = 0;
        double alphaSum = 0;
        float maxAlpha = 0;
        for (int i = 0; i < rays; i++)
        {
            int s = (int)cells[2 * i];
            float a = BitConverter.UInt32BitsToSingle(cells[2 * i + 1]);
            if (s > 0)
            {
                hit++;
                steps += s;
                maxSteps = Math.Max(maxSteps, s);
            }
            if (a >= 1f / 255) covered++;
            alphaSum += a;
            maxAlpha = MathF.Max(maxAlpha, a);
        }
        Stats = new SmokeStats(frame, rays, hit, hit > 0 ? (double)steps / hit : 0, maxSteps, (double)covered / rays, alphaSum / rays, maxAlpha);
    }

    public void Dispose()
    {
        _readback.Dispose();
        _stats.Dispose();
        _lights.Dispose();
        _constants.Dispose();
    }
}
