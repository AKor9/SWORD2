// Интерфейс (UiPass): аналитические фигуры (поле расстояний) и GPU-векторный текст. Перенос ui.hlsl SWORD 2eab13b
// (туда — из Star2). Одна запись на квад, шесть вершин по SV_VertexID. Слоты UI_*, режимы UI_BOX/UI_TRI/UI_SPRITE,
// UI_VECTOR_BANDS и размеры записей — из контракта шейдеров (UiPass.Contribute).
#include "pass.hlsli"

struct UiQuad
{
    float4 shape;
    float2 centre;
    float2 halfSize;
    float2 axis;
    float2 uv0;
    float2 uv1;
    uint   colourA;
    uint   colourB;
    uint   mode;
};
_Static_assert(sizeof(UiQuad) == UI_QUAD_SIZE, "UiQuad layout differs from C# UiQuad (Sword.Engine.Ui)");

struct PsInput
{
    float4 pos    : SV_Position;
    float4 colour : COLOR0;
    float2 local  : TEXCOORD0;
    nointerpolation float4 uvRect   : TEXCOORD1;
    nointerpolation float4 shape    : TEXCOORD2;
    nointerpolation float2 halfSize : TEXCOORD3;
    nointerpolation uint   mode     : TEXCOORD4;
};

float4 Unpack(uint c)
{
    return float4(c & 0xFFu, (c >> 8) & 0xFFu, (c >> 16) & 0xFFu, (c >> 24) & 0xFFu) * (1.0 / 255.0);
}

PsInput VsUi(uint vid : SV_VertexID)
{
    StructuredBuffer<UiQuad> quads = ResourceDescriptorHeap[UI_QUADS];
    UiQuad q = quads[UI_BASE + vid / 6u];

    const float2 kCorners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(1, 1), float2(0, 1) };
    float2 t = kCorners[vid % 6u];

    float2 local = (t * 2.0 - 1.0) * q.halfSize;
    float2 turned = float2(local.x * q.axis.x - local.y * q.axis.y, local.x * q.axis.y + local.y * q.axis.x);
    float2 screen = q.centre + turned;

    PsInput o;
    o.pos = float4(screen.x / UI_WIDTH * 2.0 - 1.0, 1.0 - screen.y / UI_HEIGHT * 2.0, 0.0, 1.0);
    float mix = (q.mode == UI_BOX) ? saturate(dot(t, q.shape.zw)) : 0.0;
    o.colour = lerp(Unpack(q.colourA), Unpack(q.colourB), mix);
    o.local = local;
    o.uvRect = float4(q.uv0, q.uv1);
    o.shape = q.shape;
    o.halfSize = q.halfSize;
    o.mode = q.mode;
    return o;
}

float SdBox(float2 p, float2 b, float r)
{
    float2 d = abs(p) - b + r;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
}

