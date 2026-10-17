// Blood on the floor (BloodFloorGpu.cs). The field of the window - Fluid2D C (uint2: half vx | vy, half rho | wet), rho is the thickness /
// h_max, the age - float per texel; the window is BLOOD_FLOOR_N^2 texels, texel (x, y) - x along +X, y along +Z. The map of dried stains -
// uint per texel: half rho | half birth time (s, floor clock minus the epoch), BLOOD_MAP_N^2 with the same texel.
// CsMapClear - zero the map; CsTransfer - texels of the window that leave it with the shift go to the map (before the step);
// CsMapStamp - drops that landed outside the window go straight to the map (one thread, in order); CsDry - dried blood of the window goes
// to the map and leaves the field (before the step, from the state between steps - the output of Advection);
// VsFull/PsFloor - Composite: the film over static receivers (map below, field above) as a layer over the scene: dual-source blending
// dst * mul + add - the floor shows through the blood darkened and reddened per channel (Beer-Lambert), wet floor around is darker;
// VsDrop/PsDrop - Opaque: flying drops as capsule impostors stretched along the velocity.
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"
#include "common/surface.hlsli"
#include "common/shadow.hlsli"
#include "blood/blood_look.hlsli"
#include "blood/blood_flight.hlsli"

// Root constants: 0...18 - the look numbers (BloodLook.Write); compute - gFloorC, gFloorAge, gFloorMap, gFloorShiftX, gFloorShiftZ,
// gFloorMapX, gFloorMapZ, gFloorNow (floor clock minus the epoch); map stamps - gFloorData (float4 x, z, volume), gFloorStampFirst,
// gFloorStampCount; dry - gFloorRho, gFloorDryAge, gFloorMinRho; draw - gFloorData (FloorConstants), gFloorDrops (DropGpu).

struct FloorConstants
{
    float4 viewProj0; float4 viewProj1; float4 viewProj2; float4 viewProj3;   // rows, vector on the left
    float4 invViewProj0; float4 invViewProj1; float4 invViewProj2; float4 invViewProj3;
    float3 eye; float invWidth;
    float3 sunDir; float invHeight;
    float3 sun; float fogStart;
    float3 skyLow; float fogRange;
    float3 skyHigh; float skyGain;
    float3 fogColour; float now;
    float2 windowMin; float cosMax; float heightBand;
    float2 mapMin; float thickMmPerRho; float normalScale;
    uint field; uint age; uint map; uint depth;
    uint normal; uint terrain; uint terrainCells; float terrainHalf;
    uint hasField; uint hasMap; uint hasTerrain; float f0;
    float drag; float stretch; float dropThickMm; float rippleSize;
    float wetDarken; float haloTexels; float meniscusMm; float ripple;
    float4 shadow0r0; float4 shadow0r1; float4 shadow0r2; float4 shadow0r3;   // rows, vector on the left (C# Matrix4x4)
    float4 shadow1r0; float4 shadow1r1; float4 shadow1r2; float4 shadow1r3;
    uint shadowMap0; uint shadowMap1; float shadowTexel0; float shadowTexel1;
    float shadowBias0; float shadowBias1; float shadowSoftness; float shadowPad;
    float noiseAmp; float noiseSize; float erodeMm; float epoch;
};
_Static_assert(sizeof(FloorConstants) == BLOOD_FLOOR_CONSTANTS_SIZE, "FloorConstants differs from C# BloodFloorConstants");

struct DropGpu
{
    float3 x0; float t;
    float3 v0; float radius;
};
_Static_assert(sizeof(DropGpu) == BLOOD_DROP_SIZE, "DropGpu differs from C# BloodDropGpu");

static const float BLOOD_TEXEL = BLOOD_FLOOR_SIZE / BLOOD_FLOOR_N;

// ── compute ───────────────────────────────────────────────────────────────

[numthreads(8, 8, 1)]
void CsMapClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BLOOD_MAP_N || id.y >= BLOOD_MAP_N) return;
    RWStructuredBuffer<uint> map = ResourceDescriptorHeap[gFloorMap];
    map[id.x + id.y * BLOOD_MAP_N] = 0;
}

