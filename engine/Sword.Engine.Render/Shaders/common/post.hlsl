// Постобработка HDR (HdrScene): bloom и тонмап Filmic — (линейный цвет сцены (RGBA16F) + bloom) x экспозиция -> Filmic ->
// sRGB-кодирование в задний буфер (R8G8B8A8_UNorm, не sRGB-формат: кодирует шейдер). Полноэкранный треугольник.
// Шаги и числа тонмапа — FilmicTonemap.cs (CPU-эталон): матрицы и константы приходят из контракта, кривая — корневыми константами.
// Bloom — BloomKernel.cs (числа, CPU-эталон ядра) и BloomChain.cs (проходы).
#include "pass.hlsli"

struct FilmicCurveK
{
    float slope, toeMatch, straightMatch, shoulderMatch, toeScale, shoulderScale, blackClip, whiteClip, invSpan;
};

FilmicCurveK CurveConstants()
{
    FilmicCurveK k;
    k.slope = asfloat(POST_SLOPE);
    k.toeMatch = asfloat(POST_TOE_MATCH);
    k.straightMatch = asfloat(POST_STRAIGHT_MATCH);
    k.shoulderMatch = asfloat(POST_SHOULDER_MATCH);
    k.toeScale = asfloat(POST_TOE_SCALE);
    k.shoulderScale = asfloat(POST_SHOULDER_SCALE);
    k.blackClip = asfloat(POST_BLACK_CLIP);
    k.whiteClip = asfloat(POST_WHITE_CLIP);
    k.invSpan = asfloat(POST_INV_SPAN);
    return k;
}

// Яркие насыщенные цвета — к более широкой гамме (доля по хроме и яркости AP1).
float3 ExpandToWide(float3 ap1)
{
    float luma = dot(ap1, FILMIC_AP1_LUMA);
    float3 chroma = ap1 / max(luma, FILMIC_LOG_FLOOR) - 1;
    float amount = (1 - exp2(-4 * dot(chroma, chroma))) * (1 - exp2(-4 * FILMIC_EXPAND_GAMUT * luma * luma));
    return lerp(ap1, mul(FILMIC_EXPAND, ap1), amount);
}

float AcesSaturation(float3 c)
{
    float hi = max(c.r, max(c.g, c.b)), lo = min(c.r, min(c.g, c.b));
    return (max(hi, FILMIC_ACES_TINY) - max(lo, FILMIC_ACES_TINY)) / max(hi, FILMIC_ACES_SATURATION_FLOOR);
}

float AcesYc(float3 c)
{
    float chroma = sqrt(max(c.b * (c.b - c.g) + c.g * (c.g - c.r) + c.r * (c.r - c.b), 0));
    return (c.r + c.g + c.b + FILMIC_YC_RADIUS * chroma) / 3;
}

float SigmoidShaper(float x)
{
    float t = max(1 - abs(0.5 * x), 0);
    return 0.5 * (1 + sign(x) * (1 - t * t));
}

float GlowGain(float yc, float gain)
{
    if (yc <= 2.0 / 3.0 * FILMIC_GLOW_MID) return gain;
    if (yc >= 2 * FILMIC_GLOW_MID) return 0;
    return gain * (FILMIC_GLOW_MID / yc - 0.5);
}

// Тон в градусах от красного (-180..180); у серого - 0. Перевода ">180 -> -360" нет: atan2 уже в [-pi, pi], за 180 - не больше ULP
// округления, а вес красного (от |тон|) там и так 0.
float CenteredHue(float3 c)
{
    if (c.r == c.g && c.g == c.b) return 0;
    return FILMIC_DEGREES_PER_RADIAN * atan2(FILMIC_SQRT3 * (c.g - c.b), 2 * c.r - c.g - c.b);
}

float CurveChannel(float x, FilmicCurveK k)
{
    float l = log10(max(x, FILMIC_LOG_FLOOR));
    float straight = k.slope * (l + k.straightMatch);
    float toe = l < k.toeMatch
        ? -k.blackClip + 2 * k.toeScale / (1 + exp(-2 * k.slope / k.toeScale * (l - k.toeMatch)))
        : straight;
    float shoulder = l > k.shoulderMatch
        ? 1 + k.whiteClip - 2 * k.shoulderScale / (1 + exp(2 * k.slope / k.shoulderScale * (l - k.shoulderMatch)))
        : straight;
    float t = saturate((l - k.toeMatch) * k.invSpan);
    if (k.invSpan < 0) t = 1 - t;
    t = t * t * (3 - 2 * t);
    return toe + (shoulder - toe) * t;
}

