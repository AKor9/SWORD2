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
/// Точка взгляда бойца игрока — последняя, которую дал курсор (<c>PlayerController._cursor</c> и <c>Fighter.GazePoint</c>
/// SWORD): курс-цель каждый тик доводится на неё от текущего корня, даже когда курсора на земле в этом кадре нет.
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

/// <summary>Боец (тег).</summary>
public struct Fighter;

/// <summary>Боец игрока (тег).</summary>
public struct PlayerTag;

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
