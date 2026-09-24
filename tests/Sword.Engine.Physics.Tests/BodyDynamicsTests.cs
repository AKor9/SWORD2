using System.Numerics;
using static Sword.Engine.Physics.Tests.TestBodies;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Динамика тела-капсулы (7б) — свойства, а не повтор формул: свободное падение с ошибкой шага, демпфирование и потолки
/// скоростей, трение о землю останавливает скольжение, инерция капсулы, поворот с обрезкой угла, коррекция и импульс в
/// точке, конечность, кинетическая энергия.
/// </summary>
public class BodyDynamicsTests
{
    /// <summary>Интеграция подшага и обновление скоростей — как в шаге: n подшагов по h.</summary>
    private static void Run(ref BodyState b, in BodyDynamics d, float h, int n, bool grounded = false)
    {
        for (int i = 0; i < n; i++)
        {
            Dynamics.Integrate(ref b, d, h);
            if (grounded) b.Grounded = true;   // как контакт с землёй в подшаге
            Dynamics.UpdateVelocity(ref b, d, h);
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(240)]
    [InlineData(480)]
    public void FreeFall_DisplacementWithinStepError_ErrorShrinksWithStep(int rate)
    {
        const float g = 9.81f;
        var d = Vacuum with { Gravity = g };
        float h = 1f / rate;
        var b = Capsule(2, 0.05f, 0.2f);
        b.Velocity = new Vector3(1.5f, 0, -0.5f);
        Run(ref b, d, h, rate);   // 1 с

        const float t = 1;
        float exact = -g * t * t / 2;
        // Явно-неявный Эйлер падает дальше точного ровно на g·h·t/2 — ошибка шага, линейная по h. Сверх неё — округление
        // float: скорость — разность положений за подшаг, её ошибка копится (при h = 1/480 — ~5 % ошибки шага за секунду).
        float stepError = g * h * t / 2;
        Assert.Equal(stepError, exact - b.Position.Y, 0.1f * stepError + 1e-4f);
        Assert.Equal(-g * t, b.Velocity.Y, 0.005f);
        // По горизонтали без демпфирования — равномерно.
        Assert.Equal(1.5f, b.Position.X, 3);
        Assert.Equal(-0.5f, b.Position.Z, 3);
        Assert.Equal(new Vector3(1.5f, 0, -0.5f), b.Velocity with { Y = 0 }, new Vector3Comparer(1e-3f));
    }

    [Fact]
    public void Damping_DecaysVelocitiesExponentially_KeepsDirection_NeverReverses()
    {
        var d = Vacuum with { LinearDamping = 0.8f, AngularDamping = 2f };
        var b = Capsule(2, 0.05f, 0.2f);
        b.Velocity = new Vector3(3, 0, 4);
        b.AngularVelocity = new Vector3(0, 0, 2);
        Run(ref b, d, H, 480);   // 1 с

        Assert.Equal(5 * MathF.Exp(-0.8f), b.Velocity.Length(), 0.01f);
        Assert.Equal(new Vector3(0.6f, 0, 0.8f), Vector3.Normalize(b.Velocity), new Vector3Comparer(1e-4f));
        Assert.Equal(2 * MathF.Exp(-2f), b.AngularVelocity.Length(), 0.01f);
        Assert.True(b.AngularVelocity.Z > 0);

        // k·h > 1: множитель не уходит ниже нуля — скорость гаснет, а не меняет знак.
        var hard = Vacuum with { LinearDamping = 1000, AngularDamping = 1000 };
        var c = Capsule(2, 0.05f, 0.2f);
        c.Velocity = new Vector3(3, 0, 4);
        c.AngularVelocity = new Vector3(0, 0, 2);
        Run(ref c, hard, H, 1);
        Assert.Equal(Vector3.Zero, c.Velocity);
        Assert.Equal(Vector3.Zero, c.AngularVelocity);
    }

    [Fact]
    public void SpeedCaps_LimitLengthKeepDirection_BelowCapUntouched()
    {
        var d = Vacuum with { MaxSpeed = 15, MaxAngularSpeed = 40 };
        var b = Capsule(2, 0.05f, 0.2f);
        b.Velocity = new Vector3(30, 0, 40);
        b.AngularVelocity = new Vector3(0, 100, 0);
        Run(ref b, d, H, 1);
        Assert.Equal(15, b.Velocity.Length(), 3);
        Assert.Equal(new Vector3(0.6f, 0, 0.8f), Vector3.Normalize(b.Velocity), new Vector3Comparer(1e-4f));
        Assert.Equal(40, b.AngularVelocity.Length(), 3);
        Assert.Equal(Vector3.UnitY, Vector3.Normalize(b.AngularVelocity), new Vector3Comparer(1e-4f));

        var slow = Capsule(2, 0.05f, 0.2f);
        slow.Velocity = new Vector3(6, 0, 8);
        slow.AngularVelocity = new Vector3(0, 10, 0);
        Run(ref slow, d, H, 1);
        Assert.Equal(10, slow.Velocity.Length(), 3);
        Assert.Equal(10, slow.AngularVelocity.Length(), 1);
    }

    [Fact]
    public void GroundFriction_StopsSliding_OnlyWhenGrounded_VerticalUntouched()
    {
        var d = new BodyDynamics { Gravity = 0 };
        var slide = Capsule(2, 0.05f, 0.2f);
        slide.Velocity = new Vector3(3, 0, -2);
        slide.AngularVelocity = new Vector3(0, 5, 0);
        var air = slide;
        Run(ref slide, d, H, 480, grounded: true);
        Run(ref air, d, H, 480);

        // На земле: 1 с трения 9 1/с — скольжение гаснет почти до нуля, вращение — втрое медленнее, но гаснет.
        Assert.True(slide.Velocity.Length() < 0.01f, $"скольжение {slide.Velocity.Length()} м/с");
        Assert.True(slide.AngularVelocity.Length() < 0.2f, $"вращение {slide.AngularVelocity.Length()} рад/с");
        // В воздухе — только демпфирование.
        Assert.True(air.Velocity.Length() > 3, $"в воздухе {air.Velocity.Length()} м/с");

        // Трение о землю — только горизонталь: вертикальную скорость держит лишь демпфирование.
        var fall = Capsule(2, 0.05f, 0.2f);
        fall.Velocity = new Vector3(0, -1, 0);
        Run(ref fall, d, H, 1, grounded: true);
        Assert.Equal(-(1 - d.LinearDamping * H), fall.Velocity.Y, 5);

        // Касание — на подшаг: интеграция его сбрасывает.
        fall.Grounded = true;
        Dynamics.Integrate(ref fall, d, H);
        Assert.False(fall.Grounded);
    }

    [Theory]
    [InlineData(0.05f, 0.2f)]
    [InlineData(0.15f, 0.1f)]
    [InlineData(0.15f, 0.02f)]   // короче 0,37 r: у формулы цилиндра SWORD ось здесь была бы не наименьшей
    [InlineData(0.065f, 0.14f)]
    [InlineData(0.03f, 0.575f)]
    public void CapsuleInertia_AlongAxisSmallest_SymmetricAcross_ScalesWithMass(float radius, float half)
    {
        var inv = Dynamics.CapsuleInvInertia(2, radius, half);
        Assert.True(inv.Y > inv.X, $"вдоль оси I = {1 / inv.Y}, поперёк {1 / inv.X}");
        Assert.Equal(inv.X, inv.Z);
        Assert.Equal(inv / 3, Dynamics.CapsuleInvInertia(6, radius, half), new Vector3Comparer(1e-4f * inv.Length()));
        // Поперёк — не меньше, чем у стержня длиной в ось, и не больше, чем если бы вся масса была на кончике (h + r).
        float rod = 2 * (2 * half) * (2 * half) / 12, tip = 2 * (half + radius) * (half + radius);
        Assert.InRange(1 / inv.X, rod, tip);
    }

    [Fact]
    public void CapsuleInertia_Sphere_Isotropic_LongRod_TendsToRod()
    {
        var sphere = Dynamics.CapsuleInvInertia(2, 0.1f, 0);
        Assert.Equal(1 / (0.4f * 2 * 0.01f), sphere.X, 2);
        Assert.Equal(sphere.X, sphere.Y, 2);

        var rod = Dynamics.CapsuleInvInertia(2, 0.001f, 1);
        Assert.Equal(2 * 2f * 2f / 12, 1 / rod.X, 2);
    }

    [Fact]
    public void Rotate_SmallStepFollowsVector_LargeStepClampedPerCall()
    {
        var b = Capsule(2, 0.05f, 0.2f, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f));
        var before = b.Rotation;
        var small = new Vector3(0, 0.002f, 0.001f);
        Dynamics.Rotate(ref b, small);
        Assert.Equal(small, PhysicsMath.RotationVector(before, b.Rotation), new Vector3Comparer(1e-6f));
        Assert.Equal(1, b.Rotation.Length(), 5);

        before = b.Rotation;
        Dynamics.Rotate(ref b, new Vector3(0, 3, 0));
        var step = PhysicsMath.RotationVector(before, b.Rotation);
        Assert.InRange(step.Length(), 0.45f, Dynamics.MaxRotationPerCall);
        Assert.Equal(Vector3.UnitY, Vector3.Normalize(step), new Vector3Comparer(1e-4f));
    }

