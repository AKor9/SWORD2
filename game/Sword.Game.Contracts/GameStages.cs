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
    /// Intent: команды арены (<c>ArenaCommand</c>) — спаун и удаление врагов, сброс. Новые бойцы появляются на барьере
    /// Intent со всеми компонентами бойца; позу им заводит <see cref="FighterSetup"/>.
    /// </summary>
    public static readonly SystemLabel ArenaCommand = new("core.arena-command");

    /// <summary>Gameplay: новому бойцу (<c>Added&lt;Fighter&gt;</c>) — поза в пуле поз (<c>PoseRef</c>).</summary>
    public static readonly SystemLabel FighterSetup = new("core.fighter-setup");

    /// <summary>Gameplay: курс и разгон корня (<c>KinematicSteer</c>) — до аниматора, который видит новую скорость.</summary>
    public static readonly SystemLabel RootSteer = new("core.root-steer");

    /// <summary>Gameplay, после <see cref="RootSteer"/>: сглаженная скорость и «идёт» (компонент <c>AnimatorMotion</c>).</summary>
    public static readonly SystemLabel AnimatorInput = new("core.animator-input");

    /// <summary>
    /// Animation: итоговая поза бойца — прямая кинематика его <see cref="PoseRef"/>. Слои позы (локомоция, позже
    /// действия) ставят свои задания до этой метки.
    /// </summary>
    public static readonly SystemLabel AnimatorPose = new("core.animator-pose");

    /// <summary>Animation, после <see cref="AnimatorPose"/>: пул поз исполняет задания тика пачкой.</summary>
    public static readonly SystemLabel AnimationTick = new("core.animation-tick");

    /// <summary>Movement: перенос корня на скорость тика (<c>KinematicBegin</c>).</summary>
    public static readonly SystemLabel RootControl = new("core.root-control");
    public static readonly SystemLabel RootCollision = new("core.root-collision");
}
