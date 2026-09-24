using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Источник контакта и поправки трассы физики (7и) — как <c>PhysicsSource</c> подсистемы Physics, для наблюдателей без неё: суставы,
/// земля, стена и колонны, пара тел на физике, толчок живого телом, клинком в ударе (проход), без отдачи (за отрубом).
/// </summary>
public enum PhysicsContactSource : byte
{
    Joints = 0,
    Ground = 1,
    Wall = 2,
    Pair = 3,
    PushBody = 4,
    PushPass = 5,
    PushNoReaction = 6,
}

/// <summary>Накопитель трассы физики (7и): последний тик, окно (с начала окна — сброс владельцем или агентом), прогон (с включения).</summary>
public enum PhysicsTracePeriod : byte
{
    Last = 0,
    Window = 1,
    Run = 2,
}

/// <summary>
/// Сустав сочленения тела (7и; оверлей <c>joints</c>, агент): тела родителя и ребёнка (номера тел сочленения — у бойца это сегменты
/// <see cref="BodySegs"/>), шарнир ли (иначе — наклон-скручивание), включён ли (сустав запястья трупа выключен — меч выпал); точка
/// сустава на ребёнке и на родителе — в системах своих тел, м (описание и привязка сустава); замер в позе тика: ошибка точки, м, сгиб
/// шарнира, наклон, скручивание, рад, выход за предел — наибольший по пределам сустава, рад (0 — в пределах).
/// </summary>
public readonly record struct PhysicsJointView(int Parent, int Child, bool Hinge, bool Enabled, Vector3 ChildAnchor, Vector3 ParentAnchor,
    float PointError, float Bend, float Swing, float Twist, float Excess);

/// <summary>
/// Контакт последнего подшага последнего шага (7и; оверлей <c>contacts</c>): источник, тело на физике — сочленение и номер тела в нём,
/// второе тело (сочленение пустое — мир: земля, стена, колонна; у толчка — кинематическое тело живого), точка на теле и нормаль к нему
/// (мир), настоящая глубина и решённая за подшаг, м.
/// </summary>
public readonly record struct PhysicsContactView(PhysicsContactSource Source, BodyRef Body, int BodyIndex, BodyRef Other, int OtherIndex,
    Vector3 Point, Vector3 Normal, float Depth, float Solved);

/// <summary>
/// Сводка накопителя трассы (7и; агент — <c>physics.bodies {trace}</c>): шагов, подшагов, секунд; событий быстрее порога; скорости тел
/// на физике по подшагам — наибольшая (и чьё тело), перцентили p99 и p99,9, угловая — наибольшая и p99; упоров в потолок профиля, замеров
/// и нечисловых; толчки живых — наибольшая доля бюджета пары и пар·подшагов, доля бюджета прохода, выталкивание сверх толкающего, м/с;
/// отдача — наибольшая (чья) и сумма, Н·с; удары — число, урезанные потолком и проходом, доля Δv, Δv и Δω тела; трение покоя — касаний и
/// держало; рост энергии за подшаг, Дж — у сочленения без толчков (чьё), у всех без толчков, у толкаемого живыми.
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
/// Рост энергии за подшаг сверх сводки накопителя (7з-2; агент — <c>physics.bodies {trace}</c> → <c>energy</c>), Дж: у трупа, касавшегося
/// другого тела на физике (чьё), — передача при ударе трупа о труп, не накачка; наибольший рост суммы всех касавшихся друг друга за подшаг
/// (инвариант <c>physics.energy</c>); наибольший рост сочленения без толчков живых по фазам подшага — интеграция, суставы, мир (земля, стена,
/// колонны), пары, мир после пар (прижатые парами к земле), скорости по профилю, скоростной проход, расслабление суставов (где энергия
/// появилась).
/// </summary>
public readonly record struct PhysicsTraceEnergy(float Paired, BodyRef PairedBody, float PairedTotal, float Integrate, float Joints, float World,
    float Pairs, float Pressed, float Velocity, float ContactVelocity, float Relax);

