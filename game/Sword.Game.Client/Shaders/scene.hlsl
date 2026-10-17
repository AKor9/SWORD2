// Отрисовка арены: пол, рельеф из поля высот, колонны, скинированные меши. Перенос scene.hlsl SWORD 2eab13b:
// пол, подкраска участков, колонны и освещение — те же формулы; рельеф берёт высоты узлов из R_TERRAIN, а не считает
// функцию; скин — без разрезов, материалов и модулей поверхности (придут со своими фичами).
#include "scene_common.hlsli"
#include "lighting.hlsli"
#include "common/gbuffer.hlsli"
#include "common/skin.hlsli"
#include "common/surface.hlsli"

// Непрозрачная фаза пишет две цели: линейный цвет HDR-сцены (SceneColor; на экран — тонмап рендера) и нормаль с признаком
// приёмника декалей (SceneTargets).
struct SceneOut { float4 colour : SV_Target0; float4 normal : SV_Target1; };
SceneOut Scene(float3 colour, float3 n, bool receiver)
{
    SceneOut o;
    o.colour = float4(colour, 1);
    o.normal = SceneNormalOut(n, receiver);
    return o;
}

// ── фон: окружение кадра там, куда не легло ничего ─────────────────────────
// Треугольник на весь экран на дальней глубине (обратный Z: 0): тест GreaterEqual пропускает только пиксели, где глубина сцены
// осталась очищенной. Рисуется, когда фон окружения не равен цвету очистки сцены (ночь); нормаль - как очистка (вверх, не приёмник).

float4 VsBackground(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * 2 - 1, 0, 1);
}

SceneOut PsBackground()
{
    return Scene(gFrame.env.background, float3(0, 1, 0), false);
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

// Заслоняющие в карту теней солнца (проход scene.shadow, только глубина): те же вершины в осях света.
float4 VsPillarShadow(uint vid : SV_VertexID, uint inst : SV_InstanceID) : SV_Position
{
    return mul(gFrame.shadowViewProj[SHADOW_CASCADE], float4(VsPillar(vid, inst).world, 1));
}

void PsShadow() { }

// Заслоняющие в грань кубической карты точечного источника (PointLights.AddShadowPasses): та же точка мира в осях грани.
float4 VsPillarPointShadow(uint vid : SV_VertexID, uint inst : SV_InstanceID) : SV_Position
{
    return PointShadowClip(VsPillar(vid, inst).world);
}

// Предпроход глубины и нормалей (GTAO читает их до непрозрачного прохода): те же вершинные шейдеры, нормаль и признак приёмника -
// как у непрозрачного.
PrepassOut PsPillarPre(LitVs i)
{
    PrepassOut o;
    o.normal = SceneNormalOut(i.normal, true);
    o.velocity = SceneVelocity(i.world, i.world);
    return o;
}

// Колонны — статичный мир: принимают декали.
SceneOut PsPillar(LitVs i)
{
    float3 n = normalize(i.normal);
    float3 albedo = i.colour;
    float roughness = MaterialRoughness(SCENE_MATERIAL_PILLAR);
    MaterialWorld(SCENE_MATERIAL_PILLAR, i.world, albedo, n, roughness);
    return Scene(ShadeSurface(albedo, n, i.world, SceneAo(i.pos.xy), roughness, 0), n, true);
}

// ── скинированные меши (персонажи) ─────────────────────────────────────────

struct SkinVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation float3 colour : COLOR0;
    nointerpolation float glow : TEXCOORD2;
    float4 layer : TEXCOORD3;       // wet layer over the skin (blood): linear albedo, coverage
    float layerRoughness : TEXCOORD4;
    nointerpolation uint material : TEXCOORD5;   // SceneMaterial: GpuSkinInstance.Flags
    float3 bindPos : TEXCOORD6;                  // поза привязки (трипланар текстуры материала)
    float3 bindNormal : TEXCOORD7;
    float3 axisX : TEXCOORD8;                    // образы осей позы привязки в мире
    float3 axisY : TEXCOORD9;
    float3 axisZ : TEXCOORD10;
};

// Предпроход скина: глубина, нормаль и точка в прошлом кадре (палитра прошлого кадра) - скорости.
struct SkinPreVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float3 prevWorld : TEXCOORD2;
};

// Wet layer of a vertex (ISkinBloodLayer, SkinLayerTexel in C#): inst.owner = bindless SRV of the layer + 1 (0 - no layer); an element
// per mesh vertex: x = half r | half g << 16, y = half b | unorm8 coverage << 16 | unorm8 roughness << 24.
float4 SkinLayerOf(uint owner, uint vid, out float roughness)
{
    roughness = 1;
    if (owner == 0) return 0;
    StructuredBuffer<uint2> layer = ResourceDescriptorHeap[owner - 1];
    uint2 u = layer[vid];
    roughness = (u.y >> 24) / 255.0;
    return float4(f16tof32(u.x), f16tof32(u.x >> 16), f16tof32(u.y), ((u.y >> 16) & 0xFF) / 255.0);
}

static const float SKIN_LAYER_F0 = 0.03;   // SkinLayerTexel.F0

