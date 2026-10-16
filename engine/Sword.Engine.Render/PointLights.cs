using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Render;

/// <summary>Затухание точечного источника (флаг UE <c>bUseInverseSquaredFalloff</c>).</summary>
public enum LightFalloff
{
    /// <summary>
    /// UE «экспонента»: (1 − saturate(d²/R²))^<see cref="PointLight.Exponent"/>, без обратного квадрата; интенсивность безразмерная.
    /// </summary>
    Exponent,

    /// <summary>
    /// Обратный квадрат с окном UE: I / (d² + 1 см²) × saturate(1 − (d²/R²)²)²; интенсивность — канделы (UE Candelas), d — метры.
    /// </summary>
    InverseSquared,
}

/// <summary>
/// Точечный источник кадра: позиция (м, мировые оси), линейный цвет, интенсивность, радиус R (за ним вклад 0), затухание.
/// Дальность показа — как UE MaxDrawDistance / MaxDistanceFadeRange.
/// </summary>
/// <param name="Exponent">Показатель режима <see cref="LightFalloff.Exponent"/> (UE FalloffExponent, по умолчанию 8).</param>
public readonly record struct PointLight(Vector3 Position, Vector3 Color, float Intensity, float Radius, LightFalloff Falloff,
    float Exponent = 8)
{
    /// <summary>Дальше от камеры (м) — источника нет; 0 — без предела.</summary>
    public float MaxDrawDistance { get; init; }

    /// <summary>Полоса угасания перед <see cref="MaxDrawDistance"/>, м.</summary>
    public float FadeRange { get; init; }

    /// <summary>Light function кадра (<see cref="PointLights.AddFunction"/>); −1 — без неё (множитель 1).</summary>
    public int Function { get; init; } = -1;

    /// <summary>
    /// Множитель света в рассеянии объёмов (UE <c>VolumetricScatteringIntensity</c>): берёт тот, кто рисует объём;
    /// light function на него не действует, в буфер кадра не пишется.
    /// </summary>
    public float VolumetricScattering { get; init; } = 1;

    /// <summary>
    /// Тестовый источник «факел LIVE-2». Без дальности показа: в LIVE-2 она 12 м, а камера арены по умолчанию в 24 м.
    /// </summary>
    public static PointLight Torch(Vector3 position) =>
        new(position, new Vector3(1f, 0.038204f, 0f), 100f, 20f, LightFalloff.Exponent, 400f) { VolumetricScattering = 0.15f };
}

/// <summary>
/// Light function точечного источника — две шумовые текстуры (как <c>M_LightFunction1</c> LIVE-2):
/// LF = A.r(UV·<see cref="ScaleA"/> + sin(2π·t)·<see cref="WobbleA"/>) + B.r(UV·<see cref="ScaleB"/> + t·<see cref="VelocityB"/>),
/// затем к <see cref="DisabledBrightness"/> в последних 20 % <see cref="FadeDistance"/> и в пределах 0…1.
/// UV — сферические углы направления в осях LF (<see cref="PointLightMath.LightFunctionUv"/>). Текстуры — bindless SRV
/// <c>Texture2DArray</c>, слой 0, канал r, билинейно с повтором, мип 0.
/// </summary>
public readonly record struct LightFunction(int TextureA, int TextureB)
{
    /// <summary>Шумы из текстур пака (<see cref="Renderer.LoadTexture(string)"/>): слой 0.</summary>
    public LightFunction(GpuTexture textureA, GpuTexture textureB) : this(textureA.Srv, textureB.Srv) { }

    /// <summary>Шумы из картинок рендера (<c>Renderer.CreateImage</c>).</summary>
    public LightFunction(GpuImage textureA, GpuImage textureB) : this(textureA.Srv, textureB.Srv) { }

    public float ScaleA { get; init; } = 0.2f;
    public Vector2 WobbleA { get; init; } = new(0.02f, 0.005f);
    public float ScaleB { get; init; } = 0.001f;
    public Vector2 VelocityB { get; init; } = new(0.03f, 0.01f);

    /// <summary>Время анимации, с (задаёт вызывающий каждый кадр).</summary>
    public double Time { get; init; }

    /// <summary>
    /// Поворот осей LF в мир. Шов развёртки (u = 0 | 1) — полуплоскость −Y осей LF, полюса — ±Z: поворотом их уводят
    /// туда, где поверхностей нет.
    /// </summary>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;

    /// <summary>Поворот из мира в оси LF (обратный <see cref="Rotation"/>; нулевой — как без поворота).</summary>
    public Quaternion ToLocal => Rotation == default ? Quaternion.Identity : Quaternion.Conjugate(Quaternion.Normalize(Rotation));

    /// <summary>Дальше от камеры (м) — яркость <see cref="DisabledBrightness"/> вместо LF (UE: 100000 см).</summary>
    public float FadeDistance { get; init; } = 1000f;

    /// <summary>Яркость без LF (UE DisabledBrightness, по умолчанию 0.5).</summary>
    public float DisabledBrightness { get; init; } = 0.5f;

    /// <summary>Сдвиг UV текстуры A в момент <see cref="Time"/>: sin(2π·t)·WobbleA.</summary>
    public Vector2 OffsetA => WobbleA * (float)Math.Sin(2 * Math.PI * Time);

    /// <summary>Сдвиг UV текстуры B: t·VelocityB по модулю 1 (выборка с повтором — тот же узор, точность float не теряется).</summary>
    public Vector2 OffsetB => new(Frac(Time * VelocityB.X), Frac(Time * VelocityB.Y));

    private static float Frac(double x) => (float)(x - Math.Floor(x));
}

