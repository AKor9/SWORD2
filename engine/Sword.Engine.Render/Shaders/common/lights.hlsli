// Точечный свет кадра (PointLights.cs — раскладка, CPU-эталон формул PointLightMath): диффузный вклад по формулам UE 5.8 —
// затухание (экспонента или обратный квадрат с окном), Ламберт (цвет/pi) x N.L, light function «две шумовые текстуры».
// Буфер кадра: элемент 0 — заголовок (count), источники — элементы 1..count. Шейдер сцены умножает итог на диффузный цвет.
#ifndef SWORD_LIGHTS_HLSLI
#define SWORD_LIGHTS_HLSLI
#include "pass.hlsli"
#include "surface.hlsli"

struct PointLightData
{
    float3 position; float invRadiusSq;
    float3 color; float exponent;          // color = цвет x интенсивность x угасание по дальности показа
    uint flags; uint textureA; uint textureB; float lfFadeDistance;
    float lfScaleA; float lfScaleB; float lfDisabled; uint count;
    float2 lfOffsetA; float2 lfOffsetB;
    float4 lfToLocal;                      // rotation world -> LF axes (quaternion xyzw)
    uint shadow0; uint shadow1; uint shadow2; uint shadow3;   // SRV граней кубической карты теней (+X -X +Y -Y +Z -Z) или POINT_SHADOW_NONE
    uint shadow4; uint shadow5; uint shadowPad0; uint shadowPad1;
};
_Static_assert(sizeof(PointLightData) == POINT_LIGHT_SIZE, "PointLightData layout differs from C# (PointLights.cs GpuPointLight)");

// Затухание по d^2: экспонента - (1 - saturate(d^2/R^2))^exp; обратный квадрат - saturate(1 - (d^2/R^2)^2)^2 / (d^2 + 1 см^2).
// За радиусом - 0 (и без pow(0, 0)).
float PointLightAttenuation(float distSq, float invRadiusSq, float exponent, bool inverseSquared)
{
    float x = distSq * invRadiusSq;
    if (inverseSquared)
    {
        float w = saturate(1 - x * x);
        return w * w / (distSq + POINT_LIGHT_BIAS_SQ);
    }
    float b = 1 - saturate(x);
    return b > 0 ? pow(b, exponent) : 0;
}

// UV light function по единичному направлению от источника к точке (оси SWORD2): сферические углы UE после перевода осей
// UE (x, y, z) = SWORD2 (z, -x, y) и перестановки zyx - u = (atan2(-x, y) + pi)/2pi, v = acos(z)/pi.
float2 LightFunctionUv(float3 dir)
{
    return float2((atan2(-dir.x, dir.y) + POINT_LIGHT_PI) / (2 * POINT_LIGHT_PI), acos(clamp(dir.z, -1, 1)) / POINT_LIGHT_PI);
}

