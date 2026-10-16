using System.Numerics;
using Sword.Engine.Render;

namespace Sword.Game.Fire;

/// <summary>
/// Числа света факела — точечный источник LIVE-2 (FSR §3.9): смещение от центра шишки, цвет, интенсивность (unitless), затухание
/// «экспонента» с радиусом, множитель рассеяния в объёмах, light function <c>M_LightFunction1</c> (две шумовые текстуры — умолчания
/// <see cref="LightFunction"/>) и угасание гаснущего огня. Дальности показа нет (в LIVE-2 12 м — камера арены по умолчанию дальше).
/// </summary>
public sealed record FireLightParams
{
    /// <summary>
    /// Смещение источника от центра шишки в мировых осях SWORD2, м: в LIVE-2 (0, −10, +10) см в мировых осях UE; UE (x, y, z) = SWORD2
    /// (z, −x, y) — 10 см вверх (+Y) и 10 см вбок (+X), от поворота бойца не зависит.
    /// </summary>
    public Vector3 Offset { get; init; } = new(0.1f, 0.1f, 0f);

    /// <summary>Линейный цвет (<c>SetLightColor((1, 0.038204, 0))</c> на BeginPlay).</summary>
    public Vector3 Colour { get; init; } = new(1f, 0.038204f, 0f);

    /// <summary>Интенсивность, unitless (без обратного квадрата — множитель цвета как есть).</summary>
    public float Intensity { get; init; } = 100f;

    /// <summary>Радиус (м) и показатель «экспоненты»: (1 − d²/R²)^400 при R 20 м ≈ exp(−(d/1 м)²).</summary>
    public float Radius { get; init; } = 20f;
    public float Exponent { get; init; } = 400f;

    /// <summary>Множитель в рассеянии объёмов (<c>VolumetricScatteringIntensity</c>).</summary>
    public float VolumetricScattering { get; init; } = 0.15f;

    /// <summary>
    /// Угасание гаснущего огня за шаг сима (60 Гц): интенсивность × это^(шагов с начала гашения) — так убывает Σρ поля без кисти при
    /// накоплении 0.9 (≈0.27 с до e-кратного). К концу срока гашения (90 шагов) — 0.004: свет не обрывается скачком.
    /// </summary>
    public float DecayPerStep { get; init; } = 0.9405f;

    /// <summary>
    /// Поворот осей light function: 180° вокруг оси полюсов развёртки (+Z SWORD2) — шов u = 0 | 1 уходит с пола под огнём (в осях мира он
    /// там) вверх, где поверхностей нет. Отступление: поворот источника в LIVE-2 не установлен.
    /// </summary>
    public Quaternion FunctionRotation { get; init; } = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

    /// <summary>Факел LIVE-2.</summary>
    public static FireLightParams Torch { get; } = new();

    /// <summary>Источник в <paramref name="position"/> с интенсивностью × <paramref name="fade"/>, без light function.</summary>
    public PointLight Light(Vector3 position, float fade) =>
        new(position, Colour, Intensity * fade, Radius, LightFalloff.Exponent, Exponent) { VolumetricScattering = VolumetricScattering };

    /// <summary>Light function кадра: текстуры A (T_Noise3) и B (T_NoiseTemplate2), время <paramref name="seconds"/>, поворот осей.</summary>
    public LightFunction Function(int textureA, int textureB, double seconds) =>
        new(textureA, textureB) { Time = seconds, Rotation = FunctionRotation };
}

/// <summary>Формулы света огня без GPU: множитель гаснущего огня и позиция источника.</summary>
public static class FireLightMath
{
    /// <summary>
    /// Множитель интенсивности экземпляра: горит — 1, свободен — 0, гаснет — decay^шагов от <see cref="FireSlot.FadeTick"/> до показанного
    /// момента кадра <paramref name="shownTick"/> (тик с долей: показ между последними двумя тиками) при <paramref name="tickRate"/> тиков в
    /// секунду.
    /// </summary>
    public static float Fade(FireSlot slot, double shownTick, int tickRate, float decayPerStep) => slot.State switch
    {
        FireState.Burning => 1f,
        FireState.Dying => MathF.Pow(decayPerStep, (float)(Math.Max(0, shownTick - slot.FadeTick) * FireArea.StepRate / tickRate)),
        _ => 0f,
    };

