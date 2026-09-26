// Освещение сцены: общее для непрозрачного (scene.hlsl) и того, что рисуется поверх (кровь).
#ifndef SWORD_LIGHTING_HLSLI
#define SWORD_LIGHTING_HLSLI
#include "scene_common.hlsli"
#include "common/lights.hlsli"

// Shade даёт линейный цвет сцены (HDR): на экран его переводит тонмап рендера, кодирования здесь нет. Цвета солнца, неба, контура и
// тумана - окружение кадра (gFrame.env: день или ночь), формулы от окружения не зависят.

float3 Shade(float3 albedo, float3 n, float3 world, float ao)
{
    float3 l = normalize(-gFrame.lightDir);
    float ndl = saturate(dot(n, l));
    // lerp(низ, зенит, n.y * 0.5 + 0.5), записанный как середина + n.y x половина: так его сворачивал компилятор, пока цвета неба
    // были константами шейдера, - дневной кадр бит в бит прежний (lerp с цветами из констант кадра расходится в 1 ULP half).
    float3 skyHalf = (gFrame.env.skyHigh - gFrame.env.skyLow) * 0.5;
    float3 sky = (gFrame.env.skyLow + skyHalf) + n.y * skyHalf;
    float3 v = normalize(gFrame.cameraPos - world);
    float rim = pow(1 - saturate(dot(n, v)), 3) * 0.25;
    float3 c = albedo * (ndl * gFrame.env.sun + sky * ao) + rim * gFrame.env.rim * ao;
    // Туман по расстоянию: начало и длина - SceneEnvironment.FogStart/FogRange (по ним же туман прозрачного модулей).
    float fog = saturate((distance(world, gFrame.cameraPos) - 25) / 140);
    // Точечный свет (факел и т. п.) - Ламберт UE: альбедо/pi x сумма источников (солнце выше - без 1/pi: его 1.25 = UE-свет pi x 1.25),
    // под тем же туманом: lerp(c + p, туман, f) = lerp(c, туман, f) + (1 - f) p. Без источников p = 0 - прежний цвет бит в бит.
    // ao гасит только рассеянный свет (небо, контур); источники - прямой свет, как солнце, и на ao не умножаются (так и в UE:
    // затенение прямого света - дело теней, а не ao).
    return lerp(c, gFrame.env.fog, fog) + (1 - fog) * albedo * PointLightsDiffuse(gFrame.pointLights, n, world, gFrame.cameraPos);
}

/// Резкий блик мокрой поверхности (кожа в крови, срез, кишки): Блинн по направлениям на свет и на камеру.
/// <paramref name="power"/> — резкость, силу задаёт вызывающий.
float WetHighlight(float3 n, float3 world, float power)
{
    float3 h = normalize(normalize(gFrame.cameraPos - world) + normalize(-gFrame.lightDir));
    return pow(saturate(dot(n, h)), power);
}

#endif
