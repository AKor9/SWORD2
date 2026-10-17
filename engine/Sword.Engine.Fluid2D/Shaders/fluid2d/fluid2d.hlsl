// 2D fluid core (LIVE-2 port): one sim step = [Painter] -> Composite -> Advection -> Divergence -> Pressure x2, then Export and stats.
// Formulas - FSR 3.4 / BR 4, 6, 8.2; the same arithmetic in C# is Fluid2DReference and Fluid2DPainterReference (CPU references).
// Grids are structured buffers of half bits: uint2 = four halves per texel (RGBA16F layout), pressure uint = (P, D),
// brush uint4 = eight halves (PainterGrid layers 0..7); brush points and binning of a step - uint words (Fluid2DBrush).
// Sampling is done by hand from loads: bilinear with edge clamp (the clamp sampler of L2) in plain float math, so GPU and CPU
// agree to half rounding. Noise textures: bilinear with wrap, mip 0.
#include "common/pass.hlsli"

struct FluidConstants
{
    uint N; uint BrushN; float InvN; float SimSpeed;
    float2 Wod; float2 VelocityBase;
    float VelocityFeedback; float VelocityClamp; float CollisionFloor; float Weaken;
    float BrushVelocity; float VeloStrength; float Eraser; float DensityFloor;
    uint AmpEnabled; float AmpNoise; float AmpSize; uint DensityEnabled;
    float2 AmpOffset; float2 DensityOffset;
    float DensityAmount; float DensityScale; float DirNoiseAmp; float DirNoiseMask;
    float2 DirNoiseOffset; float DirNoiseScale; float DirNoiseTime;
    float EdgeInvWidth; float EdgePow; float DensityAccumulation; float WetInvWidth;
    float WetFeed; float DivergenceMult; float Puncture; float PressureFeedback;
    float DivergenceFeedback; uint PressureMask; float PressureInvWidth; float PressurePow;
    uint Taps; float TapCenter; float Contrast; uint ExportFlags;
    float ExportInvWidth; uint AmpSrv; uint AmpLayer; uint AmpWidth;
    uint AmpHeight; uint DensitySrv; uint DensityLayer; uint DensityWidth;
    uint DensityHeight; uint StepLo; uint StepHi; uint CrudePass;
    uint Pad;
    float WeightP[16];
    float WeightD[16];
    uint PainterCells; uint PainterCellGroups; uint PainterCellBase; uint PainterEntryBase;
    uint PainterPointBase; uint PainterFlags; uint PainterNoiseSrv; uint PainterNoiseLayer;
    uint PainterNoiseWidth; uint PainterNoiseHeight; uint BitmapSrv; uint BitmapLayer;
    uint BitmapWidth; uint BitmapHeight; float BrushInvN; float PainterBase;
    float PainterQ; float PainterSharpen; float PainterEdgeInv; float PainterVeloClamp;
    float PainterVeloMult; float VeloNoiseScale; float VeloNoiseAmp; float VeloNoiseBias;
    float BrushDensityScale; float2 VeloNoiseOffset; float2 BrushDensityOffset;
    float BrushDensityFloor; float BrushDensityPow; float2 BitmapOffset; float BitmapScale; float DensityTxtMult;
    int ShiftX; int ShiftY; int BrushShiftX; int BrushShiftY;
    uint AgeEnabled; float AgeStep;
    uint StampCells; uint StampCellTexels; uint StampCellBase; uint StampEntryBase; uint StampBase;
    uint StampShapeSrv; uint StampShapeLayer; uint StampShapeWidth; uint StampShapeHeight;
    uint RoundNearest;
    uint FilmFlags; float FilmHeight; float FilmMinThickness; float FilmPuddle; float FilmFrictionDt;
    float FilmInvTwoDx; float FilmInvTwoDy; float FilmAccelX; float FilmAccelY; float FilmClamp;
    uint HeightSrv; uint HeightCells; float HeightX0; float HeightZ0; float HeightScaleU; float HeightScaleV; float HeightInvStep;
};
_Static_assert(sizeof(FluidConstants) == FLUID2D_CONSTANTS_SIZE, "FluidConstants differs from C# Fluid2DConstants");

struct FluidStatsPartial
{
    float Sum; float SumX; float SumY; float MaxSpeed;
    float MaxDensity; float SumWet; uint NonFinite; uint Pad;
};
_Static_assert(sizeof(FluidStatsPartial) == FLUID2D_STATS_PARTIAL_SIZE, "FluidStatsPartial differs from C#");

FluidConstants LoadConstants()
{
    StructuredBuffer<FluidConstants> all = ResourceDescriptorHeap[gFluidConstants];
    return all[gFluidIndex];
}

float4 Unpack4(uint2 u) { return float4(f16tof32(u.x), f16tof32(u.x >> 16), f16tof32(u.y), f16tof32(u.y >> 16)); }
uint2 Pack4(float4 v) { return uint2(f32tof16(v.x) | (f32tof16(v.y) << 16), f32tof16(v.z) | (f32tof16(v.w) << 16)); }
float2 Unpack2(uint u) { return float2(f16tof32(u), f16tof32(u >> 16)); }
uint Pack2(float2 v) { return f32tof16(v.x) | (f32tof16(v.y) << 16); }

// f32tof16 of the hardware rounds toward zero (measured: a puddle at rest loses 0.26 % of its mass in 600 steps of the neutral
// accumulation); round to nearest, ties to even - the same half as the C# (Half) cast: the truncated half or the next one away from zero.
uint F32ToF16Nearest(float v)
{
    uint h = f32tof16(v);
    if ((h & 0x7FFFu) >= 0x7BFFu) return h;
    float a = f16tof32(h), b = f16tof32(h + 1u);
    float da = abs(v - a), db = abs(b - v);
    return (db < da || (db == da && (h & 1u) != 0)) ? h + 1u : h;
}

uint2 PackState(float4 v, bool nearest)
{
    if (!nearest) return Pack4(v);
    return uint2(F32ToF16Nearest(v.x) | (F32ToF16Nearest(v.y) << 16), F32ToF16Nearest(v.z) | (F32ToF16Nearest(v.w) << 16));
}

float4 Lerp4(float4 a, float4 b, float t) { return a + (b - a) * t; }
float2 Lerp2(float2 a, float2 b, float t) { return a + (b - a) * t; }

// ---- grid sampling (clamp to edge) ----

float4 Texel4(StructuredBuffer<uint2> g, int n, int x, int y)
{
    x = clamp(x, 0, n - 1);
    y = clamp(y, 0, n - 1);
    return Unpack4(g[y * n + x]);
}

float4 Bilinear4(StructuredBuffer<uint2> g, int n, float2 uv)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float4 a = Texel4(g, n, x, y), b = Texel4(g, n, x + 1, y);
    float4 c = Texel4(g, n, x, y + 1), d = Texel4(g, n, x + 1, y + 1);
    return Lerp4(Lerp4(a, b, t.x), Lerp4(c, d, t.x), t.y);
}

