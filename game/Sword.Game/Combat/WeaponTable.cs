using Sword.Engine.Content;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Таблица оружия: все мечи пака в порядке списка контента (по алфавиту без учёта регистра), номер меча — номер в таблице;
/// клинок и тело клинка (<see cref="Bodies"/>) каждого — по его мешу, один раз при загрузке. Меч по умолчанию —
/// <see cref="DefaultName"/> (по его клинку размечены окна ударов). Неизменяема, общая для симуляций процесса; без оружия игра не стартует.
/// </summary>
public sealed class WeaponTable
{
    /// <summary>Меч по умолчанию (<c>DefaultWeaponName</c>): им размечены удары, он же в руке игрока без конфига.</summary>
    public const string DefaultName = "sword_1h";

    private readonly WeaponInfo[] _weapons;
    private readonly BladeBody[] _bodies;

    public ReadOnlySpan<WeaponInfo> Weapons => _weapons;
    public int Count => _weapons.Length;

    /// <summary>Тело клинка меча по номеру (<see cref="WeaponInfo.Body"/> — то же, посчитанное заранее).</summary>
    public ReadOnlySpan<BladeBody> Bodies => _bodies;

    /// <summary>Номер меча по умолчанию (<c>DefaultWeapon</c>).</summary>
    public int Default { get; }

    public WeaponTable(ReadOnlySpan<WeaponInfo> weapons)
    {
        if (weapons.IsEmpty) throw new InvalidDataException($"нет оружия ({WeaponMesh.Dir}/*{WeaponMesh.Ext})");
        if (weapons.Length > PlayerInventory.MaxWeapons)
            throw new InvalidDataException($"оружия {weapons.Length} — инвентарь игрока держит не больше {PlayerInventory.MaxWeapons}");
        _weapons = weapons.ToArray();
        _bodies = [.. _weapons.Select(w => w.Body)];
        Default = Math.Max(0, Find(DefaultName));
    }

    /// <summary>Номер меча по имени без учёта регистра; нет — −1.</summary>
    public int Find(string name)
    {
        for (int i = 0; i < _weapons.Length; i++)
            if (string.Equals(_weapons[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Оружие врагов: номера таблицы по возрастанию без оружия только игрока (<paramref name="playerOnly"/> — имена, регистр не важен).
    /// Оружие только игрока в конце таблицы не меняет выбор врагов среди остальных.
    /// </summary>
    /// <exception cref="InvalidDataException">Врагам не осталось оружия.</exception>
    public int[] EnemyWeapons(IEnumerable<string> playerOnly)
    {
        var excluded = new HashSet<string>(playerOnly, StringComparer.OrdinalIgnoreCase);
        int[] pool = [.. Enumerable.Range(0, _weapons.Length).Where(w => !excluded.Contains(_weapons[w].Name))];
        if (pool.Length == 0)
            throw new InvalidDataException($"combat.playerOnlyWeapons: всё оружие пака ({Names}) — только игрока, врагам не осталось");
        return pool;
    }

    /// <summary>Имена мечей через запятую — для сообщений.</summary>
    public string Names => string.Join(", ", _weapons.Select(w => w.Name));

    /// <summary>Все мечи пака (<c>content.List(WeaponDir, WeaponExt)</c>).</summary>
    /// <exception cref="FileNotFoundException">В контенте нет ни одного меча.</exception>
    /// <exception cref="InvalidDataException">Меш меча испорчен, со суставами или без клинка; мечей больше 64.</exception>
    public static WeaponTable Load(IContentSource content)
    {
        var files = content.List(WeaponMesh.Dir, WeaponMesh.Ext);
        if (files.Count == 0)
            throw new FileNotFoundException($"в {content.Location} нет оружия ({WeaponMesh.Dir}/*{WeaponMesh.Ext})", WeaponMesh.Dir);
        var weapons = new WeaponInfo[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            WeaponBlade blade;
            using (var s = content.OpenRead(path)) blade = WeaponMesh.Read(s, content.Describe(path));
            weapons[i] = new WeaponInfo(Path.GetFileNameWithoutExtension(path), path, blade.Base, blade.Tip);
        }
        return new WeaponTable(weapons);
    }
}
