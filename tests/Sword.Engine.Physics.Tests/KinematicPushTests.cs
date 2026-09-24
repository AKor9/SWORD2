using System.Numerics;
using static Sword.Engine.Physics.Tests.KinematicScene;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Заготовки сцен толчков 7д: мир с профилем «вакуум» (без гравитации и сопротивления — скорость меняют только толчки),
/// толкающий из кинематических шаров, цель из шаров на физике; тик — настоящий <see cref="PhysicsWorld.Step"/> без земли,
/// стены и колонн (цели разных сочленений не касаются друг друга — в шаге только толчки). Шар толкают в точке на линии
/// центров — без плеча: импульс тела за тик ровно <c>Σλ/h</c>.
/// </summary>
internal static class KinematicScene
{
    /// <summary>Тик 120 Гц, с.</summary>
    public const float Dt = 1f / 120;

    /// <summary>Номер профиля «вакуум» в мире <see cref="World"/>.</summary>
    public const int Vacuum = 1;

    public static PhysicsWorld World(int substeps = 4, PhysicsSettings? settings = null)
    {
        var w = new PhysicsWorld(settings: (settings ?? PhysicsSettings.Default) with { Substeps = substeps });
        Assert.Equal(Vacuum, w.RegisterDynamics(TestBodies.Vacuum));
        return w;
    }

    /// <summary>Толкающий: кинематические шары радиуса <paramref name="radius"/> (масса 1 кг — для ударов по нему) в точках.</summary>
    public static ArticulationHandle Pusher(PhysicsWorld w, float radius, params Vector3[] at)
    {
        var a = w.CreateArticulation(at.Length);
        for (int i = 0; i < at.Length; i++)
        {
            var b = w.Body(a, i);
            w.SetShape(b, 1, radius, 0);
            w.Place(b, at[i], Quaternion.Identity);
        }
        return a;
    }

    /// <summary>Цель: шары на физике массы <paramref name="mass"/> с профилем <paramref name="profile"/>.</summary>
    public static ArticulationHandle Target(PhysicsWorld w, float mass, float radius, int profile, params Vector3[] at)
    {
        var a = w.CreateArticulation(at.Length);
        for (int i = 0; i < at.Length; i++)
        {
            var b = w.Body(a, i);
            w.SetShape(b, mass, radius, 0);
            w.Place(b, at[i], Quaternion.Identity);
            w.SetMode(b, BodyMode.Simulated);
            w.SetDynamics(b, profile);
        }
        return a;
    }

    /// <summary>Импульс сочленения <c>Σ m v</c>, Н·с.</summary>
    public static Vector3 Momentum(IPhysicsWorld w, ArticulationHandle a)
    {
        var p = Vector3.Zero;
        for (int i = 0; i < w.BodyCount(a); i++)
        {
            var s = w.Get(w.Body(a, i));
            p += s.Velocity * s.Mass;
        }
        return p;
    }

    /// <summary>Сдвинуть кинематическое сочленение за тик со скоростью <paramref name="velocity"/> (цель тика).</summary>
    public static void Move(IPhysicsWorld w, ArticulationHandle a, Vector3 velocity, float dt = Dt)
    {
        for (int i = 0; i < w.BodyCount(a); i++)
        {
            var b = w.Body(a, i);
            var s = w.Get(b);
            w.SetKinematicTarget(b, s.Position + velocity * dt, s.Rotation, dt);
        }
    }

    /// <summary>Чем толкают все тела сочленения.</summary>
    public static void Source(IPhysicsWorld w, ArticulationHandle a, KinematicSource source)
    {
        for (int i = 0; i < w.BodyCount(a); i++) w.SetKinematicSource(w.Body(a, i), source);
    }

    /// <summary>Бюджет пары за подшаг при подшаге <paramref name="h"/>, кг·м.</summary>
    public static float Budget(float h, PhysicsSettings? s = null) => (s ?? PhysicsSettings.Default).PushForce * h * h;

