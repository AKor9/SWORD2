// Точечный свет кадра (PointLights.cs — раскладка, CPU-эталон формул PointLightMath): диффузный вклад по формулам UE 5.8 —
// затухание (экспонента или обратный квадрат с окном), Ламберт (цвет/pi) x N.L, light function «две шумовые текстуры».
// Буфер кадра: элемент 0 — заголовок (count), источники — элементы 1..count. Шейдер сцены умножает итог на диффузный цвет.
#ifndef SWORD_LIGHTS_HLSLI
#define SWORD_LIGHTS_HLSLI
#include "pass.hlsli"

struct PointLightData
{
    float3 position; float invRadiusSq;
    float3 color; float exponent;          // color = цвет x интенсивность x угасание по дальности показа
    uint flags; uint textureA; uint textureB; float lfFadeDistance;
    float lfScaleA; float lfScaleB; float lfDisabled; uint count;
    float2 lfOffsetA; float2 lfOffsetB;
    float4 lfToLocal;                      // rotation world -> LF axes (quaternion xyzw)
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

float3 PointLightDiffuse(PointLightData l, float3 n, float3 world, float3 eye)
{
    float3 toLight = l.position - world;
    float distSq = dot(toLight, toLight);
    float att = PointLightAttenuation(distSq, l.invRadiusSq, l.exponent, (l.flags & POINT_LIGHT_INVERSE_SQUARED) != 0);
    if (att <= 0 || distSq <= 0) return 0;
    float3 L = toLight * rsqrt(distSq);
    float ndl = saturate(dot(n, L));
    if (ndl > 0 && (l.flags & POINT_LIGHT_FUNCTION) != 0) att *= LightFunctionValue(l, -L, distance(world, eye));
    return l.color * (att * ndl);
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
