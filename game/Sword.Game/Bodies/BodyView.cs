using Sword.Engine.Physics;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Тела для наблюдателей (<see cref="IBodyView"/>): чтение подсистемы Physics без права её менять; сегменты суставов
/// скелета — <see cref="JointSegments"/>. Снимок тела несёт и динамику (поза начала тика, масса, режим, профиль, касание земли)
/// и сон сочленения (снимок, <see cref="IsAsleep"/>, <see cref="RestTime"/>).
/// </summary>
public sealed class BodyView(IPhysicsWorld physics, int[] jointSegments) : IBodyView
{
    public int BodyCount(BodyRef body)
    {
        var a = FighterBodies.Handle(body);
        return physics.IsAlive(a) ? physics.BodyCount(a) : 0;
    }

    public BodySnapshot Body(BodyRef body, int index)
    {
        var a = FighterBodies.Handle(body);
        return Snapshot(physics.Get(physics.Body(a, index)), physics.IsAsleep(a));
    }

    /// <summary>Снимок тела для наблюдателей из состояния подсистемы (<paramref name="asleep"/> — спит ли его сочленение).</summary>
    public static BodySnapshot Snapshot(in BodyState s, bool asleep = false) =>
        new(s.Position, s.Rotation, s.PrevPosition, s.PrevRotation, s.Velocity, s.AngularVelocity, s.Radius, s.HalfLength, s.StartPosition,
            s.StartRotation, s.Mass, s.Mode == BodyMode.Simulated, s.Profile, s.Grounded, asleep);

    public int Articulations => physics.ArticulationCount;

    public int Bodies => physics.LiveBodyCount;

    public bool IsAsleep(BodyRef body)
    {
        var a = FighterBodies.Handle(body);
        return physics.IsAlive(a) && physics.IsAsleep(a);
    }

    public float RestTime(BodyRef body)
    {
        var a = FighterBodies.Handle(body);
        return physics.IsAlive(a) ? physics.RestTime(a) : 0;
    }

    public int AsleepCount => physics.AsleepCount;

    public int JointCount => jointSegments.Length;

    public int JointSegment(int joint) => jointSegments[joint];
}