    [Fact]
    public void Correct_AtCenterOnlyMoves_WithLeverAlsoTurnsAboutLeverCrossImpulse()
    {
        var b = Capsule(4, 0.05f, 0.2f, new Vector3(1, 2, 3));
        var p = new Vector3(0.02f, 0, 0);
        Dynamics.Correct(ref b, p, Vector3.Zero);
        Assert.Equal(new Vector3(1.005f, 2, 3), b.Position, new Vector3Comparer(1e-6f));
        Assert.Equal(Quaternion.Identity, b.Rotation);

        var c = Capsule(4, 0.05f, 0.2f);
        Dynamics.Correct(ref c, p, new Vector3(0, 0.2f, 0));
        Assert.Equal(new Vector3(0.005f, 0, 0), c.Position, new Vector3Comparer(1e-6f));
        var turn = PhysicsMath.RotationVector(Quaternion.Identity, c.Rotation);
        // r × p = (0, 0.2, 0) × (0.02, 0, 0) = (0, 0, −0.004): поворот вокруг −Z на I⁻¹ · 0,004
        Assert.Equal(-0.004f * c.InvInertia.Z, turn.Z, 3);
        Assert.Equal(0, turn.X, 6);
        Assert.Equal(0, turn.Y, 6);
    }

    [Fact]
    public void ApplyImpulse_ChangesMomentumByImpulse_AngularByLeverCrossImpulse()
    {
        var rng = new Random(11);
        for (int k = 0; k < 50; k++)
        {
            var b = RandomCapsule(rng, RandomVector(rng, 3));
            b.Velocity = RandomVector(rng, 4);
            b.AngularVelocity = RandomVector(rng, 6);
            var v0 = b.Velocity;
            var w0 = b.AngularVelocity;
            var j = RandomVector(rng, 5);
            var r = RandomVector(rng, 0.4f);
            Dynamics.ApplyImpulse(ref b, j, r);
            Assert.Equal(j, (b.Velocity - v0) * b.Mass, new Vector3Comparer(1e-4f));
            // Момент импульса относительно центра меняется на r × J: I(ω1 − ω0) = r × J.
            var dw = b.AngularVelocity - w0;
            Assert.Equal(b.InvInertiaWorld(Vector3.Cross(r, j)), dw, new Vector3Comparer(1e-4f));
        }
    }

