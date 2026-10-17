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
float4 PsPillarPre(LitVs i) : SV_Target0 { return SceneNormalOut(i.normal, true); }

// Колонны — статичный мир: принимают декали.
SceneOut PsPillar(LitVs i)
{
    float3 n = normalize(i.normal);
    return Scene(ShadeMaterial(i.colour, n, i.world, SceneAo(i.pos.xy), SCENE_MATERIAL_PILLAR), n, true);
}

// ── скинированные меши (персонажи) ─────────────────────────────────────────

struct SkinVs
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation float3 colour : COLOR0;
    nointerpolation float glow : TEXCOORD2;
    float4 layerAdd : TEXCOORD3;    // wet layer over the skin (blood): its own albedo A, coverage of the highlight
    float4 layerMul : TEXCOORD4;    // multiplier M of the skin under the layer, roughness
    nointerpolation uint material : TEXCOORD5;   // SceneMaterial: GpuSkinInstance.Flags
    float3 local : TEXCOORD6;                    // vertex in mesh space (weapon: hand space) - blood on the blade by position
    nointerpolation uint weaponLayer : TEXCOORD7;   // blood on the weapon blade: SRV + 1 (0 - none)
};

// Wet layer of a vertex (ISkinBloodLayer, SkinLayerTexel in C#): inst.owner = bindless SRV of the layer + 1 (0 - no layer); an element
// per mesh vertex: x = A in R11G11B10F (half bits without the sign), y = M unorm7 x 3 | coverage unorm5 << 21 | roughness unorm6 << 26.
// The layer over the skin: albedo = skin * M + A. No layer: A = 0, M = 1, coverage 0.
void SkinLayerOf(uint owner, uint vid, out float4 add, out float4 mul)
{
    add = 0;
    mul = float4(1, 1, 1, 1);
    if (owner == 0) return;
    StructuredBuffer<uint2> layer = ResourceDescriptorHeap[owner - 1];
    uint2 u = layer[vid];
    add = float4(f16tof32((u.x & 0x7FF) << 4), f16tof32(((u.x >> 11) & 0x7FF) << 4), f16tof32((u.x >> 22) << 5), ((u.y >> 21) & 0x1F) / 31.0);
    mul = float4((u.y & 0x7F) / 127.0, ((u.y >> 7) & 0x7F) / 127.0, ((u.y >> 14) & 0x7F) / 127.0, (u.y >> 26) / 63.0);
}

// Value noise of a point (smooth, 0...1) - stains on the blade without straight bands.
float WeaponNoiseHash(float2 c) { return frac(sin(dot(c, float2(127.1, 311.7))) * 43758.5453); }
float WeaponNoise(float2 p)
{
    float2 c = floor(p), f = frac(p);
    float2 s = f * f * (3 - 2 * f);
    float a = WeaponNoiseHash(c), b = WeaponNoiseHash(c + float2(1, 0)), d = WeaponNoiseHash(c + float2(0, 1)), e = WeaponNoiseHash(c + 1);
    return lerp(lerp(a, b, s.x), lerp(d, e, s.x), s.y);
}

void UnpackLayer(uint2 u, out float4 add, out float4 mul)
{
    add = float4(f16tof32((u.x & 0x7FF) << 4), f16tof32(((u.x >> 11) & 0x7FF) << 4), f16tof32((u.x >> 22) << 5), ((u.y >> 21) & 0x1F) / 31.0);
    mul = float4((u.y & 0x7F) / 127.0, ((u.y >> 7) & 0x7F) / 127.0, ((u.y >> 14) & 0x7F) / 127.0, (u.y >> 26) / 63.0);
}

