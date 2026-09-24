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
/// Ядро игры (принцип 4 плана: то, без чего игры нет, — одна реализация, не модуль и не выключается): компоненты и
/// команда игрока из <c>Game.Contracts</c>, движение корня, аниматор бойца (поза в пуле поз, слой движения для
/// локомоции), начальный мир — игрок в центре арены курсом 0 (<c>DuelArena.Reset</c> SWORD: <c>Build(Player, 0, 0)</c>)
/// и <c>arena.enemies</c> врагов кольцом вокруг него; команды арены (<see cref="ArenaCommand"/>: число врагов, сброс —
/// этап 4а); ИИ врагов (<see cref="EnemyAiSystem"/> — этап 4б); столкновения корней бойцов между собой
/// (<see cref="RootCollisionSystem"/>, инвариант <see cref="RootOverlapInvariant"/> — этап 4в); слой действий аниматора — поворот
/// врага на месте клипом (<see cref="AnimatorAction"/>, <see cref="ActionLayer"/>) и команда врагу (<see cref="EnemyCommand"/>)
/// — этап 4г; тело бойца — сочленение капсул в подсистеме Physics за <see cref="BodyRef"/>, время жизни — хуками ECS
/// (<see cref="FighterBodies"/>, участник хеша <c>physics</c>, инварианты <c>physics.*</c> — этап 5а); форма тела и постановка
/// при постройке (<see cref="BodySetupSystem"/>), капсулы по позе тика (<see cref="KinematicPlacementSystem"/>, PrePhysics),
/// поза постройки (<see cref="StartPose"/>), вид тел <see cref="IBodyView"/> — 5б; тела бойцов сборки мира стоят до первого
/// тика, вид тел отдаёт клиенту сегменты суставов скелета (ID-буфер) — 5в. Этап 6б: команды арены — в стадии Begin (Intent
/// видит арену после них), слой действий у всех бойцов, оружие — таблица мечей пака и таблица ударов (<see cref="CharacterAssets"/>),
/// меч в руке (<see cref="WeaponSlot"/>: враг — случайный, игрок — <c>combat.playerWeapon</c>), инвентарь игрока
/// (<see cref="PlayerInventory"/>, T), клинок каждого тика (<see cref="Blade"/>: постройка — <see cref="BladeSetupSystem"/>,
/// тик — <see cref="BladePlacementSystem"/> в PrePhysics), вид <see cref="ICombatView"/>, инварианты <c>combat.*</c>. Этап 6в: удар
/// и вид рывка в слое действий (<see cref="ActionLayer"/>; снимок позы — <see cref="ActionSnapshotSystem"/>, слой верха — маска
/// пула <c>ActionRules.UpperMask</c>), бой игрока (<see cref="PlayerControl"/>, <see cref="AttackCombo"/>), удар ИИ
/// (<see cref="EnemyAttack"/>), правила боя <see cref="CombatRules"/> из секции <c>combat</c>, инвариант <c>combat.control</c>. Этап 6г:
/// урон по протоколу боя — проходы клинка (<see cref="StrikeDetectionSystem"/>, Combat → <see cref="StrikeContact"/>; пары
/// <see cref="StrikePairs"/>; проба <see cref="StrikeProbe"/>), урон и смерть (<see cref="HitApplySystem"/>, Resolve → <see cref="Health"/>,
/// <see cref="Dead"/>, <see cref="Hit"/>, <see cref="SeverRequested"/>), реакции (<see cref="HitReactionSystem"/>, React: враг — действие,
/// игрок — вздрагивание <see cref="Flinch"/>), кровопотеря и возрождение (<see cref="BleedSystem"/>, <see cref="RespawnSystem"/>, Aftermath),
/// статистика (<see cref="ArenaStatsSystem"/>, Settle → <see cref="ArenaStats"/>); мёртвый замирает (системы ядра и модуль локомоции
/// его пропускают); инварианты <c>combat.health</c>, <c>combat.dead</c>, <c>combat.hits</c>, <c>combat.respawn</c>. Этап 6е: ручки «Бой»
/// (<see cref="CombatKnobs"/>: поля <see cref="CombatRules"/>, команда ручки — система <c>tuning.combat</c> в Begin; смена правил —
/// <see cref="CombatRulesChangeSystem"/>). Этап 7ж: тело бойца — 14 тел по номерам сегментов с массами (<see cref="FighterShape.Masses"/>),
/// тело клинка по мечу в руке (<see cref="BladeBody"/>; постройка — <see cref="BladeSetupSystem"/>, тик — <see cref="BladePlacementSystem"/>);
/// причина смерти (<see cref="DeathCause"/>); команды сцен (<see cref="SceneCommand"/>: <see cref="SceneCommandSystem"/> в Intent,
/// <see cref="ScenePlayerIntentSystem"/> в Gameplay); привязка скина мёртвого (<see cref="SkinBindings"/>, снимок —
/// <see cref="SkinCaptureSystem"/> в Gameplay и Settle, сброс — <see cref="SkinReleaseSystem"/> в End; вид <see cref="ISkinView"/>,
/// участник хеша <c>skin</c>, инвариант <c>ragdoll.skin</c>). Регистрации ядра — без токена модуля.
///
/// Порядок <c>DuelArena.Tick</c> SWORD — по стадиям: команды арены (из цикла кадра SWORD до тика) — Begin; команда игрока и
/// ИИ врагов — Intent. Порядок <c>KinematicBegin</c> — по стадиям: курс и разгон, у врага — начало поворота клипом (Gameplay) → аниматор
/// без поз по новой скорости: слой движения и шаг действия (Gameplay) → слои позы (локомоция, действие) и прямая
/// кинематика, пул поз исполняет задания (Animation) → поворот курса клипом, перенос корня и столкновения (Movement); затем
/// <c>PlaceBodies</c> — капсулы тела и клинок по позе тика (PrePhysics).
/// </summary>
public static class GameCore
{
    /// <summary>
    /// Поз пула на бойца в худшем случае (запас пула, 6ж): поза бойца, слой и снимок локомоции (модуль Locomotion), слой, снимок, две позы
    /// памяти смешивания действий и две — перехода от снимка, две позы вздрагивания (игрок).
    /// </summary>
    public const int PosesPerFighter = 11;

