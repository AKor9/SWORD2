using Sword.Engine.Physics;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Тела для наблюдателей (<see cref="IBodyView"/>): чтение подсистемы Physics без права её менять; сегменты суставов
/// скелета — <see cref="JointSegments"/> (5в).
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
        var s = physics.Get(physics.Body(FighterBodies.Handle(body), index));
        return new BodySnapshot(s.Position, s.Rotation, s.PrevPosition, s.PrevRotation, s.Velocity, s.AngularVelocity, s.Radius, s.HalfLength);
    }

    public int Articulations => physics.ArticulationCount;

    public int Bodies => physics.LiveBodyCount;

    public int JointCount => jointSegments.Length;

    public int JointSegment(int joint) => jointSegments[joint];
}
