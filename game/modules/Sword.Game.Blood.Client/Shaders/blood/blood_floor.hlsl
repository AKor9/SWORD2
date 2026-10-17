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
    float shadowBias0; float shadowBias1; float shadowSoftness; float edgeWarp;
    float noiseAmp; float noiseSize; float erodeMm; float epoch;
    float glint; float glintRough; float horizon; float maxTilt;
};
_Static_assert(sizeof(FloorConstants) == BLOOD_FLOOR_CONSTANTS_SIZE, "FloorConstants differs from C# BloodFloorConstants");

struct DropGpu
{
    float3 x0; float t;
    float3 v0; float radius;
};
_Static_assert(sizeof(DropGpu) == BLOOD_DROP_SIZE, "DropGpu differs from C# BloodDropGpu");

static const float BLOOD_TEXEL = BLOOD_FLOOR_SIZE / BLOOD_FLOOR_N;
static const float DROP_MIN_PIXELS = 0.75;   // a flying drop on screen: radius not below, pixels

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

// Mitchell-Netravali cubic (B = C = 1/3 = B-spline / 3 + Catmull-Rom * 2/3): weights of the four taps around w in [0, 1) and their
// derivatives d/dw. Round contours instead of the texel grid, yet a single-texel drop keeps ~80 % of its peak (a pure B-spline - 44 %,
// and the erosion ate the small drops); the slight negative lobe only lowers the signed thickness outside the film.
void Cubic(float w, out float4 k, out float4 dk)
{
    float v = 1 - w, w2 = w * w, w3 = w2 * w;
    float4 kb = float4(v * v * v, 3 * w3 - 6 * w2 + 4, -3 * w3 + 3 * w2 + 3 * w + 1, w3) / 6;
    float4 dkb = float4(-0.5 * v * v, 1.5 * w2 - 2 * w, -1.5 * w2 + w + 0.5, 0.5 * w2);
    float4 kc = float4(-w3 + 2 * w2 - w, 3 * w3 - 5 * w2 + 2, -3 * w3 + 4 * w2 + w, w3 - w2) * 0.5;
    float4 dkc = float4(-3 * w2 + 4 * w - 1, 9 * w2 - 10 * w, -9 * w2 + 8 * w + 1, 3 * w2 - 2 * w) * 0.5;
    k = (kb + 2 * kc) / 3;
    dk = (dkb + 2 * dkc) / 3;
}

// Smooth rho of the field at texel coordinates f (bicubic, 16 taps) and its gradient, rho per texel.
float FieldSmooth(StructuredBuffer<uint2> c, float2 f, out float2 grad)
{
    float2 i = floor(f);
    grad = 0;
    // Nothing in the four nearest texels - nothing worth the other twelve taps (the outer ones only weigh a few per cent).
    int2 n = int2(i);
    if (FieldRho(c, n) + FieldRho(c, n + int2(1, 0)) + FieldRho(c, n + int2(0, 1)) + FieldRho(c, n + int2(1, 1)) <= 0) return 0;
    float4 kx, dkx, kz, dkz;
    Cubic(f.x - i.x, kx, dkx);
    Cubic(f.y - i.y, kz, dkz);
    int2 t = n - 1;
    float v = 0;
    [unroll] for (int y = 0; y < 4; y++)
    {
        float4 r = float4(FieldRho(c, t + int2(0, y)), FieldRho(c, t + int2(1, y)), FieldRho(c, t + int2(2, y)), FieldRho(c, t + int2(3, y)));
        float row = dot(kx, r);
        v += kz[y] * row;
        grad += float2(kz[y] * dot(dkx, r), dkz[y] * row);
    }
    return v;
}

float MapRho(StructuredBuffer<uint> map, int2 t, out float birth)
{
    birth = 0;
    if (any(t < 0) || any(t >= BLOOD_MAP_N)) return 0;
    uint u = map[t.x + t.y * BLOOD_MAP_N];
    birth = f16tof32(u >> 16);
    return max(f16tof32(u), 0);
}

