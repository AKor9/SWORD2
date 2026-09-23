using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Корень бойца на земле: позиция (y = 0, высоту тела даёт постановка на рельеф) и курс — поворот вокруг Y, рад
/// (0 — взгляд вдоль +Z). <c>Fighter.Root</c> и <c>Fighter.Facing</c> SWORD.
/// </summary>
public struct Transform
{
    public Vector3 Position;
    public float Facing;
}

/// <summary>Корень прошлого тика — для интерполяции в клиенте (<c>KinPrevRoot</c>, <c>KinPrevFacing</c> SWORD).</summary>
public struct PrevTransform
{
    public Vector3 Position;
    public float Facing;
}

/// <summary>Скорость корня, м/с (<c>Fighter.RootVel</c>).</summary>
public struct RootVelocity
{
    public Vector3 Value;
}

/// <summary>Намерение бойца на тик: желаемая скорость корня, м/с, и курс-цель, рад (<c>DesiredVel</c>, <c>FacingGoal</c>).</summary>
public struct FighterCommand
{
    public Vector3 DesiredVelocity;
    public float FacingGoal;
}

/// <summary>
/// Точка взгляда бойца (<c>Fighter.GazePoint</c> SWORD). У игрока — последняя, которую дал курсор
/// (<c>PlayerController._cursor</c>): курс-цель каждый тик доводится на неё от текущего корня, даже когда курсора на земле
/// в этом кадре нет. У врага — корень игрока до движения тика (ИИ, этап 4б); потребителя у неё пока нет: в SWORD
/// <c>GazePoint</c> читает только поворот головы живого рэгдолла (<c>Fighter.BeginTick</c>), он не переносится.
/// </summary>
public struct GazeTarget
{
    public Vector3 Point;

    /// <summary>Курсор хоть раз был на земле (<c>_hasCursor</c> SWORD).</summary>
    public bool Has;
}

/// <summary>
/// Поза бойца в пуле поз подсистемы Animation (раздел 6 плана: данные позы — в пуле, в ECS — хэндл): номер слота и
/// поколение. Итоговая поза тика — модельные матрицы суставов в пространстве персонажа (+Z вперёд, +X влево, Y вверх);
/// ядро заводит её вместе с бойцом и освобождает, когда компонент снят.
/// </summary>
public struct PoseRef
{
    public int Index;
    public int Generation;
}

/// <summary>
/// Движение бойца для слоя локомоции аниматора (<c>FighterAnimator</c> SWORD: <c>_vel</c>, <c>_moving</c>,
/// <c>LocoInput</c>): сглаженная скорость в системе бойца (x — вправо, z — вперёд), «идёт» с гистерезисом и можно ли
/// искать новый кадр (false — действие ведёт всё тело). Пишет ядро в Gameplay, читает модуль локомоции в Animation.
/// </summary>
public struct AnimatorMotion
{
    public Vector3 Velocity;
    public bool Moving;
    public bool Search;
}

/// <summary>
/// Боец (тег). <c>Added&lt;Fighter&gt;</c> — боец построен: новая сущность или та же, у которой метку сняли и поставили на
/// одном барьере (постройка заново — <c>Fighter.Build</c> SWORD над бойцом того же места, например при сбросе арены).
/// Модули заводят по нему своё состояние бойца с нуля.
/// </summary>
public struct Fighter;

/// <summary>Боец игрока (тег).</summary>
public struct PlayerTag;

/// <summary>Боец-враг (тег): место 1…32 (<see cref="FighterSlot"/>), намерение ему пишет ИИ (этап 4б).</summary>
public struct EnemyTag;

/// <summary>
/// Место бойца на арене — номер бойца SWORD (<c>DuelArena._fighters[i]</c>, <c>Fighter.Index</c>): игрок — 0, враги —
/// 1…<see cref="ArenaState.MaxEnemies"/> без дыр. Порядок мест — порядок обхода SWORD (мозги, пары столкновений корней):
/// от него зависит порядок сложения float, поэтому системы, которым он важен, идут по местам, а не по сущностям.
/// </summary>
public struct FighterSlot
{
    public int Index;
}

/// <summary>
/// Зерно бойца (<c>Fighter.Seed</c> SWORD): равномерно в [0, 1), берётся при постройке бойца; его читает ИИ врага
/// (покачивание <c>sin(time·0,7 + seed·10)</c>, этап 4б).
/// </summary>
public struct FighterSeed
{
    public float Value;
}

/// <summary>
/// Состояние арены — ресурс-значение (хешируется): заданное число врагов (<c>DuelArena.EnemyCount</c> SWORD). Бойцов на
/// арене — 1 + <see cref="Enemies"/>, места 0…<see cref="Enemies"/> (инвариант <c>arena.slots</c>).
/// </summary>
public struct ArenaState
{
    /// <summary>Врагов не больше: <c>DuelArena.MaxFighters</c> 33 минус игрок.</summary>
    public const int MaxEnemies = 32;

    public int Enemies;
}

/// <summary>Что делает команда арены.</summary>
public enum ArenaAction : byte
{
    /// <summary>Ничего (пустая команда — пропускается).</summary>
    None = 0,

    /// <summary>Число врагов на <see cref="ArenaCommand.Enemies"/> больше (меньше — отрицательным): PageUp/PageDown SWORD.</summary>
    ChangeEnemies = 1,

    /// <summary>Число врагов — ровно <see cref="ArenaCommand.Enemies"/> (<c>DuelArena.SetEnemyCount</c>).</summary>
    SetEnemies = 2,

    /// <summary>Сброс арены (R, <c>DuelArena.Reset(EnemyCount)</c>): игрок заново в центре, все враги заново.</summary>
    Reset = 3,
}

/// <summary>
/// Команда арены на тик (раздел 7 плана): клиент порождает её из клавиш PageUp/PageDown/R (фронт — ровно одним тиком),
/// агент — <c>command.send</c>; реплей хранит её готовой. Число врагов — в пределах 0…<see cref="ArenaState.MaxEnemies"/>
/// (больше или меньше — упирается в предел, как <c>SetEnemyCount</c>). Несколько команд тика применяются по порядку.
/// </summary>
public struct ArenaCommand
{
    public ArenaAction Action;

    /// <summary>Сколько врагов добавить (<see cref="ArenaAction.ChangeEnemies"/>) или сколько их будет (<see cref="ArenaAction.SetEnemies"/>).</summary>
    public int Enemies;
}

/// <summary>
/// Команда игрока на тик (из клиента или реплея). Направление движения уже в мировых координатах — клиент считает его
/// относительно камеры до записи команды (раздел 7): симуляция камеры не знает. Удерживаемое действие клиент повторяет
/// в каждом тике кадра.
/// </summary>
public struct PlayerCommand
{
    /// <summary>
    /// Куда идти по земле (x, z) — только направление: длина не важна, скорость — бег или шаг (<see cref="Walk"/>),
    /// как WASD в SWORD (<c>PlayerController.Think</c> нормирует направление). Ноль — стоять.
    /// </summary>
    public Vector2 Move;

    /// <summary>Шагом (Shift), а не бегом.</summary>
    public bool Walk;

    /// <summary>
    /// Курсор на земле в этом кадре: <see cref="Gaze"/> — новая точка взгляда. Без неё действует прежняя
    /// (<see cref="GazeTarget"/>), как в SWORD.
    /// </summary>
    public bool HasGaze;

    /// <summary>Точка взгляда на земле. Нечисловые координаты (NaN, бесконечность) команда не несёт — такие отбрасываются.</summary>
    public Vector3 Gaze;
}
