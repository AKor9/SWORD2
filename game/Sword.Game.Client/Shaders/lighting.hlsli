// Освещение сцены: общее для непрозрачного (scene.hlsl) и того, что рисуется поверх (кровь).
#ifndef SWORD_LIGHTING_HLSLI
#define SWORD_LIGHTING_HLSLI
#include "scene_common.hlsli"
#include "common/lights.hlsli"
#include "common/shadow.hlsli"
#include "common/surface.hlsli"

// Shade даёт линейный цвет сцены (HDR): на экран его переводит тонмап рендера, кодирования здесь нет. Цвета солнца, неба, контура и
// тумана - окружение кадра (gFrame.env: день или ночь), формулы от окружения не зависят.

float3 Shade(float3 albedo, float3 n, float3 world, float ao)
{
    float3 l = normalize(-gFrame.lightDir);
    // Тень солнца гасит только солнце: небо, контур и точечные источники - не её.
    float ndl = saturate(dot(n, l)) * SunShadow(gFrame.shadowMap0, gFrame.shadowViewProj[0], gFrame.shadowTexel0, gFrame.shadowBias0,
        gFrame.shadowMap1, gFrame.shadowViewProj[1], gFrame.shadowTexel1, gFrame.shadowBias1, gFrame.shadowSoftness, world, n, l);
    // lerp(низ, зенит, n.y * 0.5 + 0.5), записанный как середина + n.y x половина: так его сворачивал компилятор, пока цвета неба
    // были константами шейдера, - дневной кадр бит в бит прежний (lerp с цветами из констант кадра расходится в 1 ULP half).
    float3 skyHalf = (gFrame.env.skyHigh - gFrame.env.skyLow) * 0.5;
    float3 sky = (gFrame.env.skyLow + skyHalf) + n.y * skyHalf;
    float3 v = normalize(gFrame.cameraPos - world);
    float rim = pow(1 - saturate(dot(n, v)), 3) * 0.25;
    float3 c = albedo * (ndl * gFrame.env.sun + sky * ao) + rim * gFrame.env.rim * ao;
    // Туман по расстоянию: начало и длина - SceneEnvironment.FogStart/FogRange (по ним же туман прозрачного модулей).
    float fog = saturate((distance(world, gFrame.cameraPos) - 25) / 140);
    // Точечный свет (факел и т. п.) - Ламберт UE: альбедо/pi x сумма источников (солнце выше - без 1/pi: его 1.25 = UE-свет pi x 1.25),
    // под тем же туманом: lerp(c + p, туман, f) = lerp(c, туман, f) + (1 - f) p. Без источников p = 0 - прежний цвет бит в бит.
    // ao гасит только рассеянный свет (небо, контур); источники - прямой свет, как солнце, и на ao не умножаются (так и в UE:
    // затенение прямого света - дело теней, а не ao).
    return lerp(c, gFrame.env.fog, fog) + (1 - fog) * albedo * PointLightsDiffuse(gFrame.pointLights, n, world, gFrame.cameraPos);
}

// Материал поверхности (SceneMaterial.cs): шероховатость GGX 0..1 (младшие 8 бит), металл (бит 8).
float MaterialRoughness(uint material) { return (material & 0xFF) / 255.0; }
float MaterialMetal(uint material) { return (material & SCENE_MATERIAL_METAL) != 0 ? 1 : 0; }

// Номер текстуры материала: 0 - нет, иначе слой + 1.
uint MaterialTexture(uint material) { return (material >> SCENE_MATERIAL_TEXTURE_SHIFT) & 3; }

