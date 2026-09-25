using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Повод поиска Motion Matching (8з): почему в этом тике искали кадр.</summary>
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

/// <summary>Вид перехода проигрывателя Motion Matching (8з).</summary>
public enum MotionTransitionKind : byte
{
    /// <summary>Смена кадра поиском: найденный кадр дешевле продолжения больше чем на порог смены (запрошенный переход).</summary>
    Search = 0,

    /// <summary>Ребро графа базы: конец нециклического клипа или ранний выход — вход продолжения.</summary>
    Edge = 1,
}

/// <summary>
/// Событие (8з; журнал ECS — <c>events.history {"types":["MotionTransition"]}</c>, причина — <c>state.why {"cause":N}</c> в режиме
/// причинности): переход проигрывателя Motion Matching бойца с инерциализацией. Откуда — кадр базы, с которого ушёл проигрыватель (у поиска
/// — текущий кадр, с которым сравнивалось продолжение; у ребра — кадр ребра), куда — кадр входа; клипы — номера библиотеки
/// (<see cref="IAnimationView.ClipName"/>). Цена: у поиска — цена найденного кадра и цена продолжения (−1 — продолжения не было); у ребра —
/// цена кадра входа против признаков кадра ребра с весами поиска (насколько шов похож; продолжения нет — −1). Шлёт модуль Locomotion
/// (Movement, после исполнения заданий пула), только при <c>locomotion.player = mm</c>.
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

/// <summary>Сводка базы Motion Matching (8з): клипов, кадров, искомых, рёбер, ранних выходов, мс постройки, отпечаток (64 бита).</summary>
public readonly record struct MotionMatchBase(int Clips, int Frames, int Searchable, int Edges, int EarlyExits, double BuildMilliseconds,
    ulong Fingerprint);

/// <summary>
/// Состояние Motion Matching бойца после тика (8з): кадр базы, клип библиотеки и кадр в нём, доля интервала, темп; <see cref="Stepping"/> —
/// проигрыватель идёт (у мёртвого стоит), <see cref="Searching"/> — поиск разрешён (не под действием всем телом), тиков с последнего
/// поиска; счётчики с постройки — поисков, смен кадра поиском, переходов (поиском и по рёбрам графа); контакты стоп кадра — бит k — сустав
/// <see cref="IMotionMatchView.ContactJoint"/>(k) на земле (по разметке базы).
/// </summary>
public readonly record struct MotionMatchStatus(int Frame, int Clip, int ClipFrame, float Alpha, float Rate, bool Stepping, bool Searching,
    int SinceSearch, int Searches, int Switches, int Transitions, int Contacts);

/// <summary>
/// Последний поиск бойца (8з): тик, повод, текущий кадр (−1 — не было), найденный кадр, его клип и кадр в клипе, цена и цена продолжения
/// (−1 — нет), осмотрено блоков и кадров, сменил ли кадр; время поиска (запрос и перебор), мкс, — последнего и наибольшее с постройки
/// (диагностика: в хеш и реплей не входит, от машины зависит).
/// </summary>
public readonly record struct MotionMatchSearch(long Tick, MotionSearchReason Reason, int Current, int Frame, int Clip, int ClipFrame,
    float Cost, float ContinuationCost, int Blocks, int Frames, bool Switched, double Microseconds, double MaxMicroseconds);

/// <summary>Точка траектории в мире (8з): момент относительно «сейчас», с (прошлое — меньше 0), положение на земле (y = 0) и курс, рад.</summary>
public readonly record struct MotionTrajectoryPoint(float Time, Vector3 Position, float Yaw);

/// <summary>
/// Замер тика (8з; поля агента <c>MotionMatch.FootSlide</c>, <c>MotionMatch.JointJump</c>, инвариант <c>locomotion.mmContinuity</c>) по
/// выходу проигрывателя (поза Motion Matching без руки с мечом и слоя действий) и корню: скольжение опорной стопы — наибольшая
/// горизонтальная скорость в мире суставов контакта на земле по разметке кадра базы, м/с, и чей сустав (нет стоп на земле или прошлого тика
/// — NaN и −1), стоп на земле; наибольший скачок сустава за тик в пространстве персонажа, м, и чей (проигрыватель стоит — NaN и −1).
/// </summary>
public readonly record struct MotionMatchMeasure(float FootSlide, int SlideJoint, int FeetOnGround, float JointJump, int JumpJoint)
{
    public static MotionMatchMeasure None => new(float.NaN, -1, 0, float.NaN, -1);
}

/// <summary>
/// Motion Matching бойцов для наблюдателей (8з; клиент — оверлей <c>mm</c> и строки F3 «Локомоция», агент — <c>anim.pose</c>, поля
/// <c>MotionMatch.*</c>): вид только для чтения (<c>WorldReader.View&lt;IMotionMatchView&gt;()</c>) на базу и проигрыватели модуля Locomotion.
/// Есть только при <c>locomotion.player = mm</c> (иначе вида нет — <c>TryView</c> false). Траектории — в мире по корню последнего поиска
/// (как их сравнивал поиск: в единицах данных — с масштабом данных запроса); буфер ответа — до следующего вызова вида. Номера суставов и
/// клипов — скелета и библиотеки (<see cref="IAnimationView"/>).
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
