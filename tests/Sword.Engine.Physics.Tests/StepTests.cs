using System.Numerics;
using static Sword.Engine.Physics.Tests.ContactsKit;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Шаг физики (7г): проверка dt, подшаги из настроек (свободный полёт — ровно интеграция подшагов), поза начала тика у тел на
/// физике, кинематические тела не тронуты, импульс контакта за тик почти не зависит от числа подшагов, детерминизм, 0 Б на тик
/// при 33 сочленениях по 14 тел.
/// </summary>
public class StepTests
{
    [Fact]
    public void Step_ValidatesDt()
    {
        var w = World();
        foreach (float bad in new[] { 0f, -Dt, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Step(bad));
        w.Step(Dt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void FreeFlight_IsSubstepIntegration_SubstepsFromSettings(int substeps)
    {
        var w = World(PhysicsSettings.Default with { Substeps = substeps });
        var b = Simulated(w, 3, 0.07f, 0.2f, new Vector3(0, 5, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f));
        var s = w.Get(b);
        s.Velocity = new Vector3(1, 2, -0.5f);
        s.AngularVelocity = new Vector3(0.5f, 3, -1);
        w.Set(b, s);
        var expected = s;
        var profile = w.GetDynamics(0);
        float h = Dt / substeps;
        for (int t = 0; t < 30; t++)
        {
            expected.StartPosition = expected.Position;
            expected.StartRotation = expected.Rotation;
            for (int k = 0; k < substeps; k++)
            {
                Dynamics.Integrate(ref expected, profile, h);
                Dynamics.UpdateVelocity(ref expected, profile, h);
            }
            w.Step(Dt);
        }
        Assert.Equal(expected, w.Get(b));
    }

    [Fact]
    public void Step_StartPoseForSimulated_KinematicUntouched()
    {
        var w = World(ground: new FlatGround());
        w.SetWall(3);
        var sim = Simulated(w, 3, 0.07f, 0.2f, new Vector3(0, 1, 0));
        var kin = w.Body(w.CreateArticulation(1), 0);
        w.SetShape(kin, 2, 0.1f, 0.3f);
        w.Place(kin, new Vector3(5, -0.5f, 0), Quaternion.Identity);   // в земле и за стеной — кинематическое не выталкивается
        w.SetKinematicTarget(kin, new Vector3(5.1f, -0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f), Dt);
        var kinBefore = w.Get(kin);
        for (int t = 0; t < 5; t++)
        {
            var before = w.Get(sim);
            w.Step(Dt);
            var after = w.Get(sim);
            Assert.Equal((before.Position, before.Rotation), (after.StartPosition, after.StartRotation));
            Assert.NotEqual(before.Position, after.Position);
            Assert.Equal(kinBefore, w.Get(kin));
        }
    }

    [Fact]
    public void Step_WithoutBodiesOnPhysics_OnlyBounds()
    {
        var w = World(ground: new FlatGround());
        var art = w.CreateArticulation(2);
        w.SetShape(w.Body(art, 0), 0.1f, 0.2f);
        w.Place(w.Body(art, 0), new Vector3(1, 2, 3), Quaternion.Identity);
        w.SetShape(w.Body(art, 1), 0.1f, 0.2f);
        w.Place(w.Body(art, 1), new Vector3(1, 2, 5), Quaternion.Identity);
        var before = (w.Get(w.Body(art, 0)), w.Get(w.Body(art, 1)));
        w.Step(Dt);
        Assert.Equal(before, (w.Get(w.Body(art, 0)), w.Get(w.Body(art, 1))));
        Assert.Equal(new Vector3(1, 2, 4), w.Bounds(art).Center);
        Assert.Equal(1.3f, w.Bounds(art).Radius, 5);   // до центра 1 м + полудлина 0,2 + радиус 0,1
    }

    /// <summary>Импульс контактов за тик: изменение импульса тела минус импульс гравитации (профиль без демпфирования и трения).</summary>
    private static List<Vector3> ContactImpulses(int substeps, Vector3 axis, Vector3 velocity, int ticks)
    {
        var w = World(PhysicsSettings.Default with { Substeps = substeps }, new FlatGround());
        const float m = 4;
        var b = Simulated(w, m, 0.08f, 0.2f, new Vector3(0, 0.35f, 0), AxisAlong(axis), ProfileGravityOnly);
        var s = w.Get(b);
        s.Velocity = velocity;
        w.Set(b, s);
        var gravity = new Vector3(0, -GravityOnly.Gravity * m * Dt, 0);
        var result = new List<Vector3>();
        for (int t = 0; t < ticks; t++)
        {
            var p0 = Momentum(w.Get(b));
            w.Step(Dt);
            result.Add(Momentum(w.Get(b)) - p0 - gravity);
        }
        return result;
    }

    public static TheoryData<string, Vector3, Vector3> Impacts => new()
    {
        { "лёжа, вертикально", Vector3.UnitX, new Vector3(0, -3, 0) },
        { "наклон, со сносом", new Vector3(1, 0.6f, 0.2f), new Vector3(1, -3, 0.5f) },
        { "стоя", Vector3.UnitY, new Vector3(0, -2, 0) },
    };

    [Theory]
    [MemberData(nameof(Impacts))]
    public void ContactImpulsePerTick_NearlyIndependentOfSubsteps(string name, Vector3 axis, Vector3 velocity)
    {
        const int ticks = 60;
        var four = ContactImpulses(4, axis, velocity, ticks);
        var eight = ContactImpulses(8, axis, velocity, ticks);
        var sum4 = Vector3.Zero;
        var sum8 = Vector3.Zero;
        float peak = 0, worst = 0;
        for (int t = 0; t < ticks; t++)
        {
            sum4 += four[t];
            sum8 += eight[t];
            peak = MathF.Max(peak, four[t].Length());
            worst = MathF.Max(worst, (four[t] - eight[t]).Length());
        }
        // Удар гасит нормальную скорость целиком (e = 0) при любом числе подшагов; за тик разница — доля подшага на границе удара.
        Assert.True((sum4 - sum8).Length() <= 0.01f * sum4.Length(), $"{name}: за {ticks} тиков {sum4} против {sum8} Н·с");
        Assert.True(worst <= 0.25f * peak, $"{name}: за тик до {worst:F4} Н·с при пике {peak:F4}");
        // В покое импульс контакта за тик — вес за тик.
        float weight = 4 * GravityOnly.Gravity * Dt;
        Assert.Equal(weight, four[^1].Y, 3);
        Assert.Equal(weight, eight[^1].Y, 3);
    }

    [Fact]
    public void Step_Deterministic_TwoWorldsSameCalls_SameBits()
    {
        var a = Pile(out var bodiesA);
        var b = Pile(out var bodiesB);
        for (int t = 0; t < 240; t++)
        {
            a.Step(Dt);
            b.Step(Dt);
        }
        for (int i = 0; i < bodiesA.Count; i++) Assert.Equal(a.Get(bodiesA[i]), b.Get(bodiesB[i]));
    }

    /// <summary>Куча из 6 сочленений по 4 тела на физике над ступенью и колонной, с разбросом поворотов и скоростей.</summary>
    private static PhysicsWorld Pile(out List<BodyHandle> bodies)
    {
        var w = World(ground: new StepGround(new Vector3(0, -1, -1), new Vector3(1, 0.12f, 1)));
        w.SetWall(3);
        w.SetPillars([new Pillar(-1, 0, 0.3f)]);
        var rng = new Random(11);
        bodies = [];
        for (int a = 0; a < 6; a++)
        {
            var art = w.CreateArticulation(4);
            for (int i = 0; i < 4; i++)
            {
                var b = w.Body(art, i);
                Setup(w, b, 1 + rng.NextSingle() * 5, 0.05f + rng.NextSingle() * 0.05f, rng.NextSingle() * 0.25f,
                    new Vector3(rng.NextSingle() - 0.5f, 0.3f + a * 0.15f + i * 0.05f, rng.NextSingle() - 0.5f), TestBodies.RandomRotation(rng), 0);
                var s = w.Get(b);
                s.Velocity = TestBodies.RandomVector(rng, 2);
                s.AngularVelocity = TestBodies.RandomVector(rng, 5);
                w.Set(b, s);
                bodies.Add(b);
            }
        }
        return w;
    }

    [Fact]
    public void SteadyState_33ArticulationsOf14Bodies_DoNotAllocate()
    {
        // 16 трупов (14 тел на физике, внахлёст друг с другом и с землёй), 17 кинематических сочленений, которые каждый тик
        // ставятся заново рядом с трупами; стена, колонны, ступень.
        var w = World(ground: new StepGround(new Vector3(0, -1, -3), new Vector3(2, 0.12f, 3)));
        w.SetWall(8);
        w.SetPillars([new Pillar(-3, 0, 0.6f), new Pillar(3, 4, 0.6f), new Pillar(-2, -4, 0.6f)]);
        var rng = new Random(5);
        var kinematic = new List<BodyHandle>();
        for (int a = 0; a < 33; a++)
        {
            var art = w.CreateArticulation(14);
            bool corpse = a % 2 == 0;
            var root = new Vector3((a % 6) * 0.5f - 1.5f, 0.4f, (a / 6) * 0.5f - 1.5f);
            for (int i = 0; i < 14; i++)
            {
                var b = w.Body(art, i);
                var p = root + new Vector3(0, i * 0.08f, 0) + TestBodies.RandomVector(rng, 0.2f);
                if (corpse) Setup(w, b, 1 + i % 5, 0.05f + (i % 3) * 0.03f, 0.05f + (i % 4) * 0.06f, p, TestBodies.RandomRotation(rng), 0);
                else
                {
                    w.SetShape(b, 1 + i % 5, 0.05f + (i % 3) * 0.03f, 0.05f + (i % 4) * 0.06f);
                    w.Place(b, p, TestBodies.RandomRotation(rng));
                    kinematic.Add(b);
                }
            }
        }
        void Tick(int t)
        {
            for (int k = 0; k < kinematic.Count; k++)
            {
                var s = w.Get(kinematic[k]);
                var offset = new Vector3(MathF.Sin(t * 0.05f + k) * 0.003f, 0, MathF.Cos(t * 0.05f + k) * 0.003f);
                w.SetKinematicTarget(kinematic[k], s.Position + offset, s.Rotation, Dt);
            }
            w.Step(Dt);
        }
        for (int t = 0; t < 200; t++) Tick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 200; t < 1200; t++) Tick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"1000 тиков 33 × 14 тел выделили {allocated} Б");
        Assert.True(w.ContactsNarrowChecks > 0, "пары тел не проверялись — замер ни о чём");
        for (int i = 0; i < w.BodyCapacity; i++)
            if (w.BodyAt(i) is { IsNone: false } h) Assert.True(w.Get(h).IsFinite);
    }
}
