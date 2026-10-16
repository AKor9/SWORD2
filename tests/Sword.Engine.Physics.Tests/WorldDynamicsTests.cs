using System.Numerics;
using static Sword.Engine.Physics.Tests.TestBodies;

namespace Sword.Engine.Physics.Tests;

/// <summary>Динамика тел в мире: форма, режим, профили, запись и чтение состояния, поза начала тика; слот не наследует; 0 Б.</summary>
public class WorldDynamicsTests
{
    private static (PhysicsWorld World, BodyHandle Body) One()
    {
        var w = new PhysicsWorld();
        return (w, w.Body(w.CreateArticulation(1), 0));
    }

    [Fact]
    public void SetShapeWithMass_InverseMassAndCapsuleInertia_ValidatesArguments()
    {
        var (w, b) = One();
        w.SetShape(b, 2.5f, 0.065f, 0.14f);
        var s = w.Get(b);
        Assert.Equal((2.5f, 0.4f, 0.065f, 0.14f), (s.Mass, s.InvMass, s.Radius, s.HalfLength));
        Assert.Equal(Dynamics.CapsuleInvInertia(2.5f, 0.065f, 0.14f), s.InvInertia);

        foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetShape(b, bad, 0.1f, 0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetShape(b, 1, bad, 0.1f));   // у тела с массой радиус — больше 0
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetShape(b, 1, 0.1f, -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetShape(b, 1, 0.1f, float.NaN));
        Assert.Equal(s, w.Get(b));

        // Новая форма с другой массой — инерция следует за формой и массой; поза и режим не меняются.
        w.Place(b, new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f));
        var placed = w.Get(b);
        w.SetShape(b, 4, 0.09f, 0.3f);
        s = w.Get(b);
        Assert.Equal((4f, 0.25f, 0.09f, 0.3f), (s.Mass, s.InvMass, s.Radius, s.HalfLength));
        Assert.Equal(Dynamics.CapsuleInvInertia(4, 0.09f, 0.3f), s.InvInertia);
        Assert.Equal((placed.Position, placed.Rotation, placed.Mode), (s.Position, s.Rotation, s.Mode));
        // Новое тело — без формы и массы (форма задаётся только с массой).
        var (w2, b2) = One();
        Assert.Equal((0f, 0f, 0f, Vector3.Zero), (w2.Get(b2).Radius, w2.Get(b2).Mass, w2.Get(b2).InvMass, w2.Get(b2).InvInertia));
    }

    [Fact]
    public void SetMode_SimulatedNeedsMass_KeepsPoseAndVelocities_KinematicTargetOnlyForKinematic()
    {
        var (w, b) = One();
        Assert.Equal(BodyMode.Kinematic, w.Get(b).Mode);
        Assert.Throws<InvalidOperationException>(() => w.SetMode(b, BodyMode.Simulated));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetMode(b, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetMode(b, (BodyMode)3));

        w.SetShape(b, 2, 0.05f, 0.2f);
        w.Place(b, new Vector3(0, 1, 0), Quaternion.Identity);
        w.SetKinematicTarget(b, new Vector3(0.01f, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.02f), 0.01f);
        var before = w.Get(b);
        w.SetMode(b, BodyMode.Simulated);
        var after = w.Get(b);
        Assert.Equal(BodyMode.Simulated, after.Mode);
        Assert.Equal(before with { Mode = BodyMode.Simulated }, after);   // переход в труп: скорости — от последней постановки

        Assert.Throws<InvalidOperationException>(() => w.SetKinematicTarget(b, Vector3.Zero, Quaternion.Identity, 0.01f));
        w.SetMode(b, BodyMode.Kinematic);
        w.SetKinematicTarget(b, Vector3.Zero, Quaternion.Identity, 0.01f);
    }

    [Fact]
    public void Profiles_DefaultAtZero_RegisterReplaceGet_BodyChoosesProfile_Validated()
    {
        var (w, b) = One();
        Assert.Equal(1, w.DynamicsCount);
        Assert.Equal(new BodyDynamics(), w.GetDynamics(0));
        Assert.Equal(0, w.Get(b).Profile);

        var corpse = new BodyDynamics { MaxSpeed = 15, MaxAngularSpeed = 40 };
        int p = w.RegisterDynamics(corpse);
        Assert.Equal((1, 2), (p, w.DynamicsCount));
        Assert.Equal(corpse, w.GetDynamics(p));
        w.SetDynamics(b, p);
        Assert.Equal(p, w.Get(b).Profile);

        // Профиль тела решает: потолок трупа держит скорость, профиль по умолчанию — нет.
        var s = w.Get(b) with { Velocity = new Vector3(0, 0, 30) };
        Dynamics.SetShape(ref s, 2, 0.05f, 0.2f);
        Dynamics.Integrate(ref s, w.GetDynamics(s.Profile), H);
        Dynamics.UpdateVelocity(ref s, w.GetDynamics(s.Profile), H);
        Assert.Equal(15, s.Velocity.Length(), 3);

        w.ReplaceDynamics(p, corpse with { MaxSpeed = 10 });
        Assert.Equal(10, w.GetDynamics(p).MaxSpeed);
        w.ReplaceDynamics(0, new BodyDynamics { Gravity = 0 });   // и профиль по умолчанию — ручки на ходу
        Assert.Equal(0, w.GetDynamics(0).Gravity);

        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetDynamics(b, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetDynamics(b, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.GetDynamics(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.ReplaceDynamics(5, corpse));
        Assert.Throws<ArgumentException>(() => w.RegisterDynamics(default));
        Assert.Throws<ArgumentException>(() => w.RegisterDynamics(corpse with { LinearDamping = float.NaN }));
        Assert.Throws<ArgumentException>(() => w.ReplaceDynamics(p, corpse with { MaxAngularSpeed = -1 }));
        Assert.Equal(2, w.DynamicsCount);
        Assert.Equal(10, w.GetDynamics(p).MaxSpeed);

        // Номер профиля в теле — байт: не больше 256 профилей.
        for (int i = w.DynamicsCount; i < PhysicsWorld.MaxDynamics; i++) w.RegisterDynamics(corpse);
        Assert.Throws<InvalidOperationException>(() => w.RegisterDynamics(corpse));
        w.SetDynamics(b, 255);
        Assert.Equal(255, w.Get(b).Profile);
    }

    private static BodyState Everything()
    {
        var s = Capsule(3.5f, 0.07f, 0.16f, new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f));
        s.PrevPosition = new Vector3(0.9f, 2.1f, 3);
        s.PrevRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.45f);
        s.StartPosition = new Vector3(0.8f, 2.2f, 3);
        s.StartRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f);
        s.Velocity = new Vector3(4, -1, 0.5f);
        s.AngularVelocity = new Vector3(0, 7, -2);
        s.Grounded = true;
        s.Profile = 1;
        s.Source = KinematicSource.Pass;
        return s;
    }

    [Fact]
    public void SetWholeState_ThenGet_SameBits_StaleHandleThrows()
    {
        var w = new PhysicsWorld();
        w.RegisterDynamics(new BodyDynamics { MaxSpeed = 15 });
        var a = w.CreateArticulation(2);
        var b = w.Body(a, 1);
        var s = Everything();
        w.Set(b, s);
        Assert.Equal(s, w.Get(b));
        Assert.Equal(BodyState.New(BodyMode.Kinematic), w.Get(w.Body(a, 0)));   // сосед не тронут

        w.DestroyArticulation(a);
        Assert.Contains("не жив", Assert.Throws<InvalidOperationException>(() => w.Set(b, s)).Message);
    }

    [Fact]
    public void SetWholeState_RejectsInvalid_BodyUnchanged()
    {
        var w = new PhysicsWorld();
        w.RegisterDynamics(new BodyDynamics { MaxSpeed = 15 });
        var b = w.Body(w.CreateArticulation(1), 0);
        var good = Everything();
        w.Set(b, good);
        var bad = new Func<BodyState, BodyState>[]
        {
            s => s with { Position = s.Position with { X = float.NaN } },
            s => s with { PrevPosition = s.PrevPosition with { Y = float.PositiveInfinity } },
            s => s with { StartPosition = s.StartPosition with { Z = float.NaN } },
            s => s with { Velocity = s.Velocity with { X = float.NaN } },
            s => s with { AngularVelocity = s.AngularVelocity with { Y = float.NegativeInfinity } },
            s => s with { Rotation = new Quaternion(0, 0, 0, 2) },
            s => s with { PrevRotation = default },
            s => s with { StartRotation = s.StartRotation with { W = float.NaN } },
            s => s with { Radius = -0.1f },
            s => s with { HalfLength = float.NaN },
            s => s with { Mass = -1 },
            s => s with { InvMass = float.PositiveInfinity },
            s => s with { InvInertia = s.InvInertia with { Y = -1 } },
            s => s with { Mode = 0 },
            s => s with { Mode = (BodyMode)7 },
            s => s with { Source = (KinematicSource)9 },
            s => s with { Profile = 2 },
            s => s with { Mass = 0, InvMass = 0 },   // тело на физике — с массой
        };
        foreach (var make in bad)
        {
            Assert.Throws<ArgumentException>(() => w.Set(b, make(good)));
            Assert.Equal(good, w.Get(b));
        }
        // Кинематическое тело без массы — годно.
        w.Set(b, good with { Mode = BodyMode.Kinematic, Mass = 0, InvMass = 0, InvInertia = Vector3.Zero });
    }

    [Fact]
    public void StartPose_PlaceSame_KinematicTargetPreviousPlacement()
    {
        var (w, b) = One();
        var p0 = new Vector3(0.1f, 0.9f, 0.2f);
        var r0 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        w.Place(b, p0, r0);
        Assert.Equal((p0, r0), (w.Get(b).StartPosition, w.Get(b).StartRotation));
        var p1 = p0 + new Vector3(0.02f, 0, 0);
        var r1 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.05f) * r0;
        w.SetKinematicTarget(b, p1, r1, 1f / 120);
        var s = w.Get(b);
        // Клиент интерполирует от позы начала тика к позе тика: у кинематического тела начало тика — прошлая постановка.
        Assert.Equal((p0, r0, p1, r1), (s.StartPosition, s.StartRotation, s.Position, s.Rotation));
        Assert.Equal((s.PrevPosition, s.PrevRotation), (s.StartPosition, s.StartRotation));
    }

    [Fact]
    public void KinematicSource_DefaultBody_SetAndValidated()
    {
        var (w, b) = One();
        Assert.Equal(KinematicSource.Body, w.Get(b).Source);
        w.SetKinematicSource(b, KinematicSource.Pass);
        Assert.Equal(KinematicSource.Pass, w.Get(b).Source);
        w.SetKinematicSource(b, KinematicSource.NoReaction);
        Assert.Equal(KinematicSource.NoReaction, w.Get(b).Source);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetKinematicSource(b, (KinematicSource)3));
    }

    [Fact]
    public void Settings_DefaultAtComposition_ReplacedWhole_InvalidRejected()
    {
        Assert.Same(PhysicsSettings.Default, new PhysicsWorld().Settings);
        var tuned = PhysicsSettings.Default with { StaticFriction = 0.5f, Substeps = 8 };
        var w = new PhysicsWorld(settings: tuned);
        Assert.Same(tuned, w.Settings);
        var other = new PhysicsWorld(settings: tuned);   // общий неизменяемый экземпляр у двух миров
        w.Settings = tuned with { PushForce = 500 };
        Assert.Equal(500, w.Settings.PushForce);
        Assert.Same(tuned, other.Settings);

        var invalid = new[]
        {
            tuned with { Substeps = 0 }, tuned with { Substeps = 65 }, tuned with { PassExitTicks = 0 },
            tuned with { PopSpeed = float.NaN }, tuned with { StaticFriction = -0.1f }, tuned with { ContactCompliance = float.PositiveInfinity },
            tuned with { JointLimitCompliance = -1 }, tuned with { PushForce = -1 }, tuned with { PassShare = float.NaN },
            tuned with { KinematicPointSpeedMax = -16 }, tuned with { HitDvShare = float.NegativeInfinity },
        };
        var before = w.Settings;
        foreach (var s in invalid)
        {
            Assert.NotNull(s.Problem());
            Assert.Throws<ArgumentException>(() => w.Settings = s);
            Assert.Throws<ArgumentException>(() => new PhysicsWorld(settings: s));
        }
        Assert.Throws<ArgumentNullException>(() => w.Settings = null!);
        Assert.Same(before, w.Settings);
    }

    [Fact]
    public void SlotReuse_NewBodyDoesNotInheritDynamics()
    {
        var w = new PhysicsWorld();
        w.RegisterDynamics(new BodyDynamics { MaxSpeed = 15 });
        var a = w.CreateArticulation(1);
        var b = w.Body(a, 0);
        w.Set(b, Everything());
        w.DestroyArticulation(a);
        var again = w.Body(w.CreateArticulation(1), 0);
        Assert.Equal(b.Index, again.Index);   // тот же слот
        Assert.Equal(BodyState.New(BodyMode.Kinematic), w.Get(again));
    }

    [Fact]
    public void SteadyState_PrimitivesAndWrites_DoNotAllocate()
    {
        var w = new PhysicsWorld();
        int corpse = w.RegisterDynamics(new BodyDynamics { MaxSpeed = 15, MaxAngularSpeed = 40 });
        var art = w.CreateArticulation(14);
        var bodies = new BodyHandle[14];
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i] = w.Body(art, i);
            w.SetShape(bodies[i], 1 + i, 0.05f + i * 0.01f, 0.1f + i * 0.02f);
        }
        var state = Everything();
        var profile = w.GetDynamics(corpse);

        float sink = 0;
        void Round(int t)
        {
            var a = Capsule(2, 0.05f, 0.2f, new Vector3(0, 1, 0));
            var b = Capsule(3, 0.06f, 0.15f, new Vector3(0.1f, 1.2f, 0));
            a.Velocity = new Vector3(1, -2, 0.5f);
            a.AngularVelocity = new Vector3(3, 0, 1);
            for (int k = 0; k < 4; k++)
            {
                Dynamics.Integrate(ref a, profile, H);
                Dynamics.Integrate(ref b, profile, H);
                sink += Xpbd.Attach(ref a, a.ToWorld(new Vector3(0, -0.2f, 0)), ref b, b.ToWorld(new Vector3(0, 0.15f, 0)), 0, H);
                sink += Xpbd.Angular(ref a, ref b, new Vector3(0.01f, 0, 0), 2e-4f, H);
                sink += Xpbd.Contact(ref a, a.EndA, Vector3.UnitY, -0.001f, 0, H);
                sink += Xpbd.Contact(ref a, a.EndB, ref b, b.EndA, Vector3.UnitY, -0.001f, 1e-7f, H, 0.01f);
                sink += Xpbd.Friction(ref a, a.EndA, Vector3.UnitY, 0.01f, 0.7f, 0, H);
                sink += Xpbd.Friction(ref a, a.EndB, ref b, b.EndA, Vector3.UnitY, 0.01f, 0.7f, 0, H);
                Dynamics.UpdateVelocity(ref a, profile, H);
                Dynamics.UpdateVelocity(ref b, profile, H);
                Dynamics.ApplyImpulse(ref a, new Vector3(0, 0.1f, 0), new Vector3(0, 0.2f, 0));
                sink += a.KineticEnergy + (a.IsFinite ? 1 : 0);
            }
            for (int i = 0; i < bodies.Length; i++)
            {
                w.Set(bodies[i], state with { Position = new Vector3(i, t * 0.01f, 0) });
                w.SetMode(bodies[i], (t & 1) == 0 ? BodyMode.Simulated : BodyMode.Kinematic);
                w.SetDynamics(bodies[i], (t & 1) == 0 ? corpse : 0);
                w.SetKinematicSource(bodies[i], (KinematicSource)(t % 3));
                w.SetShape(bodies[i], 2, 0.05f, 0.2f);
                sink += w.Get(bodies[i]).Position.X;
            }
        }
        for (int t = 0; t < 100; t++) Round(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 100; t < 1100; t++) Round(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"1000 кругов примитивов и записи 14 тел выделили {allocated} Б");
        Assert.True(float.IsFinite(sink));
    }
}
