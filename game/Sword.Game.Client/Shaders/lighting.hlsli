// Освещение сцены: общее для непрозрачного (scene.hlsl) и того, что рисуется поверх (кровь).
#ifndef SWORD_LIGHTING_HLSLI
#define SWORD_LIGHTING_HLSLI
#include "scene_common.hlsli"

static const float3 kFog = float3(0.10, 0.085, 0.08);

float3 Gamma(float3 c) { return pow(saturate(c), 1.0 / 2.2); }

float3 Shade(float3 albedo, float3 n, float3 world, float ao)
{
    float3 l = normalize(-gFrame.lightDir);
    float ndl = saturate(dot(n, l));
    float3 sky = lerp(float3(0.16, 0.12, 0.1), float3(0.32, 0.36, 0.45), n.y * 0.5 + 0.5);
    float3 v = normalize(gFrame.cameraPos - world);
    float rim = pow(1 - saturate(dot(n, v)), 3) * 0.25;
    float3 c = albedo * (ndl * float3(1.25, 1.1, 0.95) + sky * ao) + rim * float3(0.9, 0.7, 0.5) * ao;
    float fog = saturate((distance(world, gFrame.cameraPos) - 25) / 140);
    return lerp(c, kFog, fog);
}

/// Резкий блик мокрой поверхности (кожа в крови, срез, кишки): Блинн по направлениям на свет и на камеру.
/// <paramref name="power"/> — резкость, силу задаёт вызывающий.
float WetHighlight(float3 n, float3 world, float power)
{
    float3 h = normalize(normalize(gFrame.cameraPos - world) + normalize(-gFrame.lightDir));
    return pow(saturate(dot(n, h)), power);
}

#endif
