namespace Sword.Engine.Physics;

// 7г: статический мир (земля, стена, колонны), контакты, границы сочленений. Заготовки контракта 7б — точка 7г заменяет их
// реализацией и реализует крючки ContactsGrow, ContactsReset (объявлены в PhysicsWorld.cs); помощники контактов с землёй —
// Ground.cs; шаг — PhysicsWorld.Step.cs.
public sealed partial class PhysicsWorld
{
    public void SetGround(IGround? ground) => throw new NotImplementedException("7г: земля");

    public void SetWall(float radius) => throw new NotImplementedException("7г: стена");

    public void SetPillars(ReadOnlySpan<Pillar> pillars) => throw new NotImplementedException("7г: колонны");

    public ArticulationBounds Bounds(ArticulationHandle articulation) => throw new NotImplementedException("7г: границы сочленений");
}
