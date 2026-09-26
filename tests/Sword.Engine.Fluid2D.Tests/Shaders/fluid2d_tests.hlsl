// Test helpers for Fluid2D tests: copy an RGBA16F texture into a structured buffer of half bits (uint2 per texel).
#include "common/pass.hlsli"

[numthreads(8, 8, 1)]
void CsCopyTexture(uint3 id : SV_DispatchThreadID)
{
    Texture2D<float4> t = ResourceDescriptorHeap[gTestIn];
    RWStructuredBuffer<uint2> s = ResourceDescriptorHeap[gTestOut];
    if (id.x >= gTestSize || id.y >= gTestSize) return;
    float4 v = t.Load(int3(id.xy, 0));
    s[id.y * gTestSize + id.x] = uint2(f32tof16(v.x) | (f32tof16(v.y) << 16), f32tof16(v.z) | (f32tof16(v.w) << 16));
}

// Channel r of layer gTestLayer, mip 0 of a texture array as the sim reads it (Load, sRGB decoded by the hardware) -> float per texel.
[numthreads(8, 8, 1)]
void CsReadNoise(uint3 id : SV_DispatchThreadID)
{
    Texture2DArray<float4> t = ResourceDescriptorHeap[gTestIn];
    RWStructuredBuffer<float> s = ResourceDescriptorHeap[gTestOut];
    if (id.x >= gTestSize || id.y >= gTestSize) return;
    s[id.y * gTestSize + id.x] = t.Load(int4(id.xy, gTestLayer, 0)).r;
}

// All four channels of layer gTestLayer, mip 0, as the painter reads them (Load) -> float4 per texel.
[numthreads(8, 8, 1)]
void CsReadRgba(uint3 id : SV_DispatchThreadID)
{
    Texture2DArray<float4> t = ResourceDescriptorHeap[gTestIn];
    RWStructuredBuffer<float4> s = ResourceDescriptorHeap[gTestOut];
    if (id.x >= gTestSize || id.y >= gTestSize) return;
    s[id.y * gTestSize + id.x] = t.Load(int4(id.xy, gTestLayer, 0));
}