// Adds rho with its birth time to a map texel: rho adds, the birth time is the rho-weighted mean (outside the map - nothing).
void MapAdd(RWStructuredBuffer<uint> map, int2 m, float rho, float birth)
{
    if (any(m < 0) || any(m >= BLOOD_MAP_N) || !(rho > 0)) return;
    uint j = m.x + m.y * BLOOD_MAP_N;
    uint old = map[j];
    float r0 = f16tof32(old), b0 = f16tof32(old >> 16);
    float r1 = r0 + rho;
    float b1 = (b0 * r0 + birth * rho) / r1;
    map[j] = f32tof16(r1) | (f32tof16(b1) << 16);
}

// Thread - texel o of the window before the shift; it stays if o - shift is inside the window, otherwise its blood goes to the map texel
// o + (gFloorMapX, gFloorMapZ): rho adds, the birth time is the rho-weighted mean. Empty texels leave the map bit for bit as it was.
[numthreads(8, 8, 1)]
void CsTransfer(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BLOOD_FLOOR_N || id.y >= BLOOD_FLOOR_N) return;
    int2 o = int2(id.xy);
    int2 kept = o - int2(asint(gFloorShiftX), asint(gFloorShiftZ));
    if (all(kept >= 0) && all(kept < BLOOD_FLOOR_N)) return;
    StructuredBuffer<uint2> c = ResourceDescriptorHeap[gFloorC];
    uint i = o.x + o.y * BLOOD_FLOOR_N;
    float rho = f16tof32(c[i].y);
    if (!(rho > 0)) return;
    StructuredBuffer<float> age = ResourceDescriptorHeap[gFloorAge];
    RWStructuredBuffer<uint> map = ResourceDescriptorHeap[gFloorMap];
    MapAdd(map, o + int2(asint(gFloorMapX), asint(gFloorMapZ)), rho, asfloat(gFloorNow) - age[i]);
}

// Drops that landed outside the window: each stamp (x, z, volume rho*m^2) goes bilinearly to four map texels, born now. One thread, in
// the order of the list - the map is the same from run to run.
[numthreads(1, 1, 1)]
void CsMapStamp(uint3 id : SV_DispatchThreadID)
{
    StructuredBuffer<float4> stamps = ResourceDescriptorHeap[gFloorData];
    RWStructuredBuffer<uint> map = ResourceDescriptorHeap[gFloorMap];
    float now = asfloat(gFloorNow);
    for (uint k = 0; k < gFloorStampCount; k++)
    {
        float4 s = stamps[gFloorStampFirst + k];
        float2 f = (s.xy + 0.5 * BLOOD_FLOOR_SIZE * BLOOD_MAP_N / BLOOD_FLOOR_N) / (BLOOD_FLOOR_SIZE / BLOOD_FLOOR_N) - 0.5;
        float2 i = floor(f), w = f - i;
        int2 t = int2(i);
        float rho = s.z / ((BLOOD_FLOOR_SIZE / BLOOD_FLOOR_N) * (BLOOD_FLOOR_SIZE / BLOOD_FLOOR_N));
        MapAdd(map, t, rho * (1 - w.x) * (1 - w.y), now);
        MapAdd(map, t + int2(1, 0), rho * w.x * (1 - w.y), now);
        MapAdd(map, t + int2(0, 1), rho * (1 - w.x) * w.y, now);
        MapAdd(map, t + int2(1, 1), rho * w.x * w.y, now);
    }
}

// Dried blood of the window (before the step, A of the last step): a thin standing texel (rho below gFloorMinRho) older than gFloorDryAge, any texel twice as
// old, goes to the map texel o + (gFloorMapX, gFloorMapZ) born at now - age and leaves the field: rho (float and half) and age - 0.
[numthreads(8, 8, 1)]
void CsDry(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BLOOD_FLOOR_N || id.y >= BLOOD_FLOOR_N) return;
    uint i = id.x + id.y * BLOOD_FLOOR_N;
    RWStructuredBuffer<float> rhoC = ResourceDescriptorHeap[gFloorRho];
    float rho = rhoC[i];
    if (!(rho > 0)) return;
    RWStructuredBuffer<float> age = ResourceDescriptorHeap[gFloorAge];
    float a = age[i], dry = asfloat(gFloorDryAge);
    if (!(a >= 2 * dry || (a >= dry && rho < asfloat(gFloorMinRho)))) return;
    RWStructuredBuffer<uint> map = ResourceDescriptorHeap[gFloorMap];
    RWStructuredBuffer<uint2> c = ResourceDescriptorHeap[gFloorC];
    MapAdd(map, int2(id.xy) + int2(asint(gFloorMapX), asint(gFloorMapZ)), rho, asfloat(gFloorNow) - a);
    rhoC[i] = 0;
    age[i] = 0;
    uint2 v = c[i];
    c[i] = uint2(v.x, v.y & 0xFFFF0000u);
}

