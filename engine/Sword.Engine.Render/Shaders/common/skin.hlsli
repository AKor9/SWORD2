// Скиннинг (SkinnedMeshes): вершина меша, палитра 3×4, экземпляр — раскладки SkinVertex, GpuMat34, GpuSkinInstance
// в C#. Перенос VsSkin SWORD 2eab13b: четыре веса, четвёртый — 1 − сумма, сустав не дальше последнего у меша.
#ifndef SWORD_SKIN_HLSLI
#define SWORD_SKIN_HLSLI
#include "pass.hlsli"

struct SkinVertex { float3 p; float3 n; float2 uv; uint joints; float3 w; };
struct SkinMat { float4 r0; float4 r1; float4 r2; };
struct SkinInstance { uint palette; uint jointCount; uint owner; uint flags; float3 colour; float glow; };
_Static_assert(sizeof(SkinVertex) == SKIN_VERTEX_SIZE, "SkinVertex layout differs from C# SkinVertex (Formats/SkinnedMeshData.cs)");
_Static_assert(sizeof(SkinMat) == SKIN_MAT_SIZE, "SkinMat layout differs from C# GpuMat34");
_Static_assert(sizeof(SkinInstance) == SKIN_INSTANCE_SIZE, "SkinInstance layout differs from C# GpuSkinInstance");

float3 SkinPoint(SkinMat m, float3 p) { return p.x * m.r0.xyz + p.y * m.r1.xyz + p.z * m.r2.xyz + float3(m.r0.w, m.r1.w, m.r2.w); }
float3 SkinDir(SkinMat m, float3 d) { return d.x * m.r0.xyz + d.y * m.r1.xyz + d.z * m.r2.xyz; }

/// Экземпляр вызова: SV_InstanceID считается от первого экземпляра меша (R_SKINBASE).
SkinInstance SkinInstanceOf(uint iid)
{
    StructuredBuffer<SkinInstance> insts = ResourceDescriptorHeap[R_SKININST];
    return insts[R_SKINBASE + iid];
}

SkinVertex SkinVertexOf(uint vid)
{
    StructuredBuffer<SkinVertex> verts = ResourceDescriptorHeap[R_SKINVERTS];
    return verts[vid];
}

/// Точка и нормаль вершины в мире (нормаль не нормирована).
void SkinTransform(SkinVertex v, SkinInstance inst, out float3 world, out float3 normal)
{
    StructuredBuffer<SkinMat> palette = ResourceDescriptorHeap[R_SKINPAL];
    float4 w = float4(v.w, saturate(1 - v.w.x - v.w.y - v.w.z));
    world = 0;
    normal = 0;
    for (uint k = 0; k < 4; k++)
    {
        if (w[k] <= 0) continue;
        uint j = min((v.joints >> (8 * k)) & 0xFF, inst.jointCount - 1);
        SkinMat m = palette[inst.palette + j];
        world += w[k] * SkinPoint(m, v.p);
        normal += w[k] * SkinDir(m, v.n);
    }
}

/// Сустав вершины с наибольшим весом (первый при равенстве) — по нему часть пикселя ID-буфера.
uint SkinDominantJoint(SkinVertex v, SkinInstance inst)
{
    float4 w = float4(v.w, saturate(1 - v.w.x - v.w.y - v.w.z));
    uint best = 0;
    for (uint k = 1; k < 4; k++)
        if (w[k] > w[best]) best = k;
    return min((v.joints >> (8 * best)) & 0xFF, inst.jointCount - 1);
}

/// Код ID-буфера (SkinnedMeshes.IdOf): владелец экземпляра << SKIN_ID_PART_BITS | часть сустава (R_SKINPARTS меша вызова);
/// экземпляр без владельца — 0.
uint SkinId(uint owner, uint joint)
{
    if (owner == 0) return 0;
    StructuredBuffer<uint> parts = ResourceDescriptorHeap[R_SKINPARTS];
    return (owner << SKIN_ID_PART_BITS) | (parts[joint] & ((1u << SKIN_ID_PART_BITS) - 1));
}

#endif
