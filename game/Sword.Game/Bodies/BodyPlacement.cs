using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Physics;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Суставы скелета, на которые встают капсулы тела (<c>CharacterRig</c> SWORD 2eab13b: <c>Pelvis, Spine, Neck, Head, UArmL,
/// LArmL, HandL, UArmR, LArmR, Hand, ThighL, CalfL, FootL, BallL, ThighR, CalfR, FootR, BallR</c>). Без любого из них игра не
/// стартует (как <c>CharacterRig</c>: «в скелете нет …»).
/// </summary>
public sealed class BodyJoints
{
    public readonly int Pelvis, Spine, Neck, Head, UArmL, LArmL, HandL, UArmR, LArmR, HandR, ThighL, CalfL, FootL, BallL, ThighR, CalfR, FootR, BallR;

    /// <summary>Имена суставов в порядке полей.</summary>
    public static readonly string[] Names =
    [
        "pelvis", "spine_01", "neck_01", "head", "upperarm_l", "lowerarm_l", "hand_l", "upperarm_r", "lowerarm_r", "hand_r",
        "thigh_l", "calf_l", "foot_l", "ball_l", "thigh_r", "calf_r", "foot_r", "ball_r",
    ];

    /// <exception cref="InvalidDataException">В скелете нет сустава тела.</exception>
    public BodyJoints(Skeleton skeleton)
    {
        int J(string n) => skeleton.Find(n) is var i and >= 0 ? i
            : throw new InvalidDataException($"в скелете {skeleton.Id} нет сустава «{n}» — на нём стоит капсула тела бойца (5б)");
        Pelvis = J("pelvis"); Spine = J("spine_01"); Neck = J("neck_01"); Head = J("head");
        UArmL = J("upperarm_l"); LArmL = J("lowerarm_l"); HandL = J("hand_l");
        UArmR = J("upperarm_r"); LArmR = J("lowerarm_r"); HandR = J("hand_r");
        ThighL = J("thigh_l"); CalfL = J("calf_l"); FootL = J("foot_l"); BallL = J("ball_l");
        ThighR = J("thigh_r"); CalfR = J("calf_r"); FootR = J("foot_r"); BallR = J("ball_r");
    }
}

/// <summary>
/// Постановка капсул тела на позу (<c>DuelArena.PlaceBodies</c> SWORD 2eab13b без клинка, <c>BodyPlacer</c>, фиксации стоп и
/// визуального смещения — этапы 6, 9, 8): мир бойца — поворот на курс и перенос в корень на высоте рельефа под корнем
/// (<c>KinWorld</c>); сустав в мире — перенос модельной матрицы в мир бойца; капсула — между двумя точками: центр — середина,
/// поворот — <see cref="AnimMath.FromBasis"/>(направление, «вправо» бойца). Выражения и порядок — SWORD.
/// </summary>
public static class BodyPlacement
{
    /// <summary>Мировая система скелета (<c>DuelArena.KinWorld</c>): поворот на курс, перенос в корень, высота — земля под корнем.</summary>
    public static Matrix4x4 World(Vector3 root, float facing)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing));
        m.Translation = new Vector3(root.X, ArenaTerrain.Height(root.X, root.Z), root.Z);
        return m;
    }

    /// <summary>«Вправо» бойца в мире — ось X капсул (<c>right</c> в <c>PlaceBodies</c>).</summary>
    public static Vector3 Right(float facing) => Vector3.Transform(new Vector3(-1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing));

    /// <summary>
    /// Постановка 13 капсул плоти бойца (тела сочленения по номерам сегментов, <see cref="BodySegs.Body"/>; место клинка — тело 11 — не
    /// трогается: его ставит клинок, <c>Combat.BladePlacement</c>) по модельным матрицам позы <paramref name="model"/> в корне
    /// <paramref name="root"/> курсом <paramref name="facing"/>: центры и повороты.
    /// </summary>
    public static void Capsules(ReadOnlySpan<Matrix4x4> model, BodyJoints j, Vector3 root, float facing, Span<Vector3> position,
        Span<Quaternion> rotation)
    {
        var world = World(root, facing);
        var right = Right(facing);

        var head = P(model, j.Head, world);
        var neck = P(model, j.Neck, world);
        Place(BodySegs.Pelvis, P(model, j.Pelvis, world), P(model, j.Spine, world), right, position, rotation);
        Place(BodySegs.Torso, P(model, j.Spine, world), neck, right, position, rotation);
        Place(BodySegs.Head, head, head + Vector3.Normalize(head - neck) * 0.22f, right, position, rotation);
        Place(BodySegs.UArmL, P(model, j.UArmL, world), P(model, j.LArmL, world), right, position, rotation);
        Place(BodySegs.FArmL, P(model, j.LArmL, world), P(model, j.HandL, world), right, position, rotation);
        Place(BodySegs.UArmR, P(model, j.UArmR, world), P(model, j.LArmR, world), right, position, rotation);
        Place(BodySegs.FArmR, P(model, j.LArmR, world), P(model, j.HandR, world), right, position, rotation);
        Place(BodySegs.ThighL, P(model, j.ThighL, world), P(model, j.CalfL, world), right, position, rotation);
        Place(BodySegs.ShinL, P(model, j.CalfL, world), P(model, j.FootL, world), right, position, rotation);
        Place(BodySegs.FootL, P(model, j.FootL, world), P(model, j.BallL, world), right, position, rotation);
        Place(BodySegs.ThighR, P(model, j.ThighR, world), P(model, j.CalfR, world), right, position, rotation);
        Place(BodySegs.ShinR, P(model, j.CalfR, world), P(model, j.FootR, world), right, position, rotation);
        Place(BodySegs.FootR, P(model, j.FootR, world), P(model, j.BallR, world), right, position, rotation);
    }

    /// <summary>Сустав в мире: перенос модельной матрицы в мир бойца (<c>P(j)</c> в <c>PlaceBodies</c>).</summary>
    private static Vector3 P(ReadOnlySpan<Matrix4x4> model, int joint, in Matrix4x4 world) => Vector3.Transform(model[joint].Translation, world);

    /// <summary>Капсула между точками <paramref name="a"/> и <paramref name="b"/> (внутренняя <c>Place</c> в <c>PlaceBodies</c>).</summary>
    private static void Place(int segment, Vector3 a, Vector3 b, Vector3 right, Span<Vector3> position, Span<Quaternion> rotation)
    {
        var dir = b - a;
        if (dir.LengthSquared() < 1e-8f) dir = Vector3.UnitY;
        int body = BodySegs.Body(segment);
        position[body] = (a + b) * 0.5f;
        rotation[body] = AnimMath.FromBasis(dir, right);
    }

    /// <summary>
    /// Точки, между которыми стоит капсула сегмента <paramref name="segment"/> (для проверок: инвариант
    /// <c>physics.kinematicPose</c>, агент) — те же, что в <see cref="Capsules"/>.
    /// </summary>
    public static (Vector3 A, Vector3 B) Ends(ReadOnlySpan<Matrix4x4> model, BodyJoints j, in Matrix4x4 world, int segment)
    {
        switch (segment)
        {
            case BodySegs.Head:
                var head = P(model, j.Head, world);
                return (head, head + Vector3.Normalize(head - P(model, j.Neck, world)) * 0.22f);
            case BodySegs.Pelvis: return (P(model, j.Pelvis, world), P(model, j.Spine, world));
            case BodySegs.Torso: return (P(model, j.Spine, world), P(model, j.Neck, world));
            case BodySegs.UArmL: return (P(model, j.UArmL, world), P(model, j.LArmL, world));
            case BodySegs.FArmL: return (P(model, j.LArmL, world), P(model, j.HandL, world));
            case BodySegs.UArmR: return (P(model, j.UArmR, world), P(model, j.LArmR, world));
            case BodySegs.FArmR: return (P(model, j.LArmR, world), P(model, j.HandR, world));
            case BodySegs.ThighL: return (P(model, j.ThighL, world), P(model, j.CalfL, world));
            case BodySegs.ShinL: return (P(model, j.CalfL, world), P(model, j.FootL, world));
            case BodySegs.FootL: return (P(model, j.FootL, world), P(model, j.BallL, world));
            case BodySegs.ThighR: return (P(model, j.ThighR, world), P(model, j.CalfR, world));
            case BodySegs.ShinR: return (P(model, j.CalfR, world), P(model, j.FootR, world));
            case BodySegs.FootR: return (P(model, j.FootR, world), P(model, j.BallR, world));
            default: throw new ArgumentOutOfRangeException(nameof(segment), segment, "у сегмента нет капсулы бойца");
        }
    }

    /// <summary>
    /// Постройка бойца (<c>Fighter.Build</c> → <c>PlaceBuilt</c> SWORD): форма тела с массой от места и курса постройки
    /// (<see cref="FighterShape"/>: радиусы, полудлины, массы — 7ж) и капсулы плоти без скорости (<see cref="IPhysicsWorld.Place"/>) по
    /// позе постройки <paramref name="startModel"/> в корне постройки. Тело клинка (11) — по мечу в руке, его строит клинок
    /// (<c>Combat.BladeSetupSystem</c>).
    /// </summary>
    public static void Build(IPhysicsWorld physics, ArticulationHandle body, ReadOnlySpan<Matrix4x4> startModel, BodyJoints joints,
        Vector3 root, float facing)
    {
        Span<float> half = stackalloc float[BodySegs.Count];
        FighterShape.HalfLengths(root, facing, half);
        Span<Vector3> pos = stackalloc Vector3[BodySegs.Bodies];
        Span<Quaternion> rot = stackalloc Quaternion[BodySegs.Bodies];
        Capsules(startModel, joints, root, facing, pos, rot);
        var radii = FighterShape.Radii;
        var masses = FighterShape.Masses;
        for (int i = 0; i < BodySegs.Bodies; i++)
        {
            int s = BodySegs.Segment(i);
            if (!BodySegs.IsFlesh(s)) continue;
            var b = physics.Body(body, i);
            physics.SetShape(b, masses[s], radii[s], half[s]);
            physics.Place(b, pos[i], rot[i]);
        }
    }

    /// <summary>
    /// Тик: капсулы плоти — в цели по позе тика (<see cref="IPhysicsWorld.SetKinematicTarget"/>: скорости — разностью за
    /// <paramref name="dt"/>); тело клинка ставит клинок.
    /// </summary>
    public static void Tick(IPhysicsWorld physics, ArticulationHandle body, ReadOnlySpan<Matrix4x4> model, BodyJoints joints,
        Vector3 root, float facing, float dt)
    {
        Span<Vector3> pos = stackalloc Vector3[BodySegs.Bodies];
        Span<Quaternion> rot = stackalloc Quaternion[BodySegs.Bodies];
        Capsules(model, joints, root, facing, pos, rot);
        for (int i = 0; i < BodySegs.Bodies; i++)
            if (BodySegs.IsFlesh(BodySegs.Segment(i))) physics.SetKinematicTarget(physics.Body(body, i), pos[i], rot[i], dt);
    }

    /// <summary>Сочленение годно для постановки бойца: живое и из <see cref="BodySegs.Bodies"/> тел (иначе его ловит <c>physics.bodyRefs</c>).</summary>
    public static bool Placeable(IPhysicsWorld physics, ArticulationHandle body) =>
        physics.IsAlive(body) && physics.BodyCount(body) == BodySegs.Bodies;
}