float2 TexelP(StructuredBuffer<uint> g, int n, int x, int y)
{
    x = clamp(x, 0, n - 1);
    y = clamp(y, 0, n - 1);
    return Unpack2(g[y * n + x]);
}

float2 BilinearP(StructuredBuffer<uint> g, int n, float2 uv)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float2 a = TexelP(g, n, x, y), b = TexelP(g, n, x + 1, y);
    float2 c = TexelP(g, n, x, y + 1), d = TexelP(g, n, x + 1, y + 1);
    return Lerp2(Lerp2(a, b, t.x), Lerp2(c, d, t.x), t.y);
}

// Linear sample along one axis at texel (x, y) + offset (texels) - taps of divergence and pressure: the other axis sits on the
// texel centre, so this equals the bilinear sample there.
float4 Axis4(StructuredBuffer<uint2> g, int n, int x, int y, float offset, bool vertical)
{
    float p = (float)(vertical ? y : x) + offset;
    float f = floor(p);
    float t = p - f;
    int i = (int)f;
    float4 a = vertical ? Texel4(g, n, x, i) : Texel4(g, n, i, y);
    float4 b = vertical ? Texel4(g, n, x, i + 1) : Texel4(g, n, i + 1, y);
    return Lerp4(a, b, t);
}

float2 AxisP(StructuredBuffer<uint> g, int n, int x, int y, float offset, bool vertical)
{
    float p = (float)(vertical ? y : x) + offset;
    float f = floor(p);
    float t = p - f;
    int i = (int)f;
    float2 a = vertical ? TexelP(g, n, x, i) : TexelP(g, n, i, y);
    float2 b = vertical ? TexelP(g, n, x, i + 1) : TexelP(g, n, i + 1, y);
    return Lerp2(a, b, t);
}

void BrushTexel(StructuredBuffer<uint4> g, int n, int x, int y, out float4 lo, out float4 hi)
{
    x = clamp(x, 0, n - 1);
    y = clamp(y, 0, n - 1);
    uint4 u = g[y * n + x];
    lo = Unpack4(u.xy);
    hi = Unpack4(u.zw);
}

// Brush layers 0..3 (lo) and 4..7 (hi), bilinear from the coarser brush grid.
void BrushBilinear(StructuredBuffer<uint4> g, int n, float2 uv, out float4 lo, out float4 hi)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float4 a0, a1, b0, b1, c0, c1, d0, d1;
    BrushTexel(g, n, x, y, a0, a1);
    BrushTexel(g, n, x + 1, y, b0, b1);
    BrushTexel(g, n, x, y + 1, c0, c1);
    BrushTexel(g, n, x + 1, y + 1, d0, d1);
    lo = Lerp4(Lerp4(a0, b0, t.x), Lerp4(c0, d0, t.x), t.y);
    hi = Lerp4(Lerp4(a1, b1, t.x), Lerp4(c1, d1, t.x), t.y);
}

// ---- grids of the previous step through the integer window shift s: texel (x, y) = stored (clamp(x, y) + s), outside - 0 ----

bool Inside(int2 p, int n) { return p.x >= 0 && p.x < n && p.y >= 0 && p.y < n; }

float4 Texel4S(StructuredBuffer<uint2> g, int n, int x, int y, int2 s)
{
    int2 p = int2(clamp(x, 0, n - 1), clamp(y, 0, n - 1)) + s;
    if (!Inside(p, n)) return 0;
    return Unpack4(g[p.y * n + p.x]);
}

float4 Bilinear4S(StructuredBuffer<uint2> g, int n, float2 uv, int2 s)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float4 a = Texel4S(g, n, x, y, s), b = Texel4S(g, n, x + 1, y, s);
    float4 c = Texel4S(g, n, x, y + 1, s), d = Texel4S(g, n, x + 1, y + 1, s);
    return Lerp4(Lerp4(a, b, t.x), Lerp4(c, d, t.x), t.y);
}

float2 TexelPS(StructuredBuffer<uint> g, int n, int x, int y, int2 s)
{
    int2 p = int2(clamp(x, 0, n - 1), clamp(y, 0, n - 1)) + s;
    if (!Inside(p, n)) return 0;
    return Unpack2(g[p.y * n + p.x]);
}

float2 BilinearPS(StructuredBuffer<uint> g, int n, float2 uv, int2 s)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float2 a = TexelPS(g, n, x, y, s), b = TexelPS(g, n, x + 1, y, s);
    float2 c = TexelPS(g, n, x, y + 1, s), d = TexelPS(g, n, x + 1, y + 1, s);
    return Lerp2(Lerp2(a, b, t.x), Lerp2(c, d, t.x), t.y);
}

void BrushTexelS(StructuredBuffer<uint4> g, int n, int x, int y, int2 s, out float4 lo, out float4 hi)
{
    int2 p = int2(clamp(x, 0, n - 1), clamp(y, 0, n - 1)) + s;
    lo = 0;
    hi = 0;
    if (!Inside(p, n)) return;
    uint4 u = g[p.y * n + p.x];
    lo = Unpack4(u.xy);
    hi = Unpack4(u.zw);
}

void BrushBilinearS(StructuredBuffer<uint4> g, int n, float2 uv, int2 s, out float4 lo, out float4 hi)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float4 a0, a1, b0, b1, c0, c1, d0, d1;
    BrushTexelS(g, n, x, y, s, a0, a1);
    BrushTexelS(g, n, x + 1, y, s, b0, b1);
    BrushTexelS(g, n, x, y + 1, s, c0, c1);
    BrushTexelS(g, n, x + 1, y + 1, s, d0, d1);
    lo = Lerp4(Lerp4(a0, b0, t.x), Lerp4(c0, d0, t.x), t.y);
    hi = Lerp4(Lerp4(a1, b1, t.x), Lerp4(c1, d1, t.x), t.y);
}

// Age channel: rho * age of a texel (rho - channel z of the grid), bilinear like Bilinear4S - divided by the bilinear rho it gives the
// rho-weighted age of the sample.
float AgeMassS(StructuredBuffer<uint2> g, StructuredBuffer<float> e, int n, int x, int y, int2 s)
{
    int2 p = int2(clamp(x, 0, n - 1), clamp(y, 0, n - 1)) + s;
    if (!Inside(p, n)) return 0.0;
    int i = p.y * n + p.x;
    return f16tof32(g[i].y) * e[i];
}

float BilinearAgeMassS(StructuredBuffer<uint2> g, StructuredBuffer<float> e, int n, float2 uv, int2 s)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float a = AgeMassS(g, e, n, x, y, s), b = AgeMassS(g, e, n, x + 1, y, s);
    float c = AgeMassS(g, e, n, x, y + 1, s), d = AgeMassS(g, e, n, x + 1, y + 1, s);
    float ab = a + (b - a) * t.x, cd = c + (d - c) * t.x;
    return ab + (cd - ab) * t.y;
}

// Float density channel (conservative transport): texel through the window shift, outside - 0; bilinear like Bilinear4S; rho * age.
float TexelFS(StructuredBuffer<float> g, int n, int x, int y, int2 s)
{
    int2 p = int2(clamp(x, 0, n - 1), clamp(y, 0, n - 1)) + s;
    if (!Inside(p, n)) return 0.0;
    return g[p.y * n + p.x];
}

