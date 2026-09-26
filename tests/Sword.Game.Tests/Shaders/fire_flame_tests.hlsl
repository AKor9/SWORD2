// Test passes of the fire flame (FireFlameGpuTests): a scene with a known linear depth per column (reverse Z of an infinite projection:
// depth = near / z) and a synthetic sim field output RGBA16F (v.xy, rho, wet) with values the test repeats on the CPU.
#include "common/pass.hlsli"

// Full-screen triangle.
float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

struct SceneOut
{
    float4 color : SV_Target0;
    float depth : SV_Depth;
};

// Scene: colour gPass1..3 (float), linear depth by column from gPass5 to gPass6 (m) at near gPass4, gPass7 - target width.
SceneOut PsScene(float4 pos : SV_Position)
{
    float z = asfloat(gPass5) + (asfloat(gPass6) - asfloat(gPass5)) * (floor(pos.x) + 0.5) / gPass7;
    SceneOut o;
    o.color = float4(asfloat(gPass1), asfloat(gPass2), asfloat(gPass3), 1);
    o.depth = asfloat(gPass4) / z;
    return o;
}

// Field: gPass0 - mode (0 - ramp: rho = 1.5 u - 0.25, v = (12 (v - 0.5), 6 (u - 0.5)); 1 - uniform rho 1, v = 0), gPass1, gPass2 - size.
float4 PsField(float4 pos : SV_Position) : SV_Target0
{
    float u = (floor(pos.x) + 0.5) / (float)gPass1;
    float v = (floor(pos.y) + 0.5) / (float)gPass2;
    if (gPass0 == 1) return float4(0, 0, 1, 1);
    return float4(12.0 * (v - 0.5), 6.0 * (u - 0.5), 1.5 * u - 0.25, 1);
}
