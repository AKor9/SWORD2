// Blood freshness - one look for skin and floor: age, s, and thickness, mm -> linear albedo, coverage, GGX roughness. The same expressions
// in the same order as BloodLook.Evaluate (C#). Fresh blood is scarlet and glossy (thin edge darker), the gloss goes first, then it darkens
// and browns; thick blood dries ThickSlow times longer. Numbers - BloodLookParams (C#), root constants 0...17 in the field order.
#ifndef SWORD_BLOOD_LOOK_HLSLI
#define SWORD_BLOOD_LOOK_HLSLI
#include "common/pass.hlsli"

struct BloodLookParams
{
    float thickMm, coverMm, thickSlow;
    float glossStart, glossEnd, darkStart, darkEnd;
    float wetRoughness, dryRoughness;
    float3 thick, thin, dry;
};

struct BloodLookResult
{
    float3 albedo;
    float coverage, roughness, wet, dry;
};

/// Numbers of the look from root constants 0...17 (BloodLook.Write).
BloodLookParams BloodLookFromPass()
{
    BloodLookParams p;
    p.thickMm = asfloat(gPass0);
    p.coverMm = asfloat(gPass1);
    p.thickSlow = asfloat(gPass2);
    p.glossStart = asfloat(gPass3);
    p.glossEnd = asfloat(gPass4);
    p.darkStart = asfloat(gPass5);
    p.darkEnd = asfloat(gPass6);
    p.wetRoughness = asfloat(gPass7);
    p.dryRoughness = asfloat(gPass8);
    p.thick = float3(asfloat(gPass9), asfloat(gPass10), asfloat(gPass11));
    p.thin = float3(asfloat(gPass12), asfloat(gPass13), asfloat(gPass14));
    p.dry = float3(asfloat(gPass15), asfloat(gPass16), asfloat(gPass17));
    return p;
}

float BloodSmooth(float a, float b, float x)
{
    float t = b > a ? saturate((x - a) / (b - a)) : (x >= a ? 1.0 : 0.0);   // a = b from the tuning panel: a step, not 0/0
    return t * t * (3 - 2 * t);
}

BloodLookResult BloodLookEval(float age, float thicknessMm, BloodLookParams p)
{
    BloodLookResult o;
    float thick = saturate(thicknessMm / p.thickMm);
    float slow = 1 + (p.thickSlow - 1) * thick;
    float a = age / slow;
    o.wet = 1 - BloodSmooth(p.glossStart, p.glossEnd, a);
    o.dry = BloodSmooth(p.darkStart, p.darkEnd, a);
    float3 fresh = p.thin + (p.thick - p.thin) * thick;
    o.albedo = fresh + (p.dry - fresh) * o.dry;
    o.roughness = p.wetRoughness + (p.dryRoughness - p.wetRoughness) * (1 - o.wet);
    o.coverage = 1 - exp(-thicknessMm / p.coverMm);
    return o;
}

#endif
