using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Game.Tests;

/// <summary>
/// Зонд-система теста: в стадии <paramref name="stage"/> между <paramref name="after"/> и <paramref name="before"/> читает мир
/// посреди тика — свойство, которого между тиками уже не видно. Только читает; подключается модулем.
/// </summary>
internal sealed class StageProbe(string name, string stage, Action<WorldReader, ulong> check, SystemLabel? after = null,
    SystemLabel? before = null) : IModule, IContinuousSystem
{
    public string Id => name;

    public void Configure(SimulationBuilder b) => b.System(stage, this);

    public void Setup(SystemSetup s)
    {
        s.Label(new SystemLabel("test." + name));
        if (after is { } a) s.After(a);
        if (before is { } b) s.Before(b);
    }

    public void Run(in SystemContext ctx) => check(ctx.World.Reader, ctx.Tick);
}
