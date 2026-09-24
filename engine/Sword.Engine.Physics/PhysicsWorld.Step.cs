namespace Sword.Engine.Physics;

// 7г: шаг физики. Заготовка контракта 7б — точка 7г заменяет её реализацией: порядок — IPhysicsWorld.Step; суставы (7в),
// толчки и трасса (7д) — через крючки PhysicsWorld.cs (SolveJoints, RelaxJoints, BeginKinematicSubstep, PushKinematic,
// EndKinematicSubstep, Trace*).
public sealed partial class PhysicsWorld
{
    public void Step(float dt) => throw new NotImplementedException("7г: шаг");
}