// Поверхность с текстурой материала (SceneMaterial): трипланар в позе привязки (точка obj, нормаль objN), образы осей позы в мире -
// axisX/Y/Z. Цвет - оттенок вызывающего x рисунок слоя (отношение к среднему цвету слоя - его последний мип), шероховатость -
// так же, нормаль - смесь трёх проекций по Голусу (whiteout с учётом знака оси), сила - SCENE_MATERIAL_NORMAL_STRENGTH.
void MaterialTextured(uint layer, float3 obj, float3 objN, float3 axisX, float3 axisY, float3 axisZ, inout float3 albedo, inout float3 n,
    inout float roughness)
{
    Texture2DArray<float4> colours = ResourceDescriptorHeap[gFrame.materialAlbedo];
    Texture2DArray<float4> normals = ResourceDescriptorHeap[gFrame.materialNormals];
    Texture2DArray<float4> roughs = ResourceDescriptorHeap[gFrame.materialRoughness];
    uint w, h, layers, mips;
    colours.GetDimensions(0, w, h, layers, mips);
    float tiling = layer == 0 ? SCENE_MATERIAL_FABRIC_TILING : SCENE_MATERIAL_STEEL_TILING;
    float3 on = normalize(objN);
    float3 blend = pow(abs(on), 4);
    blend /= blend.x + blend.y + blend.z;
    float3 s = sign(on);
    float2 uvX = obj.zy * tiling * float2(s.x, 1), uvY = obj.xz * tiling * float2(s.y, 1), uvZ = obj.xy * tiling * float2(-s.z, 1);
    float3 mean = colours.SampleLevel(gLinearWrap, float3(0.5, 0.5, layer), mips - 1).rgb;
    float meanRough = roughs.SampleLevel(gLinearWrap, float3(0.5, 0.5, layer), mips - 1).r;
    float3 c = colours.Sample(gLinearWrap, float3(uvX, layer)).rgb * blend.x + colours.Sample(gLinearWrap, float3(uvY, layer)).rgb * blend.y
        + colours.Sample(gLinearWrap, float3(uvZ, layer)).rgb * blend.z;
    float r = roughs.Sample(gLinearWrap, float3(uvX, layer)).r * blend.x + roughs.Sample(gLinearWrap, float3(uvY, layer)).r * blend.y
        + roughs.Sample(gLinearWrap, float3(uvZ, layer)).r * blend.z;
    albedo *= dot(c, 1) / max(dot(mean, 1), 1e-3);
    roughness = saturate(roughness * r / max(meanRough, 1e-3));
    // Нормали проекций (xy - касательные, OpenGL), сила, знак оси; whiteout: xy + нормаль поверхности в плоскости проекции.
    float2 tx = (normals.Sample(gLinearWrap, float3(uvX, layer)).xy * 2 - 1) * SCENE_MATERIAL_NORMAL_STRENGTH * float2(s.x, 1);
    float2 ty = (normals.Sample(gLinearWrap, float3(uvY, layer)).xy * 2 - 1) * SCENE_MATERIAL_NORMAL_STRENGTH * float2(s.y, 1);
    float2 tz = (normals.Sample(gLinearWrap, float3(uvZ, layer)).xy * 2 - 1) * SCENE_MATERIAL_NORMAL_STRENGTH * float2(-s.z, 1);
    float3 nx = float3(tx + on.zy, sqrt(saturate(1 - dot(tx, tx))) * on.x);
    float3 ny = float3(ty + on.xz, sqrt(saturate(1 - dot(ty, ty))) * on.y);
    float3 nz = float3(tz + on.xy, sqrt(saturate(1 - dot(tz, tz))) * on.z);
    float3 objPerturbed = normalize(nx.zyx * blend.x + ny.xzy * blend.y + nz.xyz * blend.z);
    float3 world = objPerturbed.x * axisX + objPerturbed.y * axisY + objPerturbed.z * axisZ;
    // Нормаль скина и образ возмущённой нормали позы - в одну сторону; на вырожденных осях - без возмущения.
    float len = length(world);
    if (len > 1e-4) n = normalize(world / len);
}

// Shade с материалом: рассеянный свет - доля (1 - металл) альбедо; блик - GGX солнца (под тенью) и отражение неба (под ao), f0 -
// 0.04 у диэлектрика, альбедо у металла (surface.hlsli). Выключенные блики (gFrame.specular = 0) - ровно Shade.
float3 ShadeSurface(float3 albedo, float3 n, float3 world, float ao, float roughness, float metal);

float3 ShadeMaterial(float3 albedo, float3 n, float3 world, float ao, uint material)
{
    return ShadeSurface(albedo, n, world, ao, MaterialRoughness(material), MaterialMetal(material));
}

float3 ShadeSurface(float3 albedo, float3 n, float3 world, float ao, float roughness, float metal)
{
    if (gFrame.specular == 0) return Shade(albedo, n, world, ao);
    float3 l = normalize(-gFrame.lightDir);
    float shadow = SunShadow(gFrame.shadowMap0, gFrame.shadowViewProj[0], gFrame.shadowTexel0, gFrame.shadowBias0,
        gFrame.shadowMap1, gFrame.shadowViewProj[1], gFrame.shadowTexel1, gFrame.shadowBias1, gFrame.shadowSoftness, world, n, l);
    float ndl = saturate(dot(n, l)) * shadow;
    float3 skyHalf = (gFrame.env.skyHigh - gFrame.env.skyLow) * 0.5;
    float3 sky = (gFrame.env.skyLow + skyHalf) + n.y * skyHalf;
    float3 v = normalize(gFrame.cameraPos - world);
    float rim = pow(1 - saturate(dot(n, v)), 3) * 0.25;
    float3 diffuse = albedo * (1 - metal);
    float3 c = diffuse * (ndl * gFrame.env.sun + sky * ao) + rim * gFrame.env.rim * ao * (1 - metal);
    float3 r = reflect(-v, n);
    float3 skyReflected = (gFrame.env.skyLow + skyHalf) + r.y * skyHalf;
    c += SurfaceSpecular3(n, v, l, gFrame.env.sun * shadow, skyReflected * ao, lerp(0.04, albedo, metal), roughness);
    float fog = saturate((distance(world, gFrame.cameraPos) - 25) / 140);
    return lerp(c, gFrame.env.fog, fog) + (1 - fog) * diffuse * PointLightsDiffuse(gFrame.pointLights, n, world, gFrame.cameraPos);
}

// Видимость рассеянного света пикселя (GTAO, карта кадра; нет карты - 1): множитель ao у Shade непрозрачной сцены.
float SceneAo(float2 px)
{
    if (gFrame.aoMap == AO_NONE) return 1;
    Texture2D<float2> ao = ResourceDescriptorHeap[gFrame.aoMap];
    return ao.Load(int3(px, 0)).r;
}

/// Резкий блик мокрой поверхности (кожа в крови, срез, кишки): Блинн по направлениям на свет и на камеру.
/// <paramref name="power"/> — резкость, силу задаёт вызывающий.
float WetHighlight(float3 n, float3 world, float power)
{
    float3 h = normalize(normalize(gFrame.cameraPos - world) + normalize(-gFrame.lightDir));
    return pow(saturate(dot(n, h)), power);
}

#endif