// Поворот вектора кватернионом q (xyzw); единичный q = (0, 0, 0, 1) возвращает v бит в бит.
float3 RotateByQuaternion(float4 q, float3 v)
{
    return v + 2 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

// Light function: UV направления в осях LF, A.r(uv*sA + сдвиг A) + B.r(uv*sB + сдвиг B) (сдвиги по времени считает CPU), к яркости
// без LF в последних 20 % дальности угасания от камеры, в пределах 0..1. Мип 0: на шве atan2 производные UV скачут, и выбор мипа дал
// бы полосу.
float LightFunctionValue(PointLightData l, float3 dir, float viewDistance)
{
    float2 uv = LightFunctionUv(RotateByQuaternion(l.lfToLocal, dir));
    Texture2DArray<float4> a = ResourceDescriptorHeap[l.textureA];
    Texture2DArray<float4> b = ResourceDescriptorHeap[l.textureB];
    float sum = a.SampleLevel(gLinearWrap, float3(uv * l.lfScaleA + l.lfOffsetA, 0), 0).r
        + b.SampleLevel(gLinearWrap, float3(uv * l.lfScaleB + l.lfOffsetB, 0), 0).r;
    float fade = saturate((l.lfFadeDistance - viewDistance) / (l.lfFadeDistance * POINT_LIGHT_LF_BAND));
    return saturate(lerp(l.lfDisabled, sum, fade));
}

// Грань кубической карты теней: (u, v, a) - оси грани, a - вдоль её оси (глубина), d - от источника к точке.
float3 PointShadowAxes(float3 d, uint face)
{
    switch (face)
    {
        case 0: return float3(-d.z, d.y, d.x);
        case 1: return float3(d.z, d.y, -d.x);
        case 2: return float3(d.x, -d.z, d.y);
        case 3: return float3(d.x, d.z, -d.y);
        case 4: return float3(d.x, d.y, d.z);
        default: return float3(-d.x, d.y, -d.z);
    }
}

// Вершина заслоняющего в грани (PointLights.AddShadowPasses): перспектива 90 градусов, обратный Z - глубина near / a.
float4 PointShadowClip(float3 world)
{
    float3 a = PointShadowAxes(world - float3(asfloat(POINT_SHADOW_X), asfloat(POINT_SHADOW_Y), asfloat(POINT_SHADOW_Z)), POINT_SHADOW_FACE);
    return float4(a.x, a.y, POINT_SHADOW_NEAR, a.z);
}

// Тень источника в точке: грань - наибольшая ось d; точка сдвинута по нормали на 1.5 текселя грани и к источнику на POINT_SHADOW_BIAS;
// 4 выборки сравнения (каждая билинейная) в полутекселе - мягкий край. За гранью и ближе near - свет.
float PointShadow(PointLightData l, float3 d, float3 n)
{
    float3 m = abs(d);
    float reach = max(m.x, max(m.y, m.z));
    d += n * (2 * reach / POINT_SHADOW_SIZE * 1.5);
    m = abs(d);
    uint face = m.x >= m.y && m.x >= m.z ? (d.x >= 0 ? 0 : 1) : m.y >= m.z ? (d.y >= 0 ? 2 : 3) : (d.z >= 0 ? 4 : 5);
    uint srv = face == 0 ? l.shadow0 : face == 1 ? l.shadow1 : face == 2 ? l.shadow2 : face == 3 ? l.shadow3 : face == 4 ? l.shadow4 : l.shadow5;
    float3 a = PointShadowAxes(d, face);
    if (a.z <= POINT_SHADOW_NEAR) return 1;
    float2 uv = a.xy / a.z * float2(0.5, -0.5) + 0.5;
    float depth = POINT_SHADOW_NEAR / max(a.z - POINT_SHADOW_BIAS, POINT_SHADOW_NEAR);
    Texture2D<float> map = ResourceDescriptorHeap[NonUniformResourceIndex(srv)];   // грань - своя у соседних пикселей
    float t = 0.5 / POINT_SHADOW_SIZE;
    return 0.25 * (map.SampleCmpLevelZero(gShadowCmp, uv + float2(-t, -t), depth) + map.SampleCmpLevelZero(gShadowCmp, uv + float2(t, -t), depth)
        + map.SampleCmpLevelZero(gShadowCmp, uv + float2(-t, t), depth) + map.SampleCmpLevelZero(gShadowCmp, uv + float2(t, t), depth));
}

float3 PointLightDiffuse(PointLightData l, float3 n, float3 world, float3 eye)
{
    float3 toLight = l.position - world;
    float distSq = dot(toLight, toLight);
    float att = PointLightAttenuation(distSq, l.invRadiusSq, l.exponent, (l.flags & POINT_LIGHT_INVERSE_SQUARED) != 0);
    if (att <= 0 || distSq <= 0) return 0;
    float3 L = toLight * rsqrt(distSq);
    float ndl = saturate(dot(n, L));
    if (ndl > 0 && (l.flags & POINT_LIGHT_FUNCTION) != 0) att *= LightFunctionValue(l, -L, distance(world, eye));
    if (ndl > 0 && l.shadow0 != POINT_SHADOW_NONE) att *= PointShadow(l, world - l.position, n);
    return l.color * (att * ndl);
}

// Блик источника (GGX, f0 цветной - металл): то же затухание, light function и тень, что у рассеянного; v - на камеру.
float3 PointLightSpecular(PointLightData l, float3 n, float3 v, float3 world, float3 eye, float3 f0, float roughness)
{
    float3 toLight = l.position - world;
    float distSq = dot(toLight, toLight);
    float att = PointLightAttenuation(distSq, l.invRadiusSq, l.exponent, (l.flags & POINT_LIGHT_INVERSE_SQUARED) != 0);
    if (att <= 0 || distSq <= 0) return 0;
    float3 L = toLight * rsqrt(distSq);
    if (dot(n, L) <= 0) return 0;
    if ((l.flags & POINT_LIGHT_FUNCTION) != 0) att *= LightFunctionValue(l, -L, distance(world, eye));
    if (l.shadow0 != POINT_SHADOW_NONE) att *= PointShadow(l, world - l.position, n);
    return l.color * att * SurfaceDirectSpecular(n, v, L, f0, roughness);
}

// Сумма бликов источников буфера srv (единицы - как у PointLightsDiffuse x диффузный цвет: прибавка к цвету сцены).
float3 PointLightsSpecular(uint srv, float3 n, float3 v, float3 world, float3 eye, float3 f0, float roughness)
{
    StructuredBuffer<PointLightData> lights = ResourceDescriptorHeap[srv];
    uint count = min(lights[0].count, POINT_LIGHTS_MAX);
    float3 sum = 0;
    for (uint i = 1; i <= count; i++) sum += PointLightSpecular(lights[i], n, v, world, eye, f0, roughness);
    return sum;
}

// Сумма источников буфера srv для точки world с нормалью n (eye - камера, для угасания LF): множитель диффузного цвета.
// Кадр без источников - ровно 0.
float3 PointLightsDiffuse(uint srv, float3 n, float3 world, float3 eye)
{
    StructuredBuffer<PointLightData> lights = ResourceDescriptorHeap[srv];
    uint count = min(lights[0].count, POINT_LIGHTS_MAX);
    float3 sum = 0;
    for (uint i = 1; i <= count; i++) sum += PointLightDiffuse(lights[i], n, world, eye);
    return sum * (1 / POINT_LIGHT_PI);
}

#endif
