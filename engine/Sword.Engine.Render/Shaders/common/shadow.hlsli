// Тень от солнца (SunShadow.cs): два каскада карт глубины в обратном Z (ближе к солнцу - больше), ортогональные проекции.
// Ближний каскад, вне него (с запасом на PCF) - дальний. Мягкий край - PCF 3x3 выборок сравнения (каждая билинейная), шаг -
// softness текселей. Против «угрей»: точка сдвинута по нормали (сильнее у скользящего света) и к солнцу на bias. За картой - без тени.
#ifndef SWORD_SHADOW_HLSLI
#define SWORD_SHADOW_HLSLI
#include "pass.hlsli"

// Тень одной карты; inside - точка в карте с запасом на выборки PCF.
float SunShadowMap(uint map, float4x4 viewProj, float texelWorld, float bias, float softness, float3 world, float3 n, float ndl,
    out bool inside)
{
    Texture2D<float> depth = ResourceDescriptorHeap[map];
    float3 p = world + n * texelWorld * SHADOW_NORMAL_OFFSET * (1 - 0.5 * ndl);
    float4 c = mul(viewProj, float4(p, 1));
    float2 uv = c.xy * float2(0.5, -0.5) + 0.5;
    uint w, h;
    depth.GetDimensions(w, h);
    float2 step = softness / float2(w, h);
    inside = all(uv > 2 * step) && all(uv < 1 - 2 * step) && c.z >= 0 && c.z <= 1;
    if (!inside) return 1;
    float lit = 0;
    [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
            lit += depth.SampleCmpLevelZero(gShadowCmp, uv + float2(x, y) * step, c.z + bias);
    return lit / 9;
}

float SunShadow(uint map0, float4x4 viewProj0, float texel0, float bias0, uint map1, float4x4 viewProj1, float texel1, float bias1,
    float softness, float3 world, float3 n, float3 toLight)
{
    if (map0 == SHADOW_NONE) return 1;
    float ndl = saturate(dot(n, toLight));
    bool inside;
    float near = SunShadowMap(map0, viewProj0, texel0, bias0, softness, world, n, ndl, inside);
    if (inside) return near;
    return SunShadowMap(map1, viewProj1, texel1, bias1, softness, world, n, ndl, inside);
}

#endif