float BilinearFS(StructuredBuffer<float> g, int n, float2 uv, int2 s)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float a = TexelFS(g, n, x, y, s), b = TexelFS(g, n, x + 1, y, s);
    float c = TexelFS(g, n, x, y + 1, s), d = TexelFS(g, n, x + 1, y + 1, s);
    float ab = a + (b - a) * t.x, cd = c + (d - c) * t.x;
    return ab + (cd - ab) * t.y;
}

float AgeMassFS(StructuredBuffer<float> rho, StructuredBuffer<float> e, int n, int x, int y, int2 s)
{
    int2 p = int2(clamp(x, 0, n - 1), clamp(y, 0, n - 1)) + s;
    if (!Inside(p, n)) return 0.0;
    int i = p.y * n + p.x;
    return rho[i] * e[i];
}

float BilinearAgeMassFS(StructuredBuffer<float> rho, StructuredBuffer<float> e, int n, float2 uv, int2 s)
{
    float2 p = uv * (float)n - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float a = AgeMassFS(rho, e, n, x, y, s), b = AgeMassFS(rho, e, n, x + 1, y, s);
    float c = AgeMassFS(rho, e, n, x, y + 1, s), d = AgeMassFS(rho, e, n, x + 1, y + 1, s);
    float ab = a + (b - a) * t.x, cd = c + (d - c) * t.x;
    return ab + (cd - ab) * t.y;
}

// Nearest texel: round-half-even of uv * n - 0.5; outside the grid - 0 (an out-of-range load of L2).
int Nearest(float u, int n) { return (int)round(u * (float)n - 0.5); }

// ---- noise ----

int WrapIndex(int i, int size) { return ((i % size) + size) % size; }

float NoiseTexel(Texture2DArray<float4> t, uint layer, int w, int h, int x, int y)
{
    return t.Load(int4(WrapIndex(x, w), WrapIndex(y, h), (int)layer, 0)).r;
}

float NoiseBilinear(uint srv, uint layer, uint width, uint height, float2 uv)
{
    Texture2DArray<float4> tex = ResourceDescriptorHeap[srv];
    int w = (int)width, h = (int)height;
    float2 p = uv * float2((float)w, (float)h) - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float a = NoiseTexel(tex, layer, w, h, x, y), b = NoiseTexel(tex, layer, w, h, x + 1, y);
    float c = NoiseTexel(tex, layer, w, h, x, y + 1), d = NoiseTexel(tex, layer, w, h, x + 1, y + 1);
    float ab = a + (b - a) * t.x, cd = c + (d - c) * t.x;
    return ab + (cd - ab) * t.y;
}

// 3D simplex, two gradient sets (SimplexNoise.cs): corners by comparing the cell offsets, falloff (1 - 2|f|^2)^3 * 1024/375,
// gradients (+-1, +-1, +-1) from the PCG3D hash of the corner coordinates * 6 wrapped at 300.
int SimplexWrap(float t)
{
    float v = (6.0 * t + 0.5) * (1.0 / 300.0);
    return (int)floor((v - floor(v)) * 300.0);
}

uint2 Pcg3dXY(int3 i)
{
    uint3 v = uint3(i) * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    return v.xy >> 16;
}

float GradientDot(uint h, float3 f)
{
    return ((h & 0x8000u) != 0 ? f.x : -f.x) + ((h & 0x4000u) != 0 ? f.y : -f.y) + ((h & 0x2000u) != 0 ? f.z : -f.z);
}

void SimplexCorner(float3 p, float3 corner, inout float2 n)
{
    float3 f = p - corner;
    float s = clamp(2.0 * (f.x * f.x + f.y * f.y + f.z * f.z), 0.0, 1.0);
    float r = 1.0 - s;
    float w = r * r * r * (1024.0 / 375.0);
    uint2 h = Pcg3dXY(int3(SimplexWrap(corner.x), SimplexWrap(corner.y), SimplexWrap(corner.z)));
    n.x += w * GradientDot(h.x, f);
    n.y += w * GradientDot(h.y, f);
}

float2 Simplex2(float3 p)
{
    float skew = (p.x + p.y + p.z) * (1.0 / 3.0);
    float3 cell = floor(p + skew);
    float unskew = (cell.x + cell.y + cell.z) * (1.0 / 6.0);
    float3 b = cell - unskew;
    float3 f = p - b;
    float3 g = float3(f.x >= f.y ? 1.0 : 0.0, f.y >= f.z ? 1.0 : 0.0, f.z >= f.x ? 1.0 : 0.0);
    float3 l = 1.0 - g.zxy;
    float3 a1 = min(g, l) - 1.0 / 6.0;
    float3 a2 = max(g, l) - 1.0 / 3.0;
    float2 n = 0;
    SimplexCorner(p, b, n);
    SimplexCorner(p, b + a1, n);
    SimplexCorner(p, b + a2, n);
    SimplexCorner(p, b + 0.5, n);
    return n;
}

// ---- masks ----

float EdgeDistance(float2 uv) { return max(abs(2.0 * uv.x - 1.0), abs(2.0 * uv.y - 1.0)); }
float EdgeMask(float m, float invWidth) { return saturate(invWidth - 0.01 - m * invWidth); }

// ---- film (A'): thickness h = rho * FilmHeight, friction and gravity, conservative transport ----

// Density ceiling: saturate (L2), or only rho >= 0 when the density is a thickness without the ceiling 1.
float BoundDensity(float rho, bool unbounded) { return unbounded ? max(rho, 0.0) : saturate(rho); }

// Gradient (dT/dx, dT/dz) of the bilinear terrain surface at the texel: nodes (HeightCells + 1)^2 by rows of z, node coordinates of the
// window texel - HeightX0 + u * HeightScaleU, HeightZ0 + v * HeightScaleV (u along +X, v along +Z), clamped to the grid.
float2 TerrainGradient(FluidConstants c, float2 uv)
{
    StructuredBuffer<float> hs = ResourceDescriptorHeap[c.HeightSrv];
    int cells = (int)c.HeightCells, nodes = cells + 1;
    float gx = c.HeightX0 + uv.x * c.HeightScaleU, gz = c.HeightZ0 + uv.y * c.HeightScaleV;
    float fx = clamp(floor(gx), 0.0, (float)(cells - 1)), fz = clamp(floor(gz), 0.0, (float)(cells - 1));
    float tx = clamp(gx - fx, 0.0, 1.0), tz = clamp(gz - fz, 0.0, 1.0);
    int i = (int)fx, j = (int)fz;
    float h00 = hs[j * nodes + i], h10 = hs[j * nodes + i + 1], h01 = hs[(j + 1) * nodes + i], h11 = hs[(j + 1) * nodes + i + 1];
    float dx0 = h10 - h00, dx1 = h11 - h01, dz0 = h01 - h00, dz1 = h11 - h10;
    return float2(dx0 + (dx1 - dx0) * tz, dz0 + (dz1 - dz0) * tx) * c.HeightInvStep;
}