// Точка и нормаль - из буфера вершин кадра (SkinWorld); смешанная матрица - только ради осей позы привязки (трипланар).
SkinVs VsSkin(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    SkinInstance inst = SkinInstanceOf(iid);
    SkinVertex v = SkinVertexOf(vid);
    SkinVs o;
    SkinWorld(vid, inst, o.world, o.normal);
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    o.colour = inst.colour;
    o.glow = inst.glow;
    o.layer = SkinLayerOf(inst.owner, vid, o.layerRoughness);
    o.material = inst.flags;
    SkinMat m = SkinBlended(v, inst);
    o.bindPos = v.p;
    o.bindNormal = v.n;
    o.axisX = m.r0.xyz;
    o.axisY = m.r1.xyz;
    o.axisZ = m.r2.xyz;
    return o;
}

SkinPreVs VsSkinPre(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    SkinInstance inst = SkinInstanceOf(iid);
    SkinPreVs o;
    SkinWorld(vid, inst, o.world, o.normal);
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    o.prevWorld = SkinPreviousPoint(SkinVertexOf(vid), inst);
    return o;
}

float4 VsSkinPointShadow(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_Position
{
    float3 world, normal;
    SkinWorld(vid, SkinInstanceOf(iid), world, normal);
    return PointShadowClip(world);
}

float4 VsSkinShadow(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_Position
{
    float3 world, normal;
    SkinWorld(vid, SkinInstanceOf(iid), world, normal);
    return mul(gFrame.shadowViewProj[SHADOW_CASCADE], float4(world, 1));
}

PrepassOut PsSkinPre(SkinPreVs i)
{
    PrepassOut o;
    o.normal = SceneNormalOut(i.normal, false);
    o.velocity = SceneVelocity(i.world, i.prevWorld);
    return o;
}

SceneOut PsSkin(SkinVs i)
{
    float3 n = normalize(i.normal);
    float3 colour = i.colour;
    float roughness = MaterialRoughness(i.material);
    uint texture = MaterialTexture(i.material);
    if (texture != 0 && gFrame.materialAlbedo != SCENE_MATERIAL_NONE)
        MaterialTextured(texture - 1, i.bindPos, i.bindNormal, i.axisX, i.axisY, i.axisZ, colour, n, roughness);
    float metal = MaterialMetal(i.material);
    float cover = saturate(i.layer.a);
    if (cover <= 0)
    {
        float3 c = ShadeSurface(colour, n, i.world, SceneAo(i.pos.xy), roughness, metal) + i.glow * float3(0.5, 0.15, 0.1);
        return Scene(c, n, false);
    }
    // The layer albedo blends with the skin by the interpolated coverage; its highlight - GGX sun + the sky of reflections
    // (SkyReflection, as metal and the floor), under the fog.
    float3 albedo = lerp(colour, i.layer.rgb, cover);
    float3 c = ShadeSurface(albedo, n, i.world, SceneAo(i.pos.xy), roughness, metal) + i.glow * float3(0.5, 0.15, 0.1);
    float3 v = normalize(gFrame.cameraPos - i.world);
    float3 sky = SkyReflection(reflect(-v, n), i.layerRoughness);
    float3 spec = SurfaceSpecular(n, v, normalize(-gFrame.lightDir), gFrame.env.sun, sky, SKIN_LAYER_F0, i.layerRoughness);
    float fog = SceneFogAt(i.world);
    return Scene(c + cover * (1 - fog) * spec, n, false);
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

float4 VsTerrainPointShadow(uint vid : SV_VertexID) : SV_Position
{
    return PointShadowClip(VsTerrain(vid).world);
}

float4 VsTerrainShadow(uint vid : SV_VertexID) : SV_Position
{
    return mul(gFrame.shadowViewProj[SHADOW_CASCADE], float4(VsTerrain(vid).world, 1));
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

PrepassOut PsGroundPre(GroundVs i)
{
    float3 n = normalize(cross(ddy(i.world), ddx(i.world)));
    PrepassOut o;
    o.normal = SceneNormalOut(n.y < 0 ? -n : n, true);
    o.velocity = SceneVelocity(i.world, i.world);
    return o;
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

    // Пятно под игроком — пока нет настоящей тени солнца.
    float dp = distance(p, gFrame.playerPos.xz);
    if (gFrame.shadowMap0 == SHADOW_NONE) albedo *= lerp(0.45, 1.0, smoothstep(0.2, 0.9, dp));

    albedo = TerrainTint(p, albedo);

    // Нормаль по производным мировой позиции: грани ступеней остаются чёткими.
    float3 n = normalize(cross(ddy(i.world), ddx(i.world)));
    if (n.y < 0) n = -n;
    // Плиты пола (их швы - на сетке 2 м) - внутри арены; песок за бортиком - без них.
    float roughness = MaterialRoughness(SCENE_MATERIAL_GROUND);
    float3 plain = albedo, plainN = n;
    float plainRough = roughness;
    MaterialWorld(SCENE_MATERIAL_GROUND, i.world, albedo, n, roughness);
    float stoneMask = 1 - smoothstep(gFrame.arenaRadius, gFrame.arenaRadius + 1.5, r);
    albedo = lerp(plain, albedo, stoneMask);
    n = normalize(lerp(plainN, n, stoneMask));
    roughness = lerp(plainRough, roughness, stoneMask);
    float3 c = ShadeSurface(albedo, n, i.world, SceneAo(i.pos.xy), roughness, 0);

    // кольцо выделения игрока
    c = lerp(c, lerp(float3(1, 0.2, 0.1), float3(0.3, 1.0, 0.45), gFrame.playerHpFrac), smoothstep(0.05, 0.0, abs(dp - 0.75)) * 0.8);
    return Scene(c, n, true);
}
