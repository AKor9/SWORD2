// Чтение целей сцены (SceneTargets) в фазе поверх готовой сцены: точка мира по глубине, нормаль, признак приёмника.
#ifndef SWORD_GBUFFER_HLSLI
#define SWORD_GBUFFER_HLSLI

// Обратный Z: глубина 0 — дальняя плоскость, там ничего не нарисовано.
bool SceneEmpty(float depth) { return depth <= 0; }

// Точка мира пикселя: px — SV_Position.xy, invViewProj — обратная к той, что строила глубину.
float3 SceneWorld(float2 px, float depth, float2 size, float4x4 invViewProj)
{
    float2 uv = px / size;
    float4 clip = float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1);
    float4 w = mul(invViewProj, clip);
    return w.xyz / w.w;
}

float3 SceneNormal(float4 texel) { return normalize(texel.xyz * 2 - 1); }

// Статичный мир (пол, стены, колонны) принимает декали мира; персонажи и оружие — нет.
bool SceneReceivesDecals(float4 texel) { return texel.a > 0.5; }

// Запись во вторую цель непрозрачной фазы.
float4 SceneNormalOut(float3 n, bool receiver) { return float4(normalize(n) * 0.5 + 0.5, receiver ? 1 : 0); }

#endif
