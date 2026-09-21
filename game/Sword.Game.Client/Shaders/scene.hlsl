// Отрисовка арены (точка 1б): пол, рельеф из поля высот, колонны, скинированные меши. Перенос scene.hlsl SWORD 2eab13b:
// пол, подкраска участков, колонны и освещение — те же формулы; рельеф берёт высоты узлов из R_TERRAIN, а не считает
// функцию; скин — без разрезов, материалов и модулей поверхности (придут со своими фичами).
#include "scene_common.hlsli"
#include "lighting.hlsli"
#include "common/gbuffer.hlsli"
#include "common/skin.hlsli"

// Непрозрачная фаза пишет две цели: цвет и нормаль с признаком приёмника декалей (SceneTargets).
struct SceneOut { float4 colour : SV_Target0; float4 normal : SV_Target1; };
SceneOut Scene(float3 colour, float3 n, bool receiver)
{
    SceneOut o;
    o.colour = float4(colour, 1);
    o.normal = SceneNormalOut(n, receiver);
    return o;
}

// ── колонны: единичная капсула, растянутая по радиусу и высоте ─────────────

struct MeshVertex { float3 n; float cap; };
_Static_assert(sizeof(MeshVertex) == CAPSULE_VERTEX_SIZE, "MeshVertex layout differs from C# CapsuleVertex (Scene/CapsuleMesh.cs)");

struct LitVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation float3 colour : COLOR0;
};

LitVs VsPillar(uint vid : SV_VertexID, uint inst : SV_InstanceID)
{
    StructuredBuffer<MeshVertex> mesh = ResourceDescriptorHeap[R_MESH];
    float4 p = gFrame.pillars[inst];
    float radius = p.z;
    float halfLen = max(p.w * 0.5 - p.z, 0.01);
    MeshVertex v = mesh[vid];
    float3 local = v.n * radius + float3(0, v.cap * halfLen, 0);

    LitVs o;
    o.world = float3(p.x, p.w * 0.5, p.y) + local;
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    o.normal = v.n;
    o.colour = float3(0.42, 0.38, 0.34);
    return o;
}

// Колонны — статичный мир: принимают декали.
SceneOut PsPillar(LitVs i)
{
    float3 n = normalize(i.normal);
    return Scene(Gamma(Shade(i.colour, n, i.world, 1)), n, true);
}

// ── скинированные меши (персонажи) ─────────────────────────────────────────

struct SkinVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation float3 colour : COLOR0;
    nointerpolation float glow : TEXCOORD2;
};

SkinVs VsSkin(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    SkinInstance inst = SkinInstanceOf(iid);
    SkinVertex v = SkinVertexOf(vid);
    SkinVs o;
    SkinTransform(v, inst, o.world, o.normal);
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    o.colour = inst.colour;
    o.glow = inst.glow;
    return o;
}

SceneOut PsSkin(SkinVs i)
{
    float3 n = normalize(i.normal);
    float3 c = Shade(i.colour, n, i.world, 1) + i.glow * float3(0.5, 0.15, 0.1);
    return Scene(Gamma(c), n, false);
}

// ── пол ───────────────────────────────────────────────────────────────────

struct GroundVs { float4 pos : SV_Position; float3 world : TEXCOORD0; };

GroundVs VsGround(uint vid : SV_VertexID)
{
    const float2 k[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };
    GroundVs o;
    // Чуть ниже нуля: там, где сетка рельефа ровная, она ложится поверх без z-борьбы.
    o.world = float3(k[vid].x * 160, -0.01, k[vid].y * 160);
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    return o;
}