/// <summary>
/// Формулы точечного света — CPU-эталон <c>common/lights.hlsli</c> (UE 5.8: RadialAttenuation, GetLocalLightAttenuation,
/// Lambert, GetLightFadeFactor, LightFunctionPixelShader). Только диффуз: specular нет.
/// </summary>
public static class PointLightMath
{
    /// <summary>Сдвиг d² режима обратного квадрата: 1 см² (DistBiasSqr UE) — в м².</summary>
    public const float DistanceBiasSq = 1e-4f;

    /// <summary>Полоса угасания LF у <see cref="LightFunction.FadeDistance"/>: доля дальности (UE — 0.2).</summary>
    public const float FunctionFadeBand = 0.2f;

    /// <summary>
    /// Затухание на расстоянии <paramref name="distance"/> (м): экспонента — (1 − saturate(d²/R²))^exp; обратный квадрат —
    /// saturate(1 − (d²/R²)²)² / (d² + 1 см²). За радиусом — 0.
    /// </summary>
    public static float Attenuation(float distance, float radius, LightFalloff falloff, float exponent)
    {
        float distSq = distance * distance;
        float x = distSq / (radius * radius);
        if (falloff == LightFalloff.InverseSquared)
        {
            float w = Math.Clamp(1 - x * x, 0, 1);
            return w * w / (distSq + DistanceBiasSq);
        }
        float b = 1 - Math.Clamp(x, 0, 1);
        return b > 0 ? MathF.Pow(b, exponent) : 0;
    }

    /// <summary>
    /// Угасание по дальности показа (UE GetLightFadeFactor): clamp((MaxDrawDistance − d) / FadeRange, 0, 1); предела нет — 1.
    /// </summary>
    public static float DistanceFade(float cameraDistance, float maxDrawDistance, float fadeRange) =>
        maxDrawDistance > 0 ? Math.Clamp((maxDrawDistance - cameraDistance) / MathF.Max(fadeRange, 1e-5f), 0, 1) : 1;

    /// <summary>
    /// UV light function по единичному направлению от источника (оси SWORD2: +X влево, +Y вверх, +Z вперёд). Оси UE
    /// (x, y, z) = SWORD2 (z, −x, y), поэтому u = (atan2(−dir.x, dir.y) + π)/2π, v = acos(dir.z)/π.
    /// </summary>
    public static Vector2 LightFunctionUv(Vector3 dir) =>
        new((MathF.Atan2(-dir.X, dir.Y) + MathF.PI) / (2 * MathF.PI), MathF.Acos(Math.Clamp(dir.Z, -1, 1)) / MathF.PI);

    /// <summary>
    /// Сумма текстур light function по единичному направлению от источника (до угасания и зажима) — CPU-эталон выборки шейдера.
    /// </summary>
    public static float LightFunctionSum(in LightFunction function, TextureTexels a, TextureTexels b, Vector3 dir)
    {
        var uv = LightFunctionUv(Vector3.Transform(dir, function.ToLocal));
        return a.Sample(uv * function.ScaleA + function.OffsetA) + b.Sample(uv * function.ScaleB + function.OffsetB);
    }

