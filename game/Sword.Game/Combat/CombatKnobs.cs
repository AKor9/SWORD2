using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Ручки «Бой» (точка 6е; <c>CombatTuning</c> SWORD 2eab13b — вкладка «Бой» панели F3): поля правил боя <see cref="CombatRules"/> —
/// ресурса мира, собранного из секции конфига <c>combat</c>. Ручка — ключ секции (<c>combat.playerDamage</c>): её значение в мире —
/// ресурс, изменение на ходу — команда ручки (<see cref="TuningCommand"/>, система <c>tuning.combat</c> в Begin), значение по
/// умолчанию — значение в коде (<see cref="CombatSettings"/>). Урон и острота — диапазоны и шкала SWORD (0,2…5 и 0,1…3,
/// логарифмические); нападают ли враги, неуязвим ли игрок и рывок — новые (решение пользователя «Решено при разбивке этапа 6»).
/// Диапазоны — внутри проверок конфига (<see cref="CombatRules.From"/>): время рывка больше 0, остальное не меньше 0.
/// </summary>
public static class CombatKnobs
{
    public const string Section = "combat";

    /// <summary>Полные имена ручек (ключи конфига).</summary>
    public const string PlayerDamage = "combat.playerDamage", PlayerSharpness = "combat.playerSharpness",
        EnemiesAttack = "combat.enemiesAttack", PlayerInvulnerable = "combat.playerInvulnerable", DashSpeed = "combat.dashSpeed",
        DashTime = "combat.dashTime", DashCooldown = "combat.dashCooldown", DashInvulnerable = "combat.dashInvulnerable";

    public static void Register(SimulationBuilder b)
    {
        var d = new CombatSettings();
        b.Tuning<CombatRules>(Section)
            .Float("playerDamage", "урон", (in CombatRules r) => r.PlayerDamage, (ref CombatRules r, float v) => r.PlayerDamage = v,
                0.2f, 5, d.PlayerDamage, log: true, format: "0.00",
                hint: "множитель урона; удар анимацией ≈ (8 + скорость кончика × 1,4) × урон, укол ≈ (12 + скорость × 1,2) × урон, скорость до 16 м/с")
            .Float("playerSharpness", "острота", (in CombatRules r) => r.PlayerSharpness, (ref CombatRules r, float v) => r.PlayerSharpness = v,
                0.1f, 3, d.PlayerSharpness, log: true, format: "0.00",
                hint: "порог отрубания части делится на остроту: меньше 1 — рубит реже, урон тот же")
            .Bool("enemiesAttack", "враги нападают", (in CombatRules r) => r.EnemiesAttack, (ref CombatRules r, bool v) => r.EnemiesAttack = v,
                d.EnemiesAttack, hint: "враги бьют по таймеру ИИ; выключено — мирные враги (начатый удар доигрывается)")
            .Bool("playerInvulnerable", "игрок неуязвим", (in CombatRules r) => r.PlayerInvulnerable,
                (ref CombatRules r, bool v) => r.PlayerInvulnerable = v, d.PlayerInvulnerable,
                hint: "касания клинков врагов есть, попаданий в игрока нет")
            .Float("dashSpeed", "скорость рывка", (in CombatRules r) => r.DashSpeed, (ref CombatRules r, float v) => r.DashSpeed = v,
                0, 20, d.DashSpeed, format: "0.0", hint: "пиковая прибавка скорости рывка (Пробел), м/с; гаснет за время рывка")
            .Float("dashTime", "время рывка", (in CombatRules r) => r.DashTime, (ref CombatRules r, float v) => r.DashTime = v,
                0.05f, 1, d.DashTime, format: "0.00", hint: "за сколько секунд гаснет прибавка рывка")
            .Float("dashCooldown", "перезарядка рывка", (in CombatRules r) => r.DashCooldown, (ref CombatRules r, float v) => r.DashCooldown = v,
                0, 3, d.DashCooldown, format: "0.00", hint: "рывок — не чаще, чем раз в столько секунд")
            .Float("dashInvulnerable", "неуязвимость рывка", (in CombatRules r) => r.DashInvulnerable,
                (ref CombatRules r, float v) => r.DashInvulnerable = v, 0, 1, d.DashInvulnerable, format: "0.00",
                hint: "сколько секунд с начала рывка удары врагов не попадают");
        b.System(GameStages.Intent, new CombatRulesChangeSystem());
    }
}

/// <summary>
/// Intent, до <c>core.player-command</c> (6е), реактивная по <c>ResourceChanged&lt;CombatRules&gt;</c>: ручки «Бой» сменили правила боя
/// (ресурс заменила система ручек в Begin этого тика) — таймеры управления игрока, отмеренные по прежним правилам, прижимаются к
/// новым: перезарядка рывка не длиннее новой перезарядки, неуязвимость — не длиннее новой неуязвимости рывка (иначе остаток
/// прежнего таймера жил бы дольше, чем разрешают правила, — инвариант <c>combat.control</c>). Идущий рывок доигрывается по новым
/// правилам (прибавка — по новой скорости и времени). Без смены правил не стоит ничего.
/// </summary>
public sealed class CombatRulesChangeSystem : IReactiveSystem<ResourceChanged<CombatRules>>
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.CombatRules).Before(GameLabels.PlayerCommand).Writes<PlayerControl>();
        _query = s.Query().Write<PlayerControl>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<ResourceChanged<CombatRules>> changes)
    {
        var rules = changes[^1].Value;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var control = chunk.Write<PlayerControl>();
            for (int i = 0; i < control.Length; i++)
            {
                control[i].DashCooldown = MathF.Min(control[i].DashCooldown, rules.DashCooldown);
                control[i].Invulnerable = MathF.Min(control[i].Invulnerable, rules.DashInvulnerable);
            }
        }
    }
}
