using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Physics;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Суставы трупа бойца — 13 суставов по дереву сегментов; сустав <c>j</c> — ребёнок сегмента <c>j + 1</c> (решение от таза к
/// листьям). Точка сустава на ребёнке — нижний конец оси его капсулы. Пределы — от позы смерти; шарниры (локти, колени) —
/// жёсткие, ось — анатомическая (<see cref="Anatomy"/>), сгиб — положительный поворот.
/// </summary>
public sealed class CorpseJoints
{
    /// <summary>Суставов у трупа бойца: по суставу на каждое тело, кроме таза.</summary>
    public const int Count = BodySegs.Bodies - 1;

    /// <summary>Сустав запястья (предплечье правое → клинок): выключается при каждой смерти — меч выпадает.</summary>
    public const int Wrist = BodySegs.Weapon - 1;

    /// <summary>Сустав, чей ребёнок — сегмент <paramref name="segment"/> (1…13); у таза сустава нет.</summary>
    public static int JointOf(int segment) => segment - 1;

    /// <summary>Родитель сегмента по дереву тела: таз — корень (−1).</summary>
    public static int Parent(int segment) => BodySegs.Parent(segment);

    /// <summary>Пределы шарнира: сгиб от выпрямленного, рад, и люфт оси, рад.</summary>
    public readonly record struct HingeLimits(float BendMin, float BendMax, float AxisSlack);

    /// <summary>Пределы наклона-скручивания от позы смерти, рад: наклон по X (вперёд-назад у ног и корпуса), по Z, скручивание.</summary>
    public readonly record struct SwingLimits(float SwingXMin, float SwingXMax, float SwingZ, float Twist);

    // Отправные пределы: X у капсул бойца — «вправо» бойца; наклон +X бедра — сгиб вперёд, корпуса и шеи — назад.
    public static readonly SwingLimits Spine = new(-0.9f, 0.35f, 0.45f, 0.6f);
    public static readonly SwingLimits Neck = new(-0.8f, 0.6f, 0.5f, 1.25f);
    public static readonly SwingLimits Shoulder = new(-2.6f, 2.6f, 2.0f, 1.3f);
    public static readonly SwingLimits Hip = new(-0.7f, 2.0f, 0.8f, 0.6f);
    public static readonly SwingLimits WristLimits = new(-1.5f, 1.5f, 1.5f, 3.0f);
    public static readonly SwingLimits Ankle = new(-0.8f, 0.8f, 0.35f, 0.6f);
    public static readonly HingeLimits Elbow = new(0, 2.6f, 0.05f);
    public static readonly HingeLimits Knee = new(0, 2.5f, 0.2f);

    /// <summary>
    /// Пары самоколлизии трупа — все пары 14 тел, кроме связанных суставом (и запястья) и торса с бёдрами (внахлёст в любой позе
    /// живого, складку держит предел бедра). Порядок — по номерам тел (a &lt; b), 76 пар.
    /// </summary>
    public static ReadOnlySpan<BodyPair> SelfPairs => SelfPairTable;

    private static readonly BodyPair[] SelfPairTable = BuildSelfPairs();

    /// <summary>Пара тел трупа сталкивается: не связаны суставом и не торс с бедром.</summary>
    public static bool Collides(int a, int b)
    {
        if (a == b) return false;
        if (a > b) (a, b) = (b, a);
        if (a != BodySegs.Pelvis && Parent(a) == b || b != BodySegs.Pelvis && Parent(b) == a) return false;
        return !(a == BodySegs.Torso && b is BodySegs.ThighL or BodySegs.ThighR);
    }

    private static BodyPair[] BuildSelfPairs()
    {
        var pairs = new List<BodyPair>();
        for (int a = 0; a < BodySegs.Bodies; a++)
            for (int b = a + 1; b < BodySegs.Bodies; b++)
                if (Collides(a, b)) pairs.Add(new BodyPair(BodySegs.Body(a), BodySegs.Body(b)));
        return [.. pairs];
    }

    private readonly Anatomy _anatomy;

    public CorpseJoints(Anatomy anatomy) => _anatomy = anatomy;

    /// <summary>Анатомия шарниров скелета, по которой строятся описания.</summary>
    public Anatomy Hinges => _anatomy;

    /// <summary>
    /// Описания 13 суставов трупа по позе смерти: точки — по нынешней форме капсул, оси шарниров — анатомические оси костей позы
    /// <paramref name="model"/> в системе капсулы ребёнка. Памяти не выделяет.
    /// </summary>
    public void Describe(IPhysicsWorld physics, ArticulationHandle body, ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld,
        Span<JointDesc> joints)
    {
        if (joints.Length < Count) throw new ArgumentException($"нужно {Count} мест", nameof(joints));
        for (int segment = 1; segment < BodySegs.Bodies; segment++)
        {
            int j = JointOf(segment), parent = Parent(segment);
            var child = physics.Get(physics.Body(body, BodySegs.Body(segment)));
            var anchor = new Vector3(0, -child.HalfLength, 0);
            int p = BodySegs.Body(parent), c = BodySegs.Body(segment);
            joints[j] = segment switch
            {
                BodySegs.Torso => Swing(p, c, anchor, Spine),
                BodySegs.Head => Swing(p, c, anchor, Neck),
                BodySegs.UArmL or BodySegs.UArmR => Swing(p, c, anchor, Shoulder),
                BodySegs.ThighL or BodySegs.ThighR => Swing(p, c, anchor, Hip),
                BodySegs.Weapon => Swing(p, c, anchor, WristLimits),
                BodySegs.FootL or BodySegs.FootR => Swing(p, c, anchor, Ankle),
                BodySegs.FArmL or BodySegs.FArmR => Hinge(p, c, anchor, Elbow, _anatomy.Axis(segment, model, fighterWorld, child.Rotation)),
                _ => Hinge(p, c, anchor, Knee, _anatomy.Axis(segment, model, fighterWorld, child.Rotation)),
            };
        }
    }

    private static JointDesc Swing(int parent, int child, Vector3 anchor, in SwingLimits l) =>
        JointDesc.SwingTwist(parent, child, anchor, l.SwingXMin, l.SwingXMax, l.SwingZ, l.Twist);

    private static JointDesc Hinge(int parent, int child, Vector3 anchor, in HingeLimits l, Vector3 axis) =>
        JointDesc.Hinge(parent, child, anchor, l.BendMin, l.BendMax, l.AxisSlack, 1) with { HingeAxis = axis };

    /// <summary>
    /// Анатомические оси шарниров (локоть, колено): ось сгиба в системе кости ребёнка — среднее <c>u × v</c> по кадрам клипов, где
    /// сустав заметно согнут (<c>|u × v| ≥ </c><see cref="BendSine"/>). Сустав почти не сгибается (синтетический скелет) — запасная
    /// ось: «вправо» бойца со знаком сгиба.
    /// </summary>
    public sealed class Anatomy
    {
        /// <summary>Синус сгиба, с которого кадр участвует в поиске оси (≈ 17°).</summary>
        public const float BendSine = 0.3f;

        /// <summary>Сколько таких кадров нужно, чтобы ось считалась найденной.</summary>
        public const int MinSamples = 30;

        private readonly int[] _joint = new int[BodySegs.Bodies];
        private readonly Vector3[] _local = new Vector3[BodySegs.Bodies];
        private readonly bool[] _found = new bool[BodySegs.Bodies];

        /// <summary>По библиотеке клипов (суставы тела — <paramref name="joints"/>).</summary>
        public Anatomy(ClipLibrary clips, BodyJoints joints)
        {
            Find(clips, BodySegs.FArmL, joints.UArmL, joints.LArmL, joints.HandL);
            Find(clips, BodySegs.FArmR, joints.UArmR, joints.LArmR, joints.HandR);
            Find(clips, BodySegs.ShinL, joints.ThighL, joints.CalfL, joints.FootL);
            Find(clips, BodySegs.ShinR, joints.ThighR, joints.CalfR, joints.FootR);
        }

        /// <summary>Ось найдена по клипам (иначе — запасная «вправо» бойца).</summary>
        public bool Found(int segment) => _found[segment];

        /// <summary>Ось сгиба в системе кости ребёнка (найденная).</summary>
        public Vector3 Local(int segment) => _local[segment];

        private void Find(ClipLibrary clips, int segment, int parent, int child, int grandchild)
        {
            _joint[segment] = child;
            var parents = clips.Skeleton.JointParents;
            if (parents[child] != parent || parents[grandchild] != child) return;   // не цепочка — запасная ось
            int jc = clips.JointCount, frames = clips.FrameCount, n = 0;
            var sum = Vector3.Zero;
            var rot = clips.LocalRot;
            var pos = clips.LocalPos;
            for (int f = 0; f < frames; f++) Sample(rot[f * jc + child], pos[f * jc + child], pos[f * jc + grandchild], ref sum, ref n);
            if (n < MinSamples || sum.LengthSquared() < 1e-12f) return;
            _local[segment] = Vector3.Normalize(sum);
            _found[segment] = true;
        }

        /// <summary>Кадр: кость родителя (в системе ребёнка — <c>conj(rot)·pos</c> ребёнка) и кость ребёнка (положение внука).</summary>
        private static void Sample(Quaternion childRot, Vector3 childPos, Vector3 grandchildPos, ref Vector3 sum, ref int n)
        {
            var u = Vector3.Transform(childPos, Quaternion.Conjugate(childRot));
            var v = grandchildPos;
            float lu = u.Length(), lv = v.Length();
            if (!(lu > 1e-6f && lv > 1e-6f)) return;
            var cross = Vector3.Cross(u / lu, v / lv);
            float s = cross.Length();
            if (!(s >= BendSine)) return;
            sum += cross / s;
            n++;
        }

        /// <summary>
        /// Ось шарнира сегмента <paramref name="segment"/> в системе его капсулы по позе <paramref name="model"/>: сгиб вокруг неё —
        /// положительный.
        /// </summary>
        public Vector3 Axis(int segment, ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld, Quaternion capsule)
        {
            Vector3 world;
            if (_found[segment])
                world = Vector3.TransformNormal(_local[segment], model[_joint[segment]] * fighterWorld);
            else
            {
                // «Вправо» бойца — X мира бойца со знаком: поза описана с +X влево (Mirror в FighterShape), «вправо» — −X мира бойца.
                var right = -Vector3.TransformNormal(Vector3.UnitX, fighterWorld);
                world = segment is BodySegs.ShinL or BodySegs.ShinR ? -right : right;
            }
            var local = Vector3.Transform(world, Quaternion.Conjugate(capsule));
            float l = local.Length();
            return l > 1e-6f ? local / l : Vector3.UnitX;
        }
    }
}