    /// <summary>
    /// Значение LF из суммы текстур: к <paramref name="disabled"/> в последних 20 % <paramref name="fadeDistance"/>, затем
    /// в пределах 0…1 (UE кладёт LF в 8-битный буфер). Дальность 0 — всюду <paramref name="disabled"/>.
    /// </summary>
    public static float LightFunctionValue(float sum, float viewDistance, float fadeDistance, float disabled)
    {
        // При дальности 0 доля — 0/0 или −∞: saturate шейдера даёт из NaN ноль, Math.Clamp оставил бы NaN.
        float fade = (fadeDistance - viewDistance) / (fadeDistance * FunctionFadeBand);
        fade = float.IsNaN(fade) ? 0 : Math.Clamp(fade, 0, 1);
        return Math.Clamp(disabled + (sum - disabled) * fade, 0, 1);
    }

    /// <summary>
    /// Диффузный вклад источника — множитель диффузного цвета: цвет × интенсивность × затухание × N·L × fade × LF / π.
    /// Точка в самом источнике — 0.
    /// </summary>
    public static Vector3 Diffuse(in PointLight light, Vector3 point, Vector3 normal, float fade = 1, float lightFunction = 1)
    {
        var toLight = light.Position - point;
        float distance = toLight.Length();
        float att = Attenuation(distance, light.Radius, light.Falloff, light.Exponent);
        if (att <= 0 || distance <= 0) return Vector3.Zero;
        float ndl = Math.Clamp(Vector3.Dot(normal, toLight / distance), 0, 1);
        return light.Color * (light.Intensity * fade * att * ndl * lightFunction / MathF.PI);
    }
}

/// <summary>Источник в кадре (отчёт): параметры, угасание по дальности показа, LF (−1 — нет).</summary>
public readonly record struct ShownPointLight(PointLight Light, float Fade);

/// <summary>Точечный свет последнего кадра: показано, отброшено сверх предела, скрыто дальностью показа, light functions.</summary>
public sealed record PointLightsReport(int Shown, int Dropped, int Hidden, int Functions, int DroppedFunctions, IReadOnlyList<ShownPointLight> Lights);

/// <summary>
/// Раскладка источника для шейдера (<c>PointLightData</c> в <c>common/lights.hlsli</c>), строки по 16 байт.
/// Элемент 0 — заголовок с числом источников в <see cref="Count"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuPointLight
{
    public Vector3 Position; public float InvRadiusSq;
    /// <summary>Цвет × интенсивность × угасание по дальности показа.</summary>
    public Vector3 Color; public float Exponent;
    public uint Flags; public uint TextureA; public uint TextureB; public float FunctionFadeDistance;
    public float ScaleA; public float ScaleB; public float DisabledBrightness; public uint Count;
    public Vector2 OffsetA; public Vector2 OffsetB;
    /// <summary>Поворот из мира в оси LF (кватернион xyzw).</summary>
    public Quaternion FunctionToLocal;
}

/// <summary>
/// Точечные источники кадра: список живёт один кадр; после построения кадра скрытые дальностью показа отбрасываются,
/// первые <see cref="MaxLights"/> видимых пишутся в буфер (<see cref="Srv"/>), остальные — в отчёт. Без выделений в кадре.
/// </summary>
public sealed class PointLights : IDisposable
{
    /// <summary>Источников в буфере кадра (шейдер обходит их всех для каждого пикселя).</summary>
    public const int MaxLights = 64;

    /// <summary>Источников в очереди кадра (видимых из них в буфер идёт не больше <see cref="MaxLights"/>).</summary>
    public const int MaxQueued = 256;

    /// <summary>Light functions в кадре.</summary>
    public const int MaxFunctions = 16;

    internal const uint FlagInverseSquared = 1, FlagFunction = 2;

    private readonly FrameData<GpuPointLight> _ring;
    private readonly PointLight[] _queued = new PointLight[MaxQueued];
    private readonly LightFunction[] _functions = new LightFunction[MaxFunctions];
    private int _queuedCount, _overflow, _functionCount, _droppedFunctions;

    // Последний записанный кадр — для отчёта (копии, без выделений).
    private readonly ShownPointLight[] _shown = new ShownPointLight[MaxLights];
    private int _shownCount, _shownDropped, _shownHidden, _shownFunctions, _shownDroppedFunctions;

