using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Секция конфига <c>combat</c> (ручки «Бой»): меч в руке игрока при старте — имя меча пака без учёта регистра; неизвестное имя —
/// ошибка конфига. Нападают ли враги, неуязвим ли игрок и рывок — в мир ресурсом <see cref="CombatRules"/>.
/// </summary>
public sealed record CombatSettings
{
    /// <summary>Меч игрока при старте (<c>combat.playerWeapon</c>): имя файла пака без расширения.</summary>
    public string PlayerWeapon { get; init; } = WeaponTable.DefaultName;

    /// <summary>
    /// Оружие только игрока (по умолчанию факел): враги не берут его случайно. Имена пака без расширения, регистр не важен; всё оружие
    /// только игрока — ошибка конфига.
    /// </summary>
    public string[] PlayerOnlyWeapons { get; init; } = ["torch", "maul"];

    /// <summary>
    /// Тупое оружие (<c>combat.bluntWeapons</c>, по умолчанию молот): его касание — тупой удар (<see cref="HitKind.Blunt"/>): ломает и
    /// отрывает, а не режет. Имена пака без расширения, регистр не важен; имя не из пака пропускается.
    /// </summary>
    public string[] BluntWeapons { get; init; } = ["maul"];

    /// <summary>Враги бьют по таймеру ИИ (<c>combat.enemiesAttack</c>, по умолчанию да); нет — мирные враги.</summary>
    public bool EnemiesAttack { get; init; } = true;

    /// <summary>Игрок неуязвим (<c>combat.playerInvulnerable</c>, по умолчанию нет).</summary>
    public bool PlayerInvulnerable { get; init; }

    /// <summary>
    /// Рывок (Пробел): пиковая прибавка скорости, м/с, время, с, перезарядка, с, неуязвимость с начала рывка, с. По умолчанию — 9,5 м/с
    /// за 0,3 с (прибавка на 1,43 м, как root motion клипов уклонения), перезарядка 0,6 с, неуязвим весь рывок.
    /// </summary>
    public float DashSpeed { get; init; } = PlayerControlRules.DashSpeed;
    public float DashTime { get; init; } = PlayerControlRules.DashTime;
    public float DashCooldown { get; init; } = PlayerControlRules.DashCooldown;
    public float DashInvulnerable { get; init; } = PlayerControlRules.DashInvulnerable;

    /// <summary>
    /// Урон ударов игрока — множитель (по умолчанию 2) и острота клинка игрока (1): порог отруба части делится на неё (не меньше 0,05).
    /// </summary>
    public float PlayerDamage { get; init; } = DamageRules.PlayerDamage;
    public float PlayerSharpness { get; init; } = 1;
}

/// <summary>Оружие и удары для наблюдателей (<see cref="ICombatView"/>): таблицы — неизменяемые ассеты.</summary>
public sealed class CombatView(WeaponTable weapons, int[] enemyWeapons, StrikeTable strikes, int hand) : ICombatView
{
    public ReadOnlySpan<WeaponInfo> Weapons => weapons.Weapons;
    public ReadOnlySpan<int> EnemyWeapons => enemyWeapons;
    public int DefaultWeapon => weapons.Default;
    public int HandJoint => hand;
    public ReadOnlySpan<AttackInfo> Attacks => strikes.Attacks;
    public int FindWeapon(string name) => weapons.Find(name);
}