/// <summary>
/// Сон трупов в накопителе трассы (7з-3; агент — <c>physics.bodies {trace}</c> → <c>sleep</c>): засыпаний; наибольшее число спящих на конец
/// шага; касаний спящего тела неспящим телом на физике в подшагах (спящее — неподвижная опора); пробуждений по причинам — толчок живого,
/// удар, касание движущегося трупа, цепочка по куче, команда (запись тела, режим, профиль, суставы), настройки (ручки, земля).
/// </summary>
public readonly record struct PhysicsTraceSleep(int Sleeps, int AsleepMax, int Contacts, int Push, int Hit, int Touch, int Chain, int Command,
    int Settings)
{
    /// <summary>Пробуждений всего.</summary>
    public int Wakes => Push + Hit + Touch + Chain + Command + Settings;
}

/// <summary>
/// Накопленное трассой по источнику (7и): поправок; наибольшие сдвиг / h, м/с, угол / h, рад/с, скрытый сдвиг прошлой позы / h;
/// псевдоимпульс за тик — наибольший и сумма, Н·с; контактов, наибольшая глубина и отложенная, м; погашено скоростным проходом, м/с;
/// точный импульс толчков, Н·с.
/// </summary>
public readonly record struct PhysicsTraceSource(int Count, float Linear, float Angular, float Hidden, float ImpulseTickMax, float ImpulseSum,
    int Contacts, float Depth, float Deferred, float VelocitySolved, float Applied);

/// <summary>
/// Событие трассы (7и): тело на физике в тике шага <see cref="Step"/> (номер от включения) было быстрее порога — сочленение и номер
/// тела, масса, наибольшие скорость и угловая за тик, Δv удара перед шагом (0 — не было), главная поправка тика (источник с наибольшим
/// сдвигом / h) — её сдвиг / h, м/с, и угол / h, рад/с.
/// </summary>
public readonly record struct PhysicsTraceEvent(long Step, BodyRef Body, int BodyIndex, float Mass, float Speed, float AngularSpeed, float HitDv,
    PhysicsContactSource Main, float MainLinear, float MainAngular);

/// <summary>
/// Диагностический вид физики для наблюдателей (7и; клиент — оверлеи <c>joints</c> и <c>contacts</c>, агент — <c>physics.bodies</c>):
/// только чтение (<c>WorldReader.View&lt;IPhysicsDebugView&gt;()</c>). Суставы сочленения тела (описание, привязка, замер) и трасса
/// физики — диагностический режим <see cref="TraceMode"/> (включают владелец симуляции и агент — <c>Simulation.Diagnostics</c>; мир,
/// хеш и реплей от неё не зависят): контакты последнего подшага, сводки накопителей, события быстрее порога. Выключенная трасса — пусто
/// и нули.
/// </summary>
public interface IPhysicsDebugView : IReadView
{
    /// <summary>Id диагностического режима трассы физики (<c>Simulation.Diagnostics</c>).</summary>
    const string TraceMode = "physics.trace";

    /// <summary>Суставов в сочленении тела <paramref name="body"/>; тела нет (пустой или устаревший хэндл) — 0.</summary>
    int JointCount(BodyRef body);

    /// <summary>Сустав <paramref name="joint"/> (0 … <see cref="JointCount"/> − 1) сочленения тела <paramref name="body"/>.</summary>
    PhysicsJointView Joint(BodyRef body, int joint);

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

    /// <summary>Рост энергии накопителя <paramref name="period"/> сверх сводки (7з-2): в контакте трупов, их сумма, по фазам подшага.</summary>
    PhysicsTraceEnergy TraceEnergy(PhysicsTracePeriod period);

    /// <summary>Сон трупов накопителя <paramref name="period"/> (7з-3): засыпания, спящие, касания спящих, пробуждения по причинам.</summary>
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
