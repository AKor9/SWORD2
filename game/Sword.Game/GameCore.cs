using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Movement;

namespace Sword.Game;

/// <summary>
/// Ядро игры (принцип 4 плана: то, без чего игры нет, — одна реализация, не модуль и не выключается): компоненты и
/// команда игрока из <c>Game.Contracts</c>, движение корня, аниматор бойца (поза в пуле поз, слой движения для
/// локомоции), начальный мир — игрок в центре арены курсом 0 (<c>DuelArena.Reset</c> SWORD: <c>Build(Player, 0, 0)</c>)
/// и <c>arena.enemies</c> врагов кольцом вокруг него; команды арены (<see cref="ArenaCommand"/>: число врагов, сброс —
/// этап 4а); ИИ врагов (<see cref="EnemyAiSystem"/> — этап 4б); столкновения корней бойцов между собой
/// (<see cref="RootCollisionSystem"/>, инвариант <see cref="RootOverlapInvariant"/> — этап 4в); слой действий аниматора — поворот
/// врага на месте клипом (<see cref="AnimatorAction"/>, <see cref="ActionLayer"/>) и команда врагу (<see cref="EnemyCommand"/>)
/// — этап 4г; тело бойца — сочленение капсул в подсистеме Physics за <see cref="BodyRef"/>, время жизни — хуками ECS
/// (<see cref="FighterBodies"/>, участник хеша <c>physics</c>, инварианты <c>physics.*</c> — этап 5а). Регистрации ядра — без токена модуля.
///
/// Порядок <c>KinematicBegin</c> SWORD — по стадиям: курс и разгон, у врага — начало поворота клипом (Gameplay) → аниматор
/// без поз по новой скорости: слой движения и шаг действия (Gameplay) → слои позы (локомоция, действие) и прямая
/// кинематика, пул поз исполняет задания (Animation) → поворот курса клипом, перенос корня и столкновения (Movement).
/// </summary>
public static class GameCore
{
    /// <summary>Регистрации ядра — в <c>Simulation.Build</c> до модулей.</summary>
    public static void Configure(SimulationBuilder b, CharacterAssets assets)
    {
        var poses = new PosePool(assets.Clips);   // пул поз — у каждой симуляции свой: ассеты только читаются
        // Физика — у каждой симуляции своя; слотов — на полную арену (33 бойца по 13 тел), дальше растёт удвоением.
        var physics = new PhysicsWorld(ArenaRules.MaxFighters, ArenaRules.MaxFighters * BodySegs.Bodies);
        var arena = b.Config.Section<ArenaSettings>("arena");
        if (arena.Enemies is < 0 or > ArenaState.MaxEnemies)
            throw new InvalidDataException($"arena.enemies = {arena.Enemies}: 0…{ArenaState.MaxEnemies}");
        b.Component<Transform>().Component<PrevTransform>().Component<RootVelocity>().Component<FighterCommand>()
            .Component<GazeTarget>().Component<Fighter>().Component<PlayerTag>().Component<EnemyTag>()
            .Component<FighterSlot>().Component<FighterSeed>().Component<EnemyAiTerms>().Component<RootContacts>()
            .Component<PoseRef>().Component<AnimatorMotion>().Component<AnimatorAction>().Component<ActionPoses>().Component<BodyRef>()
            .Command<PlayerCommand>().Command<ArenaCommand>().Command<EnemyCommand>()
            .Resource(poses).Resource(assets.Actions).Resource(new ArenaState { Enemies = arena.Enemies })
            .Resource<IPhysicsWorld>(physics)
            .ReadView<IAnimationView>(new AnimationView(poses))
            .StateContributor(new AnimationState(poses))
            .StateContributor(new PhysicsState(physics))
            .Invariants(AnimationInvariants.For(poses, assets.Actions))
            .Invariant(new ArenaSlotsInvariant())
            .Invariant(new RootOverlapInvariant())
            .Invariants(PhysicsInvariants.For(physics))
            // Представление узнаёт о бойцах из очереди кадра: появился — завести, ушёл — убрать (Extract клиента).
            .Export<Added<Fighter>>().Export<Removed<Fighter>>();
        FighterBodies.Register(b, physics);

        b.System(GameStages.Begin, new PrevTransformSystem())
            .System(GameStages.Intent, new ArenaCommandSystem())
            .System(GameStages.Intent, new PlayerCommandSystem())
            .System(GameStages.Intent, new EnemyAiSystem())
            .System(GameStages.Gameplay, new FighterSetupSystem())
            .System(GameStages.Gameplay, new EnemyCommandSystem())
            .System(GameStages.Gameplay, new RootSteerSystem())
            .System(GameStages.Gameplay, new AnimatorInputSystem())
            .System(GameStages.Animation, new AnimatorPoseSystem())
            .System(GameStages.Animation, new AnimationTickSystem())
            .System(GameStages.Movement, new RootControlSystem())
            .System(GameStages.Movement, new RootCollisionSystem())
            .System(GameStages.End, new PoseReleaseSystem())
            .System(GameStages.End, new ActionPoseReleaseSystem());

        b.OnWorldBuilt(w => SpawnArena(w, arena.Enemies));
    }

    /// <summary>Боец игрока — единственный с <see cref="PlayerTag"/>; ядро создаёт его при сборке мира.</summary>
    public static Entity Player(WorldReader world)
    {
        foreach (var chunk in world.Iterate(world.Query().With<PlayerTag>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("в мире нет игрока — ядро игры не собрано (GameCore.Configure)");
    }

    /// <summary>
    /// Начальная арена (<c>DuelArena.Reset(enemies)</c> SWORD): игрок в центре курсом 0 (место 0), затем враги на местах
    /// 1…<paramref name="enemies"/> кольцом вокруг него (<see cref="ArenaRules.Enemy"/>, партия 0 — сборка мира).
    /// </summary>
    public static void SpawnArena(World world, int enemies)
    {
        var rng = world.Resource<SimRandom>();
        ulong tick = world.Resource<SimTime>().Tick;
        var player = SpawnPlayer(world);
        var center = world.Get<Transform>(player).Position;
        for (int slot = 1; slot <= enemies; slot++) Fighters.Create(world, ArenaRules.Enemy(rng, tick, 0, slot, center));
    }

    /// <summary>Игрок: в центре, курсом 0, стоит, точки взгляда ещё нет; поза — поза привязки до первого тика.</summary>
    public static Entity SpawnPlayer(World world) =>
        Fighters.Create(world, ArenaRules.Player(world.Resource<SimRandom>(), world.Resource<SimTime>().Tick, 0));
}
