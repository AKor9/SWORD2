using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Оружие бойца в мире после тика — простые данные для клиентских модулей представления (огонь на головке факела).
/// <see cref="Key"/> — сущность бойца: тот же после смерти и постройки заново на месте. <see cref="Tip"/> — дальняя от кисти
/// вершина меша (у факела — головка). <see cref="Dropped"/> — выпало при смерти и лежит на теле клинка трупа.
/// <see cref="Built"/> — построен в этом тике: головка «прыгает», всё, что велось по тикам, начинается заново.
/// </summary>
public readonly record struct WeaponTip(ulong Key, int Slot, bool IsPlayer, int Weapon, Vector3 Base, Vector3 Tip, bool Alive, bool Dropped,
    bool Built);

/// <summary>
/// Оружие бойцов тика для клиентских модулей (сервис клиента): читает мир в момент вызова — из наблюдателя тиков это
/// состояние после своего тика, не кадра. Только чтение: симуляции, хеша и реплея не касается.
/// </summary>
public interface IWeaponTips
{
    /// <summary>Оружия в таблице (номера 0 … N − 1; до постройки мира — 0).</summary>
    int WeaponCount { get; }

    /// <summary>Имя оружия (файл пака без расширения: <c>sword_1h</c>, <c>torch</c>).</summary>
    string WeaponName(int weapon);

    /// <summary>Номер оружия по имени без учёта регистра; нет — −1.</summary>
    int FindWeapon(string name);

    /// <summary>
    /// Оружие бойцов после последнего тика — по одному на бойца с оружием, в порядке мира; <paramref name="playerOnly"/> —
    /// только игрока. Вернёт, сколько их всего (записано не больше <paramref name="into"/>.Length). Без выделений.
    /// </summary>
    int Read(Span<WeaponTip> into, bool playerOnly);
}
