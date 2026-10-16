// 3D gas core (smoke in the world): one step = Sources (window shift + sources) -> Curl -> Forces (buoyancy + vorticity
// confinement) -> Advect velocity -> Divergence -> Pressure (red-black SOR, in place) -> Project -> Advect scalars
// (semi-Lagrangian or MacCormack), then stats. The same arithmetic in C# is Fluid3DReference (CPU reference).
// Grid: collocated, axis-aligned with the world, cell (x, y, z) at index x + Nx * (y + Ny * z), y up. Buffers hold half bits:
// velocity uint2 = (vx, vy, vz, 0), scalars uint = (density, temperature), aux uint = (pressure, t) where t is |curl| after
// Curl and divergence after Divergence. Sampling is done by hand from loads (trilinear): velocity is clamped to the edge
// (open boundary without a false wall), scalars and pressure are zero outside (empty space, Dirichlet p = 0).
// Plain mul/add everywhere (no dot/lerp intrinsics): with IEEE strictness GPU and CPU compute the same float expressions.
#include "common/pass.hlsli"

// A capsule: segment Tail -> Tail + Axis (a sphere when Axis = 0), index space (cell centres at integers).
struct GasSource
{
    float4 Tail;       // segment start, w = outer radius in cells (radius + edge / 2)
    float4 Axis;       // segment end - start, w = 1 / |axis|^2 (0 for a sphere)
    float4 Rates;      // density * dt, temperature * dt, velocity blend per step, 1 / edge width in cells
    float4 Velocity;   // target velocity, m/s
};

struct GasConstants
{
    uint Nx; uint Ny; uint Nz; uint Sources;
    int ShiftX; int ShiftY; int ShiftZ; uint WarmStart;
    int SourceMinX; int SourceMinY; int SourceMinZ; float Dt;
    int SourceMaxX; int SourceMaxY; int SourceMaxZ; float HalfInvDx;
    float BackStep; float Vorticity; float MaxSpeed; float MaxSpeed2;
    float Alpha; float Beta; float GravityX; float GravityY;
    float GravityZ; float DensityKeep; float DensityLoss; float TemperatureKeep;
    float TemperatureLoss; float SorKeep; float SorWeight6; float PressureRhs;
    float GradScale; uint StepLo; uint StepHi; int OriginX;
    int OriginY; int OriginZ; float CellSize; float NoiseAmp;
    float NoiseStep; float NoiseInvDensity; float NoiseX; float NoiseY;
    float NoiseZ; float WindX; float WindY; float WindZ;
    float PlaneX; float PlaneY; float PlaneZ; uint PlaneSide;
    float PlaneUx; float PlaneUy; float PlaneUz; float PlaneHalf;
    float PlaneVx; float PlaneVy; float PlaneVz; float PlaneThick;
    float PlaneNx; float PlaneNy; float PlaneNz; float PlaneInvEdge;
    uint PlaneOffset; uint PlanePad0; uint PlanePad1; uint PlanePad2;
    GasSource Source[FLUID3D_MAX_SOURCES];
};
_Static_assert(sizeof(GasConstants) == FLUID3D_CONSTANTS_SIZE, "GasConstants differs from C# Fluid3DConstants");

struct GasStatsPartial
{
    float Density; float Temperature; float SumX; float SumY;
    float SumZ; float MaxSpeed; float MaxDensity; float MaxTemperature;
    float DivBefore; float DivAfter; float Enstrophy; uint NonFinite;
};
_Static_assert(sizeof(GasStatsPartial) == FLUID3D_STATS_PARTIAL_SIZE, "GasStatsPartial differs from C#");

static const float HalfMax = 65504.0;

GasConstants LoadConstants()
{
    StructuredBuffer<GasConstants> all = ResourceDescriptorHeap[gGasConstants];
    return all[gGasIndex];
}

float3 Unpack3(uint2 u) { return float3(f16tof32(u.x), f16tof32(u.x >> 16), f16tof32(u.y)); }
uint2 Pack3(float3 v) { return uint2(f32tof16(v.x) | (f32tof16(v.y) << 16), f32tof16(v.z)); }
float2 Unpack2(uint u) { return float2(f16tof32(u), f16tof32(u >> 16)); }
uint Pack2(float2 v) { return f32tof16(v.x) | (f32tof16(v.y) << 16); }

