// Test passes of the fire smoke (FireSmokeGpuTests): a scene with a known linear depth (reverse Z of an infinite projection:
// depth = near / z) - a ramp by column and an optional rectangle at its own depth. The gas density is a storage the test fills.
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

// Scene: colour gPass1..3 (float), linear depth by column from gPass5 to gPass6 (m) at near gPass4, gPass7 - target width;
// rectangle gPass8..11 (pixels x0, y0, x1, y1; empty - none) at depth gPass12 (m) with colour gPass13..15.
SceneOut PsScene(float4 pos : SV_Position)
{
    float z = asfloat(gPass5) + (asfloat(gPass6) - asfloat(gPass5)) * (floor(pos.x) + 0.5) / gPass7;
    float3 colour = float3(asfloat(gPass1), asfloat(gPass2), asfloat(gPass3));
    uint2 p = uint2(pos.xy);
    if (p.x >= gPass8 && p.y >= gPass9 && p.x < gPass10 && p.y < gPass11)
    {
        z = asfloat(gPass12);
        colour = float3(asfloat(gPass13), asfloat(gPass14), asfloat(gPass15));
    }
    SceneOut o;
    o.color = float4(colour, 1);
    o.depth = asfloat(gPass4) / z;
    return o;
}
