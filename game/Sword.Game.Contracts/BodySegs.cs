namespace Sword.Game.Contracts;

/// <summary>
/// Сегменты тела бойца — номера SWORD 2eab13b (<c>Sword.Combat.BodySegs</c>, константы <c>Fighter</c>). Номер сегмента —
/// идентификатор для всех, кто говорит о части тела: тела сочленения (<see cref="BodyRef"/>), позже маска отрубленного,
/// попадания, ID-буфер снимка. Номера не сдвигаются. С 7ж у каждого сегмента своё тело: сочленение бойца — <see cref="Bodies"/>
/// тел, тело <c>i</c> — сегмент <c>i</c> (<see cref="Segment"/>, <see cref="Body"/> — тождество). 11 — клинок (<c>Weapon</c>
/// SWORD): капсула по мечу в руке (решение 3 этапа 7, <see cref="BladeBody"/>), кинематическая, как тела живого; целью ударов и
/// ран она не бывает (<see cref="IsFlesh"/>): отрезок клинка удара — <see cref="Blade"/>.
/// </summary>
public static class BodySegs
{
    public const int Pelvis = 0, Torso = 1, Head = 2, UArmL = 3, FArmL = 4, UArmR = 5, FArmR = 6,
        ThighL = 7, ShinL = 8, ThighR = 9, ShinR = 10, FootL = 12, FootR = 13;

    /// <summary>
    /// Клинок (<c>Fighter.Weapon</c> SWORD): с 7ж — тело 11 сочленения бойца, капсула от хвата до кончика меча в руке
    /// (<see cref="BladeBody"/>; хват — конец A оси, точка сустава запястья трупа, 7з); отрезок удара — <see cref="Blade"/>, меч на экране.
    /// </summary>
    public const int Weapon = 11;

    /// <summary>Номеров сегментов — 0…13, как <c>BodySegs.Count</c> SWORD (с номером клинка 11).</summary>
    public const int Count = 14;

    /// <summary>Тел в сочленении бойца (7ж): по телу на сегмент, клинок — тоже (до 7ж — 13 тел без клинка).</summary>
    public const int Bodies = Count;

    /// <summary>Капсул плоти бойца — все сегменты, кроме клинка: цели ударов и проб, раны (<see cref="IsFlesh"/>).</summary>
    public const int Flesh = Count - 1;

    /// <summary>Сегмент тела <paramref name="body"/> сочленения бойца (0…<see cref="Bodies"/> − 1): тело <c>i</c> — сегмент <c>i</c>.</summary>
    public static int Segment(int body) => (uint)body < Bodies
        ? body
        : throw new ArgumentOutOfRangeException(nameof(body), body, $"в сочленении бойца тел {Bodies}");

    /// <summary>Тело сочленения бойца для сегмента (7ж — тот же номер); вне 0…13 — −1.</summary>
    public static int Body(int segment) => (uint)segment < Bodies ? segment : -1;

    /// <summary>Сегмент плоти (0…13, не клинок): цель ударов и проб, у него рана (клинок — тело, но не часть тела бойца).</summary>
    public static bool IsFlesh(int segment) => (uint)segment < Count && segment != Weapon;

    /// <summary>Имя сегмента для людей и агента (<c>pelvis</c>, <c>forearm.l</c>…); нет такого сегмента — номер.</summary>
    public static string Name(int segment) => segment switch
    {
        Pelvis => "pelvis",
        Torso => "torso",
        Head => "head",
        UArmL => "upperarm.l",
        FArmL => "forearm.l",
        UArmR => "upperarm.r",
        FArmR => "forearm.r",
        ThighL => "thigh.l",
        ShinL => "shin.l",
        ThighR => "thigh.r",
        ShinR => "shin.r",
        FootL => "foot.l",
        FootR => "foot.r",
        Weapon => "blade",
        _ => segment.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Сегмент по имени <see cref="Name"/> (без учёта регистра; с 7ж и клинок — <c>blade</c>); нет такого — −1.</summary>
    public static int Find(string name)
    {
        for (int body = 0; body < Bodies; body++)
            if (string.Equals(Name(Segment(body)), name, StringComparison.OrdinalIgnoreCase)) return Segment(body);
        return -1;
    }

    /// <summary>Название сегмента по-русски для людей (агент, ID-буфер: «левое предплечье врага 3»); нет такого — номер.</summary>
    public static string Title(int segment) => segment switch
    {
        Pelvis => "таз",
        Torso => "торс",
        Head => "голова",
        UArmL => "левое плечо",
        FArmL => "левое предплечье",
        UArmR => "правое плечо",
        FArmR => "правое предплечье",
        ThighL => "левое бедро",
        ShinL => "левая голень",
        ThighR => "правое бедро",
        ShinR => "правая голень",
        FootL => "левая стопа",
        FootR => "правая стопа",
        Weapon => "клинок",
        _ => segment.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