// Кривая в AP1: glow и red modifier ACES в AP0, десатурации и S-кривая по каналам в AP1.
float3 ToneAp1(float3 ap1, FilmicCurveK k)
{
    float3 ap0 = mul(FILMIC_AP1_TO_AP0, ap1);
    float saturation = AcesSaturation(ap0);
    ap0 *= 1 + GlowGain(AcesYc(ap0), FILMIC_GLOW_GAIN * SigmoidShaper((saturation - FILMIC_GLOW_SATURATION_MID) / FILMIC_GLOW_SATURATION_WIDTH));
    float weight = smoothstep(0, 1, 1 - abs(2 * CenteredHue(ap0) / FILMIC_RED_WIDTH));
    weight *= weight;
    ap0.r += weight * saturation * (FILMIC_RED_PIVOT - ap0.r) * (1 - FILMIC_RED_SCALE);

    float3 w = max(mul(FILMIC_AP0_TO_AP1, ap0), 0);
    w = lerp(dot(w, FILMIC_AP1_LUMA), w, FILMIC_PRE_DESATURATE);
    float3 tone = float3(CurveChannel(w.r, k), CurveChannel(w.g, k), CurveChannel(w.b, k));
    tone = lerp(dot(tone, FILMIC_AP1_LUMA), tone, FILMIC_POST_DESATURATE);
    return max(tone, 0);
}

float EncodeSrgb(float v)
{
    return saturate(v <= FILMIC_SRGB_LINEAR_LIMIT ? FILMIC_SRGB_LINEAR_SLOPE * v : FILMIC_SRGB_SCALE * pow(v, 1 / FILMIC_SRGB_GAMMA) - FILMIC_SRGB_OFFSET);
}

float3 FilmicDisplay(float3 scene, float exposure)
{
    float3 c = clamp(scene * exposure, 0, FILMIC_INPUT_MAX);   // NaN -> 0: min и max берут не-NaN операнд
    float3 ap1 = mul(FILMIC_REC709_TO_AP1, c);
    ap1 = ExpandToWide(ap1);
    ap1 = mul(FILMIC_BLUE_PRE, ap1);
    ap1 = lerp(ap1, ToneAp1(ap1, CurveConstants()), FILMIC_TONE_CURVE_AMOUNT);
    float3 rgb = max(mul(FILMIC_OUTPUT, ap1), 0);
    return float3(EncodeSrgb(rgb.r), EncodeSrgb(rgb.g), EncodeSrgb(rgb.b));
}

// Экспозиция кадра: буфер автоэкспозиции (AutoExposure, элемент 0.x) или корневая константа.
float FrameExposure(uint constant, uint buffer)
{
    if (buffer == EXPOSURE_NONE) return asfloat(constant);
    StructuredBuffer<float4> state = ResourceDescriptorHeap[buffer];
    return state[0].x;
}

