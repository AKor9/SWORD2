using System.Numerics;
using Sword.Bake.Engine.Anim;

namespace Sword.Bake.Engine.Tests;

/// <summary>
/// Синтетический скелет тестов (как <c>SynthSkeleton</c> тестов SWORD): корень, таз, две ноги (бедро, голень, стопа, носок),
/// позвоночник, ключицы. Бедро 0.43, голень 0.42 м. Здесь — источник синтетического raw-экспорта UE: полный скелет,
/// компонентные позы в см, «вперёд» — +Y UE, как у Manny.
/// </summary>
internal static class SynthSkeleton
{
    public static readonly string[] Joints =
        ["root", "pelvis", "thigh_l", "calf_l", "foot_l", "ball_l", "thigh_r", "calf_r", "foot_r", "ball_r", "spine_05", "clavicle_l", "clavicle_r"];
    public static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 1, 6, 7, 8, 1, 10, 10];
    /// <summary>Смещение сустава от родителя в покое, пространство персонажа (+X влево, +Y вверх, +Z вперёд), м.</summary>
    public static readonly Vector3[] Offsets =
    [
        Vector3.Zero, new(0, 0.95f, 0), new(0.1f, -0.05f, 0), new(0, -0.43f, 0), new(0, -0.42f, 0), new(0, -0.05f, 0.12f),
        new(-0.1f, -0.05f, 0), new(0, -0.43f, 0), new(0, -0.42f, 0), new(0, -0.05f, 0.12f), new(0, 0.45f, 0), new(0.15f, 0.1f, 0),
        new(-0.15f, 0.1f, 0),
    ];

    public const int Pelvis = 1, ThighL = 2, ThighR = 6;

    /// <summary>Точка персонажа (м) → UE Manny (см): +X персонажа — +X UE, вверх — +Z, вперёд — +Y (<see cref="UeSpace"/> обратно).</summary>
    public static Vector3 ToUe(Vector3 c) => new Vector3(c.X, c.Z, c.Y) * 100;

    /// <summary>Манифест экспорта полного скелета (UE, см, вперёд +Y) без клипов.</summary>
    public static RawManifest Manifest() => new() { Bones = [.. Joints], Parents = [.. Parents] };

    /// <summary>
    /// Клип экспорта: кость root едет вперёд (+Y UE) на <paramref name="distance"/> м и поворачивается вокруг вертикали на
    /// <paramref name="turn"/> рад за клип, бёдра качаются на ±<paramref name="swing"/> рад, таз покачивается по высоте.
    /// </summary>
    public static RawClip Walk(string name, int frames, bool loop, float distance, float turn, float swing, string path = "/Game/Synth")
    {
        var info = new RawClipInfo { Name = name, Path = $"{path}/{name}", File = name + ".bin", Fps = 30, Frames = frames, Loop = loop };
        int n = Joints.Length;
        var clip = new RawClip(info, n);
        var local = new Quaternion[n];
        for (int f = 0; f < frames; f++)
        {
            float k = f / (float)(frames - 1);
            Array.Fill(local, Quaternion.Identity);
            local[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, k * turn);                    // UE: вокруг вертикали
            float s = swing * MathF.Sin(k * MathF.Tau);
            local[ThighL] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, s);
            local[ThighR] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -s);
            var rootPos = new Vector3(0, distance * 100 * k, 0);
            for (int j = 0; j < n; j++)
            {
                int p = Parents[j];
                var offset = ToUe(Offsets[j]);
                if (j == Pelvis) offset.Z += 2 * MathF.Sin(k * MathF.Tau * 2);   // раскачка таза, см
                int at = f * n + j;
                if (p < 0)
                {
                    clip.Pos[at] = rootPos;
                    clip.Rot[at] = local[j];
                }
                else
                {
                    var parentRot = clip.Rot[f * n + p];
                    clip.Pos[at] = clip.Pos[f * n + p] + Vector3.Transform(offset, parentRot);
                    clip.Rot[at] = Quaternion.Normalize(parentRot * local[j]);
                }
            }
        }
        return clip;
    }
}
