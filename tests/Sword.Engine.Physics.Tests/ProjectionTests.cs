using System.Numerics;
using static Sword.Engine.Physics.Tests.TestBodies;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Проекции XPBD — свойства: <c>Attach</c> сводит точки и делит поправку по обратным массам, податливость смягчает,
/// <c>Contact</c> односторонний и λ ≥ 0 (NaN и ≤ 0 предела — без поправки), трение покоя держит малый сдвиг и отпускает
/// сверх Кулона, <c>Angular</c> поворачивает к цели, мир — как тело бесконечной массы, поправка пары не меняет импульс,
/// проекции не добавляют энергии.
/// </summary>
public class ProjectionTests
{
    /// <summary>Неподвижная сторона бесконечной массы — телом (обратные масса и инерция — 0).</summary>
    private static BodyState Anchor(Vector3 position = default) => new()
    {
        Position = position, PrevPosition = position, StartPosition = position,
        Rotation = Quaternion.Identity, PrevRotation = Quaternion.Identity, StartRotation = Quaternion.Identity,
        Mode = BodyMode.Kinematic,
    };

    [Fact]
    public void Attach_ToWorld_PointAtCenterLandsOnAnchor_WithLeverConverges()
    {
        var b = Capsule(2, 0.05f, 0.2f, new Vector3(0.1f, 1, 0));
        var anchor = new Vector3(0.4f, 0.8f, 0.1f);
        float gap = Vector3.Distance(b.Position, anchor);
        float lambda = Xpbd.Attach(ref b, b.Position, anchor, 0, H);
        Assert.Equal(anchor, b.Position, new Vector3Comparer(1e-6f));
        Assert.Equal(Quaternion.Identity, b.Rotation);
        Assert.Equal(-gap * b.Mass, lambda, 4);   // λ = −c / w, w = 1/m

        // Точка на конце оси: одна проекция закрывает разрыв вдоль его направления (остаток — второго порядка), но поворот
        // уводит точку и поперёк (плечо); каждая следующая проекция сближает, несколько сводят.
        var local = new Vector3(0, -0.2f, 0);
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        var c = Capsule(2, 0.05f, 0.2f, rotation: tilt);
        var near = c.ToWorld(local) + new Vector3(0.003f, -0.002f, 0.002f);
        var gapNear = c.ToWorld(local) - near;
        Xpbd.Attach(ref c, c.ToWorld(local), near, 0, H);
        float along = Vector3.Dot(c.ToWorld(local) - near, Vector3.Normalize(gapNear));
        Assert.True(MathF.Abs(along) < 0.05f * gapNear.Length(), $"вдоль разрыва осталось {along} из {gapNear.Length()}");

        var d = Capsule(2, 0.05f, 0.2f, rotation: tilt);
        var far = new Vector3(0.1f, -0.25f, 0.05f);
        float left = Vector3.Distance(d.ToWorld(local), far);
        for (int i = 0; i < 20; i++)
        {
            Xpbd.Attach(ref d, d.ToWorld(local), far, 0, H);
            float now = Vector3.Distance(d.ToWorld(local), far);
            Assert.True(now < left || now < 1e-5f, $"проекция {i}: {now} ≥ {left}");
            left = now;
        }
        Assert.True(left < 1e-4f, $"после 20 проекций {left} м");
    }

    [Fact]
    public void Attach_TwoBodies_PointsMeet_CorrectionSplitByInverseMass_MomentumKept()
    {
        var a = Capsule(1, 0.05f, 0.2f, new Vector3(0, 1, 0));
        var b = Capsule(3, 0.05f, 0.2f, new Vector3(0.4f, 1.2f, -0.2f));
        Vector3 a0 = a.Position, b0 = b.Position;
        Xpbd.Attach(ref a, a.Position, ref b, b.Position, 0, H);
        Assert.Equal(a.Position, b.Position, new Vector3Comparer(1e-6f));
        var da = a.Position - a0;
        var db = b.Position - b0;
        // Сдвиги — обратно массам (1 : 3 → 3 : 1), навстречу; импульс пары не меняется.
        Assert.Equal(3, da.Length() / db.Length(), 4);
        Assert.Equal(Vector3.Zero, da * a.Mass + db * b.Mass, new Vector3Comparer(1e-6f));
    }