// Сетка рельефа без вершинного буфера: ячейка и угол — из SV_VertexID, высота — узел поля высот (HeightField).
GroundVs VsTerrain(uint vid : SV_VertexID)
{
    const uint2 k[6] = { uint2(0, 0), uint2(1, 0), uint2(1, 1), uint2(0, 0), uint2(1, 1), uint2(0, 1) };
    StructuredBuffer<float> heights = ResourceDescriptorHeap[R_TERRAIN];
    uint cell = vid / 6u;
    uint2 node = uint2(cell % TERRAIN_CELLS, cell / TERRAIN_CELLS) + k[vid % 6u];
    float2 xz = -TERRAIN_HALF + float2(node) * (2.0 * TERRAIN_HALF / TERRAIN_CELLS);
    GroundVs o;
    o.world = float3(xz.x, heights[node.y * (TERRAIN_CELLS + 1) + node.x], xz.y);
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    return o;
}

/// Подкраска тестовых участков, чтобы их было видно издалека.
float3 TerrainTint(float2 p, float3 albedo)
{
    float hills = TSmooth(9.0, 6.0, length(p - float2(16, 0)));
    albedo = lerp(albedo, float3(0.24, 0.30, 0.16) * (0.8 + 0.4 * ValueNoise(p * 1.7)), hills * 0.85);
    float c = max(abs(p.x + 16.0), abs(p.y));
    float level = clamp(floor(7.0 - c), 0.0, 5.0);
    albedo = lerp(albedo, float3(0.40, 0.38, 0.35) * (0.8 + 0.12 * level), c < 7.0 ? 0.9 : 0.0);
    float slope = TSmooth(9.0, 1.0, length(p - float2(0, 18)));
    albedo = lerp(albedo, float3(0.44, 0.34, 0.2), saturate(slope * 1.6) * 0.8);
    float bumps = TSmooth(8.0, 6.0, length(p - float2(0, -18)));
    albedo = lerp(albedo, float3(0.26, 0.22, 0.2) * (0.7 + 0.6 * ValueNoise(p * 4.0)), bumps * 0.85);
    return albedo;
}

SceneOut PsGround(GroundVs i)
{
    float2 p = i.world.xz;
    float r = length(p);

    float2 tile = floor(p / 2.0);
    float2 f = frac(p / 2.0);
    float edge = min(min(f.x, 1 - f.x), min(f.y, 1 - f.y));
    float tint = Rand(Hash(uint(tile.x + 500) * 7919u + uint(tile.y + 500) * 104729u));
    float grain = ValueNoise(p * 3.1) * 0.5 + ValueNoise(p * 0.7) * 0.5;
    float3 stone = lerp(float3(0.30, 0.27, 0.23), float3(0.40, 0.35, 0.29), tint) * (0.8 + 0.35 * grain);
    stone *= lerp(0.55, 1.0, smoothstep(0.0, 0.04, edge));

    float3 sand = float3(0.22, 0.17, 0.12) * (0.7 + 0.5 * grain);
    float3 albedo = lerp(stone, sand, smoothstep(gFrame.arenaRadius, gFrame.arenaRadius + 1.5, r));

    // бортик и круги в центре
    albedo = lerp(albedo, float3(0.18, 0.15, 0.13), smoothstep(0.6, 0.0, abs(r - gFrame.arenaRadius)));
    albedo = lerp(albedo, float3(0.45, 0.28, 0.12), smoothstep(0.12, 0.0, abs(r - 8.0)) * 0.6);
    albedo = lerp(albedo, float3(0.45, 0.28, 0.12), smoothstep(0.08, 0.0, abs(r - 20.0)) * 0.4);

    // тень под игроком
    float dp = distance(p, gFrame.playerPos.xz);
    albedo *= lerp(0.45, 1.0, smoothstep(0.2, 0.9, dp));

    albedo = TerrainTint(p, albedo);

    // Нормаль по производным мировой позиции: грани ступеней остаются чёткими.
    float3 n = normalize(cross(ddy(i.world), ddx(i.world)));
    if (n.y < 0) n = -n;
    float3 c = Shade(albedo, n, i.world, 1);

    // кольцо выделения игрока
    c = lerp(c, lerp(float3(1, 0.2, 0.1), float3(0.3, 1.0, 0.45), gFrame.playerHpFrac), smoothstep(0.05, 0.0, abs(dp - 0.75)) * 0.8);
    return Scene(Gamma(c), n, true);
}
