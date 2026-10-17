// Временное сглаживание и размытие движения (TemporalAa.cs). Скорость пикселя - из цели скоростей сцены (предпроход игры: камера и
// движение объектов, скин - по палитре прошлого кадра), без неё или в пустоте - по глубине и матрицам (только камера; персонажи и
// оружие - признак «не приёмник декалей» - тогда стоят на месте экрана, идут вместе с камерой).
// PsResolve: история прошлого кадра там, откуда пиксель пришёл (скорость - от ближайшей глубины окрестности 3x3: край силуэта не
// рвётся), история зажата в среднее +- TAA_CLIP_GAMMA сигм окрестности (YCoCg), смесь - в сжатом цвете c / (1 + яркость).
// Размытие движения по плиткам TAA_TILE (по мотивам McGuire и др., 2012, своими словами): CsTileMax - наибольшая скорость плитки,
// CsNeighborMax - наибольшая из 3x3 плиток, PsMotionBlur - выборки вдоль неё: то, что ближе и движется, ложится на дальнее (конус
// своей скорости), дальнее под ним - своим конусом, на одной глубине - цилиндры обеих скоростей.
#include "pass.hlsli"
#include "gbuffer.hlsli"

struct TaaConstants
{
    float4x4 invViewProj;
    float4x4 viewProj;
    float4x4 prevViewProj;
    float2 jitterUv; float blend; uint valid;
    float2 size; float blurScale; float blurMax;
    uint velocity; uint tilesX; uint tilesY; uint pad;
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

// Скорость пикселя p (центр, кадр со сдвигом), доли экрана за кадр без сдвига: цель скоростей; в пустоте или без цели - камера по
// глубине (точка мира в прошлой матрице); персонаж без цели - 0.
float2 TaaVelocity(TaaConstants k, int2 p, float depth, float4 normal)
{
    if (k.velocity != TAA_NONE && !SceneEmpty(depth))
    {
        Texture2D<float2> velocity = ResourceDescriptorHeap[k.velocity];
        return velocity.Load(int3(p, 0));
    }
    if (!TaaStatic(depth, normal)) return 0;
    float2 px = p + 0.5;
    float3 world = SceneWorld(px, SceneEmpty(depth) ? 1e-7 : depth, k.size, k.invViewProj);
    float4 prev = mul(k.prevViewProj, float4(world, 1));
    // Без сдвига: этот кадр - пиксель минус сдвиг, прошлый - точка в прошлой матрице.
    return px / k.size - k.jitterUv - (prev.xy / prev.w * float2(0.5, -0.5) + 0.5);
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
    // История выровнена по сетке пикселей: пиксель p пришёл из p - скорость (скорость - без сдвига обоих кадров).
    float2 uv = (p + 0.5) / k.size - TaaVelocity(k, at, closest, normals.Load(int3(at, 0)));
    if (any(uv < 0) || any(uv > 1)) return float4(Expand(current), 1);
    float3 h = ToYCoCg(Compress(TaaHistory(history, uv, k.size)));
    h = clamp(h, mean - TAA_CLIP_GAMMA * sigma, mean + TAA_CLIP_GAMMA * sigma);
    return float4(Expand(lerp(FromYCoCg(h), current, k.blend)), 1);
}

// ---- Размытие движения ----

// Скорость размытия пикселя, пиксели: скорость x размер x выдержка, не длиннее blurMax.
float2 BlurVelocity(TaaConstants k, int2 p, Texture2D<float> depth, Texture2D<float4> normals)
{
    float2 v = TaaVelocity(k, p, depth.Load(int3(p, 0)), normals.Load(int3(p, 0))) * k.size * k.blurScale;
    float len = length(v);
    return len > k.blurMax ? v * (k.blurMax / len) : v;
}

groupshared float3 gTile[256];

[numthreads(16, 16, 1)]
void CsTileMax(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    TaaConstants k = TaaFrame();
    Texture2D<float> depth = ResourceDescriptorHeap[TAA_DEPTH];
    Texture2D<float4> normals = ResourceDescriptorHeap[TAA_NORMAL];
    RWTexture2D<float2> tiles = ResourceDescriptorHeap[TAA_TARGET];
    int2 p = group.xy * TAA_TILE + local.xy;
    float2 v = all(p < int2(k.size)) ? BlurVelocity(k, p, depth, normals) : 0;
    gTile[index] = float3(v, dot(v, v));
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint step = 128; step > 0; step >>= 1)
    {
        if (index < step && gTile[index + step].z > gTile[index].z) gTile[index] = gTile[index + step];
        GroupMemoryBarrierWithGroupSync();
    }
    if (index == 0) tiles[group.xy] = gTile[0].xy;
}