// Blood on a weapon blade (IWeaponBloodLayer, WeaponBloodLayout in C#): owner = SRV + 1; header - the blade in mesh space (start, cells
// per side, length axis / length, width axis / half width, plane normal, noise seed), then cells along the blade, side 0 (along the
// normal) and side 1, each SkinLayerTexel. Between cells - linear; the position along the blade is warped by noise varying across the
// width (stains end ragged, not in bands across the blade), partial coverage (thin film) is dissolved by streak noise along the blade.
// Off the blade (guard, grip) - none.
void WeaponLayerOf(uint owner, float3 p, out float4 add, out float4 mul)
{
    add = 0;
    mul = float4(1, 1, 1, 1);
    StructuredBuffer<uint2> layer = ResourceDescriptorHeap[owner - 1];
    uint2 h0 = layer[0], h1 = layer[1], h2 = layer[2], h3 = layer[3], h4 = layer[4], h5 = layer[5], h6 = layer[6];
    uint cells = h1.y;
    float3 d = p - asfloat(uint3(h0.x, h0.y, h1.x));
    float u = dot(d, asfloat(uint3(h2.x, h2.y, h3.x)));
    float v = dot(d, asfloat(uint3(h3.y, h4.x, h4.y)));
    if (cells == 0 || u < 0 || u > 1) return;
    uint side = dot(d, asfloat(uint3(h5.x, h5.y, h6.x))) >= 0 ? 0 : 1;
    float2 seed = float2(h6.y & 0xFF, (h6.y >> 8) & 0xFF) * 0.37;
    float warp = WeaponNoise(float2(u * cells * 0.3, v * 3) + seed) - 0.5;
    float f = clamp(u * cells - 0.5 + warp * 4, 0, cells - 1);
    uint i0 = (uint)floor(f), i1 = min(i0 + 1, cells - 1);
    float t = f - i0;
    uint first = WEAPON_BLOOD_HEADER + side * cells;
    float4 a0, m0, a1, m1;
    UnpackLayer(layer[first + i0], a0, m0);
    UnpackLayer(layer[first + i1], a1, m1);
    add = lerp(a0, a1, t);
    mul = lerp(m0, m1, t);
    // Thin film (partial coverage) - streaks along the blade: long along it, narrow across.
    float edge = 0.7 * WeaponNoise(float2(u * cells * 0.2, v * 5) + seed + 11.3) + 0.3 * WeaponNoise(float2(u * cells * 1.5, v * 14) + seed + 5.1);
    float keep = saturate((add.a - 0.85 * edge) / 0.15) * saturate(u * cells * 2) * saturate((1 - u) * cells * 4);
    add *= keep;
    mul.rgb = lerp(1, mul.rgb, keep);
}

static const float SKIN_LAYER_F0 = 0.028;   // SkinLayerTexel.F0
// Second, wider sun lobe of a fresh wet layer (the film is uneven: the glint spreads around the mirror direction, as on the blood floor
// film): weight and added roughness; it fades out as the layer roughens to dry (roughness 0.15...0.45).
static const float SKIN_LAYER_GLINT = 0.4;
static const float SKIN_LAYER_GLINT_ROUGHNESS = 0.3;

SkinVs VsSkin(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    SkinInstance inst = SkinInstanceOf(iid);
    SkinVertex v = SkinVertexOf(vid);
    SkinVs o;
    SkinTransform(v, inst, o.world, o.normal);
    o.pos = mul(gFrame.viewProj, float4(o.world, 1));
    o.colour = inst.colour;
    o.glow = inst.glow;
    o.local = v.p;
    o.weaponLayer = (inst.flags & SCENE_MATERIAL_WEAPON_LAYER) != 0 ? inst.owner : 0;
    SkinLayerOf(o.weaponLayer != 0 ? 0 : inst.owner, vid, o.layerAdd, o.layerMul);
    o.material = inst.flags;
    return o;
}

