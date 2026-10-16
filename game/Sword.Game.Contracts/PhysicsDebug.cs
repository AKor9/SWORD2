using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Источник контакта и поправки трассы физики — как <c>PhysicsSource</c> подсистемы Physics, для наблюдателей без неё.</summary>
public enum PhysicsContactSource : byte
{
    Joints = 0,
    Ground = 1,
    Wall = 2,
    Pair = 3,
    PushBody = 4,
    PushPass = 5,
    PushNoReaction = 6,
    Self = 7,
}

/// <summary>Накопитель трассы физики: последний тик, окно (с начала окна — сброс владельцем или инструментом), прогон (с включения).</summary>
public enum PhysicsTracePeriod : byte
{
    Last = 0,
    Window = 1,
    Run = 2,
}

/// <summary>
/// Сустав сочленения тела (оверлей <c>joints</c>): тела родителя и ребёнка (у бойца — сегменты <see cref="BodySegs"/>);
/// выключен — запястье трупа (меч выпал); точки сустава — в системах своих тел, м; замер в позе тика: ошибка точки, м,
/// сгиб, наклон, скручивание и выход за предел, рад (0 — в пределах).
/// </summary>
public readonly record struct PhysicsJointView(int Parent, int Child, bool Hinge, bool Enabled, Vector3 ChildAnchor, Vector3 ParentAnchor,
    float PointError, float Bend, float Swing, float Twist, float Excess);

/// <summary>
/// Контакт последнего подшага (оверлей <c>contacts</c>): второе тело с пустым сочленением — мир (у толчка — кинематическое
/// тело живого); точка и нормаль — в мире, глубина настоящая и решённая за подшаг, м.
/// </summary>
public readonly record struct PhysicsContactView(PhysicsContactSource Source, BodyRef Body, int BodyIndex, BodyRef Other, int OtherIndex,
    Vector3 Point, Vector3 Normal, float Depth, float Solved);

/// <summary>
/// Сводка накопителя трассы: скорости тел на физике по подшагам (наибольшая, p99, p99,9; угловая), упоры в потолок,
/// толчки живых (доли бюджета, выталкивание, м/с), отдача, Н·с, удары (Δv, Δω), трение покоя, рост энергии за подшаг, Дж.
/// </summary>
public readonly record struct PhysicsTraceSummary(int Steps, int Substeps, double Seconds, int Events,
    float SpeedMax, BodyRef SpeedBody, int SpeedBodyIndex, float SpeedP99, float SpeedP999, float AngularSpeedMax, float AngularP99,
    int Capped, long SpeedSamples, int NonFinite,
    float PairShare, int PairSubsteps, float PassShare, float PushPopSpeed,
    float ReactionMax, BodyRef ReactionBody, float ReactionSum,
    int Hits, int HitsCapped, int HitsBudgeted, float HitDvShare, float HitDv, float HitDw,
    int FrictionContacts, int FrictionHeld,
    float EnergyGrowth, BodyRef EnergyBody, float EnergyGrowthTotal, float EnergyGrowthPushed);

/// <summary>
/// Рост энергии за подшаг сверх сводки, Дж: у трупа, касавшегося другого тела на физике (передача при ударе, не накачка),
/// сумма касавшихся, и у сочленения без толчков по фазам подшага — где энергия появилась.
/// </summary>
public readonly record struct PhysicsTraceEnergy(float Paired, BodyRef PairedBody, float PairedTotal, float Integrate, float Joints, float World,
    float Pairs, float Pressed, float Velocity, float ContactVelocity, float Relax);

/// <summary>
/// Самоколлизия тела: пар в таблице (у трупа — все, кроме связанных суставом и торса с бёдрами), действуют ли, толщина
/// (0 — выключена); в позе тика — пересекшихся пар и самая глубокая: номера тел (нет — −1) и глубина, м.
/// </summary>
public readonly record struct PhysicsSelfView(int Pairs, bool Active, float Scale, int Touching, int DeepestA, int DeepestB, float Depth);

