// Проверочные проходы текстур пака (TextureGpuTests): тексели мипа массива - в хранилище float4 (Load или выборка линейным
// сэмплером в центре текселя), полноэкранный квад с выборкой текстуры (кадр, который её читает).
#include "common/pass.hlsli"

// gPass0 - SRV текстуры (Texture2DArray), gPass1 - UAV хранилища float4, gPass2 и gPass3 - ширина и высота мипа, gPass4 - слой,
// gPass5 - мип; тексель (x, y) - в элемент y * ширина + x.
[numthreads(8, 8, 1)]
void CsLoad(uint3 id : SV_DispatchThreadID)
{
    Texture2DArray<float4> t = ResourceDescriptorHeap[gPass0];
    RWStructuredBuffer<float4> s = ResourceDescriptorHeap[gPass1];
    if (id.x < gPass2 && id.y < gPass3) s[id.y * gPass2 + id.x] = t.Load(int4(id.xy, gPass4, gPass5));
}

// То же выборкой gLinearWrap в центре текселя: вес соседей 0 - значение текселя после декода sRGB.
[numthreads(8, 8, 1)]
void CsSample(uint3 id : SV_DispatchThreadID)
{
    Texture2DArray<float4> t = ResourceDescriptorHeap[gPass0];
    RWStructuredBuffer<float4> s = ResourceDescriptorHeap[gPass1];
    float2 uv = (id.xy + 0.5) / float2(gPass2, gPass3);
    if (id.x < gPass2 && id.y < gPass3) s[id.y * gPass2 + id.x] = t.SampleLevel(gLinearWrap, float3(uv, gPass4), gPass5);
}

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Кадр с выборкой: текстура gPass0 растянута на цель (gPass2 и gPass3 - её размер), слой 0, мипы по производным.
float4 PsShow(float4 pos : SV_Position) : SV_Target0
{
    Texture2DArray<float4> t = ResourceDescriptorHeap[gPass0];
    return t.Sample(gLinearClamp, float3(pos.xy / float2(gPass2, gPass3), 0));
}
