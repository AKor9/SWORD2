// Fire flame (material MI_Torch4 of LIVE-2): the sim area as a camera-facing quad over the HDR scene, transparent phase, premultiplied
// alpha, scene depth tested (no write) and read for the soft edge. Per-quad constants - StructuredBuffer<FlameConstants> gFlameData,
// element gFlameIndex (FireFlame.cs; CPU reference - FlameMath). Field output RGBA16F: (v.xy, rho, wet).
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"
#include "common/fog.hlsli"

struct FlameConstants
{
    float4 viewProj0; float4 viewProj1; float4 viewProj2; float4 viewProj3;   // rows, vector on the left (System.Numerics)
    float3 center; float phase;              // phase = frac(FlowSpeed * t)
    float3 u; float worldOffsetX;            // half axis to the right, m
    float3 v; float worldOffsetY;            // half axis up, m (uv.y grows down)
    float3 eye; float fadeDistance;          // m
    float3 fogColour; float fogStart;
    float3 colour1; float fogRange;
    float3 colour2; float flowStrength;
    float velocityClamp; float flowTile; float densityGain; float noisePower;
    float densityPower; float edgeMask; float2 viewDepth;   // viewDepth - k of SceneViewDepth
    uint field; uint noise; uint noiseLayer; uint sceneDepth;
    float glowCut; float fogDensity; float fogFalloff; float pad;   // glowCut - k of the full alpha (0 - off); height fog
};
_Static_assert(sizeof(FlameConstants) == FIRE_FLAME_CONSTANTS_SIZE, "FlameConstants differs from C# FlameConstants");

static const float FlamePi = 3.14159265;

FlameConstants Flame()
{
    StructuredBuffer<FlameConstants> data = ResourceDescriptorHeap[gFlameData];
    return data[gFlameIndex];
}

struct FlameVs
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    float3 world : TEXCOORD1;
};

// Two triangles; uv (0, 0) - top left of the area, v grows down (as the sim field).
static const float2 FlameCorners[6] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(0, 1), float2(1, 0), float2(1, 1) };

FlameVs VsFlame(uint vid : SV_VertexID)
{
    FlameConstants c = Flame();
    float2 uv = FlameCorners[vid];
    float3 p = c.center + (2.0 * uv.x - 1.0) * c.u + (1.0 - 2.0 * uv.y) * c.v;
    FlameVs o;
    o.pos = mul(float4(p, 1.0), float4x4(c.viewProj0, c.viewProj1, c.viewProj2, c.viewProj3));
    o.uv = uv;
    o.world = p;
    return o;
}

// Premultiplied contribution: rgb = colour * alpha, a = alpha.
float4 FlameShade(FlameVs i)
{
    FlameConstants c = Flame();
    Texture2D<float4> field = ResourceDescriptorHeap[c.field];
    float4 s = field.SampleLevel(gLinearClamp, i.uv, 0);
    // min(rho, 1) as LIVE-2; below 0 - 0 (pow of a negative is NaN).
    float rho = clamp(s.z, 0.0, 1.0);

    // Two-phase flow map of the crumple noise along the sim velocity; details bound to the world by WorldOffset.
    float2 vel = -clamp(s.xy, -c.velocityClamp, c.velocityClamp) * c.flowStrength;
    float ph = c.phase;
    float2 uvF = c.flowTile * (i.uv + float2(c.worldOffsetX, c.worldOffsetY));
    Texture2DArray<float4> noise = ResourceDescriptorHeap[c.noise];
    float a = noise.Sample(gLinearWrap, float3((uvF + (ph - 0.5) * vel + 1.0) * 0.5, c.noiseLayer)).r;
    float b = noise.Sample(gLinearWrap, float3((uvF + (frac(ph + 0.5) - 0.5) * vel + 1.0) * 0.5, c.noiseLayer)).r;
    float n = lerp(a, b, abs(2.0 * ph - 1.0));

    // Ramp Colour2 -> Colour1 by k = FlowDensityMult n^FlowDensityPow rho ColorMult rho^ColorPow, no saturation (HDR core for bloom).
    float k = c.densityGain * pow(max(n, 0.0), c.noisePower) * pow(rho, c.densityPower);
    float3 emis = max(c.colour2 + k * (c.colour1 - c.colour2), 0.0);

    float edge = c.edgeMask > 0.5 ? sin(FlamePi * i.uv.x) * sin(FlamePi * i.uv.y) : 1.0;
    Texture2D<float> depth = ResourceDescriptorHeap[c.sceneDepth];
    float fade = DepthFade(SceneViewDepth(depth.Load(int3(i.pos.xy, 0)), c.viewDepth), SceneViewDepth(i.pos.z, c.viewDepth), c.fadeDistance);
    // Tail cut: the weak tail of the sim (k near 0 - Colour2 veil) fades out; the smoke is the 3D gas.
    float glow = c.glowCut > 0.0 ? saturate(k / c.glowCut) : 1.0;
    float alpha = saturate(rho * edge * fade) * glow;

    // Scene fog as Shade (linear by distance and height fog): lerp(emis, fog, f).
    float fog = FogFactor(c.eye, i.world, c.fogStart, c.fogRange, c.fogDensity, c.fogFalloff);
    float3 colour = emis * (1.0 - fog) + c.fogColour * fog;
    return float4(colour * alpha, alpha);
}

float4 PsFlame(FlameVs i) : SV_Target0
{
    return FlameShade(i);
}

// Snapshot layer (Rgba8): the same contribution with the colour compressed x / (1 + x) per channel, premultiplied.
float4 PsFlameLayer(FlameVs i) : SV_Target0
{
    float4 f = FlameShade(i);
    float3 colour = f.a > 0.0 ? f.rgb / f.a : 0.0;
    return float4(colour / (1.0 + colour) * f.a, f.a);
}
