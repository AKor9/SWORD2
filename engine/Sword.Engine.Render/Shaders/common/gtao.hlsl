// Затенение рассеянного света (AmbientOcclusion.cs): GTAO (Jimenez и др., 2016) по глубине и нормалям сцены. CsGtao - на пиксель
// AO_SLICES плоскостей через луч взгляда (угол - шум 4x4), в каждой - горизонты по AO_STEPS шагов в обе стороны в радиусе Radius
// метров, видимость - интеграл косинуса между горизонтами вокруг проекции нормали. CsBlur - среднее 4x4 с весом по расстоянию
// до глаза: шум 4x4 уходит, края силуэтов не размываются. Выход: r - видимость 0..1, g - расстояние до глаза, м.
#include "pass.hlsli"
#include "gbuffer.hlsli"

struct AoConstants
{
    float4x4 invViewProj;
    float4x4 viewProj;
    float3 eye; float radius;
    float3 right; float intensity;
    float3 up; float pad0;
    uint width; uint height; uint pad1; uint pad2;
};
_Static_assert(sizeof(AoConstants) == AO_CONSTANTS_SIZE, "AoConstants layout differs from C# (AmbientOcclusion.cs)");

static const float AO_PI = 3.14159265;

AoConstants AoFrame()
{
    StructuredBuffer<AoConstants> c = ResourceDescriptorHeap[AO_CONSTANTS];
    return c[0];
}

// Горизонт стороны: наибольший косинус угла к взгляду среди точек глубины на шагах вдоль dir (пиксели), с угасанием у края радиуса.
float AoHorizon(Texture2D<float> depth, AoConstants k, float2 px, float2 dir, float pixels, float jitter, float3 p, float3 v)
{
    float best = -1;
    for (int j = 0; j < AO_STEPS; j++)
    {
        float t = (j + jitter) / AO_STEPS;
        float2 q = px + dir * (1 + t * t * (pixels - 1));
        if (any(q < 0) || q.x >= k.width || q.y >= k.height) break;
        float d = depth.Load(int3(q, 0));
        if (SceneEmpty(d)) continue;
        float3 s = SceneWorld(floor(q) + 0.5, d, float2(k.width, k.height), k.invViewProj) - p;
        float l = length(s);
        float w = saturate((k.radius - l) / (k.radius * AO_FALLOFF));
        best = max(best, lerp(-1, dot(s, v) / max(l, 1e-4), w));
    }
    return best;
}

[numthreads(8, 8, 1)]
void CsGtao(uint3 id : SV_DispatchThreadID)
{
    AoConstants k = AoFrame();
    if (id.x >= k.width || id.y >= k.height) return;
    RWTexture2D<float2> target = ResourceDescriptorHeap[AO_TARGET];
    Texture2D<float> depth = ResourceDescriptorHeap[AO_DEPTH];
    Texture2D<float4> normals = ResourceDescriptorHeap[AO_NORMAL];
    float d = depth.Load(int3(id.xy, 0));
    if (SceneEmpty(d))
    {
        target[id.xy] = float2(1, 0);
        return;
    }
    float2 px = id.xy + 0.5;
    float3 p = SceneWorld(px, d, float2(k.width, k.height), k.invViewProj);
    float3 v = normalize(k.eye - p);
    float3 n = SceneNormal(normals.Load(int3(id.xy, 0)));
    float dist = distance(k.eye, p);
    // Радиус в пикселях: проекция отрезка Radius по оси камеры вправо.
    float4 c0 = mul(k.viewProj, float4(p, 1)), c1 = mul(k.viewProj, float4(p + k.right * k.radius, 1));
    float pixels = min(length((c1.xy / c1.w - c0.xy / c0.w) * 0.5 * float2(k.width, k.height)), AO_MAX_PIXELS);
    if (pixels < 2)
    {
        target[id.xy] = float2(1, dist);
        return;
    }
    // Шум 4x4: угол плоскостей и сдвиг шагов - разные у соседей, размытие 4x4 их усредняет.
    uint cell = (id.x & 3) + (id.y & 3) * 4;
    float angle = ((cell * 7) & 15) / 16.0, jitter = ((cell * 11 + 5) & 15) / 16.0;
    float visibility = 0;
    [unroll] for (int i = 0; i < AO_SLICES; i++)
    {
        float phi = (i + angle) * AO_PI / AO_SLICES;
        float2 dir = float2(cos(phi), sin(phi));
        // Направление на экране (y вниз) в мире, поперёк взгляда; плоскость - взгляд и оно.
        float3 t = dir.x * k.right - dir.y * k.up;
        t = normalize(t - v * dot(t, v));
        float3 axis = normalize(cross(t, v));
        float3 np = n - axis * dot(n, axis);
        float len = length(np);
        float cosN = saturate(dot(np, v) / max(len, 1e-4));
        float gamma = (dot(np, t) >= 0 ? 1 : -1) * acos(cosN);
        float h0 = -acos(clamp(AoHorizon(depth, k, px, -dir, pixels, jitter, p, v), -1, 1));
        float h1 = acos(clamp(AoHorizon(depth, k, px, dir, pixels, jitter, p, v), -1, 1));
        h0 = gamma + max(h0 - gamma, -AO_PI / 2);
        h1 = gamma + min(h1 - gamma, AO_PI / 2);
        float cn = cos(gamma), sn = sin(gamma);
        visibility += len * 0.25 * ((-cos(2 * h0 - gamma) + cn + 2 * h0 * sn) + (-cos(2 * h1 - gamma) + cn + 2 * h1 * sn));
    }
    // Видимость плоскости у наклонной нормали бывает больше 1 - верна в среднем по углам: зажим и степень - после размытия.
    target[id.xy] = float2(visibility / AO_SLICES, dist);
}

[numthreads(8, 8, 1)]
void CsBlur(uint3 id : SV_DispatchThreadID)
{
    AoConstants k = AoFrame();
    if (id.x >= k.width || id.y >= k.height) return;
    Texture2D<float2> source = ResourceDescriptorHeap[AO_SOURCE];
    RWTexture2D<float2> target = ResourceDescriptorHeap[AO_TARGET];
    float2 own = source.Load(int3(id.xy, 0));
    if (own.y <= 0)
    {
        target[id.xy] = float2(1, 0);
        return;
    }
    float sum = 0, weight = 0;
    for (int y = -2; y < 2; y++)
        for (int x = -2; x < 2; x++)
        {
            int2 q = clamp(int2(id.xy) + int2(x, y), 0, int2(k.width, k.height) - 1);
            float2 s = source.Load(int3(q, 0));
            float w = saturate(1 - abs(s.y - own.y) / (0.05 * own.y + 0.05));
            sum += s.x * w;
            weight += w;
        }
    target[id.xy] = float2(pow(saturate(sum / max(weight, 1e-4)), k.intensity), own.y);
}
