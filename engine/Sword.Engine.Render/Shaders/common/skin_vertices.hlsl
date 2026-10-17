// Скиннинг кадра (SkinnedMeshes.Prepare, проход skin.vertices): поток на вершину меша, строка групп на экземпляр. Точка и нормаль в
// мире - в буфер вершин кадра (R_SKINNED - UAV) с первой вершины экземпляра; проходы кадра читают их (SkinWorld), а не скинируют сами.
#include "skin.hlsli"

[numthreads(64, 1, 1)]
void CsSkin(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= R_SKINCOUNT) return;
    SkinInstance inst = SkinInstanceOf(id.y);
    SkinnedVertex s;
    SkinTransform(SkinVertexOf(id.x), inst, s.p, s.n);
    RWStructuredBuffer<SkinnedVertex> skinned = ResourceDescriptorHeap[R_SKINNED];
    skinned[inst.vertexBase + id.x] = s;
}