// ── draw ──────────────────────────────────────────────────────────────────

FloorConstants Floor()
{
    StructuredBuffer<FloorConstants> data = ResourceDescriptorHeap[gFloorData];
    return data[0];
}

float FieldRho(StructuredBuffer<uint2> c, int2 t)
{
    if (any(t < 0) || any(t >= BLOOD_FLOOR_N)) return 0;
    return max(f16tof32(c[t.x + t.y * BLOOD_FLOOR_N].y), 0);
}

// Interpolation weights with zero slope at texel centres (Hermite): blobs of a few texels read round, not as diamonds.
float2 SmoothWeights(float2 w) { return w * w * (3 - 2 * w); }

// Bilinear rho of the field (smooth weights) at texel coordinates f (texel centres at integers).
float FieldBilinear(StructuredBuffer<uint2> c, float2 f)
{
    float2 i = floor(f);
    float2 w = SmoothWeights(f - i);
    int2 t = int2(i);
    float a = FieldRho(c, t), b = FieldRho(c, t + int2(1, 0)), d = FieldRho(c, t + int2(0, 1)), e = FieldRho(c, t + int2(1, 1));
    return lerp(lerp(a, b, w.x), lerp(d, e, w.x), w.y);
}

float MapRho(StructuredBuffer<uint> map, int2 t, out float birth)
{
    birth = 0;
    if (any(t < 0) || any(t >= BLOOD_MAP_N)) return 0;
    uint u = map[t.x + t.y * BLOOD_MAP_N];
    birth = f16tof32(u >> 16);
    return max(f16tof32(u), 0);
}

// Terrain height - bilinear over the nodes of the height buffer (as the terrain mesh is drawn), outside - the edge cell.
float TerrainHeight(FloorConstants c, float2 xz)
{
    StructuredBuffer<float> h = ResourceDescriptorHeap[c.terrain];
    float cells = c.terrainCells;
    float2 g = clamp((xz + c.terrainHalf) / (2 * c.terrainHalf) * cells, 0, cells);
    float2 i = min(floor(g), cells - 1);
    float2 w = g - i;
    uint nodes = c.terrainCells + 1;
    uint k = uint(i.y) * nodes + uint(i.x);
    return lerp(lerp(h[k], h[k + 1], w.x), lerp(h[k + nodes], h[k + nodes + 1], w.x), w.y);
}

// Sky radiance in direction y (the scene's gradient: lerp(low, zenith, y * 0.5 + 0.5)).
float3 SkyAt(FloorConstants c, float y) { return lerp(c.skyLow, c.skyHigh, saturate(y * 0.5 + 0.5)); }

// Light scattered by the layer itself (diffuse: sun N.L x its shadow + sky by the normal) and its highlight (GGX sun x shadow + sky by
// the reflected vector x skyGain).
float3 LayerDiffuseLight(FloorConstants c, float3 n, float sunVis) { return c.sun * (saturate(dot(n, c.sunDir)) * sunVis) + SkyAt(c, n.y); }

float3 LayerSpecular(FloorConstants c, float3 n, float3 v, float roughness, float sunVis)
{
    float3 r = reflect(-v, n);
    return SurfaceSpecular(n, v, c.sunDir, c.sun * sunVis, SkyAt(c, r.y) * c.skyGain, c.f0, roughness);
}

// Sun visibility at a point of the receiver (the scene's sun shadow map; no map - 1).
float FloorSunVisibility(FloorConstants c, float3 world, float3 n)
{
    float4x4 m0 = transpose(float4x4(c.shadow0r0, c.shadow0r1, c.shadow0r2, c.shadow0r3));
    float4x4 m1 = transpose(float4x4(c.shadow1r0, c.shadow1r1, c.shadow1r2, c.shadow1r3));
    return SunShadow(c.shadowMap0, m0, c.shadowTexel0, c.shadowBias0, c.shadowMap1, m1, c.shadowTexel1, c.shadowBias1, c.shadowSoftness,
        world, n, c.sunDir);
}

