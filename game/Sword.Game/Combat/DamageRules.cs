using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Правила урона (этап 6г) — <c>DuelArena.KinematicStrikes</c>, <c>ApplyHits</c>, <c>ApplyHit</c>, <c>Fighter</c> (<c>HpMul</c>,
/// <c>SeverAt</c>, <c>SeverThreshold</c>, <c>AfterTick</c>) SWORD 2eab13b в отзывчивом режиме (<c>Fighter.Responsive</c>, по умолчанию
/// включён). Чистые функции: константы, порядок вычислений float — SWORD.
/// </summary>
public static class DamageRules
{
    /// <summary>Клинок медленнее, м/с, — вне прохода: замах ещё не начался или удар уже кончился (<c>MinStrikeSpeed</c>).</summary>
    public const float MinStrikeSpeed = 3.5f;

    /// <summary>Кончик дальше радиуса жертвы плюс столько, м, — проход не проверяется (<c>vic.BoundR + 1,5</c>).</summary>
    public const float BoundMargin = 1.5f;

    /// <summary>Положений клинка между тиками — шагов развёртки (<c>SweepBlade</c>: 3 шага — 4 положения).</summary>
    public const int SweepSteps = 3;

    /// <summary>Касание — ближе радиуса капсулы плюс столько, м (<c>body.Radius + 0,02</c>).</summary>
    public const float SweepPad = 0.02f;

    /// <summary>Попадания одного бойца по другому не чаще, с (<c>HitCooldown</c>).</summary>
    public const float HitCooldown = 0.1f;

    /// <summary>Урон меньше этого не засчитывается (<c>ev.Damage &lt; 4</c> в <c>ApplyHits</c>); в SWORD2 такой удар и прохода не тратит.</summary>
    public const float MinDamage = 4f;

    /// <summary>Скорость кончика в урон — не больше, м/с (кончик в клипах разгоняется до 30–50 м/с за кадр).</summary>
    public const float SpeedCap = 16f;

    /// <summary>Урон ударов игрока по умолчанию — множитель (<c>SWORD_PLAYER_CUT</c> = 2).</summary>
    public const float PlayerDamage = 2f;

    /// <summary>Смертельный укол: урон больше этого (<c>ThrustKill</c>) остриём вперёд — доля движения кончика вдоль клинка больше <see cref="ThrustAlong"/>.</summary>
    public const float ThrustKill = 20f, ThrustAlong = 0.6f;

    /// <summary>Рана части: + урон / 45, не больше 1,5; рана торса 1,4 — смерть.</summary>
    public const float WoundScale = 45f, WoundMax = 1.5f, TorsoWoundDeath = 1.4f;

    /// <summary>Кровотечение: + урон·0,02 очков в секунду с попадания, гаснет на 0,2 в секунду (<c>AfterTick</c>).</summary>
    public const float BleedPerDamage = 0.02f, BleedDecay = 0.2f;

    /// <summary>Порог отруба: торс — 75, прочее — <c>SeverAt</c>·0,6; острота игрока — не меньше 0,05.</summary>
    public const float TorsoSever = 75f, SeverScale = 0.6f, MinSharpness = 0.05f;

    /// <summary>Враг возрождается, когда мёртв дольше, с (<c>Time − DeadTime &gt; 8</c>).</summary>
    public const float RespawnDelay = 8f;

    private static readonly float[] HpMulTable = [0.9f, 1f, 1.6f, .4f, .4f, .4f, .4f, .5f, .5f, .5f, .5f, 0, .3f, .3f];
    private static readonly float[] SeverAtTable = [1e9f, 1e9f, 45, 42, 30, 42, 30, 62, 44, 62, 44, 1e9f, 30, 30];

    /// <summary>Множитель урона в очки жизни по сегменту (<c>Fighter.HpMul</c>): голова — 1,6, торс — 1, конечности — 0,3…0,5.</summary>
    public static ReadOnlySpan<float> HpMul => HpMulTable;

    /// <summary>Порог отруба части по сегменту без отзывчивого режима (<c>Fighter.SeverAt</c>): таз, торс и клинок не отрубаются.</summary>
    public static ReadOnlySpan<float> SeverAt => SeverAtTable;

    /// <summary>
    /// Урон касания клинка (<c>KinematicStrikes</c>): <c>((укол ? 12 : 8) + min(скорость, 16)·(укол ? 1,2 : 1,4))·(игрок ? урон игрока : 1)</c>.
    /// </summary>
    public static float StrikeDamage(bool thrust, float speed, bool player, float playerDamage)
    {
        float s = MathF.Min(speed, SpeedCap);
        return ((thrust ? 12f : 8f) + s * (thrust ? 1.2f : 1.4f)) * (player ? playerDamage : 1);
    }

    /// <summary>Порог отруба части <paramref name="segment"/> (<c>Fighter.SeverThreshold</c>, отзывчивый режим): торс — 75, прочее — <c>SeverAt</c>·0,6.</summary>
    public static float SeverThreshold(int segment) => segment == BodySegs.Torso ? TorsoSever : SeverAtTable[segment] * SeverScale;

    /// <summary>Порог отруба для удара: удара игрока — ÷ max(острота, 0,05) (<c>ApplyHit</c>).</summary>
    public static float SeverThreshold(int segment, bool player, float sharpness) =>
        SeverThreshold(segment) / (player ? MathF.Max(sharpness, MinSharpness) : 1);

    /// <summary>
    /// Смертельный укол (SWORD2): укол в голову или торс с уроном больше 20 — только остриём вперёд: доля движения кончика вдоль
    /// оси клинка (<c>dot(dir, blade)</c>, как <c>thrust</c> физического пути <c>RegisterHit</c> SWORD) больше 0,6. В SWORD убивал весь
    /// клип укола — и замах, и отвод клинка.
    /// </summary>
    public static bool LethalThrust(HitKind kind, int segment, float damage, Vector3 dir, Vector3 blade) =>
        kind == HitKind.Thrust && segment is BodySegs.Head or BodySegs.Torso && damage > ThrustKill && Vector3.Dot(dir, blade) > ThrustAlong;

    /// <summary>
    /// Попадание в часть <paramref name="segment"/> (<c>ApplyHit</c>): очки жизни — урон × множитель части, рана части — + урон/45 (не больше
    /// 1,5), кровотечение — + урон·0,02. Порядок SWORD.
    /// </summary>
    public static void Apply(ref Health h, int segment, float damage)
    {
        h.Hp -= damage * HpMulTable[segment];
        h.Wounds[segment] = MathF.Min(WoundMax, h.Wounds[segment] + damage / WoundScale);
        h.Bleed += damage * BleedPerDamage;
    }

    /// <summary>Попадание убило: очков жизни не осталось или рана торса дошла до 1,4 (<c>ApplyHit</c>), или смертельный укол.</summary>
    public static bool Killed(in Health h, bool lethalThrust) => lethalThrust || h.Hp <= 0 || h.Wounds[BodySegs.Torso] >= TorsoWoundDeath;

    /// <summary>Кровопотеря за тик (<c>Fighter.AfterTick</c> живого): очки жизни — кровотечение·dt, кровотечение гаснет на 0,2·dt (не ниже 0).</summary>
    public static void Bleed(ref Health h, float dt)
    {
        h.Hp -= h.Bleed * dt;
        h.Bleed = MathF.Max(0, h.Bleed - BleedDecay * dt);
    }
}
