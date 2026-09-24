using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Стадии тика игры (раздел 7 плана) — раскладка <c>DuelArena.Tick</c> SWORD. После каждой стадии — барьер.
/// Точки расширения для модулей: модуль встаёт в стадию и до или после метки ядра (<see cref="GameLabels"/>).
/// </summary>
public static class GameStages
{
    public const string Begin = "Begin", Intent = "Intent", Gameplay = "Gameplay", Animation = "Animation",
        Movement = "Movement", PrePhysics = "PrePhysics", Physics = "Physics", Combat = "Combat", Defense = "Defense",
        Resolve = "Resolve", Dismember = "Dismember", React = "React", Aftermath = "Aftermath", Settle = "Settle", End = "End";

    public static readonly IReadOnlyList<string> All =
    [
        Begin, Intent, Gameplay, Animation, Movement, PrePhysics, Physics, Combat, Defense, Resolve, Dismember, React,
        Aftermath, Settle, End,
    ];
}

/// <summary>Метки систем ядра игры: модули ставят свои системы до или после них, не видя их типов.</summary>
public static class GameLabels
{
    public static readonly SystemLabel PrevTransform = new("core.prev-transform");
    public static readonly SystemLabel PlayerCommand = new("core.player-command");

    /// <summary>
    /// Intent, после <see cref="PlayerCommand"/>: ИИ врагов (<c>EnemyAi.Think</c>) — намерение (<c>FighterCommand</c>) и
    /// точка взгляда каждого врага по корням до движения тика.
    /// </summary>
    public static readonly SystemLabel EnemyAi = new("core.enemy-ai");

    /// <summary>
    /// Begin, после <see cref="PrevTransform"/> (с 6б; на этапах 4–6а — Intent): команды арены (<c>ArenaCommand</c>) — спаун и
    /// удаление врагов, сброс, меч игрока. Новые и построенные заново бойцы появляются на барьере Begin со всеми компонентами
    /// бойца — Intent того же тика (команда игрока, ИИ врагов) видит арену после команд, как <c>Think</c> SWORD после
    /// <c>SetEnemyCount</c>/<c>Reset</c>; позу им заводит <see cref="FighterSetup"/>.
    /// </summary>
    public static readonly SystemLabel ArenaCommand = new("core.arena-command");

    /// <summary>Gameplay: новому бойцу (<c>Added&lt;Fighter&gt;</c>) — поза в пуле поз (<c>PoseRef</c>).</summary>
    public static readonly SystemLabel FighterSetup = new("core.fighter-setup");

    /// <summary>
    /// Gameplay, до <see cref="RootSteer"/>: команды врагу тика (<c>EnemyCommand</c>) — намерение врага вместо ИИ поверх всего,
    /// что записал Intent (ИИ и команда арены).
    /// </summary>
    public static readonly SystemLabel EnemyCommand = new("core.enemy-command");

    /// <summary>
    /// Gameplay: курс и разгон корня (<c>KinematicSteer</c>) — до аниматора, который видит новую скорость; у врага (не-<c>Agile</c>)
    /// здесь же начинается поворот на месте клипом.
    /// </summary>
    public static readonly SystemLabel RootSteer = new("core.root-steer");

    /// <summary>
    /// Gameplay, после <see cref="RootSteer"/>: аниматор бойца без поз — сглаженная скорость и «идёт» (компонент
    /// <c>AnimatorMotion</c>), у врага — шаг слоя действий (время, вес, поворот курса клипом).
    /// </summary>
    public static readonly SystemLabel AnimatorInput = new("core.animator-input");

    /// <summary>
    /// Animation, первой (6в): снимок позы при прерывании действия — копия итоговой позы прошлого тика заданием пулу, пока слои
    /// позы этого тика её не переписали. Слои позы модулей (локомоция) ставят свои задания после этой метки.
    /// </summary>
    public static readonly SystemLabel ActionSnapshot = new("core.action-snapshot");

    /// <summary>
    /// Animation: итоговая поза бойца — слой действий (удар — по маске верха, поворот, вид рывка), переход от снимка и прямая
    /// кинематика его <see cref="PoseRef"/>. Слои позы (локомоция) ставят свои задания до этой метки.
    /// </summary>
    public static readonly SystemLabel AnimatorPose = new("core.animator-pose");

