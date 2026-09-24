namespace Sword.Engine.Physics;

/// <summary>
/// Настройки физики одной симуляции (этап 7): задаются при композиции (<see cref="PhysicsWorld(int, int, PhysicsSettings?)"/>)
/// и заменяются на ходу целиком (<see cref="IPhysicsWorld.Settings"/> — ручки «Рэгдолл» игры, 7з); подсистема конфиг не
/// читает. Неизменяемый объект: замена — <c>settings with { … }</c>, общий экземпляр у двух миров безопасен. Отправные
/// значения — SWORD 2eab13b (<c>DuelArena</c>, <c>RagdollSettings</c>, <c>KinematicContacts</c>); кто что читает — README.
/// </summary>
public sealed record PhysicsSettings
{
    /// <summary>Настройки по умолчанию (отправные значения).</summary>
    public static PhysicsSettings Default { get; } = new();

    /// <summary>Подшагов на шаг (<see cref="IPhysicsWorld.Step"/>): тик 1/120 с — h = 1/480 с. 1…64. Читает 7г.</summary>
    public int Substeps { get; init; } = 4;

    /// <summary>
    /// Скорость выталкивания, м/с: застарелая глубина контакта (тело ушло в землю в позе анимации, трупы легли друг в друга)
    /// уходит не быстрее её сверх встречного движения; толчок живого разгоняет точку не быстрее скорости толкающей точки
    /// плюс эта. Читают 7г (<c>SolvedDepth</c>) и 7д (предел толчка).
    /// </summary>
    public float PopSpeed { get; init; } = 3f;

    /// <summary>
    /// Трение тел на физике о землю, μ (7г): трение покоя «всё или ничего» (<c>Xpbd.Friction</c>) с общим бюджетом на тело
    /// (μ·Σλn по всем касаниям подшага) и тот же μ — трение скольжения по Кулону в скоростном проходе.
    /// </summary>
    public float StaticFriction { get; init; } = 0.7f;

    /// <summary>
    /// Податливость контакта тело ↔ тело (тела на физике между собой и толчок живого), м/Н. Земля, стена и колонны — жёсткие
    /// (0). Читают 7г и 7д.
    /// </summary>
    public float ContactCompliance { get; init; } = 1e-7f;

    /// <summary>
    /// Податливость пределов суставов, рад/(Н·м), — у суставов, чьё описание не задаёт свою
    /// (<see cref="JointDesc.LimitCompliance"/> = null; у SWORD — наклон и скручивание трупа). Читает 7в.
    /// </summary>
    public float JointLimitCompliance { get; init; } = 2e-4f;

    /// <summary>
    /// Бюджет толчка пары, Н: наибольшая сила, которой кинематическое сочленение толкает одно сочленение на физике (все тела
    /// вместе) — <c>λ ≤ F·h²</c> за подшаг. Читает 7д.
    /// </summary>
    public float PushForce { get; init; } = 800f;

    /// <summary>
    /// Доля прохода: потенциал импульса прохода пары — <c>PassShare · масса удара · min(скорость, KinematicPointSpeedMax)</c>,
    /// Н·с (<see cref="IPhysicsWorld.PreparePass"/>). Читает 7д.
    /// </summary>
    public float PassShare { get; init; } = 0.5f;

    /// <summary>Потолок скорости кинематической точки, м/с: в пределе толчка и в потенциале прохода. Читает 7д.</summary>
    public float KinematicPointSpeedMax { get; init; } = 16f;

    /// <summary>Столько тиков подряд без контакта и без попадания — и проход пары закрыт (<see cref="IPhysicsWorld.EndKinematicTick"/>). От 1. Читает 7д.</summary>
    public int PassExitTicks { get; init; } = 2;

    /// <summary>
    /// Доля Δv удара: импульс удара прибавляет точке удара скорость вдоль удара не больше этой доли скорости удара
    /// (<see cref="IPhysicsWorld.ApplyHit"/>). Читает 7д.
    /// </summary>
    public float HitDvShare { get; init; } = 0.5f;

    /// <summary>
    /// Скорость сведения унаследованного разрыва точки сустава, м/с (7з-2): разрыв до <c>1 см</c>, оставленный прошлым подшагом (контакты
    /// решаются после суставов — удар трупа о землю рвёт сустав на 5…7 мм), сводится не быстрее её; новый разрыв этого подшага (интеграция)
    /// и разрыв сверх 1 см — целиком. Как <see cref="PopSpeed"/> для застарелой глубины: одна жёсткая поправка переводила разрыв 5…7 мм в
    /// скорость ~3 м/с — всплеск энергии до 10…17 Дж за подшаг. <see cref="float.PositiveInfinity"/> — без ограничения (до 7з-2). Читает 7в
    /// (проход суставов). Своя, не SWORD.
    /// </summary>
    public float JointPopSpeed { get; init; } = 1.5f;