[numthreads(8, 8, 1)]
void CsNeighborMax(uint3 id : SV_DispatchThreadID)
{
    TaaConstants k = TaaFrame();
    if (id.x >= k.tilesX || id.y >= k.tilesY) return;
    Texture2D<float2> tiles = ResourceDescriptorHeap[TAA_TILES];
    RWTexture2D<float2> neighbor = ResourceDescriptorHeap[TAA_TARGET];
    float2 best = 0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            int2 t = clamp(int2(id.xy) + int2(x, y), 0, int2(k.tilesX, k.tilesY) - 1);
            float2 v = tiles.Load(int3(t, 0));
            if (dot(v, v) > dot(best, best)) best = v;
        }
    neighbor[id.xy] = best;
}

// Расстояние вдоль взгляда (в долях; обратный Z с бесконечной дальней плоскостью: ~ 1 / глубина); пусто - очень далеко.
float BlurDistance(float depth) { return SceneEmpty(depth) ? 1e6 : 1 / depth; }

// a ближе b (на 5 % расстояния - полностью).
float BlurCloser(float a, float b) { return saturate((b - a) / (0.05 * b)); }
float BlurCone(float dist, float speed) { return saturate(1 - dist / max(speed, 1e-3)); }
float BlurCylinder(float dist, float speed) { return 1 - smoothstep(0.95 * speed, 1.05 * speed + 1e-3, dist); }

float4 PsMotionBlur(float4 pos : SV_Position) : SV_Target0
{
    TaaConstants k = TaaFrame();
    Texture2D<float4> scene = ResourceDescriptorHeap[TAA_SCENE];
    Texture2D<float> depth = ResourceDescriptorHeap[TAA_DEPTH];
    Texture2D<float4> normals = ResourceDescriptorHeap[TAA_NORMAL];
    Texture2D<float2> neighbor = ResourceDescriptorHeap[TAA_NEIGHBOR];
    int2 p = int2(pos.xy), last = int2(k.size) - 1;
    float3 c = scene.Load(int3(p, 0)).rgb;
    float2 vn = neighbor.Load(int3(p / TAA_TILE, 0));
    float ln = length(vn);
    if (ln < 0.5) return float4(c, 1);
    float zc = BlurDistance(depth.Load(int3(p, 0)));
    float lc = length(BlurVelocity(k, p, depth, normals));
    // Шум выборок по пикселю (interleaved gradient noise): ступеньки N выборок превращаются в мелкое зерно, его гасит TAA.
    float noise = frac(52.9829189 * frac(dot(pos.xy, float2(0.06711056, 0.00583715)))) - 0.5;
    float total = 1 / max(lc, 1);
    float3 sum = c * total;
    for (int i = 0; i < TAA_BLUR_SAMPLES; i++)
    {
        float t = lerp(-1, 1, (i + 0.5 + noise) / TAA_BLUR_SAMPLES);
        int2 q = clamp(int2(pos.xy + vn * (t * 0.5)), 0, last);
        float dist = abs(t) * ln * 0.5;
        float zq = BlurDistance(depth.Load(int3(q, 0)));
        float lq = length(BlurVelocity(k, q, depth, normals));
        float w = BlurCloser(zq, zc) * BlurCone(dist, lq) + BlurCloser(zc, zq) * BlurCone(dist, lc)
            + BlurCylinder(dist, lq) * BlurCylinder(dist, lc) * 2;
        sum += scene.Load(int3(q, 0)).rgb * w;
        total += w;
    }
    return float4(sum / total, 1);
}
