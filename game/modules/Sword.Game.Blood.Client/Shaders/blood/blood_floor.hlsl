// Blood on the floor (BloodFloorGpu.cs). The field of the window - Fluid2D C (uint2: half vx | vy, half rho | wet), rho is the thickness /
// h_max, the age - float per texel; the window is BLOOD_FLOOR_N^2 texels, texel (x, y) - x along +X, y along +Z. The map of dried stains -
// uint per texel: half rho | half birth time (s, floor clock), BLOOD_MAP_N^2 with the same texel.
// CsMapClear - zero the map; CsTransfer - texels of the window that leave it with the shift go to the map (before the step);
// VsFull/PsFloor - Composite: the film over static receivers (map below, field above), premultiplied into HDR;
// VsDrop/PsDrop - Opaque: flying drops as capsule impostors stretched along the velocity.
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"
#include "common/surface.hlsli"
#include "common/fog.hlsli"
#include "blood/blood_look.hlsli"
#include "blood/blood_flight.hlsli"

// Root constants: 0...17 - the look numbers (BloodLook.Write); compute - gFloorC, gFloorAge, gFloorMap, gFloorShiftX, gFloorShiftZ,
// gFloorMapX, gFloorMapZ, gFloorNow; draw - gFloorData (FloorConstants), gFloorDrops (DropGpu).

struct FloorConstants
{
    float4 viewProj0; float4 viewProj1; float4 viewProj2; float4 viewProj3;   // rows, vector on the left
    float4 invViewProj0; float4 invViewProj1; float4 invViewProj2; float4 invViewProj3;
    float3 eye; float invWidth;
    float3 sunDir; float invHeight;
    float3 sun; float fogStart;
    float3 sky; float fogRange;
    float3 fogColour; float now;
    float2 windowMin; float cosMax; float heightBand;
    float2 mapMin; float thickMmPerRho; float normalScale;
    uint field; uint age; uint map; uint depth;
    uint normal; uint terrain; uint terrainCells; float terrainHalf;
    uint hasField; uint hasMap; uint hasTerrain; float f0;
    float drag; float stretch; float dropThickMm; float pad0;
    float fogDensity; float fogFalloff; float2 pad1;
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
    int2 m = o + int2(asint(gFloorMapX), asint(gFloorMapZ));
    if (any(m < 0) || any(m >= BLOOD_MAP_N)) return;
    RWStructuredBuffer<uint> map = ResourceDescriptorHeap[gFloorMap];
    uint j = m.x + m.y * BLOOD_MAP_N;
    uint old = map[j];
    float r0 = f16tof32(old), b0 = f16tof32(old >> 16);
    float r1 = r0 + rho;
    float b1 = (b0 * r0 + (asfloat(gFloorNow) - age[i]) * rho) / r1;
    map[j] = f32tof16(r1) | (f32tof16(b1) << 16);
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

// Bilinear rho of the field at texel coordinates f (texel centres at integers).
float FieldBilinear(StructuredBuffer<uint2> c, float2 f)
{
    float2 i = floor(f);
    float2 w = f - i;
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

float3 Lit(FloorConstants c, BloodLookResult look, float3 n, float3 v, float3 world)
{
    float3 l = c.sunDir;
    float3 diffuse = look.albedo * (c.sun * saturate(dot(n, l)) + c.sky);
    float3 spec = SurfaceSpecular(n, v, l, c.sun, c.sky, c.f0, look.roughness);
    float fog = FogFactor(c.eye, world, c.fogStart, c.fogRange, c.fogDensity, c.fogFalloff);
    return lerp(diffuse + spec, c.fogColour, fog);
}

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PsFloor(float4 pos : SV_Position) : SV_Target0
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

    float4 o = 0;
    if (c.hasMap != 0)
    {
        StructuredBuffer<uint> map = ResourceDescriptorHeap[c.map];
        float2 f = (p.xz - c.mapMin) / BLOOD_TEXEL - 0.5;
        float2 i = floor(f);
        float2 wt = f - i;
        int2 t = int2(i);
        float b00, b10, b01, b11;
        float r00 = MapRho(map, t, b00), r10 = MapRho(map, t + int2(1, 0), b10), r01 = MapRho(map, t + int2(0, 1), b01),
              r11 = MapRho(map, t + int2(1, 1), b11);
        float rho = lerp(lerp(r00, r10, wt.x), lerp(r01, r11, wt.x), wt.y);
        if (rho > 0)
        {
            float wsum = r00 + r10 + r01 + r11;
            float birth = (b00 * r00 + b10 * r10 + b01 * r01 + b11 * r11) / max(wsum, 1e-20);
            BloodLookResult look = BloodLookEval(max(c.now - birth, 0), rho * c.thickMmPerRho, lp);
            o = float4(Lit(c, look, n, v, p) * look.coverage, look.coverage);
        }
    }
    if (c.hasField != 0)
    {
        float2 f = (p.xz - c.windowMin) / BLOOD_TEXEL - 0.5;
        if (all(f > -1) && all(f < BLOOD_FLOOR_N))
        {
            StructuredBuffer<uint2> field = ResourceDescriptorHeap[c.field];
            float rho = FieldBilinear(field, f);
            if (rho > 0)
            {
                StructuredBuffer<float> ageBuf = ResourceDescriptorHeap[c.age];
                int2 t = clamp(int2(floor(f + 0.5)), 0, BLOOD_FLOOR_N - 1);
                float age = ageBuf[t.x + t.y * BLOOD_FLOOR_N];
                // Film normal: thickness differences (central, one texel) in the receiver basis, exaggerated by normalScale.
                float k = c.thickMmPerRho * 1e-3 / (2 * BLOOD_TEXEL) * c.normalScale;
                float gx = (FieldBilinear(field, f + float2(1, 0)) - FieldBilinear(field, f - float2(1, 0))) * k;
                float gz = (FieldBilinear(field, f + float2(0, 1)) - FieldBilinear(field, f - float2(0, 1))) * k;
                float3 g = float3(gx, 0, gz);
                g -= n * dot(n, g);
                float3 nf = normalize(n - g);
                BloodLookResult look = BloodLookEval(age, rho * c.thickMmPerRho, lp);
                float a = look.coverage;
                o = float4(Lit(c, look, nf, v, p) * a, a) + o * (1 - a);
            }
        }
    }
    if (!(o.a > 0)) discard;
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
