using System.Numerics;

namespace Sword.Engine.Physics;

// 7д: толчки живых (кинематических) тел — бюджет пары, проход, отдача, импульс удара. Заготовки контракта 7б — точка 7д
// заменяет их реализацией и реализует крючки KinematicGrow, KinematicReset, BeginKinematicSubstep, PushKinematic,
// EndKinematicSubstep (объявлены в PhysicsWorld.cs).
public sealed partial class PhysicsWorld
{
    public void PreparePass(ArticulationHandle pusher, int strike, float mass, float speed) => throw new NotImplementedException("7д: проход");

    public void PassHit(ArticulationHandle pusher, ArticulationHandle target) => throw new NotImplementedException("7д: проход");

    public float SpendHit(ArticulationHandle pusher, ArticulationHandle target, float impulse) => throw new NotImplementedException("7д: проход");

    public Vector3 ApplyHit(BodyHandle body, Vector3 point, Vector3 impulse, float impactSpeed, ArticulationHandle pusher) =>
        throw new NotImplementedException("7д: импульс удара");

    public PassState Pass(ArticulationHandle pusher, ArticulationHandle target) => throw new NotImplementedException("7д: проход");

    public Vector3 Reaction(ArticulationHandle articulation) => throw new NotImplementedException("7д: отдача");

    public Vector3 TakeReaction(ArticulationHandle articulation) => throw new NotImplementedException("7д: отдача");

    public void EndKinematicTick() => throw new NotImplementedException("7д: конец тика толчков");
}