float4 VsSkinPointShadow(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_Position
{
    SkinInstance inst = SkinInstanceOf(iid);
    SkinVertex v = SkinVertexOf(vid);
    float3 world, normal;
    SkinTransform(v, inst, world, normal);
    return PointShadowClip(world);
}

float4 VsSkinShadow(uint vid : SV_VertexID, uint iid : SV_InstanceID) : SV_Position
{
    SkinInstance inst = SkinInstanceOf(iid);
    SkinVertex v = SkinVertexOf(vid);
    float3 world, normal;
    SkinTransform(v, inst, world, normal);
    return mul(gFrame.shadowViewProj[SHADOW_CASCADE], float4(world, 1));
}

float4 PsSkinPre(SkinVs i) : SV_Target0 { return SceneNormalOut(i.normal, false); }

SceneOut PsSkin(SkinVs i)
{
    float3 n = normalize(i.normal);
    float4 layerAdd = i.layerAdd, layerMul = i.layerMul;
    if (i.weaponLayer != 0) WeaponLayerOf(i.weaponLayer, i.local, layerAdd, layerMul);
    float cover = saturate(layerAdd.a);
    if (cover <= 0)
    {
        float3 c = ShadeMaterial(i.colour, n, i.world, SceneAo(i.pos.xy), i.material) + i.glow * float3(0.5, 0.15, 0.1);
        return Scene(c, n, false);
    }
    // Blood is an absorbing layer over the skin: the skin shows through it darkened and reddened (M), the layer adds its own albedo (A);
    // its highlight - GGX sun + sky reflection by the reflected vector, x coverage, under the fog. On steel the metal mirror shows through
    // the turbid film dimmed twice over (x M^2: absorbed and scattered out of the mirror direction) and the blood scatters as a dielectric:
    // its albedo is lit diffusely (Shade without the rim and fog that do not depend on it).
    float ao = SceneAo(i.pos.xy);
    float3 c;
    if ((i.material & SCENE_MATERIAL_METAL) != 0)
        c = ShadeMaterial(i.colour * saturate(layerMul.rgb * layerMul.rgb), n, i.world, ao, i.material)
            + Shade(max(layerAdd.rgb, 0), n, i.world, ao) - Shade(0, n, i.world, ao);
    else
        c = ShadeMaterial(i.colour * saturate(layerMul.rgb) + max(layerAdd.rgb, 0), n, i.world, ao, i.material);
    c += i.glow * float3(0.5, 0.15, 0.1);
    float3 v = normalize(gFrame.cameraPos - i.world);
    float3 r = reflect(-v, n);
    float3 l = normalize(-gFrame.lightDir);
    float shadow = SunShadow(gFrame.shadowMap0, gFrame.shadowViewProj[0], gFrame.shadowTexel0, gFrame.shadowBias0,
        gFrame.shadowMap1, gFrame.shadowViewProj[1], gFrame.shadowTexel1, gFrame.shadowBias1, gFrame.shadowSoftness, i.world, n, l);
    float3 skyHalf = (gFrame.env.skyHigh - gFrame.env.skyLow) * 0.5;
    float3 sky = (gFrame.env.skyLow + skyHalf) + r.y * skyHalf;
    float3 spec = SurfaceSpecular(n, v, l, gFrame.env.sun * shadow, sky * ao, SKIN_LAYER_F0, layerMul.a);
    float glint = SKIN_LAYER_GLINT * (1 - smoothstep(0.15, 0.45, layerMul.a));
    if (glint > 0) spec += glint * SurfaceSpecular(n, v, l, gFrame.env.sun * shadow, 0, SKIN_LAYER_F0, layerMul.a + SKIN_LAYER_GLINT_ROUGHNESS);
    float fog = saturate((distance(i.world, gFrame.cameraPos) - 25) / 140);
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

float4 PsGroundPre(GroundVs i) : SV_Target0
{
    float3 n = normalize(cross(ddy(i.world), ddx(i.world)));
    return SceneNormalOut(n.y < 0 ? -n : n, true);
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
    float3 c = ShadeMaterial(albedo, n, i.world, SceneAo(i.pos.xy), SCENE_MATERIAL_GROUND);

    // кольцо выделения игрока
    c = lerp(c, lerp(float3(1, 0.2, 0.1), float3(0.3, 1.0, 0.45), gFrame.playerHpFrac), smoothstep(0.05, 0.0, abs(dp - 0.75)) * 0.8);
    return Scene(c, n, true);
}
