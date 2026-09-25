using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Секция конфига <c>combat</c> (ручки «Бой»): меч в руке игрока при старте — имя меча пака
/// (<c>SWORD_WEAPON</c> SWORD; без учёта регистра); неизвестное имя — ошибка конфига (в SWORD — сообщение и меч по умолчанию).
/// Нападают ли враги, неуязвим ли игрок и рывок — в мир ресурсом <see cref="CombatRules"/>.
/// </summary>
public sealed record CombatSettings
{
    /// <summary>Меч игрока при старте (<c>combat.playerWeapon</c>): имя файла пака без расширения.</summary>
    public string PlayerWeapon { get; init; } = WeaponTable.DefaultName;

    /// <summary>Враги бьют по таймеру ИИ (<c>combat.enemiesAttack</c>, по умолчанию да); нет — мирные враги.</summary>
    public bool EnemiesAttack { get; init; } = true;

    /// <summary>Игрок неуязвим (<c>combat.playerInvulnerable</c>, по умолчанию нет).</summary>
    public bool PlayerInvulnerable { get; init; }

    /// <summary>
    /// Рывок (Пробел): пиковая прибавка скорости, м/с (<c>combat.dashSpeed</c>), время, с (<c>combat.dashTime</c>), перезарядка, с
    /// (<c>combat.dashCooldown</c>), неуязвимость с начала рывка, с (<c>combat.dashInvulnerable</c>). По умолчанию — 9,5 м/с за 0,3 с:
    /// прибавка на 1,43 м, как root motion клипов уклонения SWORD (1,29…1,61 м, в среднем 1,42), перезарядка 0,6 с (SWORD), неуязвим
    /// весь рывок.
    /// </summary>
    public float DashSpeed { get; init; } = PlayerControlRules.DashSpeed;
    public float DashTime { get; init; } = PlayerControlRules.DashTime;
    public float DashCooldown { get; init; } = PlayerControlRules.DashCooldown;
    public float DashInvulnerable { get; init; } = PlayerControlRules.DashInvulnerable;

    /// <summary>
    /// Урон ударов игрока — множитель (<c>combat.playerDamage</c>; <c>SWORD_PLAYER_CUT</c>, ручка «урон» SWORD — по умолчанию 2) и
    /// острота клинка игрока (<c>combat.playerSharpness</c>, ручка «острота» — 1): порог отруба части делится на неё (не меньше 0,05).
    /// </summary>
    public float PlayerDamage { get; init; } = DamageRules.PlayerDamage;
    public float PlayerSharpness { get; init; } = 1;
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
