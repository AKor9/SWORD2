// Fire field view (debug): the sim area as a camera-facing quad over the frame, density rho of the field output as a heat map,
// semi-transparent. Camera - view-projection in gPass0..gPass15 (rows of Matrix4x4, vector on the left, as debug.hlsl); area center and
// half-axes in the world (u - right, v - up); field output RGBA16F (v.xy, rho, wet) - bindless SRV.
#include "common/pass.hlsli"

struct FieldVs
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

float4x4 FieldViewProj()
{
    return float4x4(
        asfloat(gPass0), asfloat(gPass1), asfloat(gPass2), asfloat(gPass3),
        asfloat(gPass4), asfloat(gPass5), asfloat(gPass6), asfloat(gPass7),
        asfloat(gPass8), asfloat(gPass9), asfloat(gPass10), asfloat(gPass11),
        asfloat(gPass12), asfloat(gPass13), asfloat(gPass14), asfloat(gPass15));
}

// Two triangles; uv (0, 0) - top left of the area, v grows down.
static const float2 FieldCorners[6] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(0, 1), float2(1, 0), float2(1, 1) };

FieldVs VsFireField(uint vid : SV_VertexID)
{
    float2 uv = FieldCorners[vid];
    float3 c = float3(asfloat(gFireCx), asfloat(gFireCy), asfloat(gFireCz));
    float3 u = float3(asfloat(gFireUx), asfloat(gFireUy), asfloat(gFireUz));
    float3 v = float3(asfloat(gFireVx), asfloat(gFireVy), asfloat(gFireVz));
    float3 p = c + (2.0 * uv.x - 1.0) * u + (1.0 - 2.0 * uv.y) * v;
    FieldVs o;
    o.pos = mul(float4(p, 1.0), FieldViewProj());
    o.uv = uv;
    return o;
}

// Heat scale: 0 - black, 1/3 - red, 2/3 - yellow, 1 - white.
float3 Heat(float x)
{
    return saturate(float3(3.0 * x, 3.0 * x - 1.0, 3.0 * x - 2.0));
}

float4 PsFireField(FieldVs i) : SV_Target0
{
    Texture2D<float4> field = ResourceDescriptorHeap[gFireField];
    float rho = field.SampleLevel(gLinearClamp, i.uv, 0).z;
    // The empty area stays visible as a faint dark square; density shows up by 1/4.
    float a = asfloat(gFireOpacity) * max(saturate(4.0 * rho), 0.15);
    return float4(Heat(saturate(rho)), a);
}
