// Blood freshness - one look for skin and floor: age, s, and thickness, mm -> the layer over a substrate. The same expressions in the same
// order as BloodLook.Evaluate (C#). Blood is an absorbing layer (Beer-Lambert): the substrate is seen through exp(-2 sigma h) per channel
// (blue-green dies within 30-50 um, red passes fractions of a millimetre), the layer itself scatters the albedo of thick blood times
// (1 - transmission). Fresh blood is glossy; the gloss goes first, then it darkens, browns and turns opaque; thick blood dries ThickSlow
// times longer. Numbers - BloodLookParams (C#), root constants 0...18 in the field order.
#ifndef SWORD_BLOOD_LOOK_HLSLI
#define SWORD_BLOOD_LOOK_HLSLI
#include "common/pass.hlsli"

struct BloodLookParams
{
    float thickMm, edgeMm, thickSlow;
    float glossStart, glossEnd, darkStart, darkEnd;
    float wetRoughness, dryRoughness;
    float3 deep, absorb, dry;
    float dryAbsorb;
};

struct BloodLookResult
{
    float3 albedo, transmission;
    float coverage, roughness, wet, dry;
};

/// Numbers of the look from root constants 0...18 (BloodLook.Write).
BloodLookParams BloodLookFromPass()
{
    BloodLookParams p;
    p.thickMm = asfloat(gPass0);
    p.edgeMm = asfloat(gPass1);
    p.thickSlow = asfloat(gPass2);
    p.glossStart = asfloat(gPass3);
    p.glossEnd = asfloat(gPass4);
    p.darkStart = asfloat(gPass5);
    p.darkEnd = asfloat(gPass6);
    p.wetRoughness = asfloat(gPass7);
    p.dryRoughness = asfloat(gPass8);
    p.deep = float3(asfloat(gPass9), asfloat(gPass10), asfloat(gPass11));
    p.absorb = float3(asfloat(gPass12), asfloat(gPass13), asfloat(gPass14));
    p.dry = float3(asfloat(gPass15), asfloat(gPass16), asfloat(gPass17));
    p.dryAbsorb = asfloat(gPass18);
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
    float h = max(thicknessMm, 0);
    float thick = saturate(h / p.thickMm);
    float slow = 1 + (p.thickSlow - 1) * thick;
    float a = age / slow;
    o.wet = 1 - BloodSmooth(p.glossStart, p.glossEnd, a);
    o.dry = BloodSmooth(p.darkStart, p.darkEnd, a);
    o.albedo = p.deep + (p.dry - p.deep) * o.dry;
    float3 sigma = p.absorb + p.dryAbsorb * o.dry;
    o.transmission = exp(-2 * sigma * h);
    o.roughness = p.wetRoughness + (p.dryRoughness - p.wetRoughness) * (1 - o.wet);
    o.coverage = BloodSmooth(0, p.edgeMm, h);
    return o;
}

/// The layer over a substrate s: s * BloodMul + BloodAdd.
float3 BloodMul(BloodLookResult o) { return 1 - o.coverage * (1 - o.transmission); }
float3 BloodAdd(BloodLookResult o) { return o.coverage * o.albedo * (1 - o.transmission); }

#endif
