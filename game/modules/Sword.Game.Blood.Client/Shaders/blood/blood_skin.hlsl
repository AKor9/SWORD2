// Blood on skin (BloodSkinGpu.cs): a buffer per fighter slot, uint2 elements - [0, V) the look of a mesh vertex (SkinLayerTexel: the skin
// shader of the game reads it), [V, 2V) the raw state of the vertex (asfloat: thickness, mm; birth time, s - the age is now - birth).
// CsClear zeroes a slot, CsScatter writes the raw elements changed on the CPU, CsLook turns raw into the look by the freshness function.
#include "common/pass.hlsli"
#include "blood/blood_look.hlsli"

// Root constants (BloodSkinGpu.Contribute): 0...17 - the look numbers, gSkinTarget, gSkinCount, gSkinEntries, gSkinFirst, gSkinNow.

[numthreads(BLOOD_SKIN_GROUP, 1, 1)]
void CsClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gSkinCount) return;
    RWStructuredBuffer<uint2> target = ResourceDescriptorHeap[gSkinTarget];
    target[id.x] = uint2(0, 0);
}

// Entry: x - the element, y, z - the two words of it.
[numthreads(BLOOD_SKIN_GROUP, 1, 1)]
void CsScatter(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gSkinCount) return;
    StructuredBuffer<uint4> entries = ResourceDescriptorHeap[gSkinEntries];
    RWStructuredBuffer<uint2> target = ResourceDescriptorHeap[gSkinTarget];
    uint4 e = entries[gSkinFirst + id.x];
    target[e.x] = e.yz;
}

uint Unorm8(float v) { return uint(saturate(v) * 255 + 0.5); }

[numthreads(BLOOD_SKIN_GROUP, 1, 1)]
void CsLook(uint3 id : SV_DispatchThreadID)
{
    uint vertices = gSkinCount;
    if (id.x >= vertices) return;
    RWStructuredBuffer<uint2> target = ResourceDescriptorHeap[gSkinTarget];
    uint2 raw = target[vertices + id.x];
    float thickness = asfloat(raw.x);
    if (!(thickness > 0)) return;
    BloodLookResult look = BloodLookEval(max(asfloat(gSkinNow) - asfloat(raw.y), 0), thickness, BloodLookFromPass());
    uint x = f32tof16(look.albedo.r) | (f32tof16(look.albedo.g) << 16);
    uint y = f32tof16(look.albedo.b) | (Unorm8(look.coverage) << 16) | (Unorm8(look.roughness) << 24);
    target[id.x] = uint2(x, y);
}
