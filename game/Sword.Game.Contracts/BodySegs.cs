namespace Sword.Game.Contracts;

/// <summary>
/// Сегменты тела бойца — номера SWORD 2eab13b (<c>Sword.Combat.BodySegs</c>, константы <c>Fighter</c>). Номер сегмента —
/// идентификатор для всех, кто говорит о части тела: тела сочленения (<see cref="BodyRef"/>), позже маска отрубленного,
/// попадания, ID-буфер снимка, трассы SWORD. Номера не сдвигаются: 11 — клинок SWORD (<c>Weapon</c>, этап 6б: отрезок
/// <see cref="Blade"/> и пиксели меча в ID-буфере); физического тела клинка у бойца нет (этап 7), поэтому в сочленении бойца
/// <see cref="Bodies"/> тел, и тело <c>i</c> — сегмент <see cref="Segment"/>(i).
/// </summary>
public static class BodySegs
{
    public const int Pelvis = 0, Torso = 1, Head = 2, UArmL = 3, FArmL = 4, UArmR = 5, FArmR = 6,
        ThighL = 7, ShinL = 8, ThighR = 9, ShinR = 10, FootL = 12, FootR = 13;

    /// <summary>Клинок (<c>Fighter.Weapon</c> SWORD): тела нет (<see cref="Body"/> — −1), есть отрезок <see cref="Blade"/> и меч на экране.</summary>
    public const int Weapon = 11;

    /// <summary>Номеров сегментов — 0…13, как <c>BodySegs.Count</c> SWORD (с номером клинка 11).</summary>
    public const int Count = 14;

    /// <summary>Тел в сочленении бойца: все сегменты, кроме клинка.</summary>
    public const int Bodies = 13;

    /// <summary>Сегмент тела <paramref name="body"/> сочленения бойца (0…<see cref="Bodies"/> − 1): тела идут по возрастанию номеров сегментов.</summary>
    public static int Segment(int body) => (uint)body < Bodies
        ? body < 11 ? body : body + 1
        : throw new ArgumentOutOfRangeException(nameof(body), body, $"в сочленении бойца тел {Bodies}");

    /// <summary>Тело сочленения бойца для сегмента; у сегмента нет тела (клинок, вне 0…13) — −1.</summary>
    public static int Body(int segment) => segment switch
    {
        >= 0 and < 11 => segment,
        FootL or FootR => segment - 1,
        _ => -1,
    };

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

    /// <summary>Сегмент тела по имени <see cref="Name"/> (без учёта регистра; только сегменты с телом — не клинок); нет такого — −1.</summary>
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
