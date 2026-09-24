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

    /// <summary>Что не так с настройками (null — годны): подшаги 1…64, проход — от 1 тика, числа — конечные и не меньше 0.</summary>
    public string? Problem()
    {
        if (Substeps is < 1 or > 64) return $"подшагов {Substeps}: нужно 1…64";
        if (PassExitTicks < 1) return $"тиков до закрытия прохода {PassExitTicks}: нужно от 1";
        if (!NonNegative(PopSpeed)) return "скорость выталкивания — конечная, не меньше 0";
        if (!NonNegative(StaticFriction)) return "трение покоя — конечное, не меньше 0";
        if (!NonNegative(ContactCompliance) || !NonNegative(JointLimitCompliance)) return "податливости — конечные, не меньше 0";
        if (!NonNegative(PushForce)) return "бюджет толчка — конечный, не меньше 0";
        if (!NonNegative(PassShare) || !NonNegative(HitDvShare)) return "доли прохода и Δv удара — конечные, не меньше 0";
        if (!NonNegative(KinematicPointSpeedMax)) return "потолок скорости кинематической точки — конечный, не меньше 0";
        return null;
    }

    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0;
}
