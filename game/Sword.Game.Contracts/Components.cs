using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Корень бойца на земле: позиция (y = 0, высоту тела даёт постановка на рельеф) и курс — поворот вокруг Y, рад
/// (0 — взгляд вдоль +Z).
/// </summary>
public struct Transform
{
    public Vector3 Position;
    public float Facing;
}

/// <summary>Корень прошлого тика — для интерполяции в клиенте.</summary>
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
/// Точка взгляда бойца. У игрока — последняя, которую дал курсор: курс-цель каждый тик доводится на неё, даже когда
/// курсора на земле в этом кадре нет. У врага — корень игрока до движения тика; потребителя пока нет.
/// </summary>
public struct GazeTarget
{
    public Vector3 Point;

    /// <summary>Курсор хоть раз был на земле.</summary>
    public bool Has;
}

/// <summary>
/// Поза бойца в пуле поз Animation (в ECS — хэндл): номер слота и поколение. Поза тика — модельные матрицы суставов
/// (+Z вперёд, +X влево, Y вверх); ядро заводит её с бойцом и освобождает со снятием компонента.
/// </summary>
public struct PoseRef
{
    public int Index;
    public int Generation;
}

/// <summary>
/// Тело бойца в Physics (в ECS — хэндл): сочленение из <see cref="BodySegs.Bodies"/> капсул. Заводит хук добавления
/// компонента на том же барьере, освобождает — хук снятия; постройка заново на той же сущности тело не меняет.
/// Тело <c>i</c> — сегмент <see cref="BodySegs.Segment"/>(i).
/// </summary>
public struct BodyRef
{
    public int Index;
    public int Generation;
}

/// <summary>
/// Поза бойца при постройке: клип <see cref="Clip"/> в момент <see cref="Time"/>; −1 — поза привязки. Ресурс, одна на
/// всех; от неё зависит скорость капсул в первом тике. Боец с <see cref="FighterStartPose"/> строится по своей.
/// </summary>
public struct StartPose
{
    public int Clip;
    public float Time;
}

/// <summary>
/// Своя поза постройки бойца (как <see cref="StartPose"/>): игроку на Motion Matching — первый кадр стойки базы MM,
/// чтобы капсулы не прыгали в первом тике. Слой <see cref="LayerClip"/> смешивается по поддереву <see cref="LayerJoint"/>
/// с весом <see cref="LayerWeight"/> (0 — слоя нет): у игрока на MM это стойка оружия по руке с ключицей.
/// </summary>
public struct FighterStartPose
{
    public int Clip;
    public float Time;

    /// <summary>Слой поверх: клип и момент, корень поддерева (номер сустава скелета) и вес 0…1; вес 0 — слоя нет.</summary>
    public int LayerClip;

    /// <inheritdoc cref="LayerClip"/>
    public float LayerTime;

    /// <inheritdoc cref="LayerClip"/>
    public int LayerJoint;

    /// <inheritdoc cref="LayerClip"/>
    public float LayerWeight;
}

/// <summary>
/// Действующая модель контроллера корня на этот тик: курс к <see cref="GoalYaw"/> не быстрее <see cref="TurnRate"/> рад/с,
/// затем <c>v += (цель − v)·min(1, Rate·dt)</c> к <see cref="GoalVelocity"/> (параметры уже с масштабами действия тика).
/// Траектория запроса Motion Matching считается этой же моделью; ядро пишет её бойцам с компонентом. Вне модели: рывок,
/// сдвиг клипом, столкновения, отдача.
/// </summary>
public struct SteerModel
{
    /// <summary>Темп разгона к цели скорости, 1/с.</summary>
    public float Rate;

    /// <summary>Предел скорости поворота курса, рад/с (с масштабом поворота действия).</summary>
    public float TurnRate;

    /// <summary>Цель скорости, мир, м/с (Y = 0; с масштабом хода действия).</summary>
    public Vector3 GoalVelocity;

    /// <summary>Цель курса, рад.</summary>
    public float GoalYaw;
}

/// <summary>
/// Движение бойца для слоя локомоции: сглаженная скорость в системе бойца (x — вправо, z — вперёд), «идёт»
/// с гистерезисом и можно ли искать новый кадр (false — действие ведёт всё тело). Пишет ядро в Gameplay.
/// </summary>
public struct AnimatorMotion
{
    public Vector3 Velocity;
    public bool Moving;
    public bool Search;
}

/// <summary>
/// Боец (тег). <c>Added&lt;Fighter&gt;</c> — боец построен (в том числе заново на той же сущности при сбросе арены):
/// модули заводят по нему своё состояние с нуля.
/// </summary>
public struct Fighter;

/// <summary>Боец игрока (тег).</summary>
public struct PlayerTag;

/// <summary>Боец-враг (тег): место 1…32 (<see cref="FighterSlot"/>), намерение ему пишет ИИ.</summary>
public struct EnemyTag;

/// <summary>
/// Место бойца на арене: игрок — 0, враги — 1…<see cref="ArenaState.MaxEnemies"/> без дыр. От порядка мест зависит
/// порядок сложения float, поэтому системы, которым он важен, идут по местам, а не по сущностям.
/// </summary>
public struct FighterSlot
{
    public int Index;
}