    /// <summary>
    /// Трение между телами на физике μ (7з-2): трение покоя пар «всё или ничего» с общим бюджетом тела (μ·Σλn его пар в подшаге) после
    /// нормалей всех пар подшага и трение скольжения по Кулону в скоростном проходе — как у земли. 0 — без трения (до 7з-2: тело, лежащее на
    /// другом трупе, съезжало с него под тяжестью — «вздрагивание» кучи). Читает 7г. Своя, не SWORD.
    /// </summary>
    public float PairFriction { get; init; } = 0.7f;

    /// <summary>
    /// Порог сна сочленения, м/с (7з-3): среднеквадратичная скорость его массы <c>√(2·E/M)</c> (E — кинетическая энергия тел с вращением, M —
    /// масса тел) меньше неё — сочленение «стоит» целиком (вздрагивание лёгкой стопы или клинка его не держит; сползающее целиком — держит).
    /// Сочленение засыпает, когда медленнее этого порога и <see cref="SleepBodySpeed"/> <see cref="SleepTime"/> подряд (README физики, «7з-3»).
    /// 0 — сна нет (сравнение строгое). Своя, не SWORD.
    /// </summary>
    public float SleepSpeed { get; init; } = 0.05f;

    /// <summary>
    /// Порог сна части, м/с (7з-3): самая быстрая точка каждого тела сочленения (<c>|v| + |ω⊥|·полудлина + |ω|·радиус</c>) медленнее него —
    /// условие сна; тело живого или неспящее тело на физике у спящего, которое в ближайшей точке оси быстрее него, будит спящее (толчок,
    /// касание). 0 — сна нет. Своя, не SWORD.
    /// </summary>
    public float SleepBodySpeed { get; init; } = 0.5f;

    /// <summary>
    /// Время покоя до сна, с (7з-3): столько подряд сочленение медленнее порогов — и оно засыпает (шаг его не считает, пока не разбудят).
    /// <see cref="float.PositiveInfinity"/> — сна нет (по умолчанию: свойства шага и тесты прежние; игра включает ручками «Рэгдолл»). Своя, не
    /// SWORD.
    /// </summary>
    public float SleepTime { get; init; } = float.PositiveInfinity;

    /// <summary>Сон включён: время конечно, пороги больше 0.</summary>
    public bool SleepEnabled => SleepTime < float.PositiveInfinity && SleepSpeed > 0 && SleepBodySpeed > 0;

    /// <summary>Что не так с настройками (null — годны): подшаги 1…64, проход — от 1 тика, числа — конечные и не меньше 0.</summary>
    public string? Problem()
    {
        if (Substeps is < 1 or > 64) return $"подшагов {Substeps}: нужно 1…64";
        if (PassExitTicks < 1) return $"тиков до закрытия прохода {PassExitTicks}: нужно от 1";
        if (!NonNegative(PopSpeed)) return "скорость выталкивания — конечная, не меньше 0";
        if (!NonNegative(SleepSpeed) || !NonNegative(SleepBodySpeed)) return "пороги сна — конечные, не меньше 0 (0 — сна нет)";
        if (float.IsNaN(SleepTime) || SleepTime < 0) return "время покоя до сна — не меньше 0 (∞ — сна нет)";
        if (float.IsNaN(JointPopSpeed) || JointPopSpeed < 0) return "скорость сведения разрыва сустава — не меньше 0 (∞ — без ограничения)";
        if (!NonNegative(StaticFriction)) return "трение покоя — конечное, не меньше 0";
        if (!NonNegative(PairFriction)) return "трение между телами — конечное, не меньше 0";
        if (!NonNegative(ContactCompliance) || !NonNegative(JointLimitCompliance)) return "податливости — конечные, не меньше 0";
        if (!NonNegative(PushForce)) return "бюджет толчка — конечный, не меньше 0";
        if (!NonNegative(PassShare) || !NonNegative(HitDvShare)) return "доли прохода и Δv удара — конечные, не меньше 0";
        if (!NonNegative(KinematicPointSpeedMax)) return "потолок скорости кинематической точки — конечный, не меньше 0";
        return null;
    }

    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0;
}