// Density of A at a texel through the window shift: the float channel under conservative transport, otherwise the half one.
float NeighbourRho(FluidConstants c, StructuredBuffer<uint2> adv, int n, int x, int y, int2 shift)
{
    if ((c.FilmFlags & FLUID2D_FILM_CONSERVATIVE) == 0) return Texel4S(adv, n, x, y, shift).z;
    StructuredBuffer<float> rhoA = ResourceDescriptorHeap[gFluidIn5];
    return TexelFS(rhoA, n, x, y, shift);
}

// Film velocity: thinner than FilmMinThickness - 0; otherwise v - g * grad(terrain + max(h - H, 0)) (neighbours from A through the window
// shift, central difference), then the implicit Poiseuille friction v / (1 + k * dt / h^2).
float2 FilmVelocity(FluidConstants c, StructuredBuffer<uint2> adv, int n, int x, int y, int2 shift, float2 uv, float2 v, float rho)
{
    float h = rho * c.FilmHeight;
    if (h < c.FilmMinThickness) return 0;
    float el = max(NeighbourRho(c, adv, n, x - 1, y, shift) * c.FilmHeight - c.FilmPuddle, 0.0);
    float er = max(NeighbourRho(c, adv, n, x + 1, y, shift) * c.FilmHeight - c.FilmPuddle, 0.0);
    float eu = max(NeighbourRho(c, adv, n, x, y - 1, shift) * c.FilmHeight - c.FilmPuddle, 0.0);
    float ed = max(NeighbourRho(c, adv, n, x, y + 1, shift) * c.FilmHeight - c.FilmPuddle, 0.0);
    float2 g = float2((er - el) * c.FilmInvTwoDx, (ed - eu) * c.FilmInvTwoDy);
    if ((c.FilmFlags & FLUID2D_FILM_HEIGHTS) != 0) g = g + TerrainGradient(c, uv);
    float2 a = float2(v.x - c.FilmAccelX * g.x, v.y - c.FilmAccelY * g.y);
    return a / (1.0 + c.FilmFrictionDt / (h * h));
}

// Donor-cell flux through the face between L and R (velocity of the face - the mean of the two): both cells of the face compute the same
// number, so the mass leaving one is exactly the mass entering the other.
float FaceFlux(float rhoL, float uL, float rhoR, float uR)
{
    float uf = 0.5 * (uL + uR);
    return uf > 0.0 ? rhoL * uf : rhoR * uf;
}

float FaceAge(float flux, float uL, float uR, float ageL, float ageR) { return flux * (0.5 * (uL + uR) > 0.0 ? ageL : ageR); }

// ---- stages ----

// Composite and Gradient: A (advected, read at uv + WOD), P (last pressure), brush -> C (v, rho, wet).
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsComposite(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<uint2> adv = ResourceDescriptorHeap[gFluidIn0];
    StructuredBuffer<uint> prs = ResourceDescriptorHeap[gFluidIn1];
    RWStructuredBuffer<uint2> outC = ResourceDescriptorHeap[gFluidOut0];
    int n = (int)c.N;
    float2 uv = (float2(id.xy) + 0.5) * c.InvN;
    float2 uvs = uv + c.Wod;
    int2 shift = int2(c.ShiftX, c.ShiftY);
    float d = c.SimSpeed * c.InvN;

    float4 a = Bilinear4S(adv, n, uvs, shift);
    bool conservative = (c.FilmFlags & FLUID2D_FILM_CONSERVATIVE) != 0;
    if (conservative)
    {
        StructuredBuffer<float> rhoA = ResourceDescriptorHeap[gFluidIn5];
        a.z = BilinearFS(rhoA, n, uvs, shift);
    }
    float px1 = BilinearPS(prs, n, uvs + float2(d, 0), shift).x, px0 = BilinearPS(prs, n, uvs - float2(d, 0), shift).x;
    float py1 = BilinearPS(prs, n, uvs + float2(0, d), shift).x, py0 = BilinearPS(prs, n, uvs - float2(0, d), shift).x;
    float2 v = a.xy - 0.5 * float2(px1 - px0, py1 - py0);

    float4 paint = 0, bitmap = 0;
    if (c.BrushN > 0)
    {
        StructuredBuffer<uint4> brush = ResourceDescriptorHeap[gFluidIn2];
        BrushBilinear(brush, (int)c.BrushN, uv, paint, bitmap);
    }
    float2 bv = c.BrushVelocity * paint.xy;
    float amp = 1.0;
    if (c.AmpEnabled != 0)
        amp = saturate(1.0 + NoiseBilinear(c.AmpSrv, c.AmpLayer, c.AmpWidth, c.AmpHeight, uv * c.AmpSize + c.AmpOffset) - c.AmpNoise);
    float2 inp = c.VelocityBase * amp + c.VeloStrength * bitmap.xy;
    float bb = min(dot(bv, bv), 1.0);
    float2 vadd = (a.z + 0.1 * paint.z * c.Eraser) * (bv + (1.0 - bb) * inp);

    float2 nz = 0;
    if (c.DirNoiseAmp != 0)
    {
        float mask = max(min(0.2 * dot(v, v), 1.0), c.DirNoiseMask);
        nz = (c.DirNoiseAmp * mask) * Simplex2(float3(uv * c.DirNoiseScale + c.DirNoiseOffset, c.DirNoiseTime));
    }
    float m = EdgeDistance(uv);
    float ring = saturate(99.99 - 100.0 * m) >= 1.0 ? 1.0 : 0.0;
    float collision = min(max(max(ring, c.CollisionFloor), c.Weaken), 1.0);
    float2 vn = clamp(0.5 * vadd + nz + v, -c.VelocityClamp, c.VelocityClamp) * collision;

    float dn = 1.0;
    if (c.DensityEnabled != 0)
        dn = 1.0 + c.DensityAmount * (NoiseBilinear(c.DensitySrv, c.DensityLayer, c.DensityWidth, c.DensityHeight,
            uv * c.DensityScale + c.DensityOffset) - 0.5);
    float ep = pow(EdgeMask(m, c.EdgeInvWidth), c.EdgePow);
    bool unbounded = (c.FilmFlags & FLUID2D_FILM_UNBOUNDED) != 0;
    float rho = BoundDensity(a.z - min(paint.z, c.Eraser) + ((1.0 - c.Eraser) * paint.z + 0.2 * bitmap.z) * dn, unbounded)
        * (0.5 * (c.DensityAccumulation * max(ep, 0.7)) + 0.5) * ep;
    if (c.StampCells != 0)
    {
        // Stamps of the step after the accumulation factor: C gains exactly their mass (times the edge mask).
        StructuredBuffer<float> stamps = ResourceDescriptorHeap[gFluidIn4];
        rho = rho + stamps[id.y * c.N + id.x] * ep;
    }
    rho = max(rho, c.DensityFloor);
    if ((c.FilmFlags & FLUID2D_FILM_FLOW) != 0) vn = FilmVelocity(c, adv, n, (int)id.x, (int)id.y, shift, uv, vn, rho);
    if (conservative)
    {
        vn = clamp(vn, -c.FilmClamp, c.FilmClamp);
        RWStructuredBuffer<float> rhoC = ResourceDescriptorHeap[gFluidOut6];
        rhoC[id.y * c.N + id.x] = rho;
    }

    int wx = Nearest(uvs.x, n) + shift.x, wy = Nearest(uvs.y, n) + shift.y;
    float wetPrev = (wx >= 0 && wx < n && wy >= 0 && wy < n) ? Unpack4(adv[wy * n + wx]).w : 0.0;
    float wet = (0.982 * wetPrev + c.WetFeed * rho) * EdgeMask(m, c.WetInvWidth);
    outC[id.y * c.N + id.x] = PackState(float4(vn, rho, wet), c.RoundNearest != 0);

    if (c.AgeEnabled != 0)
    {
        // Age: rho-weighted sample of A's age, + one step; inflow (rho above what the old blood alone gives) rejuvenates in proportion.
        StructuredBuffer<float> ageA = ResourceDescriptorHeap[gFluidIn3];
        RWStructuredBuffer<float> ageC = ResourceDescriptorHeap[gFluidOut1];
        float ageMass;
        if (conservative)
        {
            StructuredBuffer<float> rhoA = ResourceDescriptorHeap[gFluidIn5];
            ageMass = BilinearAgeMassFS(rhoA, ageA, n, uvs, shift);
        }
        else ageMass = BilinearAgeMassS(adv, ageA, n, uvs, shift);
        float ageIn = a.z > 0.0 ? ageMass / a.z : 0.0;
        float rhoOld = BoundDensity(a.z, unbounded) * (0.5 * (c.DensityAccumulation * max(ep, 0.7)) + 0.5) * ep;
        float fresh = rho > rhoOld ? rhoOld / rho : 1.0;
        ageC[id.y * c.N + id.x] = rho > 0.0 ? (ageIn + c.AgeStep) * fresh : 0.0;
    }
}