float3 Lerp3(float3 a, float3 b, float t) { return a + (b - a) * t; }
float2 Lerp2(float2 a, float2 b, float t) { return a + (b - a) * t; }
float Dot3(float3 a, float3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
float3 Cross3(float3 a, float3 b) { return float3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }

int3 Size(GasConstants k) { return int3((int)k.Nx, (int)k.Ny, (int)k.Nz); }
bool Inside(int3 c, GasConstants k) { return c.x >= 0 && c.y >= 0 && c.z >= 0 && c.x < (int)k.Nx && c.y < (int)k.Ny && c.z < (int)k.Nz; }
uint Index(int3 c, GasConstants k) { return (uint)c.x + k.Nx * ((uint)c.y + k.Ny * (uint)c.z); }
int3 ClampCell(int3 c, GasConstants k) { return clamp(c, int3(0, 0, 0), Size(k) - 1); }

// ---- loads: velocity clamped to the edge, scalars and pressure zero outside ----

float3 Vel(StructuredBuffer<uint2> v, int3 c, GasConstants k) { return Unpack3(v[Index(ClampCell(c, k), k)]); }

float2 Scal(StructuredBuffer<uint> s, int3 c, GasConstants k)
{
    if (!Inside(c, k)) return float2(0, 0);
    return Unpack2(s[Index(c, k)]);
}

float Pres(StructuredBuffer<uint> a, int3 c, GasConstants k)
{
    if (!Inside(c, k)) return 0;
    return f16tof32(a[Index(c, k)]);
}

float PresRW(RWStructuredBuffer<uint> a, int3 c, GasConstants k)
{
    if (!Inside(c, k)) return 0;
    return f16tof32(a[Index(c, k)]);
}

// |curl| (t of aux), clamped to the edge.
float CurlAt(StructuredBuffer<uint> a, int3 c, GasConstants k) { return f16tof32(a[Index(ClampCell(c, k), k)] >> 16); }

// ---- trilinear sampling at q (index space: cell centres at integers) ----

float3 SampleVel(StructuredBuffer<uint2> v, float3 q, GasConstants k)
{
    float3 f = floor(q);
    float3 t = q - f;
    int3 c = (int3)f;
    float3 x00 = Lerp3(Vel(v, c, k), Vel(v, c + int3(1, 0, 0), k), t.x);
    float3 x10 = Lerp3(Vel(v, c + int3(0, 1, 0), k), Vel(v, c + int3(1, 1, 0), k), t.x);
    float3 x01 = Lerp3(Vel(v, c + int3(0, 0, 1), k), Vel(v, c + int3(1, 0, 1), k), t.x);
    float3 x11 = Lerp3(Vel(v, c + int3(0, 1, 1), k), Vel(v, c + int3(1, 1, 1), k), t.x);
    return Lerp3(Lerp3(x00, x10, t.y), Lerp3(x01, x11, t.y), t.z);
}

float2 SampleScal(StructuredBuffer<uint> s, float3 q, GasConstants k)
{
    float3 f = floor(q);
    float3 t = q - f;
    int3 c = (int3)f;
    float2 x00 = Lerp2(Scal(s, c, k), Scal(s, c + int3(1, 0, 0), k), t.x);
    float2 x10 = Lerp2(Scal(s, c + int3(0, 1, 0), k), Scal(s, c + int3(1, 1, 0), k), t.x);
    float2 x01 = Lerp2(Scal(s, c + int3(0, 0, 1), k), Scal(s, c + int3(1, 0, 1), k), t.x);
    float2 x11 = Lerp2(Scal(s, c + int3(0, 1, 1), k), Scal(s, c + int3(1, 1, 1), k), t.x);
    return Lerp2(Lerp2(x00, x10, t.y), Lerp2(x01, x11, t.y), t.z);
}

// Min and max of the eight scalar corners around q (the MacCormack limiter).
void CornerRange(StructuredBuffer<uint> s, float3 q, GasConstants k, out float2 lo, out float2 hi)
{
    int3 c = (int3)floor(q);
    lo = Scal(s, c, k);
    hi = lo;
    [unroll] for (int i = 1; i < 8; i++)
    {
        float2 x = Scal(s, c + int3(i & 1, (i >> 1) & 1, i >> 2), k);
        lo = min(lo, x);
        hi = max(hi, x);
    }
}

// Back-trace step of the advection: (v + wind) * dt / dx in cells; wind 0 gives v * dt / dx bit for bit.
float3 BackOf(float3 v, GasConstants k) { return v * k.BackStep + float3(k.WindX, k.WindY, k.WindZ); }

// ---- map source on a plane (Fluid3DPlane): texel uint4 = (density | temperature << 16, blend, vx | vy << 16, vz), per step ----

struct MapValue
{
    float3 Rates;      // density, temperature, velocity blend
    float3 Velocity;
};

MapValue MapTexel(StructuredBuffer<uint4> map, int i, int j, GasConstants k)
{
    MapValue m;
    m.Rates = float3(0, 0, 0);
    m.Velocity = float3(0, 0, 0);
    int n = (int)k.PlaneSide;
    if (i < 0 || j < 0 || i >= n || j >= n) return m;
    uint4 u = map[k.PlaneOffset + (uint)(i + n * j)];
    m.Rates = float3(f16tof32(u.x), f16tof32(u.x >> 16), f16tof32(u.y));
    m.Velocity = float3(f16tof32(u.z), f16tof32(u.z >> 16), f16tof32(u.w));
    return m;
}

MapValue LerpMap(MapValue a, MapValue b, float t)
{
    MapValue m;
    m.Rates = Lerp3(a.Rates, b.Rates, t);
    m.Velocity = Lerp3(a.Velocity, b.Velocity, t);
    return m;
}

// Bilinear at map coordinates (texel centres at integers), zero outside the map.
MapValue SampleMap(StructuredBuffer<uint4> map, float mu, float mv, GasConstants k)
{
    float fu = floor(mu), fv = floor(mv);
    float tu = mu - fu, tv = mv - fv;
    int i = (int)fu, j = (int)fv;
    MapValue y0 = LerpMap(MapTexel(map, i, j, k), MapTexel(map, i + 1, j, k), tu);
    MapValue y1 = LerpMap(MapTexel(map, i, j + 1, k), MapTexel(map, i + 1, j + 1, k), tu);
    return LerpMap(y0, y1, tv);
}

float2 Decay(float2 s, GasConstants k)
{
    float2 r = float2(s.x * k.DensityKeep - k.DensityLoss, s.y * k.TemperatureKeep - k.TemperatureLoss);
    return clamp(r, float2(0, 0), float2(HalfMax, HalfMax));
}

// ---- curl noise (Fluid3DCurlNoise): curl of a vector potential of three Perlin gradient noises on one lattice (three gradients per node),
// analytic quintic derivatives - divergence-free by construction; the lattice repeats every 256 nodes (coordinates wrapped on the CPU) ----

static const float GasGrad[48] =
{
    1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0,
    1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1,
    0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1,
    1, 1, 0, 0, -1, 1, -1, 1, 0, 0, -1, -1,
};

// pcg3d (Jarzynski and Olano) of the node modulo the period: one word per potential component.
uint3 GasHash(int3 c)
{
    uint3 v = (uint3)c & 255u;
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return v;
}

float GasFade(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }
float GasFadeDerivative(float t) { return t * t * 30.0 * (t * (t - 2.0) + 1.0); }

// Gradient of one potential component, one node: grad(w (g.d)) = grad(w) (g.d) + w g.
float3 GasNoiseCorner(uint h, float3 d, float w, float3 sw)
{
    float3 g = float3(GasGrad[3 * h], GasGrad[3 * h + 1], GasGrad[3 * h + 2]);
    float v = g.x * d.x + g.y * d.y + g.z * d.z;
    return sw * v + g * w;
}

// curl psi at noise coordinates q (lattice units).
float3 GasCurlNoise(float3 q)
{
    float3 f = floor(q);
    float3 t = q - f;
    int3 c = (int3)f;
    float3 u = float3(GasFade(t.x), GasFade(t.y), GasFade(t.z));
    float3 du = float3(GasFadeDerivative(t.x), GasFadeDerivative(t.y), GasFadeDerivative(t.z));
    float3 ja = 0, jb = 0, jg = 0;
    [unroll] for (int n = 0; n < 8; n++)
    {
        int3 o = int3(n & 1, (n >> 1) & 1, n >> 2);
        uint3 h = GasHash(c + o) >> 28;
        float3 d = t - float3(o);
        float3 w3 = float3(o.x != 0 ? u.x : 1.0 - u.x, o.y != 0 ? u.y : 1.0 - u.y, o.z != 0 ? u.z : 1.0 - u.z);
        float3 e = float3(o.x != 0 ? du.x : -du.x, o.y != 0 ? du.y : -du.y, o.z != 0 ? du.z : -du.z);
        float w = w3.x * w3.y * w3.z;
        float3 sw = float3(e.x * w3.y * w3.z, w3.x * e.y * w3.z, w3.x * w3.y * e.z);
        ja = ja + GasNoiseCorner(h.x, d, w, sw);
        jb = jb + GasNoiseCorner(h.y, d, w, sw);
        jg = jg + GasNoiseCorner(h.z, d, w, sw);
    }
    return float3(jg.y - jb.z, ja.z - jg.x, jb.x - ja.y);
}

#define GAS_CELL(id, k) \
    GasConstants k = LoadConstants(); \
    int3 c = (int3)id; \
    if (!Inside(c, k)) return;

// ---- stages ----

// Zero the state and both aux buffers (reset): gGasOut0..3 = velocity, scalars, aux0, aux1.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsClear(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    RWStructuredBuffer<uint2> v = ResourceDescriptorHeap[gGasOut0];
    RWStructuredBuffer<uint> s = ResourceDescriptorHeap[gGasOut1];
    RWStructuredBuffer<uint> a0 = ResourceDescriptorHeap[gGasOut2];
    RWStructuredBuffer<uint> a1 = ResourceDescriptorHeap[gGasOut3];
    uint i = Index(c, k);
    v[i] = uint2(0, 0);
    s[i] = 0;
    a0[i] = 0;
    a1[i] = 0;
}

