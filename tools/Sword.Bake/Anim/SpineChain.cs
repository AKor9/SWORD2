using System.Numerics;

namespace Sword.Bake.Anim;

/// <summary>
/// Перенос позвоночника между скелетами одной семьи с разным числом позвонков: у UE4 Mannequin их три
/// (spine_01…03), у UE5 Manny — пять (spine_01…05), и одноимённые кости стоят на разной высоте (spine_03 UE4 —
/// верх груди, у Manny — низ). Перенос по имени опускал всё, что к ним привязано, на 20–27 см. Поэтому позвонок
/// источника ставится по доле длины цепочки <see cref="Start"/> → позвонки → <see cref="End"/>: в точку той же
/// доли на цепочке рига, в звено той кости рига, куда эта точка попала.
/// </summary>
public static class SpineChain
{
    public const string Start = "pelvis", End = "neck_01", Prefix = "spine_";

    /// <summary>Позвонок источника → кость рига (начало звена) и точка на цепочке рига; пусто — у кого-то нет цепочки.</summary>
    public static Dictionary<int, (int Joint, Vector3 Point)> Map(string[] srcNames, Vector3[] srcPos, string[] tgtNames, Vector3[] tgtPos)
    {
        var map = new Dictionary<int, (int, Vector3)>();
        if (Chain(srcNames, srcPos) is not { } src || Chain(tgtNames, tgtPos) is not { } tgt) return map;
        for (int i = 1; i + 1 < src.Joints.Length; i++)
        {
            float f = src.Fraction[i];
            int s = 0;
            while (s + 2 < tgt.Joints.Length && tgt.Fraction[s + 1] <= f + 1e-4f) s++;
            float span = tgt.Fraction[s + 1] - tgt.Fraction[s];
            float t = span > 1e-6f ? Math.Clamp((f - tgt.Fraction[s]) / span, 0, 1) : 0;
            var point = Vector3.Lerp(tgtPos[tgt.Joints[s]], tgtPos[tgt.Joints[s + 1]], t);
            map[src.Joints[i]] = (tgt.Joints[s], point);
        }
        return map;
    }

    /// <summary>Кости цепочки по порядку (начало, позвонки по имени, конец) и доля длины до каждой; null — цепочки нет.</summary>
    private static (int[] Joints, float[] Fraction)? Chain(string[] names, Vector3[] pos)
    {
        int start = Array.IndexOf(names, Start), end = Array.IndexOf(names, End);
        if (start < 0 || end < 0) return null;
        var spine = Enumerable.Range(0, names.Length).Where(i => names[i].StartsWith(Prefix, StringComparison.Ordinal)
            && names[i].Length == Prefix.Length + 2 && char.IsAsciiDigit(names[i][^1]) && char.IsAsciiDigit(names[i][^2]))
            .OrderBy(i => names[i], StringComparer.Ordinal);
        int[] joints = [start, .. spine, end];
        if (joints.Length < 3) return null;
        var fraction = new float[joints.Length];
        for (int i = 1; i < joints.Length; i++) fraction[i] = fraction[i - 1] + Vector3.Distance(pos[joints[i - 1]], pos[joints[i]]);
        float total = fraction[^1];
        if (total <= 0) return null;
        for (int i = 1; i < joints.Length; i++) fraction[i] /= total;
        return (joints, fraction);
    }
}
