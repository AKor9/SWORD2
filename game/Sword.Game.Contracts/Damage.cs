using System.Numerics;
using System.Runtime.CompilerServices;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Раны частей тела бойца по номеру сегмента (<see cref="BodySegs"/>, 0…13): 0 — цела, до 1,5.</summary>
[InlineArray(BodySegs.Count)]
public struct Wounds
{
    private float _element;
}

/// <summary>
/// Здоровье бойца: очки жизни (100 при постройке), кровотечение, очки/с (растёт с каждым попаданием, гаснет на 0,2/с),
/// раны частей. Реактивный: меняется только командой <c>Set</c> (попадание в Resolve, кровопотеря в Aftermath), поэтому
/// у каждого изменения есть причина (<c>Changed&lt;Health&gt;</c>).
/// </summary>
[Reactive]
public struct Health
{
    /// <summary>Очков жизни у построенного бойца (<c>Fighter.Build</c>: <c>Hp = 100</c>).</summary>
    public const float Full = 100;

    public float Hp;

    /// <summary>Кровотечение, очков жизни в секунду.</summary>
    public float Bleed;

    public Wounds Wounds;

    /// <summary>Полное здоровье: 100 очков, без кровотечения и ран.</summary>
    public static Health Fresh => new() { Hp = Full };
}

/// <summary>
/// Боец мёртв: время смерти, с (<c>SimTime.Time</c>), и причина. До трупа на физике мёртвый замирает (системы его
/// пропускают), капсулы хранят скорости последнего тика жизни для рэгдолла. В тик смерти снимается привязка скина.
/// Трупы — тоже цели ударов (не умирают снова). Враг возрождается через 8 с, игрок — по сбросу арены (R).
/// </summary>
public struct Dead
{
    public float Time;

    /// <summary>Отчего умер: удар, проба, кровопотеря, команда сцены. Меч выпадает при любой.</summary>
    public DeathCause Cause;
}

/// <summary>Причина смерти.</summary>
public enum DeathCause : byte
{
    /// <summary>Попадание клинка (<see cref="Hit"/> не пробы): очки жизни, рана торса, смертельный укол.</summary>
    Hit = 0,

    /// <summary>Проба удара командой (<see cref="StrikeProbe"/>).</summary>
    Probe = 1,

    /// <summary>Кровопотеря (стадия Aftermath).</summary>
    Bleed = 2,

    /// <summary>Команда сцены «убить без удара» (<see cref="SceneAction.Kill"/>).</summary>
    Command = 3,

    /// <summary>Отделение ноги, головы или торса (<see cref="LimbDetached"/>).</summary>
    Severed = 4,

    /// <summary>Взрыв (<see cref="BlastCommand"/>).</summary>
    Blast = 5,
}

/// <summary>Вид попадания: рубящий, укол, тупой (тупое оружие — <c>combat.bluntWeapons</c>), взрыв (<see cref="BlastCommand"/>).</summary>
public enum HitKind : byte
{
    Slash = 0,
    Thrust = 1,
    Blunt = 2,
    Blast = 3,
}

/// <summary>Идентификатор касания клинка: тик и порядковый номер в детерминированном порядке тика.</summary>
public readonly record struct ContactId(ulong Tick, int Index);

/// <summary>Идентификатор заявки (<see cref="SeverRequested"/>): тик и порядковый номер в тике.</summary>
public readonly record struct RequestId(ulong Tick, int Index);

/// <summary>
/// Касание клинка (событие Combat): клинок бойца в ударе прошёл сквозь капсулу другого бойца — раз за проход и не чаще
/// 0,1 с на пару; пары только игрок ↔ враг, жертва может быть трупом. Урон уже посчитан по скорости кончика; применит его
/// (или нет) Resolve. Проба удара (<see cref="StrikeProbe"/>) даёт такое же касание с <see cref="Probe"/>.
/// </summary>
public struct StrikeContact
{
    public ContactId Id;
    public Entity Attacker, Victim;

    /// <summary>Сегмент тела жертвы (<see cref="BodySegs"/>): ближайшая капсула первого положения клинка с касанием.</summary>
    public int Part;

    /// <summary>Точка на поверхности капсулы, мир.</summary>
    public Vector3 Point;

    /// <summary>Направление движения кончика клинка за тик (нет движения — ось клинка), единичное.</summary>
    public Vector3 Dir;