// Conservative advection: velocity - the back-traced sample s times VelocityFeedback, rho (float channel; half copy in A) - donor-cell fluxes through the four faces (closed
// walls at the grid edge), age - the same fluxes times the age of the donor.
void AdvectConservative(FluidConstants c, StructuredBuffer<uint2> comp, int n, int x, int y, float4 here, float4 s)
{
    RWStructuredBuffer<uint2> outA = ResourceDescriptorHeap[gFluidOut0];
    StructuredBuffer<float> rhoC = ResourceDescriptorHeap[gFluidIn5];
    RWStructuredBuffer<float> rhoA = ResourceDescriptorHeap[gFluidOut6];
    float4 l = Texel4(comp, n, x - 1, y), r = Texel4(comp, n, x + 1, y), u = Texel4(comp, n, x, y - 1), d = Texel4(comp, n, x, y + 1);
    here.z = rhoC[y * n + x];
    l.z = rhoC[y * n + clamp(x - 1, 0, n - 1)];
    r.z = rhoC[y * n + clamp(x + 1, 0, n - 1)];
    u.z = rhoC[clamp(y - 1, 0, n - 1) * n + x];
    d.z = rhoC[clamp(y + 1, 0, n - 1) * n + x];
    float fxm = x > 0 ? FaceFlux(l.z, l.x, here.z, here.x) : 0.0;
    float fxp = x + 1 < n ? FaceFlux(here.z, here.x, r.z, r.x) : 0.0;
    float fym = y > 0 ? FaceFlux(u.z, u.y, here.z, here.y) : 0.0;
    float fyp = y + 1 < n ? FaceFlux(here.z, here.y, d.z, d.y) : 0.0;
    float rho = max(here.z - ((fxp - fxm) + (fyp - fym)) * c.SimSpeed, 0.0);
    uint i = (uint)(y * n + x);
    outA[i] = PackState(float4(s.xy * c.VelocityFeedback, rho, here.w), c.RoundNearest != 0);
    rhoA[i] = rho;
    if (c.AgeEnabled == 0) return;
    StructuredBuffer<float> ageC = ResourceDescriptorHeap[gFluidIn1];
    RWStructuredBuffer<float> ageA = ResourceDescriptorHeap[gFluidOut1];
    float ah = ageC[i];
    float axm = x > 0 ? FaceAge(fxm, l.x, here.x, ageC[i - 1], ah) : 0.0;
    float axp = x + 1 < n ? FaceAge(fxp, here.x, r.x, ah, ageC[i + 1]) : 0.0;
    float aym = y > 0 ? FaceAge(fym, u.y, here.y, ageC[i - (uint)n], ah) : 0.0;
    float ayp = y + 1 < n ? FaceAge(fyp, here.y, d.y, ah, ageC[i + (uint)n]) : 0.0;
    float mass = here.z * ah - ((axp - axm) + (ayp - aym)) * c.SimSpeed;
    ageA[i] = rho > 0.0 ? max(mass / rho, 0.0) : 0.0;
}

// Advection: C -> A; back-trace uv - v * SimSpeed / N (clamped sampling), v and rho times VelocityFeedback, wet copied as is.
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsAdvection(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<uint2> comp = ResourceDescriptorHeap[gFluidIn0];
    RWStructuredBuffer<uint2> outA = ResourceDescriptorHeap[gFluidOut0];
    int n = (int)c.N;
    float2 uv = (float2(id.xy) + 0.5) * c.InvN;
    float4 here = Unpack4(comp[id.y * c.N + id.x]);
    float2 back = uv - here.xy * (c.SimSpeed * c.InvN);
    float4 s = Bilinear4(comp, n, back);
    if ((c.FilmFlags & FLUID2D_FILM_CONSERVATIVE) != 0)
    {
        AdvectConservative(c, comp, n, (int)id.x, (int)id.y, here, s);
        return;
    }
    outA[id.y * c.N + id.x] = PackState(float4(s.xyz * c.VelocityFeedback, here.w), c.RoundNearest != 0);
    if (c.AgeEnabled != 0)
    {
        StructuredBuffer<float> ageC = ResourceDescriptorHeap[gFluidIn1];
        RWStructuredBuffer<float> ageA = ResourceDescriptorHeap[gFluidOut1];
        ageA[id.y * c.N + id.x] = s.z > 0.0 ? BilinearAgeMassS(comp, ageC, n, back, int2(0, 0)) / s.z : 0.0;
    }
}

