// Fire smoke (material MI_HVOL_SIM_TorchSmoke of LIVE-2 in Heterogeneous Volumes): a ray march of the volume around the sim area into
// the half-resolution transparent target, premultiplied, one full-screen triangle per volume (far volumes first). The ray is rebuilt from
// the pixel by the inverse view-projection; the segment is the slab of the volume (sigma = 0 outside it), clipped by the half-resolution
// scene depth. Per-volume constants - StructuredBuffer<SmokeConstants> gSmokeData, element gSmokeIndex (FireSmoke.cs; CPU reference -
// SmokeMath). Field output RGBA16F: (v.xy, rho, wet).
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"
#include "common/lights.hlsli"

struct SmokeConstants
{
    float4 invViewProj0; float4 invViewProj1; float4 invViewProj2; float4 invViewProj3;   // rows, vector on the left
    float3 eye; float stepLength;            // m
    float3 forward; uint maxSteps;           // camera axis (for the view depth of the scene)
    float3 center; float slabHalf;           // slab half depth, m
    float3 axisU; float extentX;             // unit axes of the volume: right, up, to the viewer; extents, m
    float3 axisV; float extentY;
    float3 axisW; float extentZ;
    float3 sun; float albedo;                // sun as a light: illuminance of the UE light
    float3 ambient; float unitsPerMetre;     // sky of the fake ambient; sigma units per metre of path
    float3 fogColour; float fog;
    float edgeMask; float densityMult; float densityPow; float extrude;
    float extrudeBias; float extrudeSoften; float softenPower; float basePlane;
    float gradientMult; float gradientTop; float gradientSpan; float ambientMult;
    float ambientPow; float ambientAoPow; float invWidth; float invHeight;
    float2 viewDepth; uint field; uint sceneDepth;
    uint lights; uint lightCount; uint pad0; uint pad1;
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
};

float SmokeSafe(float x) { return abs(x) < 1e-8 ? (x < 0 ? -1e-8 : 1e-8) : x; }

// Ray direction through the full-resolution pixel position px (pixel centre at .5).
float3 SmokeRay(SmokeConstants c, float2 px)
{
    float2 ndc = float2(px.x * c.invWidth * 2.0 - 1.0, 1.0 - px.y * c.invHeight * 2.0);
    float4 p = mul(float4(ndc, 1.0, 1.0), float4x4(c.invViewProj0, c.invViewProj1, c.invViewProj2, c.invViewProj3));
    return normalize(p.xyz / p.w - c.eye);
}

// Sigma of the material at uvw with field density rho: edge mask x DensityMult rho^DensityPow x slab^power.
float SmokeSigma(SmokeConstants c, float rho, float3 uvw)
{
    float slab = 1.0 - saturate((abs(uvw.z - c.basePlane) - c.extrude * rho + c.extrudeBias) / c.extrudeSoften);
    float edge = min(saturate((0.5 - abs(uvw.x - 0.5)) * c.edgeMask), saturate((0.5 - abs(uvw.y - 0.5)) * c.edgeMask));
    return edge * c.densityMult * pow(rho, c.densityPow) * pow(slab, c.softenPower);
}

