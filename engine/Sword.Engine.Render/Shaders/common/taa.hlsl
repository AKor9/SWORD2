// Временное сглаживание и размытие движения (TemporalAa.cs). PsResolve: история прошлого кадра там, где точка мира пикселя была
// тогда (статичный мир и пустота - по глубине и матрицам; персонажи и оружие - на том же месте экрана: идут вместе с камерой),
// скорость - от ближайшей глубины окрестности 3x3 (край силуэта не рвётся), история зажата в среднее +- TAA_CLIP_GAMMA сигм
// окрестности (YCoCg), смесь - в сжатом цвете c / (1 + яркость) (яркие пиксели не мерцают). PsMotionBlur: статичный мир -
// вдоль его скорости от движения камеры, персонажи не размываются и в размытие фона не попадают.
#include "pass.hlsli"
#include "gbuffer.hlsli"

struct TaaConstants
{
    float4x4 invViewProj;
    float4x4 viewProj;
    float4x4 prevViewProj;
    float2 jitterUv; float blend; uint valid;
    float2 size; float blurScale; float blurMax;
};
_Static_assert(sizeof(TaaConstants) == TAA_CONSTANTS_SIZE, "TaaConstants layout differs from C# (TemporalAa.cs)");

TaaConstants TaaFrame()
{
    StructuredBuffer<TaaConstants> c = ResourceDescriptorHeap[TAA_CONSTANTS];
    return c[0];
}

float4 VsFullscreen(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float3 ToYCoCg(float3 c) { return float3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * c.r - 0.5 * c.b, -0.25 * c.r + 0.5 * c.g - 0.25 * c.b); }
float3 FromYCoCg(float3 y) { return float3(y.x + y.y - y.z, y.x + y.z, y.x - y.y - y.z); }
float3 Compress(float3 c) { return c / (1 + dot(c, BLOOM_LUMA)); }
float3 Expand(float3 c) { return c / max(1 - dot(c, BLOOM_LUMA), 1e-4); }

// Статичный мир: пусто (фон) или приёмник декалей (пол, колонны); персонажи и оружие - нет.
bool TaaStatic(float depth, float4 normal) { return SceneEmpty(depth) || SceneReceivesDecals(normal); }

// Где пиксель px (центр, кадр со сдвигом) лежал в истории: статичный - точка мира по глубине в прошлой матрице без сдвига + сдвиг
// этого кадра (история выровнена по сетке пикселей); персонажи - то же место экрана.
float2 TaaHistoryUv(TaaConstants k, float2 px, float depth, bool staticWorld)
{
    float2 uv = px / k.size;
    if (!staticWorld) return uv;
    float3 world = SceneWorld(px, SceneEmpty(depth) ? 1e-7 : depth, k.size, k.invViewProj);
    float4 prev = mul(k.prevViewProj, float4(world, 1));
    return prev.xy / prev.w * float2(0.5, -0.5) + 0.5 + k.jitterUv;
}

// История сплайном Катмулла - Рома (4x4 текселя пятью билинейными выборками без угловых): при сдвиге на долю пикселя не мылит,
// как билинейная. Веса углов отброшены - сумма нормируется.
float3 TaaHistory(Texture2D<float4> history, float2 uv, float2 size)
{
    float2 p = uv * size;
    float2 t1 = floor(p - 0.5) + 0.5;
    float2 f = p - t1;
    float2 w0 = f * (-0.5 + f * (1 - 0.5 * f));
    float2 w1 = 1 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 t0 = (t1 - 1) / size, t3 = (t1 + 2) / size, t12 = (t1 + w2 / w12) / size;
    float3 c = history.SampleLevel(gLinearClamp, float2(t12.x, t0.y), 0).rgb * (w12.x * w0.y)
        + history.SampleLevel(gLinearClamp, float2(t0.x, t12.y), 0).rgb * (w0.x * w12.y)
        + history.SampleLevel(gLinearClamp, t12, 0).rgb * (w12.x * w12.y)
        + history.SampleLevel(gLinearClamp, float2(t3.x, t12.y), 0).rgb * (w3.x * w12.y)
        + history.SampleLevel(gLinearClamp, float2(t12.x, t3.y), 0).rgb * (w12.x * w3.y);
    float sum = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return max(c / sum, 0);
}

