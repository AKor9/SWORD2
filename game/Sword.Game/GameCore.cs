using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Movement;
using Sword.Game.Ragdoll;

namespace Sword.Game;

/// <summary>
/// Ядро игры — то, без чего игры нет: одна реализация, не модуль и не выключается. Регистрации ядра — без токена
/// модуля; устройство подсистем — в README игры.
/// </summary>
public static class GameCore
{
    /// <summary>
    /// Поз пула на бойца в худшем случае: поза бойца, слой и снимок локомоции, слой и снимок действия, по две позы памяти
    /// смешивания, перехода от снимка и вздрагивания.
    /// </summary>
    public const int PosesPerFighter = 11;

    /// <summary>
    /// Заданий пула на бойца за тик в худшем случае: локомоция до 4, действие до 3, снимок 1, вздрагивание до 6, прямая
    /// кинематика 1.
    /// </summary>
    public const int PoseJobsPerFighter = 16;

    /// <summary>
    /// Запас пула поз на арену из <paramref name="enemies"/> врагов: бой не растит пул посреди игры, тик боя — 0 Б. Зовут
    /// постройка симуляции и команда арены, прибавившая врагов (до постройки новых бойцов). Логика пула от запаса не
    /// зависит (<see cref="PosePool.Reserve"/>).
    /// </summary>
    public static void ReservePoses(PosePool poses, int enemies)
    {
        int fighters = Math.Clamp(enemies, 0, ArenaState.MaxEnemies) + 1;
        poses.Reserve(fighters * PosesPerFighter, fighters * PoseJobsPerFighter);
    }

    /// <summary>Тупое ли оружие по номеру таблицы (<c>combat.bluntWeapons</c>; имя не из пака — пропуск).</summary>
    private static bool[] BluntTable(WeaponTable weapons, IEnumerable<string> blunt)
    {
        var table = new bool[weapons.Count];
        foreach (string name in blunt)
            if (weapons.Find(name) is >= 0 and var w) table[w] = true;
        return table;
    }

