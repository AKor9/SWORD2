// Scene fog, the same for opaque and transparent: linear by the distance to the eye (start, range) plus exponential height fog -
// density * exp(-falloff * y) per metre, integrated along the ray eye -> p. Combined as f = fd + (1 - fd) * fh, so without height fog
// (density 0) it is exactly the linear fog. Colour -> lerp(colour, fog colour, f).
#ifndef SWORD_FOG_HLSLI
#define SWORD_FOG_HLSLI

float FogFactor(float3 eye, float3 p, float start, float range, float density, float falloff)
{
    float d = distance(p, eye);
    float fd = saturate((d - start) / range);
    if (density <= 0) return fd;
    // Integral of exp(-falloff * y) along the ray over its length: exp(-falloff * eye.y) * (1 - exp(-h)) / h, h - the height change.
    float h = falloff * (p.y - eye.y);
    float along = abs(h) > 1e-4 ? (1 - exp(-h)) / h : 1;
    float fh = 1 - exp(-density * d * exp(-falloff * eye.y) * along);
    return fd + (1 - fd) * fh;
}

#endif