    /// <summary>Ось клинка — от основания к кончику, единичная.</summary>
    public Vector3 Blade;

    /// <summary>Скорость кончика клинка за тик, м/с (в урон — не больше 16).</summary>
    public float Speed;

    public HitKind Kind;

    /// <summary>Урон: (укол ? 12 : 8) + min(скорость, 16)·(укол ? 1,2 : 1,4), у игрока × <c>combat.playerDamage</c>.</summary>
    public float Damage;

    /// <summary>Номер удара атакующего (<c>AnimatorAction.AttackSerial</c>) — проход клинка; у пробы — −1.</summary>
    public int AttackSerial;

    /// <summary>Касание дала проба удара командой (<see cref="StrikeProbe"/>), а не клинок.</summary>
    public bool Probe;
}

/// <summary>
/// Засчитанное попадание (событие Resolve). Касание без попадания — неуязвимая жертва или убитая прошлым касанием этого
/// тика; жертва, мёртвая до тика, — попадание по трупу (<see cref="Corpse"/>).
/// </summary>
public struct Hit
{
    public ContactId Contact;
    public Entity Attacker, Victim;
    public int Part;
    public float Damage;
    public Vector3 Point, Dir;
    public HitKind Kind;

    /// <summary>Попадание убило жертву (очки жизни кончились, рана торса 1,4 или смертельный укол).</summary>
    public bool Killed;

    /// <summary>Смертельный укол: укол остриём вперёд (кончик шёл вдоль клинка — доля больше 0,6) в голову или торс, урон больше 20.</summary>
    public bool LethalThrust;

    public bool Probe;

    /// <summary>
    /// Попадание по трупу: урон и раны идут в здоровье, смерти и реакции нет, «убито» не растёт
    /// (<see cref="ArenaStats.CorpseHits"/>).
    /// </summary>
    public bool Corpse;
}

/// <summary>
/// Заявка на разрушение части (событие Resolve): каждое засчитанное попадание по плоти. Решает модуль Cutting — по энергии удара
/// против сечения части: отруб (<see cref="LimbDetached"/>), неполный разрез (<see cref="PartialCut"/>) или ничего; без модуля
/// конечность на месте.
/// </summary>
public struct SeverRequested
{
    public RequestId Id;
    public ContactId Contact;

    /// <summary>Атакующий (у взрыва — нет).</summary>
    public Entity Attacker;
    public Entity Victim;
    public int Part;
    public Vector3 Point, Blade, Dir;
    public HitKind Kind;

    /// <summary>Скорость точки клинка в месте касания, м/с (мир); у пробы — по урону вдоль направления.</summary>
    public Vector3 Velocity;

    /// <summary>Острота удара: множитель энергии разреза (<c>combat.playerSharpness</c> у игрока, 1 у врага).</summary>
    public float Sharpness;

    /// <summary>Жертва — труп (мертва до тика).</summary>
    public bool Corpse;

    /// <summary>Взрыв (<see cref="HitKind.Blast"/>): энергия, дошедшая до части, Дж; <see cref="Velocity"/> — скорость, которую взрыв даёт части.</summary>
    public float Energy;
}

/// <summary>
/// Проба удара командой (инструменты, тесты): синтетическое касание по местам (0 — игрок, 1…32 — враги) тем же путём
/// Resolve, что касание клинка. Точка — центр капсулы части, направление — от атакующего к жертве по земле.
/// Место без бойца, одно место за обоих, часть без капсулы, нечисловой или отрицательный урон — ничего.
/// </summary>
public struct StrikeProbe
{
    public int Attacker, Victim;
    public int Part;
    public float Damage;
    public HitKind Kind;
}

/// <summary>
/// Статистика арены — ресурс-значение (хешируется): убито врагов (любые смерти, и от кровопотери), попаданий по живым
/// (по трупам — <see cref="CorpseHits"/>), последний удар игрока. Сброс арены (R) обнуляет всё.
/// </summary>
public struct ArenaStats
{
    public int Kills;
    public int Hits;

    /// <summary>Последнее попадание игрока: сегмент (−1 — не было) и урон.</summary>
    public int LastPart;
    public float LastDamage;

    /// <summary>Попаданий по трупам (<see cref="Hit.Corpse"/>): в <see cref="Hits"/> не входят, «убито» от них не растёт.</summary>
    public int CorpseHits;

    public static ArenaStats Start => new() { LastPart = -1 };
}