float4 VsFullscreen(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Цветокоррекция и виньетка (PostEffects) - линейный цвет после экспозиции, до кривой: баланс белого (множители той же яркости),
// насыщенность (к яркости Rec.709), контраст (степень вокруг 0.18), виньетка (углы темнее по расстоянию от центра с учётом сторон).
float3 PostGrade(float3 c, float2 px, float2 size)
{
    c *= float3(asfloat(POST_WB_R), asfloat(POST_WB_G), asfloat(POST_WB_B));
    c = max(lerp(dot(c, BLOOM_LUMA), c, asfloat(POST_SATURATION)), 0);
    c = 0.18 * pow(c / 0.18, asfloat(POST_CONTRAST));
    float2 aspect = float2(size.x / size.y, 1);
    float r = length((px / size - 0.5) * aspect) / length(0.5 * aspect);
    return c * (1 - asfloat(POST_VIGNETTE) * smoothstep(0.2, 1.0, r));
}

float4 PsTonemap(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> scene = ResourceDescriptorHeap[POST_SCENE];
    float3 c = scene.Load(int3(pos.xy, 0)).rgb;
    if (POST_BLOOM != BLOOM_NONE)
    {
        // Итог bloom — вдвое меньше: билинейно, центр пикселя экрана x+0.5 -> (x+0.5)/2 текселей bloom.
        Texture2D<float4> bloom = ResourceDescriptorHeap[POST_BLOOM];
        float2 size;
        bloom.GetDimensions(size.x, size.y);
        c += bloom.SampleLevel(gLinearClamp, 0.5 * pos.xy / size, 0).rgb;
    }
    float exposure = FrameExposure(POST_EXPOSURE, POST_EXPOSURE_BUFFER);
    if (POST_GRADE == 0) return float4(FilmicDisplay(c, exposure), 1);
    float2 size;
    scene.GetDimensions(size.x, size.y);
    return float4(FilmicDisplay(PostGrade(min(c * exposure, FILMIC_INPUT_MAX), pos.xy, size), 1), 1);
}

// ---- Bloom: сумма гауссиан ----

// Среднее блока 4x4 входа вокруг центра пикселя выхода (вход вдвое больше): четыре билинейные выборки со сдвигом +-1 тексель
// входа от угла между 2x2 текселями под центром выхода. Отрицательное — 0.
float3 BloomDownsample(uint srv, float2 pos)
{
    Texture2D<float4> src = ResourceDescriptorHeap[srv];
    float2 size;
    src.GetDimensions(size.x, size.y);
    float2 texel = 1 / size;
    float2 uv = 2 * pos * texel;
    float3 c = src.SampleLevel(gLinearClamp, uv - texel, 0).rgb
        + src.SampleLevel(gLinearClamp, uv + float2(texel.x, -texel.y), 0).rgb
        + src.SampleLevel(gLinearClamp, uv + float2(-texel.x, texel.y), 0).rgb
        + src.SampleLevel(gLinearClamp, uv + texel, 0).rgb;
    return max(0.25 * c, 0);
}

// Порог: сцена вдвое меньше, доля saturate((яркость x экспозиция - порог) x 0.5) идёт в bloom; порог <= -1 - всё.
// Вход зажат в предел half: NaN -> 0 (max берёт не-NaN операнд), бесконечность -> 65504 - bloom не разносит их по кадру.
float4 PsBloomSetup(float4 pos : SV_Position) : SV_Target0
{
    float3 c = min(BloomDownsample(BLOOM_SOURCE, pos.xy), FILMIC_INPUT_MAX);
    float threshold = asfloat(BLOOM_THRESHOLD);
    float amount = threshold > -1 ? saturate((dot(c, BLOOM_LUMA) * FrameExposure(BLOOM_EXPOSURE, BLOOM_EXPOSURE_BUFFER) - threshold) * 0.5) : 1;
    return float4(c * amount, 0);
}

float4 PsBloomDownsample(float4 pos : SV_Position) : SV_Target0
{
    return float4(BloomDownsample(BLOOM_SOURCE, pos.xy), 0);
}

float BloomWeight(float x, float radius)
{
    float t = x / radius;
    return exp(-BLOOM_FALLOFF * t * t);
}

// Гауссиана по x или по y (BLOOM_VERTICAL): тексели -N..N с весом exp(-16.7 (x/R)^2), пары соседей - одной билинейной выборкой,
// веса нормированы; x усиление ступени; + сумма более широких ступеней (уровень вдвое меньше, билинейно). За краем - чёрный
// (свет из-за края кадра не приходит, край не тянет полосу).
float4 PsBloomBlur(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> src = ResourceDescriptorHeap[BLOOM_SOURCE];
    float2 size;
    src.GetDimensions(size.x, size.y);
    float2 uv = pos.xy / size;
    float2 texel = BLOOM_VERTICAL != 0 ? float2(0, 1 / size.y) : float2(1 / size.x, 0);
    float radius = asfloat(BLOOM_RADIUS);
    int n = int(BLOOM_TAPS);
    float3 sum = 0;
    float total = 0;
    for (int i = -n; i <= n; i += 2)
    {
        float w0 = BloomWeight(i, radius);
        float w1 = i < n ? BloomWeight(i + 1, radius) : 0;
        float w = w0 + w1;
        float offset = w > 0 ? i + w1 / w : i;
        sum += w * src.SampleLevel(gLinearBorder, uv + texel * offset, 0).rgb;
        total += w;
    }
    float3 c = sum / total * asfloat(BLOOM_GAIN);
    if (BLOOM_ADDITIVE != BLOOM_NONE)
    {
        Texture2D<float4> wider = ResourceDescriptorHeap[BLOOM_ADDITIVE];
        c += wider.SampleLevel(gLinearClamp, uv, 0).rgb;
    }
    // В пределе half: сумма ступеней от пятна на пределе (бесконечность сцены) не переполняет цель в бесконечность.
    return float4(min(c, FILMIC_INPUT_MAX), 0);
}

// ---- Резкость (PostEffects.Sharpen): по мотивам CAS AMD - вес креста соседей отрицательный и тем меньше, чем ближе пиксель к
// краю диапазона (min, 1 - max) в своём кресте: на резких краях ореолов нет, в мягких деталях - резче. LDR после тонмапа.
float4 PsSharpen(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> src = ResourceDescriptorHeap[SHARPEN_SOURCE];
    int2 size;
    src.GetDimensions(size.x, size.y);
    int2 p = int2(pos.xy);
    float3 c = src.Load(int3(p, 0)).rgb;
    float3 n = src.Load(int3(clamp(p + int2(0, -1), 0, size - 1), 0)).rgb;
    float3 s = src.Load(int3(clamp(p + int2(0, 1), 0, size - 1), 0)).rgb;
    float3 w = src.Load(int3(clamp(p + int2(-1, 0), 0, size - 1), 0)).rgb;
    float3 e = src.Load(int3(clamp(p + int2(1, 0), 0, size - 1), 0)).rgb;
    float3 mn = min(c, min(min(n, s), min(w, e))), mx = max(c, max(max(n, s), max(w, e)));
    float3 amp = sqrt(saturate(min(mn, 1 - mx) / max(mx, 1e-4)));
    float3 weight = amp * (-1 / lerp(8.0, 5.0, asfloat(SHARPEN_AMOUNT)));
    return float4(saturate((c + weight * (n + s + w + e)) / (1 + 4 * weight)), 1);
}