// Треугольник: вершины в системе квада — uv0, uv1 и shape.zw (см. UiDrawList.Triangle). Знаковое
// расстояние до треугольника, отрицательное внутри (Иниго Килес, «2D distance functions»).
float SdTriangle(float2 p, float2 p0, float2 p1, float2 p2)
{
    float2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
    float2 v0 = p - p0, v1 = p - p1, v2 = p - p2;
    float2 pq0 = v0 - e0 * saturate(dot(v0, e0) / dot(e0, e0));
    float2 pq1 = v1 - e1 * saturate(dot(v1, e1) / dot(e1, e1));
    float2 pq2 = v2 - e2 * saturate(dot(v2, e2) / dot(e2, e2));
    float s = sign(e0.x * e2.y - e0.y * e2.x);
    float2 d = min(min(float2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                       float2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                       float2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}

// Картинка: shape = (bindless-индекс, мип, 1 — тексели квадратами, слой массива); uv0..uv1 — участок текстуры.
float4 Sprite(PsInput input)
{
    Texture2DArray<float4> tex = ResourceDescriptorHeap[NonUniformResourceIndex((uint)(input.shape.x + 0.5))];
    float2 t = input.local / max(input.halfSize, 1e-5) * 0.5 + 0.5;
    float2 uv = lerp(input.uvRect.xy, input.uvRect.zw, t);
    float mip = input.shape.y;
    if (input.shape.z > 0.5)
    {
        uint w, h, slices, levels;
        tex.GetDimensions((uint)(mip + 0.5), w, h, slices, levels);
        float2 size = float2(w, h);
        uv = (floor(uv * size) + 0.5) / size;
    }
    return tex.SampleLevel(gLinearClamp, float3(uv, input.shape.w), mip) * input.colour;
}

float4 PsUi(PsInput input) : SV_Target
{
    if (input.mode == UI_SPRITE) return Sprite(input);
    float d = input.mode == UI_TRI
        ? SdTriangle(input.local, input.uvRect.xy, input.uvRect.zw, input.shape.zw)
        : SdBox(input.local, input.uvRect.xy, input.shape.x);
    float stroke = input.shape.y;
    if (stroke > 0.0) d = abs(d) - stroke * 0.5;
    float w = max(fwidth(d), 1e-5);
    float4 colour = input.colour;
    colour.a *= saturate(0.5 - d / w);
    return colour;
}

// ── GPU-векторный текст. Математика корней и объединения двух лучей основана
// на Slug Algorithm, Copyright 2017 Eric Lengyel, MIT/Apache-2.0.

struct VectorGlyph { float4 bounds; uint horizontalHeader; uint verticalHeader; float2 bandScale; float2 bandOffset; };
struct VectorCurve { float4 p12; float4 p3Pad; };
struct VectorBand  { uint count; uint offset; };
_Static_assert(sizeof(VectorGlyph) == UI_GLYPH_SIZE, "VectorGlyph layout differs from C# VectorGlyph (Sword.Engine.Formats)");
_Static_assert(sizeof(VectorCurve) == UI_CURVE_SIZE, "VectorCurve layout differs from C# VectorCurve (Sword.Engine.Formats)");
_Static_assert(sizeof(VectorBand) == UI_BAND_SIZE, "VectorBand layout differs from C# VectorBand (Sword.Engine.Formats)");

uint VectorRootCode(float y1, float y2, float y3)
{
    uint i1 = asuint(y1) >> 31u;
    uint i2 = asuint(y2) >> 30u;
    uint i3 = asuint(y3) >> 29u;
    uint shift = (i2 & 2u) | (i1 & ~2u);
    shift = (i3 & 4u) | (shift & ~4u);
    return (0x2E74u >> shift) & 0x0101u;
}

float2 VectorSolveHorizontal(float4 p12, float2 p3)
{
    float2 a = p12.xy - p12.zw * 2.0 + p3;
    float2 b = p12.xy - p12.zw;
    float d = sqrt(max(b.y * b.y - a.y * p12.y, 0.0));
    float2 t = float2(b.y - d, b.y + d) / a.y;
    if (abs(a.y) < 1.0 / 65536.0) t = p12.y / (2.0 * b.y);
    return (a.x * t - b.x * 2.0) * t + p12.x;
}

float2 VectorSolveVertical(float4 p12, float2 p3)
{
    float2 a = p12.xy - p12.zw * 2.0 + p3;
    float2 b = p12.xy - p12.zw;
    float d = sqrt(max(b.x * b.x - a.x * p12.x, 0.0));
    float2 t = float2(b.x - d, b.x + d) / a.x;
    if (abs(a.x) < 1.0 / 65536.0) t = p12.x / (2.0 * b.x);
    return (a.y * t - b.y * 2.0) * t + p12.y;
}

float VectorCoverage(float2 renderCoord, uint glyphIndex)
{
    StructuredBuffer<VectorGlyph> glyphs = ResourceDescriptorHeap[UI_GLYPHS];
    StructuredBuffer<VectorCurve> curves = ResourceDescriptorHeap[UI_CURVES];
    StructuredBuffer<VectorBand> bands = ResourceDescriptorHeap[UI_BANDS];
    StructuredBuffer<uint> indices = ResourceDescriptorHeap[UI_INDICES];
    VectorGlyph glyph = glyphs[glyphIndex];

    float2 emsPerPixel = max(fwidth(renderCoord), 1e-7);
    float2 pixelsPerEm = 1.0 / emsPerPixel;
    int2 bandIndex = clamp(int2(renderCoord * glyph.bandScale + glyph.bandOffset),
                           int2(0, 0), int2(UI_VECTOR_BANDS - 1, UI_VECTOR_BANDS - 1));

    float xcov = 0.0, xwgt = 0.0;
    VectorBand hband = bands[glyph.horizontalHeader + bandIndex.y];
    for (uint i = 0; i < hband.count; i++)
    {
        VectorCurve curve = curves[indices[hband.offset + i]];
        float4 p12 = curve.p12 - float4(renderCoord, renderCoord);
        float2 p3 = curve.p3Pad.xy - renderCoord;
        if (max(max(p12.x, p12.z), p3.x) * pixelsPerEm.x < -0.5) break;
        uint code = VectorRootCode(p12.y, p12.w, p3.y);
        if (code != 0u)
        {
            float2 roots = VectorSolveHorizontal(p12, p3) * pixelsPerEm.x;
            if ((code & 1u) != 0u) { xcov += saturate(roots.x + 0.5); xwgt = max(xwgt, saturate(1.0 - abs(roots.x) * 2.0)); }
            if (code > 1u)         { xcov -= saturate(roots.y + 0.5); xwgt = max(xwgt, saturate(1.0 - abs(roots.y) * 2.0)); }
        }
    }

    float ycov = 0.0, ywgt = 0.0;
    VectorBand vband = bands[glyph.verticalHeader + bandIndex.x];
    for (uint j = 0; j < vband.count; j++)
    {
        VectorCurve curve = curves[indices[vband.offset + j]];
        float4 p12 = curve.p12 - float4(renderCoord, renderCoord);
        float2 p3 = curve.p3Pad.xy - renderCoord;
        if (max(max(p12.y, p12.w), p3.y) * pixelsPerEm.y < -0.5) break;
        uint code = VectorRootCode(p12.x, p12.z, p3.x);
        if (code != 0u)
        {
            float2 roots = VectorSolveVertical(p12, p3) * pixelsPerEm.y;
            if ((code & 1u) != 0u) { ycov -= saturate(roots.x + 0.5); ywgt = max(ywgt, saturate(1.0 - abs(roots.x) * 2.0)); }
            if (code > 1u)         { ycov += saturate(roots.y + 0.5); ywgt = max(ywgt, saturate(1.0 - abs(roots.y) * 2.0)); }
        }
    }

    float combined = abs(xcov * xwgt + ycov * ywgt) / max(xwgt + ywgt, 1.0 / 65536.0);
    return saturate(max(combined, min(abs(xcov), abs(ycov))));
}

float4 PsVectorText(PsInput input) : SV_Target
{
    float2 t = input.local / max(input.halfSize, 1e-5) * 0.5 + 0.5;
    float2 renderCoord = lerp(input.uvRect.xy, input.uvRect.zw, t);
    float alpha = VectorCoverage(renderCoord, (uint)(input.shape.x + 0.5));
    alpha = pow(alpha, 1.0 / max(asfloat(UI_GAMMA), 0.1));
    float4 colour = input.colour;
    colour.a *= alpha;
    return colour;
}