    internal PointLights(Renderer renderer) => _ring = renderer.CreateFrameData<GpuPointLight>("Frame.PointLights", MaxLights + 1);

    internal static void Contribute(ShaderContract c)
    {
        c.Section("точечный свет (Sword.Engine.Render.PointLights)");
        c.AddSize<GpuPointLight>("POINT_LIGHT_SIZE");
        c.Add("POINT_LIGHTS_MAX", MaxLights, "источников в буфере кадра");
        c.Add("POINT_LIGHT_INVERSE_SQUARED", FlagInverseSquared, "флаг: обратный квадрат (иначе экспонента)");
        c.Add("POINT_LIGHT_FUNCTION", FlagFunction, "флаг: есть light function");
        c.Add("POINT_LIGHT_BIAS_SQ", PointLightMath.DistanceBiasSq, "сдвиг d^2 обратного квадрата, м^2");
        c.Add("POINT_LIGHT_LF_BAND", PointLightMath.FunctionFadeBand, "полоса угасания LF, доля дальности");
        c.Add("POINT_LIGHT_PI", MathF.PI);
    }

    /// <summary>SRV буфера источников этого кадра (действителен с начала кадра — кладётся в константы кадра до добавления источников).</summary>
    public int Srv => _ring.Srv;

    /// <summary>Точка камеры кадра — для дальности показа (<see cref="PointLight.MaxDrawDistance"/>); задаёт тот, кто строит кадр.</summary>
    public Vector3 ViewOrigin { get; set; }

    /// <summary>Источников добавлено в этом кадре (в очереди, до отбора).</summary>
    public int Queued => _queuedCount;

    /// <summary>Источник в кадр; очередь полна (<see cref="MaxQueued"/>) — false, источник отброшен (счётчик в отчёте).</summary>
    public bool Add(in PointLight light)
    {
        if (_queuedCount == MaxQueued)
        {
            _overflow++;
            return false;
        }
        _queued[_queuedCount++] = light;
        return true;
    }

    /// <summary>Light function на этот кадр; вернёт её индекс для <see cref="PointLight.Function"/>, −1 — мест нет (отброшена, в отчёте).</summary>
    public int AddFunction(in LightFunction function)
    {
        if (_functionCount == MaxFunctions)
        {
            _droppedFunctions++;
            return -1;
        }
        _functions[_functionCount] = function;
        return _functionCount++;
    }

    internal void BeginFrame()
    {
        _queuedCount = _overflow = _functionCount = _droppedFunctions = 0;
    }

    /// <summary>Отбор и запись буфера кадра — после построения кадра, до исполнения графа.</summary>
    internal void Flush()
    {
        var slot = _ring.Slot;
        int shown = 0, hidden = 0, dropped = _overflow;
        for (int i = 0; i < _queuedCount; i++)
        {
            ref readonly var light = ref _queued[i];
            float fade = Visible(light);
            if (fade <= 0)
            {
                hidden++;
                continue;
            }
            if (shown == MaxLights)
            {
                dropped++;
                continue;
            }
            _shown[shown] = new ShownPointLight(light, fade);
            slot[++shown] = Pack(light, fade);
        }
        slot[0] = new GpuPointLight { Count = (uint)shown };
        _shownCount = shown;
        _shownDropped = dropped;
        _shownHidden = hidden;
        _shownFunctions = _functionCount;
        _shownDroppedFunctions = _droppedFunctions;
    }

    /// <summary>Угасание по дальности показа от <see cref="ViewOrigin"/>; 0 — источник скрыт (или без света).</summary>
    private float Visible(in PointLight light) =>
        light.Intensity <= 0 || light.Radius <= 0 || light.Color == Vector3.Zero ? 0
            : PointLightMath.DistanceFade(Vector3.Distance(light.Position, ViewOrigin), light.MaxDrawDistance, light.FadeRange);