// Window shift by whole cells (gather from cell + shift; outside - empty) and sources: density and temperature += rate*dt*w,
// velocity pulled to the source velocity by blend*w. Weight of a distance d: saturate((R - d) / edge) (half at the radius, the edge
// anti-aliased over at least one cell); w = max(w(head), w(segment) - w(tail)) - the sphere at the head and the part of the capsule swept
// since the tail (the tail sphere was injected by the previous step), so a straight chain of steps sums to one capsule without beads.
// A sphere (Axis = 0) gives w = w(centre).
// Cells no source touches are copied bit for bit. Pressure of the previous step moves with the window (warm start) or is zero.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsSources(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<GasConstants> all = ResourceDescriptorHeap[gGasConstants];
    StructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> s0 = ResourceDescriptorHeap[gGasIn1];
    StructuredBuffer<uint> auxPrev = ResourceDescriptorHeap[gGasIn2];
    RWStructuredBuffer<uint2> v1 = ResourceDescriptorHeap[gGasOut0];
    RWStructuredBuffer<uint> s1 = ResourceDescriptorHeap[gGasOut1];
    RWStructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasOut2];
    int3 from = c + int3(k.ShiftX, k.ShiftY, k.ShiftZ);
    uint2 vu = uint2(0, 0);
    uint su = 0, pu = 0;
    if (Inside(from, k))
    {
        uint j = Index(from, k);
        vu = v0[j];
        su = s0[j];
        if (k.WarmStart != 0) pu = auxPrev[j] & 0xFFFF;
    }
    if ((k.Sources > 0 || k.PlaneSide > 0) && c.x >= k.SourceMinX && c.y >= k.SourceMinY && c.z >= k.SourceMinZ &&
        c.x <= k.SourceMaxX && c.y <= k.SourceMaxY && c.z <= k.SourceMaxZ)
    {
        float3 vel = Unpack3(vu);
        float2 sc = Unpack2(su);
        bool touched = false;
        for (uint n = 0; n < k.Sources; n++)
        {
            GasSource src = all[gGasIndex].Source[n];
            float3 d = float3(c) - src.Tail.xyz;
            float t = saturate(Dot3(d, src.Axis.xyz) * src.Axis.w);
            float3 e = d - src.Axis.xyz * t;
            float3 h = d - src.Axis.xyz;
            float wTail = saturate((src.Tail.w - sqrt(Dot3(d, d))) * src.Rates.w);
            float wSegment = saturate((src.Tail.w - sqrt(Dot3(e, e))) * src.Rates.w);
            float wHead = saturate((src.Tail.w - sqrt(Dot3(h, h))) * src.Rates.w);
            float w = max(wHead, wSegment - wTail);
            if (w > 0)
            {
                touched = true;
                sc = float2(sc.x + src.Rates.x * w, sc.y + src.Rates.y * w);
                vel = vel + (src.Velocity.xyz - vel) * (src.Rates.z * w);
            }
        }
        // Map source: layer weight by the distance to the plane, the map sampled at the projection of the cell.
        if (k.PlaneSide > 0)
        {
            float3 d = float3(c) - float3(k.PlaneX, k.PlaneY, k.PlaneZ);
            float dn = abs(Dot3(d, float3(k.PlaneNx, k.PlaneNy, k.PlaneNz)));
            float ws = saturate((k.PlaneThick - dn) * k.PlaneInvEdge);
            if (ws > 0)
            {
                StructuredBuffer<uint4> map = ResourceDescriptorHeap[gGasIn3];
                MapValue m = SampleMap(map, Dot3(d, float3(k.PlaneUx, k.PlaneUy, k.PlaneUz)) + k.PlaneHalf,
                    Dot3(d, float3(k.PlaneVx, k.PlaneVy, k.PlaneVz)) + k.PlaneHalf, k);
                if (m.Rates.x > 0 || m.Rates.y > 0 || m.Rates.z > 0)
                {
                    touched = true;
                    sc = float2(sc.x + m.Rates.x * ws, sc.y + m.Rates.y * ws);
                    vel = vel + (m.Velocity - vel) * (m.Rates.z * ws);
                }
            }
        }
        if (touched)
        {
            vu = Pack3(vel);
            su = Pack2(clamp(sc, float2(0, 0), float2(HalfMax, HalfMax)));
        }
    }
    uint i = Index(c, k);
    v1[i] = vu;
    s1[i] = su;
    aux[i] = pu;
}

