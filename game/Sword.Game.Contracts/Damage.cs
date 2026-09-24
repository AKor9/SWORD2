using System.Numerics;
using System.Runtime.CompilerServices;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Раны частей тела бойца по номеру сегмента (<see cref="BodySegs"/>, 0…13): 0 — цела, до 1,5 (<c>Body.Wound</c> SWORD).</summary>
[InlineArray(BodySegs.Count)]
public struct Wounds
{
    private float _element;
}

/// <summary>
/// Здоровье бойца (этап 6г; <c>Fighter.Hp</c>, <c>Fighter.Bleed</c>, <c>Body.Wound</c> SWORD 2eab13b): очки жизни (100 при постройке),
/// кровотечение, очки в секунду (растёт с каждым попаданием, гаснет на 0,2 в секунду), раны частей. Реактивный: меняется только
/// командой <c>Set</c> — попадание (стадия Resolve) и кровопотеря (Aftermath, только у кровоточащих), — поэтому у каждого изменения
/// есть причина (<c>Changed&lt;Health&gt;</c>, <c>state.why</c> — 6д). Постройка бойца — полное здоровье.
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
/// Боец мёртв (этап 6г; <c>Fighter.Dead</c>, <c>DeadTime</c> SWORD): время смерти, с (<c>SimTime.Time</c>), и причина (7ж). До трупа
/// на физике (7з) мёртвый замирает: системы ядра и модули его пропускают — поза, корень, капсулы и клинок стоят, капсулы хранят
/// скорости последнего тика жизни (их возьмёт рэгдолл). В тик смерти снимается привязка скина (7ж, <see cref="ISkinView"/>).
/// Трупы — не цели ударов. Враг возрождается через 8 с (постройка места заново снимает метку), игрок — по сбросу арены (R).
/// </summary>
public struct Dead
{
    public float Time;

    /// <summary>Отчего умер (7ж): удар, проба, кровопотеря, команда сцены. Меч выпадает при любой (решение 6 этапа 7; 7з).</summary>
    public DeathCause Cause;
}

/// <summary>Причина смерти (7ж; в SWORD — только путь в коде: <c>ApplyHit</c>, <c>AfterTick</c>, <c>Die</c> сцены).</summary>
public enum DeathCause : byte
{
    /// <summary>Попадание клинка (<see cref="Hit"/> не пробы): очки жизни, рана торса, смертельный укол.</summary>
    Hit = 0,

    /// <summary>Проба удара командой (<see cref="StrikeProbe"/>).</summary>
    Probe = 1,

    /// <summary>Кровопотеря (стадия Aftermath).</summary>
    Bleed = 2,

    /// <summary>Команда сцены «убить без удара» (<see cref="SceneAction.Kill"/>; <c>Die(t, null)</c> сцен SWORD).</summary>
    Command = 3,
}

/// <summary>
/// Вид попадания (<c>ArenaHitKind</c> SWORD): рубящий, укол, тупой (плашмя). Тупой в SWORD давал только физический клинок
/// (<c>RegisterHit</c>/<c>RegisterClash</c>); в SWORD2 физического пути удара нет (решено при разбивке этапа 7) — тупого не бывает.
/// </summary>
public enum HitKind : byte
{
    Slash = 0,
    Thrust = 1,
    Blunt = 2,
}

/// <summary>Идентификатор касания клинка (протокол боя, раздел 9 плана): тик и порядковый номер в детерминированном порядке тика.</summary>
public readonly record struct ContactId(ulong Tick, int Index);

/// <summary>Идентификатор заявки (<see cref="SeverRequested"/>): тик и порядковый номер в тике.</summary>
public readonly record struct RequestId(ulong Tick, int Index);

/// <summary>
/// Касание клинка (событие стадии Combat, протокол боя): клинок бойца в ударе прошёл сквозь капсулу тела другого бойца — один раз
/// за проход клинка сквозь тело и не чаще 0,1 с на пару (<c>KinematicStrikes</c> SWORD 2eab13b). Пары — только игрок ↔ враг, трупы
/// — не цели. Урон уже посчитан по скорости кончика; применит его (или нет — неуязвимость, труп) стадия Resolve. Проба удара
/// командой (<see cref="StrikeProbe"/>) даёт такое же касание с <see cref="Probe"/>.
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
/// Засчитанное попадание (событие стадии Resolve; <c>ArenaHit</c> SWORD): кто, кого, в какую часть, урон, точка, направление, вид,
/// убило ли оно, смертельный ли укол и порог отруба части для этого удара. Касание без попадания — неуязвимая жертва (рывок,
/// <c>combat.playerInvulnerable</c>) или жертва, уже мёртвая к этому касанию.
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

    /// <summary>Порог отруба части для этого удара (торс — 75, прочее — <c>SeverAt</c>·0,6; удар игрока — ÷ острота).</summary>
    public float SeverAt;

    public bool Probe;
}

/// <summary>
/// Заявка на отруб части (событие стадии Resolve; <c>LimbDetachRequest</c> SWORD): урон попадания больше порога отруба. Ответа нет до
/// этапа 11 (модуль Cutting ответит <c>LimbDetached</c>): без модуля конечность на месте (протокол боя, раздел 9 плана).
/// </summary>
public struct SeverRequested
{
    public RequestId Id;
    public ContactId Contact;
    public Entity Victim;
    public int Part;
    public Vector3 Point, Blade, Dir;
    public HitKind Kind;
}

/// <summary>
/// Проба удара командой (агент, сценарии фич, тесты; в SWORD нет): синтетическое касание — атакующий и жертва по местам
/// (<see cref="FighterSlot"/>: 0 — игрок, 1…32 — враги), часть (сегмент тела), урон и вид — тем же путём Resolve, что касание
/// клинка (неуязвимость, труп, смерть, отруб, реакция, статистика). Точка — центр капсулы части, направление и ось — от корня
/// атакующего к жертве по земле. Место без бойца, одно место за обоих, часть без капсулы, нечисловой или отрицательный урон — ничего.
/// </summary>
public struct StrikeProbe
{
    public int Attacker, Victim;
    public int Part;
    public float Damage;
    public HitKind Kind;
}

/// <summary>
/// Статистика арены (этап 6г; <c>DuelArena.Kills</c>, <c>Hits</c>, <c>LastHit</c>, <c>LastPlayerDamage</c>, <c>LastPlayerSeverAt</c>
/// SWORD) — ресурс-значение (хешируется): убито врагов — любые смерти врагов, в том числе от кровопотери (в SWORD — только от
/// удара игрока); засчитанных попаданий — всех бойцов; последний удар игрока — часть, урон и порог отруба. Сброс арены (R) обнуляет
/// всё, и последний удар (в SWORD он переживал сброс).
/// </summary>
public struct ArenaStats
{
    public int Kills;
    public int Hits;

    /// <summary>Последнее попадание игрока: сегмент (−1 — не было), урон и порог отруба той части.</summary>
    public int LastPart;
    public float LastDamage, LastSeverAt;

    public static ArenaStats Start => new() { LastPart = -1 };
}
