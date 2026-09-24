using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Секция конфига <c>combat</c> (этап 6; ручки «Бой» — 6е). С 6б — меч в руке игрока при старте: имя меча пака
/// (<c>SWORD_WEAPON</c> SWORD; без учёта регистра); неизвестное имя — ошибка конфига (в SWORD — сообщение и меч по умолчанию).
/// </summary>
public sealed record CombatSettings
{
    /// <summary>Меч игрока при старте (<c>combat.playerWeapon</c>): имя файла пака без расширения.</summary>
    public string PlayerWeapon { get; init; } = WeaponTable.DefaultName;
}

/// <summary>Оружие и удары для наблюдателей (<see cref="ICombatView"/>): таблицы — неизменяемые ассеты.</summary>
public sealed class CombatView(WeaponTable weapons, StrikeTable strikes, int hand) : ICombatView
{
    public ReadOnlySpan<WeaponInfo> Weapons => weapons.Weapons;
    public int DefaultWeapon => weapons.Default;
    public int HandJoint => hand;
    public ReadOnlySpan<AttackInfo> Attacks => strikes.Attacks;
    public int FindWeapon(string name) => weapons.Find(name);
}