// |curl v| (central differences) into t of aux; pressure stays.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsCurl(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v = ResourceDescriptorHeap[gGasIn0];
    RWStructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasOut0];
    float3 xm = Vel(v, c - int3(1, 0, 0), k), xp = Vel(v, c + int3(1, 0, 0), k);
    float3 ym = Vel(v, c - int3(0, 1, 0), k), yp = Vel(v, c + int3(0, 1, 0), k);
    float3 zm = Vel(v, c - int3(0, 0, 1), k), zp = Vel(v, c + int3(0, 0, 1), k);
    float3 w = float3((yp.z - ym.z) - (zp.y - zm.y), (zp.x - zm.x) - (xp.z - xm.z), (xp.y - xm.y) - (yp.x - ym.x)) * k.HalfInvDx;
    uint i = Index(c, k);
    aux[i] = (aux[i] & 0xFFFF) | (f32tof16(sqrt(Dot3(w, w))) << 16);
}

// Buoyancy F = (alpha*rho - beta*T) * g, vorticity confinement F = eps*dx*(N x w), N = grad|w| / |grad|w||, and curl noise; v += F*dt.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsForces(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v1 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> s1 = ResourceDescriptorHeap[gGasIn1];
    StructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasIn2];
    RWStructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    float3 v = Unpack3(v1[i]);
    float2 s = Unpack2(s1[i]);
    float b = k.Alpha * s.x - k.Beta * s.y;
    float3 force = float3(b * k.GravityX, b * k.GravityY, b * k.GravityZ);
    if (k.Vorticity != 0)
    {
        float3 xm = Vel(v1, c - int3(1, 0, 0), k), xp = Vel(v1, c + int3(1, 0, 0), k);
        float3 ym = Vel(v1, c - int3(0, 1, 0), k), yp = Vel(v1, c + int3(0, 1, 0), k);
        float3 zm = Vel(v1, c - int3(0, 0, 1), k), zp = Vel(v1, c + int3(0, 0, 1), k);
        float3 w = float3((yp.z - ym.z) - (zp.y - zm.y), (zp.x - zm.x) - (xp.z - xm.z), (xp.y - xm.y) - (yp.x - ym.x)) * k.HalfInvDx;
        float3 g = float3(CurlAt(aux, c + int3(1, 0, 0), k) - CurlAt(aux, c - int3(1, 0, 0), k),
            CurlAt(aux, c + int3(0, 1, 0), k) - CurlAt(aux, c - int3(0, 1, 0), k),
            CurlAt(aux, c + int3(0, 0, 1), k) - CurlAt(aux, c - int3(0, 0, 1), k)) * k.HalfInvDx;
        float len = sqrt(Dot3(g, g));
        if (len > 1e-5 && Dot3(v, v) > 1e-10) force = force + Cross3(g / len, w) * k.Vorticity;
    }
    // Curl noise in world coordinates, scaled by min(rho / NoiseDensity, 1) (1 without the mask).
    if (k.NoiseAmp != 0)
    {
        float m = k.NoiseInvDensity > 0 ? min(s.x * k.NoiseInvDensity, 1.0) : 1.0;
        if (m > 0) force = force + GasCurlNoise(float3(c) * k.NoiseStep + float3(k.NoiseX, k.NoiseY, k.NoiseZ)) * (k.NoiseAmp * m);
    }
    v0[i] = Pack3(v + force * k.Dt);
}