    /// <summary>Позиция источника: показанный центр шишки + смещение.</summary>
    public static Vector3 Position(Vector3 displayCenter, FireLightParams p) => displayCenter + p.Offset;
}

/// <summary>
/// Свет огней кадра: источник на каждый горящий или гаснущий экземпляр в показанном центре шишки (<see cref="FireModule.Display"/>) со
/// смещением, одна light function на все (время — время кадра, как у пламени). Считается в извлечении кадра, в кадр идёт до проходов игры.
/// Текстур LF нет — света нет. Для отчёта — значение LF вниз (на пол под огнём) в этом кадре и его разброс за прогон (CPU-выборка тех же
/// текстур). Кадр — 0 Б.
/// </summary>
public sealed class FireLight(int maxFires)
{
    /// <summary>Шумы LF: T_Noise3 (sRGB) и T_NoiseTemplate2 (линейная, её же берёт кисть).</summary>
    public const string NoisePathA = "textures/fire/light_noise.swtex", NoisePathB = FireModule.BrushNoisePath;

    private readonly PointLight[] _lights = new PointLight[maxFires];
    private readonly float[] _fades = new float[maxFires];
    private TextureTexels? _texelsA, _texelsB;
    private int _textureA = -1, _textureB = -1;

    public FireLightParams Params { get; set; } = FireLightParams.Torch;

    /// <summary>Свет включён (ручка <c>fire.light</c>) и LF (ручка <c>fire.lightFunction</c>; выкл. — ровно затухание).</summary>
    public bool Shown { get; set; } = true;
    public bool FunctionShown { get; set; } = true;

    /// <summary>Текстуры LF загружены — свет возможен.</summary>
    public bool Ready => _textureA >= 0;

    /// <summary>Источники идут в кадр (текстуры есть, ручка включена) — их же рассеивает дым.</summary>
    public bool Emits => Ready && Shown;

    /// <summary>Почему света нет (текстур нет в паке); null — есть.</summary>
    public string? Missing { get; internal set; }

    /// <summary>Время light function кадра, с.</summary>
    public double Seconds { get; private set; }

    /// <summary>Источников кадра (горящих и гаснущих), отданных рендеру в последнем кадре.</summary>
    public int Added { get; private set; }

    /// <summary>LF вниз в этом кадре (−1 — не считалась: нет текстур или огня) и за прогон: кадров, среднее, минимум, максимум, доля насыщения.</summary>
    public float FrameFunction { get; private set; } = -1;
    public long FunctionFrames { get; private set; }
    public double FunctionMean => FunctionFrames > 0 ? _functionSum / FunctionFrames : 0;
    public float FunctionMin { get; private set; } = float.MaxValue;
    public float FunctionMax { get; private set; } = float.MinValue;
    public double FunctionSaturated => FunctionFrames > 0 ? (double)_saturated / FunctionFrames : 0;
    private double _functionSum;
    private long _saturated;

    /// <summary>Источник экземпляра <paramref name="slot"/> в этом кадре (без LF); null — огня нет.</summary>
    public PointLight? Light(int slot) => _fades[slot] > 0 ? _lights[slot] : null;

    /// <summary>Множитель интенсивности экземпляра в этом кадре (0 — огня нет).</summary>
    public float Fade(int slot) => _fades[slot];

    /// <summary>Шумы LF: SRV для шейдера и тексели на CPU для отчёта.</summary>
    internal void SetTextures(int textureA, int textureB, TextureTexels texelsA, TextureTexels texelsB)
    {
        (_textureA, _textureB, _texelsA, _texelsB) = (textureA, textureB, texelsA, texelsB);
    }