    private GpuPointLight Pack(in PointLight light, float fade)
    {
        var g = new GpuPointLight
        {
            Position = light.Position,
            InvRadiusSq = 1 / (light.Radius * light.Radius),
            Color = light.Color * (light.Intensity * fade),
            Exponent = light.Exponent,
            Flags = light.Falloff == LightFalloff.InverseSquared ? FlagInverseSquared : 0,
        };
        if ((uint)light.Function < (uint)_functionCount)
        {
            ref readonly var f = ref _functions[light.Function];
            g.Flags |= FlagFunction;
            g.TextureA = (uint)f.TextureA;
            g.TextureB = (uint)f.TextureB;
            g.FunctionFadeDistance = f.FadeDistance;
            g.ScaleA = f.ScaleA;
            g.ScaleB = f.ScaleB;
            g.DisabledBrightness = f.DisabledBrightness;
            g.OffsetA = f.OffsetA;
            g.OffsetB = f.OffsetB;
            g.FunctionToLocal = f.ToLocal;
        }
        return g;
    }

    /// <summary>Источники последнего записанного кадра (выделяет: для отчёта, не для кадра).</summary>
    public PointLightsReport Report() =>
        new(_shownCount, _shownDropped, _shownHidden, _shownFunctions, _shownDroppedFunctions, _shown.AsSpan(0, _shownCount).ToArray());

    /// <summary>Показано источников в последнем записанном кадре.</summary>
    public int Shown => _shownCount;

    /// <summary>
    /// Оверлей источников кадра отрезками: крест, круги радиуса R и круг половинного затухания; с <paramref name="labels"/> —
    /// подпись (выделяет строку). Вернёт, сколько нарисовано.
    /// </summary>
    public int DrawOverlay(DebugDraw debug, bool labels = false)
    {
        int drawn = 0;
        for (int i = 0; i < _queuedCount && drawn < MaxLights; i++)
        {
            ref readonly var light = ref _queued[i];
            float fade = Visible(light);
            if (fade <= 0) continue;
            DrawLight(debug, light, fade, drawn++, labels);
        }
        return drawn;
    }

    private static void DrawLight(DebugDraw debug, in PointLight light, float fade, int index, bool labels)
    {
        uint colour = OverlayColour(light.Color);
        uint dim = (colour & 0x00FFFFFFu) | 0x80000000u;
        var p = light.Position;
        const float Cross = 0.25f;
        debug.Line(p - new Vector3(Cross, 0, 0), p + new Vector3(Cross, 0, 0), colour);
        debug.Line(p - new Vector3(0, Cross, 0), p + new Vector3(0, Cross, 0), colour);
        debug.Line(p - new Vector3(0, 0, Cross), p + new Vector3(0, 0, Cross), colour);
        Circle(debug, p, Vector3.UnitX, Vector3.UnitZ, light.Radius, dim);
        Circle(debug, p, Vector3.UnitX, Vector3.UnitY, light.Radius, dim);
        Circle(debug, p, Vector3.UnitZ, Vector3.UnitY, light.Radius, dim);
        if (light.Falloff == LightFalloff.Exponent && light.Exponent > 0)
            Circle(debug, p, Vector3.UnitX, Vector3.UnitZ, light.Radius * MathF.Sqrt(1 - MathF.Pow(0.5f, 1 / light.Exponent)), colour);
        if (labels)
            debug.Text(p + new Vector3(0, 0.35f, 0), string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"свет {index}: I {light.Intensity:0.##} R {light.Radius:0.##} м{(fade < 1 ? $" угасание {fade:0.##}" : "")}"), colour);
    }

    private static void Circle(DebugDraw debug, Vector3 centre, Vector3 a, Vector3 b, float radius, uint colour)
    {
        const int Segments = 48;
        var prev = centre + a * radius;
        for (int k = 1; k <= Segments; k++)
        {
            float t = k * (2 * MathF.PI / Segments);
            var next = centre + (a * MathF.Cos(t) + b * MathF.Sin(t)) * radius;
            debug.Line(prev, next, colour);
            prev = next;
        }
    }

    /// <summary>Цвет оверлея: цвет источника с наибольшим каналом 1 (ABGR, непрозрачный).</summary>
    private static uint OverlayColour(Vector3 c)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (m <= 0) return 0xFFFFFFFFu;
        c /= m;
        // Тёмные каналы подняты: чистый красный факела в оверлее — оранжево-красный, а не тёмный.
        c = Vector3.Max(c, new Vector3(0.25f));
        return 0xFF000000u | ((uint)(c.Z * 255) << 16) | ((uint)(c.Y * 255) << 8) | (uint)(c.X * 255);
    }

    public void Dispose() => _ring.Dispose();
}
