namespace Sword.Game.Contracts;

/// <summary>
/// Сегменты тела бойца. Номер сегмента — идентификатор части тела для всех (тела сочленения, попадания, ID-буфер снимка);
/// номера не сдвигаются. Тело <c>i</c> сочленения — сегмент <c>i</c>. 11 — клинок: кинематическая капсула по мечу в руке
/// (<see cref="BladeBody"/>), целью ударов и ран не бывает (<see cref="IsFlesh"/>).
/// </summary>
public static class BodySegs
{
    public const int Pelvis = 0, Torso = 1, Head = 2, UArmL = 3, FArmL = 4, UArmR = 5, FArmR = 6,
        ThighL = 7, ShinL = 8, ThighR = 9, ShinR = 10, FootL = 12, FootR = 13;

    /// <summary>
    /// Клинок: тело 11 сочленения бойца, капсула от хвата (конец A оси, точка сустава запястья трупа) до кончика меча
    /// (<see cref="BladeBody"/>); отрезок удара — <see cref="Blade"/>.
    /// </summary>
    public const int Weapon = 11;

    /// <summary>Номеров сегментов — 0…13 (с номером клинка 11).</summary>
    public const int Count = 14;

    /// <summary>Тел в сочленении бойца: по телу на сегмент, клинок — тоже.</summary>
    public const int Bodies = Count;

    /// <summary>Капсул плоти бойца — все сегменты, кроме клинка: цели ударов и проб, раны (<see cref="IsFlesh"/>).</summary>
    public const int Flesh = Count - 1;

    /// <summary>Сегмент тела <paramref name="body"/> сочленения бойца (0…<see cref="Bodies"/> − 1): тело <c>i</c> — сегмент <c>i</c>.</summary>
    public static int Segment(int body) => (uint)body < Bodies
        ? body
        : throw new ArgumentOutOfRangeException(nameof(body), body, $"в сочленении бойца тел {Bodies}");

    /// <summary>Тело сочленения бойца для сегмента (тот же номер); вне 0…13 — −1.</summary>
    public static int Body(int segment) => (uint)segment < Bodies ? segment : -1;

    /// <summary>Сегмент плоти (0…13, не клинок): цель ударов и проб, у него рана (клинок — тело, но не часть тела бойца).</summary>
    public static bool IsFlesh(int segment) => (uint)segment < Count && segment != Weapon;

    /// <summary>Имя сегмента для людей и журнала (<c>pelvis</c>, <c>forearm.l</c>…); нет такого сегмента — номер.</summary>
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

    /// <summary>Сегмент по имени <see cref="Name"/> (без учёта регистра; и клинок — <c>blade</c>); нет такого — −1.</summary>
    public static int Find(string name)
    {
        for (int body = 0; body < Bodies; body++)
            if (string.Equals(Name(Segment(body)), name, StringComparison.OrdinalIgnoreCase)) return Segment(body);
        return -1;
    }

    /// <summary>Название сегмента по-русски для людей; нет такого — номер.</summary>
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
