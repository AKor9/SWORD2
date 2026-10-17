using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Правила урона — чистые функции: константы и порядок вычислений float — часть поведения.
/// </summary>
public static class DamageRules
{
    /// <summary>Клинок медленнее, м/с, — вне прохода: замах ещё не начался или удар уже кончился.</summary>
    public const float MinStrikeSpeed = 3.5f;

    /// <summary>Кончик дальше радиуса жертвы плюс столько, м, — проход не проверяется.</summary>
    public const float BoundMargin = 1.5f;

    /// <summary>Положений клинка между тиками — шагов развёртки (3 шага — 4 положения).</summary>
    public const int SweepSteps = 3;

    /// <summary>Касание — ближе радиуса капсулы плюс столько, м.</summary>
    public const float SweepPad = 0.02f;

    /// <summary>Попадания одного бойца по другому не чаще, с.</summary>
    public const float HitCooldown = 0.1f;

    /// <summary>Урон меньше этого не засчитывается, и такой удар прохода не тратит.</summary>
    public const float MinDamage = 4f;

    /// <summary>Скорость кончика в урон — не больше, м/с (кончик в клипах разгоняется до 30–50 м/с за кадр).</summary>
    public const float SpeedCap = 16f;

    /// <summary>Урон ударов игрока по умолчанию — множитель.</summary>
    public const float PlayerDamage = 2f;

    /// <summary>Смертельный укол: урон больше этого (<c>ThrustKill</c>) остриём вперёд — доля движения кончика вдоль клинка больше <see cref="ThrustAlong"/>.</summary>
    public const float ThrustKill = 20f, ThrustAlong = 0.6f;

    /// <summary>Рана части: + урон / 45, не больше 1,5; рана торса 1,4 — смерть.</summary>
    public const float WoundScale = 45f, WoundMax = 1.5f, TorsoWoundDeath = 1.4f;

    /// <summary>Кровотечение: + урон·0,02 очков в секунду с попадания, гаснет на 0,2 в секунду (<c>AfterTick</c>).</summary>
    public const float BleedPerDamage = 0.02f, BleedDecay = 0.2f;

    /// <summary>Острота удара игрока — не меньше 0,05 (множитель энергии разреза, <see cref="SeverRequested.Sharpness"/>).</summary>
    public const float MinSharpness = 0.05f;

    /// <summary>Враг возрождается, когда мёртв дольше, с (<c>Time − DeadTime &gt; 8</c>).</summary>
    public const float RespawnDelay = 8f;

    private static readonly float[] HpMulTable = [0.9f, 1f, 1.6f, .4f, .4f, .4f, .4f, .5f, .5f, .5f, .5f, 0, .3f, .3f];

    /// <summary>Множитель урона в очки жизни по сегменту: голова — 1,6, торс — 1, конечности — 0,3…0,5.</summary>
    public static ReadOnlySpan<float> HpMul => HpMulTable;

    /// <summary>
    /// Урон касания клинка: <c>((укол ? 12 : 8) + min(скорость, 16)·(укол ? 1,2 : 1,4))·(игрок ? урон игрока : 1)</c>.
    /// </summary>
    public static float StrikeDamage(bool thrust, float speed, bool player, float playerDamage)
    {
        float s = MathF.Min(speed, SpeedCap);
        return ((thrust ? 12f : 8f) + s * (thrust ? 1.2f : 1.4f)) * (player ? playerDamage : 1);
    }

    /// <summary>
    /// Смертельный укол: укол в голову или торс с уроном больше 20 — только остриём вперёд (<c>dot(dir, blade)</c> больше 0,6), а не
    /// замахом или отводом клинка.
    /// </summary>
    public static bool LethalThrust(HitKind kind, int segment, float damage, Vector3 dir, Vector3 blade) =>
        kind == HitKind.Thrust && segment is BodySegs.Head or BodySegs.Torso && damage > ThrustKill && Vector3.Dot(dir, blade) > ThrustAlong;

    /// <summary>
    /// Попадание в часть <paramref name="segment"/>: очки жизни — урон × множитель части, рана части — + урон/45 (не больше 1,5),
    /// кровотечение — + урон·0,02.
    /// </summary>
    public static void Apply(ref Health h, int segment, float damage)
    {
        h.Hp -= damage * HpMulTable[segment];
        h.Wounds[segment] = MathF.Min(WoundMax, h.Wounds[segment] + damage / WoundScale);
        h.Bleed += damage * BleedPerDamage;
    }

    /// <summary>Попадание убило: очков жизни не осталось или рана торса дошла до 1,4 (<c>ApplyHit</c>), или смертельный укол.</summary>
    public static bool Killed(in Health h, bool lethalThrust) => lethalThrust || h.Hp <= 0 || h.Wounds[BodySegs.Torso] >= TorsoWoundDeath;

    /// <summary>Кровопотеря живого за тик: очки жизни — кровотечение·dt, кровотечение гаснет на 0,2·dt (не ниже 0).</summary>
    public static void Bleed(ref Health h, float dt)
    {
        h.Hp -= h.Bleed * dt;
        h.Bleed = MathF.Max(0, h.Bleed - BleedDecay * dt);
    }
}
