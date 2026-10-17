using System.Numerics;
using Sword.Engine.Animation;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Bodies;

/// <summary>
/// Профили ткани сегментов (<see cref="TissueProfiles"/>) по мешу персонажа в позе привязки: вершины сегмента (по главному суставу) —
/// в систему его капсулы (центр — середина между суставами, ось Y — от проксимального к дистальному, как ставит
/// <see cref="BodyPlacement"/>); в слое — средний радиус от оси. Кость — табличный радиус (анатомия взрослого, м).
/// </summary>
public static class Tissue
{
    /// <summary>Радиус кости по сегменту, м: таз — крестец, торс и голова — позвонок (разрез шеи), плечо — плечевая, предплечье — две кости как одна, бедро, голень, стопа.</summary>
    private static readonly float[] BoneRadius = [0.03f, 0.013f, 0.012f, 0.011f, 0.01f, 0.011f, 0.01f, 0.014f, 0.013f, 0.014f, 0.013f, 0, 0.012f, 0.012f];

    public static TissueProfiles Build(CharacterMeshPoints mesh, Skeleton skeleton)
    {
        var model = new Matrix4x4[skeleton.JointCount];
        skeleton.ToModel(skeleton.ReferenceRot, skeleton.ReferencePos, model);
        var joints = new BodyJoints(skeleton);
        var segments = JointSegments.Build(skeleton);
        Span<Vector3> pos = stackalloc Vector3[BodySegs.Bodies];
        Span<Quaternion> rot = stackalloc Quaternion[BodySegs.Bodies];
        BodyPlacement.Capsules(model, joints, Vector3.Zero, 0, pos, rot);
        var world = BodyPlacement.World(Vector3.Zero, 0);

        var profiles = new TissueProfiles();
        var local = new Vector3[mesh.Positions.Length];
        var segment = new int[mesh.Positions.Length];
        for (int s = 0; s < BodySegs.Count; s++)
        {
            profiles.Start[s] = float.MaxValue;
            profiles.End[s] = float.MinValue;
            profiles.Bone[s] = BoneRadius[s];
            if (BodySegs.IsFlesh(s))
            {
                var (a, b) = BodyPlacement.Ends(model, joints, world, s);
                profiles.Half[s] = Vector3.Distance(a, b) * 0.5f;
            }
        }
        for (int i = 0; i < local.Length; i++)
        {
            int j = mesh.DominantJoint[i];
            int s = (uint)j < (uint)segments.Length ? segments[j] : -1;
            segment[i] = s;
            if (!BodySegs.IsFlesh(s)) continue;
            var p = Vector3.Transform(Vector3.Transform(mesh.Positions[i], world) - pos[s], Quaternion.Conjugate(rot[s]));
            local[i] = p;
            profiles.Start[s] = MathF.Min(profiles.Start[s], p.Y);
            profiles.End[s] = MathF.Max(profiles.End[s], p.Y);
        }
        Span<float> sum = stackalloc float[TissueProfiles.Samples];
        Span<int> count = stackalloc int[TissueProfiles.Samples];
        for (int s = 0; s < BodySegs.Count; s++)
        {
            if (!BodySegs.IsFlesh(s))
            {
                profiles.Start[s] = profiles.End[s] = 0;
                continue;
            }
            sum.Clear();
            count.Clear();
            int points = 0;
            for (int i = 0; i < local.Length; i++)
                if (segment[i] == s)
                {
                    points++;
                    if (profiles.End[s] > profiles.Start[s]) Accumulate(profiles, s, local[i], sum, count);
                }
            if (points < MinPoints) Capsule(profiles, s);
            else Fill(profiles, s, sum, count);
        }
        return profiles;
    }

    /// <summary>Меньше стольких вершин у сегмента — меша у него нет (синтетический контент): профиль — капсула тела.</summary>
    public const int MinPoints = 16;

    /// <summary>Профиль-капсула: между суставами, радиус капсулы бойца (<see cref="FighterShape.Radii"/>).</summary>
    private static void Capsule(TissueProfiles profiles, int s)
    {
        float half = MathF.Max(profiles.Half[s], 0.05f), r = FighterShape.Radii[s];
        profiles.Start[s] = -half;
        profiles.End[s] = half;
        for (int i = 0; i < TissueProfiles.Samples; i++) profiles.Radius[s * TissueProfiles.Samples + i] = r;
    }

    private static void Accumulate(TissueProfiles profiles, int s, Vector3 p, Span<float> sum, Span<int> count)
    {
        int slice = profiles.Slice(s, p.Y);
        sum[slice] += MathF.Sqrt(p.X * p.X + p.Z * p.Z);
        count[slice]++;
    }

    /// <summary>Средний радиус слоёв; пустой слой — от ближних непустых (не меньше кости).</summary>
    private static void Fill(TissueProfiles profiles, int s, ReadOnlySpan<float> sum, ReadOnlySpan<int> count)
    {
        int n = TissueProfiles.Samples, row = s * n;
        for (int i = 0; i < n; i++) profiles.Radius[row + i] = count[i] > 0 ? sum[i] / count[i] : 0;
        for (int i = 0; i < n; i++)
        {
            if (count[i] > 0) continue;
            float left = 0, right = 0;
            for (int k = i - 1; k >= 0; k--) if (count[k] > 0) { left = profiles.Radius[row + k]; break; }
            for (int k = i + 1; k < n; k++) if (count[k] > 0) { right = profiles.Radius[row + k]; break; }
            profiles.Radius[row + i] = left > 0 && right > 0 ? (left + right) * 0.5f : MathF.Max(left, right);
        }
        for (int i = 0; i < n; i++) profiles.Radius[row + i] = MathF.Max(profiles.Radius[row + i], profiles.Bone[s] * 1.5f);
    }
}
