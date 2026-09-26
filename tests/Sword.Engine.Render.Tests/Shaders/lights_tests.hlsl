// Проверочные проходы точечного света (PointLightsGpuTests): плоскость y = 0 под источниками кадра и UV light function.
#include "common/lights.hlsli"

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// gPass0 - SRV буфера источников; gPass1..3 - глаз (float); gPass4 - метров на пиксель (float): пиксель (x, y) - точка
// ((x + 0.5 - W/2) * шаг, 0, (y + 0.5 - H/2) * шаг), нормаль +Y; gPass5, gPass6 - ширина и высота цели.
float4 PsPlane(float4 pos : SV_Position) : SV_Target0
{
    float step = asfloat(gPass4);
    float3 world = float3((pos.x - 0.5 * gPass5) * step, 0, (pos.y - 0.5 * gPass6) * step);
    float3 eye = float3(asfloat(gPass1), asfloat(gPass2), asfloat(gPass3));
    return float4(PointLightsDiffuse(gPass0, float3(0, 1, 0), world, eye), 1);
}

// Поворот осей LF в проверке: тот же кватернион (xyzw, единичный) - в PointLightsGpuTests.
static const float4 TestRotation = float4(0.2, -0.5, 0.3, 0.78740079);

// UV light function: пиксель x - направление x из хранилища gPass7 (float4, xyz); выход - (u, v) направления и (u, v) направления,
// повёрнутого TestRotation.
float4 PsUv(float4 pos : SV_Position) : SV_Target0
{
    StructuredBuffer<float4> dirs = ResourceDescriptorHeap[gPass7];
    float3 dir = normalize(dirs[uint(pos.x)].xyz);
    return float4(LightFunctionUv(dir), LightFunctionUv(RotateByQuaternion(TestRotation, dir)));
}
