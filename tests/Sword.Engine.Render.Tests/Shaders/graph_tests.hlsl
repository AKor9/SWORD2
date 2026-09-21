// Проверочные проходы рендер-графа (RenderGraphGpuTests): вычислительный проход пишет текстуру, графический читает её
// и пишет цель, вычислительный копирует цель в хранилище; цепочка приращений хранилища — порядок записей UAV.
#include "common/pass.hlsli"

// gPass0 — размер (сторона текстуры или число элементов), gPass1..4 — индексы в bindless-куче.

[numthreads(8, 8, 1)]
void CsFill(uint3 id : SV_DispatchThreadID)
{
    RWTexture2D<float> a = ResourceDescriptorHeap[gPass1];
    if (id.x < gPass0 && id.y < gPass0) a[id.xy] = id.x + 100.0 * id.y;
}

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float PsDouble(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float> a = ResourceDescriptorHeap[gPass2];
    return a.Load(int3(pos.xy, 0)) * 2;
}

[numthreads(8, 8, 1)]
void CsCopy(uint3 id : SV_DispatchThreadID)
{
    Texture2D<float> b = ResourceDescriptorHeap[gPass3];
    RWStructuredBuffer<float> s = ResourceDescriptorHeap[gPass4];
    if (id.x < gPass0 && id.y < gPass0) s[id.y * gPass0 + id.x] = b.Load(int3(id.xy, 0));
}

[numthreads(64, 1, 1)]
void CsIncrement(uint3 id : SV_DispatchThreadID)
{
    RWStructuredBuffer<uint> s = ResourceDescriptorHeap[gPass4];
    if (id.x < gPass0) s[id.x] = s[id.x] + 1;
}
