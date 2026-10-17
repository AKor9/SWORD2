// Glossy surface highlight (wet film: blood on skin and floor, drops): GGX sun specular with Smith visibility and Schlick Fresnel, plus
// the sky reflection by the split-sum EnvBRDFApprox (Karis 2014, mobile). Light units - those of the scene: sun = the multiplier of
// albedo * N.L (the Lambert radiance pi * sun), sky = the radiance of the sky in the reflected direction. Only functions, no resources.
#ifndef SWORD_SURFACE_HLSLI
#define SWORD_SURFACE_HLSLI

static const float SURFACE_PI = 3.14159265;

/// GGX normal distribution; a2 = roughness^4 (alpha = roughness^2).
float SurfaceGgx(float nh, float a2)
{
    float d = nh * nh * (a2 - 1) + 1;
    return a2 / (SURFACE_PI * d * d);
}

/// Smith-GGX joint visibility (approximation, Heitz 2014): V = G / (4 N.L N.V).
float SurfaceVisibility(float nl, float nv, float a)
{
    float l = nv * (nl * (1 - a) + a);
    float v = nl * (nv * (1 - a) + a);
    return 0.5 / max(l + v, 1e-5);
}

float SurfaceSchlick(float f0, float vh)
{
    float k = 1 - vh;
    float k2 = k * k;
    return f0 + (1 - f0) * k2 * k2 * k;
}

/// Split-sum terms of the environment specular without a LUT (Karis, "Physically Based Shading on Mobile"): f0 * x + y.
float2 SurfaceEnvBrdfAb(float roughness, float nv)
{
    const float4 c0 = float4(-1, -0.0275, -0.572, 0.022);
    const float4 c1 = float4(1, 0.0425, 1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nv)) * r.x + r.y;
    return float2(-1.04, 1.04) * a004 + r.zw;
}

/// Split-sum specular of the environment without a LUT (Karis, "Physically Based Shading on Mobile").
float SurfaceEnvBrdfApprox(float f0, float roughness, float nv)
{
    float2 ab = SurfaceEnvBrdfAb(roughness, nv);
    return f0 * ab.x + ab.y;
}

/// Specular radiance of a glossy surface: sun (direction l to the sun, colour sun in the scene's albedo*N.L units) and sky (radiance in the
/// reflected direction). n, v, l - unit; roughness - perceptual GGX 0...1.
float3 SurfaceSpecular(float3 n, float3 v, float3 l, float3 sun, float3 skyReflected, float f0, float roughness)
{
    float r = clamp(roughness, 0.03, 1);
    float a = r * r;
    float nv = saturate(dot(n, v)) + 1e-4;
    float nl = saturate(dot(n, l));
    float3 h = v + l;
    h *= rsqrt(max(dot(h, h), 1e-12));   // v = -l: no half vector (spec * nl = 0), without NaN
    float nh = saturate(dot(n, h));
    float vh = saturate(dot(v, h));
    float spec = SurfaceGgx(nh, a * a) * SurfaceVisibility(nl, nv, a) * SurfaceSchlick(f0, vh);
    return SURFACE_PI * sun * (spec * nl) + skyReflected * SurfaceEnvBrdfApprox(f0, r, nv);
}

/// The same with a coloured f0 (metals: f0 = albedo): Schlick and the split sum per channel.
float3 SurfaceSpecular3(float3 n, float3 v, float3 l, float3 sun, float3 skyReflected, float3 f0, float roughness)
{
    float r = clamp(roughness, 0.03, 1);
    float a = r * r;
    float nv = saturate(dot(n, v)) + 1e-4;
    float nl = saturate(dot(n, l));
    float3 h = v + l;
    h *= rsqrt(max(dot(h, h), 1e-12));
    float nh = saturate(dot(n, h));
    float k = 1 - saturate(dot(v, h));
    float k5 = k * k * k * k * k;
    float3 fresnel = f0 + (1 - f0) * k5;
    float2 ab = SurfaceEnvBrdfAb(r, nv);
    return SURFACE_PI * sun * (SurfaceGgx(nh, a * a) * SurfaceVisibility(nl, nv, a) * nl) * fresnel + skyReflected * (f0 * ab.x + ab.y);
}

#endif