    /// <summary><paramref name="actual"/> в пределах относительной ошибки <paramref name="rel"/> от <paramref name="expected"/>.</summary>
    public static void Near(float expected, float actual, float rel, string what) =>
        Assert.True(MathF.Abs(actual - expected) <= rel * MathF.Abs(expected) + 1e-9f, $"{what}: {actual} против {expected}");
}

/// <summary>
/// Толчки живых (7д): бюджет пары за подшаг (скаляр на все контакты пары, первый берёт сколько может, сброс каждый подшаг,
/// пары независимы), сила за тик не зависит от числа подшагов, отдача против поправки физической стороны, предел скорости
/// лёгкой части, толчок без отдачи, клинок списывает проход и бюджет пары вместе.
/// </summary>
public class KinematicPushTests
{
    [Fact]
    public void PairBudget_SharedByAllContactsOfPair_FirstTakesAll()
    {
        const float h = TestBodies.H;
        var w = World(substeps: 1);
        // три пары шаров внахлёст на 5 см: каждый контакт один взял бы в сотни раз больше бюджета
        var pusher = Pusher(w, 0.1f, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0));
        var target = Target(w, 50, 0.1f, Vacuum, new Vector3(0, 0, 0.15f), new Vector3(1, 0, 0.15f), new Vector3(2, 0, 0.15f));
        w.Step(h);