/// <summary>
/// Сон трупов в накопителе трассы: засыпаний, наибольшее число спящих, касаний спящего неспящим (спящее — опора)
/// и пробуждений по причинам.
/// </summary>
public readonly record struct PhysicsTraceSleep(int Sleeps, int AsleepMax, int Contacts, int Push, int Hit, int Touch, int Chain, int Command,
    int Settings)
{
    /// <summary>Пробуждений всего.</summary>
    public int Wakes => Push + Hit + Touch + Chain + Command + Settings;
}

/// <summary>
/// Накопленное трассой по источнику: поправок; наибольшие сдвиг / h, м/с, угол / h, рад/с; псевдоимпульс, Н·с;
/// контактов и глубины, м; погашено скоростным проходом, м/с; точный импульс толчков, Н·с.
/// </summary>
public readonly record struct PhysicsTraceSource(int Count, float Linear, float Angular, float Hidden, float ImpulseTickMax, float ImpulseSum,
    int Contacts, float Depth, float Deferred, float VelocitySolved, float Applied);

/// <summary>
/// Событие трассы: тело на физике в шаге <see cref="Step"/> было быстрее порога — масса, скорости за тик, Δv удара
/// (0 — не было) и главная поправка тика: сдвиг / h, м/с, и угол / h, рад/с.
/// </summary>
public readonly record struct PhysicsTraceEvent(long Step, BodyRef Body, int BodyIndex, float Mass, float Speed, float AngularSpeed, float HitDv,
    PhysicsContactSource Main, float MainLinear, float MainAngular);

/// <summary>
/// Диагностический вид физики для наблюдателей (оверлеи <c>joints</c>, <c>contacts</c>), только чтение. Трасса — режим
/// <see cref="TraceMode"/> (мир, хеш и реплей от неё не зависят); выключенная трасса — пусто и нули.
/// </summary>
public interface IPhysicsDebugView : IReadView
{
    /// <summary>Id диагностического режима трассы физики (<c>Simulation.Diagnostics</c>).</summary>
    const string TraceMode = "physics.trace";

    /// <summary>Суставов в сочленении тела <paramref name="body"/>; тела нет (пустой или устаревший хэндл) — 0.</summary>
    int JointCount(BodyRef body);

    /// <summary>Сустав <paramref name="joint"/> (0 … <see cref="JointCount"/> − 1) сочленения тела <paramref name="body"/>.</summary>
    PhysicsJointView Joint(BodyRef body, int joint);

    /// <summary>Самоколлизия тела: пары и самая глубокая в позе тика; тела нет — пусто.</summary>
    PhysicsSelfView Self(BodyRef body);

    /// <summary>Трасса физики включена.</summary>
    bool TraceOn { get; }

    /// <summary>Порог события трассы, м/с (выключена — 0).</summary>
    float TraceEventSpeed { get; }

    /// <summary>Контакты последнего подшага последнего шага (выключена — пусто). Действуют до следующего тика.</summary>
    ReadOnlySpan<PhysicsContactView> Contacts { get; }

    /// <summary>Контактов последнего подшага, не поместившихся в трассу.</summary>
    int LostContacts { get; }

    /// <summary>Сводка накопителя <paramref name="period"/>.</summary>
    PhysicsTraceSummary TraceSummary(PhysicsTracePeriod period);

    /// <summary>Рост энергии накопителя <paramref name="period"/> сверх сводки: в контакте трупов, их сумма, по фазам подшага.</summary>
    PhysicsTraceEnergy TraceEnergy(PhysicsTracePeriod period);

    /// <summary>Сон трупов накопителя <paramref name="period"/>: засыпания, спящие, касания спящих, пробуждения по причинам.</summary>
    PhysicsTraceSleep TraceSleep(PhysicsTracePeriod period);

    /// <summary>Накопленное накопителем <paramref name="period"/> по источнику <paramref name="source"/>.</summary>
    PhysicsTraceSource TraceSource(PhysicsTracePeriod period, PhysicsContactSource source);

    /// <summary>Самых быстрых событий накопителя (по убыванию скорости, до 8).</summary>
    int TraceTopCount(PhysicsTracePeriod period);

    PhysicsTraceEvent TraceTop(PhysicsTracePeriod period, int k);

    /// <summary>Событий в кольце последних (до 64).</summary>
    int RecentEventCount { get; }

    /// <summary>Событие из кольца последних: 0 — самое новое.</summary>
    PhysicsTraceEvent RecentEvent(int k);
}
