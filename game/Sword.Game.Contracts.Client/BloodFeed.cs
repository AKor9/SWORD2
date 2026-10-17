using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Попадание тика для крови: засчитанное попадание (<see cref="Hit"/>) с геометрией клинка его касания (<see cref="StrikeContact"/> с тем же
/// <see cref="Hit.Contact"/>). <see cref="Fighter"/> — жертва, <see cref="Attacker"/> — кто бил (ключи <see cref="FighterKey"/>);
/// <see cref="Segment"/> — часть тела (<see cref="BodySegs"/>); <see cref="Point"/> — точка на поверхности капсулы сегмента, мир, в позе
/// своего тика (якорь раны — в осях тела сегмента того же тика, <see cref="IFighterPoses"/>); <see cref="Dir"/> — ход кончика клинка,
/// <see cref="Blade"/> — ось клинка (единичные; у пробы удара — как в её касании); <see cref="Speed"/> — скорость кончика, м/с;
/// <see cref="ContactIndex"/> — номер касания в тике (детерминированный порядок) — для зерна ГСЧ вместе с <see cref="Tick"/>.
/// Касания без пары (не бывает) — <see cref="Blade"/> нулевой и <see cref="Speed"/> 0.
/// </summary>
public readonly record struct BloodHit(ulong Tick, ulong Fighter, ulong Attacker, byte Segment, Vector3 Point, Vector3 Dir, Vector3 Blade,
    float Speed, float Damage, HitKind Kind, bool Killed, bool Probe, bool Corpse, uint ContactIndex);

/// <summary>
/// События боя последнего сыгранного тика для крови (сервис клиента): в наблюдателе тиков (<c>AfterTick(t)</c>) — ровно тика <c>t</c>, при
/// любом числе тиков за кадр; так каждое попадание видно ровно один раз. Вне наблюдателя — последнего тика кадра. Только чтение: симуляции,
/// хеша и реплея не касается. Списки действуют до следующего тика; без выделений после прогрева.
/// </summary>
public interface IBloodFeed
{
    /// <summary>Тик, чьи события отдаются (последний сыгранный; до первого тика — 0 и списки пусты).</summary>
    ulong Tick { get; }

    /// <summary>Попадания тика по порядку разрешения.</summary>
    ReadOnlySpan<BloodHit> Hits { get; }

    /// <summary>Бойцы, умершие в тике (метка смерти добавлена) — от удара, пробы, кровопотери или команды.</summary>
    ReadOnlySpan<ulong> Deaths { get; }

    /// <summary>
    /// Бойцы, построенные в тике: новый, возрождённый, поставленный заново сбросом R — всё, что модуль вёл по этому бойцу, начинается заново
    /// (ключ у возрождённого прежний).
    /// </summary>
    ReadOnlySpan<ulong> Built { get; }

    /// <summary>Бойцы, снятые в тике: удалённые и снятые перед постройкой заново того же тика (тогда они есть и в <see cref="Built"/>).</summary>
    ReadOnlySpan<ulong> Removed { get; }

    /// <summary>
    /// Арена сброшена в тике: игрок построен заново на прежней сущности (сброс R; так же — постройка игрока командой сцены инструментов
    /// и пересборка «взорвавшегося» трупа игрока). Всё, что живёт «до R» (засохшее на полу), — с нуля.
    /// </summary>
    bool ArenaReset { get; }

    /// <summary>Кровотечение бойца сейчас (<see cref="Health.Bleed"/>, очки жизни в секунду) и мёртв ли он; бойца нет — false.</summary>
    bool TryBleed(ulong fighter, out float bleed, out bool dead);
}
