// Fire smoke (material MI_HVOL_SIM_TorchSmoke of LIVE-2 in Heterogeneous Volumes) over the density of the fire's 3D gas: a ray march of the
// gas window - an axis-aligned box in the world - into the half-resolution transparent target, premultiplied, far windows first. The pass
// draws the box of the window (its back faces only - one fragment per covered pixel, also with the camera inside), so pixels outside it cost
// nothing. The ray is rebuilt from the pixel by the inverse view-projection; the segment is the box clipped by the camera and the
// half-resolution scene depth; samples are the midpoints of equal steps of that segment. Empty space is skipped: the occupancy grid
// holds the max density of each brick of 4^3 cells with a one-cell margin, the ray is clipped to the bounds of the non-empty bricks and jumps
// over empty bricks - skipped samples would have sigma = 0, so the result is that of the full march. Detail noise erodes the density:
// rho' = rho sat(1 - a (1/2 + n)), n - gradient noise in the world carried by the gas velocity with a two-phase flow map;
// it only lowers rho, so the skip stays exact. Per-window constants -
// StructuredBuffer<SmokeConstants> gSmokeData, element gSmokeIndex (FireSmoke.cs; CPU reference - SmokeMath). Gas scalars: uint per cell,
// half density in the low 16 bits (Fluid3DSim.Scalars), cell (x, y, z) at x + nx (y + ny z); index space - cell centres at integers.
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"
#include "common/lights.hlsli"

struct SmokeConstants
{
    float4 viewProj0; float4 viewProj1; float4 viewProj2; float4 viewProj3;               // rows, vector on the left
    float4 invViewProj0; float4 invViewProj1; float4 invViewProj2; float4 invViewProj3;
    float3 eye; float stepCells;             // step, cells of the gas
    float3 forward; uint maxSteps;           // camera axis (for the view depth of the scene)
    float3 boxMin; float cellSize;           // lower corner of the window, m; cell, m
    uint nx; uint ny; uint nz; uint scalars;
    uint bx; uint by; uint bz; uint bricks;  // occupancy grid: bricks per axis, SRV of the max density per brick
    float3 sun; float albedo;                // sun as a light: illuminance of the UE light
    float3 ambient; float unitsPerMetre;     // sky of the fake ambient; sigma units per metre of path
    float3 fogColour; float fog;
    float densityMult; float densityPow; float densityCut; float ambientMult;
    float ambientPow; float ambientAoPow; float invWidth; float invHeight;
    float2 viewDepth; uint sceneDepth; uint bounds;   // bounds: SRV of the non-empty brick bounds (min xyz, max xyz, count, total)
    uint lights; uint lightCount; uint skip; float minTransmittance;
    float3 noiseOrigin; float noiseStep;     // detail noise: coordinates of cell 0 (mod 256), nodes per cell
    float detail; float flowShift0; float flowShift1; float flowBlend;   // a; flow-map shifts (s/m) and weight of phase 1
    uint velocity; uint pad0; uint pad1; uint pad2;                      // SRV of the gas velocity (half vx vy | vz)
};
_Static_assert(sizeof(SmokeConstants) == FIRE_SMOKE_CONSTANTS_SIZE, "SmokeConstants differs from C# SmokeConstants");

struct SmokeLightData
{
    float3 position; float invRadiusSq;
    float3 radiance; float exponent;         // colour x intensity x fade x volumetric scattering
};
_Static_assert(sizeof(SmokeLightData) == FIRE_SMOKE_LIGHT_SIZE, "SmokeLightData differs from C# SmokeLight");

static const float SmokePhase = 1.0 / (4.0 * 3.14159265);

SmokeConstants Smoke(uint index)
{
    StructuredBuffer<SmokeConstants> data = ResourceDescriptorHeap[gSmokeData];
    return data[index];
}

struct SmokeResult
{
    float3 light;
    float transmittance;
    uint steps;
    uint samples;
};

float SmokeSafe(float x) { return abs(x) < 1e-8 ? (x < 0 ? -1e-8 : 1e-8) : x; }

// Ray direction through the full-resolution pixel position px (pixel centre at .5).
float3 SmokeRay(SmokeConstants c, float2 px)
{
    float2 ndc = float2(px.x * c.invWidth * 2.0 - 1.0, 1.0 - px.y * c.invHeight * 2.0);
    float4 p = mul(float4(ndc, 1.0, 1.0), float4x4(c.invViewProj0, c.invViewProj1, c.invViewProj2, c.invViewProj3));
    return normalize(p.xyz / p.w - c.eye);
}