    /// <summary>Animation, после <see cref="AnimatorPose"/>: пул поз исполняет задания тика пачкой.</summary>
    public static readonly SystemLabel AnimationTick = new("core.animation-tick");

    /// <summary>Movement: перенос корня на скорость тика (<c>KinematicBegin</c>).</summary>
    public static readonly SystemLabel RootControl = new("core.root-control");
    public static readonly SystemLabel RootCollision = new("core.root-collision");

    /// <summary>
    /// Gameplay, до <see cref="RootSteer"/>: построенному бойцу (<c>Added&lt;Fighter&gt;</c>) — форма тела и постановка капсул
    /// без скорости по позе постройки в корне постройки (<c>Fighter.Build</c>, <c>PlaceBuilt</c> SWORD; 5б).
    /// </summary>
    public static readonly SystemLabel BodySetup = new("core.body-setup");

    /// <summary>
    /// PrePhysics: капсулы тела живого бойца — на суставы позы этого тика в корне после Movement (<c>PlaceBodies</c> SWORD;
    /// 5б), скорости — разностью с прошлой постановкой.
    /// </summary>
    public static readonly SystemLabel KinematicPlacement = new("core.kinematic-placement");

    /// <summary>
    /// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, после <see cref="BodySetup"/> (6б): построенному бойцу — клинок по позе
    /// постройки в корне постройки без прошлого (<c>PlaceBuilt</c> SWORD: первая постановка — <c>KinPrevBase = KinBase</c>).
    /// </summary>
    public static readonly SystemLabel BladeSetup = new("core.blade-setup");

    /// <summary>
    /// PrePhysics, после <see cref="KinematicPlacement"/> (6б): клинок бойца — по модельной матрице кисти позы тика в мире бойца
    /// (<c>anim.Blade</c> в <c>PlaceBodies</c> SWORD), прошлый — клинок прошлого тика (сменился меч — прошлое = нынешнему).
    /// </summary>
    public static readonly SystemLabel BladePlacement = new("core.blade-placement");

    /// <summary>End: позы слоя действий снятого бойца возвращаются в пул поз.</summary>
    public static readonly SystemLabel ActionRelease = new("core.action-release");

    /// <summary>
    /// Combat (6г): проходы клинка (<c>KinematicStrikes</c> SWORD) — клинок бойца в ударе против капсул живой жертвы (пары игрок ↔
    /// враг), касание — событие <c>StrikeContact</c>; затем пробы удара командой (<c>StrikeProbe</c>) тем же событием.
    /// </summary>
    public static readonly SystemLabel StrikeDetection = new("core.strike-detection");

    /// <summary>
    /// Resolve (6г), реактивная по <c>Event&lt;StrikeContact&gt;</c> (<c>ApplyHits</c>, <c>ApplyHit</c> SWORD): урон и раны
    /// (<c>Health</c>), смерть (<c>Dead</c>), события <c>Hit</c> и <c>SeverRequested</c>. Модули защиты (этап 10) встанут в стадию
    /// Defense между Combat и Resolve.
    /// </summary>
    public static readonly SystemLabel HitApply = new("core.hit-apply");

    /// <summary>React (6г), реактивная по <c>Event&lt;Hit&gt;</c>: реакция живой жертвы на последнее попадание тика.</summary>
    public static readonly SystemLabel HitReaction = new("core.hit-reaction");

    /// <summary>Aftermath (6г): кровопотеря живых (<c>Fighter.AfterTick</c> SWORD) — очки жизни, кровотечение, смерть.</summary>
    public static readonly SystemLabel Bleed = new("core.bleed");

    /// <summary>Aftermath (6г), после <see cref="Bleed"/>: возрождение врага через 8 с после смерти — постройка места заново.</summary>
    public static readonly SystemLabel Respawn = new("core.respawn");

    /// <summary>Settle (6г): статистика арены (<c>ArenaStats</c>) — попадания и смерти врагов тика.</summary>
    public static readonly SystemLabel ArenaStats = new("core.arena-stats");
}