    /// <summary>Регистрации ядра — в <c>Simulation.Build</c> до модулей.</summary>
    public static void Configure(SimulationBuilder b, CharacterAssets assets)
    {
        var poses = new PosePool(assets.Clips);   // пул поз — у каждой симуляции свой: ассеты только читаются
        // Правила трупа — ресурс мира из секции ragdoll (ручки «Рэгдолл»); настройки физики и профиль трупа из них производны.
        var ragdoll = RagdollRules.From(b.Config.Section<RagdollSettings>(RagdollKnobs.Section));
        // Физика — у каждой симуляции своя; слотов — на полную арену (33 бойца по 14 тел — с клинком), дальше растёт удвоением.
        var physics = new PhysicsWorld(ArenaRules.MaxFighters, ArenaRules.MaxFighters * BodySegs.Bodies, ragdoll.Physics(PhysicsSettings.Default));
        // Статический мир трупов — аналитическая земля арены (рельеф и ярусы пирамиды), стена и колонны; профиль трупа — №1.
        physics.SetGround(ArenaGround.Instance);
        physics.SetWall(ArenaGround.WallRadius);
        physics.SetPillars(ArenaGround.Pillars);
        int corpseProfile = physics.RegisterDynamics(ragdoll.Corpse());
        var corpseJoints = new CorpseJoints(assets.Hinges);
        var joints = new BodyJoints(assets.Clips.Skeleton);   // суставы капсул тела: нет — игра не стартует
        var jointSegments = JointSegments.Build(assets.Clips.Skeleton);   // сустав скелета → сегмент тела (привязка скина)
        var skins = new SkinBindings(jointSegments, ArenaRules.MaxFighters);   // привязки скина мёртвых — по слотам сочленений
        var start = new StartPoseModel(assets.Clips);   // поза постройки тела (общая и своя у бойца): сборка мира, постройка командой, инвариант
        var arena = b.Config.Section<ArenaSettings>("arena");
        if (arena.Enemies is < 0 or > ArenaState.MaxEnemies)
            throw new InvalidDataException($"arena.enemies = {arena.Enemies}: 0…{ArenaState.MaxEnemies}");
        // Память пула — сразу на арену с боем из конфига (рост посреди боя — аллокации в тике); прибавку врагов добирает команда арены.
        ReservePoses(poses, arena.Enemies);
        var weapons = assets.Weapons;
        var strikes = assets.Strikes;
        var upper = poses.AddMask(ActionRules.UpperMask(assets.Clips.Skeleton));   // слой верха тела (удары)
        var full = poses.AddMask(Enumerable.Repeat(1f, assets.Clips.JointCount).ToArray());   // всё тело: переход от снимка с памятью
        var combat = b.Config.Section<CombatSettings>("combat");
        int playerWeapon = weapons.Find(combat.PlayerWeapon);
        if (playerWeapon < 0) throw new InvalidDataException($"combat.playerWeapon = «{combat.PlayerWeapon}»: такого меча нет; есть: {weapons.Names}");
        // Оружие врагов — таблица без оружия только игрока (факел): номера таблицы, выбор врага — k-е из них.
        int[] enemyWeapons = weapons.EnemyWeapons(combat.PlayerOnlyWeapons ?? []);
        b.Component<Transform>().Component<PrevTransform>().Component<RootVelocity>().Component<FighterCommand>()
            .Component<GazeTarget>().Component<Fighter>().Component<PlayerTag>().Component<EnemyTag>()
            .Component<FighterSlot>().Component<FighterSeed>().Component<EnemyAiTerms>().Component<RootContacts>()
            .Component<PoseRef>().Component<AnimatorMotion>().Component<AnimatorAction>().Component<ActionPoses>().Component<BodyRef>()
            .Component<WeaponSlot>().Component<Blade>().Component<PlayerControl>().Component<AttackCombo>().Component<EnemyAttack>()
            .Component<Health>().Component<Dead>().Component<StrikePairs>().Component<Flinch>().Component<HitImpulse>().Component<Recoil>()
            // Своя поза постройки бойца и действующая модель контроллера — компоненты контрактов, которые ставит модуль (игроку на Motion
            // Matching); ядро читает и пишет их тем, у кого они есть. Бойцы без них — общая поза и модель по умолчанию (хеш ecs — от компонентов на сущностях).
            .Component<FighterStartPose>().Component<SteerModel>().Component<Severance>()
            .Event<StrikeContact>().Event<Hit>().Event<SeverRequested>().Event<LimbDetached>().Event<PartialCut>()
            .Command<PlayerCommand>().Command<ArenaCommand>().Command<EnemyCommand>().Command<StrikeProbe>().Command<SceneCommand>()
            .Command<BlastCommand>()
            .Resource(poses).Resource(assets.Actions).Resource(new ArenaState { Enemies = arena.Enemies })
            .Resource(PlayerInventory.All(weapons.Count, playerWeapon)).Resource(CombatRules.From(combat)).Resource(ArenaStats.Start).Resource(ragdoll)
            .Resource<IPhysicsWorld>(physics).Resource(new StartPose { Clip = -1 }).Resource(skins).Resource(assets.Tissue)
            .ReadView<IAnimationView>(new AnimationView(poses)).ReadView<IBodyView>(new BodyView(physics, jointSegments))
            .ReadView<ICombatView>(new CombatView(weapons, enemyWeapons, assets.Strikes, assets.Hand)).ReadView<ISkinView>(new SkinView(skins))
            .StateContributor(new AnimationState(poses))
            .StateContributor(new PhysicsState(physics))
            .StateContributor(new SkinState(skins))
            .Invariants(AnimationInvariants.For(poses, assets.Actions, strikes))
            .Invariant(new ArenaSlotsInvariant())
            .Invariant(new RootOverlapInvariant())
            .Invariants(PhysicsInvariants.For(physics, poses, joints, start, weapons, assets.Hand))
            .Invariant(new SkinInvariant(physics, skins))
            .Invariants(CorpseInvariants.For(physics, corpseProfile))
            .Invariants(KinematicPushInvariants.For(physics))
            .Invariants(CombatInvariants.For(poses, weapons, assets.Hand, start, strikes))
            .Invariants(DamageInvariants.For(physics))
            // Представление узнаёт о бойцах из очереди кадра: появился — завести, ушёл — убрать (Extract клиента).
            .Export<Added<Fighter>>().Export<Removed<Fighter>>()
            // Бой: касания, попадания, заявки на отруб и смерти — клиенту (HUD; без подписчика у события нет журнала).
            .Export<Event<StrikeContact>>().Export<Event<Hit>>().Export<Event<SeverRequested>>().Export<Added<Dead>>()
            // Отделения и неполные разрезы (модуль Cutting) — клиенту: разрез меша, кровь.
            .Export<Event<LimbDetached>>().Export<Event<PartialCut>>();
        FighterBodies.Register(b, physics);
        PhysicsDebug.Register(b, physics);   // вид суставов и трассы физики, режим physics.trace — мир, хеш и реплей не меняют
        // Ручки «Бой» — поля правил боя (ресурс из секции combat), изменение на ходу — команда ручки (система tuning.combat в Begin).
        CombatKnobs.Register(b);
        // Ручки «Рэгдолл» — поля правил трупа (секция ragdoll), команда ручки — система tuning.ragdoll в Begin, замена настроек физики — Intent.
        RagdollKnobs.Register(b, physics, corpseProfile);

        b.System(GameStages.Begin, new PrevTransformSystem())
            .System(GameStages.Begin, new ArenaCommandSystem(enemyWeapons))
            .System(GameStages.Intent, new SceneCommandSystem(physics, weapons.Count))
            .System(GameStages.Intent, new PlayerCommandSystem(strikes))
            .System(GameStages.Intent, new EnemyAiSystem(strikes))
            .System(GameStages.Gameplay, new FighterSetupSystem())
            .System(GameStages.Gameplay, new BodySetupSystem(physics, joints, start, skins))
            .System(GameStages.Gameplay, new BladeSetupSystem(physics, weapons, assets.Hand, start))
            .System(GameStages.Gameplay, new SkinCaptureSystem(physics, skins, gameplay: true))
            .System(GameStages.Gameplay, new DeathToRagdollSystem(physics, corpseJoints, corpseProfile, gameplay: true))
            .System(GameStages.Gameplay, new EnemyCommandSystem())
            .System(GameStages.Gameplay, new ScenePlayerIntentSystem())
            .System(GameStages.Gameplay, new RootSteerSystem(strikes))
            .System(GameStages.Gameplay, new AnimatorInputSystem(strikes))
            .System(GameStages.Animation, new ActionSnapshotSystem())
            .System(GameStages.Animation, new AnimatorPoseSystem(upper, full))
            .System(GameStages.Animation, new AnimationTickSystem())
            .System(GameStages.Movement, new RootControlSystem())
            .System(GameStages.Movement, new RootCollisionSystem())
            .System(GameStages.PrePhysics, new KinematicPlacementSystem(physics, joints))
            .System(GameStages.PrePhysics, new BladePlacementSystem(physics, weapons, assets.Hand))
            .System(GameStages.PrePhysics, new KinematicSourceSystem(physics))
            .System(GameStages.Physics, new PhysicsStepSystem(physics))
            .System(GameStages.Physics, new CorpseRootSystem(physics))
            .System(GameStages.Combat, new StrikeDetectionSystem(physics, strikes, BluntTable(weapons, combat.BluntWeapons ?? [])))
            .System(GameStages.Resolve, new HitApplySystem(physics))
            .System(GameStages.Resolve, new BlastSystem(physics))
            .System(GameStages.React, new HitReactionSystem())
            .System(GameStages.React, new SeverAftermathSystem(physics, corpseJoints, skins, corpseProfile))
            .System(GameStages.React, new RootRecoilSystem(physics))
            .System(GameStages.Aftermath, new BleedSystem())
            .System(GameStages.Aftermath, new RespawnSystem(enemyWeapons))
            .System(GameStages.Aftermath, new BodyRebuildSystem(physics))
            .System(GameStages.Settle, new ArenaStatsSystem())
            .System(GameStages.Settle, new SkinCaptureSystem(physics, skins, gameplay: false))
            .System(GameStages.Settle, new DeathToRagdollSystem(physics, corpseJoints, corpseProfile, gameplay: false))
            .System(GameStages.Settle, new BlastPushSystem(physics))
            .System(GameStages.End, new SeverClearSystem(physics))
            .System(GameStages.End, new PoseReleaseSystem())
            .System(GameStages.End, new ActionPoseReleaseSystem())
            .System(GameStages.End, new SkinReleaseSystem(skins));

        b.OnWorldBuilt(w => SpawnArena(w, arena.Enemies, enemyWeapons));
        // Тела и клинки бойцов сборки мира — по позе постройки, которую задаёт модуль при сборке мира, до первого тика.
        b.OnWorldReady(w =>
        {
            BodySetupSystem.PlaceWorldBuilt(w, physics, joints, start);
            BladeSetupSystem.PlaceWorldBuilt(w, physics, weapons, assets.Hand, start);
        });
    }

