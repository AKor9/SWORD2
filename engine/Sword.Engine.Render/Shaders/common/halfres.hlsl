// Прозрачное в 1/2 (HalfResTransparency.cs, CPU-эталон - TransparencyMath): уменьшение глубины сцены до 1/2 (ближайшая глубина
// блока 2x2) и композит цели 1/2 в HDR-сцену апсемплом по глубине, премультиплицированно (смешивание One, InvSrcAlpha).
// Полноэкранный треугольник.
#include "pass.hlsli"
#include "gbuffer.hlsli"

float4 VsFullscreen(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

struct DownsampleOut
{
    float4 color : SV_Target0;
    float depth : SV_Depth;
};

// Тексель 1/2 (i, j) - блок полных пикселей 2i..2i+1 x 2j..2j+1 (за краем нечётного кадра - край): ближайшая глубина блока
// (обратный Z - наибольшая), чтобы марш не проходил сквозь передний план. Цель цвета 1/2 - прозрачный ноль.
DownsampleOut PsDownsampleDepth(float4 pos : SV_Position)
{
    Texture2D<float> scene = ResourceDescriptorHeap[HALFRES_SCENE_DEPTH];
    int2 p = int2(pos.xy) * 2;
    int2 q = min(p + 1, int2(HALFRES_WIDTH, HALFRES_HEIGHT) - 1);
    float a = max(scene.Load(int3(p, 0)), scene.Load(int3(q.x, p.y, 0)));
    float b = max(scene.Load(int3(p.x, q.y, 0)), scene.Load(int3(q, 0)));
    DownsampleOut o;
    o.color = 0;
    o.depth = max(a, b);
    return o;
}

// Соседи 1/2 вдоль оси для полного пикселя x: центр пикселя в текселях 1/2 - x/2 - 1/4; вес второго - 3/4 у чётного x, 1/4 у нечётного.
void HalfResTaps(int x, int size, out int i0, out int i1, out float w1)
{
    int first = (x - 1) >> 1;
    i0 = clamp(first, 0, size - 1);
    i1 = clamp(first + 1, 0, size - 1);
    w1 = (x & 1) == 0 ? 0.75 : 0.25;
}

// Апсемпл по глубине: из четырёх билинейных соседей 1/2 - согласованные по линейной глубине с пикселем (разница не больше допуска x
// глубину пикселя), веса перенормированы; все согласованы - билинейный; ни одного - сосед с ближайшей глубиной. Цвет -
// премультиплицированный, смешивание «поверх»: сцена = цвет + сцена x (1 - альфа).
float4 PsComposite(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> color = ResourceDescriptorHeap[HALFRES_COLOR];
    Texture2D<float> halfDepth = ResourceDescriptorHeap[HALFRES_HALF_DEPTH];
    Texture2D<float> scene = ResourceDescriptorHeap[HALFRES_SCENE];
    float2 k = float2(asfloat(HALFRES_K0), asfloat(HALFRES_K1));
    float tolerance = asfloat(HALFRES_TOLERANCE);
    int2 px = int2(pos.xy);
    float z = SceneViewDepth(scene.Load(int3(px, 0)), k);

    int x0, x1, y0, y1;
    float wx, wy;
    HalfResTaps(px.x, HALFRES_HALF_WIDTH, x0, x1, wx);
    HalfResTaps(px.y, HALFRES_HALF_HEIGHT, y0, y1, wy);
    int2 texel[4] = { int2(x0, y0), int2(x1, y0), int2(x0, y1), int2(x1, y1) };
    float weight[4] = { (1 - wx) * (1 - wy), wx * (1 - wy), (1 - wx) * wy, wx * wy };

    float4 sum = 0, nearest = 0;
    float total = 0, best = 3.402823e38;
    [unroll]
    for (int n = 0; n < 4; n++)
    {
        float4 c = color.Load(int3(texel[n], 0));
        float dz = abs(SceneViewDepth(halfDepth.Load(int3(texel[n], 0)), k) - z);
        if (dz <= tolerance * z)
        {
            sum += weight[n] * c;
            total += weight[n];
        }
        if (dz < best)
        {
            best = dz;
            nearest = c;
        }
    }
    return total > 0 ? sum / total : nearest;
}
