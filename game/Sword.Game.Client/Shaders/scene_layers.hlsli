// Blood layers of the scene skin shader (scene.hlsl): the wet layer of a skin vertex and blood on a weapon blade. Separate from
// scene.hlsl so the GPU test (BloodLayerGpuTests) checks the unpacking against SkinLayerTexel / WeaponBloodLayout in C#.
#ifndef SCENE_LAYERS_HLSLI
#define SCENE_LAYERS_HLSLI

// One SkinLayerTexel: x = A in R11G11B10F (half bits without the sign), y = M unorm7 x 3 | coverage unorm5 << 21 | roughness unorm6 << 26.
void UnpackLayer(uint2 u, out float4 add, out float4 mul)
{
    add = float4(f16tof32((u.x & 0x7FF) << 4), f16tof32(((u.x >> 11) & 0x7FF) << 4), f16tof32((u.x >> 22) << 5), ((u.y >> 21) & 0x1F) / 31.0);
    mul = float4((u.y & 0x7F) / 127.0, ((u.y >> 7) & 0x7F) / 127.0, ((u.y >> 14) & 0x7F) / 127.0, (u.y >> 26) / 63.0);
}

// Wet layer of a vertex (ISkinBloodLayer): inst.owner = bindless SRV of the layer + 1 (0 - no layer); a SkinLayerTexel per mesh vertex.
// The layer over the skin: albedo = skin * M + A. No layer: A = 0, M = 1, coverage 0.
void SkinLayerOf(uint owner, uint vid, out float4 add, out float4 mul)
{
    add = 0;
    mul = float4(1, 1, 1, 1);
    if (owner == 0) return;
    StructuredBuffer<uint2> layer = ResourceDescriptorHeap[owner - 1];
    uint count, stride;
    layer.GetDimensions(count, stride);
    if (vid >= count) return;   // vertices added by a mesh cut (dismemberment) have no layer texel
    UnpackLayer(layer[vid], add, mul);
}

// Value noise of a point (smooth, 0...1) - stains on the blade without straight bands.
float WeaponNoiseHash(float2 c) { return frac(sin(dot(c, float2(127.1, 311.7))) * 43758.5453); }
float WeaponNoise(float2 p)
{
    float2 c = floor(p), f = frac(p);
    float2 s = f * f * (3 - 2 * f);
    float a = WeaponNoiseHash(c), b = WeaponNoiseHash(c + float2(1, 0)), d = WeaponNoiseHash(c + float2(0, 1)), e = WeaponNoiseHash(c + 1);
    return lerp(lerp(a, b, s.x), lerp(d, e, s.x), s.y);
}

// Blood on a weapon blade (IWeaponBloodLayer, WeaponBloodLayout in C#): owner = SRV + 1; header - the blade in mesh space (start, cells
// per side, length axis / length, width axis / half width, plane normal, noise seed), then cells along the blade, side 0 (along the
// normal) and side 1, each SkinLayerTexel. Between cells - linear; the position along the blade is warped by noise varying across the
// width (stains end ragged, not in bands across the blade), partial coverage (thin film) is dissolved by streak noise along the blade.
// Off the blade (guard, grip) - none.
void WeaponLayerOf(uint owner, float3 p, out float4 add, out float4 mul)
{
    add = 0;
    mul = float4(1, 1, 1, 1);
    StructuredBuffer<uint2> layer = ResourceDescriptorHeap[owner - 1];
    uint2 h0 = layer[0], h1 = layer[1], h2 = layer[2], h3 = layer[3], h4 = layer[4], h5 = layer[5], h6 = layer[6];
    uint cells = h1.y;
    float3 d = p - asfloat(uint3(h0.x, h0.y, h1.x));
    float u = dot(d, asfloat(uint3(h2.x, h2.y, h3.x)));
    float v = dot(d, asfloat(uint3(h3.y, h4.x, h4.y)));
    if (cells == 0 || u < 0 || u > 1) return;
    uint side = dot(d, asfloat(uint3(h5.x, h5.y, h6.x))) >= 0 ? 0 : 1;
    float2 seed = float2(h6.y & 0xFF, (h6.y >> 8) & 0xFF) * 0.37;
    float warp = WeaponNoise(float2(u * cells * 0.3, v * 3) + seed) - 0.5;
    float f = clamp(u * cells - 0.5 + warp * 4, 0, cells - 1);
    uint i0 = (uint)floor(f), i1 = min(i0 + 1, cells - 1);
    float t = f - i0;
    uint first = WEAPON_BLOOD_HEADER + side * cells;
    float4 a0, m0, a1, m1;
    UnpackLayer(layer[first + i0], a0, m0);
    UnpackLayer(layer[first + i1], a1, m1);
    add = lerp(a0, a1, t);
    mul = lerp(m0, m1, t);
    // Thin film (partial coverage) - streaks along the blade: long along it, narrow across.
    float edge = 0.7 * WeaponNoise(float2(u * cells * 0.2, v * 5) + seed + 11.3) + 0.3 * WeaponNoise(float2(u * cells * 1.5, v * 14) + seed + 5.1);
    float keep = saturate((add.a - 0.85 * edge) / 0.15) * saturate(u * cells * 2) * saturate((1 - u) * cells * 4);
    add *= keep;
    mul.rgb = lerp(1, mul.rgb, keep);
}

#endif
