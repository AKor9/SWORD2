using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Movement;

namespace Sword.Game;

/// <summary>
/// Ядро игры (принцип 4 плана: то, без чего игры нет, — одна реализация, не модуль и не выключается). Точка 1в:
/// компоненты и команда игрока из <c>Game.Contracts</c>, системы движения корня, начальный мир — игрок в центре арены
/// курсом 0 (<c>DuelArena.Reset</c> SWORD: <c>Build(Player, 0, 0)</c>). Регистрации ядра — без токена модуля.
/// </summary>
public static class GameCore
{
    /// <summary>Регистрации ядра — в <c>Simulation.Build</c> до модулей.</summary>
    public static void Configure(SimulationBuilder b)
    {
        b.Component<Transform>().Component<PrevTransform>().Component<RootVelocity>().Component<FighterCommand>()
            .Component<GazeTarget>().Component<Fighter>().Component<PlayerTag>()
            .Command<PlayerCommand>();

        b.System(GameStages.Begin, new PrevTransformSystem())
            .System(GameStages.Intent, new PlayerCommandSystem())
            .System(GameStages.Movement, new RootControlSystem())
            .System(GameStages.Movement, new RootCollisionSystem());

        b.OnWorldBuilt(w => SpawnPlayer(w));
    }

    /// <summary>Боец игрока — единственный с <see cref="PlayerTag"/>; ядро создаёт его при сборке мира.</summary>
    public static Entity Player(WorldReader world)
    {
        foreach (var chunk in world.Iterate(world.Query().With<PlayerTag>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("в мире нет игрока — ядро игры не собрано (GameCore.Configure)");
    }

    /// <summary>Игрок: в центре, курсом 0, стоит, точки взгляда ещё нет.</summary>
    public static Entity SpawnPlayer(World world)
    {
        var e = world.CreateImmediate();
        world.AddImmediate(e, new Fighter());
        world.AddImmediate(e, new PlayerTag());
        world.AddImmediate(e, new Transform());
        world.AddImmediate(e, new PrevTransform());
        world.AddImmediate(e, new RootVelocity());
        world.AddImmediate(e, new FighterCommand());
        world.AddImmediate(e, new GazeTarget());
        return e;
    }
}