// Semi-Lagrangian velocity: v(x - v(x)*dt) trilinear.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsAdvectVelocity(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasIn0];
    RWStructuredBuffer<uint2> v1 = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    float3 q = float3(c) - BackOf(Unpack3(v0[i]), k);
    v1[i] = Pack3(SampleVel(v0, q, k));
}

// Divergence (central differences) into t of aux; pressure - kept (warm start) or zero.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsDivergence(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v = ResourceDescriptorHeap[gGasIn0];
    RWStructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasOut0];
    float3 xm = Vel(v, c - int3(1, 0, 0), k), xp = Vel(v, c + int3(1, 0, 0), k);
    float3 ym = Vel(v, c - int3(0, 1, 0), k), yp = Vel(v, c + int3(0, 1, 0), k);
    float3 zm = Vel(v, c - int3(0, 0, 1), k), zp = Vel(v, c + int3(0, 0, 1), k);
    float div = ((xp.x - xm.x) + (yp.y - ym.y) + (zp.z - zm.z)) * k.HalfInvDx;
    uint i = Index(c, k);
    uint p = k.WarmStart != 0 ? (aux[i] & 0xFFFF) : 0;
    aux[i] = p | (f32tof16(div) << 16);
}

// Red-black SOR half pass gGasPass: cells with (x + y + z) % 2 == gGasPass, in place (neighbours are of the other colour).
// pJ = (sum p_nb - dx^2/dt * div) / 6, p = (1 - w) p + w pJ.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsPressure(uint3 id : SV_DispatchThreadID)
{
    GasConstants k = LoadConstants();
    int3 c = int3(2 * (int)id.x + (int)((id.y + id.z + gGasPass) & 1), (int)id.y, (int)id.z);
    if (!Inside(c, k)) return;
    RWStructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    uint u = aux[i];
    float p = f16tof32(u), div = f16tof32(u >> 16);
    float sum = PresRW(aux, c - int3(1, 0, 0), k) + PresRW(aux, c + int3(1, 0, 0), k) + PresRW(aux, c - int3(0, 1, 0), k) +
        PresRW(aux, c + int3(0, 1, 0), k) + PresRW(aux, c - int3(0, 0, 1), k) + PresRW(aux, c + int3(0, 0, 1), k);
    float next = k.SorKeep * p + (sum - div * k.PressureRhs) * k.SorWeight6;
    aux[i] = f32tof16(next) | (u & 0xFFFF0000);
}

