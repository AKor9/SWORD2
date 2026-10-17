using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Game.Client.Scene;

/// <summary>
/// Константы кадра сцены (<c>gFrame : register(b0)</c> в scene_common.hlsli; размер сверяет шейдер — FRAME_CONSTANTS_SIZE).
/// Поля — строками по 16 байт; часть полей придёт в дело вместе со своими фичами.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FrameConstants
{
    public float Dt; public float Time; public uint Tick; public uint ActiveUnits;
    public Vector3 PlayerPos; public float Alpha;
    public Matrix4x4 ViewProj;
    public Vector3 CameraPos; public float ArenaRadius;
    public Vector4 Pillar0, Pillar1, Pillar2, Pillar3, Pillar4, Pillar5, Pillar6, Pillar7;
    public Vector3 LightDir; public float PlayerHpFrac;
    /// <summary>Обратная к <see cref="ViewProj"/>: точка мира по глубине сцены (декали).</summary>
    public Matrix4x4 InvViewProj;
    /// <summary>SRV точечных источников кадра (<c>RenderFrame.Lights.Srv</c>, <c>common/lights.hlsli</c>).</summary>
    public uint PointLights; public uint Pad0; public uint Pad1; public uint Pad2;
    /// <summary>Окружение сцены — цвета освещения и фон (<see cref="SceneEnvironment"/>: день или ночь).</summary>
    public SceneEnvironment Environment;
    /// <summary>Тени от солнца (<c>RenderFrame.Shadow</c>), каскады 0 (ближний) и 1 (дальний): матрицы мир → карта, SRV карт
    /// (<c>SunShadow.None</c> — нет), стороны текселей, м, сдвиги глубины к солнцу (единицы карты), мягкость края, тексели.</summary>
    public Matrix4x4 ShadowViewProj0, ShadowViewProj1;
    public uint ShadowMap0; public uint ShadowMap1; public float ShadowTexel0; public float ShadowTexel1;
    public float ShadowBias0; public float ShadowBias1; public float ShadowSoftness; public float ShadowPad;
    /// <summary>Видимость рассеянного света по GTAO (<c>RenderFrame.Ao</c>): SRV карты (<c>AmbientOcclusion.None</c> — нет); блики и металл
    /// материалов (<see cref="SceneMaterial"/>): 1 — есть, 0 — только рассеянный свет, как без материалов.</summary>
    public uint AoMap; public uint Specular; public uint Pad3; public uint Pad4;
}