// Smooth rho of the map at texel coordinates f (bicubic, 16 taps) and its birth - the mean weighted by the share of rho.
float MapSmooth(StructuredBuffer<uint> map, float2 f, out float birth)
{
    float2 i = floor(f);
    birth = 0;
    int2 n = int2(i);
    float b;
    if (MapRho(map, n, b) + MapRho(map, n + int2(1, 0), b) + MapRho(map, n + int2(0, 1), b) + MapRho(map, n + int2(1, 1), b) <= 0) return 0;
    float4 kx, dkx, kz, dkz;
    Cubic(f.x - i.x, kx, dkx);
    Cubic(f.y - i.y, kz, dkz);
    int2 t = n - 1;
    float v = 0, vb = 0, wb = 0;
    [unroll] for (int y = 0; y < 4; y++)
        [unroll] for (int x = 0; x < 4; x++)
        {
            float r = kx[x] * kz[y] * MapRho(map, t + int2(x, y), b);
            v += r;
            // The birth - over the positive taps only: at the edge the negative lobes cancel rho to ~0, and a mean over unequal births
            // divided by it left their range (an old stain's rim read as fresh).
            float rb = max(r, 0);
            wb += rb;
            vb += rb * b;
        }
    birth = vb / max(wb, 1e-20);
    return max(v, 0);
}

