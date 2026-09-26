// Общее для шейдеров сцены арены: константы кадра (FrameConstants.cs), хэш и шум, поворот кватернионом.
// Перенос sim_common.hlsli SWORD 2eab13b — без буферов симуляции и без функции рельефа: рельеф — поле высот R_TERRAIN.
#ifndef SWORD_SCENE_COMMON
#define SWORD_SCENE_COMMON
#include "common/pass.hlsli"

// Окружение сцены (Scene/SceneEnvironment.cs): цвета освещения Shade и фон; день или ночь выбирает клиент.
struct SceneEnvironment
{
    float3 sun; float pad0;
    float3 skyLow; float pad1;
    float3 skyHigh; float pad2;
    float3 rim; float pad3;
    float3 fog; float pad4;
    float3 background; float pad5;
};

// Константы кадра — одна структура, её размер сверяется с C# (FRAME_CONSTANTS_SIZE). Поля — строками по 16 байт:
// так плотная раскладка (sizeof) совпадает с упаковкой константного буфера. Тексты проверок — ASCII: DXC получает
// исходник в кодировке ANSI и на кириллицу в строковых литералах предупреждает.
struct FrameConstants
{
    float dt; float time; uint tick; uint active;
    float3 playerPos; float alpha;
    float4x4 viewProj;
    float3 cameraPos; float arenaRadius;
    float4 pillars[PILLAR_COUNT];
    float3 lightDir; float playerHpFrac;
    float4x4 invViewProj;
    uint pointLights; uint pad0; uint pad1; uint pad2;
    SceneEnvironment env;
};
_Static_assert(sizeof(FrameConstants) == FRAME_CONSTANTS_SIZE, "FrameConstants layout differs from C# (Scene/FrameConstants.cs)");

ConstantBuffer<FrameConstants> gFrame : register(b0);

uint Hash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
    return x;
}
float Rand(uint seed) { return (Hash(seed) & 0xFFFFFFu) / 16777216.0; }

// ── шум значений: один на всех (пол, позже срез мяса, кишки) ───────────────
// Случайное число в узле целочисленной решётки, сглаженная интерполяция между узлами; масштаб задаёт вызывающий.

float LatticeRand(int3 c)
{
    return Rand(uint(c.x + 512) * 73856093u ^ uint(c.y + 512) * 19349663u ^ uint(c.z + 512) * 83492791u);
}

/// Плоский шум: <paramref name="wrapY"/> > 0 — по y он повторяется с этим периодом (развёртка по кругу без шва).
float ValueNoise(float2 p, int wrapY = 0)
{
    float2 i = floor(p), f = p - i;
    f = f * f * (3 - 2 * f);
    int x = int(i.x), y0 = int(i.y), y1 = y0 + 1;
    if (wrapY > 0)
    {
        y0 = (y0 % wrapY + wrapY) % wrapY;
        y1 = (y1 % wrapY + wrapY) % wrapY;
    }
    float a = lerp(LatticeRand(int3(x, y0, 0)), LatticeRand(int3(x + 1, y0, 0)), f.x);
    float b = lerp(LatticeRand(int3(x, y1, 0)), LatticeRand(int3(x + 1, y1, 0)), f.x);
    return lerp(a, b, f.y);
}

float3 QuatRotate(float4 q, float3 v)
{
    float3 t = 2 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

// Гладкая ступенька для подкраски участков рельефа — та же, что в ArenaTerrain.Smooth.
float TSmooth(float a, float b, float x)
{
    float t = saturate((x - a) / (b - a));
    return t * t * (3.0 - 2.0 * t);
}

#endif