    /// <summary>
    /// Извлечение кадра: источники экземпляров в показанных центрах <paramref name="display"/>, угасание гаснущих на момент
    /// <paramref name="shownTick"/>, время LF <paramref name="seconds"/>.
    /// </summary>
    public void Update(FireSlots slots, ReadOnlySpan<Vector3> display, double seconds, double shownTick)
    {
        Seconds = seconds;
        var p = Params;
        bool any = false;
        for (int i = 0; i < _lights.Length; i++)
        {
            float fade = FireLightMath.Fade(slots[i], shownTick, slots.TickRate, p.DecayPerStep);
            _fades[i] = fade;
            if (fade <= 0) continue;
            _lights[i] = p.Light(FireLightMath.Position(display[i], p), fade);
            any = true;
        }
        FrameFunction = -1;
        if (!any || _texelsA is null || _texelsB is null) return;
        var function = p.Function(_textureA, _textureB, seconds);
        float lf = PointLightMath.LightFunctionValue(PointLightMath.LightFunctionSum(function, _texelsA, _texelsB, -Vector3.UnitY), 0,
            function.FadeDistance, function.DisabledBrightness);
        FrameFunction = lf;
        FunctionFrames++;
        _functionSum += lf;
        if (lf >= 1) _saturated++;
        FunctionMin = MathF.Min(FunctionMin, lf);
        FunctionMax = MathF.Max(FunctionMax, lf);
    }

    /// <summary>Источники кадра рендеру (до проходов игры): одна LF на все, если включена.</summary>
    public void Add(PointLights lights)
    {
        Added = 0;
        if (!Ready || !Shown) return;
        int function = -2;
        for (int i = 0; i < _lights.Length; i++)
        {
            if (_fades[i] <= 0) continue;
            if (function == -2) function = FunctionShown ? lights.AddFunction(Params.Function(_textureA, _textureB, Seconds)) : -1;
            lights.Add(_lights[i] with { Function = function });
            Added++;
        }
    }

    /// <summary>Раздел <c>fire.light</c> итога.</summary>
    internal object Report(FireSlots slots)
    {
        var p = Params;
        var function = p.Function(_textureA, _textureB, Seconds);
        return new
        {
            shown = Shown,
            function = FunctionShown,
            state = Ready ? "ok" : Missing is null ? "нет огня" : $"нет текстур LF: {Missing}",
            added = Added,
            offset = new[] { p.Offset.X, p.Offset.Y, p.Offset.Z },
            colour = new[] { p.Colour.X, p.Colour.Y, p.Colour.Z },
            intensity = p.Intensity,
            radius = p.Radius,
            exponent = p.Exponent,
            volumetricScattering = p.VolumetricScattering,
            decayPerStep = p.DecayPerStep,
            lf = new
            {
                seconds = Seconds,
                offsetA = new[] { function.OffsetA.X, function.OffsetA.Y },
                offsetB = new[] { function.OffsetB.X, function.OffsetB.Y },
                rotation = new[] { function.Rotation.X, function.Rotation.Y, function.Rotation.Z, function.Rotation.W },
                // LF вниз от источника (пол под огнём): в последнем кадре и за прогон по кадрам со светом.
                down = FrameFunction,
                frames = FunctionFrames,
                mean = FunctionMean,
                min = FunctionFrames > 0 ? FunctionMin : 0,
                max = FunctionFrames > 0 ? FunctionMax : 0,
                saturated = FunctionSaturated,
            },
            lights = Enumerable.Range(0, _lights.Length).Where(i => _fades[i] > 0).Select(i => new
            {
                slot = i,
                state = slots[i].State.ToString().ToLowerInvariant(),
                position = new[] { _lights[i].Position.X, _lights[i].Position.Y, _lights[i].Position.Z },
                intensity = _lights[i].Intensity,
                fade = _fades[i],
            }).ToArray(),
        };
    }
}
