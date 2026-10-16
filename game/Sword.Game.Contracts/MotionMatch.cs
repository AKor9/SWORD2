using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Повод поиска Motion Matching: почему в этом тике искали кадр.</summary>
public enum MotionSearchReason : byte
{
    /// <summary>Не поиск (переход по ребру графа) или поиска ещё не было.</summary>
    None = 0,

    /// <summary>Первый поиск после постройки бойца (появление, возрождение, R, команда сцены).</summary>
    Start = 1,

    /// <summary>По расписанию: прошло <c>round(locomotion.searchPeriod × частота)</c> тиков.</summary>
    Schedule = 2,

    /// <summary>Скачок цели скорости модели контроллера с прошлого поиска.</summary>
    GoalJump = 3,

    /// <summary>Первый тик после действия всем телом (рывок): под ним поиска нет.</summary>
    AfterAction = 4,
}

/// <summary>Вид перехода проигрывателя Motion Matching.</summary>
public enum MotionTransitionKind : byte
{
    /// <summary>Смена кадра поиском: найденный кадр дешевле продолжения больше чем на порог смены (запрошенный переход).</summary>
    Search = 0,

    /// <summary>Ребро графа базы: конец нециклического клипа или ранний выход — вход продолжения.</summary>
    Edge = 1,

    /// <summary>Поворот на месте: стоя курс корня ушёл от курса ног — первый кадр клипа поворота (<c>Stand_Turn_*</c>; запрошенный переход).</summary>
    TurnInPlace = 2,
}

/// <summary>
/// Событие: переход проигрывателя Motion Matching с инерциализацией — из кадра базы (у поиска — текущий, у ребра —
/// кадр ребра) в кадр входа. Цена: у поиска — найденного кадра и продолжения, у ребра — шва с весами поиска, у поворота
/// на месте — −1. Шлёт модуль Locomotion (Movement) при <c>locomotion.player = mm</c>.
/// </summary>
public struct MotionTransition
{
    public Entity Fighter;
    public MotionTransitionKind Kind;

    /// <summary>Повод поиска (у ребра — <see cref="MotionSearchReason.None"/>).</summary>
    public MotionSearchReason Reason;

    public int From, To, FromClip, ToClip, FromClipFrame, ToClipFrame;
    public float Cost, ContinuationCost;

    /// <summary>Переходов проигрывателя с постройки — после этого.</summary>
    public int Transitions;
}

/// <summary>Сводка базы Motion Matching: клипов, кадров, искомых, рёбер, ранних выходов, мс постройки, отпечаток (64 бита).</summary>
public readonly record struct MotionMatchBase(int Clips, int Frames, int Searchable, int Edges, int EarlyExits, double BuildMilliseconds,
    ulong Fingerprint);

/// <summary>
/// Состояние Motion Matching бойца после тика. Контакты стоп — бит k: сустав <see cref="IMotionMatchView.ContactJoint"/>(k)
/// на земле. Поворот на месте: <see cref="Twist"/> — курс корня минус курс ног, рад (+ — корень левее), <see cref="Chest"/> —
/// выравнивание груди, рад, <see cref="TurnClip"/> — клип поворота, пока корень поворачивается (−1 — нет).
/// </summary>
public readonly record struct MotionMatchStatus(int Frame, int Clip, int ClipFrame, float Alpha, float Rate, bool Stepping, bool Searching,
    int SinceSearch, int Searches, int Switches, int Transitions, int Contacts, float Twist = 0, float Chest = 0, int TurnClip = -1, int Turns = 0);

/// <summary>Последний поиск бойца; −1 — нет. Время поиска, мкс, — диагностика: в хеш и реплей не входит, от машины зависит.</summary>
public readonly record struct MotionMatchSearch(long Tick, MotionSearchReason Reason, int Current, int Frame, int Clip, int ClipFrame,
    float Cost, float ContinuationCost, int Blocks, int Frames, bool Switched, double Microseconds, double MaxMicroseconds);

/// <summary>Точка траектории в мире: момент относительно «сейчас», с (прошлое — меньше 0), положение на земле (y = 0) и курс, рад.</summary>
public readonly record struct MotionTrajectoryPoint(float Time, Vector3 Position, float Yaw);

/// <summary>
/// Замер тика (инвариант <c>locomotion.mmContinuity</c>) по выходу проигрывателя: скольжение опорной стопы по разметке
/// кадра базы, м/с, и наибольший скачок сустава за тик в пространстве персонажа, м; нет данных — NaN и −1.
/// </summary>
public readonly record struct MotionMatchMeasure(float FootSlide, int SlideJoint, int FeetOnGround, float JointJump, int JumpJoint)
{
    public static MotionMatchMeasure None => new(float.NaN, -1, 0, float.NaN, -1);
}

/// <summary>
/// Motion Matching бойцов для наблюдателей (оверлей <c>mm</c>, F3 «Локомоция»), только чтение; есть только при
/// <c>locomotion.player = mm</c>. Траектории — в мире по корню последнего поиска; буфер ответа — до следующего вызова.
/// </summary>
public interface IMotionMatchView : IReadView
{
    /// <summary>Сводка и отпечаток базы.</summary>
    MotionMatchBase Base { get; }

    /// <summary>Отбор клипов базы (<c>locomotion.clips</c>).</summary>
    string Pattern { get; }

    /// <summary>Групп признаков схемы (у GASP — 12) и имя группы.</summary>
    int GroupCount { get; }

    string GroupName(int group);

    /// <summary>Суставов контакта стоп (бит k <see cref="MotionMatchStatus.Contacts"/>) и сустав скелета контакта k.</summary>
    int ContactJointCount { get; }

    int ContactJoint(int contact);

    /// <summary>Состояние бойца; боец не на Motion Matching или ещё не начат — false.</summary>
    bool TryStatus(Entity fighter, out MotionMatchStatus status);

    /// <summary>Последний поиск бойца; поиска с постройки не было — false.</summary>
    bool TrySearch(Entity fighter, out MotionMatchSearch search);

    /// <summary>Цена группы <paramref name="group"/> в последнем поиске (сумма групп — цена); поиска не было — NaN.</summary>
    float GroupCost(Entity fighter, int group);

    /// <summary>Желаемая траектория — запрос последнего поиска: прошлое (история корня), «сейчас», будущее по модели контроллера.</summary>
    ReadOnlySpan<MotionTrajectoryPoint> Desired(Entity fighter);

    /// <summary>Траектория найденного кадра (сырые признаки кадра) от того же корня.</summary>
    ReadOnlySpan<MotionTrajectoryPoint> Matched(Entity fighter);

    /// <summary>История корня бойца (записи по тикам, после столкновений), от старой к новой; время — относительно последней записи.</summary>
    ReadOnlySpan<MotionTrajectoryPoint> History(Entity fighter);

    /// <summary>Замер тика — скольжение опорной стопы и скачок сустава (<see cref="MotionMatchMeasure"/>); не на MM — <see cref="MotionMatchMeasure.None"/>.</summary>
    MotionMatchMeasure Measure(Entity fighter);
}
