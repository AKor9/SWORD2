using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Physics.Tests.TestBodies;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Трение суставов (7з-4, <see cref="PhysicsSettings.JointFriction"/>): вязкость в расслаблении подшага гасит относительную угловую скорость частей
/// в суставе — примерно в <c>e^k</c> раз за секунду; энергии не добавляет (шаг за шагом), момент импульса пары сохраняет; 0 — трения нет:
/// относительное вращение идёт, как шло. Боец в вакууме со случайными толчками за 10 с энергию не набирает, части с трением вращаются
/// относительно друг друга медленнее.
/// </summary>
public class JointFrictionTests(ITestOutputHelper output)
{
    /// <summary>
    /// Пара «родитель — ребёнок» в вакууме, сустав наклона-скручивания с пределами шире движения (±3 рад): ребёнок крутится вокруг своей оси
    /// (скручивание, точка сустава на оси — не двигается) 5 рад/с. Относительная угловая скорость, энергия и момент импульса за 0,4 с.
    /// </summary>
    private static (float Relative, float EnergyEnd, float EnergyStart, bool Monotone, float Momentum) Twist(float friction)
    {
        var (w, art, parent, child) = JointsRig.Pair(BodyMode.Simulated, BodyMode.Simulated);
        w.Settings = w.Settings with { JointFriction = friction };
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -3, 3, 3, 3)]);
        JointsRig.SetVelocity(w, child, Vector3.Zero, new Vector3(0, 5, 0));
        Vector3 L() => AngularMomentum(w.Get(parent)) + AngularMomentum(w.Get(child));
        var l0 = L();
        float e0 = JointsRig.Energy(w, art, 0), prev = e0;
        bool monotone = true;
        for (int t = 0; t < 48; t++)
        {
            w.Step(JointsRig.Tick);
            float e = JointsRig.Energy(w, art, 0);
            monotone &= e <= prev * (1 + 1e-5f) + 1e-7f;
            prev = e;
        }
        float relative = (w.Get(child).AngularVelocity - w.Get(parent).AngularVelocity).Length();
        return (relative, prev, e0, monotone, (L() - l0).Length() / l0.Length());
    }

    /// <summary>Момент импульса тела относительно начала координат: <c>I·ω + r × m·v</c> (инерция в мире — из обратной).</summary>
    private static Vector3 AngularMomentum(in BodyState s)
    {
        var m = Matrix4x4.CreateFromQuaternion(s.Rotation);
        var local = Vector3.Transform(s.AngularVelocity, Quaternion.Conjugate(s.Rotation));
        var inertia = new Vector3(1 / s.InvInertia.X, 1 / s.InvInertia.Y, 1 / s.InvInertia.Z);
        return Vector3.Transform(local * inertia, s.Rotation) + Vector3.Cross(s.Position, s.Velocity * s.Mass);
    }

    [Fact]
    public void JointFriction_DampsRelativeRotation_NoEnergyAdded_AngularMomentumKept_ZeroOff()
    {
        var free = Twist(0);
        var damped = Twist(10);
        output.WriteLine($"скручивание 5 рад/с, 0,4 с: без трения — относительная {free.Relative:F3} рад/с, энергия {free.EnergyStart:F4} → {free.EnergyEnd:F4} Дж; трение 10 1/с — {damped.Relative:F4} рад/с, энергия → {damped.EnergyEnd:F4} Дж, момент импульса ушёл на долю {damped.Momentum:G3}");
        Assert.True(free.Relative >= 4.9f, $"без трения относительное вращение затухло: {free.Relative} рад/с");
        Assert.True(free.EnergyEnd <= free.EnergyStart && free.EnergyEnd >= 0.99f * free.EnergyStart, $"без трения энергия {free.EnergyStart} → {free.EnergyEnd}");
        // e^(−10·0,4) ≈ 0,018: гасится почти всё; неявный Эйлер по подшагам — чуть слабее
        Assert.True(damped.Relative <= 0.05f * 5, $"трение не погасило вращение: {damped.Relative} рад/с");
        Assert.True(damped.Monotone, "трение суставов добавило энергию");
        // пара докручивается вместе: остаётся доля I_c / (I_p + I_c) энергии (≈ 0,2 — у ребёнка меньше осевая инерция)
        Assert.True(damped.EnergyEnd < 0.5f * damped.EnergyStart, $"энергия {damped.EnergyEnd} из {damped.EnergyStart}");
        Assert.True(damped.Momentum <= 1e-3f, $"момент импульса пары ушёл на долю {damped.Momentum}");
    }

    /// <summary>
    /// Боец (14 капсул, 13 суставов, пределы бойца) в вакууме, случайные толчки до 15 рад/с, 10 с: с трением суставов 5 1/с энергия не растёт (как
    /// без трения — суставы и пределы работы не совершают), части к концу вращаются относительно друг друга медленнее (сумма относительных угловых
    /// скоростей по суставам — меньше, чем без трения; энергия — не обязательно: трение сводит движение к вращению бойца целиком, которое в вакууме
    /// гасить нечему, а без трения его рассеивают удары о пределы); суставы держат.
    /// </summary>
    [Fact]
    public void JointFriction_FreeFighter_10s_NoEnergyGain_PartsCalmerThanWithout()
    {
        float Run(float friction, int seed, out float gain, out float excess)
        {
            var w = Fighter(friction, seed, out gain, out excess, out var art);
            float relative = 0;
            for (int j = 0; j < w.JointCount(art); j++)
            {
                var d = w.Joint(art, j);
                relative += (w.Get(w.Body(art, d.Child)).AngularVelocity - w.Get(w.Body(art, d.Parent)).AngularVelocity).Length();
            }
            return relative;
        }

        PhysicsWorld Fighter(float friction, int seed, out float gain, out float excess, out ArticulationHandle art)
        {
            var rng = new Random(seed);
            var w = JointsRig.World();
            w.Settings = w.Settings with { JointFriction = friction };
            art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
            w.SetJoints(art, JointsRig.FighterJoints(w, art));
            for (int i = 0; i < 14; i++) JointsRig.SetVelocity(w, w.Body(art, i), RandomVector(rng, 3), RandomVector(rng, 15));
            w.Step(JointsRig.Tick);   // первый шаг согласует скорости тел
            float e0 = JointsRig.Energy(w, art, 0);
            gain = float.MinValue;
            excess = 0;
            for (int t = 1; t < 1200; t++)
            {
                w.Step(JointsRig.Tick);
                gain = MathF.Max(gain, (JointsRig.Energy(w, art, 0) - e0) / e0);
                if ((t & 3) == 0) excess = MathF.Max(excess, JointsRig.Worst(w, art).Excess);
            }
            return w;
        }

        for (int seed = 0; seed < 5; seed++)
        {
            float with = Run(5, 7000 + seed, out float gain, out float excess);
            float without = Run(0, 7000 + seed, out _, out _);
            output.WriteLine($"прогон {seed}: сумма относительных угловых скоростей в суставах через 10 с — {with:F3} рад/с с трением 5 1/с, {without:F3} без; рост энергии с трением ≤ доли {gain:G3}, выход ≤ {excess:G3} рад");
            Assert.True(gain <= 1e-3f, $"с трением суставов боец набрал энергию: доля {gain}");
            Assert.True(with < 0.5f * without, $"трение не успокоило части: {with} против {without} рад/с");
            Assert.True(excess <= 0.3f, $"выход за предел {excess} рад");
        }
    }
}