float4 PsResolve(float4 pos : SV_Position) : SV_Target0
{
    TaaConstants k = TaaFrame();
    Texture2D<float4> scene = ResourceDescriptorHeap[TAA_SCENE];
    Texture2D<float> depth = ResourceDescriptorHeap[TAA_DEPTH];
    Texture2D<float4> normals = ResourceDescriptorHeap[TAA_NORMAL];
    Texture2D<float4> history = ResourceDescriptorHeap[TAA_HISTORY];
    int2 p = int2(pos.xy), last = int2(k.size) - 1;
    float3 m1 = 0, m2 = 0, current = 0;
    float closest = -1;
    int2 at = p;
    [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            int2 q = clamp(p + int2(x, y), 0, last);
            float3 s = Compress(clamp(scene.Load(int3(q, 0)).rgb, 0, FILMIC_INPUT_MAX));   // NaN -> 0
            float3 c = ToYCoCg(s);
            m1 += c;
            m2 += c * c;
            if (x == 0 && y == 0) current = s;
            float d = depth.Load(int3(q, 0));
            if (d > closest)
            {
                closest = d;
                at = q;
            }
        }
    if (k.valid == 0) return float4(Expand(current), 1);
    float3 mean = m1 / 9, sigma = sqrt(max(m2 / 9 - mean * mean, 0));
    bool staticWorld = TaaStatic(closest, normals.Load(int3(at, 0)));
    float2 uv = TaaHistoryUv(k, at + 0.5, closest, staticWorld) + (p - at) / k.size;
    if (any(uv < 0) || any(uv > 1)) return float4(Expand(current), 1);
    float3 h = ToYCoCg(Compress(TaaHistory(history, uv, k.size)));
    h = clamp(h, mean - TAA_CLIP_GAMMA * sigma, mean + TAA_CLIP_GAMMA * sigma);
    return float4(Expand(lerp(FromYCoCg(h), current, k.blend)), 1);
}

float4 PsMotionBlur(float4 pos : SV_Position) : SV_Target0
{
    TaaConstants k = TaaFrame();
    Texture2D<float4> scene = ResourceDescriptorHeap[TAA_SCENE];
    Texture2D<float> depth = ResourceDescriptorHeap[TAA_DEPTH];
    Texture2D<float4> normals = ResourceDescriptorHeap[TAA_NORMAL];
    int2 p = int2(pos.xy), last = int2(k.size) - 1;
    float3 c = scene.Load(int3(p, 0)).rgb;
    float d = depth.Load(int3(p, 0));
    if (!TaaStatic(d, normals.Load(int3(p, 0)))) return float4(c, 1);
    // Скорость статичного мира на экране от движения камеры (пиксели за кадр) x выдержка.
    float2 v = (pos.xy / k.size - TaaHistoryUv(k, pos.xy, d, true)) * k.size * k.blurScale;
    float len = length(v);
    if (len < 0.5) return float4(c, 1);
    v *= min(len, k.blurMax) / len;
    float3 sum = c;
    float n = 1;
    for (int i = 0; i < TAA_BLUR_SAMPLES; i++)
    {
        int2 q = clamp(int2(pos.xy + v * ((i + 0.5) / TAA_BLUR_SAMPLES - 0.5)), 0, last);
        float dq = depth.Load(int3(q, 0));
        if (!TaaStatic(dq, normals.Load(int3(q, 0)))) continue;
        sum += scene.Load(int3(q, 0)).rgb;
        n++;
    }
    return float4(sum / n, 1);
}