// Opaque blood (a drop in flight, unshadowed): its albedo lit + the highlight, under the fog.
float3 Lit(FloorConstants c, BloodLookResult look, float3 n, float3 v, float3 world)
{
    float3 colour = look.albedo * LayerDiffuseLight(c, n, 1) + LayerSpecular(c, n, v, look.roughness, 1);
    float fog = saturate((distance(world, c.eye) - c.fogStart) / c.fogRange);
    return lerp(colour, c.fogColour, fog);
}

// Value noise with its gradient (cubic Hermite), for the still ripple of the film.
float RippleHash(int2 p)
{
    uint h = uint(p.x) * 0x8DA6B343u ^ uint(p.y) * 0xD8163841u;
    h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
    return (h & 0xFFFFFF) / 16777216.0;
}

float2 RippleGradient(float2 x)
{
    float2 i = floor(x), f = x - i;
    float2 u = f * f * (3 - 2 * f), du = 6 * f * (1 - f);
    int2 t = int2(i);
    float a = RippleHash(t), b = RippleHash(t + int2(1, 0)), d = RippleHash(t + int2(0, 1)), e = RippleHash(t + int2(1, 1));
    float k = a - b - d + e;
    return du * float2(b - a + k * u.y, d - a + k * u.x);
}

// Value noise in [0, 1] (smooth bilinear of the hash) and its sum of four octaves - the still unevenness of the film in the world.
float ValueNoise(float2 x)
{
    float2 i = floor(x), f = x - i;
    float2 u = f * f * (3 - 2 * f);
    int2 t = int2(i);
    return lerp(lerp(RippleHash(t), RippleHash(t + int2(1, 0)), u.x), lerp(RippleHash(t + int2(0, 1)), RippleHash(t + int2(1, 1)), u.x), u.y);
}

float FilmNoise(float2 xz, float size)
{
    float2 q = xz / size + 31.7;
    float sum = 0, weight = 0.5, norm = 0;
    for (int o = 0; o < 4; o++)
    {
        sum += weight * ValueNoise(q);
        norm += weight;
        q = q * 2.03 + 17.1;
        weight *= 0.5;
    }
    return sum / norm;
}

// Thickness on screen, mm: x (1 +- noiseAmp) by the noise and minus up to erodeMm where the noise is low - a torn edge of the puddle and an
// uneven thickness (the mass of the field is not touched).
float UnevenThickness(FloorConstants c, float hMm, float n)
{
    float e = 1 - n;
    return max(hMm * (1 + c.noiseAmp * (2 * n - 1)) - c.erodeMm * e * e, 0);
}

// One layer of blood over the floor: the floor multiplier and the light it adds (premultiplied by coverage); wet - darkening of the floor
// (0 - none); mottle - factor of the light the layer scatters. Fog: the scene below is already fogged, so dst * mul + add * (1 - fog) + fogColour * fog * (1 - mul) keeps the fog on top.
void FloorLayer(FloorConstants c, BloodLookResult look, float3 n, float3 v, float3 world, float wet, float sunVis, float mottle, out float3 mul,
    out float3 add)
{
    mul = BloodMul(look) * lerp(1, c.wetDarken, wet);
    add = BloodAdd(look) * (LayerDiffuseLight(c, n, sunVis) * mottle) + look.coverage * LayerSpecular(c, n, v, look.roughness, sunVis);
    float fog = saturate((distance(world, c.eye) - c.fogStart) / c.fogRange);
    add = add * (1 - fog) + c.fogColour * fog * (1 - mul);
}

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

struct FilmOut
{
    float4 add : SV_Target0;   // dual-source blending: dst = add + dst * mul
    float4 mul : SV_Target1;
};

// Film profile for the normal: h_c * (1 - exp(-h / h_c)), mm - steep at the thin edge (meniscus), flat in the thick middle of a puddle.
float FilmProfile(FloorConstants c, float rho)
{
    return c.meniscusMm * (1 - exp(-rho * c.thickMmPerRho / c.meniscusMm));
}