        float budget = Budget(h);
        Near(budget, w.KinematicPairBudget, 1e-6f, "бюджет пары F·h²");
        Assert.Equal(budget, w.KinematicPairUsed(pusher, target));
        // первый контакт пары (тело 0 толкающего — тело 0 цели) взял весь бюджет, остальным не осталось
        var v = new Vector3[3];
        for (int i = 0; i < 3; i++) v[i] = w.Get(w.Body(target, i)).Velocity;
        Near(budget, 50 * v[0].Z * h, 1e-3f, "λ первого контакта");
        Assert.Equal((Vector3.Zero, Vector3.Zero), (v[1], v[2]));
    }

    [Fact]
    public void PairBudget_SmallPenetrations_EachTakesUpToRemainder_SumNeverExceedsBudget()
    {
        const float h = TestBodies.H;
        var rng = new Random(7);
        float budget = Budget(h), alphaH = PhysicsSettings.Default.ContactCompliance / (h * h);
        int limited = 0, free = 0;
        for (int round = 0; round < 200; round++)
        {
            var w = World(substeps: 1);
            int n = 1 + rng.Next(6);
            var at = new Vector3[n];
            var depth = new float[n];
            var targetAt = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                at[i] = new Vector3(i, 0, 0);
                depth[i] = rng.NextSingle() * 3e-4f;
                targetAt[i] = at[i] + new Vector3(0, 0, 0.2f - depth[i]);
            }
            float mass = 1 + rng.NextSingle() * 99;
            var pusher = Pusher(w, 0.1f, at);
            var target = Target(w, mass, 0.1f, Vacuum, targetAt);
            w.Step(h);

            float remaining = budget, sum = 0;
            for (int i = 0; i < n; i++)
            {
                // свободная λ контакта в центр шара: глубина / (1/m + α/h²); берёт не больше остатка бюджета пары
                float want = depth[i] / (1 / mass + alphaH);
                float expected = MathF.Min(want, remaining);
                float got = mass * w.Get(w.Body(target, i)).Velocity.Z * h;
                Assert.True(MathF.Abs(got - expected) <= 1e-3f * budget, $"круг {round}, контакт {i}: λ {got} против {expected}");
                if (want > remaining) limited++;
                else free++;
                remaining = MathF.Max(0, remaining - expected);
                sum += got;
            }
            Assert.True(sum <= budget * (1 + 1e-3f), $"круг {round}: сумма λ {sum} больше бюджета {budget}");
            Assert.True(w.KinematicPairUsed(pusher, target) <= budget);
        }
        Assert.True(limited > 20 && free > 20, $"случаи: упёрлись в бюджет {limited}, свободные {free}");
    }

    [Fact]
    public void PairBudget_ResetsEverySubstep_AndPairsAreIndependent()
    {
        var w = World(substeps: 4);
        var a = Pusher(w, 0.1f, new Vector3(0, 0, 0));
        var b = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        var c = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, -0.15f));
        var d = Pusher(w, 0.1f, new Vector3(-0.15f, 0, 0.15f));   // второй толкающий той же цели — вдоль X
        w.Step(Dt);

        // каждая пара (A→B, A→C, D→B) каждый подшаг берёт свой полный бюджет: F·h² · 4 / h = F·dt
        float fdt = PhysicsSettings.Default.PushForce * Dt;
        var pb = Momentum(w, b);
        var pc = Momentum(w, c);
        // B сдвигается за тик на доли миллиметра — нормали A→B и D→B чуть поворачиваются: допуск 3e-3
        Near(fdt, pb.Z, 3e-3f, "A→B");
        Near(fdt, pb.X, 3e-3f, "D→B");
        Near(-fdt, pc.Z, 1e-3f, "A→C");
        Assert.True(MathF.Abs(pc.X) < 1e-6f * fdt);
        Assert.Equal(Vector3.Zero, Momentum(w, a) + Momentum(w, d));   // кинематические не двигаются
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void ForcePerTick_IndependentOfSubsteps(int substeps)
    {
        var w = World(substeps);
        // цель — в начале координат: скорость — разность положений, у нуля float точнее всего
        var pusher = Pusher(w, 0.1f, new Vector3(0, 0, -0.15f));
        var target = Target(w, 100, 0.1f, Vacuum, Vector3.Zero);
        var trace = w.EnableTrace();
        for (int t = 0; t < 3; t++) w.Step(Dt);
        float fdt = PhysicsSettings.Default.PushForce * Dt;
        Near(3 * fdt, Momentum(w, target).Z, 1e-3f, $"импульс за 3 тика при {substeps} подшагах");
        Near(3 * fdt, trace.Run.Source(PhysicsSource.PushBody).Applied, 1e-4f, "Σλ/h трассы");
        Assert.Equal(1f, trace.Run.PairShare);
        Assert.Equal(3 * substeps, trace.Run.PairSubsteps);
        _ = pusher;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reaction_OpposesCorrectionOfPhysicalSide_InBothRoles(bool pusherFirst)
    {
        var w = World(substeps: 4);
        var n = Vector3.Normalize(new Vector3(1, 0.5f, -0.3f));
        ArticulationHandle pusher, target;
        if (pusherFirst)
        {
            pusher = Pusher(w, 0.1f, Vector3.Zero);
            target = Target(w, 20, 0.1f, Vacuum, n * 0.17f);
        }
        else
        {
            target = Target(w, 20, 0.1f, Vacuum, n * 0.17f);
            pusher = Pusher(w, 0.1f, Vector3.Zero);
        }
        Assert.Equal(pusherFirst, pusher.Index < target.Index);
        w.Step(Dt);
        w.Step(Dt);

        var p = Momentum(w, target);
        Assert.True(Vector3.Dot(p, n) > 0.5f * p.Length(), "цель толкают от толкающего");
        var reaction = w.Reaction(pusher);
        Assert.True(Vector3.Distance(-p, reaction) <= 1e-3f * p.Length(), $"отдача {reaction} против −импульса цели {-p}");
        Assert.Equal(Vector3.Zero, w.Reaction(target));
        // Reaction читает, не забирая; TakeReaction забирает
        Assert.Equal(reaction, w.Reaction(pusher));
        Assert.Equal(reaction, w.TakeReaction(pusher));
        Assert.Equal(Vector3.Zero, w.Reaction(pusher));
        Assert.Equal(Vector3.Zero, w.TakeReaction(pusher));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    [InlineData(25f)]
    public void LightPiece_PushedNoFasterThanPusherPlusPopSpeed(float pusherSpeed)
    {
        var s = PhysicsSettings.Default;
        var w = World(substeps: 4);
        var pusher = Pusher(w, 0.1f, Vector3.Zero);
        var piece = Target(w, 0.3f, 0.05f, Vacuum, new Vector3(0, 0, 0.03f));
        var pb = w.Body(pusher, 0);
        var velocity = new Vector3(0, 0, pusherSpeed);
        float limit = MathF.Min(pusherSpeed, s.KinematicPointSpeedMax) + s.PopSpeed, top = 0;
        for (int t = 0; t < 8; t++)
        {
            // толкающий каждый тик — за частью внахлёст 12 см (глубины хватает на весь тик) со скоростью pusherSpeed
            var at = w.Get(w.Body(piece, 0)).Position - new Vector3(0, 0, 0.03f);
            w.Place(pb, at - velocity * Dt, Quaternion.Identity);
            w.SetKinematicTarget(pb, at, Quaternion.Identity, Dt);
            w.Step(Dt);
            float v = w.Get(w.Body(piece, 0)).Velocity.Z;
            Assert.True(v <= limit * (1 + 1e-4f), $"тик {t}: часть {v} м/с быстрее толкающего + выталкивание ({limit})");
            top = MathF.Max(top, v);
        }
        // упирается именно предел скорости: бюджет пары разогнал бы 0,3 кг на 5,6 м/с за подшаг
        Assert.True(top >= limit - 0.05f, $"часть разогналась до {top} при пределе {limit}");
    }

    [Fact]
    public void NoReaction_PushesWithoutReaction()
    {
        var w = World();
        var body = Pusher(w, 0.1f, Vector3.Zero);
        var severed = Pusher(w, 0.1f, new Vector3(2, 0, 0));
        Source(w, severed, KinematicSource.NoReaction);
        var t1 = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        var t2 = Target(w, 100, 0.1f, Vacuum, new Vector3(2, 0, 0.15f));
        w.Step(Dt);
        float fdt = PhysicsSettings.Default.PushForce * Dt;
        Near(fdt, Momentum(w, t1).Z, 1e-3f, "толчок тела");
        Near(fdt, Momentum(w, t2).Z, 1e-3f, "толчок за отрубом — тот же бюджет");
        Near(-fdt, w.Reaction(body).Z, 1e-4f, "отдача тела");
        Assert.Equal(Vector3.Zero, w.Reaction(severed));
    }

    [Theory]
    [InlineData(0.5f, 4f)]    // потенциал 1 Н·с: проход меньше бюджета пары за подшаг
    [InlineData(2f, 16f)]     // потенциал 16 Н·с: упирается бюджет пары
    public void Blade_DebitsPassAndPairTogether(float mass, float speed)
    {
        const float h = TestBodies.H;
        var s = PhysicsSettings.Default;
        var w = World(substeps: 1);
        var pusher = Pusher(w, 0.1f, Vector3.Zero);
        Source(w, pusher, KinematicSource.Pass);
        var target = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        w.PreparePass(pusher, 1, mass, speed);
        float potential = s.PassShare * mass * speed;
        Assert.Equal(potential, w.Pass(pusher, target).Potential);
        w.Step(h);

        float lambda = Momentum(w, target).Z * h;   // импульс цели · h = Σλ
        float expected = MathF.Min(Budget(h), potential * h);
        Near(expected, lambda, 1e-3f, "λ клинка");
        var pass = w.Pass(pusher, target);
        Assert.True(pass.Inside);
        Assert.Equal(1, pass.Number);
        // одна λ — из обоих бюджетов: проход потерял λ/h, пара израсходовала λ
        Near(lambda, (pass.Potential - pass.Remaining) * h, 1e-3f, "списано из прохода");
        Near(lambda, w.KinematicPairUsed(pusher, target), 1e-3f, "списано из пары");
        Assert.True(pass.Remaining >= 0);
        Near(-lambda / h, w.Reaction(pusher).Z, 1e-3f, "отдача клинка");
    }
}
