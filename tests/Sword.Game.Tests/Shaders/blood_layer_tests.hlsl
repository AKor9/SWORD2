// Test passes of the blood layers (BloodLayerGpuTests): the scene skin shader's unpacking (scene_layers.hlsli) against SkinLayerTexel and
// WeaponBloodLayout in C#, and the floor film helpers of PsFloor (blood_floor.hlsl) on a small window and map (the test contract: 8^2 texels).
// Common: gPass20 - layer SRV + 1 (0 - none), gPass21 - output, gPass22 - count, gPass23 - input points (float4).
#include "blood/blood_floor.hlsl"
#include "scene_layers.hlsli"

// Skin: output float4 pairs (add; mul) per vertex.
[numthreads(64, 1, 1)]
void CsSkinLayer(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gPass22) return;
    RWStructuredBuffer<float4> output = ResourceDescriptorHeap[gPass21];
    float4 add, mul;
    SkinLayerOf(gPass20, id.x, add, mul);
    output[2 * id.x] = add;
    output[2 * id.x + 1] = mul;
}

// Weapon: points in mesh space (xyz) -> float4 pairs (add; mul).
[numthreads(64, 1, 1)]
void CsWeaponLayer(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gPass22) return;
    StructuredBuffer<float4> points = ResourceDescriptorHeap[gPass23];
    RWStructuredBuffer<float4> output = ResourceDescriptorHeap[gPass21];
    float4 add, mul;
    WeaponLayerOf(gPass20, points[id.x].xyz, add, mul);
    output[2 * id.x] = add;
    output[2 * id.x + 1] = mul;
}

// Floor: points (x, y - texel coordinates; z - thickness per rho, mm; w - meniscus, mm), gPass24 - map (uint), gPass25 - field (uint2)
// -> float4 pairs (map rho, map birth, field rho, profile slope; field gradient, 0, 0).
[numthreads(64, 1, 1)]
void CsFloorSample(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gPass22) return;
    StructuredBuffer<float4> points = ResourceDescriptorHeap[gPass23];
    StructuredBuffer<uint> map = ResourceDescriptorHeap[gPass24];
    StructuredBuffer<uint2> field = ResourceDescriptorHeap[gPass25];
    RWStructuredBuffer<float4> output = ResourceDescriptorHeap[gPass21];
    float4 q = points[id.x];
    float birth;
    float mapRho = MapSmooth(map, q.xy, birth);
    float2 grad;
    float fieldRho = FieldSmooth(field, q.xy, grad);
    output[2 * id.x] = float4(mapRho, birth, fieldRho, FilmProfileSlope(fieldRho, q.z, q.w));
    output[2 * id.x + 1] = float4(grad, 0, 0);
}
