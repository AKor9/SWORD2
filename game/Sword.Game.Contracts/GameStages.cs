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
    public static readonly SystemLabel RootControl = new("core.root-control");
    public static readonly SystemLabel RootCollision = new("core.root-collision");
}
