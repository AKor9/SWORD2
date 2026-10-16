using System.Numerics;

namespace Sword.Bake.Anim;

/// <summary>
/// Источник пальцев для ретаргет-клипов: raw-экспорт исходного скелета (UE4 Mannequin) с теми же
/// именами клипов и поза покоя целевого скелета (skeleton.json из tools/ue/export_meshes.py).
/// </summary>
public sealed class FingerSource
{
    public required RawManifest Manifest;
    public required string Dir;
    /// <summary>Поза покоя костей пальцев цели относительно родителя (UE, единицы экспорта).</summary>
    public required Dictionary<string, (Vector3 T, Quaternion Q)> Rest;
    public required string RefPosePath;

    /// <summary>null, если источник пальцев не задан. Без позы покоя перенос невозможен.</summary>
    /// <param name="refPose">Уже прочитанная поза из <paramref name="refPosePath"/>; null — прочитать.</param>
    public static FingerSource? Open(string? fingerSrcDir, string? refPosePath, RawManifest dstMan,
        Dictionary<string, (Vector3 T, Quaternion Q)>? refPose = null)
    {
        if (fingerSrcDir is null) return null;
        if (refPosePath is null)
            throw new ArgumentException("--finger-src требует позу покоя целевого скелета: --ref-pose skeleton.json");
        return new FingerSource
        {
            Manifest = RawAnimIO.ReadManifest(fingerSrcDir), Dir = fingerSrcDir, RefPosePath = refPosePath,
            Rest = FingerTransfer.RestPose(refPose ?? UeRig.ReadRefPose(refPosePath), dstMan),
        };
    }

    /// <summary>Клип источника с тем же именем; null — нет.</summary>
    public RawClip? Source(RawClipInfo info) =>
        Manifest.Clips.FirstOrDefault(c => c.Name == info.Name) is { } src ? RawAnimIO.ReadClip(Dir, Manifest, src) : null;
}

/// <summary>
/// Перенос пальцев с исходного клипа (UE4 Mannequin) на ретаргет-клип (UE5 Manny) по направлениям фаланг.
/// IK-ретаргет цепочек пальцев UE4→UE5 ломает кисть: у Manny цепочки начинаются с пястных костей, которых
/// в UE4 нет, и сгиб первой фаланги источника достаётся пясти — костяшки складываются к запястью
/// (угол кисть→пясть→фаланга 140–170° вместо 10–30°), а фаланги расходятся веером на ~50°.
/// Здесь пальцы цели сначала ставятся в позу покоя Manny относительно кисти (пясть остаётся как у меша),
/// затем каждая фаланга доворачивается кратчайшим поворотом так, чтобы её направление в системе кисти
/// совпало с исходным: от прямого пальца это чистый сгиб без скрутки. Кончик, у которого нет дочерней
/// кости, гнётся так же, как средняя фаланга. Система кисти строится по точкам (кисть→middle_01,
/// index_01→pinky_01), поэтому не зависит от осей костей скелетов.
/// Работает в компонентном пространстве UE, до перевода в локальные повороты.
/// </summary>
internal static class FingerTransfer
{
    /// <summary>Звенья пальца от кисти наружу: 0 — пясть, 1…3 — фаланги.</summary>
    private const int Links = 4;
    private static readonly int IndexFinger = Array.IndexOf(UeRig.Fingers, "index"),
        MiddleFinger = Array.IndexOf(UeRig.Fingers, "middle"), PinkyFinger = Array.IndexOf(UeRig.Fingers, "pinky");

    private static string LinkName(string finger, int link, string side) =>
        link == 0 ? UeRig.Metacarpal(finger, side) : UeRig.Phalanx(finger, link, side);

    /// <summary>Индексы костей кистей скелета, один раз на клип; −1 — кости нет.</summary>
    private sealed class Rig
    {
        /// <summary>[сторона] по <see cref="UeRig.Sides"/>.</summary>
        public readonly int[] Hand = new int[UeRig.Sides.Length];
        /// <summary>[сторона, палец, звено].</summary>
        public readonly int[,,] Bone = new int[UeRig.Sides.Length, UeRig.Fingers.Length, Links];
        /// <summary>Кость — звено какого-либо пальца.</summary>
        public readonly bool[] InChain;
        public readonly List<int>[] Children;