    [Fact]
    public void Compliance_Softens_CorrectedShareIsWOverWPlusAlphaOverH2()
    {
        float gap = 0.01f;
        float previous = float.MaxValue;
        foreach (float alpha in new[] { 0f, 1e-7f, 1e-6f, 1e-5f, 1e-3f, 1e-1f })
        {
            var b = Capsule(2, 0.05f, 0.2f);
            var anchor = new Vector3(gap, 0, 0);
            Xpbd.Attach(ref b, b.Position, anchor, alpha, H);
            float w = 1f / b.Mass, tilde = alpha / (H * H);
            Assert.Equal(gap * w / (w + tilde), b.Position.X, 6);
            Assert.True(b.Position.X < previous || alpha == 0);
            previous = b.Position.X;
        }
        Assert.True(previous < 1e-3f * gap);   // очень податливое почти не тянет
    }

    [Fact]
    public void Contact_OneSided_PushesOutAlongNormal_ReturnsPositiveLambda_SignsFollowRoles()
    {
        var n = Vector3.UnitY;
        var b = Capsule(2, 0.05f, 0.2f, new Vector3(0, 0.3f, 0));
        float lambda = Xpbd.Contact(ref b, b.Position, n, -0.01f, 0, H);
        Assert.True(lambda > 0);
        Assert.Equal(0.31f, b.Position.Y, 6);
        Assert.Equal(0.01f * b.Mass, lambda, 5);

        // Тело — тело: a получает +λn, b — −λn (нормаль — от b к a), поровну при равных массах.
        var a = Capsule(2, 0.05f, 0.2f, new Vector3(0, 0.1f, 0));
        var c = Capsule(2, 0.05f, 0.2f, new Vector3(0, 0, 0));
        Assert.True(Xpbd.Contact(ref a, a.Position, ref c, c.Position, n, -0.02f, 0, H) > 0);
        Assert.Equal(0.11f, a.Position.Y, 6);
        Assert.Equal(-0.01f, c.Position.Y, 6);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.05f)]
    [InlineData(float.NaN)]
    public void Contact_NoPenetration_NoCorrection(float c)
    {
        var b = Capsule(2, 0.05f, 0.2f, new Vector3(0, 0.3f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f));
        var before = b;
        Assert.Equal(0, Xpbd.Contact(ref b, b.ToWorld(new Vector3(0, -0.2f, 0)), Vector3.UnitY, c, 0, H));
        Assert.Equal(before, b);
        var other = Capsule(1, 0.05f, 0.2f);
        var otherBefore = other;
        Assert.Equal(0, Xpbd.Contact(ref b, b.Position, ref other, other.Position, Vector3.UnitY, c, 0, H));
        Assert.Equal((before, otherBefore), (b, other));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Contact_ZeroNegativeOrNaNLimit_NoCorrection(float limit)
    {
        var b = Capsule(2, 0.05f, 0.2f, new Vector3(0, 0.3f, 0));
        var before = b;
        Assert.Equal(0, Xpbd.Contact(ref b, b.Position, Vector3.UnitY, -0.01f, 0, H, limit));
        Assert.Equal(before, b);
    }

    [Fact]
    public void Contact_Limit_CapsLambda_WithoutLimitSameAsAlong()
    {
        var b = Capsule(2, 0.05f, 0.2f);
        float lambda = Xpbd.Contact(ref b, b.Position, Vector3.UnitY, -0.01f, 0, H, maxLambda: 0.005f);
        Assert.Equal(0.005f, lambda);
        Assert.Equal(0.005f / b.Mass, b.Position.Y, 6);

        // Без предела — как двусторонняя проекция при c < 0.
        var c = Capsule(2, 0.05f, 0.2f, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f));
        var d = c;
        var p = c.ToWorld(new Vector3(0.03f, -0.2f, 0));
        Assert.Equal(Xpbd.Along(ref d, p, Vector3.UnitY, -0.01f, 1e-7f, H), Xpbd.Contact(ref c, p, Vector3.UnitY, -0.01f, 1e-7f, H));
        Assert.Equal(d, c);
    }

    [Fact]
    public void Friction_SmallSlipHeld_OverCoulombNot_NormalMotionIgnored()
    {
        var n = Vector3.UnitY;
        // Сдвиг центра на 1 мм вдоль X за подшаг: λt = сдвиг / w = 0,002 кг·м.
        BodyState Slid(Vector3 by)
        {
            var s = Capsule(2, 0.05f, 0.2f);
            s.Position += by;
            return s;
        }

        var held = Slid(new Vector3(0.001f, 0, 0));
        float lambdaT = Xpbd.Friction(ref held, held.Position, n, lambdaN: 0.01f, muStatic: 0.7f, 0, H);
        Assert.Equal(0.002f, lambdaT, 6);
        Assert.Equal(0, held.Position.X, 7);   // касательный сдвиг отменён

        var slips = Slid(new Vector3(0.001f, 0, 0));
        var before = slips;
        Assert.Equal(0, Xpbd.Friction(ref slips, slips.Position, n, lambdaN: 0.002f, muStatic: 0.7f, 0, H));   // 0,002 > 0,7 · 0,002
        Assert.Equal(before, slips);

        var normal = Slid(new Vector3(0, -0.001f, 0));
        before = normal;
        Assert.Equal(0, Xpbd.Friction(ref normal, normal.Position, n, 1, 0.7f, 0, H));
        Assert.Equal(before, normal);

        foreach (float bad in new[] { 0f, -1f, float.NaN })
        {
            var s = Slid(new Vector3(0.001f, 0, 0));
            Assert.Equal(0, Xpbd.Friction(ref s, s.Position, n, bad, 0.7f, 0, H));
            Assert.Equal(0, Xpbd.Friction(ref s, s.Position, n, 1, bad, 0, H));
        }
    }

    [Fact]
    public void Friction_RotationInPlaceIsSlip_TwoBodiesSlidingTogetherHoldNothing()
    {
        // Тело повернулось на месте: точка касания на конце оси сдвинулась — это скольжение, трение его держит.
        var b = Capsule(2, 0.05f, 0.2f);
        Dynamics.Rotate(ref b, new Vector3(0, 0, 0.01f));
        var p = b.ToWorld(new Vector3(0, -0.2f, 0));
        float slip = Vector3.Distance(p, b.PrevPoint(p));
        Assert.True(slip > 1e-3f);
        Assert.True(Xpbd.Friction(ref b, p, Vector3.UnitY, 1, 0.7f, 0, H) > 0);
        var q = b.ToWorld(new Vector3(0, -0.2f, 0));
        Assert.True(Vector3.Distance(q, b.PrevPoint(q)) < 0.1f * slip);

        // Два тела сдвинулись одинаково: относительного скольжения нет.
        var a = Capsule(2, 0.05f, 0.2f, new Vector3(0, 0.1f, 0));
        var c = Capsule(3, 0.05f, 0.2f);
        a.Position.X += 0.002f;
        c.Position.X += 0.002f;
        var (a0, c0) = (a, c);
        Assert.Equal(0, Xpbd.Friction(ref a, a.Position, ref c, c.Position, Vector3.UnitY, 1, 0.7f, 0, H));
        Assert.Equal((a0, c0), (a, c));

        // Разошлись навстречу — трение сводит относительный сдвиг к нулю, импульс пары не меняется.
        a.Position.X += 0.002f;
        c.Position.X -= 0.001f;
        var (pa0, pc0) = (a.Position, c.Position);
        Assert.True(Xpbd.Friction(ref a, a.Position, ref c, c.Position, Vector3.UnitY, 1, 0.7f, 0, H) > 0);
        Assert.Equal(a.Position.X - a.PrevPosition.X, c.Position.X - c.PrevPosition.X, 6);
        Assert.Equal(Vector3.Zero, (a.Position - pa0) * a.Mass + (c.Position - pc0) * c.Mass, new Vector3Comparer(1e-7f));
    }

    [Fact]
    public void Angular_RotatesTowardTarget_PrincipalAxisConverges_PairSplitsByInertia()
    {
        var start = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 0.5f)), 0.7f);
        var b = Capsule(2, 0.05f, 0.2f, rotation: start);
        // Цель — поворот вокруг главной оси тела (его X в мире): поворот идёт прямо к цели.
        var axis = Vector3.Transform(Vector3.UnitX, start);
        var target = Quaternion.CreateFromAxisAngle(axis, 0.3f) * start;
        Xpbd.Angular(ref b, PhysicsMath.RotationVector(b.Rotation, target), 0, H);
        Assert.True(Angle(b.Rotation, target) < 0.01f * 0.3f, $"осталось {Angle(b.Rotation, target)} рад");
        for (int i = 0; i < 5; i++) Xpbd.Angular(ref b, PhysicsMath.RotationVector(b.Rotation, target), 0, H);
        Assert.True(Angle(b.Rotation, target) < 1e-4f);

        // Одинаковая инерция по осям (шар): прямо к цели вокруг любой оси.
        var ball = Capsule(2, 0.1f, 0, rotation: start);
        var goal = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, -1, 0.8f)), 0.25f) * start;
        Xpbd.Angular(ref ball, PhysicsMath.RotationVector(ball.Rotation, goal), 0, H);
        Assert.True(Angle(ball.Rotation, goal) < 0.01f * 0.25f, $"шар: осталось {Angle(ball.Rotation, goal)} рад");

        // Разная инерция по осям: ограничение одномерное — вдоль оси поправки тело проходит ровно угол, а поперёк
        // поворачивается, как от импульса момента вокруг этой оси (I⁻¹n не параллелен n): вокруг своей оси капсула легче.
        var c = Capsule(2, 0.05f, 0.3f, rotation: start);
        var theta = PhysicsMath.RotationVector(c.Rotation, Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, -1, 0.8f)), 0.02f) * start);
        var n = Vector3.Normalize(theta);
        var before = c.Rotation;
        var along = Vector3.Normalize(c.InvInertiaWorld(n));
        Xpbd.Angular(ref c, theta, 0, H);
        var applied = PhysicsMath.RotationVector(before, c.Rotation);
        Assert.Equal(theta.Length(), Vector3.Dot(applied, n), 0.01f * theta.Length());
        Assert.Equal(along, Vector3.Normalize(applied), new Vector3Comparer(1e-3f));

        // Пара одинаковых тел: каждое проходит половину относительного поворота, навстречу.
        var p = Capsule(2, 0.05f, 0.2f);
        var q = Capsule(2, 0.05f, 0.2f);
        Xpbd.Angular(ref p, ref q, new Vector3(0, 0.1f, 0), 0, H);
        Assert.Equal(new Vector3(0, 0.05f, 0), PhysicsMath.RotationVector(Quaternion.Identity, p.Rotation), new Vector3Comparer(1e-4f));
        Assert.Equal(new Vector3(0, -0.05f, 0), PhysicsMath.RotationVector(Quaternion.Identity, q.Rotation), new Vector3Comparer(1e-4f));
    }

    [Fact]
    public void WorldOverload_SameAsBodyOfInfiniteMass_ThatNeverMoves()
    {
        var rng = new Random(3);
        for (int k = 0; k < 40; k++)
        {
            var body = RandomCapsule(rng, RandomVector(rng, 1));
            body.Position += RandomVector(rng, 0.002f);          // сдвиг за подшаг — для трения
            Dynamics.Rotate(ref body, RandomVector(rng, 0.01f));
            var anchor = Anchor(RandomVector(rng, 1));
            var anchor0 = anchor;
            var p = body.ToWorld(RandomVector(rng, 0.2f));
            var n = RandomUnit(rng);
            var theta = RandomVector(rng, 0.3f);
            float alpha = rng.NextSingle() * 1e-6f;

            var (x, y) = (body, body);
            Assert.Equal(Xpbd.Along(ref x, p, n, 0.01f, alpha, H), Xpbd.Along(ref y, p, ref anchor, anchor.Position, n, 0.01f, alpha, H));
            Assert.Equal(x, y);
            (x, y) = (body, body);
            Assert.Equal(Xpbd.Attach(ref x, p, anchor.Position, alpha, H), Xpbd.Attach(ref y, p, ref anchor, anchor.Position, alpha, H));
            Assert.Equal(x, y);
            (x, y) = (body, body);
            Assert.Equal(Xpbd.Contact(ref x, p, n, -0.01f, alpha, H), Xpbd.Contact(ref y, p, ref anchor, anchor.Position, n, -0.01f, alpha, H));
            Assert.Equal(x, y);
            (x, y) = (body, body);
            Assert.Equal(Xpbd.Friction(ref x, p, n, 1, 0.7f, alpha, H), Xpbd.Friction(ref y, p, ref anchor, anchor.Position, n, 1, 0.7f, alpha, H));
            Assert.Equal(x, y);
            (x, y) = (body, body);
            Assert.Equal(Xpbd.Angular(ref x, theta, alpha, H), Xpbd.Angular(ref y, ref anchor, theta, alpha, H));
            Assert.Equal(x, y);
            Assert.Equal(anchor0, anchor);
        }
    }

    [Fact]
    public void PairCorrections_KeepLinearMomentum_WithLevers()
    {
        var rng = new Random(5);
        for (int k = 0; k < 50; k++)
        {
            var a = RandomCapsule(rng, RandomVector(rng, 1));
            var b = RandomCapsule(rng, RandomVector(rng, 1));
            var sum0 = a.Position * a.Mass + b.Position * b.Mass;
            var pa = a.ToWorld(RandomVector(rng, 0.2f));
            var pb = b.ToWorld(RandomVector(rng, 0.2f));
            if (k % 2 == 0) Xpbd.Attach(ref a, pa, ref b, pb, 0, H);
            else Xpbd.Contact(ref a, pa, ref b, pb, RandomUnit(rng), -0.01f, 1e-7f, H);
            var sum1 = a.Position * a.Mass + b.Position * b.Mass;
            Assert.Equal(sum0, sum1, new Vector3Comparer(2e-5f * (a.Mass + b.Mass)));
        }
    }

    public enum Projection { AttachWorld, AttachPair, ContactWorld, ContactPair, FrictionWorld, FrictionPair, AngularWorld, AngularPair }

    /// <summary>
    /// Ограничение выполнено в начале подшага; тела движутся со случайными скоростями (без гравитации и демпфирования),
    /// интеграция его нарушает, проекция возвращает — и кинетическая энергия после обновления скоростей не больше, чем до
    /// подшага: проекция гасит скорость вдоль ограничения, а не добавляет (неупругий удар, e = 0).
    /// </summary>
    [Theory]
    [InlineData(Projection.AttachWorld)]
    [InlineData(Projection.AttachPair)]
    [InlineData(Projection.ContactWorld)]
    [InlineData(Projection.ContactPair)]
    [InlineData(Projection.FrictionWorld)]
    [InlineData(Projection.FrictionPair)]
    [InlineData(Projection.AngularWorld)]
    [InlineData(Projection.AngularPair)]
    public void Projections_DoNotAddEnergy(Projection kind)
    {
        var rng = new Random(100 + (int)kind);
        int checkedCases = 0;
        float worst = 0;
        for (int k = 0; k < 400; k++)
        {
            var a = RandomCapsule(rng, RandomVector(rng, 1));
            var b = RandomCapsule(rng, RandomVector(rng, 1));
            a.Velocity = RandomVector(rng, 6);
            a.AngularVelocity = RandomVector(rng, 15);
            b.Velocity = RandomVector(rng, 6);
            b.AngularVelocity = RandomVector(rng, 15);
            // Точки ограничения в начале подшага: у тела — своя точка, у пары — общая точка мира.
            var la = RandomVector(rng, a.HalfLength + a.Radius);
            var anchor = a.ToWorld(la);
            var lb = b.ToLocal(anchor);
            var n = RandomUnit(rng);
            var restA = a.Rotation;
            var restRel = Quaternion.Conjugate(b.Rotation) * a.Rotation;
            bool pair = kind is Projection.AttachPair or Projection.ContactPair or Projection.FrictionPair or Projection.AngularPair;
            bool contact = kind is Projection.ContactWorld or Projection.ContactPair or Projection.FrictionWorld or Projection.FrictionPair;
            if (contact)
            {
                // контакт: точка сближается с поверхностью (иначе проникновения нет)
                float vn = Vector3.Dot(a.PointVelocity(anchor) - (pair ? b.PointVelocity(anchor) : Vector3.Zero), n);
                if (vn > 0) n = -n;
            }
            float e0 = a.KineticEnergy + (pair ? b.KineticEnergy : 0);

            Dynamics.Integrate(ref a, Vacuum, H);
            if (pair) Dynamics.Integrate(ref b, Vacuum, H);
            var pa = a.ToWorld(la);
            var pb = pair ? b.ToWorld(lb) : anchor;
            switch (kind)
            {
                case Projection.AttachWorld: Xpbd.Attach(ref a, pa, anchor, 0, H); break;
                case Projection.AttachPair: Xpbd.Attach(ref a, pa, ref b, pb, 0, H); break;
                case Projection.ContactWorld: Xpbd.Contact(ref a, pa, n, Vector3.Dot(pa - pb, n), 0, H); break;
                case Projection.ContactPair: Xpbd.Contact(ref a, pa, ref b, pb, n, Vector3.Dot(pa - pb, n), 0, H); break;
                case Projection.FrictionWorld:
                {
                    float ln = Xpbd.Contact(ref a, pa, n, Vector3.Dot(pa - pb, n), 0, H);
                    var p = a.ToWorld(la);
                    Xpbd.Friction(ref a, p, n, ln, 1e6f, 0, H);   // трение держит всегда
                    break;
                }
                case Projection.FrictionPair:
                {
                    float ln = Xpbd.Contact(ref a, pa, ref b, pb, n, Vector3.Dot(pa - pb, n), 0, H);
                    Xpbd.Friction(ref a, a.ToWorld(la), ref b, b.ToWorld(lb), n, ln, 1e6f, 0, H);
                    break;
                }
                case Projection.AngularWorld: Xpbd.Angular(ref a, PhysicsMath.RotationVector(a.Rotation, restA), 0, H); break;
                case Projection.AngularPair: Xpbd.Angular(ref a, ref b, PhysicsMath.RotationVector(a.Rotation, b.Rotation * restRel), 0, H); break;
            }
            Dynamics.UpdateVelocity(ref a, Vacuum, H);
            if (pair) Dynamics.UpdateVelocity(ref b, Vacuum, H);
            float e1 = a.KineticEnergy + (pair ? b.KineticEnergy : 0);
            worst = MathF.Max(worst, (e1 - e0) / MathF.Max(e0, 1e-3f));
            checkedCases++;
        }
        Assert.Equal(400, checkedCases);
        Assert.True(worst <= 1e-4f, $"{kind}: энергия выросла на {worst:P3}");
    }
}