/// <summary>Зерно бойца: равномерно в [0, 1), берётся при постройке; его читает ИИ врага (<c>sin(time·0,7 + seed·10)</c>).</summary>
public struct FighterSeed
{
    public float Value;
}

/// <summary>
/// Состояние арены — ресурс-значение (хешируется): число врагов; места 0…<see cref="Enemies"/> (инвариант <c>arena.slots</c>).
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

    /// <summary>Число врагов на <see cref="ArenaCommand.Enemies"/> больше (меньше — отрицательным): PageUp/PageDown.</summary>
    ChangeEnemies = 1,

    /// <summary>Число врагов — ровно <see cref="ArenaCommand.Enemies"/> (<c>DuelArena.SetEnemyCount</c>).</summary>
    SetEnemies = 2,

    /// <summary>Сброс арены (R, <c>DuelArena.Reset(EnemyCount)</c>): игрок заново в центре, все враги заново.</summary>
    Reset = 3,

    /// <summary>Следующий меч инвентаря игрока по кругу (T).</summary>
    NextWeapon = 4,

    /// <summary>Взять меч инвентаря номер <see cref="ArenaCommand.Weapon"/> (клавиши 1–9, панель инвентаря); нет такого — ничего.</summary>
    EquipWeapon = 5,
}

/// <summary>
/// Команда арены на тик (клавиши PageUp/PageDown/R/T, фронт — одним тиком); число врагов упирается в 0…
/// <see cref="ArenaState.MaxEnemies"/>. Команды тика применяются по порядку в Begin: Intent видит арену после них.
/// </summary>
public struct ArenaCommand
{
    public ArenaAction Action;

    /// <summary>Сколько врагов добавить (<see cref="ArenaAction.ChangeEnemies"/>) или сколько их будет (<see cref="ArenaAction.SetEnemies"/>).</summary>
    public int Enemies;

    /// <summary>Номер меча в таблице оружия для <see cref="ArenaAction.EquipWeapon"/> (в старых реплеях поля нет — 0).</summary>
    public int Weapon;
}

/// <summary>
/// Команда врагу на тик (инструменты, тесты): желаемая скорость и курс-цель вместо ИИ. Игра её не посылает.
/// Действует один тик; из нескольких одному месту — последняя; места без врага пропускаются.
/// </summary>
public struct EnemyCommand
{
    /// <summary>Место врага, 1…<see cref="ArenaState.MaxEnemies"/> (<see cref="FighterSlot"/>).</summary>
    public int Slot;

    /// <summary>Желаемая скорость по земле (x, z), м/с — как её дал бы ИИ (не направление). Нечисловая — стоять.</summary>
    public Vector2 Velocity;

    /// <summary>Курс-цель, рад (0 — взгляд вдоль +Z). Нечисловой — курс-цель ИИ этого тика.</summary>
    public float FacingGoal;

    /// <summary>
    /// Удар в этот тик вместо ИИ: номер удара + 1 (1…4 — slash1–4, 5…7 — stab1–3, <see cref="ICombatView.Attacks"/>);
    /// 0 — нет. Начинается, если враг может ударить; таймер удара ИИ не трогается.
    /// </summary>
    public int Attack;
}

/// <summary>
/// Команда игрока на тик. Направление движения — уже в мировых координатах (камеру симуляция не знает); нажатия
/// (удар, укол, рывок) — фронты в одном тике, ждут в буферах игрока. В старых реплеях этих полей нет — нули.
/// </summary>
public struct PlayerCommand
{
    /// <summary>Куда идти по земле (x, z) — только направление; скорость — бег или шаг (<see cref="Walk"/>). Ноль — стоять.</summary>
    public Vector2 Move;

    /// <summary>Шагом (Shift), а не бегом.</summary>
    public bool Walk;

    /// <summary>Курсор на земле в этом кадре: <see cref="Gaze"/> — новая точка взгляда; без неё действует прежняя.</summary>
    public bool HasGaze;

    /// <summary>Точка взгляда на земле. Нечисловые координаты (NaN, бесконечность) команда не несёт — такие отбрасываются.</summary>
    public Vector3 Gaze;

    /// <summary>
    /// Рубящий удар (ЛКМ) — фронт; ждёт в буфере игрока 0,3 с. Без жеста — следующий удар связки, с жестом — ближайший
    /// по направлению замаха.
    /// </summary>
    public bool Attack;

    /// <summary>Укол (Q) — фронт: в буфер удара 0,3 с, удар — укол (случайный клип уколов).</summary>
    public bool Stab;

    /// <summary>
    /// Жест удара — направление замаха в системе бойца (x — вправо, y — вверх); ноль — связка. Клиент выбирает удар,
    /// чья траектория на экране ближе к сдвигу мыши перед кликом (порог 12 пикс.).
    /// </summary>
    public Vector2 Gesture;

    /// <summary>
    /// Рывок (Пробел) — фронт, буфер 0,2 с: ускорение в сторону <see cref="Move"/> (без хода — назад), перезарядка 0,6 с.
    /// </summary>
    public bool Dash;
}
