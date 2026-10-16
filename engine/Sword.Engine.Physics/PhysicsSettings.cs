namespace Sword.Engine.Physics;

/// <summary>
/// Настройки физики одной симуляции: задаются при композиции и заменяются на ходу целиком (ручки «Рэгдолл»). Неизменяемый
/// объект: замена — <c>settings with { … }</c>, общий экземпляр у двух миров безопасен. Кто что читает — README.
/// </summary>
public sealed record PhysicsSettings
{
    public static PhysicsSettings Default { get; } = new();

    /// <summary>Подшагов на шаг: тик 1/120 с — h = 1/480 с. 1…64.</summary>
    public int Substeps { get; init; } = 4;

    /// <summary>
    /// Скорость выталкивания, м/с: застарелая глубина контакта уходит не быстрее её сверх встречного движения; толчок живого
    /// разгоняет точку не быстрее толкающей точки плюс эта.
    /// </summary>
    public float PopSpeed { get; init; } = 3f;

    /// <summary>Трение тел о землю, μ: покоя — «всё или ничего» с общим бюджетом тела μ·Σλn; скольжения — по Кулону в скоростном проходе.</summary>
    public float StaticFriction { get; init; } = 0.7f;

    /// <summary>Податливость контакта тело ↔ тело и толчка живого, м/Н. Земля, стена и колонны — жёсткие.</summary>
    public float ContactCompliance { get; init; } = 1e-7f;

    /// <summary>Податливость пределов суставов, рад/(Н·м), — у суставов без своей (<see cref="JointDesc.LimitCompliance"/> = null).</summary>
    public float JointLimitCompliance { get; init; } = 2e-4f;

    /// <summary>Бюджет толчка пары, Н: наибольшая сила, которой кинематическое сочленение толкает одно сочленение на физике, — <c>λ ≤ F·h²</c> за подшаг.</summary>
    public float PushForce { get; init; } = 800f;

    /// <summary>Доля прохода: потенциал — <c>PassShare · масса удара · min(скорость, KinematicPointSpeedMax)</c>, Н·с.</summary>
    public float PassShare { get; init; } = 0.5f;

    /// <summary>Потолок скорости кинематической точки, м/с: в пределе толчка и в потенциале прохода.</summary>
    public float KinematicPointSpeedMax { get; init; } = 16f;

    /// <summary>Тиков подряд без контакта и попадания до закрытия прохода пары. От 1.</summary>
    public int PassExitTicks { get; init; } = 2;

    /// <summary>Доля Δv удара: удар прибавляет точке скорость вдоль удара не больше этой доли скорости удара.</summary>
    public float HitDvShare { get; init; } = 0.5f;

    /// <summary>
    /// Скорость сведения унаследованного разрыва точки сустава, м/с: разрыв до 1 см от прошлого подшага (удар трупа о землю рвёт
    /// сустав на 5…7 мм) сводится не быстрее её; новый и сверх 1 см — целиком. Жёсткая поправка давала ~3 м/с и всплеск
    /// до 10…17 Дж за подшаг. <see cref="float.PositiveInfinity"/> — без ограничения.
    /// </summary>
    public float JointPopSpeed { get; init; } = 1.5f;

    /// <summary>
    /// Трение между телами на физике, μ: покоя — «всё или ничего» с общим бюджетом тела после нормалей пар, скольжения — по
    /// Кулону. 0 — без трения (труп на трупе съезжает под тяжестью).
    /// </summary>
    public float PairFriction { get; init; } = 0.7f;

    /// <summary>
    /// Порог сна сочленения, м/с: <c>√(2·E/M)</c> меньше — сочленение стоит целиком (вздрагивание лёгкой части его не держит).
    /// Засыпает, когда медленнее этого и <see cref="SleepBodySpeed"/> <see cref="SleepTime"/> подряд. 0 — сна нет.
    /// </summary>
    public float SleepSpeed { get; init; } = 0.05f;

    /// <summary>
    /// Порог сна части, м/с: самая быстрая точка каждого тела медленнее — условие сна; тело у спящего быстрее — будит его.
    /// 0 — сна нет.
    /// </summary>
    public float SleepBodySpeed { get; init; } = 0.5f;

    /// <summary>Время покоя до сна, с. <see cref="float.PositiveInfinity"/> — сна нет (по умолчанию; игра включает ручками).</summary>
    public float SleepTime { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// Трение суставов, 1/с: относительная угловая скорость ребёнка и родителя гасится на <c>k·h / (1 + k·h)</c> за подшаг
    /// (неявный Эйлер) парой противоположных импульсов — энергии не добавляет, момент пары сохраняет. 0 — нет.
    /// </summary>
    public float JointFriction { get; init; } = 0;

    /// <summary>
    /// Толщина частей в самоколлизии: радиусы пар × это; 0 — самоколлизии нет, больше 1 — ближе к мешу. С миром, трупами и
    /// живыми — настоящие радиусы.
    /// </summary>
    public float SelfContactScale { get; init; } = 1;

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
        if (!NonNegative(JointFriction)) return "трение суставов — конечное, не меньше 0";
        if (!NonNegative(SelfContactScale)) return "толщина частей в самоколлизии — конечная, не меньше 0 (0 — самоколлизии нет)";
        return null;
    }

    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0;
}
