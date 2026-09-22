using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;
using Sword.Game.Movement;

namespace Sword.Game;

/// <summary>
/// Ядро игры (принцип 4 плана: то, без чего игры нет, — одна реализация, не модуль и не выключается): компоненты и
/// команда игрока из <c>Game.Contracts</c>, движение корня, аниматор бойца (поза в пуле поз, слой движения для
/// локомоции), начальный мир — игрок в центре арены курсом 0 (<c>DuelArena.Reset</c> SWORD: <c>Build(Player, 0, 0)</c>).
/// Регистрации ядра — без токена модуля.
///
/// Порядок <c>KinematicBegin</c> SWORD — по стадиям: курс и разгон (Gameplay) → вход аниматора по новой скорости
/// (Gameplay) → слои позы и прямая кинематика, пул поз исполняет задания (Animation) → перенос корня и столкновения
/// (Movement).
/// </summary>
public static class GameCore
{
    /// <summary>Регистрации ядра — в <c>Simulation.Build</c> до модулей.</summary>
    public static void Configure(SimulationBuilder b, CharacterAssets assets)
    {
        var poses = new PosePool(assets.Clips);   // пул поз — у каждой симуляции свой: ассеты только читаются
        b.Component<Transform>().Component<PrevTransform>().Component<RootVelocity>().Component<FighterCommand>()
            .Component<GazeTarget>().Component<Fighter>().Component<PlayerTag>()
            .Component<PoseRef>().Component<AnimatorMotion>()
            .Command<PlayerCommand>()
            .Resource(poses).Resource(assets.Actions)
            .ReadView<IAnimationView>(new AnimationView(poses))
            // Представление узнаёт о бойцах из очереди кадра: появился — завести, ушёл — убрать (Extract клиента).
            .Export<Added<Fighter>>().Export<Removed<Fighter>>();

        b.System(GameStages.Begin, new PrevTransformSystem())
            .System(GameStages.Intent, new PlayerCommandSystem())
            .System(GameStages.Gameplay, new RootSteerSystem())
            .System(GameStages.Gameplay, new AnimatorInputSystem())
            .System(GameStages.Animation, new AnimatorPoseSystem())
            .System(GameStages.Animation, new AnimationTickSystem())
            .System(GameStages.Movement, new RootControlSystem())
            .System(GameStages.Movement, new RootCollisionSystem())
            .System(GameStages.End, new PoseReleaseSystem());

        b.OnWorldBuilt(w => SpawnPlayer(w));
    }

    /// <summary>Боец игрока — единственный с <see cref="PlayerTag"/>; ядро создаёт его при сборке мира.</summary>
    public static Entity Player(WorldReader world)
    {
        foreach (var chunk in world.Iterate(world.Query().With<PlayerTag>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("в мире нет игрока — ядро игры не собрано (GameCore.Configure)");
    }

    /// <summary>Игрок: в центре, курсом 0, стоит, точки взгляда ещё нет; поза — поза привязки до первого тика.</summary>
    public static Entity SpawnPlayer(World world)
    {
        var e = world.CreateImmediate();
        world.AddImmediate(e, new Fighter());
        world.AddImmediate(e, new PlayerTag());
        // Прошлый корень — тот же, что нынешний: боец, появившийся посреди тика, не «прилетает» в кадр из начала
        // координат (в SWORD: RenderPos = Pos при постройке).
        var root = new Transform();
        world.AddImmediate(e, root);
        world.AddImmediate(e, new PrevTransform { Position = root.Position, Facing = root.Facing });
        world.AddImmediate(e, new RootVelocity());
        world.AddImmediate(e, new FighterCommand());
        world.AddImmediate(e, new GazeTarget());
        world.AddImmediate(e, new AnimatorMotion());
        var pose = world.Resource<PosePool>().Alloc();
        world.AddImmediate(e, new PoseRef { Index = pose.Index, Generation = pose.Generation });
        return e;
    }
}
