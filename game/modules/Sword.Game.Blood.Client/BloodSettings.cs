namespace Sword.Game.Blood;

/// <summary>
/// Секция конфига <c>blood</c> (слои — как у всех секций: код → <c>sword.json</c> → <c>sword.user.json</c> → <c>SWORD_BLOOD_*</c> →
/// <c>--set blood.ключ=значение</c>). Весь модуль выключает <c>modules.blood.enabled=false</c>; здесь — его системы по отдельности.
/// </summary>
public sealed record BloodSettings
{
    /// <summary>Кровь на теле: потёки из ран по коже, кровь на коже, сочение сильных ран, капли с тела.</summary>
    public bool Body { get; init; } = true;

    /// <summary>Кровь на полу: вязкое поле в окне за игроком и карта засохших пятен.</summary>
    public bool Floor { get; init; } = true;

    /// <summary>Попаданий последнего кадра на оверлее <c>blood</c> не больше (лишние не рисуются).</summary>
    public int ShownHits { get; init; } = 64;

    /// <summary>Потёков по коже на бойца не больше (вытесняется самый старый).</summary>
    public int DripsPerFighter { get; init; } = 32;

    /// <summary>Потёков по коже на мир не больше.</summary>
    public int DripsWorld { get; init; } = 1024;

    /// <summary>Бойцов с кровью на коже одновременно (пул буферов по вершинам: ≈ 0.78 МБ видеопамяти и 0.6 МБ памяти на слот).</summary>
    public int SkinSlots { get; init; } = 40;

    /// <summary>Предел <see cref="ShownHits"/>.</summary>
    public const int ShownHitsLimit = 1024;

    /// <summary>Проверка чисел — до ресурсов.</summary>
    public void Validate()
    {
        if (ShownHits is < 0 or > ShownHitsLimit) throw new InvalidDataException($"конфиг: blood.shownHits = {ShownHits}: 0…{ShownHitsLimit}");
        if (DripsPerFighter is < 1 or > 256) throw new InvalidDataException($"конфиг: blood.dripsPerFighter = {DripsPerFighter}: 1…256");
        if (SkinSlots is < 1 or > 256) throw new InvalidDataException($"конфиг: blood.skinSlots = {SkinSlots}: 1…256");
        if (DripsWorld is < 1 or > 65536) throw new InvalidDataException($"конфиг: blood.dripsWorld = {DripsWorld}: 1…65536");
    }
}
