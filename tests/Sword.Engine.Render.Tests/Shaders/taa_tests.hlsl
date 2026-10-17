// Проверка TAA (TaaGpuTests): пол y = 0 в полоску по x (полосы шириной 0.37 м: тёмная и светлая), статичный мир. PsStripes лучом
// из глаза через матрицу кадра (со сдвигом TAA) пишет цвет HDR-сцены, нормаль (приёмник декалей - статичный мир) и глубину.
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"

struct StripesFrame
{
    float4x4 viewProj;
    float4x4 invViewProj;
    float4x4 velocityViewProj;   // без сдвига TAA - скорости
    float3 eye; float pad;
};
ConstantBuffer<StripesFrame> gStripes : register(b0);

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

struct StripesOut { float4 colour : SV_Target0; float4 normal : SV_Target1; float depth : SV_Depth; };

StripesOut PsStripes(float4 pos : SV_Position)
{
    float2 size = float2(gPass0, gPass1);
    float3 far = SceneWorld(pos.xy, 0.0001, size, gStripes.invViewProj);
    float3 dir = normalize(far - gStripes.eye);
    StripesOut o;
    o.normal = SceneNormalOut(float3(0, 1, 0), true);
    if (dir.y >= 0)
    {
        o.colour = float4(0.02, 0.02, 0.02, 1);
        o.depth = 0;
        return o;
    }
    float3 p = gStripes.eye + dir * (-gStripes.eye.y / dir.y);
    float4 c = mul(gStripes.viewProj, float4(p, 1));
    o.depth = c.z / c.w;
    // Полосы едут по полу: gPass2 - сдвиг этого кадра, м (gPass3 - прошлого, для скоростей).
    o.colour = frac((p.x - asfloat(gPass2)) / 0.74) < 0.5 ? float4(0.04, 0.04, 0.04, 1) : float4(0.8, 0.8, 0.8, 1);
    return o;
}

// Скорости пола (TaaGpuTests, цель скоростей сцены): точка полосы в прошлом кадре - на (сдвиг этого - сдвиг прошлого) левее.
struct VelocityOut { float2 velocity : SV_Target0; float depth : SV_Depth; };

VelocityOut PsStripesVelocity(float4 pos : SV_Position)
{
    float2 size = float2(gPass0, gPass1);
    float3 far = SceneWorld(pos.xy, 0.0001, size, gStripes.invViewProj);
    float3 dir = normalize(far - gStripes.eye);
    VelocityOut o;
    o.velocity = 0;
    o.depth = 0;
    if (dir.y >= 0) return o;
    float3 p = gStripes.eye + dir * (-gStripes.eye.y / dir.y);
    float4 c = mul(gStripes.viewProj, float4(p, 1));
    o.depth = c.z / c.w;
    float4 now = mul(gStripes.velocityViewProj, float4(p, 1));
    float4 before = mul(gStripes.velocityViewProj, float4(p - float3(asfloat(gPass2) - asfloat(gPass3), 0, 0), 1));
    o.velocity = (now.xy / now.w - before.xy / before.w) * float2(0.5, -0.5);
    return o;
}