// v -= grad p * dt (central differences), then |v| <= MaxSpeed.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsProject(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v1 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasIn1];
    RWStructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    float3 grad = float3(Pres(aux, c + int3(1, 0, 0), k) - Pres(aux, c - int3(1, 0, 0), k),
        Pres(aux, c + int3(0, 1, 0), k) - Pres(aux, c - int3(0, 1, 0), k),
        Pres(aux, c + int3(0, 0, 1), k) - Pres(aux, c - int3(0, 0, 1), k)) * k.GradScale;
    float3 v = Unpack3(v1[i]) - grad;
    float m2 = Dot3(v, v);
    if (m2 > k.MaxSpeed2) v = v * (k.MaxSpeed / sqrt(m2));
    v0[i] = Pack3(v);
}

// Semi-Lagrangian scalars s(x - v(x)*dt) trilinear, then decay s/(1 + k dt) - c dt.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsAdvectScalars(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> s1 = ResourceDescriptorHeap[gGasIn1];
    RWStructuredBuffer<uint> s0 = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    float3 q = float3(c) - BackOf(Unpack3(v0[i]), k);
    s0[i] = Pack2(Decay(SampleScal(s1, q, k), k));
}

// MacCormack, forward half: the semi-Lagrangian result without decay.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsAdvectForward(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> s1 = ResourceDescriptorHeap[gGasIn1];
    RWStructuredBuffer<uint> s2 = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    float3 q = float3(c) - BackOf(Unpack3(v0[i]), k);
    s2[i] = Pack2(SampleScal(s1, q, k));
}