// Segment of eye + t dir in the box [lo, hi] within [near, far]; empty - false.
bool SmokeBox(float3 eye, float3 dir, float3 lo, float3 hi, float near, float far, out float t0, out float t1)
{
    float3 inv = float3(1.0 / SmokeSafe(dir.x), 1.0 / SmokeSafe(dir.y), 1.0 / SmokeSafe(dir.z));
    float3 ta = (lo - eye) * inv;
    float3 tb = (hi - eye) * inv;
    float3 tmin = min(ta, tb);
    float3 tmax = max(ta, tb);
    t0 = max(max(tmin.x, tmin.y), max(tmin.z, near));
    t1 = min(min(tmax.x, tmax.y), min(tmax.z, far));
    return t1 > t0;
}

float SmokeCell(StructuredBuffer<uint> s, SmokeConstants c, int3 p)
{
    if (p.x < 0 || p.y < 0 || p.z < 0 || p.x >= (int)c.nx || p.y >= (int)c.ny || p.z >= (int)c.nz) return 0;
    return f16tof32(s[(uint)p.x + c.nx * ((uint)p.y + c.ny * (uint)p.z)]);
}

float SmokeLerp(float a, float b, float t) { return a + (b - a) * t; }

// Trilinear density at q (index space), zero outside the window.
float SmokeDensity(StructuredBuffer<uint> s, SmokeConstants c, float3 q)
{
    float3 f = floor(q);
    int3 i = int3(f);
    float3 w = q - f;
    float x00 = SmokeLerp(SmokeCell(s, c, i), SmokeCell(s, c, i + int3(1, 0, 0)), w.x);
    float x10 = SmokeLerp(SmokeCell(s, c, i + int3(0, 1, 0)), SmokeCell(s, c, i + int3(1, 1, 0)), w.x);
    float x01 = SmokeLerp(SmokeCell(s, c, i + int3(0, 0, 1)), SmokeCell(s, c, i + int3(1, 0, 1)), w.x);
    float x11 = SmokeLerp(SmokeCell(s, c, i + int3(0, 1, 1)), SmokeCell(s, c, i + int3(1, 1, 1)), w.x);
    return max(SmokeLerp(SmokeLerp(x00, x10, w.y), SmokeLerp(x01, x11, w.y), w.z), 0.0);
}

// Gas velocity at q (index space), trilinear, the cell index clamped to the window (as the gas does).
float3 SmokeVelocityCell(StructuredBuffer<uint2> v, SmokeConstants c, int3 p)
{
    p = clamp(p, int3(0, 0, 0), int3(c.nx, c.ny, c.nz) - 1);
    uint2 u = v[(uint)p.x + c.nx * ((uint)p.y + c.ny * (uint)p.z)];
    return float3(f16tof32(u.x), f16tof32(u.x >> 16), f16tof32(u.y));
}

float3 SmokeLerp3(float3 a, float3 b, float t) { return a + (b - a) * t; }

float3 SmokeVelocity(StructuredBuffer<uint2> v, SmokeConstants c, float3 q)
{
    float3 f = floor(q);
    int3 i = int3(f);
    float3 w = q - f;
    float3 x00 = SmokeLerp3(SmokeVelocityCell(v, c, i), SmokeVelocityCell(v, c, i + int3(1, 0, 0)), w.x);
    float3 x10 = SmokeLerp3(SmokeVelocityCell(v, c, i + int3(0, 1, 0)), SmokeVelocityCell(v, c, i + int3(1, 1, 0)), w.x);
    float3 x01 = SmokeLerp3(SmokeVelocityCell(v, c, i + int3(0, 0, 1)), SmokeVelocityCell(v, c, i + int3(1, 0, 1)), w.x);
    float3 x11 = SmokeLerp3(SmokeVelocityCell(v, c, i + int3(0, 1, 1)), SmokeVelocityCell(v, c, i + int3(1, 1, 1)), w.x);
    return SmokeLerp3(SmokeLerp3(x00, x10, w.y), SmokeLerp3(x01, x11, w.y), w.z);
}