        public Rig(RawManifest m)
        {
            InChain = new bool[m.Bones.Length];
            for (int s = 0; s < UeRig.Sides.Length; s++)
            {
                Hand[s] = Array.IndexOf(m.Bones, UeRig.Hand(UeRig.Sides[s]));
                for (int fi = 0; fi < UeRig.Fingers.Length; fi++)
                    for (int l = 0; l < Links; l++)
                    {
                        int j = Array.IndexOf(m.Bones, LinkName(UeRig.Fingers[fi], l, UeRig.Sides[s]));
                        Bone[s, fi, l] = j;
                        if (j >= 0) InChain[j] = true;
                    }
            }
            Children = new List<int>[m.Parents.Length];
            for (int i = 0; i < Children.Length; i++) Children[i] = [];
            for (int i = 0; i < m.Parents.Length; i++) if (m.Parents[i] >= 0) Children[m.Parents[i]].Add(i);
        }
    }

    /// <summary>Кадр источника для кадра цели по доле клипа: первый к первому, последний к последнему.</summary>
    public static int SourceFrame(int frame, int dstFrames, int srcFrames) =>
        dstFrames <= 1 ? 0 : Math.Clamp((int)MathF.Round(frame * (srcFrames - 1) / (float)(dstFrames - 1)), 0, srcFrames - 1);

    /// <summary>Поза покоя костей пальцев относительно родителя по манифесту; кость пальца без позы — ошибка.</summary>
    public static Dictionary<string, (Vector3 T, Quaternion Q)> RestPose(Dictionary<string, (Vector3 T, Quaternion Q)> refPose, RawManifest man)
    {
        var rest = new Dictionary<string, (Vector3, Quaternion)>();
        foreach (var side in UeRig.Sides)
            foreach (var finger in UeRig.Fingers)
                for (int l = 0; l < Links; l++)
                {
                    string bone = LinkName(finger, l, side);
                    int j = Array.IndexOf(man.Bones, bone);
                    if (j < 0) continue;
                    if (man.Parents[j] < 0) throw new InvalidDataException($"у кости пальца {bone} нет родителя");
                    string parent = man.Bones[man.Parents[j]];
                    if (!refPose.TryGetValue(bone, out var w)) throw new InvalidDataException($"в позе покоя нет кости {bone}");
                    if (!refPose.TryGetValue(parent, out var p)) throw new InvalidDataException($"в позе покоя нет кости {parent}");
                    var pinv = Quaternion.Conjugate(p.Q);
                    rest[bone] = (Vector3.Transform(w.T - p.T, pinv), Quaternion.Normalize(pinv * w.Q));
                }
        return rest;
    }

