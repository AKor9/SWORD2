// Автоэкспозиция (AutoExposure.cs). CsMeter - группа 16x16 на плитку сетки через EXPOSURE_STRIDE пикселей: сумма log2 яркости
// Rec.709 (в пределах EXPOSURE_MIN/MAX_LUMINANCE) и число выборок плитки - в элемент 1 + плитка буфера. CsAdapt - одна группа:
// среднее по плиткам, цель key / 2^среднее в пределах min..max, экспоненциальная подстройка в log2 (светлее - speedUp, темнее -
// speedDown), итог - элемент 0 (x - экспозиция, y - средний log2 яркости).
#include "pass.hlsli"

groupshared float2 gSums[256];

float ExposureLog2(float3 c)
{
    float l = dot(min(c, FILMIC_INPUT_MAX), BLOOM_LUMA);
    return log2(clamp(l, EXPOSURE_MIN_LUMINANCE, EXPOSURE_MAX_LUMINANCE));
}

[numthreads(16, 16, 1)]
void CsMeter(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    Texture2D<float4> scene = ResourceDescriptorHeap[EXPOSURE_SCENE];
    RWStructuredBuffer<float4> state = ResourceDescriptorHeap[EXPOSURE_STATE];
    uint w, h;
    scene.GetDimensions(w, h);
    uint2 p = ((group.xy * 16 + local.xy) * EXPOSURE_STRIDE) + EXPOSURE_STRIDE / 2;
    float2 s = 0;
    if (p.x < w && p.y < h)
    {
        float3 c = scene.Load(int3(p, 0)).rgb;
        // NaN и бесконечность сцены - мимо замера.
        if (all(c == c) && all(c < 1e30)) s = float2(ExposureLog2(c), 1);
    }
    gSums[index] = s;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint step = 128; step > 0; step >>= 1)
    {
        if (index < step) gSums[index] += gSums[index + step];
        GroupMemoryBarrierWithGroupSync();
    }
    if (index == 0) state[1 + group.y * EXPOSURE_TILES + group.x] = float4(gSums[0], 0, 0);
}

[numthreads(256, 1, 1)]
void CsAdapt(uint index : SV_GroupIndex)
{
    RWStructuredBuffer<float4> state = ResourceDescriptorHeap[EXPOSURE_STATE];
    float2 s = 0;
    for (uint t = index; t < EXPOSURE_TILES; t += 256) s += state[1 + t].xy;
    gSums[index] = s;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint step = 128; step > 0; step >>= 1)
    {
        if (index < step) gSums[index] += gSums[index + step];
        GroupMemoryBarrierWithGroupSync();
    }
    if (index != 0) return;
    float4 prev = state[0];
    float average = gSums[0].y > 0 ? gSums[0].x / gSums[0].y : prev.y;
    float target = clamp(asfloat(EXPOSURE_KEY) / exp2(average), asfloat(EXPOSURE_MIN), asfloat(EXPOSURE_MAX));
    float current = prev.x > 0 ? prev.x : target;
    float delta = log2(target) - log2(current);
    float speed = delta > 0 ? asfloat(EXPOSURE_SPEED_UP) : asfloat(EXPOSURE_SPEED_DOWN);
    float exposure = exp2(log2(current) + delta * (1 - exp(-asfloat(EXPOSURE_DT) * speed)));
    state[0] = float4(exposure, average, 0, 0);
}