    [Fact]
    public void KineticEnergy_TranslationPlusRotationAboutPrincipalAxes()
    {
        var b = Capsule(3, 0.1f, 0.3f, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        b.Velocity = new Vector3(2, 0, 0);
        Assert.Equal(0.5f * 3 * 4, b.KineticEnergy, 4);
        // Ось капсулы повёрнута в −X мира: вращение вокруг X мира — вокруг её оси (наименьшая инерция).
        b.Velocity = Vector3.Zero;
        b.AngularVelocity = new Vector3(5, 0, 0);
        Assert.Equal(0.5f * 25 / b.InvInertia.Y, b.KineticEnergy, 3);
        b.AngularVelocity = new Vector3(0, 5, 0);
        Assert.Equal(0.5f * 25 / b.InvInertia.X, b.KineticEnergy, 3);
        // Тело без массы энергии не несёт.
        var ghost = BodyState.New(BodyMode.Kinematic);
        ghost.Velocity = new Vector3(3, 0, 0);
        ghost.AngularVelocity = new Vector3(1, 1, 1);
        Assert.Equal(0, ghost.KineticEnergy);
    }

    [Fact]
    public void IsFinite_AnyPoseOrVelocityComponent()
    {
        var ok = Capsule(2, 0.05f, 0.2f, new Vector3(1, 2, 3));
        Assert.True(ok.IsFinite);
        var cases = new Func<BodyState, BodyState>[]
        {
            s => s with { Position = s.Position with { Y = float.NaN } },
            s => s with { PrevPosition = s.PrevPosition with { Z = float.PositiveInfinity } },
            s => s with { StartPosition = s.StartPosition with { X = float.NaN } },
            s => s with { Velocity = s.Velocity with { X = float.NegativeInfinity } },
            s => s with { AngularVelocity = s.AngularVelocity with { Z = float.NaN } },   // у SWORD не проверялась
            s => s with { Rotation = s.Rotation with { X = float.NaN } },                 // у SWORD — только W
            s => s with { PrevRotation = s.PrevRotation with { W = float.NaN } },
            s => s with { StartRotation = s.StartRotation with { Y = float.NaN } },
        };
        Assert.All(cases, make => Assert.False(make(ok).IsFinite));
    }

    [Fact]
    public void ProfileProblem_RejectsNonFiniteNegativeAndZeroCaps()
    {
        Assert.Null(new BodyDynamics().Problem());
        Assert.Null((new BodyDynamics() with { MaxSpeed = float.PositiveInfinity }).Problem());
        Assert.NotNull(default(BodyDynamics).Problem());   // нулевые потолки — не профиль
        Assert.NotNull((new BodyDynamics() with { Gravity = float.NaN }).Problem());
        Assert.NotNull((new BodyDynamics() with { LinearDamping = -0.1f }).Problem());
        Assert.NotNull((new BodyDynamics() with { GroundAngularFriction = float.PositiveInfinity }).Problem());
        Assert.NotNull((new BodyDynamics() with { MaxAngularSpeed = 0 }).Problem());
        Assert.NotNull((new BodyDynamics() with { MaxSpeed = float.NaN }).Problem());
    }
}

/// <summary>Сравнение векторов покомпонентно с допуском.</summary>
internal sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
{
    public bool Equals(Vector3 a, Vector3 b) =>
        MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance && MathF.Abs(a.Z - b.Z) <= tolerance;

    public int GetHashCode(Vector3 v) => 0;
}