    /// <summary>
    /// Заданий пула на бойца за тик в худшем случае (запас очереди, 6ж): локомоция — до 4 (снимок, сэмпл, переход, копия), действие — до 3
    /// (сэмпл, смешивание, переход от снимка), снимок — 1, вздрагивание — до 6 (два слота по 3), прямая кинематика — 1; с запасом — 16.
    /// </summary>
    public const int PoseJobsPerFighter = 16;

    /// <summary>
    /// Запас пула поз на арену из <paramref name="enemies"/> врагов (6ж, доводка): (враги + игрок) × <see cref="PosesPerFighter"/> поз и
    /// × <see cref="PoseJobsPerFighter"/> заданий — бой (действия, вздрагивания, снимки, память смешивания) не растит пул посреди игры, тик
    /// боя — 0 Б. Зовут постройка симуляции (<c>arena.enemies</c>) и команда арены, прибавившая врагов (<see cref="ArenaCommandSystem"/>:
    /// тик команды, до постройки новых бойцов). Раньше запас был на полную арену у каждой симуляции (33 бойца — 512 слотов, ~7,6 МБ на
    /// настоящем паке: ветки fork, тесты, прогрев JIT); 10 врагов — 128 слотов. Логика пула (ёмкость, номера поз, хеш <c>animation</c>) от
    /// запаса не зависит (<see cref="PosePool.Reserve"/>). Меньше заведённого — ничего.
    /// </summary>
    public static void ReservePoses(PosePool poses, int enemies)
    {
        int fighters = Math.Clamp(enemies, 0, ArenaState.MaxEnemies) + 1;
        poses.Reserve(fighters * PosesPerFighter, fighters * PoseJobsPerFighter);
    }

