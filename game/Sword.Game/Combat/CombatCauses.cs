using Sword.Engine.Ecs;

namespace Sword.Game.Combat;

/// <summary>
/// Каналы побочной таблицы причинности боя (<see cref="CausalityLog.Link"/>; диагностический режим <c>state.why</c>) — родители
/// для непрерывных производителей, чьей причины нет в срабатывании: проход клинка берёт причину начала удара, кровопотеря — попадания,
/// возрождение — смерти. Только диагностика: в хеш и поведение не входит, вне режима не пишется.
/// </summary>
public static class CombatCauses
{
    /// <summary>Нажатие удара игрока (команда с <c>Attack</c> или <c>Stab</c>): удар выходит из буфера позже — родитель начала удара.</summary>
    public const int Swing = 1;

    /// <summary>Начало удара бойца; ключ — номер удара <c>AttackSerial</c>: касание клинка берёт причину своего удара.</summary>
    public const int Attack = 2;

    /// <summary>Последнее попадание в бойца (<c>Hit</c>): родитель кровопотери.</summary>
    public const int Bleed = 3;

    /// <summary>Смерть бойца (<c>Add&lt;Dead&gt;</c>): родитель возрождения.</summary>
    public const int Death = 4;

    /// <summary>Пояснения шагов в журнале причинности.</summary>
    public const string AttackStart = "начало удара", AiStrike = "ИИ: удар по таймеру", RespawnDue = "возрождение: мёртв дольше 8 с";

    /// <summary>
    /// Удар начался (номер удара вырос с <paramref name="serialBefore"/>): шаг «начало удара» с родителем <paramref name="parent"/>
    /// (команда игрока или врага, решение ИИ) — в таблицу связей по номеру удара.
    /// </summary>
    public static void Started(in SystemContext ctx, CausalityLog log, Entity fighter, int serialBefore, int serial, CauseId parent)
    {
        if (serial == serialBefore) return;
        var start = ctx.Commands.Note(AttackStart, fighter, parent, serial);
        log.Link(fighter, Attack, start, serial);
    }
}
