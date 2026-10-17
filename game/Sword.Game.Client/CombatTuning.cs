using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Вкладка «Бой» панели ручек F3: ручки секции <c>combat</c> (<see cref="CombatKnobs"/>) через привязку клиента
/// (<see cref="TuningBinding"/>) и строки состояния — последний удар игрока, максимум удара. Острота — множитель энергии
/// разреза удара игрока (решает модуль Cutting). «Сохранить» и «загрузить» — секция <c>combat</c> файла <c>sword.user.json</c>.
/// Строки пишут текст на стек — кадр панели без выделений.
/// </summary>
public static class CombatTuning
{
    public const string Id = "combat", Title = "Бой";

    /// <summary>Группа «Бой» над привязкой <paramref name="tuning"/>; статистика — из мира <paramref name="link"/>.</summary>
    public static TuningGroup Build(TuningBinding tuning, SimulationLink link)
    {
        var g = new TuningGroup(Id, Title);
        g.Header("УДАР ИГРОКА");
        tuning.Add(g, CombatKnobs.PlayerDamage);
        tuning.Add(g, CombatKnobs.PlayerSharpness);
        g.Info("последний удар", d => LastStrike(d, link.World.Resource<ArenaStats>()), "часть и урон последнего удара игрока");
        g.Info("максимум удара", d => MaxStrike(d, tuning.Value(CombatKnobs.PlayerDamage)));
        g.Header("ВРАГИ И ИГРОК");
        tuning.Add(g, CombatKnobs.EnemiesAttack);
        tuning.Add(g, CombatKnobs.PlayerInvulnerable);
        g.Header("РЫВОК");
        tuning.Add(g, CombatKnobs.DashSpeed);
        tuning.Add(g, CombatKnobs.DashTime);
        tuning.Add(g, CombatKnobs.DashCooldown);
        tuning.Add(g, CombatKnobs.DashInvulnerable);
        return g;
    }

    /// <summary>Часть и урон последнего удара игрока: «голова 61»; удара не было — «—».</summary>
    public static int LastStrike(Span<char> d, in ArenaStats stats) =>
        stats.LastPart < 0 ? Length(new Line(d).S("—").Text)
            : Length(new Line(d).S(BodySegs.Title(stats.LastPart)).S(" ").F(stats.LastDamage, "F0").Text);

    /// <summary>«рубящий 61, укол 62» — удар клинком быстрее 16 м/с с уроном игрока (<see cref="DamageRules.StrikeDamage"/>).</summary>
    public static int MaxStrike(Span<char> d, float playerDamage) =>
        Length(new Line(d).S("рубящий ").F(DamageRules.StrikeDamage(false, DamageRules.SpeedCap, true, playerDamage), "F0")
            .S(", укол ").F(DamageRules.StrikeDamage(true, DamageRules.SpeedCap, true, playerDamage), "F0").Text);

    /// <summary>Строка уже на месте (<see cref="Line"/> пишет в тот же буфер) — её длина.</summary>
    private static int Length(ReadOnlySpan<char> text) => text.Length;
}
