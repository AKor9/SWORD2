using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Стадии тика игры; после каждой — барьер. Модуль встаёт в стадию до или после метки ядра (<see cref="GameLabels"/>).
/// Defense и Dismember — пустые точки протокола боя: блок (Guard) и расчленение (Cutting) отложены.
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
    /// Intent, до <see cref="PlayerCommand"/>, по <c>ResourceChanged&lt;CombatRules&gt;</c>: таймеры управления игрока
    /// прижимаются к новым пределам ручек «Бой».
    /// </summary>
    public static readonly SystemLabel CombatRules = new("core.combat-rules");

    /// <summary>Intent, после <see cref="PlayerCommand"/>: ИИ врагов — намерение и точка взгляда по корням до движения тика.</summary>
    public static readonly SystemLabel EnemyAi = new("core.enemy-ai");

    /// <summary>
    /// Begin, после <see cref="PrevTransform"/>: команды арены. Новые и построенные заново бойцы появляются на барьере Begin
    /// со всеми компонентами — Intent того же тика видит арену после команд; позу им заводит <see cref="FighterSetup"/>.
    /// </summary>
    public static readonly SystemLabel ArenaCommand = new("core.arena-command");

    /// <summary>Gameplay: новому бойцу (<c>Added&lt;Fighter&gt;</c>) — поза в пуле поз (<c>PoseRef</c>).</summary>
    public static readonly SystemLabel FighterSetup = new("core.fighter-setup");

    /// <summary>Gameplay, до <see cref="RootSteer"/>: команды врагу тика — намерение вместо ИИ поверх всего, что записал Intent.</summary>
    public static readonly SystemLabel EnemyCommand = new("core.enemy-command");

    /// <summary>
    /// Gameplay: курс и разгон корня — до аниматора, который видит новую скорость; у врага здесь же начинается поворот
    /// на месте клипом.
    /// </summary>
    public static readonly SystemLabel RootSteer = new("core.root-steer");

    /// <summary>
    /// Gameplay, после <see cref="RootSteer"/>: аниматор бойца без поз — сглаженная скорость и «идёт» (компонент
    /// <c>AnimatorMotion</c>), у врага — шаг слоя действий (время, вес, поворот курса клипом).
    /// </summary>
    public static readonly SystemLabel AnimatorInput = new("core.animator-input");

    /// <summary>
    /// Animation, первой: снимок позы при прерывании действия — копия итоговой позы прошлого тика заданием пулу, пока слои
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
    /// Gameplay, до <see cref="RootSteer"/>: построенному бойцу — форма тела и постановка капсул без скорости по позе
    /// постройки.
    /// </summary>
    public static readonly SystemLabel BodySetup = new("core.body-setup");

    /// <summary>
    /// PrePhysics: капсулы тела живого бойца — на суставы позы тика в корне после Movement, скорости — разностью с прошлой
    /// постановкой.
    /// </summary>
    public static readonly SystemLabel KinematicPlacement = new("core.kinematic-placement");

    /// <summary>
    /// Gameplay, по <c>Added&lt;Fighter&gt;</c>, после <see cref="BodySetup"/>: построенному бойцу — клинок по позе постройки
    /// без прошлого (прошлое = нынешнему).
    /// </summary>
    public static readonly SystemLabel BladeSetup = new("core.blade-setup");

    /// <summary>
    /// PrePhysics, после <see cref="KinematicPlacement"/>: клинок бойца по кисти позы тика в мире бойца и тело клинка
    /// (кинематическая цель); сменился меч — прошлое = нынешнему, новая форма без скорости.
    /// </summary>
    public static readonly SystemLabel BladePlacement = new("core.blade-placement");

    /// <summary>
    /// Intent, до <see cref="PlayerCommand"/>: команды сцены тика по порядку (расстановка, смерть без удара, сдвиг тел трупа,
    /// меч места). Gameplay того же тика строит тело и клинок поставленному и снимает привязку скина убитому.
    /// </summary>
    public static readonly SystemLabel SceneCommand = new("core.scene-command");

    /// <summary>
    /// Gameplay, после <see cref="EnemyCommand"/>, до <see cref="RootSteer"/>: намерение игрока командой сцены
    /// (<c>SceneAction.PlayerIntent</c>) — поверх команды игрока этого тика.
    /// </summary>
    public static readonly SystemLabel SceneIntent = new("core.scene-intent");

    /// <summary>
    /// Gameplay (до <see cref="RootSteer"/>) и Settle, по <c>Added&lt;Dead&gt;</c>: снимок привязки скина мёртвого по позе
    /// и телам момента смерти (Gameplay — смерти командой сцены, Settle — от ударов и кровопотери). Переход в труп — после неё.
    /// </summary>
    public static readonly SystemLabel SkinCapture = new("core.skin-capture");

    /// <summary>End, реактивная по <c>Removed&lt;Dead&gt;</c>: ожившему (постройка заново) или ушедшему мёртвому — привязка скина снята.</summary>
    public static readonly SystemLabel SkinRelease = new("core.skin-release");

    /// <summary>
    /// Intent, первой, реактивная по <c>ResourceChanged&lt;RagdollRules&gt;</c>: ручки «Рэгдолл» сменили правила трупа (система
    /// <c>tuning.ragdoll</c> в Begin) — настройки физики и профиль трупа заменяются до шага этого тика.
    /// </summary>
    public static readonly SystemLabel RagdollRules = new("core.ragdoll-rules");

    /// <summary>
    /// Gameplay (после <see cref="SkinCapture"/>, до <see cref="RootSteer"/>) и Settle (после <see cref="SkinCapture"/>), реактивная
    /// по <c>Added&lt;Dead&gt;</c>: переход в труп — скорости тел урезаны, суставы привязаны по позе смерти, запястье выключено
    /// (меч выпадает), все тела на физике с профилем трупа. Уже перешедших пропускает.
    /// </summary>
    public static readonly SystemLabel DeathToRagdoll = new("core.death-to-ragdoll");

    /// <summary>Physics: шаг физики на тик — тела трупов (интеграция, суставы, контакты, толчки живых).</summary>
    public static readonly SystemLabel PhysicsStep = new("core.physics-step");

    /// <summary>Physics, после <see cref="PhysicsStep"/>: корень мёртвого идёт за тазом трупа по земле (x, z).</summary>
    public static readonly SystemLabel CorpseRoot = new("core.corpse-root");

    /// <summary>
    /// Aftermath, после <see cref="Respawn"/>: труп с нечисловым телом (любое поле любого из 14 тел) строится заново на месте
    /// корня — живым, тела ставит постройка следующего тика.
    /// </summary>
    public static readonly SystemLabel BodyRebuild = new("core.body-rebuild");

    /// <summary>
    /// PrePhysics, после <see cref="BladePlacement"/>: чем толкают тела живых в шаге физики — клинок в ударе проходом,
    /// остальные телом; потенциал прохода — по скорости кончика клинка тика.
    /// </summary>
    public static readonly SystemLabel KinematicSource = new("core.kinematic-source");

    /// <summary>React: отдача толчков и ударов по трупам — горизонталью в скорость корня живого бойца, затем конец тика проходов.</summary>
    public static readonly SystemLabel RootRecoil = new("core.root-recoil");

    /// <summary>End: позы слоя действий снятого бойца возвращаются в пул поз.</summary>
    public static readonly SystemLabel ActionRelease = new("core.action-release");

    /// <summary>
    /// Combat: проходы клинка в ударе сквозь капсулы жертвы (пары игрок ↔ враг) — событие <c>StrikeContact</c>; затем пробы
    /// удара командой (<c>StrikeProbe</c>) тем же событием.
    /// </summary>
    public static readonly SystemLabel StrikeDetection = new("core.strike-detection");

    /// <summary>
    /// Resolve, по <c>Event&lt;StrikeContact&gt;</c>: урон и раны, смерть, события <c>Hit</c> и <c>SeverRequested</c>, импульс
    /// удара в часть через физику. Модули защиты встанут в стадию Defense между Combat и Resolve.
    /// </summary>
    public static readonly SystemLabel HitApply = new("core.hit-apply");

    /// <summary>React, реактивная по <c>Event&lt;Hit&gt;</c>: реакция живой жертвы на последнее попадание тика.</summary>
    public static readonly SystemLabel HitReaction = new("core.hit-reaction");

    /// <summary>Aftermath: кровопотеря живых — очки жизни, кровотечение, смерть.</summary>
    public static readonly SystemLabel Bleed = new("core.bleed");

    /// <summary>Aftermath, после <see cref="Bleed"/>: возрождение врага через 8 с после смерти — постройка места заново.</summary>
    public static readonly SystemLabel Respawn = new("core.respawn");

    /// <summary>Settle: статистика арены (<c>ArenaStats</c>) — попадания и смерти врагов тика.</summary>
    public static readonly SystemLabel ArenaStats = new("core.arena-stats");
}