FilmOut PsFloor(float4 pos : SV_Position)
{
    FloorConstants c = Floor();
    int3 px = int3(pos.xy, 0);
    Texture2D<float> depthTex = ResourceDescriptorHeap[c.depth];
    float depth = depthTex.Load(px);
    if (SceneEmpty(depth)) discard;
    Texture2D<float4> normalTex = ResourceDescriptorHeap[c.normal];
    float4 nt = normalTex.Load(px);
    if (!SceneReceivesDecals(nt)) discard;
    float3 n = SceneNormal(nt);
    if (n.y < c.cosMax) discard;
    float2 uv = pos.xy * float2(c.invWidth, c.invHeight);
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1), float4x4(c.invViewProj0, c.invViewProj1, c.invViewProj2, c.invViewProj3));
    float3 p = w.xyz / w.w;
    if (c.hasTerrain != 0 && abs(p.y - TerrainHeight(c, p.xz)) > c.heightBand) discard;
    float3 v = normalize(c.eye - p);
    BloodLookParams lp = BloodLookFromPass();
    float sunVis = FloorSunVisibility(c, p, n);

    float3 mulAll = 1, addAll = 0;
    bool drawn = false;
    float noise = FilmNoise(p.xz, c.noiseSize);
    // Mottled body of the layer: the scattered light of thick blood (opaque - its thickness no longer shows) varies by a second noise.
    float mottle = 1 + c.noiseAmp * 1.2 * (FilmNoise(p.xz * 1.7 + 5.3, c.noiseSize) - 0.5);
    if (c.hasMap != 0)
    {
        StructuredBuffer<uint> map = ResourceDescriptorHeap[c.map];
        float2 f = (p.xz - c.mapMin) / BLOOD_TEXEL - 0.5;
        float2 i = floor(f);
        float2 wt = SmoothWeights(f - i);
        int2 t = int2(i);
        float b00, b10, b01, b11;
        float r00 = MapRho(map, t, b00), r10 = MapRho(map, t + int2(1, 0), b10), r01 = MapRho(map, t + int2(0, 1), b01),
              r11 = MapRho(map, t + int2(1, 1), b11);
        float rho = lerp(lerp(r00, r10, wt.x), lerp(r01, r11, wt.x), wt.y);
        if (rho > 0)
        {
            float wsum = r00 + r10 + r01 + r11;
            float birth = (b00 * r00 + b10 * r10 + b01 * r01 + b11 * r11) / max(wsum, 1e-20);
            BloodLookResult look = BloodLookEval(max(c.now - c.epoch - birth, 0), UnevenThickness(c, rho * c.thickMmPerRho, noise), lp);
            FloorLayer(c, look, n, v, p, look.coverage * look.wet, sunVis, mottle, mulAll, addAll);
            drawn = true;
        }
    }
    if (c.hasField != 0)
    {
        float2 f = (p.xz - c.windowMin) / BLOOD_TEXEL - 0.5;
        float reach = c.haloTexels + 1;
        if (all(f > -reach) && all(f < BLOOD_FLOOR_N + reach))
        {
            StructuredBuffer<uint2> field = ResourceDescriptorHeap[c.field];
            StructuredBuffer<float> ageBuf = ResourceDescriptorHeap[c.age];
            float rho = FieldBilinear(field, f);
            // Wet floor around fresh blood: the thickest of eight samples on a circle of haloTexels (and here) wets the floor by its freshness
            // (four samples on the axes drew a cross around small spots).
            float2 at = f;
            float peak = rho;
            [unroll] for (int k = 0; k < 8; k++)
            {
                float2 o = c.haloTexels * float2(cos(k * 0.78539816), sin(k * 0.78539816));
                float r = FieldBilinear(field, f + o);
                if (r > peak) { peak = r; at = f + o; }
            }
            if (peak > 0)
            {
                int2 ta = clamp(int2(floor(at + 0.5)), 0, BLOOD_FLOOR_N - 1);
                BloodLookResult around = BloodLookEval(ageBuf[ta.x + ta.y * BLOOD_FLOOR_N], peak * c.thickMmPerRho, lp);
                float wet = around.coverage * around.wet;
                int2 t = clamp(int2(floor(f + 0.5)), 0, BLOOD_FLOOR_N - 1);
                BloodLookResult look = BloodLookEval(ageBuf[t.x + t.y * BLOOD_FLOOR_N], UnevenThickness(c, rho * c.thickMmPerRho, noise), lp);
                // Film normal: slope of the film profile (central differences, one texel) x normalScale plus the still ripple (world noise),
                // in the receiver basis.
                float k = 1e-3 / (2 * BLOOD_TEXEL) * c.normalScale;
                float gx = (FilmProfile(c, FieldBilinear(field, f + float2(1, 0))) - FilmProfile(c, FieldBilinear(field, f - float2(1, 0)))) * k;
                float gz = (FilmProfile(c, FieldBilinear(field, f + float2(0, 1))) - FilmProfile(c, FieldBilinear(field, f - float2(0, 1)))) * k;
                float2 q = p.xz / c.rippleSize;
                float2 ripple = (RippleGradient(q) + 0.5 * RippleGradient(q * 2.31 + 17.3)) * c.ripple;
                float3 g = float3(gx + ripple.x, 0, gz + ripple.y);
                g -= n * dot(n, g);
                float3 nf = normalize(n - g);
                float3 m2, a2;
                FloorLayer(c, look, nf, v, p, max(wet, look.coverage * look.wet), sunVis, mottle, m2, a2);
                addAll = addAll * m2 + a2;
                mulAll *= m2;
                drawn = true;
            }
        }
    }
    if (!drawn) discard;
    FilmOut o;
    o.add = float4(max(addAll, 0), 0);
    o.mul = float4(saturate(mulAll), 1);
    return o;
}