// March of one ray eye + t dir up to the scene (t <= sceneT): midpoints of equal steps of the slab segment, at most maxSteps;
// L += T (E + S) k, T *= exp(-sigma k), k = step x units per metre (integral of HV: emission and single scattering, Beer-Lambert).
SmokeResult MarchSmoke(SmokeConstants c, float3 dir, float sceneT)
{
    SmokeResult r;
    r.light = 0;
    r.transmittance = 1;
    r.steps = 0;
    float3 rel = c.eye - c.center;
    float3 o = float3(dot(rel, c.axisU), dot(rel, c.axisV), dot(rel, c.axisW));
    float3 d = float3(dot(dir, c.axisU), dot(dir, c.axisV), dot(dir, c.axisW));
    float3 h = float3(c.extentX * 0.5, c.extentY * 0.5, c.slabHalf);
    float3 inv = float3(1.0 / SmokeSafe(d.x), 1.0 / SmokeSafe(d.y), 1.0 / SmokeSafe(d.z));
    float3 ta = (-h - o) * inv;
    float3 tb = (h - o) * inv;
    float3 tmin = min(ta, tb);
    float3 tmax = max(ta, tb);
    float t0 = max(max(tmin.x, tmin.y), max(tmin.z, 0.0));
    float t1 = min(min(tmax.x, tmax.y), min(tmax.z, sceneT));
    if (!(t1 > t0)) return r;
    uint n = (uint)min(ceil((t1 - t0) / c.stepLength), (float)c.maxSteps);
    n = max(n, 1u);
    float dt = (t1 - t0) / n;
    float k = c.unitsPerMetre * dt;
    Texture2D<float4> field = ResourceDescriptorHeap[c.field];
    StructuredBuffer<SmokeLightData> lights = ResourceDescriptorHeap[c.lights];
    r.steps = n;
    [loop]
    for (uint i = 0; i < n; i++)
    {
        float t = t0 + (i + 0.5) * dt;
        float3 lp = o + d * t;
        float3 uvw = float3(lp.x / c.extentX + 0.5, 0.5 - lp.y / c.extentY, lp.z / c.extentZ + 0.5);
        float rho = saturate(field.SampleLevel(gLinearClamp, uvw.xy, 0).z);
        float sigma = SmokeSigma(c, rho, uvw);
        if (!(sigma > 0)) continue;
        // Fake ambient: sky with "AO" by density, darker towards the far half of the slab.
        float g = saturate(1.0 - c.gradientMult * (c.gradientTop - uvw.z) / c.gradientSpan);
        float3 emission = c.ambient * (c.ambientMult * pow(sigma, c.ambientPow) * (1.0 - pow(sigma, c.ambientAoPow)) * g);
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

// Premultiplied contribution of one volume to the half-resolution target: scene fog over it as over the flame.
float4 PsSmoke(float4 pos : SV_Position) : SV_Target0
{
    SmokeConstants c = Smoke(gSmokeIndex);
    int2 hp = int2(pos.xy);
    // Half texel (i, j) - block of full pixels 2i..2i+1: its centre is the full-resolution position 2i + 1.
    float3 dir = SmokeRay(c, float2(hp * 2 + 1));
    SmokeResult r = MarchSmoke(c, dir, SmokeSceneT(c, hp, dir));
    float alpha = 1.0 - r.transmittance;
    return float4(r.light * (1.0 - c.fog) + c.fogColour * (c.fog * alpha), alpha);
}

// Agent statistics: the same march on a grid of rays over the frame - (steps summed over volumes, alpha of all volumes "over").
[numthreads(FIRE_SMOKE_GROUP, FIRE_SMOKE_GROUP, 1)]
void CsSmokeStats(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= FIRE_SMOKE_GRID_X || id.y >= FIRE_SMOKE_GRID_Y) return;
    SmokeConstants first = Smoke(0);
    int2 full = int2(round(1.0 / first.invWidth), round(1.0 / first.invHeight));
    int2 halfSize = (full + 1) / 2;
    int2 hp = int2((id.xy + 0.5) * float2(halfSize) / float2(FIRE_SMOKE_GRID_X, FIRE_SMOKE_GRID_Y));
    uint steps = 0;
    float transmittance = 1;
    for (uint v = 0; v < gSmokeCount; v++)
    {
        SmokeConstants c = Smoke(v);
        float3 dir = SmokeRay(c, float2(hp * 2 + 1));
        SmokeResult r = MarchSmoke(c, dir, SmokeSceneT(c, hp, dir));
        steps += r.steps;
        transmittance *= r.transmittance;
    }
    RWStructuredBuffer<uint> output = ResourceDescriptorHeap[gSmokeOut];
    uint cell = (id.y * FIRE_SMOKE_GRID_X + id.x) * 2;
    output[cell] = steps;
    output[cell + 1] = asuint(1.0 - transmittance);
}

// Snapshot layer or screen overlay (full resolution): the half-resolution contribution with the colour compressed x / (1 + x), premultiplied.
float4 PsSmokeLayer(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> halfColor = ResourceDescriptorHeap[gSmokeColor];
    float4 s = halfColor.Load(int3(int2(pos.xy) / 2, 0));
    float3 colour = s.a > 0.0 ? s.rgb / s.a : 0.0;
    return float4(colour / (1.0 + colour) * s.a, s.a);
}
