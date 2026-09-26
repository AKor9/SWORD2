// Проверочные проходы прозрачного (TransparencyGpuTests): глубина сцены с известной линейной глубиной (обратный Z бесконечной
// проекции: d = near / z), премультиплицированный квад на заданной глубине, мягкий край, слой 1/2 для апсемпла.
#include "common/pass.hlsli"
#include "common/gbuffer.hlsli"

// Полноэкранный треугольник на глубине gPass0 (float; 0 - дальняя плоскость).
float4 VsQuad(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), asfloat(gPass0), 1);
}

struct SceneOut
{
    float4 color : SV_Target0;
    float depth : SV_Depth;
};

// Сцена: цвет (gPass1..3, float) и линейная глубина по столбцу от gPass5 до gPass6 (м, float) при near gPass4 (float); gPass7 -
// ширина цели.
SceneOut PsDepthRamp(float4 pos : SV_Position)
{
    float z = asfloat(gPass5) + (asfloat(gPass6) - asfloat(gPass5)) * (floor(pos.x) + 0.5) / gPass7;
    SceneOut o;
    o.color = float4(asfloat(gPass1), asfloat(gPass2), asfloat(gPass3), 1);
    o.depth = asfloat(gPass4) / z;
    return o;
}

// Сцена: чёрная; глубина - near gPass4 / gPass5 (передний план) внутри прямоугольников [x0, x1) x [y0, y1) из gPass8..gPass23
// (до четырёх; пустой - x0 = x1), иначе near / gPass6 (фон).
SceneOut PsDepthRects(float4 pos : SV_Position)
{
    uint rects[16] = { gPass8, gPass9, gPass10, gPass11, gPass12, gPass13, gPass14, gPass15,
                       gPass16, gPass17, gPass18, gPass19, gPass20, gPass21, gPass22, gPass23 };
    uint2 p = uint2(pos.xy);
    bool front = false;
    [unroll]
    for (uint r = 0; r < 4; r++)
        front = front || (p.x >= rects[r * 4] && p.x < rects[r * 4 + 2] && p.y >= rects[r * 4 + 1] && p.y < rects[r * 4 + 3]);
    SceneOut o;
    o.color = float4(0, 0, 0, 1);
    o.depth = asfloat(gPass4) / (front ? asfloat(gPass5) : asfloat(gPass6));
    return o;
}

// Премультиплицированный цвет gPass1..4 (float RGBA) как есть.
float4 PsPremul(float4 pos : SV_Position) : SV_Target0
{
    return float4(asfloat(gPass1), asfloat(gPass2), asfloat(gPass3), asfloat(gPass4));
}

// Премультиплицированный цвет gPass1..4 с мягким краем: расстояние gPass5 (м), k = gPass6, gPass7, SRV глубины сцены gPass8.
float4 PsFadeQuad(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float> scene = ResourceDescriptorHeap[gPass8];
    float2 k = float2(asfloat(gPass6), asfloat(gPass7));
    float fade = DepthFade(SceneViewDepth(scene.Load(int3(pos.xy, 0)), k), SceneViewDepth(pos.z, k), asfloat(gPass5));
    return float4(asfloat(gPass1), asfloat(gPass2), asfloat(gPass3), asfloat(gPass4)) * fade;
}

// Слой 1/2 (gPass1 - SRV глубины 1/2, k = gPass2, gPass3, альфа gPass5): режим gPass0 = 0 - по слою: тексел ближе gPass4 (м) -
// красный, дальше - синий; 1 - плавный узор ((x + 0.5)/w, (y + 0.5)/h, 0.25) x альфа, w, h - gPass6, gPass7.
float4 PsHalfLayer(float4 pos : SV_Position) : SV_Target0
{
    float a = asfloat(gPass5);
    if (gPass0 == 1) return float4((floor(pos.x) + 0.5) / gPass6, (floor(pos.y) + 0.5) / gPass7, 0.25, 1) * a;
    Texture2D<float> depth = ResourceDescriptorHeap[gPass1];
    float z = SceneViewDepth(depth.Load(int3(pos.xy, 0)), float2(asfloat(gPass2), asfloat(gPass3)));
    return z < asfloat(gPass4) ? float4(a, 0, 0, a) : float4(0, 0, a, a);
}