struct DropVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float2 local : TEXCOORD1;
    nointerpolation float radius : TEXCOORD2;
    nointerpolation float halfSeg : TEXCOORD3;
    nointerpolation float3 side : TEXCOORD4;
    nointerpolation float3 axis : TEXCOORD5;
    nointerpolation float3 toEye : TEXCOORD6;
};

DropVs VsDrop(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    const float2 k[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };
    FloorConstants c = Floor();
    StructuredBuffer<DropGpu> drops = ResourceDescriptorHeap[gFloorDrops];
    DropGpu d = drops[iid];
    float3 x = BloodFlightPosition(d.x0, d.v0, c.drag, d.t);
    float3 vel = BloodFlightVelocity(d.v0, c.drag, d.t);
    float speed = length(vel);
    float3 axis = speed > 1e-3 ? vel / speed : float3(0, -1, 0);
    float3 toEye = normalize(c.eye - x);
    float3 side = cross(axis, toEye);
    side = dot(side, side) > 1e-8 ? normalize(side) : normalize(cross(axis, float3(1, 0, 0.001)));
    float r = d.radius;
    float halfLen = r + 0.5 * min(speed * c.stretch, 6 * r);
    float2 q = k[vid];
    DropVs o;
    o.world = x + side * (q.x * r) + axis * (q.y * halfLen);
    o.pos = mul(float4(o.world, 1), float4x4(c.viewProj0, c.viewProj1, c.viewProj2, c.viewProj3));
    o.local = float2(q.x * r, q.y * halfLen);
    o.radius = r;
    o.halfSeg = halfLen - r;
    o.side = side;
    o.axis = axis;
    o.toEye = toEye;
    return o;
}

struct DropOut
{
    float4 colour : SV_Target0;
    float4 normal : SV_Target1;
};

DropOut PsDrop(DropVs i)
{
    float2 d = float2(i.local.x, max(abs(i.local.y) - i.halfSeg, 0));
    float r2 = dot(d, d) / (i.radius * i.radius);
    if (r2 > 1) discard;
    float3 n = normalize(i.side * (d.x / i.radius) + i.axis * (sign(i.local.y) * d.y / i.radius) + i.toEye * sqrt(1 - r2));
    FloorConstants c = Floor();
    BloodLookResult look = BloodLookEval(0, c.dropThickMm, BloodLookFromPass());
    DropOut o;
    o.colour = float4(Lit(c, look, n, normalize(c.eye - i.world), i.world), 1);
    o.normal = SceneNormalOut(n, false);
    return o;
}