// MacCormack: f + (s - back(f)) / 2, where back(f) samples the forward result at x + v*dt; outside the range of the eight
// corners of the forward sample - the forward (semi-Lagrangian) value. Then decay.
[numthreads(FLUID3D_GROUP_X, FLUID3D_GROUP_Y, FLUID3D_GROUP_Z)]
void CsAdvectMacCormack(uint3 id : SV_DispatchThreadID)
{
    GAS_CELL(id, k)
    StructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> s1 = ResourceDescriptorHeap[gGasIn1];
    StructuredBuffer<uint> s2 = ResourceDescriptorHeap[gGasIn2];
    RWStructuredBuffer<uint> s0 = ResourceDescriptorHeap[gGasOut0];
    uint i = Index(c, k);
    float3 b = BackOf(Unpack3(v0[i]), k);
    float3 q = float3(c) - b;
    float3 qb = float3(c) + b;
    float2 forward = Unpack2(s2[i]);
    float2 back = SampleScal(s2, qb, k);
    float2 r = forward + (Unpack2(s1[i]) - back) * 0.5;
    float2 lo, hi;
    CornerRange(s1, q, k, lo, hi);
    if (r.x < lo.x || r.x > hi.x) r.x = forward.x;
    if (r.y < lo.y || r.y > hi.y) r.y = forward.y;
    s0[i] = Pack2(Decay(r, k));
}

// ---- stats: one group per z slice, then the slices in order (no atomics) ----

groupshared GasStatsPartial gsStats[FLUID3D_STATS_THREADS];

GasStatsPartial Combine(GasStatsPartial a, GasStatsPartial b)
{
    GasStatsPartial r;
    r.Density = a.Density + b.Density;
    r.Temperature = a.Temperature + b.Temperature;
    r.SumX = a.SumX + b.SumX;
    r.SumY = a.SumY + b.SumY;
    r.SumZ = a.SumZ + b.SumZ;
    r.MaxSpeed = max(a.MaxSpeed, b.MaxSpeed);
    r.MaxDensity = max(a.MaxDensity, b.MaxDensity);
    r.MaxTemperature = max(a.MaxTemperature, b.MaxTemperature);
    r.DivBefore = a.DivBefore + b.DivBefore;
    r.DivAfter = a.DivAfter + b.DivAfter;
    r.Enstrophy = a.Enstrophy + b.Enstrophy;
    r.NonFinite = a.NonFinite + b.NonFinite;
    return r;
}

bool HalfFinite(uint h) { return (h & 0x7C00) != 0x7C00; }

