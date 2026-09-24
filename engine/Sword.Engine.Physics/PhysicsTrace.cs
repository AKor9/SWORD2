namespace Sword.Engine.Physics;

// 7д: трасса физики. Заготовки контракта 7б — точка 7д заменяет их реализацией, дописывает члены PhysicsTrace (объявлен в
// Contract.cs) и реализует крючки Trace* (объявлены в PhysicsWorld.cs; поле _trace — там же).
public sealed partial class PhysicsWorld
{
    public PhysicsTrace EnableTrace(PhysicsTrace? trace = null) => throw new NotImplementedException("7д: трасса");

    public void DisableTrace() => throw new NotImplementedException("7д: трасса");
}
