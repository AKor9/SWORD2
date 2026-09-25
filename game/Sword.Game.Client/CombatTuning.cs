using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Вкладка «Бой» панели ручек F3 (<c>CombatTuning</c> SWORD 2eab13b): ручки симуляции секции <c>combat</c> (<see cref="CombatKnobs"/>)
/// через привязку клиента (<see cref="TuningBinding"/>: значение — ожидающее или мира, изменение — команда ручки на ближайший тик) и
/// строки состояния SWORD — последний удар игрока (урон, порог отруба — «отрубил бы» или «нет»), максимум удара, пороги отруба по
/// частям (с остротой; тексты короче SWORD — вмещаются в строку панели). Ключи группы — ключи секции (<c>playerDamage</c>): «сохранить» и
/// «загрузить» пишут и читают секцию
/// <c>combat</c> файла <c>sword.user.json</c>. Отличия от SWORD: ручки «враги нападают», «игрок неуязвим» и рывка — новые (решение
/// пользователя); «отрубил бы» — отруба нет: Cutting отложен, решение 17 (заявка <c>SeverRequested</c> без ответа). Строки состояния пишут текст на
/// стек — кадр панели без выделений.
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
        g.Info("последний удар", d => LastStrike(d, link.World.Resource<ArenaStats>()),
            "урон последнего удара игрока и порог отруба той части (÷ острота): больше порога — отрубил бы (отруба пока нет — отложен)");
        g.Info("максимум удара", d => MaxStrike(d, tuning.Value(CombatKnobs.PlayerDamage)));
        g.Info("пороги отруба", d => SeverThresholds(d, tuning.Value(CombatKnobs.PlayerSharpness)),
            "порог части ÷ острота: голова, предплечье, плечо, голень, бедро, торс");
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

    /// <summary>
    /// Урон последнего удара игрока против порога отруба части: «61 &gt; 27 — отрубил бы», «20 ≤ 27 — нет» (SWORD: «урон N, порог отруба M —
    /// отрубил / не отрубил»; короче — строка панели шириной 340 не вмещала); удара не было — «—».
    /// </summary>
    public static int LastStrike(Span<char> d, in ArenaStats stats) =>
        stats.LastPart < 0 ? Length(new Line(d).S("—").Text)
            : stats.LastDamage > stats.LastSeverAt
                ? Length(new Line(d).F(stats.LastDamage, "F0").S(" > ").F(stats.LastSeverAt, "F0").S(" — отрубил бы").Text)
                : Length(new Line(d).F(stats.LastDamage, "F0").S(" ≤ ").F(stats.LastSeverAt, "F0").S(" — нет").Text);

    /// <summary>«рубящий 61, укол 62» — удар клинком быстрее 16 м/с с уроном игрока (<see cref="DamageRules.StrikeDamage"/>).</summary>
    public static int MaxStrike(Span<char> d, float playerDamage) =>
        Length(new Line(d).S("рубящий ").F(DamageRules.StrikeDamage(false, DamageRules.SpeedCap, true, playerDamage), "F0")
            .S(", укол ").F(DamageRules.StrikeDamage(true, DamageRules.SpeedCap, true, playerDamage), "F0").Text);

    /// <summary>
    /// Пороги отруба частей ударом игрока (порог ÷ max(острота, 0,05)) — голова, предплечье, плечо, голень, бедро, торс (порядок SWORD;
    /// названия — в подсказке строки): «27, 18, 25, 26, 37, 75».
    /// </summary>
    public static int SeverThresholds(Span<char> d, float sharpness)
    {
        float k = MathF.Max(sharpness, DamageRules.MinSharpness);
        return Length(new Line(d).F(DamageRules.SeverThreshold(BodySegs.Head) / k, "F0").S(", ")
            .F(DamageRules.SeverThreshold(BodySegs.FArmR) / k, "F0").S(", ").F(DamageRules.SeverThreshold(BodySegs.UArmR) / k, "F0")
            .S(", ").F(DamageRules.SeverThreshold(BodySegs.ShinR) / k, "F0").S(", ").F(DamageRules.SeverThreshold(BodySegs.ThighR) / k, "F0")
            .S(", ").F(DamageRules.SeverThreshold(BodySegs.Torso) / k, "F0").Text);
    }

    /// <summary>Строка уже на месте (<see cref="Line"/> пишет в тот же буфер) — её длина.</summary>
    private static int Length(ReadOnlySpan<char> text) => text.Length;
}
