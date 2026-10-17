// SMAA 1x (Jimenez и др., 2012) после тонмапа, три полноэкранных прохода (Smaa.cs, CPU-эталон SmaaMath): края по яркости
// sRGB-кодированного цвета с местным контрастом -> веса: концы линии края, пересекающие края на концах, площадь под линией,
// переведённой в наклонную (ортогональные узоры, скругление углов; диагонали - серии ступеней 1:1) -> смешивание с соседом через край.
// Своё: площадь — формулой, а не таблицей AreaTex; концы линии — поштучными Load, а не SearchTex; диагональ — только у прямой;
// смешивание — в линейном цвете.
// Цель краёв: r — край у левой стороны пикселя, g — у верхней (0 или 1).
// Цель весов: r, g — верхний край (r — этому пикселю, g — верхнему); b, a — левый край (b — этому, a — левому).
#include "pass.hlsli"

float4 VsFullscreen(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

int2 SmaaSize(Texture2D<float4> t)
{
    uint w, h;
    t.GetDimensions(w, h);
    return int2(w, h);
}

float SmaaLuma(Texture2D<float4> color, int2 p, int2 size)
{
    float3 c = color.Load(int3(clamp(p, 0, size - 1), 0)).rgb;
    return c.r * SMAA_LUMA_R + c.g * SMAA_LUMA_G + c.b * SMAA_LUMA_B;
}

// Края пикселя; за кадром — нет.
float2 SmaaEdge(Texture2D<float4> edges, int2 p, int2 size)
{
    return all(p >= 0) && all(p < size) ? edges.Load(int3(p, 0)).rg : 0;
}

float4 PsSmaaEdges(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> color = ResourceDescriptorHeap[SMAA_COLOR];
    int2 size = SmaaSize(color);
    int2 p = int2(pos.xy);
    float l = SmaaLuma(color, p, size);
    float lLeft = SmaaLuma(color, p + int2(-1, 0), size);
    float lTop = SmaaLuma(color, p + int2(0, -1), size);
    float2 delta = abs(l - float2(lLeft, lTop));
    float2 edges = step(SMAA_THRESHOLD, delta);
    if (edges.x + edges.y == 0) return 0;
    // Местный контраст: край много слабее сильнейшего рядом — не край (иначе размылась бы деталь).
    float lRight = SmaaLuma(color, p + int2(1, 0), size);
    float lBottom = SmaaLuma(color, p + int2(0, 1), size);
    float2 maxDelta = max(delta, abs(l - float2(lRight, lBottom)));
    float lLeftLeft = SmaaLuma(color, p + int2(-2, 0), size);
    float lTopTop = SmaaLuma(color, p + int2(0, -2), size);
    maxDelta = max(maxDelta, abs(float2(lLeft, lTop) - float2(lLeftLeft, lTopTop)));
    float finalDelta = max(maxDelta.x, maxDelta.y);
    edges *= step(finalDelta, SMAA_CONTRAST_ADAPTATION * delta);
    return float4(edges, 0, 0);
}

// Площадь под отрезком p1 -> p2 над пикселем [x, x + 1] линии: x — часть с отрицательной стороны (этот пиксель),
// y — с положительной (пиксель через край).
float2 SmaaSegment(float2 p1, float2 p2, float x)
{
    float dx = p2.x - p1.x, dy = p2.y - p1.y;
    float x1 = x, x2 = x + 1;
    if (!((x1 >= p1.x && x1 < p2.x) || (x2 > p1.x && x2 <= p2.x))) return 0;
    float y1 = p1.y + dy * (x1 - p1.x) / dx;
    float y2 = p1.y + dy * (x2 - p1.x) / dx;
    float a1, a2;
    if ((y1 >= 0) == (y2 >= 0) || abs(y1) < 1e-4 || abs(y2) < 1e-4)
    {
        a1 = (y1 + y2) * 0.5;
        a2 = 0;
    }
    else
    {
        // Отрезок пересекает край внутри пикселя: два треугольника разных знаков.
        float xc = p1.x - p1.y * dx / dy;
        float f = xc - floor(xc);
        a1 = xc > p1.x ? y1 * f * 0.5 : 0;
        a2 = xc < p2.x ? y2 * (1 - f) * 0.5 : 0;
    }
    return float2(max(-a1, 0) + max(-a2, 0), max(a1, 0) + max(a2, 0));
}

// Узоры U: короткие смягчены (корень площади), длинные — точная площадь.
float2 SmaaSmooth(float d, float2 a)
{
    float2 b = sqrt(a * 2) * 0.5;
    return b + (a - b) * saturate(d / SMAA_SMOOTH_DISTANCE);
}

// Биты узора: 1 — пересекающий край в начале со стороны этого пикселя, 2 — в конце с этой стороны, 4 — в начале через край,
// 8 — в конце через край. left, right — расстояния до концов линии, пиксели.
float2 SmaaArea(uint pattern, float left, float right)
{
    float x = left, d = left + right + 1, h = d * 0.5;
    const float o1 = 0.5, o2 = -0.5;
    switch (pattern)
    {
        case 1: return left <= right ? SmaaSegment(float2(0, o2), float2(h, 0), x) : 0;
        case 2: return left >= right ? SmaaSegment(float2(h, 0), float2(d, o2), x) : 0;
        case 3: return SmaaSmooth(d, SmaaSegment(float2(0, o2), float2(h, 0), x)) + SmaaSmooth(d, SmaaSegment(float2(h, 0), float2(d, o2), x));
        case 4: return left <= right ? SmaaSegment(float2(0, o1), float2(h, 0), x) : 0;
        case 6: case 7: case 14: return SmaaSegment(float2(0, o1), float2(d, o2), x);
        case 8: return left >= right ? SmaaSegment(float2(h, 0), float2(d, o1), x) : 0;
        case 9: case 11: case 13: return SmaaSegment(float2(0, o2), float2(d, o1), x);
        case 12: return SmaaSmooth(d, SmaaSegment(float2(0, o1), float2(h, 0), x)) + SmaaSmooth(d, SmaaSegment(float2(h, 0), float2(d, o1), x));
        default: return 0;
    }
}

// Скругление углов: пересекающий край идёт дальше конца линии — настоящий угол, у этого конца смешивать меньше.
float2 SmaaCorners(float2 d, float nearStart, float nearEnd, float farStart, float farEnd)
{
    float2 lr = step(d.xy, d.yx);
    float2 rounding = (1 - SMAA_CORNER_ROUNDING) * lr / (lr.x + lr.y);
    return saturate(float2(1 - (rounding.x * nearStart + rounding.y * nearEnd), 1 - (rounding.x * farStart + rounding.y * farEnd)));
}

// Верхний край p: линия вдоль x; пересекающие — левые края этой строки и строки выше.
float2 SmaaHorizontal(Texture2D<float4> edges, int2 p, int2 size)
{
    int left = p.x;
    [loop] for (int i = 0; i < SMAA_MAX_SEARCH; i++)
    {
        if (SmaaEdge(edges, int2(left, p.y), size).r + SmaaEdge(edges, int2(left, p.y - 1), size).r > 0) break;
        if (SmaaEdge(edges, int2(left - 1, p.y), size).g == 0) break;
        left--;
    }
    int right = p.x;
    [loop] for (int j = 0; j < SMAA_MAX_SEARCH; j++)
    {
        if (SmaaEdge(edges, int2(right + 1, p.y), size).r + SmaaEdge(edges, int2(right + 1, p.y - 1), size).r > 0) break;
        if (SmaaEdge(edges, int2(right + 1, p.y), size).g == 0) break;
        right++;
    }
    uint pattern = uint(SmaaEdge(edges, int2(left, p.y), size).r) | uint(SmaaEdge(edges, int2(right + 1, p.y), size).r) << 1
        | uint(SmaaEdge(edges, int2(left, p.y - 1), size).r) << 2 | uint(SmaaEdge(edges, int2(right + 1, p.y - 1), size).r) << 3;
    float2 d = float2(p.x - left, right - p.x);
    return SmaaArea(pattern, d.x, d.y) * SmaaCorners(d,
        SmaaEdge(edges, int2(left, p.y + 1), size).r, SmaaEdge(edges, int2(right + 1, p.y + 1), size).r,
        SmaaEdge(edges, int2(left, p.y - 2), size).r, SmaaEdge(edges, int2(right + 1, p.y - 2), size).r);
}

// Левый край p: то же, транспонированно.
float2 SmaaVertical(Texture2D<float4> edges, int2 p, int2 size)
{
    int top = p.y;
    [loop] for (int i = 0; i < SMAA_MAX_SEARCH; i++)
    {
        if (SmaaEdge(edges, int2(p.x, top), size).g + SmaaEdge(edges, int2(p.x - 1, top), size).g > 0) break;
        if (SmaaEdge(edges, int2(p.x, top - 1), size).r == 0) break;
        top--;
    }
    int bottom = p.y;
    [loop] for (int j = 0; j < SMAA_MAX_SEARCH; j++)
    {
        if (SmaaEdge(edges, int2(p.x, bottom + 1), size).g + SmaaEdge(edges, int2(p.x - 1, bottom + 1), size).g > 0) break;
        if (SmaaEdge(edges, int2(p.x, bottom + 1), size).r == 0) break;
        bottom++;
    }
    uint pattern = uint(SmaaEdge(edges, int2(p.x, top), size).g) | uint(SmaaEdge(edges, int2(p.x, bottom + 1), size).g) << 1
        | uint(SmaaEdge(edges, int2(p.x - 1, top), size).g) << 2 | uint(SmaaEdge(edges, int2(p.x - 1, bottom + 1), size).g) << 3;
    float2 d = float2(p.y - top, bottom - p.y);
    return SmaaArea(pattern, d.x, d.y) * SmaaCorners(d,
        SmaaEdge(edges, int2(p.x + 1, top), size).g, SmaaEdge(edges, int2(p.x + 1, bottom + 1), size).g,
        SmaaEdge(edges, int2(p.x - 2, top), size).g, SmaaEdge(edges, int2(p.x - 2, bottom + 1), size).g);
}

// Ячейка диагонали «/» (rising) — пиксель с левым и верхним краем; «\» — с верхним краем, у правого соседа — левый.
bool SmaaCell(Texture2D<float4> edges, int2 p, int2 size, bool rising)
{
    float2 e = SmaaEdge(edges, p, size);
    return rising ? e.r > 0 && e.g > 0 : e.g > 0 && SmaaEdge(edges, p + int2(1, 0), size).r > 0;
}

// Ступень за концом серии: круче - 1/2, положе - -1/2, нет или обе - 0.
float SmaaEnd(float steep, float flat)
{
    return steep > 0 && flat == 0 ? 0.5 : flat > 0 && steep == 0 ? -0.5 : 0;
}

// Сдвиг отрезка от середин ступеней у ячейки на u (0..n): оба конца - прямая от ts до te; один - от него до 0 к середине серии.
float SmaaDiagonalOffset(float ts, float te, float u, float n)
{
    float half = n * 0.5;
    if (ts != 0 && te != 0) return ts + (te - ts) * u / n;
    if (ts != 0) return u < half ? ts * (1 - u / half) : 0;
    return u > half ? te * (u - half) / half : 0;
}

// Диагональ через пиксель с верхним краем: серия ячеек одного вида (шаг (1, -1) у «/», (1, 1) у «\») не короче
// SMAA_MIN_DIAGONAL, концы которой говорят о прямой (наклон или известен один конец); изгиб и серия без концов - не диагональ.
// Вернёт 0 - нет, 1 - «/», 2 - «\»; area - доли пары «пиксель - верхний сосед»: треугольники (1/2 + t)^2/2 и (1/2 - t)^2/2.
uint SmaaDiagonal(Texture2D<float4> edges, int2 p, int2 size, out float2 area)
{
    area = 0;
    uint best = 0;
    int bestN = 0;
    [unroll] for (uint kind = 1; kind <= 2; kind++)
    {
        bool rising = kind == 1;
        if (!SmaaCell(edges, p, size, rising)) continue;
        int sy = rising ? -1 : 1;
        int back = 0, fwd = 0;
        [loop] while (back < SMAA_MAX_SEARCH_DIAGONAL && SmaaCell(edges, p - int2(back + 1, sy * (back + 1)), size, rising)) back++;
        [loop] while (fwd < SMAA_MAX_SEARCH_DIAGONAL && SmaaCell(edges, p + int2(fwd + 1, sy * (fwd + 1)), size, rising)) fwd++;
        int n = back + fwd + 1;
        if (n < SMAA_MIN_DIAGONAL || n <= bestN) continue;
        int2 p0 = p - int2(back, sy * back), p1 = p + int2(fwd, sy * fwd);
        float ts, te;
        if (rising)
        {
            ts = SmaaEnd(SmaaEdge(edges, p0 + int2(0, 1), size).r, SmaaEdge(edges, p0 + int2(-1, 1), size).g);
            te = -SmaaEnd(SmaaEdge(edges, p1 + int2(1, -1), size).r, SmaaEdge(edges, p1 + int2(1, 0), size).g);
        }
        else
        {
            ts = -SmaaEnd(SmaaEdge(edges, p0 + int2(0, -1), size).r, SmaaEdge(edges, p0 + int2(-1, 0), size).g);
            float2 after = SmaaEdge(edges, p1 + int2(1, 1), size);
            te = SmaaEnd(after.r, after.g);
        }
        if (back == SMAA_MAX_SEARCH_DIAGONAL) ts = 0;
        if (fwd == SMAA_MAX_SEARCH_DIAGONAL) te = 0;
        if (ts == te) continue;
        float t = SmaaDiagonalOffset(ts, te, back + 0.5, n);
        area = float2((0.5 + t) * (0.5 + t) * 0.5, (0.5 - t) * (0.5 - t) * 0.5);
        best = kind;
        bestN = n;
    }
    return best;
}

float4 PsSmaaWeights(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> edges = ResourceDescriptorHeap[SMAA_EDGES];
    int2 size = SmaaSize(edges);
    int2 p = int2(pos.xy);
    float2 e = edges.Load(int3(p, 0)).rg;
    float4 w = 0;
    bool vertical = e.r > 0;
    if (e.g > 0)
    {
        float2 diagonal;
        uint kind = SmaaDiagonal(edges, p, size, diagonal);
        w.rg = kind != 0 ? diagonal : SmaaHorizontal(edges, p, size);
        // Диагональ «/»: левый край пикселя - её ступень, его доля уже в весах верхнего.
        if (kind == 1) vertical = false;
    }
    // Левый край пикселя - ступень диагонали «\» левого соседа: его доля уже в её весах.
    if (vertical && SmaaEdge(edges, p + int2(-1, 0), size).g > 0)
    {
        float2 unused;
        if (SmaaDiagonal(edges, p + int2(-1, 0), size, unused) == 2) vertical = false;
    }
    if (vertical) w.ba = SmaaVertical(edges, p, size);
    return w;
}

float SmaaDecode(float v)
{
    return v <= FILMIC_SRGB_LINEAR_SLOPE * FILMIC_SRGB_LINEAR_LIMIT ? v / FILMIC_SRGB_LINEAR_SLOPE
        : pow((v + FILMIC_SRGB_OFFSET) / FILMIC_SRGB_SCALE, FILMIC_SRGB_GAMMA);
}

float3 SmaaDecode3(float3 v)
{
    return float3(SmaaDecode(v.r), SmaaDecode(v.g), SmaaDecode(v.b));
}

float SmaaEncode(float v)
{
    return saturate(v <= FILMIC_SRGB_LINEAR_LIMIT ? FILMIC_SRGB_LINEAR_SLOPE * v : FILMIC_SRGB_SCALE * pow(v, 1 / FILMIC_SRGB_GAMMA) - FILMIC_SRGB_OFFSET);
}

float4 SmaaWeight(Texture2D<float4> weights, int2 p, int2 size)
{
    return all(p < size) ? weights.Load(int3(p, 0)) : 0;
}

// Пиксель берёт четыре веса (свои верхний и левый края, левый край правого соседа, верхний край нижнего) и смешивается с
// соседями по более сильному направлению. Пиксель без весов копируется как есть.
float4 PsSmaaBlend(float4 pos : SV_Position) : SV_Target0
{
    Texture2D<float4> color = ResourceDescriptorHeap[SMAA_COLOR];
    Texture2D<float4> weights = ResourceDescriptorHeap[SMAA_WEIGHTS];
    int2 size = SmaaSize(color);
    int2 p = int2(pos.xy);
    float4 own = weights.Load(int3(p, 0));
    float4 a = float4(SmaaWeight(weights, p + int2(1, 0), size).a, SmaaWeight(weights, p + int2(0, 1), size).g, own.b, own.r);
    float4 c = color.Load(int3(p, 0));
    if (a.x + a.y + a.z + a.w == 0) return c;
    bool horizontal = max(a.x, a.z) > max(a.y, a.w);
    float2 w = horizontal ? a.xz : a.yw;
    int2 o = horizontal ? int2(1, 0) : int2(0, 1);
    float3 lc = SmaaDecode3(c.rgb);
    float3 n1 = SmaaDecode3(color.Load(int3(clamp(p + o, 0, size - 1), 0)).rgb);
    float3 n2 = SmaaDecode3(color.Load(int3(clamp(p - o, 0, size - 1), 0)).rgb);
    float3 r = (w.x * (lc + (n1 - lc) * w.x) + w.y * (lc + (n2 - lc) * w.y)) / (w.x + w.y);
    return float4(SmaaEncode(r.r), SmaaEncode(r.g), SmaaEncode(r.b), 1);
}