    public static void Apply(RawManifest dstMan, RawClip dst, RawManifest srcMan, RawClip src, Dictionary<string, (Vector3 T, Quaternion Q)> rest)
    {
        var d = new Rig(dstMan);
        var s = new Rig(srcMan);
        var restOf = new (Vector3 T, Quaternion Q)[dstMan.Bones.Length];
        for (int j = 0; j < restOf.Length; j++)
            if (d.InChain[j] && !rest.TryGetValue(dstMan.Bones[j], out restOf[j]))
                throw new InvalidDataException($"в позе покоя пальцев нет кости {dstMan.Bones[j]}");

        for (int side = 0; side < UeRig.Sides.Length; side++)
        {
            if (d.Hand[side] < 0 || s.Hand[side] < 0) continue;
            for (int f = 0; f < dst.Info.Frames; f++)
            {
                int sf = SourceFrame(f, dst.Info.Frames, src.Info.Frames);
                ResetToRest(dstMan, d, dst, f, side, restOf);
                if (!HandFrame(s, src, sf, side, out var sBasis) || !HandFrame(d, dst, f, side, out var dBasis)) continue;
                for (int fi = 0; fi < UeRig.Fingers.Length; fi++)
                {
                    var delta = Quaternion.Identity;
                    for (int i = 1; i <= 3; i++)
                    {
                        int dj = d.Bone[side, fi, i];
                        if (dj < 0) break;
                        if (i == 3)
                        {
                            // у кончика нет дочерней кости — гнётся как средняя фаланга
                            RotateSubtree(dst, f, dj, delta, d.Children);
                            break;
                        }
                        int sa = s.Bone[side, fi, i], sb = s.Bone[side, fi, i + 1], db = d.Bone[side, fi, i + 1];
                        if (sa < 0 || sb < 0 || db < 0) break;
                        var want = ToWorld(dBasis, ToLocal(sBasis, Vector3.Normalize(src.P(sf, sb) - src.P(sf, sa))));
                        var cur = Vector3.Normalize(dst.P(f, db) - dst.P(f, dj));
                        delta = ShortestArcOrIdentity(cur, want);
                        RotateSubtree(dst, f, dj, delta, d.Children);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Кости пальцев кадра — в позу покоя относительно кисти (компонентное пространство), от кисти наружу.
    /// Потомки звеньев, которые сами не звенья (leaf-кости, сокеты), едут жёстко вместе со звеном.
    /// </summary>
    private static void ResetToRest(RawManifest man, Rig rig, RawClip c, int f, int side, (Vector3 T, Quaternion Q)[] rest)
    {
        for (int fi = 0; fi < UeRig.Fingers.Length; fi++)
            for (int l = 0; l < Links; l++)
            {
                int j = rig.Bone[side, fi, l];
                if (j < 0) continue;
                int kp = f * c.BoneCount + man.Parents[j], k = f * c.BoneCount + j;
                var oldPos = c.Pos[k];
                var oldRot = c.Rot[k];
                c.Rot[k] = Quaternion.Normalize(c.Rot[kp] * rest[j].Q);
                c.Pos[k] = c.Pos[kp] + Vector3.Transform(rest[j].T, c.Rot[kp]);
                var move = Quaternion.Normalize(c.Rot[k] * Quaternion.Conjugate(oldRot));
                foreach (int ch in rig.Children[j])
                    if (!rig.InChain[ch]) MoveSubtree(c, f, ch, oldPos, c.Pos[k], move, rig.Children);
            }
    }

    /// <summary>Среднее отклонение направлений фаланг 01→02 и 02→03 цели от источника в системе кисти, градусы.</summary>
    public static double Deviation(RawManifest dstMan, RawClip dst, RawManifest srcMan, RawClip src)
    {
        var d = new Rig(dstMan);
        var s = new Rig(srcMan);
        double sum = 0;
        int n = 0;
        for (int f = 0; f < dst.Info.Frames; f += 5)
        {
            int sf = SourceFrame(f, dst.Info.Frames, src.Info.Frames);
            for (int side = 0; side < UeRig.Sides.Length; side++)
            {
                if (!HandFrame(s, src, sf, side, out var sb) || !HandFrame(d, dst, f, side, out var db)) continue;
                for (int fi = 0; fi < UeRig.Fingers.Length; fi++)
                    for (int a = 1; a <= 2; a++)
                    {
                        int sa = s.Bone[side, fi, a], s2 = s.Bone[side, fi, a + 1], da = d.Bone[side, fi, a], d2 = d.Bone[side, fi, a + 1];
                        if (sa < 0 || s2 < 0 || da < 0 || d2 < 0) continue;
                        var u = ToLocal(sb, Vector3.Normalize(src.P(sf, s2) - src.P(sf, sa)));
                        var v = ToLocal(db, Vector3.Normalize(dst.P(f, d2) - dst.P(f, da)));
                        sum += MathF.Acos(Math.Clamp(Vector3.Dot(u, v), -1, 1)) * 180 / MathF.PI;
                        n++;
                    }
            }
        }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>
    /// Сгиб кисть→пясть→первая фаланга, градусы (среднее по указательному…мизинцу, обе кисти, каждый 5-й кадр).
    /// У здоровой кисти Manny 10–30°; после IK-ретаргета UE4→UE5 — 140–170°.
    /// </summary>
    public static double MetacarpalBend(RawManifest man, RawClip c)
    {
        var rig = new Rig(man);
        double sum = 0;
        int n = 0;
        for (int f = 0; f < c.Info.Frames; f += 5)
            for (int side = 0; side < UeRig.Sides.Length; side++)
            {
                int hand = rig.Hand[side];
                if (hand < 0) continue;
                // GripFingers — все, кроме большого (Fingers[0])
                for (int fi = 1; fi < UeRig.Fingers.Length; fi++)
                {
                    int meta = rig.Bone[side, fi, 0], first = rig.Bone[side, fi, 1];
                    if (meta < 0 || first < 0) continue;
                    var u = Vector3.Normalize(c.P(f, meta) - c.P(f, hand));
                    var v = Vector3.Normalize(c.P(f, first) - c.P(f, meta));
                    sum += MathF.Acos(Math.Clamp(Vector3.Dot(u, v), -1, 1)) * 180 / MathF.PI;
                    n++;
                }
            }
        return n > 0 ? sum / n : 0;
    }

    private static bool HandFrame(Rig r, RawClip c, int f, int side, out Matrix4x4 basis)
    {
        basis = Matrix4x4.Identity;
        int hand = r.Hand[side], mid = r.Bone[side, MiddleFinger, 1], idx = r.Bone[side, IndexFinger, 1], pky = r.Bone[side, PinkyFinger, 1];
        if (hand < 0 || mid < 0 || idx < 0 || pky < 0) return false;
        var fwd = Vector3.Normalize(c.P(f, mid) - c.P(f, hand));
        var lat = c.P(f, idx) - c.P(f, pky);
        lat = Vector3.Normalize(lat - fwd * Vector3.Dot(lat, fwd));
        var up = Vector3.Cross(fwd, lat);
        basis = new Matrix4x4(fwd.X, fwd.Y, fwd.Z, 0, lat.X, lat.Y, lat.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        return true;
    }

    private static Vector3 ToLocal(Matrix4x4 b, Vector3 v) =>
        new(Vector3.Dot(v, new Vector3(b.M11, b.M12, b.M13)), Vector3.Dot(v, new Vector3(b.M21, b.M22, b.M23)),
            Vector3.Dot(v, new Vector3(b.M31, b.M32, b.M33)));

    private static Vector3 ToWorld(Matrix4x4 b, Vector3 l) =>
        new Vector3(b.M11, b.M12, b.M13) * l.X + new Vector3(b.M21, b.M22, b.M23) * l.Y + new Vector3(b.M31, b.M32, b.M33) * l.Z;

    /// <summary>
    /// Кратчайший поворот единичного <paramref name="a"/> в <paramref name="b"/>. В отличие от <see cref="Sword.Engine.Animation.AnimMath.FromTo"/>,
    /// при развороте на 180° возвращает identity: ось такого сгиба не определена, и произвольная ось вывернула бы палец.
    /// </summary>
    private static Quaternion ShortestArcOrIdentity(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        var axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-10f) return Quaternion.Identity;
        return Quaternion.Normalize(new Quaternion(axis, 1 + d));
    }

    /// <summary>Поворот кости и всех потомков вокруг её сустава (компонентное пространство).</summary>
    private static void RotateSubtree(RawClip c, int f, int bone, Quaternion delta, List<int>[] children)
    {
        var pivot = c.P(f, bone);
        MoveSubtree(c, f, bone, pivot, pivot, delta, children);
    }

    /// <summary>Жёсткое перемещение поддерева: точка <paramref name="from"/> переходит в <paramref name="to"/>, поворот <paramref name="delta"/>.</summary>
    private static void MoveSubtree(RawClip c, int f, int bone, Vector3 from, Vector3 to, Quaternion delta, List<int>[] children)
    {
        var stack = new Stack<int>();
        stack.Push(bone);
        while (stack.Count > 0)
        {
            int j = stack.Pop();
            int k = f * c.BoneCount + j;
            c.Pos[k] = to + Vector3.Transform(c.Pos[k] - from, delta);
            c.Rot[k] = Quaternion.Normalize(delta * c.Rot[k]);
            foreach (int ch in children[j]) stack.Push(ch);
        }
    }
}