    /// <summary>Боец игрока — единственный с <see cref="PlayerTag"/>; ядро создаёт его при сборке мира.</summary>
    public static Entity Player(WorldReader world)
    {
        foreach (var chunk in world.Iterate(world.Query().With<PlayerTag>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("в мире нет игрока — ядро игры не собрано (GameCore.Configure)");
    }

    /// <summary>
    /// Начальная арена: игрок в центре курсом 0 (место 0), враги на местах 1…<paramref name="enemies"/> кольцом вокруг
    /// него (партия 0) со случайными мечами из <paramref name="weapons"/> (номера таблицы).
    /// </summary>
    public static void SpawnArena(World world, int enemies, ReadOnlySpan<int> weapons)
    {
        var rng = world.Resource<SimRandom>();
        ulong tick = world.Resource<SimTime>().Tick;
        var player = SpawnPlayer(world);
        var center = world.Get<Transform>(player).Position;
        for (int slot = 1; slot <= enemies; slot++) Fighters.Create(world, ArenaRules.Enemy(rng, tick, 0, slot, center, weapons));
    }

    /// <summary>Игрок: в центре, курсом 0, стоит, точки взгляда ещё нет, меч инвентаря; поза — поза привязки до первого тика.</summary>
    public static Entity SpawnPlayer(World world) =>
        Fighters.Create(world, ArenaRules.Player(world.Resource<SimRandom>(), world.Resource<SimTime>().Tick, 0,
            world.Resource<PlayerInventory>().Equipped));
}
