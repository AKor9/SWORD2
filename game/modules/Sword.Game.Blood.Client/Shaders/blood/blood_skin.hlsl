// Blood on skin (BloodSkinGpu.cs): a buffer per fighter slot, uint2 elements - [0, V) the look of a mesh vertex (SkinLayerTexel: the skin
// shader of the game reads it), [V, 2V) the raw state of the vertex (asfloat: thickness, mm; birth time, s - the age is now - birth).
// CsClear zeroes a slot, CsScatter writes the raw elements changed on the CPU, CsLook turns raw into the look by the freshness function.
#include "common/pass.hlsli"
#include "blood/blood_look.hlsli"

// Root constants (BloodSkinGpu.Contribute): 0...18 - the look numbers, gSkinTarget, gSkinCount, gSkinEntries, gSkinFirst, gSkinNow.

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

uint UnormBits(float v, float max) { return uint(saturate(v) * max + 0.5); }
uint Float11(float v) { return (f32tof16(max(v, 0)) >> 4) & 0x7FF; }
uint Float10(float v) { return (f32tof16(max(v, 0)) >> 5) & 0x3FF; }

// The look of a vertex - SkinLayerTexel: x = the layer's own albedo A (R11G11B10F), y = the substrate multiplier M (unorm7 x 3),
// coverage (unorm5), roughness (unorm6). The skin shader: albedo = skin * M + A, the highlight x coverage.
[numthreads(BLOOD_SKIN_GROUP, 1, 1)]
void CsLook(uint3 id : SV_DispatchThreadID)
{
    uint vertices = gSkinCount;
    if (id.x >= vertices) return;
    RWStructuredBuffer<uint2> target = ResourceDescriptorHeap[gSkinTarget];
    uint2 raw = target[vertices + id.x];
    float thickness = asfloat(raw.x);
    // A vertex without blood gets the neutral look (M = 1, A = 0, coverage 0, roughness of fresh blood), not zeros: the skin shader
    // interpolates the look across triangles, and M = 0 there drew a dark outline around every stain.
    float age = thickness > 0 ? max(asfloat(gSkinNow) - asfloat(raw.y), 0) : 0;
    BloodLookResult look = BloodLookEval(age, max(thickness, 0), BloodLookFromPass());
    float3 add = BloodAdd(look), mul = BloodMul(look);
    uint x = Float11(add.r) | (Float11(add.g) << 11) | (Float10(add.b) << 22);
    uint y = UnormBits(mul.r, 127) | (UnormBits(mul.g, 127) << 7) | (UnormBits(mul.b, 127) << 14) | (UnormBits(look.coverage, 31) << 21)
        | (UnormBits(look.roughness, 63) << 26);
    target[id.x] = uint2(x, y);
}
