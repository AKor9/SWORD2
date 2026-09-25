using System.Numerics;
using Sword.Engine.Physics;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Величины тела бойца целиком по снимкам его тел (<see cref="IBodyView"/>, <see cref="BodySnapshot"/>) — для наблюдателей: вычисляемые
/// поля агента (<c>Body.Com</c>, <c>Body.ComVelocity</c>, <c>Body.KineticEnergy</c>, <c>Body.Energy</c> — <c>watch.add</c>, <c>entity.history</c>,
/// метрики и проверки манифестов) и тесты. Центр масс и его скорость — по массам тел; кинетическая энергия — поступательная и вращательная
/// (инерция — точная капсулы, как у физики: <see cref="Dynamics.CapsuleInvInertia"/>); полная — плюс <c>m·g·y</c> (g — профиля по
/// умолчанию, его берёт и профиль трупа). У живого бойца — те же величины по кинематическим телам.
/// </summary>
public static class CorpseMetrics
{
    /// <summary>Ускорение свободного падения профилей тел бойца, м/с².</summary>
    public static readonly float Gravity = new BodyDynamics().Gravity;

    /// <summary>Кинетическая энергия тела, Дж: <c>½ m v² + ½ ω · I ω</c> (инерция — по форме в системе тела).</summary>
    public static float KineticEnergy(in BodySnapshot b)
    {
        if (!(b.Mass > 0)) return 0;
        float e = 0.5f * b.Mass * b.Velocity.LengthSquared();
        var inv = Dynamics.CapsuleInvInertia(b.Mass, b.Radius, b.HalfLength);
        var w = Vector3.Transform(b.AngularVelocity, Quaternion.Conjugate(b.Rotation));
        if (inv.X > 0) e += 0.5f * w.X * w.X / inv.X;
        if (inv.Y > 0) e += 0.5f * w.Y * w.Y / inv.Y;
        if (inv.Z > 0) e += 0.5f * w.Z * w.Z / inv.Z;
        return e;
    }

    /// <summary>Потенциальная энергия тела, Дж: <c>m·g·y</c>.</summary>
    public static float PotentialEnergy(in BodySnapshot b) => b.Mass * Gravity * b.Position.Y;

    /// <summary>Итоги по телам бойца: масса, центр масс, скорость центра масс, кинетическая и полная энергия.</summary>
    public readonly record struct Totals(float Mass, Vector3 Com, Vector3 ComVelocity, float KineticEnergy, float Energy);

    /// <summary>Итоги по всем телам <paramref name="body"/> (нет тел или массы — масса 0, центр — NaN).</summary>
    public static Totals Of(IBodyView view, BodyRef body)
    {
        int n = view.BodyCount(body);
        float mass = 0, ke = 0, pe = 0;
        Vector3 p = Vector3.Zero, v = Vector3.Zero;
        for (int k = 0; k < n; k++)
        {
            var b = view.Body(body, k);
            mass += b.Mass;
            p += b.Position * b.Mass;
            v += b.Velocity * b.Mass;
            ke += KineticEnergy(b);
            pe += PotentialEnergy(b);
        }
        return mass > 0 ? new Totals(mass, p / mass, v / mass, ke, ke + pe) : new Totals(0, new Vector3(float.NaN), new Vector3(float.NaN), 0, 0);
    }
}
