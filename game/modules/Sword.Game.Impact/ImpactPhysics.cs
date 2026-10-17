using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Impact;

/// <summary>
/// Импульс удара и баланс тела. Тело — одно твёрдое тело массы <see cref="ImpactRules.BodyMass"/> с инерцией
/// <see cref="ImpactRules.BodyInertia"/> вокруг центра масс: оружие встречает в точке приведённую массу
/// <c>μ = m_w · m / (m_w + m)</c>, <c>1/m = 1/M + |r × n|² / I</c>; импульс <c>J = μ · v · (1 + e) · k</c> (k — доля клинка: рез гасит
/// удар; тупое — 1). Скорость центра масс <c>Δv = J / M</c>; точка захвата (куда надо шагнуть, чтобы устоять) уходит на
/// <c>Δv / ω0</c>, <c>ω0 = √(g / h)</c>.
/// </summary>
public static class ImpactPhysics
{
    public const float Gravity = 9.81f;

    /// <summary>Приведённая масса оружия <paramref name="weapon"/> и тела в точке <paramref name="r"/> (от центра масс) вдоль <paramref name="n"/>, кг.</summary>
    public static float Reduced(float weapon, Vector3 r, Vector3 n, in ImpactRules rules)
    {
        var rn = Vector3.Cross(r, n);
        float inverseBody = 1 / rules.BodyMass + rn.LengthSquared() / rules.BodyInertia;
        return weapon / (1 + weapon * inverseBody);
    }

    /// <summary>
    /// Импульс удара телу, Н·с: скорость точки оружия <paramref name="velocity"/>, точка <paramref name="r"/> от центра масс; масса оружия
    /// и доля — по виду удара.
    /// </summary>
    public static Vector3 Impulse(Vector3 velocity, Vector3 r, bool blunt, in ImpactRules rules, out float weaponMomentum)
    {
        float speed = velocity.Length();
        float weapon = blunt ? rules.BluntMass : rules.BladeMass;
        weaponMomentum = weapon * speed;
        if (!(speed > 1e-4f)) return Vector3.Zero;
        var n = velocity / speed;
        float j = Reduced(weapon, r, n, rules) * speed * (1 + rules.Restitution) * (blunt ? 1 : rules.BladeShare);
        return n * j;
    }

    /// <summary>Сдвиг точки захвата от импульса <paramref name="impulse"/>, м: по земле, <c>|J_гориз| / M / ω0</c>.</summary>
    public static float CaptureShift(Vector3 impulse, in ImpactRules rules)
    {
        float omega = MathF.Sqrt(Gravity / rules.ComHeight);
        return new Vector2(impulse.X, impulse.Z).Length() / rules.BodyMass / omega;
    }

    /// <summary>Накопленный сдвиг баланса через <paramref name="elapsed"/> с: гаснет по экспоненте со временем восстановления.</summary>
    public static float Decay(float shift, float elapsed, in ImpactRules rules) =>
        elapsed > 0 ? shift * MathF.Exp(-elapsed / rules.Recovery) : shift;

    /// <summary>
    /// Отклик по накопленному сдвигу <paramref name="shift"/>, м: меньше порога пошатывания — вздрагивание с силой по доле порога;
    /// меньше порога падения — пошатывание; больше — падение. Игрок только вздрагивает (управление не отнимается).
    /// </summary>
    public static HitLevel Level(float shift, bool player, in ImpactRules rules, out float strength)
    {
        strength = Math.Clamp(shift / rules.StaggerShift, rules.MinFlinch, 1);
        if (player || shift < rules.StaggerShift) return HitLevel.Flinch;
        return shift < rules.FallShift ? HitLevel.Stagger : HitLevel.Knockdown;
    }

    /// <summary>Удар по ногам выбивает опору: сдвиг баланса — с множителем <see cref="ImpactRules.LegFactor"/>.</summary>
    public static bool IsLeg(int part) =>
        part is BodySegs.ThighL or BodySegs.ShinL or BodySegs.FootL or BodySegs.ThighR or BodySegs.ShinR or BodySegs.FootR;
}
