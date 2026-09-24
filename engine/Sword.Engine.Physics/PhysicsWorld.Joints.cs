namespace Sword.Engine.Physics;

// 7в: суставы. Заготовки контракта 7б — точка 7в заменяет их реализацией и реализует крючки JointsGrow, JointsReset,
// SolveJoints, RelaxJoints (объявлены в PhysicsWorld.cs); внутренние типы — Joints.cs.
public sealed partial class PhysicsWorld
{
    public void SetJoints(ArticulationHandle articulation, ReadOnlySpan<JointDesc> joints) => throw new NotImplementedException("7в: суставы");

    public int JointCount(ArticulationHandle articulation) => throw new NotImplementedException("7в: суставы");

    public JointDesc Joint(ArticulationHandle articulation, int joint) => throw new NotImplementedException("7в: суставы");

    public void RebindJoints(ArticulationHandle articulation) => throw new NotImplementedException("7в: суставы");

    public void SetJointEnabled(ArticulationHandle articulation, int joint, bool enabled) => throw new NotImplementedException("7в: суставы");

    public bool IsJointEnabled(ArticulationHandle articulation, int joint) => throw new NotImplementedException("7в: суставы");

    public JointBinding Binding(ArticulationHandle articulation, int joint) => throw new NotImplementedException("7в: суставы");

    public ReadOnlySpan<JointLimitHit> JointLimitHits(ArticulationHandle articulation) => throw new NotImplementedException("7в: суставы");

    public JointMeasure MeasureJoint(ArticulationHandle articulation, int joint) => throw new NotImplementedException("7в: суставы");
}
