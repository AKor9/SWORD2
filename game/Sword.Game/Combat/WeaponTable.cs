using Sword.Engine.Content;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Таблица оружия (<c>CharacterRig.Weapons</c> SWORD 2eab13b, этап 6б): все мечи пака (<c>weapons/*.swmesh</c>) в порядке
/// списка контента — по алфавиту без учёта регистра, номер меча — номер в таблице (<see cref="WeaponSlot.Index"/>); клинок
/// каждого — по его мешу (<see cref="WeaponMesh"/>: <c>MakeWeapon</c>). Меч по умолчанию — <see cref="DefaultName"/>, нет его —
/// первый: по его клинку размечены окна ударов (<see cref="StrikeTable"/>). Ассет симуляции: неизменяема, общая для всех
/// симуляций процесса, наружу — только <see cref="ReadOnlySpan{T}"/>. Без оружия игра не стартует (как <c>TryLoad</c> SWORD).
/// С 7ж — и тело клинка каждого меча (<see cref="Bodies"/>: капсула от хвата до кончика, <see cref="BladeBody"/>), посчитанное
/// один раз при загрузке.
/// </summary>
public sealed class WeaponTable
{
    /// <summary>Меч по умолчанию (<c>DefaultWeaponName</c>): им размечены удары, он же в руке игрока без конфига.</summary>
    public const string DefaultName = "sword_1h";

    private readonly WeaponInfo[] _weapons;
    private readonly BladeBody[] _bodies;

    public ReadOnlySpan<WeaponInfo> Weapons => _weapons;
    public int Count => _weapons.Length;

    /// <summary>Тело клинка меча по номеру (7ж; <see cref="WeaponInfo.Body"/> — то же, посчитанное заранее).</summary>
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

    /// <summary>Номер меча по имени без учёта регистра (<c>DuelArena.Weapon</c> SWORD); нет — −1.</summary>
    public int Find(string name)
    {
        for (int i = 0; i < _weapons.Length; i++)
            if (string.Equals(_weapons[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Имена мечей через запятую — для сообщений.</summary>
    public string Names => string.Join(", ", _weapons.Select(w => w.Name));

    /// <summary>Все мечи пака (<c>CharacterRig.TryLoad</c>: <c>content.List(WeaponDir, WeaponExt)</c> → <c>MakeWeapon</c>).</summary>
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
