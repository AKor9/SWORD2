// Smoke from the flame: the field C of a step of the flame's 2D sim -> the map source of the fire's 3D gas (Fluid3DPlane) where the flame
// fades. A map texel (i, j) - one gas cell on the flame area around the knob (columns along the area x, rows up the area) - averages
// Sub^2 bilinear samples of C: weight w = rho (1 - visibility), visibility = saturate(gain rho^power / cut) (the flame at the mean noise),
// amount A = sum w / Sub^2 * zone, zone = saturate((R - r) / edge) saturate((y - floor) / edge + 1/2) by the distance r from the knob and
// the height y up the area (the flame's fringe at the knob gives no smoke). Out: density A*rate, temperature
// A*rate, blend min(A*rate, 1), velocity - the w-weighted field velocity in m/s, x along the area x, y along the world vertical.
// Root constants - FireGasFeed.cs (CPU reference - FireGasFeed.Texel, the same float expressions in the same order).
#include "common/pass.hlsli"

float4 FeedLoad(StructuredBuffer<uint2> c, int n, int x, int y)
{
    x = clamp(x, 0, n - 1);
    y = clamp(y, 0, n - 1);
    uint2 u = c[y * n + x];
    return float4(f16tof32(u.x), f16tof32(u.x >> 16), f16tof32(u.y), f16tof32(u.y >> 16));
}

// Bilinear at uv (texel centres at (i + 1/2) / n), the edge texel beyond the edge.
float4 FeedBilinear(StructuredBuffer<uint2> c, int n, float u, float v)
{
    float px = u * (float)n - 0.5, py = v * (float)n - 0.5;
    float fx = floor(px), fy = floor(py);
    float tx = px - fx, ty = py - fy;
    int x = (int)fx, y = (int)fy;
    float4 a = FeedLoad(c, n, x, y);
    float4 b = FeedLoad(c, n, x + 1, y);
    float4 d = FeedLoad(c, n, x, y + 1);
    float4 e = FeedLoad(c, n, x + 1, y + 1);
    float4 r0 = a + (b - a) * tx;
    float4 r1 = d + (e - d) * tx;
    return r0 + (r1 - r0) * ty;
}

[numthreads(FIRE_FEED_GROUP, FIRE_FEED_GROUP, 1)]
void CsFeed(uint3 id : SV_DispatchThreadID)
{
    int side = (int)gFeedSide;
    int i = (int)id.x, j = (int)id.y;
    if (i >= side || j >= side) return;
    StructuredBuffer<uint2> field = ResourceDescriptorHeap[gFeedField];
    RWStructuredBuffer<uint4> map = ResourceDescriptorHeap[gFeedMap];
    int n = (int)gFeedN, sub = (int)gFeedSub;
    float texel = asfloat(gFeedTexel), invSub = asfloat(gFeedInvSub), invExtX = asfloat(gFeedInvExtX), invExtY = asfloat(gFeedInvExtY);
    float gain = asfloat(gFeedGain), power = asfloat(gFeedPower), invCut = asfloat(gFeedInvCut);
    float half_ = 0.5 * (float)side;
    float xc = ((float)i + 0.5 - half_) * texel, yc = ((float)j + 0.5 - half_) * texel;
    float zone = saturate((asfloat(gFeedRadius) - sqrt(xc * xc + yc * yc)) * asfloat(gFeedInvZoneEdge)) *
        saturate((yc - asfloat(gFeedFloor)) * asfloat(gFeedInvZoneEdge) + 0.5);
    float w = 0, vx = 0, vy = 0;
    if (zone > 0)
    {
        for (int b = 0; b < sub; b++)
            for (int a = 0; a < sub; a++)
            {
                float x = ((float)i + ((float)a + 0.5) * invSub - half_) * texel;
                float y = ((float)j + ((float)b + 0.5) * invSub - half_) * texel;
                float4 s = FeedBilinear(field, n, 0.5 + x * invExtX, 0.5 - y * invExtY);
                float rho = saturate(s.z);
                float vis = saturate(gain * pow(rho, power) * invCut);
                float weight = rho * (1.0 - vis);
                w += weight;
                vx += weight * s.x;
                vy += weight * s.y;
            }
    }
    float amount = w * invSub * invSub * zone;
    float3 velocity = float3(0, 0, 0);
    if (w > 0)
    {
        float3 ax = float3(asfloat(gFeedAxisX), asfloat(gFeedAxisY), asfloat(gFeedAxisZ));
        float sx = vx / w * asfloat(gFeedScaleX), sy = -(vy / w) * asfloat(gFeedScaleUp);
        velocity = float3(ax.x * sx, ax.y * sx + sy, ax.z * sx);
    }
    float density = amount * asfloat(gFeedRateDensity), temperature = amount * asfloat(gFeedRateTemperature);
    float blend = min(amount * asfloat(gFeedRateBlend), 1.0);
    map[gFeedOffset + (uint)(i + side * j)] = uint4(f32tof16(density) | (f32tof16(temperature) << 16), f32tof16(blend),
        f32tof16(velocity.x) | (f32tof16(velocity.y) << 16), f32tof16(velocity.z));
}
