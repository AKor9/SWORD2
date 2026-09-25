using Sword.Engine.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Сустав скелета → сегмент тела бойца (<c>CharacterRig.JointSegment</c> SWORD 2eab13b): точное имя ключевой кости, иначе
/// по имени кости UE Manny (стороны — суффиксы <c>_l/_r</c>, пальцы — к предплечью), иначе вверх по родителям до ключевой,
/// иначе таз. По имени, а не по иерархии: экспорт UE оставил часть костей без родителя (<c>ball_r</c>, пальцы). Нужна
/// ID-буферу снимка (сегмент пикселя — по суставу вершины), позже — разрезам (Cutting отложен — решение 17).
/// </summary>
public static class JointSegments
{
    private static readonly Dictionary<string, int> Key = new(StringComparer.Ordinal)
    {
        ["pelvis"] = BodySegs.Pelvis, ["spine_01"] = BodySegs.Torso, ["clavicle_l"] = BodySegs.Torso,
        ["clavicle_r"] = BodySegs.Torso, ["neck_01"] = BodySegs.Head,
        ["upperarm_l"] = BodySegs.UArmL, ["lowerarm_l"] = BodySegs.FArmL, ["hand_l"] = BodySegs.FArmL,
        ["upperarm_r"] = BodySegs.UArmR, ["lowerarm_r"] = BodySegs.FArmR, ["hand_r"] = BodySegs.FArmR,
        ["weapon_r"] = BodySegs.FArmR,
        ["thigh_l"] = BodySegs.ThighL, ["calf_l"] = BodySegs.ShinL, ["foot_l"] = BodySegs.FootL,
        ["thigh_r"] = BodySegs.ThighR, ["calf_r"] = BodySegs.ShinR, ["foot_r"] = BodySegs.FootR,
    };

    /// <summary>Сегмент каждого сустава скелета (<see cref="BodySegs"/>).</summary>
    public static int[] Build(Skeleton skeleton) => Build(skeleton.JointNames, skeleton.JointParents);

    public static int[] Build(ReadOnlySpan<string> names, ReadOnlySpan<int> parents)
    {
        var result = new int[names.Length];
        for (int j = 0; j < names.Length; j++)
        {
            string n = names[j];
            int seg = Key.TryGetValue(n, out var exact) ? exact : ByName(n);
            if (seg < 0)
            {
                seg = BodySegs.Pelvis;
                for (int k = parents[j]; k >= 0; k = parents[k])
                    if (Key.TryGetValue(names[k], out var s))
                    {
                        seg = s;
                        break;
                    }
            }
            result[j] = seg;
        }
        return result;
    }

    /// <summary>Сегмент по имени кости скелета UE Manny (<c>CharacterRig.SegmentByName</c> SWORD); −1 — не распознано.</summary>
    public static int ByName(string name)
    {
        bool left = name.EndsWith("_l", StringComparison.Ordinal) || name.Contains("_l_", StringComparison.Ordinal);
        bool right = name.EndsWith("_r", StringComparison.Ordinal) || name.Contains("_r_", StringComparison.Ordinal);
        bool Has(string p) => name.StartsWith(p, StringComparison.Ordinal);
        if (left || right)
        {
            if (Has("ball") || Has("foot")) return right ? BodySegs.FootR : BodySegs.FootL;
            if (Has("calf")) return right ? BodySegs.ShinR : BodySegs.ShinL;
            if (Has("thigh")) return right ? BodySegs.ThighR : BodySegs.ThighL;
            if (Has("hand") || Has("index") || Has("middle") || Has("ring") || Has("pinky") || Has("thumb") || Has("lowerarm") || Has("weapon"))
                return right ? BodySegs.FArmR : BodySegs.FArmL;
            if (Has("upperarm")) return right ? BodySegs.UArmR : BodySegs.UArmL;
            if (Has("clavicle")) return BodySegs.Torso;
        }
        if (Has("spine")) return BodySegs.Torso;
        if (Has("neck") || Has("head")) return BodySegs.Head;
        if (Has("pelvis") || Has("root") || Has("ik_") || Has("poi") || Has("attach")) return BodySegs.Pelvis;
        return -1;
    }
}
