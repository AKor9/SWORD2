// Проверка GTAO (AoGpuTests): аналитический угол - пол y = 0 (нормаль +y) и стена z = 0 (нормаль +z), занято y < 0 и z < 0.
// PsCorner лучом из глаза пишет глубину (SV_Depth, обратный Z) и нормаль сцены; PsShowAo выводит видимость карты AO серым.
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"

struct CornerFrame
{
    float4x4 viewProj;
    float4x4 invViewProj;
    float3 eye; float pad;
};
ConstantBuffer<CornerFrame> gCorner : register(b0);

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

struct CornerOut { float4 normal : SV_Target0; float depth : SV_Depth; };

CornerOut PsCorner(float4 pos : SV_Position)
{
    float2 size = float2(gPass0, gPass1);
    float3 far = SceneWorld(pos.xy, 0.0001, size, gCorner.invViewProj);
    float3 dir = normalize(far - gCorner.eye);
    float tFloor = dir.y < 0 ? -gCorner.eye.y / dir.y : 1e9;
    float tWall = dir.z < 0 ? -gCorner.eye.z / dir.z : 1e9;
    float3 n = float3(0, 1, 0);
    float t = tFloor;
    float3 hit = gCorner.eye + dir * tFloor;
    if (hit.z < 0) { t = tWall; n = float3(0, 0, 1); }
    CornerOut o;
    float3 p = gCorner.eye + dir * t;
    float4 c = mul(gCorner.viewProj, float4(p, 1));
    o.depth = t < 1e8 ? c.z / c.w : 0;
    o.normal = SceneNormalOut(n, false);
    return o;
}

float4 PsShowAo(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float2> ao = ResourceDescriptorHeap[gPass0];
    return float4(ao.Load(int3(pos.xy, 0)).rrr, 1);
}
