using Sword.Engine.Physics;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Тела для наблюдателей (<see cref="IBodyView"/>): чтение подсистемы Physics без права её менять; сегменты суставов
/// скелета — <see cref="JointSegments"/> (5в). С 7ж снимок тела несёт и динамику (поза начала тика, масса, режим, профиль, касание земли).
/// </summary>
public sealed class BodyView(IPhysicsWorld physics, int[] jointSegments) : IBodyView
{
    public int BodyCount(BodyRef body)
    {
        var a = FighterBodies.Handle(body);
        return physics.IsAlive(a) ? physics.BodyCount(a) : 0;
    }

    public BodySnapshot Body(BodyRef body, int index) => Snapshot(physics.Get(physics.Body(FighterBodies.Handle(body), index)));

    /// <summary>Снимок тела для наблюдателей из состояния подсистемы.</summary>
    public static BodySnapshot Snapshot(in BodyState s) =>
        new(s.Position, s.Rotation, s.PrevPosition, s.PrevRotation, s.Velocity, s.AngularVelocity, s.Radius, s.HalfLength, s.StartPosition,
            s.StartRotation, s.Mass, s.Mode == BodyMode.Simulated, s.Profile, s.Grounded);

    public int Articulations => physics.ArticulationCount;

    public int Bodies => physics.LiveBodyCount;

    public int JointCount => jointSegments.Length;

    public int JointSegment(int joint) => jointSegments[joint];
}