    /// <summary>Регистрации ядра — в <c>Simulation.Build</c> до модулей.</summary>
    public static void Configure(SimulationBuilder b, CharacterAssets assets)
    {
        var poses = new PosePool(assets.Clips);   // пул поз — у каждой симуляции свой: ассеты только читаются
        // Физика — у каждой симуляции своя; слотов — на полную арену (33 бойца по 14 тел — с 7ж и клинок), дальше растёт удвоением.
        var physics = new PhysicsWorld(ArenaRules.MaxFighters, ArenaRules.MaxFighters * BodySegs.Bodies);
        var joints = new BodyJoints(assets.Clips.Skeleton);   // суставы капсул тела: нет — игра не стартует (как CharacterRig)
        var jointSegments = JointSegments.Build(assets.Clips.Skeleton);   // сустав скелета → сегмент тела (ID-буфер 5в, привязка скина 7ж)
        var skins = new SkinBindings(jointSegments, ArenaRules.MaxFighters);   // 7ж: привязки скина мёртвых — по слотам сочленений
        var start = new StartPoseModel(assets.Clips);   // поза постройки тела: сборка мира, постройка командой, инвариант
        var arena = b.Config.Section<ArenaSettings>("arena");
        if (arena.Enemies is < 0 or > ArenaState.MaxEnemies)
            throw new InvalidDataException($"arena.enemies = {arena.Enemies}: 0…{ArenaState.MaxEnemies}");
        // 6ж: память пула — сразу на арену с боем (рост посреди боя выделял ~3,8 МБ на настоящем паке — 539 Б/тик бюджета толпы); доводка
        // 6ж — на арену из конфига, прибавку врагов добирает команда арены.
        ReservePoses(poses, arena.Enemies);
        var weapons = assets.Weapons;
        var strikes = assets.Strikes;
        var upper = poses.AddMask(ActionRules.UpperMask(assets.Clips.Skeleton));   // слой верха тела (удары): CharacterRig.UpperMask
        var full = poses.AddMask(Enumerable.Repeat(1f, assets.Clips.JointCount).ToArray());   // всё тело: переход от снимка с памятью (6ж)
        var combat = b.Config.Section<CombatSettings>("combat");
        int playerWeapon = weapons.Find(combat.PlayerWeapon);
        if (playerWeapon < 0) throw new InvalidDataException($"combat.playerWeapon = «{combat.PlayerWeapon}»: такого меча нет; есть: {weapons.Names}");
        b.Component<Transform>().Component<PrevTransform>().Component<RootVelocity>().Component<FighterCommand>()
            .Component<GazeTarget>().Component<Fighter>().Component<PlayerTag>().Component<EnemyTag>()
            .Component<FighterSlot>().Component<FighterSeed>().Component<EnemyAiTerms>().Component<RootContacts>()
            .Component<PoseRef>().Component<AnimatorMotion>().Component<AnimatorAction>().Component<ActionPoses>().Component<BodyRef>()
            .Component<WeaponSlot>().Component<Blade>().Component<PlayerControl>().Component<AttackCombo>().Component<EnemyAttack>()
            .Component<Health>().Component<Dead>().Component<StrikePairs>().Component<Flinch>()
            .Event<StrikeContact>().Event<Hit>().Event<SeverRequested>()
            .Command<PlayerCommand>().Command<ArenaCommand>().Command<EnemyCommand>().Command<StrikeProbe>().Command<SceneCommand>()
            .Resource(poses).Resource(assets.Actions).Resource(new ArenaState { Enemies = arena.Enemies })
            .Resource(PlayerInventory.All(weapons.Count, playerWeapon)).Resource(CombatRules.From(combat)).Resource(ArenaStats.Start)
            .Resource<IPhysicsWorld>(physics).Resource(new StartPose { Clip = -1 }).Resource(skins)
            .ReadView<IAnimationView>(new AnimationView(poses)).ReadView<IBodyView>(new BodyView(physics, jointSegments))
            .ReadView<ICombatView>(new CombatView(weapons, assets.Strikes, assets.Hand)).ReadView<ISkinView>(new SkinView(skins))
            .StateContributor(new AnimationState(poses))
            .StateContributor(new PhysicsState(physics))
            .StateContributor(new SkinState(skins))
            .Invariants(AnimationInvariants.For(poses, assets.Actions, strikes))
            .Invariant(new ArenaSlotsInvariant())
            .Invariant(new RootOverlapInvariant())
            .Invariants(PhysicsInvariants.For(physics, poses, joints, start, weapons, assets.Hand))
            .Invariant(new SkinInvariant(physics, skins))
            .Invariants(CombatInvariants.For(poses, weapons, assets.Hand, start, strikes))
            .Invariants(DamageInvariants.For())
            // Представление узнаёт о бойцах из очереди кадра: появился — завести, ушёл — убрать (Extract клиента).
            .Export<Added<Fighter>>().Export<Removed<Fighter>>()
            // Бой (6г): касания, попадания, заявки на отруб и смерти — клиенту (HUD 6е, кровь — этап 12) и агенту (events.history: без
            // подписчика у события нет журнала).
            .Export<Event<StrikeContact>>().Export<Event<Hit>>().Export<Event<SeverRequested>>().Export<Added<Dead>>();
        FighterBodies.Register(b, physics);
        // 6е: ручки «Бой» — поля правил боя (ресурс из секции combat), изменение на ходу — команда ручки (система tuning.combat в Begin).
        CombatKnobs.Register(b);

        b.System(GameStages.Begin, new PrevTransformSystem())
            .System(GameStages.Begin, new ArenaCommandSystem(weapons.Count))
            .System(GameStages.Intent, new SceneCommandSystem(physics, weapons.Count))
            .System(GameStages.Intent, new PlayerCommandSystem(strikes))
            .System(GameStages.Intent, new EnemyAiSystem(strikes))
            .System(GameStages.Gameplay, new FighterSetupSystem())
            .System(GameStages.Gameplay, new BodySetupSystem(physics, joints, start))
            .System(GameStages.Gameplay, new BladeSetupSystem(physics, weapons, assets.Hand, start))
            .System(GameStages.Gameplay, new SkinCaptureSystem(physics, skins, gameplay: true))
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
            .System(GameStages.Combat, new StrikeDetectionSystem(physics, strikes))
            .System(GameStages.Resolve, new HitApplySystem())
            .System(GameStages.React, new HitReactionSystem())
            .System(GameStages.Aftermath, new BleedSystem())
            .System(GameStages.Aftermath, new RespawnSystem(weapons.Count))
            .System(GameStages.Settle, new ArenaStatsSystem())
            .System(GameStages.Settle, new SkinCaptureSystem(physics, skins, gameplay: false))
            .System(GameStages.End, new PoseReleaseSystem())
            .System(GameStages.End, new ActionPoseReleaseSystem())
            .System(GameStages.End, new SkinReleaseSystem(skins));

        b.OnWorldBuilt(w => SpawnArena(w, arena.Enemies, weapons.Count));
        // Тела и клинки бойцов сборки мира — по позе постройки, которую задаёт модуль при сборке мира (5в, 6б): как PlaceBuilt
        // SWORD после AttachRig, до первого тика.
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
    /// Начальная арена (<c>DuelArena.Reset(enemies)</c> SWORD): игрок в центре курсом 0 (место 0), затем враги на местах
    /// 1…<paramref name="enemies"/> кольцом вокруг него (<see cref="ArenaRules.Enemy"/>, партия 0 — сборка мира) со случайными
    /// мечами из <paramref name="weapons"/>.
    /// </summary>
    public static void SpawnArena(World world, int enemies, int weapons)
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
