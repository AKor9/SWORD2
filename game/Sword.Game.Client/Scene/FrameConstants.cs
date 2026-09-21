using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Game.Client.Scene;

/// <summary>
/// Константы кадра сцены (<c>ConstantBuffer&lt;FrameConstants&gt; gFrame : register(b0)</c> в scene_common.hlsli; размер
/// сверяет шейдер — FRAME_CONSTANTS_SIZE). Раскладка — как у <c>Sim/SimLayout.cs FrameConstants</c> SWORD 2eab13b:
/// поля придут в дело вместе со своими фичами. Поля — строками по 16 байт.
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
}