// Detail noise (SmokeMath.Noise): Perlin gradient noise, quintic fade, lattice repeating every 256 nodes; hash - first word of pcg3d.

uint SmokeHash(int3 p)
{
    uint3 v = (uint3)p & 255u;
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    return v.x + v.y * v.z;
}

// g.d for the Perlin gradient h (12 cube edges and 4 repeats) by selects - no indexed table: u + v with signs.
float SmokeCorner(int3 p, float3 d)
{
    uint h = SmokeHash(p) >> 28;
    float u = h < 8 ? d.x : d.y;
    float v = h < 4 ? d.y : (h == 12 || h == 14 ? d.x : d.z);
    return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
}

float SmokeFade(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

float SmokeNoise(float3 q)
{
    float3 f = floor(q);
    int3 i = int3(f);
    float3 t = q - f;
    float3 u = float3(SmokeFade(t.x), SmokeFade(t.y), SmokeFade(t.z));
    float x00 = SmokeLerp(SmokeCorner(i, t), SmokeCorner(i + int3(1, 0, 0), t - float3(1, 0, 0)), u.x);
    float x10 = SmokeLerp(SmokeCorner(i + int3(0, 1, 0), t - float3(0, 1, 0)), SmokeCorner(i + int3(1, 1, 0), t - float3(1, 1, 0)), u.x);
    float x01 = SmokeLerp(SmokeCorner(i + int3(0, 0, 1), t - float3(0, 0, 1)), SmokeCorner(i + int3(1, 0, 1), t - float3(1, 0, 1)), u.x);
    float x11 = SmokeLerp(SmokeCorner(i + int3(0, 1, 1), t - float3(0, 1, 1)), SmokeCorner(i + int3(1, 1, 1), t - float3(1, 1, 1)), u.x);
    return SmokeLerp(SmokeLerp(x00, x10, u.y), SmokeLerp(x01, x11, u.y), u.z);
}

// Density with the detail noise at q: the noise point moved by the gas velocity in two flow-map phases, blended by the weight of phase 1.
float SmokeDetail(StructuredBuffer<uint2> velocity, SmokeConstants c, float3 q, float rho)
{
    float3 qn = q * c.noiseStep + c.noiseOrigin;
    float3 v = SmokeVelocity(velocity, c, q);
    float n0 = SmokeNoise(qn - v * c.flowShift0);
    float n1 = SmokeNoise(qn - v * c.flowShift1);
    float n = n0 + (n1 - n0) * c.flowBlend;
    return rho * saturate(1.0 - c.detail * (0.5 + n));
}

// Sigma of the material: DensityMult (rho - cut)^DensityPow, zero at or below the cut.
float SmokeSigma(SmokeConstants c, float rho) { return rho > c.densityCut ? c.densityMult * pow(rho - c.densityCut, c.densityPow) : 0.0; }

// First sample index at or after t: midpoints t0 + (i + 1/2) dt, at most n.
uint SmokeSampleAt(float t, float t0, float dt, uint n) { return (uint)min(max(ceil((t - t0) / dt - 0.5), 0.0), (float)n); }

// March of one ray eye + t dir up to the scene (t <= sceneT): midpoints of equal steps of the window segment, at most maxSteps;
// L += T (E + S) k, T *= exp(-sigma k), k = step x units per metre (integral of HV: emission and single scattering, Beer-Lambert),
// until T < minTransmittance. With skip: only inside the bounds of the non-empty bricks, empty bricks jumped over.
SmokeResult MarchSmoke(SmokeConstants c, float3 dir, float sceneT)
{
    SmokeResult r;
    r.light = 0;
    r.transmittance = 1;
    r.steps = 0;
    r.samples = 0;
    float t0, t1;
    if (!SmokeBox(c.eye, dir, c.boxMin, c.boxMin + float3(c.nx, c.ny, c.nz) * c.cellSize, 0.0, sceneT, t0, t1)) return r;
    uint n = max((uint)min(ceil((t1 - t0) / (c.stepCells * c.cellSize)), (float)c.maxSteps), 1u);
    float dt = (t1 - t0) / n;
    r.steps = n;
    float3 q0 = (c.eye - c.boxMin) / c.cellSize - 0.5;
    float3 dq = dir / c.cellSize;
    float3 invDq = float3(1.0 / SmokeSafe(dq.x), 1.0 / SmokeSafe(dq.y), 1.0 / SmokeSafe(dq.z));
    uint i = 0;
    float tEnd = t1;
    StructuredBuffer<float> bricks = ResourceDescriptorHeap[c.bricks];
    if (c.skip != 0)
    {
        // Non-empty density lies within [4 min - 1, 4 max + 4] in index space (a corner cell of a sample is in a non-empty brick);
        // half a cell more on each side.
        StructuredBuffer<uint> bounds = ResourceDescriptorHeap[c.bounds];
        if (bounds[6] == 0) return r;
        float3 lo = float3(bounds[0], bounds[1], bounds[2]) * FIRE_SMOKE_BRICK - 1.5;
        float3 hi = float3(bounds[3], bounds[4], bounds[5]) * FIRE_SMOKE_BRICK + (FIRE_SMOKE_BRICK + 0.5);
        float b0, b1;
        if (!SmokeBox(c.eye, dir, c.boxMin + (lo + 0.5) * c.cellSize, c.boxMin + (hi + 0.5) * c.cellSize, t0, t1, b0, b1)) return r;
        i = SmokeSampleAt(b0, t0, dt, n);
        tEnd = b1;
    }
    float k = c.unitsPerMetre * dt;
    StructuredBuffer<uint> scalars = ResourceDescriptorHeap[c.scalars];
    StructuredBuffer<uint2> velocity = ResourceDescriptorHeap[c.velocity];
    StructuredBuffer<SmokeLightData> lights = ResourceDescriptorHeap[c.lights];
    int3 lastBrick = int3(c.bx, c.by, c.bz) - 1;
    // Every pass advances i: at most n iterations.
    [loop]
    while (i < n)
    {
        float t = t0 + (i + 0.5) * dt;
        if (t > tEnd) break;
        float3 q = q0 + dq * t;
        if (c.skip != 0)
        {
            int3 b = clamp(int3(floor(q / FIRE_SMOKE_BRICK)), int3(0, 0, 0), lastBrick);
            if (bricks[(uint)b.x + c.bx * ((uint)b.y + c.by * (uint)b.z)] <= c.densityCut)
            {
                // Exit of the brick region [4b, 4b + 4) along the ray, then the first sample after it.
                float3 lo = float3(b) * FIRE_SMOKE_BRICK;
                float3 exitQ = float3(dq.x > 0 ? lo.x + FIRE_SMOKE_BRICK : lo.x, dq.y > 0 ? lo.y + FIRE_SMOKE_BRICK : lo.y,
                    dq.z > 0 ? lo.z + FIRE_SMOKE_BRICK : lo.z);
                float3 te = (exitQ - q0) * invDq;
                i = max(i + 1, SmokeSampleAt(min(te.x, min(te.y, te.z)), t0, dt, n));
                continue;
            }
        }
        float rho = SmokeDensity(scalars, c, q);
        if (c.detail > 0 && rho > c.densityCut) rho = SmokeDetail(velocity, c, q, rho);
        float sigma = SmokeSigma(c, rho);
        i++;
        if (!(sigma > 0)) continue;
        r.samples++;
        // Fake ambient: sky with "AO" by density.
        float3 emission = c.ambient * (c.ambientMult * pow(sigma, c.ambientPow) * (1.0 - pow(sigma, c.ambientAoPow)));
        // Single scattering, isotropic phase, no light function and no shadows: sun + fire lights by their falloff.
        float3 world = c.eye + dir * t;
        float3 incident = c.sun;
        for (uint l = 0; l < c.lightCount; l++)
        {
            SmokeLightData s = lights[l];
            float3 toLight = s.position - world;
            incident += s.radiance * PointLightAttenuation(dot(toLight, toLight), s.invRadiusSq, s.exponent, false);
        }
        float3 scatter = incident * (c.albedo * sigma * SmokePhase);
        r.light += r.transmittance * (emission + scatter) * k;
        r.transmittance *= exp(-sigma * k);
        if (r.transmittance < c.minTransmittance) break;
    }
    return r;
}

// Ray parameter of the scene behind the half-resolution pixel hp: linear view depth along the camera axis / cos to the axis.
float SmokeSceneT(SmokeConstants c, int2 hp, float3 dir)
{
    Texture2D<float> depth = ResourceDescriptorHeap[c.sceneDepth];
    return SceneViewDepth(depth.Load(int3(hp, 0)), c.viewDepth) / max(dot(dir, c.forward), 1e-6);
}

// Full-screen triangle.
float4 VsSmoke(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

struct SmokeBoxVertex
{
    float4 pos : SV_Position;
    nointerpolation float3 normal : NORMAL;   // outward normal of the face
    nointerpolation float3 face : POSITION;   // a point of the face, world
};

// Box of the window: 6 faces x 2 triangles; face f - axis f / 2, side f % 2 (0 - lower, 1 - upper). Winding does not matter: the pixel
// shader keeps only faces turned away from the eye (geometrically), so every covered pixel gets exactly one fragment.
SmokeBoxVertex VsSmokeBox(uint vid : SV_VertexID)
{
    SmokeConstants c = Smoke(gSmokeIndex);
    uint face = vid / 6, corner = vid % 6;
    uint axis = face / 2;
    float side = (float)(face % 2);
    float2 q = float2(corner == 1 || corner == 2 || corner == 4 ? 1.0 : 0.0, corner == 2 || corner == 4 || corner == 5 ? 1.0 : 0.0);
    float3 t = axis == 0 ? float3(side, q.x, q.y) : axis == 1 ? float3(q.y, side, q.x) : float3(q.x, q.y, side);
    float3 world = c.boxMin + t * (float3(c.nx, c.ny, c.nz) * c.cellSize);
    SmokeBoxVertex o;
    o.pos = mul(float4(world, 1.0), float4x4(c.viewProj0, c.viewProj1, c.viewProj2, c.viewProj3));
    o.normal = (axis == 0 ? float3(1, 0, 0) : axis == 1 ? float3(0, 1, 0) : float3(0, 0, 1)) * (side * 2.0 - 1.0);
    o.face = world;
    return o;
}

// Premultiplied contribution of one window to the half-resolution target: scene fog over it as over the flame. Front faces are dropped -
// the back face behind them covers the same pixel.
float4 PsSmoke(SmokeBoxVertex v) : SV_Target0
{
    SmokeConstants c = Smoke(gSmokeIndex);
    if (dot(v.normal, c.eye - v.face) > 0) discard;
    int2 hp = int2(v.pos.xy);
    // Half texel (i, j) - block of full pixels 2i..2i+1: its centre is the full-resolution position 2i + 1.
    float3 dir = SmokeRay(c, float2(hp * 2 + 1));
    SmokeResult r = MarchSmoke(c, dir, SmokeSceneT(c, hp, dir));
    float alpha = 1.0 - r.transmittance;
    return float4(r.light * (1.0 - c.fog) + c.fogColour * (c.fog * alpha), alpha);
}

// Debug statistics: the same march on a grid of rays over the frame - (steps and samples summed over windows, alpha of all windows
// "over"); thread 0 copies the bounds of window 0 after the rays.
[numthreads(FIRE_SMOKE_GROUP, FIRE_SMOKE_GROUP, 1)]
void CsSmokeStats(uint3 id : SV_DispatchThreadID)
{
    RWStructuredBuffer<uint> output = ResourceDescriptorHeap[gSmokeOut];
    SmokeConstants first = Smoke(0);
    if (id.x == 0 && id.y == 0)
    {
        StructuredBuffer<uint> bounds = ResourceDescriptorHeap[first.bounds];
        for (uint b = 0; b < 8; b++) output[FIRE_SMOKE_GRID_X * FIRE_SMOKE_GRID_Y * 3 + b] = bounds[b];
    }
    if (id.x >= FIRE_SMOKE_GRID_X || id.y >= FIRE_SMOKE_GRID_Y) return;
    int2 full = int2(round(1.0 / first.invWidth), round(1.0 / first.invHeight));
    int2 halfSize = (full + 1) / 2;
    int2 hp = int2((id.xy + 0.5) * float2(halfSize) / float2(FIRE_SMOKE_GRID_X, FIRE_SMOKE_GRID_Y));
    uint steps = 0, samples = 0;
    float transmittance = 1;
    for (uint v = 0; v < gSmokeCount; v++)
    {
        SmokeConstants c = Smoke(v);
        float3 dir = SmokeRay(c, float2(hp * 2 + 1));
        SmokeResult r = MarchSmoke(c, dir, SmokeSceneT(c, hp, dir));
        steps += r.steps;
        samples += r.samples;
        transmittance *= r.transmittance;
    }
    uint cell = (id.y * FIRE_SMOKE_GRID_X + id.x) * 3;
    output[cell] = steps;
    output[cell + 1] = samples;
    output[cell + 2] = asuint(1.0 - transmittance);
}

// Occupancy grid: one group per brick of 4^3 cells, 7^3 threads - its cells with a margin of one cell below and two above
// (cells 4b - 1 .. 4b + 5): a sample of the brick region, even a little outside it by rounding, has its trilinear corners among them.
// Max density over them, zero outside the window.
groupshared uint gsBrickMax;

[numthreads(7, 7, 7)]
void CsSmokeBricks(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    if (index == 0) gsBrickMax = 0;
    GroupMemoryBarrierWithGroupSync();
    StructuredBuffer<uint> s = ResourceDescriptorHeap[gSmokeScalars];
    int3 cell = int3(group * FIRE_SMOKE_BRICK + thread) - 1;
    float rho = 0;
    if (cell.x >= 0 && cell.y >= 0 && cell.z >= 0 && cell.x < (int)gSmokeNx && cell.y < (int)gSmokeNy && cell.z < (int)gSmokeNz)
        rho = max(f16tof32(s[(uint)cell.x + gSmokeNx * ((uint)cell.y + gSmokeNy * (uint)cell.z)]), 0.0);
    // Non-negative floats order as their bits.
    uint m = WaveActiveMax(asuint(rho));
    if (WaveIsFirstLane()) InterlockedMax(gsBrickMax, m);
    GroupMemoryBarrierWithGroupSync();
    if (index != 0) return;
    RWStructuredBuffer<float> bricks = ResourceDescriptorHeap[gSmokeOut];
    bricks[group.x + gSmokeBx * (group.y + gSmokeBy * group.z)] = asfloat(gsBrickMax);
}

// Bounds of the non-empty bricks (max density above the cut): min xyz, max xyz, count, total - one group, bricks strided over the threads.
groupshared uint gsBounds[7];

[numthreads(FIRE_SMOKE_BOUNDS_THREADS, 1, 1)]
void CsSmokeBounds(uint index : SV_GroupIndex)
{
    if (index < 3) gsBounds[index] = 0xFFFFFFFF;
    else if (index < 7) gsBounds[index] = 0;
    GroupMemoryBarrierWithGroupSync();
    StructuredBuffer<float> bricks = ResourceDescriptorHeap[gSmokeBricks];
    float cut = asfloat(gSmokeCut);
    uint total = gSmokeBx * gSmokeBy * gSmokeBz;
    uint3 lo = 0xFFFFFFFF, hi = 0;
    uint count = 0;
    for (uint i = index; i < total; i += FIRE_SMOKE_BOUNDS_THREADS)
    {
        if (!(bricks[i] > cut)) continue;
        uint3 b = uint3(i % gSmokeBx, (i / gSmokeBx) % gSmokeBy, i / (gSmokeBx * gSmokeBy));
        lo = min(lo, b);
        hi = max(hi, b);
        count++;
    }
    if (count > 0)
    {
        InterlockedMin(gsBounds[0], lo.x);
        InterlockedMin(gsBounds[1], lo.y);
        InterlockedMin(gsBounds[2], lo.z);
        InterlockedMax(gsBounds[3], hi.x);
        InterlockedMax(gsBounds[4], hi.y);
        InterlockedMax(gsBounds[5], hi.z);
        InterlockedAdd(gsBounds[6], count);
    }
    GroupMemoryBarrierWithGroupSync();
    RWStructuredBuffer<uint> output = ResourceDescriptorHeap[gSmokeOut];
    if (index < 7) output[index] = gsBounds[6] == 0 ? 0 : gsBounds[index];
    else if (index == 7) output[7] = total;
}

// Snapshot layer or screen overlay (full resolution): the half-resolution contribution with the colour compressed x / (1 + x), premultiplied.
float4 PsSmokeLayer(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> halfColor = ResourceDescriptorHeap[gSmokeColor];
    float4 s = halfColor.Load(int3(int2(pos.xy) / 2, 0));
    float3 colour = s.a > 0.0 ? s.rgb / s.a : 0.0;
    return float4(colour / (1.0 + colour) * s.a, s.a);
}