// Slope of the film profile h_c (1 - exp(-h / h_c)) per unit rho, mm: steep at the thin edge (meniscus), flat in the thick middle. Of
// max(rho, 0): the negative lobe of the bicubic outside the film would overflow exp (NaN in the normal) at a small meniscus.
float FilmProfileSlope(float rho, float thickMmPerRho, float meniscusMm)
{
    return thickMmPerRho * exp(-max(rho, 0) * thickMmPerRho / meniscusMm);
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

// Sky seen in the reflected direction r: the scene's gradient plus a bright horizon (x skyHigh, growing as (1 - r.y)^4) - at grazing
// angles the Fresnel of the film reflects the light low sky, so wet blood reads even in shadow; x skyGain.
float3 ReflectedSky(FloorConstants c, float3 r)
{
    float k = 1 - saturate(r.y);
    k *= k;
    return (SkyAt(c, r.y) + c.skyHigh * (c.horizon * k * k)) * c.skyGain;
}

// Highlight of the film: GGX sun (sharp, the layer's roughness) + the reflected sky, and a second, wider sun lobe of weight glint (the
// ripple and the meniscus spread the glint around the mirror direction - a dual-lobe wet highlight; the sky is already in the first).
float3 LayerSpecular(FloorConstants c, float3 n, float3 v, float roughness, float sunVis, float glint)
{
    float3 r = reflect(-v, n);
    float3 sun = c.sun * sunVis;
    float3 s = SurfaceSpecular(n, v, c.sunDir, sun, ReflectedSky(c, r), c.f0, roughness);
    if (glint > 0) s += glint * SurfaceSpecular(n, v, c.sunDir, sun, 0, c.f0, roughness + c.glintRough);
    return s;
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
    float3 colour = look.albedo * LayerDiffuseLight(c, n, 1) + LayerSpecular(c, n, v, look.roughness, 1, c.glint * look.wet);
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

// Static warp of the sample point, texels: two value noises at ~2 cm move it by up to edgeWarp - the edge tears within a texel.
float2 EdgeWarp(FloorConstants c, float2 xz)
{
    float2 q = xz / 0.021 + 7.7;
    float2 r = float2(1.6 * q.x + 1.2 * q.y, 1.6 * q.y - 1.2 * q.x);
    float2 d = float2(ValueNoise(q) + 0.5 * ValueNoise(r + 3.1), ValueNoise(q + 41.3) + 0.5 * ValueNoise(r + 57.9)) / 1.5;
    return c.edgeWarp * (2 * d - 1);
}

float FilmNoise(float2 xz, float size)
{
    float2 q = xz / size + 31.7;
    float sum = 0, weight = 0.5, norm = 0;
    for (int o = 0; o < 4; o++)
    {
        sum += weight * ValueNoise(q);
        norm += weight;
        q = float2(1.6 * q.x + 1.2 * q.y, 1.6 * q.y - 1.2 * q.x) + 17.1;   // x2, turned by 37 deg: value noise loses its grid
        weight *= 0.5;
    }
    return sum / norm;
}

// Thickness on screen, mm: x (1 +- noiseAmp) by the noise and minus up to erodeMm where the noise is low - a torn edge of the puddle and an
// uneven thickness (the mass of the field is not touched). Signed: below zero outside the film, so its screen derivative stays
// meaningful across the edge.
float UnevenThickness(FloorConstants c, float hMm, float n)
{
    float e = 1 - n;
    return hMm * (1 + c.noiseAmp * (2 * n - 1)) - c.erodeMm * e * e;
}

// Look of a layer averaged over the pixel footprint in thickness: four evaluations across [s - fw/2, s + fw/2] (fw - the screen
// derivative of the thickness) - the edge, where the thickness jumps from nothing to the bulk within a pixel, is antialiased by the same
// look function instead of stepping on pixels. mul, add (albedo part), coverage and its wet part, roughness - averages.
struct FilmLook
{
    float3 mul, add;
    float coverage, wet, roughness;
};

FilmLook FilteredLook(float age, float s, float fw, BloodLookParams lp)
{
    FilmLook o = (FilmLook)0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        BloodLookResult r = BloodLookEval(age, s + fw * ((k + 0.5) * 0.25 - 0.5), lp);
        o.mul += BloodMul(r);
        o.add += BloodAdd(r);
        o.coverage += r.coverage;
        o.wet += r.coverage * r.wet;
        o.roughness += r.roughness;
    }
    o.mul *= 0.25; o.add *= 0.25; o.coverage *= 0.25; o.wet *= 0.25; o.roughness *= 0.25;
    return o;
}

// One layer of blood over the floor: the floor multiplier and the light it adds (premultiplied by coverage); wet - darkening of the floor
// (0 - none); mottle - factor of the light the layer scatters. Fog: the scene below is already fogged, so dst * mul + add * (1 - fog) + fogColour * fog * (1 - mul) keeps the fog on top.
void FloorLayer(FloorConstants c, FilmLook look, float3 n, float3 v, float3 world, float wet, float sunVis, float mottle, out float3 mul,
    out float3 add)
{
    mul = look.mul * BloodWetMul(wet, c.wetDarken);
    float glint = c.glint * saturate(look.wet / max(look.coverage, 1e-4));   // fresh film only: dry crust has no wide glint
    add = look.add * (LayerDiffuseLight(c, n, sunVis) * mottle) + look.coverage * LayerSpecular(c, n, v, look.roughness, sunVis, glint);
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
    // Thicknesses are sampled in uniform flow: their screen derivatives (fwidth) are the pixel footprint for FilteredLook.
    float mapS = -1, mapBirth = 0, fieldS = -1, fieldRho = 0, peak = 0;
    float2 fieldF = 0, fieldGrad = 0, peakAt = 0;
    float2 warp = EdgeWarp(c, p.xz);   // the map and the window share the texel grid
    if (c.hasMap != 0)
    {
        StructuredBuffer<uint> map = ResourceDescriptorHeap[c.map];
        float2 f = (p.xz - c.mapMin) / BLOOD_TEXEL - 0.5 + warp;
        float rho = MapSmooth(map, f, mapBirth);
        mapS = UnevenThickness(c, rho * c.thickMmPerRho, noise);
    }
    if (c.hasField != 0)
    {
        StructuredBuffer<uint2> field = ResourceDescriptorHeap[c.field];
        fieldF = (p.xz - c.windowMin) / BLOOD_TEXEL - 0.5 + warp;
        fieldRho = FieldSmooth(field, fieldF, fieldGrad);
        fieldS = UnevenThickness(c, fieldRho * c.thickMmPerRho, noise);   // continuous across rho = 0: fwidth stays the footprint
        // Wet floor around fresh blood: the mean rho of eight samples on a circle of haloTexels - it fades smoothly outwards (the
        // thickest sample drew a hard band of the halo radius along the texel grid); the freshness - of the thickest sample.
        float best = fieldRho, sum = 0;
        peakAt = fieldF;
        [unroll] for (int k = 0; k < 8; k++)
        {
            float2 o = c.haloTexels * float2(cos(k * 0.78539816), sin(k * 0.78539816));
            float r = FieldBilinear(field, fieldF + o);
            sum += r;
            if (r > best) { best = r; peakAt = fieldF + o; }
        }
        peak = sum / 8;
    }
    // A pixel spans at most half a millimetre of thickness. A quad across a silhouette (a body over the floor: its world point lies over
    // the puddle) is no footprint at all - there the step of the world point exceeds what the ray step on this surface allows (the ray
    // direction is continuous across silhouettes), and the pixel takes no filtering instead of smearing the film onto the outline.
    float expected = distance(c.eye, p) * (length(ddx(v)) + length(ddy(v))) / max(abs(dot(n, v)), 0.1);
    float aa = length(ddx(p)) + length(ddy(p)) < 2 * expected ? 1 : 0;
    float mapFw = min(fwidth(mapS), 0.5) * aa, fieldFw = min(fwidth(fieldS), 0.5) * aa;
    if (mapS + 0.5 * mapFw > 0)
    {
        FilmLook look = FilteredLook(max(c.now - c.epoch - mapBirth, 0), mapS, mapFw, lp);
        FloorLayer(c, look, n, v, p, look.wet, sunVis, mottle, mulAll, addAll);
        drawn = true;
    }
    if (fieldS + 0.5 * fieldFw > 0 || peak > 0)
    {
        StructuredBuffer<float> ageBuf = ResourceDescriptorHeap[c.age];
        int2 ta = clamp(int2(floor(peakAt + 0.5)), 0, BLOOD_FLOOR_N - 1);
        // The halo wets by the freshness of the blood it comes from, fading in over the first 0.1 mm of the mean thickness around.
        float haloWet = BloodLookEval(ageBuf[ta.x + ta.y * BLOOD_FLOOR_N], 1, lp).wet * BloodSmooth(0, 0.1, peak * c.thickMmPerRho);
        int2 t = clamp(int2(floor(fieldF + 0.5)), 0, BLOOD_FLOOR_N - 1);
        FilmLook look = FilteredLook(ageBuf[t.x + t.y * BLOOD_FLOOR_N], fieldS, fieldFw, lp);
        // Film normal: slope of the film profile h_c (1 - exp(-h / h_c)) (steep at the thin edge - meniscus, flat in the thick middle) from
        // the smooth field's gradient x normalScale, plus the still ripple (world noise), in the receiver basis.
        float dProfile = FilmProfileSlope(fieldRho, c.thickMmPerRho, c.meniscusMm);
        float2 slope = fieldGrad * (dProfile * 1e-3 / BLOOD_TEXEL * c.normalScale);
        float2 q = p.xz / c.rippleSize;
        float2 ripple = (RippleGradient(q) + 0.5 * RippleGradient(q * 2.31 + 17.3)) * c.ripple;
        float3 g = float3(slope.x + ripple.x, 0, slope.y + ripple.y);
        g -= n * dot(n, g);
        float gl = length(g);
        if (gl > c.maxTilt) g *= c.maxTilt / gl;   // the steep meniscus at the very edge: tilted, never on its side
        float3 nf = normalize(n - g);
        float3 m2, a2;
        FloorLayer(c, look, nf, v, p, max(haloWet, look.wet), sunVis, mottle, m2, a2);
        addAll = addAll * m2 + a2;
        mulAll *= m2;
        drawn = true;
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
    float4x4 vp = float4x4(c.viewProj0, c.viewProj1, c.viewProj2, c.viewProj3);
    // A far drop is no thinner than DROP_MIN_PIXELS in radius: smaller ones are widened to it and kept with the probability of their
    // true area share (a stable hash of the drop's start) - fewer, not fainter, drops in the distance, without subpixel flicker.
    float4 c0 = mul(float4(x, 1), vp), c1 = mul(float4(x + side * r, 1), vp);
    float pixels = c0.w > 1e-4 && c1.w > 1e-4 ? length((c1.xy / c1.w - c0.xy / c0.w) * 0.5 / float2(c.invWidth, c.invHeight)) : DROP_MIN_PIXELS;
    DropVs o;
    if (pixels < DROP_MIN_PIXELS)
    {
        float grow = DROP_MIN_PIXELS / max(pixels, 1e-3);
        float keep = 1 / (grow * grow);
        uint h = asuint(d.x0.x) * 0x8DA6B343u ^ asuint(d.x0.y) * 0xD8163841u ^ asuint(d.x0.z) * 0xCB1AB31Fu;
        h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
        if ((h & 0xFFFF) / 65536.0 >= keep)
        {
            o = (DropVs)0;   // degenerate: all six corners at one point - nothing is drawn
            return o;
        }
        r *= grow;
        halfLen *= grow;
    }
    float2 q = k[vid];
    o.world = x + side * (q.x * r) + axis * (q.y * halfLen);
    o.pos = mul(float4(o.world, 1), vp);
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