// Divergence: A, P, brush -> S = (P shifted with the area, clamp(div, -2, 2)).
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsDivergence(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<uint2> adv = ResourceDescriptorHeap[gFluidIn0];
    StructuredBuffer<uint> prs = ResourceDescriptorHeap[gFluidIn1];
    RWStructuredBuffer<uint> outS = ResourceDescriptorHeap[gFluidOut0];
    int n = (int)c.N;
    int x = (int)id.x, y = (int)id.y;
    float2 uv = (float2(id.xy) + 0.5) * c.InvN;
    float dvx = Axis4(adv, n, x, y, c.SimSpeed, false).x - Axis4(adv, n, x, y, -c.SimSpeed, false).x;
    float dvy = Axis4(adv, n, x, y, c.SimSpeed, true).y - Axis4(adv, n, x, y, -c.SimSpeed, true).y;
    float paintRho = 0.0;
    if (c.BrushN > 0)
    {
        StructuredBuffer<uint4> brush = ResourceDescriptorHeap[gFluidIn2];
        int bn = (int)c.BrushN;
        float4 lo, hi;
        BrushTexel(brush, bn, Nearest(uv.x, bn), Nearest(uv.y, bn), lo, hi);
        paintRho = lo.z;
    }
    float div = clamp(c.DivergenceMult * (c.Puncture * paintRho + (dvx + dvy)), -2.0, 2.0);
    float p0 = BilinearPS(prs, n, uv + c.Wod, int2(c.ShiftX, c.ShiftY)).x;
    outS[id.y * c.N + id.x] = Pack2(float2(p0, div));
}

// Pressure pass gFluidPass (0 - along x, 1 - along y): 15/13/.../5-tap kernels (+ one extra tap, as in L2) SimSpeed texels apart.
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsPressure(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<uint> src = ResourceDescriptorHeap[gFluidIn0];
    RWStructuredBuffer<uint> dst = ResourceDescriptorHeap[gFluidOut0];
    int n = (int)c.N;
    int x = (int)id.x, y = (int)id.y;
    bool vertical = gFluidPass != 0;
    float accP = 0.0, accD = 0.0;
    for (uint j = 0; j <= c.Taps; j++)
    {
        float2 s = AxisP(src, n, x, y, ((float)j - c.TapCenter) * c.SimSpeed, vertical);
        accP += s.x * c.WeightP[j];
        accD += s.y * c.WeightD[j];
    }
    float2 r = float2(vertical ? accP - accD : accP, accD) * 0.999;
    if (c.PressureMask != 0)
    {
        float2 uv = (float2(id.xy) + 0.5) * c.InvN;
        r *= pow(EdgeMask(EdgeDistance(uv), c.PressureInvWidth), c.PressurePow);
    }
    r *= float2(c.PressureFeedback, c.DivergenceFeedback);
    dst[id.y * c.N + id.x] = Pack2(r);
}

// Export VelocityAndDensity: C -> RT_Composite (v * edge10, rho^contrast, max(wet * masks, 0.01)).
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsExport(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<uint2> comp = ResourceDescriptorHeap[gFluidIn0];
    RWTexture2D<float4> rt = ResourceDescriptorHeap[gFluidOut0];
    float2 uv = (float2(id.xy) + 0.5) * c.InvN;
    float4 s = Unpack4(comp[id.y * c.N + id.x]);
    float edge10 = saturate(9.99 - max(abs(20.0 * uv.x - 10.0), abs(20.0 * uv.y - 10.0)));
    float rc = pow(max(s.z, 0.0), c.Contrast);
    float inverted = 1.0 - rc;
    float outRho = (c.ExportFlags & FLUID2D_EXPORT_INVERT) != 0 ? inverted : rc;
    float wet = (c.ExportFlags & FLUID2D_EXPORT_INVERTED_ALPHA) != 0 ? inverted
        : s.w * EdgeMask(EdgeDistance(uv), c.ExportInvWidth) * edge10;
    rt[id.xy] = float4(s.xy * edge10, outRho, max(wet, 0.01));
}

// Zero all four grids (reset): gFluidOut0..3 = C, A, P, S.
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsClear(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    RWStructuredBuffer<uint2> outC = ResourceDescriptorHeap[gFluidOut0];
    RWStructuredBuffer<uint2> outA = ResourceDescriptorHeap[gFluidOut1];
    RWStructuredBuffer<uint> outP = ResourceDescriptorHeap[gFluidOut2];
    RWStructuredBuffer<uint> outS = ResourceDescriptorHeap[gFluidOut3];
    uint i = id.y * c.N + id.x;
    outC[i] = 0;
    outA[i] = 0;
    outP[i] = 0;
    outS[i] = 0;
    if (c.AgeEnabled != 0)
    {
        RWStructuredBuffer<float> ageC = ResourceDescriptorHeap[gFluidOut4];
        RWStructuredBuffer<float> ageA = ResourceDescriptorHeap[gFluidOut5];
        ageC[i] = 0;
        ageA[i] = 0;
    }
    if ((c.FilmFlags & FLUID2D_FILM_CONSERVATIVE) != 0)
    {
        RWStructuredBuffer<float> rhoC = ResourceDescriptorHeap[gFluidOut6];
        RWStructuredBuffer<float> rhoA = ResourceDescriptorHeap[gFluidOut7];
        rhoC[i] = 0;
        rhoA[i] = 0;
    }
}

// Export of the age channel (after Composite of the last step): seconds per texel -> R32F texture.
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsExportAge(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<float> age = ResourceDescriptorHeap[gFluidIn0];
    RWTexture2D<float> rt = ResourceDescriptorHeap[gFluidOut0];
    rt[id.xy] = age[id.y * c.N + id.x];
}

// ---- brush: TrackPoint Painter (BR 4.2, deterministic merge BR 8.2) ----

float4 ImageTexel(Texture2DArray<float4> t, uint layer, int w, int h, int x, int y)
{
    return t.Load(int4(WrapIndex(x, w), WrapIndex(y, h), (int)layer, 0));
}

// Bilinear RGBA with wrap, mip 0 (brush noise, bitmap) - Fluid2DPainterReference.SampleWrap.
float4 ImageBilinear(uint srv, uint layer, uint width, uint height, float2 uv)
{
    Texture2DArray<float4> tex = ResourceDescriptorHeap[srv];
    int w = (int)width, h = (int)height;
    float2 p = uv * float2((float)w, (float)h) - 0.5;
    float2 f = floor(p);
    float2 t = p - f;
    int x = (int)f.x, y = (int)f.y;
    float4 a = ImageTexel(tex, layer, w, h, x, y), b = ImageTexel(tex, layer, w, h, x + 1, y);
    float4 c = ImageTexel(tex, layer, w, h, x, y + 1), d = ImageTexel(tex, layer, w, h, x + 1, y + 1);
    return Lerp4(Lerp4(a, b, t.x), Lerp4(c, d, t.x), t.y);
}

float PointWord(StructuredBuffer<uint> data, uint b, uint i) { return asfloat(data[b + i]); }

