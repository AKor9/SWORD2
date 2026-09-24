using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Physics.Tests.TestBodies;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Суставы (7в), решатель: пределы держатся после шагов (шарнир в диапазоне и только в анатомическую сторону, люфт оси, эллипс
/// наклона, скручивание), податливость предела, выключенный сустав не держит, кинематические тела суставы не двигают, журнал
/// срабатываний, расслабление гасит относительную скорость точек и отскок от предела без прироста энергии, висящая цепочка и
/// падающий боец держат суставы и не набирают энергию, 0 Б на подшаг. Шаг — настоящий <see cref="PhysicsWorld.Step"/> в мире без
/// земли, стены, колонн и пар (одно сочленение или сочленения порознь): в подшаге действуют только интеграция, суставы, обновление
/// скоростей и расслабление. Проход суставов и расслабление по отдельности — шагом из одного подшага (<see cref="OneSubstep"/>),
/// где до расслабления действует известное (<see cref="BindAfterIntegration"/>, <see cref="BeforeRelax"/>).
/// </summary>
public class JointsSolverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 6f)]     // сгиб в анатомическую сторону — до BendMax
    [InlineData(1, -6f)]    // переразгиб — нет
    [InlineData(-1, 6f)]
    [InlineData(-1, -6f)]
    public void Hinge_BendStaysInRange_OnlyOnAnatomicalSide(int sign, float spin)
    {
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        w.SetJoints(art, [JointDesc.Hinge(0, 1, JointsRig.ChildAnchor, 0, 2.5f, 0.2f, (sbyte)sign)]);
        JointsRig.Spin(w, c, JointsRig.JointPoint, new Vector3(spin, 0, 0));
        float lo = float.MaxValue, hi = float.MinValue, worstOff = 0, worstPoint = 0, afterStop = float.MaxValue;
        for (int t = 0; t < 120; t++)
        {
            w.Step(JointsRig.Tick);
            var m = w.MeasureJoint(art, 0);
            (lo, hi) = (MathF.Min(lo, m.Bend), MathF.Max(hi, m.Bend));
            if (hi >= 2.5f - 0.01f) afterStop = MathF.Min(afterStop, m.Bend);
            worstOff = MathF.Max(worstOff, m.Swing);
            worstPoint = MathF.Max(worstPoint, m.PointError);
        }
        output.WriteLine($"сгиб {lo:F5}…{hi:F5}, после упора {(afterStop < float.MaxValue ? $"не меньше {afterStop:F6}" : "—")}, уход оси {worstOff:G3}, точка {worstPoint:G3}");
        if (sign * spin > 0)
        {
            Assert.True(hi <= 2.5f + StopOvershoot && hi >= 2.5f - 0.01f, $"сгиб до {hi}");
            Assert.True(lo >= -StopOvershoot);
            Assert.True(afterStop >= 2.5f - StopRebound, $"отскок от упора: сгиб после упора до {afterStop}");
            Assert.Equal(2.5f, w.MeasureJoint(art, 0).Bend, 1e-3f);   // у упора и остался
        }
        else Assert.True(lo >= -StopOvershoot && hi <= StopOvershoot, $"переразгиб или отскок: сгиб {lo}…{hi}");
        Assert.True(worstOff <= StopOvershoot, $"уход оси {worstOff}");
        Assert.True(worstPoint <= PairPoint, $"ошибка точки {worstPoint}");
    }

    [Fact]
    public void Hinge_SlightlyHyperextendedAtBind_IsPushedBackToStraight_NotBentBackward()
    {
        // Поза смерти с чуть переразогнутым коленом (−0,02 при знаке +1): знак — из описания, не из позы; толчок в сторону
        // переразгиба сустав не пускает, а поза возвращается к выпрямленной.
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.02f));
        w.SetJoints(art, [JointDesc.Hinge(0, 1, JointsRig.ChildAnchor, 0, 2.5f, 0.2f, 1)]);
        Assert.Equal(-0.02f, w.MeasureJoint(art, 0).Bend, 1e-5f);
        JointsRig.Spin(w, c, JointsRig.JointPoint, new Vector3(-4, 0, 0));
        float lo = float.MaxValue, hi = float.MinValue;
        for (int t = 0; t < 60; t++)
        {
            w.Step(JointsRig.Tick);
            float bend = w.MeasureJoint(art, 0).Bend;
            (lo, hi) = (MathF.Min(lo, bend), MathF.Max(hi, bend));
        }
        float last = w.MeasureJoint(art, 0).Bend;
        output.WriteLine($"сгиб {lo:G4}…{hi:G4}, в конце {last:G4}");
        Assert.True(lo >= -0.02f - StopOvershoot, $"колено согнулось назад: {lo}");
        Assert.True(last >= -1e-3f && hi <= StopOvershoot, $"не вернулось к выпрямленному или отскочило: {last}, до {hi}");
    }

    [Theory]
    [InlineData(0f, 3f, 0f, 0.2f)]    // вокруг оси капсулы — скручивание уводит ось шарнира
    [InlineData(0f, 0f, 3f, 0.2f)]    // вбок
    [InlineData(0f, 0f, -3f, 0.05f)]  // люфт локтя
    [InlineData(0f, 2f, 2f, 0f)]      // без люфта
    public void Hinge_AxisStaysWithinSlack(float x, float y, float z, float slack)
    {
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        w.SetJoints(art, [JointDesc.Hinge(0, 1, JointsRig.ChildAnchor, -1, 1, slack, 1)]);
        JointsRig.Spin(w, c, JointsRig.JointPoint, new Vector3(x, y, z));
        float worst = 0, reached = 0;
        for (int t = 0; t < 120; t++)
        {
            w.Step(JointsRig.Tick);
            var m = w.MeasureJoint(art, 0);
            worst = MathF.Max(worst, m.Excess);
            reached = MathF.Max(reached, m.Swing);
        }
        float last = w.MeasureJoint(art, 0).Excess;
        output.WriteLine($"уход оси до {reached:F5}, выход {worst:G3}, в конце {last:G3}");
        Assert.True(worst <= StopOvershoot, $"выход за люфт {worst}");
        Assert.True(last <= 1e-3f, $"заход не погас: {last}");
        Assert.True(reached >= slack - 0.01f, $"до люфта не дошло: {reached}");
    }

    [Theory]
    [InlineData(1f, 0f, 0f, 1.0f)]     // +X — до SwingXMax
    [InlineData(-1f, 0f, 0f, 0.4f)]    // −X — до −SwingXMin
    [InlineData(0f, 0f, 1f, 0.6f)]     // Z — до SwingZ
    [InlineData(0f, 1f, 0f, 0.5f)]     // скручивание — до Twist
    [InlineData(1f, 0.7f, -1f, float.NaN)]   // наклон по диагонали и скручивание вместе
    public void SwingTwist_EllipseAndTwistHold(float x, float y, float z, float limit)
    {
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.4f, 1.0f, 0.6f, 0.5f, 0)]);
        JointsRig.Spin(w, c, JointsRig.JointPoint, Vector3.Normalize(new Vector3(x, y, z)) * 5);
        float worst = 0, reached = 0, worstPoint = 0;
        for (int t = 0; t < 120; t++)
        {
            w.Step(JointsRig.Tick);
            var m = w.MeasureJoint(art, 0);
            worst = MathF.Max(worst, m.Excess);
            reached = MathF.Max(reached, y != 0 && x == 0 ? MathF.Abs(m.Twist) : m.Swing);
            worstPoint = MathF.Max(worstPoint, m.PointError);
        }
        output.WriteLine($"дошло до {reached:F5}, выход {worst:G3}, точка {worstPoint:G3}");
        Assert.True(worst <= StopOvershoot, $"выход за предел {worst}");
        if (!float.IsNaN(limit)) Assert.True(reached >= limit - 0.01f, $"до предела {limit} не дошло: {reached}");
        Assert.True(worstPoint <= PairPoint, $"ошибка точки {worstPoint}");
    }

    [Theory]
    [InlineData(JointKind.Hinge)]
    [InlineData(JointKind.SwingTwist)]
    public void RigidLimits_HoldAfterEveryTick_RandomSpins_BothBodiesSimulated(JointKind kind)
    {
        var rng = new Random(kind == JointKind.Hinge ? 21 : 22);
        float worstExcess = 0, worstPoint = 0;
        for (int run = 0; run < 100; run++)
        {
            var w = JointsRig.World();
            var art = w.CreateArticulation(2);
            float ph = 0.1f + rng.NextSingle() * 0.2f, ch = 0.05f + rng.NextSingle() * 0.25f;
            var p = JointsRig.Capsule(w, art, 0, 2 + rng.NextSingle() * 18, 0.04f + rng.NextSingle() * 0.12f, ph, Vector3.Zero, Quaternion.Identity, BodyMode.Simulated);
            var c = JointsRig.Capsule(w, art, 1, 0.5f + rng.NextSingle() * 8, 0.03f + rng.NextSingle() * 0.08f, ch, new Vector3(0, ph + ch, 0), Quaternion.Identity, BodyMode.Simulated);
            var anchor = new Vector3(0, -ch, 0);
            var joint = kind == JointKind.Hinge
                ? JointDesc.Hinge(0, 1, anchor, -0.2f, 2.0f, 0.1f, 1)
                : JointDesc.SwingTwist(0, 1, anchor, -0.5f, 0.9f, 0.6f, 0.4f, 0);
            w.SetJoints(art, [joint]);
            JointsRig.SetVelocity(w, p, RandomVector(rng, 2), RandomVector(rng, 10));
            JointsRig.SetVelocity(w, c, RandomVector(rng, 2), RandomVector(rng, 20));
            for (int t = 0; t < 60; t++)
            {
                w.Step(JointsRig.Tick);
                var (point, excess) = JointsRig.Worst(w, art);
                worstExcess = MathF.Max(worstExcess, excess);
                worstPoint = MathF.Max(worstPoint, point);
            }
        }
        output.WriteLine($"{kind}: выход {worstExcess:G3} рад, точка {worstPoint:G3} м");
        Assert.True(worstExcess <= RandomExcess, $"выход за предел {worstExcess}");
        Assert.True(worstPoint <= RandomPoint, $"ошибка точки {worstPoint}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-4f)]
    [InlineData(2e-4f)]
    [InlineData(1e-3f)]
    public void LimitCompliance_CorrectsShare_WOverWPlusAlphaOverH2(float alpha)
    {
        // Шар (инерция одинакова по осям) с точкой сустава в центре (сведение точки его не поворачивает): поправка предела
        // поворачивает ребёнка на долю w / (w + α/h²) выхода. Выход мал (0,05 рад): линеаризованный поворот Dynamics.Rotate
        // отстаёт от точного на φ³/12 — здесь ≤ 1e-5. Шаг из одного подшага h = H: тело в покое и без гравитации — интеграция
        // его не двигает, расслабление меняет только скорости, поза после шага — поза после прохода суставов.
        const float over = 0.05f;
        float Excess(float? compliance, PhysicsSettings? settings = null)
        {
            var w = OneSubstep(settings);
            var art = w.CreateArticulation(2);
            JointsRig.Capsule(w, art, 0, 4, 0.07f, 0.2f, Vector3.Zero, Quaternion.Identity, BodyMode.Kinematic);
            var anchor = Vector3.Zero;
            var c = JointsRig.Capsule(w, art, 1, 2, 0.08f, 0, JointsRig.JointPoint - anchor, Quaternion.Identity, BodyMode.Simulated);
            w.SetJoints(art, [JointDesc.SwingTwist(0, 1, anchor, -0.3f, 0.3f, 0.3f, 0.3f, compliance)]);
            JointsRig.PlaceAbout(w, c, anchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f + over));
            Assert.Equal(over, w.MeasureJoint(art, 0).Excess, 1e-5f);
            w.Step(H);
            return w.MeasureJoint(art, 0).Excess;
        }
        float wInv = Dynamics.CapsuleInvInertia(2, 0.08f, 0).X;
        float Share(float a) => over * (a / (H * H)) / (wInv + a / (H * H));
        Assert.Equal(Share(alpha), Excess(alpha), 3e-5f);
        // null — податливость настроек, ручка меняет её на ходу.
        Assert.Equal(Share(alpha), Excess(null, PhysicsSettings.Default with { JointLimitCompliance = alpha }), 3e-5f);
        Assert.Equal(Share(2e-4f), Excess(null), 3e-5f);
    }

    [Fact]
    public void DefaultCompliance_SoftLimitsStillHoldUnderGravityAndSpin()
    {
        // Податливость пределов по умолчанию (2e-4, у наклона-скручивания): предел мягче, но выход остаётся малым.
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated, gravity: 9.81f);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.4f, 1.0f, 0.6f, 0.5f)]);
        JointsRig.Spin(w, c, JointsRig.JointPoint, new Vector3(8, 3, -4));
        float worst = 0, last = 0;
        for (int t = 0; t < 240; t++)
        {
            w.Step(JointsRig.Tick);
            last = w.MeasureJoint(art, 0).Excess;
            worst = MathF.Max(worst, last);
        }
        output.WriteLine($"выход: наибольший {worst:G3}, в конце {last:G3}");
        Assert.True(worst <= SoftExcess, $"выход за мягкий предел {worst}");
    }

    [Fact]
    public void DisabledJoint_HoldsNothing_SameAsNoJoint_ReenabledPullsBack()
    {
        (PhysicsWorld, ArticulationHandle) Build(bool joints)
        {
            var (w, art, p, c) = JointsRig.Pair(BodyMode.Simulated, BodyMode.Simulated, gravity: 9.81f);
            if (joints)
            {
                w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.1f, 0.1f, 0.1f, 0.1f, 0)]);
                w.SetJointEnabled(art, 0, false);
            }
            JointsRig.SetVelocity(w, p, new Vector3(-1, 0, 0), new Vector3(0, 0, 2));
            JointsRig.SetVelocity(w, c, new Vector3(1, 2, 0), new Vector3(5, 1, 0));
            return (w, art);
        }
        var (off, offArt) = Build(true);
        var (free, freeArt) = Build(false);
        for (int t = 0; t < 60; t++)
        {
            off.Step(JointsRig.Tick);
            free.Step(JointsRig.Tick);
            Assert.True(off.JointLimitHits(offArt).IsEmpty);
        }
        for (int i = 0; i < 2; i++) Assert.Equal(free.Get(free.Body(freeArt, i)), off.Get(off.Body(offArt, i)));   // побитово
        var m = off.MeasureJoint(offArt, 0);
        Assert.True(m.PointError > 0.5f && m.Excess > 0.1f, $"выключенный сустав держал: {m}");

        // Включили снова после 5 тиков порознь: сустав стягивает точку к прежней привязке и держит пару вместе.
        var (again, againArt) = Build(true);
        for (int t = 0; t < 5; t++) again.Step(JointsRig.Tick);
        float gap = again.MeasureJoint(againArt, 0).PointError;
        Assert.True(gap > 0.05f, $"за 5 тиков разошлись только на {gap}");
        again.SetJointEnabled(againArt, 0, true);
        for (int t = 0; t < 60; t++) again.Step(JointsRig.Tick);
        var (point, excess) = JointsRig.Worst(again, againArt);
        output.WriteLine($"порознь {gap:G3} м; включённый снова через 0,5 с: точка {point:G3}, выход {excess:G3}");
        Assert.True(point <= RandomPoint && excess <= RandomExcess, $"включённый не держит: точка {point}, выход {excess}");
    }

    [Fact]
    public void KinematicArticulation_JointsDoNotTouchIt_LogStaysEmpty()
    {
        var w = JointsRig.World(9.81f);
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Kinematic);
        w.SetJoints(art, JointsRig.FighterJoints(w, art));
        // Поза за пределами и с разошедшимися точками, со скоростями постановки.
        var rng = new Random(5);
        for (int i = 0; i < 14; i++)
        {
            var b = w.Body(art, i);
            var s = w.Get(b);
            w.SetKinematicTarget(b, s.Position + RandomVector(rng, 0.1f), RandomRotation(rng), JointsRig.Tick);
        }
        var before = new BodyState[14];
        for (int i = 0; i < 14; i++) before[i] = w.Get(w.Body(art, i));
        for (int t = 0; t < 10; t++) w.Step(JointsRig.Tick);
        for (int i = 0; i < 14; i++) Assert.Equal(before[i], w.Get(w.Body(art, i)));
        Assert.True(w.JointLimitHits(art).IsEmpty);
        Assert.True(JointsRig.Worst(w, art).Excess > 0.1f);   // пределы нарушены — но кинематику суставы не трогают
    }

    [Fact]
    public void KinematicParent_IsFixedSide_ChildFollowsItsMotion()
    {
        var (w, art, p, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated, gravity: 9.81f);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.3f, 0.3f, 0.3f, 0.3f, 0)]);
        float worstPoint = 0, worstExcess = 0;
        for (int t = 0; t < 120; t++)
        {
            // Родитель — живая кость: ведут по кругу радиуса 0,3 м (от своего места, 0,9 м/с) с поворотом 3 рад/с.
            float a = (t + 1) * JointsRig.Tick * 3;
            w.SetKinematicTarget(p, new Vector3(MathF.Sin(a), 0, MathF.Cos(a) - 1) * 0.3f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, a), JointsRig.Tick);
            var parent = w.Get(p);
            w.Step(JointsRig.Tick);
            Assert.Equal(parent, w.Get(p));   // кинематическое тело шаг и суставы не двигают
            var (point, excess) = JointsRig.Worst(w, art);
            worstPoint = MathF.Max(worstPoint, point);
            worstExcess = MathF.Max(worstExcess, excess);
        }
        output.WriteLine($"точка {worstPoint:G3} м, выход {worstExcess:G3} рад");
        Assert.True(worstPoint <= KinematicPoint, $"ошибка точки {worstPoint}");
        Assert.True(worstExcess <= KinematicExcess, $"выход за предел {worstExcess}");
    }

    [Fact]
    public void LimitHits_LoggedPerStep_JointSubstepUnitAxis_ClearedNextStep()
    {
        var w = JointsRig.World();
        var art = w.CreateArticulation(3);
        JointsRig.Capsule(w, art, 0, 4, 0.07f, 0.2f, Vector3.Zero, Quaternion.Identity, BodyMode.Kinematic);
        var b1 = JointsRig.Capsule(w, art, 1, 2, 0.055f, 0.15f, new Vector3(0, 0.35f, 0), Quaternion.Identity, BodyMode.Simulated);
        var b2 = JointsRig.Capsule(w, art, 2, 1, 0.05f, 0.15f, new Vector3(0, 0.65f, 0), Quaternion.Identity, BodyMode.Simulated);
        w.SetJoints(art,
        [
            JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.1f, 0.1f, 0.1f, 0.1f, 0),
            JointDesc.Hinge(1, 2, JointsRig.ChildAnchor, 0, 0.1f, 0.05f, 1),
        ]);
        JointsRig.Spin(w, b1, JointsRig.JointPoint, new Vector3(40, 10, 0));
        JointsRig.Spin(w, b2, new Vector3(0, 0.5f, 0), new Vector3(30, 0, 20));
        w.Step(JointsRig.Tick);

        var hits = w.JointLimitHits(art);
        Assert.False(hits.IsEmpty);
        bool first = false, second = false;
        int substep = 0;
        foreach (var hit in hits)
        {
            Assert.InRange(hit.Joint, 0, 1);
            Assert.InRange(hit.Substep, substep, w.Settings.Substeps - 1);   // по порядку подшагов
            substep = hit.Substep;
            Assert.Equal(1f, hit.Axis.Length(), 1e-5f);
            first |= hit.Joint == 0;
            second |= hit.Joint == 1;
        }
        Assert.True(first && second, $"сработали: сустав 0 — {first}, сустав 1 — {second}");
        Assert.True(hits.Length <= 2 * 2 * w.Settings.Substeps, $"срабатываний {hits.Length}");

        // Следующий шаг без нарушений — журнал пуст (очищается в начале шага).
        w.Place(b1, new Vector3(0, 0.35f, 0), Quaternion.Identity);   // скорости — 0
        w.Place(b2, new Vector3(0, 0.65f, 0), Quaternion.Identity);
        w.Step(JointsRig.Tick);
        Assert.True(w.JointLimitHits(art).IsEmpty);
    }

    [Fact]
    public void LimitHits_MoreSubstepsByKnob_LogGrows_HoldsEverySubstep()
    {
        // Ребёнок отведён на 1,2 рад, предел ±0,1 от позы привязки: гравитация прижимает его к пределу — срабатывание в каждом
        // подшаге. Журнал заведён на 4 подшага; ручка ставит 16 — журнал дорастает и держит все.
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated, gravity: 9.81f);
        JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f));
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.1f, 0.1f, 0.1f, 0.1f, 0)]);
        for (int t = 0; t < 30; t++) w.Step(JointsRig.Tick);
        Assert.Equal(4, w.JointLimitHits(art).Length);
        w.Settings = w.Settings with { Substeps = 16 };
        w.Step(JointsRig.Tick);
        var hits = w.JointLimitHits(art).ToArray();
        Assert.Equal(Enumerable.Range(0, 16), hits.Select(h => h.Substep));
        Assert.All(hits, h => Assert.Equal(0, h.Joint));
    }

    [Fact]
    public void RelaxPoint_KillsRelativeAnchorVelocity_NoEnergyGain_MomentumKept()
    {
        // Шаг из одного подшага, в котором до расслабления действует только интеграция: сустав привязан по позе после неё
        // (BindAfterIntegration), точки сведены, пределы широкие — проход суставов ничего не делает. Вход расслабления — скорости
        // после обновления по сдвигу (BeforeRelax: случайные скорости тел), выход — скорости после шага.
        var rng = new Random(31);
        float worstResidual = 0, worstGain = float.MinValue, worstMomentum = 0;
        for (int run = 0; run < 400; run++)
        {
            var w = OneSubstep();
            var art = w.CreateArticulation(2);
            var p = w.Body(art, 0);
            var c = w.Body(art, 1);
            var parent = RandomCapsule(rng, RandomVector(rng, 0.3f));
            var child = RandomCapsule(rng, RandomVector(rng, 0.3f) + new Vector3(0, 0.4f, 0));
            var anchor = RandomVector(rng, 0.3f);
            (parent.Velocity, parent.AngularVelocity) = (RandomVector(rng, 3), RandomVector(rng, 15));
            (child.Velocity, child.AngularVelocity) = (RandomVector(rng, 3), RandomVector(rng, 15));
            BindAfterIntegration(w, art, parent, child, JointDesc.SwingTwist(0, 1, anchor, -3, 3, 3, 3), Quaternion.Identity);
            w.Step(H);
            Assert.True(w.JointLimitHits(art).IsEmpty);

            var (after0, after1) = (w.Get(p), w.Get(c));
            w.Set(p, BeforeRelax(w, after0));
            w.Set(c, BeforeRelax(w, after1));
            float e0 = JointsRig.Energy(w, art, 0);
            var m0 = JointsRig.Momentum(w, art);
            float dv0 = RelativeAnchorSpeed(w, art);
            w.Set(p, after0);
            w.Set(c, after1);
            float dv1 = RelativeAnchorSpeed(w, art);
            float e1 = JointsRig.Energy(w, art, 0);
            worstResidual = MathF.Max(worstResidual, dv1 / MathF.Max(dv0, 1e-6f));
            worstGain = MathF.Max(worstGain, (e1 - e0) / e0);
            worstMomentum = MathF.Max(worstMomentum, (JointsRig.Momentum(w, art) - m0).Length() / MathF.Max(m0.Length(), 1));
        }
        output.WriteLine($"остаток {worstResidual:G3}, рост энергии {worstGain:G3}, импульс {worstMomentum:G3}");
        Assert.True(worstResidual <= RelaxResidual, $"относительная скорость точек осталась: доля {worstResidual}");
        Assert.True(worstGain <= 1e-5f, $"энергия выросла: доля {worstGain}");
        Assert.True(worstMomentum <= RelaxMomentum, $"импульс изменился: доля {worstMomentum}");
    }

    [Fact]
    public void RelaxPoint_KinematicParentMoving_ChildAnchorTakesItsVelocity()
    {
        var (w, art, p, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -1, 1, 1, 1)]);
        w.SetKinematicTarget(p, new Vector3(0.01f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.02f), JointsRig.Tick);
        var parent = w.Get(p);
        JointsRig.SetVelocity(w, c, new Vector3(0, 0, 1), new Vector3(3, 0, 0));
        w.Step(JointsRig.Tick);   // расслабление — последний этап подшага
        Assert.Equal(parent, w.Get(p));
        Assert.True(RelativeAnchorSpeed(w, art) <= 1e-4f);
    }

    [Theory]
    [InlineData(0.25f, -3f, true)]    // за пределом, поправка возвращает к нему: скорость «от предела» (вдоль поправки) — гасится
    [InlineData(0.19f, 12f, false)]   // подошёл к пределу изнутри и встал на нём: скорость «к пределу» — остаётся
    public void RelaxLimit_KillsAngularVelocityAwayFromLimit_Only(float twist, float spin, bool killed)
    {
        // Скручивание: ребёнок повёрнут вокруг своей оси на twist и крутится со скоростью spin; шаг из одного подшага доводит
        // скручивание до предела 0,2 — поправка вокруг оси капсулы (−Y), точка сустава на оси, её скорость — 0. На входе
        // расслабления (BeforeRelax) угловая скорость — сдвиг подшага / h: (0,2 − twist) / h. «От предела» (вдоль поправки,
        // u > 0) расслабление гасит, «к пределу» (u < 0) — не трогает.
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        w.Settings = w.Settings with { Substeps = 1 };
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -1, 1, 1, 0.2f, 0)]);
        JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(Vector3.UnitY, twist));
        JointsRig.SetVelocity(w, c, Vector3.Zero, new Vector3(0, spin, 0));
        w.Step(H);
        var hit = Assert.Single(w.JointLimitHits(art).ToArray());
        Assert.Equal(new JointLimitHit(0, 0, -Vector3.UnitY), hit with { Axis = Round(hit.Axis) });
        Assert.Equal(0.2f, w.MeasureJoint(art, 0).Twist, 1e-4f);

        var after = w.Get(c);
        var before = BeforeRelax(w, after);
        float u = Vector3.Dot(before.AngularVelocity, hit.Axis);
        Assert.Equal((0.2f - twist) / H, before.AngularVelocity.Y, 0.01f * MathF.Abs((0.2f - twist) / H));
        Assert.Equal(killed, u > 0);
        w.Set(c, before);
        float e0 = JointsRig.Energy(w, art, 0);
        w.Set(c, after);
        if (killed) Assert.True(after.AngularVelocity.Length() <= 1e-4f, $"отскок остался: {after.AngularVelocity}");
        else Assert.Equal(before.AngularVelocity, after.AngularVelocity);
        Assert.True(JointsRig.Energy(w, art, 0) <= e0);
    }

    [Fact]
    public void RelaxLimit_RandomPairs_KillsReboundWithAnchorVelocity_NoEnergyGain()
    {
        // Случайные пары на физике со случайными скоростями, шаг из одного подшага: после интеграции наклон ребёнка — за тесным
        // эллипсом на 1–10 мрад (BindAfterIntegration), предел срабатывает, поправка мала (сравнима со случайными скоростями).
        // На входе расслабления (BeforeRelax) — случайные скорости и отскок от поправки; расслабление гасит отскок (u ≤ 0 вдоль
        // оси срабатывания) и относительную скорость точек вместе, энергия не растёт.
        var rng = new Random(51);
        float worstU = 0, worstAnchor = 0, worstGain = float.MinValue;
        int killed = 0;
        for (int run = 0; run < 400; run++)
        {
            var w = OneSubstep();
            var art = w.CreateArticulation(2);
            var p = w.Body(art, 0);
            var c = w.Body(art, 1);
            var parent = RandomCapsule(rng, RandomVector(rng, 0.3f));
            var child = RandomCapsule(rng, RandomVector(rng, 0.3f) + new Vector3(0, 0.4f, 0));
            var anchor = RandomVector(rng, 0.3f);
            (parent.Velocity, parent.AngularVelocity) = (RandomVector(rng, 3), RandomVector(rng, 15));
            (child.Velocity, child.AngularVelocity) = (RandomVector(rng, 3), RandomVector(rng, 15));
            // Только наклон за тесный эллипс (ось отклонения — в плоскости XZ ребёнка, скручивание широкое): одно срабатывание.
            float heading = rng.NextSingle() * MathF.Tau;
            var turn = Quaternion.CreateFromAxisAngle(new Vector3(MathF.Cos(heading), 0, MathF.Sin(heading)), 0.051f + rng.NextSingle() * 0.009f);
            BindAfterIntegration(w, art, parent, child, JointDesc.SwingTwist(0, 1, anchor, -0.05f, 0.05f, 0.05f, 3, 0), turn);
            w.Step(H);
            var hit = Assert.Single(w.JointLimitHits(art).ToArray());

            var (after0, after1) = (w.Get(p), w.Get(c));
            w.Set(p, BeforeRelax(w, after0));
            w.Set(c, BeforeRelax(w, after1));
            float e0 = JointsRig.Energy(w, art, 0), dv0 = RelativeAnchorSpeed(w, art);
            w.Set(p, after0);
            w.Set(c, after1);
            float e1 = JointsRig.Energy(w, art, 0);
            float u = Vector3.Dot(after1.AngularVelocity - after0.AngularVelocity, hit.Axis);
            if (MathF.Abs(u) < 1e-3f) killed++;
            worstU = MathF.Max(worstU, u);
            worstAnchor = MathF.Max(worstAnchor, RelativeAnchorSpeed(w, art) / MathF.Max(dv0, 1e-3f));
            worstGain = MathF.Max(worstGain, (e1 - e0) / e0);
        }
        output.WriteLine($"u после ≤ {worstU:G3} рад/с (погашено {killed} из 400), остаток скорости точек — доля {worstAnchor:G3}, рост энергии {worstGain:G3}");
        Assert.True(worstU <= ReboundU, $"отскок остался: u = {worstU}");
        Assert.True(worstAnchor <= ReboundAnchor, $"точки сустава разъезжаются: доля {worstAnchor}");
        Assert.True(worstGain <= 1e-5f, $"энергия выросла: доля {worstGain}");
        Assert.InRange(killed, 100, 400);   // отскок был примерно в половине случаев
    }

    [Theory]
    [InlineData(JointKind.Hinge, 2.5f)]
    [InlineData(JointKind.Hinge, 0.3f)]
    [InlineData(JointKind.SwingTwist, 2.5f)]
    [InlineData(JointKind.SwingTwist, 0.3f)]
    public void HangingChain_HoldsJointPointsTogether_GainsNoEnergy(JointKind kind, float limit)
    {
        // Цепочка: кинематический крюк и 4 капсулы на физике, суставы — концы капсул; без земли, без демпфирования. Цепочка
        // отведена на 1 рад и отпущена с толчком поперёк: качается в пространстве, при узких пределах — упирается в них.
        const float g = 9.81f, half = 0.15f;
        var w = JointsRig.World(g);
        var art = w.CreateArticulation(5);
        var hook = new Vector3(0, 3, 0);
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1);
        var down = Vector3.Transform(-Vector3.UnitY, tilt);
        // Крюк — продолжение цепочки: при привязке все сгибы — 0 (шарнир мерит сгиб от выпрямленного положения).
        JointsRig.Capsule(w, art, 0, 5, 0.05f, 0.1f, hook - down * 0.1f, tilt, BodyMode.Kinematic);
        var joints = new JointDesc[4];
        var top = new Vector3(0, half, 0);   // точка сустава — верхний конец ребёнка
        for (int i = 1; i < 5; i++)
        {
            JointsRig.Capsule(w, art, i, 1 + i, 0.05f, half, hook + down * (half * (2 * i - 1)), tilt, BodyMode.Simulated);
            joints[i - 1] = kind == JointKind.Hinge
                ? JointDesc.Hinge(i - 1, i, top, -limit, limit, 0.1f, 1) with { HingeAxis = Vector3.UnitZ }
                : JointDesc.SwingTwist(i - 1, i, top, -limit, limit, limit, limit, 0);
        }
        w.SetJoints(art, joints);
        JointsRig.SetVelocity(w, w.Body(art, 4), new Vector3(0, 0, 1.5f), Vector3.Zero);

        float e0 = JointsRig.Energy(w, art, g), worstGain = 0, worstPoint = 0, worstExcess = 0, lowest = e0;
        int hits = 0;
        for (int t = 0; t < 600; t++)
        {
            w.Step(JointsRig.Tick);
            float e = JointsRig.Energy(w, art, g);
            worstGain = MathF.Max(worstGain, e - e0);
            lowest = MathF.Min(lowest, e);
            var (point, excess) = JointsRig.Worst(w, art);
            worstPoint = MathF.Max(worstPoint, point);
            worstExcess = MathF.Max(worstExcess, excess);
            hits += w.JointLimitHits(art).Length;
        }
        float scale = 14 * g * 1.2f;   // масса цепочки · g · её длина — масштаб энергии, Дж
        output.WriteLine($"{kind} {limit}: E0 {e0:F3}, рост {worstGain:G3} Дж, наименьшая {lowest:F3}, точка {worstPoint:G3}, выход {worstExcess:G3}, срабатываний {hits}");
        Assert.True(worstGain <= 1e-3f * scale, $"цепочка набрала энергию: {worstGain} Дж");
        Assert.True(worstPoint <= ChainPoint, $"точки суставов разошлись: {worstPoint}");
        Assert.True(worstExcess <= ChainExcess, $"выход за предел {worstExcess}");
        if (limit < 1) Assert.True(hits > 0);
    }

    [Fact]
    public void FallingFighter_RandomKicks_JointsHold_NoEnergyGain()
    {
        const float g = 9.81f;
        var rng = new Random(41);
        float worstGain = 0, worstPoint = 0, worstExcess = 0;
        for (int run = 0; run < 10; run++)
        {
            var w = JointsRig.World(g);
            var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
            w.SetJoints(art, JointsRig.FighterJoints(w, art));
            for (int i = 0; i < 14; i++) JointsRig.SetVelocity(w, w.Body(art, i), RandomVector(rng, 3), RandomVector(rng, 15));
            // Скорости тел несогласованы — первый шаг их согласует (энергия падает); рост считается от конца первого шага.
            w.Step(JointsRig.Tick);
            float e0 = JointsRig.Energy(w, art, g);
            for (int t = 1; t < 240; t++)
            {
                w.Step(JointsRig.Tick);
                worstGain = MathF.Max(worstGain, (JointsRig.Energy(w, art, g) - e0) / MathF.Abs(e0));
                var (point, excess) = JointsRig.Worst(w, art);
                worstPoint = MathF.Max(worstPoint, point);
                worstExcess = MathF.Max(worstExcess, excess);
            }
        }
        output.WriteLine($"рост энергии {worstGain:G3}, точка {worstPoint:G3} м, выход {worstExcess:G3} рад");
        Assert.True(worstGain <= 1e-3f, $"боец набрал энергию: доля {worstGain}");
        Assert.True(worstPoint <= FighterPoint, $"точки суставов разошлись: {worstPoint}");
        Assert.True(worstExcess <= FighterExcess, $"выход за предел {worstExcess}");
    }

    // Долгие прогоны (10 с): боец без земли и гравитации, случайные толчки. Суставы — связи без работы, пределы — неупругие:
    // энергия может только убывать. Предел, поправляемый не вдоль градиента своей меры, совершает работу по замкнутому пути
    // (коническое движение руки) — так было до поправки скручивания и наклона вдоль градиента (README «7в → Исправлено»: рост в
    // 4 из 20 прогонов, до ×13; у руки «плечо — локоть — клинок» — в 29 из 40).

    [Fact]
    public void FreeFighter_10s_NoGravityNoDamping_NoEnergyGain()
    {
        var (grew, gain, excess, point) = LongRun(seed: 1015, runs: 20, omega: 15, damped: false, keep: null);
        output.WriteLine($"вакуум, ≤ 15 рад/с: рост > 0,1 % в {grew} из 20, наибольший — доля {gain:G3}; выход ≤ {excess:G3} рад, точка ≤ {point:G3} м");
        Assert.True(gain <= 1e-3f, $"боец набрал энергию: в {grew} из 20 прогонов, до доли {gain}");
        Assert.True(excess <= LongExcess, $"выход за предел {excess}");
    }

    [Fact]
    public void FreeFighter_10s_DefaultDamping_Kicks25_NoEnergyGain()
    {
        var (grew, gain, excess, point) = LongRun(seed: 1025, runs: 20, omega: 25, damped: true, keep: null);
        output.WriteLine($"демпфирование профиля, ≤ 25 рад/с: рост > 0,1 % в {grew} из 20, наибольший — доля {gain:G3}; выход ≤ {excess:G3} рад, точка ≤ {point:G3} м");
        Assert.True(gain <= 1e-3f, $"боец набрал энергию: в {grew} из 20 прогонов, до доли {gain}");
        Assert.True(excess <= LongExcess, $"выход за предел {excess}");
    }

    [Fact]
    public void ArmChain_ShoulderElbowBlade_10s_NoEnergyGain()
    {
        // Рука: плечо (наклон 2,6 — у предела скручивания градиент дальше всего от оси кости), локоть, клинок; остальные суставы
        // выключены (тела летят свободно, их энергия постоянна).
        var (grew, gain, excess, point) = LongRun(seed: 1015, runs: 20, omega: 15, damped: false, keep: [4, 5, 10]);
        output.WriteLine($"рука, вакуум, ≤ 15 рад/с: рост > 0,1 % в {grew} из 20, наибольший — доля {gain:G3}; выход ≤ {excess:G3} рад, точка ≤ {point:G3} м");
        Assert.True(gain <= 1e-3f, $"рука набрала энергию: в {grew} из 20 прогонов, до доли {gain}");
        Assert.True(excess <= LongExcess, $"выход за предел {excess}");
    }

    private static (int Grew, float Gain, float Excess, float Point) LongRun(int seed, int runs, float omega, bool damped, int[]? keep)
    {
        var rng = new Random(seed);
        float worstGain = float.MinValue, worstExcess = 0, worstPoint = 0;
        int grew = 0;
        for (int run = 0; run < runs; run++)
        {
            var w = JointsRig.World();
            if (damped) w.ReplaceDynamics(0, new BodyDynamics { Gravity = 0 });
            var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
            w.SetJoints(art, JointsRig.FighterJoints(w, art));
            if (keep is not null)
                for (int j = 0; j < w.JointCount(art); j++) w.SetJointEnabled(art, j, keep.Contains(j));
            for (int i = 0; i < 14; i++) JointsRig.SetVelocity(w, w.Body(art, i), RandomVector(rng, 3), RandomVector(rng, omega));
            w.Step(JointsRig.Tick);   // первый шаг согласует скорости тел (энергия падает)
            float e0 = JointsRig.Energy(w, art, 0), gain = float.MinValue;
            for (int t = 1; t < 1200; t++)
            {
                w.Step(JointsRig.Tick);
                gain = MathF.Max(gain, (JointsRig.Energy(w, art, 0) - e0) / e0);
                if ((t & 3) == 0)
                {
                    var (p, e) = JointsRig.Worst(w, art);
                    (worstPoint, worstExcess) = (MathF.Max(worstPoint, p), MathF.Max(worstExcess, e));
                }
            }
            if (gain > 1e-3f) grew++;
            worstGain = MathF.Max(worstGain, gain);
        }
        return (grew, worstGain, worstExcess, worstPoint);
    }

    [Fact]
    public void SteadyState_33Corpses_Substeps_AndRebinding_DoNotAllocate()
    {
        const int fighters = 33;
        var w = JointsRig.World(9.81f);
        var arts = new ArticulationHandle[fighters];
        var joints = new JointDesc[fighters][];
        for (int f = 0; f < fighters; f++)
        {
            arts[f] = JointsRig.Fighter(w, new Vector3(f * 2, 0, 0), (f & 1) == 0 ? BodyMode.Simulated : BodyMode.Kinematic);
            joints[f] = JointsRig.FighterJoints(w, arts[f]);
            w.SetJoints(arts[f], joints[f]);
        }
        float sink = 0;
        void Tick(int t)
        {
            w.Step(JointsRig.Tick);
            int k = t % fighters;
            // Смерть по позе: новые описания и перепривязка; меч выпал; замер и журнал — наблюдателям.
            w.SetJoints(arts[k], joints[k]);
            w.SetJointEnabled(arts[k], 10, (t & 2) == 0);
            w.RebindJoints(arts[(k + 1) % fighters]);
            sink += w.MeasureJoint(arts[k], t % 13).Excess + w.JointLimitHits(arts[k]).Length + w.Binding(arts[k], 0).Rest.W;
        }
        for (int t = 0; t < 50; t++) Tick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 50; t < 250; t++) Tick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"200 тиков 33 × 13 суставов (половина — на физике) выделили {allocated} Б");
        Assert.True(float.IsFinite(sink));
    }

    /// <summary>
    /// 7з: место под суставы дерева (тел − 1) и журнал — за слотом с создания сочленения: первый <c>SetJoints</c> бойца посреди игры (первая
    /// смерть на этом слоте) и шаги с его суставами на физике не выделяют памяти; суставов у созданного сочленения по-прежнему нет.
    /// </summary>
    [Fact]
    public void FirstSetJoints_OfTree_AfterCreation_DoesNotAllocate()
    {
        var w = JointsRig.World(9.81f);
        var warm = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
        w.SetJoints(warm, JointsRig.FighterJoints(w, warm));   // прогрев JIT путей SetJoints и шага с суставами
        for (int t = 0; t < 5; t++) w.Step(JointsRig.Tick);
        var fresh = JointsRig.Fighter(w, new Vector3(3, 0, 0), BodyMode.Simulated);
        Assert.Equal(0, w.JointCount(fresh));
        var joints = JointsRig.FighterJoints(w, fresh);
        long before = GC.GetAllocatedBytesForCurrentThread();
        w.SetJoints(fresh, joints);
        for (int t = 0; t < 5; t++) w.Step(JointsRig.Tick);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"первый SetJoints 13 суставов нового сочленения и 5 шагов выделили {allocated} Б");
        Assert.Equal(joints.Length, w.JointCount(fresh));
    }

    // Допуски — по своим замерам (≈ 2× к худшему, как бюджеты этапов 4–6; замеры — в выводе тестов и README «7в»).

    /// <summary>
    /// Заход за жёсткий упор, рад: подход 5–6 рад/с по одной оси — замер ≤ 0,008, ось шарнира без люфта при подходе по двум
    /// осям (2,8 рад/с) — ≤ 0,024; гаснет за 2–3 тика до 1e-7.
    /// </summary>
    private const float StopOvershoot = 0.05f;

    /// <summary>
    /// Откат от жёсткого упора после захода, рад: подход 6 рад/с — замер меньше 1e-5 (у схемы «точка, затем предел» и расслабления
    /// только угловой скорости сгиб падал от упора 2,5 до 0,97).
    /// </summary>
    private const float StopRebound = 1e-3f;

    /// <summary>Ошибка точки сустава у пары с неподвижным родителем, м (замер ≤ 1e-5).</summary>
    private const float PairPoint = 2e-5f;

    /// <summary>
    /// Расслабление точек (400 случайных пар): остаток относительной скорости — доля (замер 4,8e-5; до сведения, отдельным
    /// проходом без шага, — 2,95e-5), изменение импульса — доля (замер 1,9e-7). Погрешность float решения 3×3, не физика.
    /// </summary>
    private const float RelaxResidual = 1e-4f, RelaxMomentum = 1e-6f;

    /// <summary>
    /// Отскок от предела (400 случайных пар): u после, рад/с (замер 2,4e-5), остаток скорости точек — доля (замер 5,6e-5; до
    /// сведения, отдельным проходом при выходе 0,15–0,45 рад, — 3,04e-4).
    /// </summary>
    private const float ReboundU = 1e-4f, ReboundAnchor = 6e-4f;

    /// <summary>Пары на физике со случайным вращением до 20 рад/с: выход, рад (замер ≤ 0,021), и ошибка точки, м (≤ 8,3e-4).</summary>
    private const float RandomExcess = 0.04f, RandomPoint = 2e-3f;

    /// <summary>Мягкий предел по умолчанию (2e-4) под гравитацией и вращением 9,4 рад/с, рад (замер ≤ 0,0275; в покое у упора — 0,0026).</summary>
    private const float SoftExcess = 0.06f;

    /// <summary>Родитель — живая кость (0,9 м/с по кругу, поворот 3 рад/с): ошибка точки, м (замер ≤ 6e-4), выход, рад (≤ 0,0055).</summary>
    private const float KinematicPoint = 1.5e-3f, KinematicExcess = 0.015f;

    /// <summary>
    /// Цепочка из 4 капсул с жёсткими пределами, тяжёлый конец (одна итерация Гаусса — Зейделя: сустав у корня возмущают
    /// поправки ниже по цепочке): ошибка точки, м (замер ≤ 0,0099), выход, рад (≤ 0,089).
    /// </summary>
    private const float ChainPoint = 0.02f, ChainExcess = 0.2f;

    /// <summary>
    /// Падающий боец с пределами по умолчанию (наклон-скручивание мягкий): ошибка точки, м (замер ≤ 0,0033), выход, рад (≤ 0,132; до
    /// поправки вдоль градиента — 0,198).
    /// </summary>
    private const float FighterPoint = 7e-3f, FighterExcess = 0.3f;

    /// <summary>
    /// Долгий прогон (10 с, пределы по умолчанию): выход, рад — замер ≤ 0,144 (рука), ≤ 0,143 (толчки до 25 рад/с с демпфированием);
    /// до поправки вдоль градиента — до 1,01.
    /// </summary>
    private const float LongExcess = 0.3f;

    private static float RelativeAnchorSpeed(PhysicsWorld w, ArticulationHandle art)
    {
        var d = w.Joint(art, 0);
        var p = w.Get(w.Body(art, d.Parent));
        var c = w.Get(w.Body(art, d.Child));
        var pc = c.ToWorld(d.ChildAnchor);
        var pp = p.ToWorld(w.Binding(art, 0).ParentAnchor);
        return (c.PointVelocity(pc) - p.PointVelocity(pp)).Length();
    }

    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 4), MathF.Round(v.Y, 4), MathF.Round(v.Z, 4));

    // ——— Шаг из одного подшага: проход суставов и расслабление по отдельности через настоящий Step ———

    /// <summary>Мир суставов (<see cref="JointsRig.World"/>, профиль 0 — вакуум) с одним подшагом: <c>Step(H)</c> — ровно один подшаг h = H.</summary>
    private static PhysicsWorld OneSubstep(PhysicsSettings? settings = null)
    {
        var w = JointsRig.World();
        w.Settings = (settings ?? w.Settings) with { Substeps = 1 };
        return w;
    }

    /// <summary>
    /// Сустав <paramref name="joint"/> — с привязкой по позе после интеграции подшага: тела ставятся в позу, в которую их приведёт
    /// интеграция шага (<see cref="Dynamics.Integrate"/> — та же функция, профиль и h), ребёнок ещё повёрнут на
    /// <paramref name="turn"/>⁻¹ вокруг точки сустава; привязка; затем тела — в исходную позу со скоростями. <c>Step(H)</c> мира
    /// <see cref="OneSubstep"/> приводит их в позу привязки: точка сустава сведена (проход суставов её не двигает), отклонение
    /// ребёнка от позы покоя — ровно <paramref name="turn"/> (предел срабатывает только на нём).
    /// </summary>
    private static void BindAfterIntegration(PhysicsWorld w, ArticulationHandle art, BodyState parent, BodyState child, JointDesc joint, Quaternion turn)
    {
        var profile = w.GetDynamics(0);
        BodyState p1 = parent, c1 = child;
        Dynamics.Integrate(ref p1, profile, H);
        Dynamics.Integrate(ref c1, profile, H);
        var bound = c1;
        if (turn != Quaternion.Identity)
        {
            bound.Rotation = Quaternion.Normalize(c1.Rotation * Quaternion.Conjugate(turn));
            bound.Position = c1.ToWorld(joint.ChildAnchor) - Vector3.Transform(joint.ChildAnchor, bound.Rotation);
        }
        var (pb, cb) = (w.Body(art, joint.Parent), w.Body(art, joint.Child));
        w.Set(pb, p1);
        w.Set(cb, bound);
        w.SetJoints(art, [joint]);
        w.Set(pb, parent);
        w.Set(cb, child);
    }

    /// <summary>
    /// Состояние тела на входе расслабления последнего подшага шага <see cref="OneSubstep"/> без контактов: обновление скоростей
    /// по сдвигу подшага (<see cref="Dynamics.UpdateVelocity"/>) из итоговой позы — расслабление меняет только скорости, поза и
    /// прошлая поза после шага те же, что у обновления скоростей.
    /// </summary>
    private static BodyState BeforeRelax(PhysicsWorld w, BodyState after)
    {
        if (after.Mode == BodyMode.Simulated) Dynamics.UpdateVelocity(ref after, w.GetDynamics(after.Profile), H);
        return after;
    }
}