void StatsCell(StructuredBuffer<uint2> v0, StructuredBuffer<uint> s0, StructuredBuffer<uint> aux, int3 c, GasConstants k,
    inout GasStatsPartial acc)
{
    uint i = Index(c, k);
    uint2 vu = v0[i];
    uint su = s0[i];
    if (!HalfFinite(vu.x) || !HalfFinite(vu.x >> 16) || !HalfFinite(vu.y) || !HalfFinite(su) || !HalfFinite(su >> 16))
    {
        acc.NonFinite++;
        return;
    }
    float3 v = Unpack3(vu);
    float2 s = Unpack2(su);
    acc.Density += s.x;
    acc.Temperature += s.y;
    acc.SumX += s.x * ((float)c.x + 0.5);
    acc.SumY += s.x * ((float)c.y + 0.5);
    acc.SumZ += s.x * ((float)c.z + 0.5);
    acc.MaxSpeed = max(acc.MaxSpeed, sqrt(Dot3(v, v)));
    acc.MaxDensity = max(acc.MaxDensity, s.x);
    acc.MaxTemperature = max(acc.MaxTemperature, s.y);
    acc.DivBefore += abs(f16tof32(aux[i] >> 16));
    float3 dx = (Vel(v0, c + int3(1, 0, 0), k) - Vel(v0, c - int3(1, 0, 0), k)) * k.HalfInvDx;
    float3 dy = (Vel(v0, c + int3(0, 1, 0), k) - Vel(v0, c - int3(0, 1, 0), k)) * k.HalfInvDx;
    float3 dz = (Vel(v0, c + int3(0, 0, 1), k) - Vel(v0, c - int3(0, 0, 1), k)) * k.HalfInvDx;
    acc.DivAfter += abs(dx.x + dy.y + dz.z);
    float3 w = float3(dy.z - dz.y, dz.x - dx.z, dx.y - dy.x);
    acc.Enstrophy += Dot3(w, w);
}

[numthreads(FLUID3D_STATS_THREADS, 1, 1)]
void CsStatsSlices(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    GasConstants k = LoadConstants();
    StructuredBuffer<uint2> v0 = ResourceDescriptorHeap[gGasIn0];
    StructuredBuffer<uint> s0 = ResourceDescriptorHeap[gGasIn1];
    StructuredBuffer<uint> aux = ResourceDescriptorHeap[gGasIn2];
    RWStructuredBuffer<GasStatsPartial> partial = ResourceDescriptorHeap[gGasOut0];
    GasStatsPartial acc = (GasStatsPartial)0;
    uint slice = k.Nx * k.Ny;
    for (uint n = thread.x; n < slice; n += FLUID3D_STATS_THREADS)
        StatsCell(v0, s0, aux, int3((int)(n % k.Nx), (int)(n / k.Nx), (int)group.x), k, acc);
    gsStats[thread.x] = acc;
    GroupMemoryBarrierWithGroupSync();
    for (uint half_ = FLUID3D_STATS_THREADS / 2; half_ > 0; half_ >>= 1)
    {
        if (thread.x < half_) gsStats[thread.x] = Combine(gsStats[thread.x], gsStats[thread.x + half_]);
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread.x == 0) partial[group.x] = gsStats[0];
}

// Slices in order -> stats words (Fluid3DStats.Parse).
[numthreads(1, 1, 1)]
void CsStats(uint3 id : SV_DispatchThreadID)
{
    GasConstants k = LoadConstants();
    StructuredBuffer<GasStatsPartial> partial = ResourceDescriptorHeap[gGasIn0];
    RWStructuredBuffer<uint> stats = ResourceDescriptorHeap[gGasOut0];
    GasStatsPartial acc = partial[0];
    for (uint z = 1; z < k.Nz; z++) acc = Combine(acc, partial[z]);
    stats[0] = k.StepLo;
    stats[1] = k.StepHi;
    stats[2] = acc.NonFinite;
    stats[3] = k.Nx;
    stats[4] = k.Ny;
    stats[5] = k.Nz;
    stats[6] = asuint(k.OriginX);
    stats[7] = asuint(k.OriginY);
    stats[8] = asuint(k.OriginZ);
    stats[9] = asuint(k.CellSize);
    stats[10] = asuint(acc.Density);
    stats[11] = asuint(acc.Temperature);
    stats[12] = asuint(acc.MaxSpeed);
    stats[13] = asuint(acc.MaxDensity);
    stats[14] = asuint(acc.MaxTemperature);
    stats[15] = asuint(acc.Density > 0.0 ? acc.SumX / acc.Density : 0.5 * (float)k.Nx);
    stats[16] = asuint(acc.Density > 0.0 ? acc.SumY / acc.Density : 0.5 * (float)k.Ny);
    stats[17] = asuint(acc.Density > 0.0 ? acc.SumZ / acc.Density : 0.5 * (float)k.Nz);
    stats[18] = asuint(acc.DivBefore);
    stats[19] = asuint(acc.DivAfter);
    stats[20] = asuint(acc.Enstrophy);
    for (uint w = 21; w < 32; w++) stats[w] = 0;
}
