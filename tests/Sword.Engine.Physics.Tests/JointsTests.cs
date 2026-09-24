using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Physics.Tests.TestBodies;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Суставы (7в), API: описания и их проверка (негодное — отказ без изменений), привязка по текущей позе (ошибка точки и
/// отклонение от позы покоя — ноль), перепривязка, сгиб шарнира от выпрямленного положения со знаком описания, замер наклона и
/// скручивания, включение, слот не наследует суставы, устаревший хэндл и номер сустава.
/// </summary>
public class JointsTests(ITestOutputHelper output)
{
    [Fact]
    public void SetJoints_StoresDescriptions_AllEnabled_EmptyLog()
    {
        var w = JointsRig.World();
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Kinematic);
        Assert.Equal(0, w.JointCount(art));
        var joints = JointsRig.FighterJoints(w, art);
        w.SetJoints(art, joints);
        Assert.Equal(joints.Length, w.JointCount(art));
        for (int j = 0; j < joints.Length; j++)
        {
            Assert.Equal(joints[j], w.Joint(art, j));
            Assert.True(w.IsJointEnabled(art, j));
        }
        Assert.True(w.JointLimitHits(art).IsEmpty);

        // Замена: прежние суставы уходят, новые включены.
        w.SetJointEnabled(art, 3, false);
        w.SetJoints(art, joints.AsSpan(0, 5));
        Assert.Equal(5, w.JointCount(art));
        Assert.True(w.IsJointEnabled(art, 3));
        w.SetJoints(art, []);
        Assert.Equal(0, w.JointCount(art));
    }

    [Fact]
    public void Rebind_ByCurrentPose_PointErrorAndRestDeviationZero_ForRandomPoses()
    {
        var rng = new Random(11);
        for (int run = 0; run < 50; run++)
        {
            var w = JointsRig.World();
            var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Kinematic);
            for (int i = 0; i < 14; i++) w.Place(w.Body(art, i), new Vector3(0, 1, 0) + RandomVector(rng, 3), RandomRotation(rng));
            var joints = JointsRig.FighterJoints(w, art);
            w.SetJoints(art, joints);
            for (int j = 0; j < joints.Length; j++)
            {
                var d = joints[j];
                var p = w.Get(w.Body(art, d.Parent));
                var c = w.Get(w.Body(art, d.Child));
                var m = w.MeasureJoint(art, j);
                Assert.True(m.PointError <= 1e-5f, $"прогон {run}, сустав {j}: ошибка точки {m.PointError}");
                Assert.True(m.Swing <= 1e-5f, $"прогон {run}, сустав {j}: наклон (у шарнира — уход оси) {m.Swing}");
                Assert.True(MathF.Abs(m.Twist) <= 1e-5f, $"прогон {run}, сустав {j}: скручивание {m.Twist}");
                if (d.Kind == JointKind.SwingTwist) Assert.True(m.Excess <= 1e-5f);
                else Assert.Equal(JointsRig.ExpectedBend(d, p.Rotation, c.Rotation), m.Bend, 1e-4f);

                // Привязка — точка ребёнка в системе родителя и поворот ребёнка относительно родителя.
                var binding = w.Binding(art, j);
                Assert.True(Vector3.Distance(p.ToWorld(binding.ParentAnchor), c.ToWorld(d.ChildAnchor)) <= 1e-5f);
                Assert.True(Angle(p.Rotation * binding.Rest, c.Rotation) <= 1e-5f);
                Assert.Equal(1f, binding.Rest.Length(), 1e-6f);
            }
        }
    }

    [Fact]
    public void RebindJoints_NewPose_NewBinding_DescriptionsAndFlagsKept()
    {
        var w = JointsRig.World();
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Kinematic);
        var joints = JointsRig.FighterJoints(w, art);
        w.SetJoints(art, joints);
        w.SetJointEnabled(art, 10, false);   // меч выпал
        var before = w.Binding(art, 2);

        // Ребёнка сустава 2 (плечо) сдвинули и повернули: точка разошлась, отклонение от позы покоя — есть.
        var arm = w.Body(art, joints[2].Child);
        var s = w.Get(arm);
        w.Place(arm, s.Position + new Vector3(0.05f, -0.02f, 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f) * s.Rotation);
        var m = w.MeasureJoint(art, 2);
        Assert.True(m.PointError > 0.05f && m.Swing > 0.5f);

        w.RebindJoints(art);
        m = w.MeasureJoint(art, 2);
        Assert.True(m.PointError <= 1e-5f && m.Swing <= 1e-5f && m.Excess <= 1e-5f);
        Assert.NotEqual(before, w.Binding(art, 2));
        Assert.Equal(joints[2], w.Joint(art, 2));
        Assert.False(w.IsJointEnabled(art, 10));
        Assert.True(w.IsJointEnabled(art, 9));
    }

    [Theory]
    [InlineData(1, 0.8f)]
    [InlineData(1, -0.3f)]
    [InlineData(-1, 0.8f)]
    [InlineData(-1, -0.3f)]
    [InlineData(1, 2.9f)]
    public void Hinge_BendFromStraight_TimesSignOfDescription_ExcessOutsideRange(int sign, float angle)
    {
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitZ })
        {
            // Родитель вдоль Y, ребёнок повёрнут вокруг оси шарнира на angle: сгиб = знак · angle.
            var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Kinematic);
            JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(axis, angle));
            w.SetJoints(art, [JointDesc.Hinge(0, 1, JointsRig.ChildAnchor, 0, 2.5f, 0.2f, (sbyte)sign) with { HingeAxis = axis }]);
            var m = w.MeasureJoint(art, 0);
            float bend = sign * angle;
            Assert.Equal(bend, m.Bend, 1e-5f);
            Assert.True(m.Swing <= 1e-5f);   // ось шарнира — на месте
            Assert.Equal(MathF.Max(MathF.Max(0 - bend, bend - 2.5f), 0), m.Excess, 1e-5f);
        }
    }

    [Fact]
    public void Hinge_OffAxisBeyondSlack_IsExcess()
    {
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Kinematic);
        w.SetJoints(art, [JointDesc.Hinge(0, 1, JointsRig.ChildAnchor, -1, 2.5f, 0.2f, 1)]);
        // Наклон вбок (вокруг Z) на 0,5: ось шарнира X уходит на 0,5, сгиба нет.
        JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f));
        var m = w.MeasureJoint(art, 0);
        Assert.Equal(0.5f, m.Swing, 1e-5f);
        Assert.Equal(0f, m.Bend, 1e-5f);
        Assert.Equal(0.3f, m.Excess, 1e-5f);
        // Уход оси больше 90° — угол верный (atan2, не asin).
        JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.5f));
        Assert.Equal(2.5f, w.MeasureJoint(art, 0).Swing, 1e-5f);
    }

    [Fact]
    public void SwingTwist_Measure_SwingLengthSignedTwist_EllipseAndTwistExcess()
    {
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Kinematic);
        var rest = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 0, 1)), 0.4f);
        JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, rest);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.4f, 1.0f, 0.6f, 0.5f)]);
        Assert.Equal(new JointMeasure(0, 0, 0, 0, 0), JointsRig.Round(w.MeasureJoint(art, 0)));

        void Check(Quaternion deviation, float swing, float twist, float excess)
        {
            // Отклонение от позы покоя — в системе ребёнка: child = parent · rest · deviation.
            JointsRig.PlaceAbout(w, c, JointsRig.ChildAnchor, JointsRig.JointPoint, rest * deviation);
            var m = w.MeasureJoint(art, 0);
            Assert.Equal(swing, m.Swing, 1e-4f);
            Assert.Equal(twist, m.Twist, 1e-4f);
            Assert.Equal(excess, m.Excess, 1e-4f);
            Assert.Equal(0f, m.Bend);
        }
        Check(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f), 0.8f, 0, 0);          // внутри по +X
        Check(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.3f), 1.3f, 0, 0.3f);       // за +X на 0,3
        Check(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.7f), 0.7f, 0, 0.3f);      // за −X (предел 0,4)
        Check(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.9f), 0.9f, 0, 0.3f);      // за Z (±0,6)
        Check(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.8f), 0, -0.8f, 0.3f);     // скручивание за −0,5
        // Наклон по диагонали: эллипс (0,6/1,0)² + (0,6/0,6)² > 1 — выход вдоль луча.
        var diag = Vector3.Normalize(new Vector3(1, 0, 1)) * 0.6f * MathF.Sqrt(2);
        float e = MathF.Sqrt(diag.X / 1.0f * (diag.X / 1.0f) + diag.Z / 0.6f * (diag.Z / 0.6f));
        Check(Quaternion.CreateFromAxisAngle(Vector3.Normalize(diag), diag.Length()), diag.Length(), 0, diag.Length() * (1 - 1 / e));
        // Наклон и скручивание вместе: выход — наибольший.
        Check(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f), 1.1f, 0.9f, 0.4f);
    }

    [Fact]
    public void Corrections_AreAlongGradientOfTheirMeasure_FiniteDifferences()
    {
        // Поправка предела — вдоль градиента его меры (иначе предел совершает работу по замкнутому пути). Меры — своим расчётом
        // (скручивание — atan2 по (Y, W), наклон — вектор поворота q·conj(скручивание), сгиб — угол проекций), градиент —
        // центральными разностями по левому повороту; поправка — ньютоновский шаг −C·∇C/|∇C|².
        var rng = new Random(61);
        var d = JointDesc.SwingTwist(0, 1, default, -0.5f, 1.2f, 0.4f, 0.3f);
        const float eps = 2e-3f;
        float worstSwing = 0, worstTwist = 0, worstBend = 0;
        int swings = 0, twists = 0, bends = 0;
        for (int run = 0; run < 3000; run++)
        {
            var q = RandomRotation(rng);
            if (q.W < 0) q = Quaternion.Negate(q);
            var sv = SwingVectorOf(q);
            float e = EllipseOf(d, sv);
            bool swingOut = Joints.SwingCorrection(d, q, out var swingDelta);
            Assert.Equal(e > 1, swingOut);
            // вдали от разрыва полуоси (sv.X ≈ 0) и от вырождения наклона на π
            if (swingOut && MathF.Abs(sv.X) > 0.05f && sv.Length() < 2.8f && e > 1.01f)
            {
                var expected = NewtonStep(qq => MathF.Sqrt(EllipseOf(d, SwingVectorOf(qq))) - 1, q, eps);
                worstSwing = MathF.Max(worstSwing, (swingDelta - expected).Length() / expected.Length());
                swings++;
            }
            float twist = 2 * MathF.Atan2(q.Y, q.W);
            bool twistOut = Joints.TwistCorrection(d, q, out var twistDelta);
            Assert.Equal(MathF.Abs(twist) > 0.3f, twistOut);
            if (twistOut && q.Y * q.Y + q.W * q.W > 0.05f && MathF.Abs(twist) < 3f)
            {
                float target = Math.Clamp(twist, -0.3f, 0.3f);
                var expected = NewtonStep(qq => 2 * MathF.Atan2(qq.Y, qq.W) - target, q, eps);
                worstTwist = MathF.Max(worstTwist, (twistDelta - expected).Length() / expected.Length());
                twists++;
            }

            // Сгиб шарнира: ось шарнира n — от родителя (поворот ребёнка её не меняет); сгиб — угол проекций осей капсул.
            var hinge = JointDesc.Hinge(0, 1, default, 0, 1, 0.2f, (sbyte)(rng.Next(2) * 2 - 1));
            var parent = RandomRotation(rng);
            var child = Quaternion.CreateFromAxisAngle(RandomUnit(rng), 0.3f * rng.NextSingle()) * parent;   // ось капсулы — не вдоль n
            var n = Vector3.Transform(Vector3.UnitX, parent);
            float Bend(Quaternion cc) => JointsRig.ExpectedBend(hinge with { HingeAxis = Vector3.UnitX }, parent, cc, n);
            var bendExpected = Gradient(Bend, child, eps);
            var bendGrad = Joints.BendGradient(hinge, child, n);
            worstBend = MathF.Max(worstBend, (bendGrad - bendExpected).Length() / bendExpected.Length());
            bends++;
        }
        output.WriteLine($"отклонение от шага по градиенту — доля: наклон {worstSwing:G3} ({swings}), скручивание {worstTwist:G3} ({twists}), сгиб {worstBend:G3} ({bends})");
        Assert.True(swings > 200 && twists > 200, $"мало случаев: наклон {swings}, скручивание {twists}");
        Assert.True(worstSwing <= 5e-3f, $"поправка наклона не вдоль градиента: доля {worstSwing} ({swings} случаев)");
        Assert.True(worstTwist <= 5e-3f, $"поправка скручивания не вдоль градиента: доля {worstTwist} ({twists} случаев)");
        Assert.True(worstBend <= 5e-3f, $"градиент сгиба: доля {worstBend} ({bends} случаев)");
    }

    /// <summary>Градиент f по левому повороту аргумента (центральные разности).</summary>
    private static Vector3 Gradient(Func<Quaternion, float> f, Quaternion q, float eps)
    {
        float D(Vector3 axis) => (f(Quaternion.CreateFromAxisAngle(axis, eps) * q) - f(Quaternion.CreateFromAxisAngle(axis, -eps) * q)) / (2 * eps);
        return new Vector3(D(Vector3.UnitX), D(Vector3.UnitY), D(Vector3.UnitZ));
    }

    /// <summary>Ньютоновский шаг к нулю меры: −C·∇C/|∇C|².</summary>
    private static Vector3 NewtonStep(Func<Quaternion, float> c, Quaternion q, float eps)
    {
        var g = Gradient(c, q, eps);
        return -g * (c(q) / g.LengthSquared());
    }

    /// <summary>Наклон отклонения — своим расчётом: q·conj(скручивание), вектор поворота.</summary>
    private static Vector3 SwingVectorOf(Quaternion q)
    {
        if (q.W < 0) q = Quaternion.Negate(q);
        float tl = MathF.Sqrt(q.Y * q.Y + q.W * q.W);
        var t = new Quaternion(0, q.Y / tl, 0, q.W / tl);
        return PhysicsMath.RotationVector(Quaternion.Identity, q * Quaternion.Conjugate(t));
    }

    private static float EllipseOf(in JointDesc d, Vector3 sv)
    {
        float lx = sv.X >= 0 ? d.SwingXMax : -d.SwingXMin;
        return sv.X / lx * (sv.X / lx) + sv.Z / d.SwingZ * (sv.Z / d.SwingZ);
    }

    [Fact]
    public void DisabledJoint_MeasuredTheSame_FlagPerJoint()
    {
        var w = JointsRig.World();
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Kinematic);
        w.SetJoints(art, JointsRig.FighterJoints(w, art));
        var arm = w.Body(art, 4);
        w.Place(arm, w.Get(arm).Position + new Vector3(0.1f, 0, 0), w.Get(arm).Rotation);
        var enabled = w.MeasureJoint(art, 3);
        w.SetJointEnabled(art, 3, false);
        Assert.False(w.IsJointEnabled(art, 3));
        Assert.Equal(enabled, w.MeasureJoint(art, 3));
        for (int j = 0; j < w.JointCount(art); j++) Assert.Equal(j != 3, w.IsJointEnabled(art, j));
        w.SetJointEnabled(art, 3, true);
        Assert.True(w.IsJointEnabled(art, 3));
    }

    public static TheoryData<string, JointDesc> BadJoints()
    {
        var anchor = new Vector3(0, -0.15f, 0);
        var hinge = JointDesc.Hinge(0, 1, anchor, 0, 2.5f, 0.2f, 1);
        var swing = JointDesc.SwingTwist(0, 1, anchor, -0.5f, 0.5f, 0.4f, 0.3f);
        return new TheoryData<string, JointDesc>
        {
            { "родитель вне сочленения", hinge with { Parent = 3 } },
            { "ребёнок отрицательный", hinge with { Child = -1 } },
            { "родитель = ребёнок", hinge with { Child = 0 } },
            { "вид 0", hinge with { Kind = 0 } },
            { "вид 3", hinge with { Kind = (JointKind)3 } },
            { "точка NaN", hinge with { ChildAnchor = new Vector3(float.NaN, 0, 0) } },
            { "точка ∞", swing with { ChildAnchor = new Vector3(0, float.PositiveInfinity, 0) } },
            { "BendMin > BendMax", hinge with { BendMin = 1, BendMax = 0.5f } },
            { "BendMax NaN", hinge with { BendMax = float.NaN } },
            { "люфт < 0", hinge with { AxisSlack = -0.1f } },
            { "знак 0", hinge with { BendSign = 0 } },
            { "знак 2", hinge with { BendSign = 2 } },
            { "ось не единичная", hinge with { HingeAxis = new Vector3(1, 1, 0) } },
            { "ось 0", hinge with { HingeAxis = Vector3.Zero } },
            { "ось NaN", hinge with { HingeAxis = new Vector3(float.NaN, 0, 0) } },
            { "податливость < 0", hinge with { LimitCompliance = -1e-4f } },
            { "податливость NaN", swing with { LimitCompliance = float.NaN } },
            { "SwingXMin > 0", swing with { SwingXMin = 0.1f } },
            { "SwingXMax < 0", swing with { SwingXMax = -0.1f } },
            { "SwingZ 0", swing with { SwingZ = 0 } },
            { "Twist < 0", swing with { Twist = -0.1f } },
            { "Twist ∞", swing with { Twist = float.PositiveInfinity } },
            { "у наклона — поле шарнира NaN", swing with { BendMin = float.NaN } },
        };
    }

    [Theory]
    [MemberData(nameof(BadJoints))]
    public void SetJoints_Invalid_Throws_PreviousJointsUnchanged(string why, JointDesc bad)
    {
        var (w, art, _, c) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Kinematic, bodies: 3);
        var good = JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.5f, 0.5f, 0.4f, 0.3f);
        w.SetJoints(art, [good]);
        w.SetJointEnabled(art, 0, false);
        var binding = w.Binding(art, 0);
        w.Place(c, new Vector3(5, 5, 5), Quaternion.Identity);   // перепривязки при отказе нет

        var ok = JointDesc.Hinge(1, 2, JointsRig.ChildAnchor, 0, 1, 0, 1);
        Assert.Throws<ArgumentException>(() => w.SetJoints(art, [ok, bad]));
        Assert.Equal(1, w.JointCount(art));
        Assert.Equal(good, w.Joint(art, 0));
        Assert.False(w.IsJointEnabled(art, 0), why);
        Assert.Equal(binding, w.Binding(art, 0));
    }

    [Fact]
    public void SetJoints_BodyIsChildOnce_ParentOfManyAllowed()
    {
        var (w, art, _, _) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Kinematic, bodies: 3);
        var a = JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.5f, 0.5f, 0.4f, 0.3f);
        w.SetJoints(art, [a, a with { Child = 2 }]);   // у тела 0 — два ребёнка
        Assert.Throws<ArgumentException>(() => w.SetJoints(art, [a, a with { Parent = 2 }]));   // тело 1 — ребёнок дважды
        Assert.Equal(2, w.JointCount(art));
    }

    [Fact]
    public void Slot_DoesNotInheritJoints_StaleHandleAndJointIndexThrow()
    {
        var (w, art, _, _) = JointsRig.Pair(BodyMode.Kinematic, BodyMode.Simulated);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -0.1f, 0.1f, 0.1f, 0.1f, 0)]);
        JointsRig.Spin(w, w.Body(art, 1), JointsRig.JointPoint, new Vector3(20, 0, 0));
        w.Step(JointsRig.Tick);
        Assert.False(w.JointLimitHits(art).IsEmpty);

        Assert.Throws<ArgumentOutOfRangeException>(() => w.Joint(art, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Binding(art, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.MeasureJoint(art, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetJointEnabled(art, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.IsJointEnabled(art, 5));

        w.DestroyArticulation(art);
        Assert.Throws<InvalidOperationException>(() => w.SetJoints(art, []));
        Assert.Throws<InvalidOperationException>(() => w.JointCount(art));
        Assert.Throws<InvalidOperationException>(() => w.Joint(art, 0));
        Assert.Throws<InvalidOperationException>(() => w.RebindJoints(art));
        Assert.Throws<InvalidOperationException>(() => w.SetJointEnabled(art, 0, true));
        Assert.Throws<InvalidOperationException>(() => w.IsJointEnabled(art, 0));
        Assert.Throws<InvalidOperationException>(() => w.Binding(art, 0));
        Assert.Throws<InvalidOperationException>(() => w.JointLimitHits(art));
        Assert.Throws<InvalidOperationException>(() => w.MeasureJoint(art, 0));

        var again = w.CreateArticulation(2);
        Assert.Equal(art.Index, again.Index);   // тот же слот
        Assert.Equal(0, w.JointCount(again));
        Assert.True(w.JointLimitHits(again).IsEmpty);
    }

    [Fact]
    public void Interface_ReachesJoints()
    {
        IPhysicsWorld w = JointsRig.World();
        var art = w.CreateArticulation(2);
        w.SetJoints(art, [JointDesc.Hinge(0, 1, JointsRig.ChildAnchor, 0, 2, 0.1f, -1)]);
        Assert.Equal(1, w.JointCount(art));
        Assert.Equal(-1, w.Joint(art, 0).BendSign);
    }
}

/// <summary>
/// Заготовки тестов суставов (7в): миры, пары тел, «боец» из 14 капсул с суставами (дерево и пределы — отправные значения
/// SWORD, данные теста, а не игры), энергия, ожидаемый сгиб шарнира своим расчётом.
/// </summary>
internal static class JointsRig
{
    /// <summary>Тик 120 Гц, с.</summary>
    public const float Tick = 1f / 120;

    /// <summary>Пара: родитель — капсула 4 кг вдоль Y в начале координат (полудлина 0,2), ребёнок — 2 кг (полудлина 0,15).</summary>
    public const float ParentHalf = 0.2f, ChildHalf = 0.15f;

    /// <summary>Точка сустава пары — верхний конец родителя, он же нижний конец ребёнка.</summary>
    public static readonly Vector3 JointPoint = new(0, ParentHalf, 0);

    /// <summary>Точка сустава на ребёнке пары — нижний конец оси.</summary>
    public static readonly Vector3 ChildAnchor = new(0, -ChildHalf, 0);

    /// <summary>Мир: профиль 0 — без демпфирования и трения, гравитация <paramref name="gravity"/>.</summary>
    public static PhysicsWorld World(float gravity = 0)
    {
        var w = new PhysicsWorld();
        w.ReplaceDynamics(0, Vacuum with { Gravity = gravity });
        return w;
    }

    /// <summary>
    /// Пара тел (и ещё <paramref name="bodies"/> − 2 тела выше по той же оси): родитель вдоль Y в начале координат, ребёнок —
    /// его продолжение вверх, в покое. Суставов нет.
    /// </summary>
    public static (PhysicsWorld World, ArticulationHandle Art, BodyHandle Parent, BodyHandle Child) Pair(BodyMode parent, BodyMode child,
        float gravity = 0, int bodies = 2)
    {
        var w = World(gravity);
        var art = w.CreateArticulation(bodies);
        var p = Capsule(w, art, 0, 4, 0.07f, ParentHalf, Vector3.Zero, Quaternion.Identity, parent);
        var c = Capsule(w, art, 1, 2, 0.055f, ChildHalf, JointPoint + new Vector3(0, ChildHalf, 0), Quaternion.Identity, child);
        for (int i = 2; i < bodies; i++)
            Capsule(w, art, i, 1, 0.05f, ChildHalf, JointPoint + new Vector3(0, ChildHalf * (2 * i - 1), 0), Quaternion.Identity, child);
        return (w, art, p, c);
    }

    public static BodyHandle Capsule(PhysicsWorld w, ArticulationHandle art, int index, float mass, float radius, float half, Vector3 position,
        Quaternion rotation, BodyMode mode)
    {
        var b = w.Body(art, index);
        w.SetShape(b, mass, radius, half);
        w.Place(b, position, rotation);
        w.SetMode(b, mode);
        return b;
    }

    /// <summary>Поставить тело поворотом <paramref name="rotation"/> так, чтобы его точка <paramref name="anchor"/> была в <paramref name="point"/>.</summary>
    public static void PlaceAbout(PhysicsWorld w, BodyHandle body, Vector3 anchor, Vector3 point, Quaternion rotation) =>
        w.Place(body, point - Vector3.Transform(anchor, rotation), rotation);

    public static void SetVelocity(PhysicsWorld w, BodyHandle body, Vector3 velocity, Vector3 angular)
    {
        var s = w.Get(body);
        s.Velocity = velocity;
        s.AngularVelocity = angular;
        w.Set(body, s);
    }

    /// <summary>Вращение тела с угловой скоростью <paramref name="angular"/> вокруг неподвижной точки <paramref name="pivot"/>.</summary>
    public static void Spin(PhysicsWorld w, BodyHandle body, Vector3 pivot, Vector3 angular) =>
        SetVelocity(w, body, Vector3.Cross(angular, w.Get(body).Position - pivot), angular);

    /// <summary>Энергия тел сочленения на физике, Дж: кинетическая + <c>m·g·y</c>.</summary>
    public static float Energy(PhysicsWorld w, ArticulationHandle art, float gravity)
    {
        float e = 0;
        for (int i = 0; i < w.BodyCount(art); i++)
        {
            var s = w.Get(w.Body(art, i));
            if (s.Mode == BodyMode.Simulated) e += s.KineticEnergy + s.Mass * gravity * s.Position.Y;
        }
        return e;
    }

    /// <summary>Импульс тел сочленения на физике, Н·с.</summary>
    public static Vector3 Momentum(PhysicsWorld w, ArticulationHandle art)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < w.BodyCount(art); i++)
        {
            var s = w.Get(w.Body(art, i));
            if (s.Mode == BodyMode.Simulated) sum += s.Velocity * s.Mass;
        }
        return sum;
    }

    /// <summary>Наибольшие ошибка точки и выход за предел по суставам сочленения.</summary>
    public static (float Point, float Excess) Worst(PhysicsWorld w, ArticulationHandle art)
    {
        float point = 0, excess = 0;
        for (int j = 0; j < w.JointCount(art); j++)
        {
            if (!w.IsJointEnabled(art, j)) continue;
            var m = w.MeasureJoint(art, j);
            point = MathF.Max(point, m.PointError);
            excess = MathF.Max(excess, m.Excess);
        }
        return (point, excess);
    }

    /// <summary>
    /// Ожидаемый сгиб шарнира своим расчётом (сразу после привязки: ось шарнира ребёнка = оси родителя): угол поворота оси
    /// капсулы родителя к оси капсулы ребёнка вокруг оси шарнира ребёнка — через проекции на плоскость, перпендикулярную
    /// оси, — умноженный на знак описания.
    /// </summary>
    public static float ExpectedBend(in JointDesc d, Quaternion parent, Quaternion child) =>
        ExpectedBend(d, parent, child, Vector3.Normalize(Vector3.Transform(d.HingeAxis, child)));

    /// <summary>Сгиб вокруг заданной оси шарнира <paramref name="n"/> (единичной) — своим расчётом через проекции.</summary>
    public static float ExpectedBend(in JointDesc d, Quaternion parent, Quaternion child, Vector3 n)
    {
        var a = Vector3.Transform(Vector3.UnitY, parent);
        var b = Vector3.Transform(Vector3.UnitY, child);
        var ap = Vector3.Normalize(a - n * Vector3.Dot(a, n));
        var bp = Vector3.Normalize(b - n * Vector3.Dot(b, n));
        float sin = Vector3.Dot(Vector3.Cross(ap, bp), n);
        return MathF.Atan2(sin, Vector3.Dot(ap, bp)) * d.BendSign;
    }

    /// <summary>Замер с округлёнными до 1e-5 полями (сравнение «ноль» без шума float).</summary>
    public static JointMeasure Round(JointMeasure m)
    {
        static float R(float v) => MathF.Abs(v) < 1e-5f ? 0 : v;
        return new JointMeasure(R(m.PointError), R(m.Bend), R(m.Swing), R(m.Twist), R(m.Excess));
    }

    // Боец из 14 капсул (номера — сегменты: 0 таз, 1 торс, 2 голова, 3/5 плечо Л/П, 4/6 предплечье Л/П, 7/9 бедро, 8/10
    // голень, 11 клинок, 12/13 стопа). Кость — от точки сустава (нижний конец капсулы, EndA) к дальнему концу.
    private static readonly int[] FighterParent = [-1, 0, 1, 1, 3, 1, 5, 0, 7, 0, 9, 6, 8, 10];
    private static readonly float[] FighterMass = [12, 20, 5, 2.5f, 2, 2.5f, 2, 8, 4.5f, 8, 4.5f, 1.4f, 1.2f, 1.2f];
    private static readonly float[] FighterRadius = [.15f, .17f, .13f, .065f, .055f, .065f, .055f, .09f, .07f, .09f, .07f, .03f, .045f, .045f];

    private static readonly Vector3 Grip = new(0.24f, 0.9f, 0.06f);
    private static readonly Vector3 BladeDir = Vector3.Normalize(new Vector3(0, 0.3f, 1));

    private static readonly (Vector3 From, Vector3 To)[] FighterBones =
    [
        (new(0, 0.92f, 0), new(0, 1.08f, 0)),
        (new(0, 1.08f, 0), new(0, 1.48f, 0)),
        (new(0, 1.5f, 0), new(0, 1.74f, 0)),
        (new(-0.22f, 1.44f, 0), new(-0.24f, 1.14f, 0)),
        (new(-0.24f, 1.14f, 0), new(-0.24f, 0.9f, 0.06f)),
        (new(0.22f, 1.44f, 0), new(0.24f, 1.14f, 0)),
        (new(0.24f, 1.14f, 0), Grip),
        (new(-0.1f, 0.94f, 0), new(-0.1f, 0.52f, 0.02f)),
        (new(-0.1f, 0.52f, 0.02f), new(-0.1f, 0.1f, -0.02f)),
        (new(0.1f, 0.94f, 0), new(0.1f, 0.52f, 0.02f)),
        (new(0.1f, 0.52f, 0.02f), new(0.1f, 0.1f, -0.02f)),
        (Grip - BladeDir * 0.15f, Grip + BladeDir * 1.0f),
        (new(-0.1f, 0.1f, -0.02f), new(-0.1f, 0.05f, 0.2f)),
        (new(0.1f, 0.1f, -0.02f), new(0.1f, 0.05f, 0.2f)),
    ];

    /// <summary>Боец: 14 капсул по костям в позе стойки, сдвинутых на <paramref name="origin"/>, режим — <paramref name="mode"/>.</summary>
    public static ArticulationHandle Fighter(PhysicsWorld w, Vector3 origin, BodyMode mode)
    {
        var art = w.CreateArticulation(14);
        for (int i = 0; i < 14; i++)
        {
            var (from, to) = FighterBones[i];
            var d = to - from;
            Capsule(w, art, i, FighterMass[i], FighterRadius[i], d.Length() / 2, origin + (from + to) / 2, Along(Vector3.Normalize(d)), mode);
        }
        return art;
    }

    /// <summary>Суставы бойца (13): пределы — отправные значения SWORD (данные теста); у клинка точка — хват.</summary>
    public static JointDesc[] FighterJoints(PhysicsWorld w, ArticulationHandle art)
    {
        var joints = new JointDesc[13];
        for (int s = 1; s < 14; s++)
        {
            float half = w.Get(w.Body(art, s)).HalfLength;
            var anchor = new Vector3(0, s == 11 ? -half + 0.15f : -half, 0);
            int p = FighterParent[s];
            joints[s - 1] = s switch
            {
                1 => JointDesc.SwingTwist(p, s, anchor, -0.9f, 0.35f, 0.45f, 0.6f),
                2 => JointDesc.SwingTwist(p, s, anchor, -0.8f, 0.6f, 0.5f, 1.25f),
                3 or 5 => JointDesc.SwingTwist(p, s, anchor, -2.6f, 2.6f, 2.0f, 1.3f),
                4 or 6 => JointDesc.Hinge(p, s, anchor, 0, 2.6f, 0.05f, 1),
                7 or 9 => JointDesc.SwingTwist(p, s, anchor, -0.7f, 2.0f, 0.8f, 0.6f),
                8 or 10 => JointDesc.Hinge(p, s, anchor, 0, 2.5f, 0.2f, 1),
                11 => JointDesc.SwingTwist(p, s, anchor, -1.5f, 1.5f, 1.5f, 3.0f),
                _ => JointDesc.SwingTwist(p, s, anchor, -0.8f, 0.8f, 0.35f, 0.6f),
            };
        }
        return joints;
    }

    /// <summary>Поворот, переводящий Y в единичное <paramref name="dir"/> (кратчайший; для −Y — на π вокруг Z).</summary>
    public static Quaternion Along(Vector3 dir)
    {
        float dot = Vector3.Dot(Vector3.UnitY, dir);
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        var axis = Vector3.Cross(Vector3.UnitY, dir);
        float len = axis.Length();
        return len < 1e-7f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(axis / len, MathF.Atan2(len, dot));
    }
}
