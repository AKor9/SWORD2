// Отладочное рисование (DebugDraw): полупрозрачные капсулы и отрезки поверх цели, без глубины сцены. Капсула — сетка из
// SV_VertexID (DEBUG_AROUND вершин по кругу, DEBUG_RINGS колец на полусферу), экземпляр — запись кольца кадра. Камера —
// корневые константы: матрица вида-проекции в gPass0…gPass15 (строки Matrix4x4, вектор слева) и глаз DEBUG_EYE_*.
#include "pass.hlsli"

struct DebugVertex { float3 p; uint colour; };
struct DebugCapsule { float3 center; float radius; float4 rotation; float halfLength; uint colour; uint2 pad; };
_Static_assert(sizeof(DebugVertex) == DEBUG_VERTEX_SIZE, "DebugVertex layout differs from C# DebugVertex (DebugDraw.cs)");
_Static_assert(sizeof(DebugCapsule) == DEBUG_CAPSULE_SIZE, "DebugCapsule layout differs from C# DebugCapsule (DebugDraw.cs)");

static const float DebugHalfPi = 1.57079633;
static const float DebugTau = 6.28318531;

float4x4 DebugViewProj()
{
    return float4x4(
        asfloat(gPass0), asfloat(gPass1), asfloat(gPass2), asfloat(gPass3),
        asfloat(gPass4), asfloat(gPass5), asfloat(gPass6), asfloat(gPass7),
        asfloat(gPass8), asfloat(gPass9), asfloat(gPass10), asfloat(gPass11),
        asfloat(gPass12), asfloat(gPass13), asfloat(gPass14), asfloat(gPass15));
}

float4 DebugColour(uint c)
{
    return float4(c & 0xFFu, (c >> 8) & 0xFFu, (c >> 16) & 0xFFu, (c >> 24) & 0xFFu) * (1.0 / 255.0);
}

float3 DebugRotate(float4 q, float3 v)
{
    float3 t = 2 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

// ── капсулы ───────────────────────────────────────────────────────────────

struct CapsuleVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation float4 colour : COLOR0;
};

CapsuleVs VsCapsule(uint vid : SV_VertexID, uint inst : SV_InstanceID)
{
    StructuredBuffer<DebugCapsule> capsules = ResourceDescriptorHeap[DEBUG_CAPSULES];
    DebugCapsule c = capsules[inst];
    const uint2 k[6] = { uint2(0, 0), uint2(1, 0), uint2(1, 1), uint2(0, 0), uint2(1, 1), uint2(0, 1) };
    uint quad = vid / 6u;
    uint2 corner = k[vid % 6u];
    uint row = quad / DEBUG_AROUND + corner.y;   // 0…2·DEBUG_RINGS+1: нижняя полусфера до экватора, затем верхняя
    uint col = quad % DEBUG_AROUND + corner.x;
    float lat, cap;
    if (row <= DEBUG_RINGS)
    {
        lat = -DebugHalfPi + row * (DebugHalfPi / DEBUG_RINGS);
        cap = -1;
    }
    else
    {
        lat = (row - DEBUG_RINGS - 1) * (DebugHalfPi / DEBUG_RINGS);
        cap = 1;
    }
    float phi = col * (DebugTau / DEBUG_AROUND);
    float3 n = float3(cos(lat) * cos(phi), sin(lat), cos(lat) * sin(phi));
    float3 local = n * c.radius + float3(0, cap * c.halfLength, 0);

    CapsuleVs o;
    o.world = c.center + DebugRotate(c.rotation, local);
    o.pos = mul(float4(o.world, 1), DebugViewProj());
    o.normal = DebugRotate(c.rotation, n);
    o.colour = DebugColour(c.colour);
    return o;
}

// Видна только сторона к камере (по нормали — без зависимости от обхода треугольников): кромка плотнее середины, так
// капсула читается поверх персонажа, а персонаж — сквозь неё.
float4 PsCapsule(CapsuleVs i) : SV_Target0
{
    float3 n = normalize(i.normal);
    float3 v = normalize(float3(asfloat(DEBUG_EYE_X), asfloat(DEBUG_EYE_Y), asfloat(DEBUG_EYE_Z)) - i.world);
    float nv = dot(n, v);
    if (nv <= 0) discard;
    float rim = 1 - nv;
    return float4(i.colour.rgb * (0.55 + 0.45 * nv), i.colour.a * lerp(0.3, 1.0, rim * rim));
}

// ── отрезки ───────────────────────────────────────────────────────────────

struct LineVs
{
    float4 pos : SV_Position;
    float4 colour : COLOR0;
};

LineVs VsLine(uint vid : SV_VertexID)
{
    StructuredBuffer<DebugVertex> verts = ResourceDescriptorHeap[DEBUG_LINES];
    DebugVertex v = verts[vid];
    LineVs o;
    o.pos = mul(float4(v.p, 1), DebugViewProj());
    o.colour = DebugColour(v.colour);
    return o;
}

float4 PsLine(LineVs i) : SV_Target0
{
    return i.colour;
}