// Painter: previous brush grid (read at uv + WOD) + points of the step -> brush grid (layers 0..6). One thread per brush texel; a group
// (8x8 texels) reads the point list of its binning cell (cells of PainterCellGroups^2 groups, points in key order). Every point is drawn
// with branch i = 0 of L2 (its numbers that do not depend on the texel come from the CPU, Fluid2DBrushPoint); strokes merge commutatively:
// density - the largest |rho| (sign kept, the earlier key on a tie), velocity - mean weighted by s_h; or sums (painter mode, additive).
// Words of a point: pos 0-1, prev 2-3, dir 4-5, segLen 6, c2 7, vb 8-9, pz 10, kvel 11, mv 12, strp 13, hs 14, zf 15, aabb 16-19.
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsPainter(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.BrushN || id.y >= c.BrushN) return;
    StructuredBuffer<uint4> prev = ResourceDescriptorHeap[gFluidIn0];
    StructuredBuffer<uint> data = ResourceDescriptorHeap[gFluidIn1];
    RWStructuredBuffer<uint4> outB = ResourceDescriptorHeap[gFluidOut0];
    int bn = (int)c.BrushN;
    float u = ((float)id.x + 0.5) * c.BrushInvN, v = ((float)id.y + 0.5) * c.BrushInvN;

    float vnx = 0.0, vny = 0.0, dn = 1.0;
    if ((c.PainterFlags & FLUID2D_PAINTER_NOISE) != 0)
    {
        float4 rg = ImageBilinear(c.PainterNoiseSrv, c.PainterNoiseLayer, c.PainterNoiseWidth, c.PainterNoiseHeight,
            float2(u * c.VeloNoiseScale + c.VeloNoiseOffset.x, v * c.VeloNoiseScale + c.VeloNoiseOffset.y));
        vnx = rg.x * c.VeloNoiseAmp - c.VeloNoiseBias;
        vny = rg.y * c.VeloNoiseAmp - c.VeloNoiseBias;
        float r = ImageBilinear(c.PainterNoiseSrv, c.PainterNoiseLayer, c.PainterNoiseWidth, c.PainterNoiseHeight,
            float2(u * c.BrushDensityScale + c.BrushDensityOffset.x, v * c.BrushDensityScale + c.BrushDensityOffset.y)).x;
        dn = max(c.BrushDensityFloor, r > 0.0 ? pow(r, c.BrushDensityPow) : 0.0);
    }

    uint2 cell = group.xy / c.PainterCellGroups;
    uint word = data[c.PainterCellBase + cell.y * c.PainterCells + cell.x];
    uint start = word & 0xFFFFu, count = word >> 16;
    float clampV = c.PainterVeloClamp;
    float best = 0.0, weight = 0.0, wx = 0.0, wy = 0.0, sx = 0.0, sy = 0.0, sz = 0.0;
    for (uint j = 0; j < count; j++)
    {
        uint b = c.PainterPointBase + data[c.PainterEntryBase + start + j] * FLUID2D_POINT_WORDS;
        if (u < PointWord(data, b, 16) || u > PointWord(data, b, 18) || v < PointWord(data, b, 17) || v > PointWord(data, b, 19)) continue;
        float c2 = PointWord(data, b, 7);
        float dx = u - PointWord(data, b, 0), dy = v - PointWord(data, b, 1);
        float f = exp2(c2 * (dx * dx + dy * dy));
        float ax = u - PointWord(data, b, 2), ay = v - PointWord(data, b, 3);
        float segLen = PointWord(data, b, 6);
        if (segLen >= 0.0)
        {
            float t = clamp(ax * PointWord(data, b, 4) + ay * PointWord(data, b, 5), 0.0, segLen);
            float fs = exp2(c2 * (ax * ax + ay * ay - t * t));
            f = max(fs, f);
        }
        f = min(f, 1.0) * PointWord(data, b, 15);
        float w1 = (1.0 - f) * (1.0 - f);
        float pz = PointWord(data, b, 10), kvel = PointWord(data, b, 11), mv = PointWord(data, b, 12), hs = PointWord(data, b, 14);
        float velX = PointWord(data, b, 8) + (vnx + ax) * 100.0 * pz * w1 * kvel;
        float velY = PointWord(data, b, 9) + (vny + ay) * 100.0 * pz * w1 * kvel;
        float xs = saturate((dn * f - 0.5 * hs) / (1.0 - hs));
        float sh = xs * xs * (3.0 - 2.0 * xs);
        float sv = sh;
        if (c.PainterQ > 0.0)
        {
            float base_ = max(sv - 0.1 * c.PainterQ, 0.0);
            sv = base_ > 0.0 ? pow(base_, c.PainterSharpen) : 0.0;
        }
        velX = clamp(sv * velX * mv, -clampV, clampV) * c.PainterVeloMult;
        velY = clamp(sv * velY * mv, -clampV, clampV) * c.PainterVeloMult;
        float dens = sh * PointWord(data, b, 13);
        if (abs(dens) > abs(best)) best = dens;
        weight += sh;
        wx += sh * velX;
        wy += sh * velY;
        sx += velX;
        sy += velY;
        sz += clamp(dens, -1.0, 1.0);
    }

    float a = saturate(best + c.PainterBase);
    float4 oldLo, oldHi;
    BrushBilinearS(prev, bn, float2(u + c.Wod.x, v + c.Wod.y), int2(c.BrushShiftX, c.BrushShiftY), oldLo, oldHi);
    float keep = 1.0 - a;
    float px, py, pz;
    if ((c.PainterFlags & FLUID2D_PAINTER_ADDITIVE) != 0)
    {
        float a4 = (c.PainterFlags & FLUID2D_PAINTER_PAINTER_MODE) != 0 ? (a > 0.0 ? pow(a, 0.25) : 0.0) : a;
        px = sx * a4 + oldLo.x * keep;
        py = sy * a4 + oldLo.y * keep;
        pz = sz * a4 + oldLo.z * keep;
    }
    else
    {
        float inv = max(weight, 1e-6);
        px = wx / inv * a * a + oldLo.x * keep;
        py = wy / inv * a * a + oldLo.y * keep;
        pz = clamp(best, -1.0, 1.0) * a * a + oldLo.z * keep;
    }

    float iw = c.PainterEdgeInv;
    float m = saturate(iw - 0.01 - max(abs(2.0 * u * iw - iw), abs(2.0 * v * iw - iw)));
    float e = (c.PainterFlags & FLUID2D_PAINTER_EDGE_STEP) != 0 ? (m >= 1.0 ? 1.0 : 0.0) : (m > 0.0 ? pow(m, 0.1) : 0.0);

    float l4 = 0.0, l5 = 0.0, l6 = 0.0;
    if ((c.PainterFlags & FLUID2D_PAINTER_BITMAP) != 0)
    {
        float4 t = ImageBilinear(c.BitmapSrv, c.BitmapLayer, c.BitmapWidth, c.BitmapHeight,
            float2(u * c.BitmapScale + c.BitmapOffset.x, v * c.BitmapScale + c.BitmapOffset.y));
        bool densityOnly = (c.PainterFlags & FLUID2D_PAINTER_BITMAP_DENSITY_ONLY) != 0;
        if (!densityOnly)
        {
            l4 = 2.0 * t.x - 1.0;
            l5 = 2.0 * t.y - 1.0;
        }
        float td = densityOnly ? t.x : t.z;
        l6 = td * c.DensityTxtMult * (1.0 - saturate(pz)) * 0.5;
    }
    outB[id.y * c.BrushN + id.x] = uint4(Pack4(float4(e * px, e * py, e * pz, 0.0)), Pack4(float4(l4, l5, l6, 0.0)));
}

// Zero both brush grids (reset): gFluidOut0, gFluidOut1.
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsPainterClear(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.BrushN || id.y >= c.BrushN) return;
    RWStructuredBuffer<uint4> a = ResourceDescriptorHeap[gFluidOut0];
    RWStructuredBuffer<uint4> b = ResourceDescriptorHeap[gFluidOut1];
    a[id.y * c.BrushN + id.x] = 0;
    b[id.y * c.BrushN + id.x] = 0;
}

// ---- stats: fixed-order reduction (no atomics), so two runs give the same bits ----

groupshared FluidStatsPartial gsStats[FLUID2D_STATS_THREADS];

FluidStatsPartial Combine(FluidStatsPartial a, FluidStatsPartial b)
{
    FluidStatsPartial r;
    r.Sum = a.Sum + b.Sum;
    r.SumX = a.SumX + b.SumX;
    r.SumY = a.SumY + b.SumY;
    r.MaxSpeed = max(a.MaxSpeed, b.MaxSpeed);
    r.MaxDensity = max(a.MaxDensity, b.MaxDensity);
    r.SumWet = a.SumWet + b.SumWet;
    r.NonFinite = a.NonFinite + b.NonFinite;
    r.Pad = 0;
    return r;
}

bool HalfFinite(uint bits) { return (bits & 0x7c00u) != 0x7c00u; }

// One group per FLUID2D_STATS_ROWS rows of C: sums of rho, rho*uv, wet; max |v| and rho; texels with Inf/NaN (not summed).
[numthreads(FLUID2D_STATS_THREADS, 1, 1)]
void CsStatsRows(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    FluidConstants c = LoadConstants();
    StructuredBuffer<uint2> comp = ResourceDescriptorHeap[gFluidIn0];
    RWStructuredBuffer<FluidStatsPartial> partial = ResourceDescriptorHeap[gFluidOut0];
    FluidStatsPartial acc = (FluidStatsPartial)0;
    uint first = group.x * FLUID2D_STATS_ROWS * c.N;
    uint last = min(first + FLUID2D_STATS_ROWS * c.N, c.N * c.N);
    for (uint i = first + thread.x; i < last; i += FLUID2D_STATS_THREADS)
    {
        uint2 u = comp[i];
        if (!HalfFinite(u.x) || !HalfFinite(u.x >> 16) || !HalfFinite(u.y) || !HalfFinite(u.y >> 16))
        {
            acc.NonFinite++;
            continue;
        }
        float4 s = Unpack4(u);
        float2 uv = (float2(i % c.N, i / c.N) + 0.5) * c.InvN;
        acc.Sum += s.z;
        acc.SumX += s.z * uv.x;
        acc.SumY += s.z * uv.y;
        acc.MaxSpeed = max(acc.MaxSpeed, sqrt(dot(s.xy, s.xy)));
        acc.MaxDensity = max(acc.MaxDensity, s.z);
        acc.SumWet += s.w;
    }
    gsStats[thread.x] = acc;
    GroupMemoryBarrierWithGroupSync();
    for (uint half_ = FLUID2D_STATS_THREADS / 2; half_ > 0; half_ >>= 1)
    {
        if (thread.x < half_) gsStats[thread.x] = Combine(gsStats[thread.x], gsStats[thread.x + half_]);
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread.x == 0) partial[group.x] = gsStats[0];
}

// Partials in group order -> stats (Fluid2DStats.Parse): step, sums, maxima, centroid of rho.
[numthreads(1, 1, 1)]
void CsStats(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    StructuredBuffer<FluidStatsPartial> partial = ResourceDescriptorHeap[gFluidIn0];
    RWStructuredBuffer<uint> stats = ResourceDescriptorHeap[gFluidOut0];
    uint groups = (c.N + FLUID2D_STATS_ROWS - 1) / FLUID2D_STATS_ROWS;
    FluidStatsPartial acc = partial[0];
    for (uint g = 1; g < groups; g++) acc = Combine(acc, partial[g]);
    stats[0] = c.StepLo;
    stats[1] = c.StepHi;
    stats[2] = acc.NonFinite;
    stats[3] = c.N;
    stats[4] = asuint(acc.Sum);
    stats[5] = asuint(acc.MaxSpeed);
    stats[6] = asuint(acc.MaxDensity);
    stats[7] = asuint(acc.SumWet);
    stats[8] = asuint(acc.Sum > 0.0 ? acc.SumX / acc.Sum : 0.5);
    stats[9] = asuint(acc.Sum > 0.0 ? acc.SumY / acc.Sum : 0.5);
    for (uint k = 10; k < 16; k++) stats[k] = 0;
}

// ---- stamps: density of the step's stamps per texel (Fluid2DStamps), summed in record order of the binning cell, no atomics ----

float StampWord(StructuredBuffer<uint> data, uint b, uint i) { return asfloat(data[b + i]); }

// Words of a stamp: centre 0-1, matrix uv -> q 2-5, peak 6, shape 7, aabb 8-11. Shape 0 - gaussian exp(-|q|^2 / 2) cut at |q| = 4;
// shape k >= 1 - channel r of layer StampShapeLayer + k - 1 on the square |q| <= 1 (bilinear with wrap at uv = q / 2 + 0.5).
[numthreads(FLUID2D_GROUP, FLUID2D_GROUP, 1)]
void CsStamp(uint3 id : SV_DispatchThreadID)
{
    FluidConstants c = LoadConstants();
    if (id.x >= c.N || id.y >= c.N) return;
    StructuredBuffer<uint> data = ResourceDescriptorHeap[gFluidIn0];
    RWStructuredBuffer<float> outS = ResourceDescriptorHeap[gFluidOut0];
    float u = ((float)id.x + 0.5) * c.InvN, v = ((float)id.y + 0.5) * c.InvN;
    uint2 cell = id.xy / c.StampCellTexels;
    uint word = data[c.StampCellBase + cell.y * c.StampCells + cell.x];
    uint start = word & 0xFFFFu, count = word >> 16;
    float sum = 0.0;
    for (uint j = 0; j < count; j++)
    {
        uint b = c.StampBase + data[c.StampEntryBase + start + j] * FLUID2D_STAMP_WORDS;
        if (u < StampWord(data, b, 8) || u > StampWord(data, b, 10) || v < StampWord(data, b, 9) || v > StampWord(data, b, 11)) continue;
        float dx = u - StampWord(data, b, 0), dy = v - StampWord(data, b, 1);
        float qx = StampWord(data, b, 2) * dx + StampWord(data, b, 3) * dy;
        float qy = StampWord(data, b, 4) * dx + StampWord(data, b, 5) * dy;
        uint shape = data[b + 7];
        float value = 0.0;
        if (shape == 0)
        {
            float q2 = qx * qx + qy * qy;
            if (q2 <= 16.0) value = exp2(-0.72134752 * q2);
        }
        else if (abs(qx) <= 1.0 && abs(qy) <= 1.0)
            value = ImageBilinear(c.StampShapeSrv, c.StampShapeLayer + shape - 1, c.StampShapeWidth, c.StampShapeHeight,
                float2(qx * 0.5 + 0.5, qy * 0.5 + 0.5)).x;
        sum += StampWord(data, b, 6) * value;
    }
    outS[id.y * c.N + id.x] = sum;
}
