using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Зоны крови оружия таблицы (<c>data/weapons/&lt;имя&gt;.swblood</c>, пекарня): для представления крови на оружии — симуляция их не читает.
/// Нет файла или он испорчен — у оружия зон нет (предупреждение в журнале), игра идёт.
/// </summary>
public static class WeaponBloodZones
{
    /// <summary>Каталог зон крови в контенте.</summary>
    public const string Dir = "data/weapons";

    /// <summary>Зоны оружия по номеру таблицы из пака игры (<see cref="ContentLocator"/>); пака нет — все null.</summary>
    public static WeaponBloodData?[] LoadFromPak(ReadOnlySpan<WeaponInfo> weapons)
    {
        var zones = new WeaponBloodData?[weapons.Length];
        if (ContentLocator.Find() is null) return zones;
        try
        {
            using var pak = ContentLocator.Open();
            Load(pak, weapons, zones);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warning("blood", $"зоны крови оружия не прочитаны: {e.Message}");
        }
        return zones;
    }

    /// <summary>Зоны оружия по номеру таблицы из <paramref name="content"/> в <paramref name="into"/>.</summary>
    public static void Load(IContentSource content, ReadOnlySpan<WeaponInfo> weapons, Span<WeaponBloodData?> into)
    {
        for (int w = 0; w < weapons.Length; w++)
        {
            string path = $"{Dir}/{weapons[w].Name}{WeaponBloodData.Extension}";
            if (!content.Exists(path))
            {
                Log.Warning("blood", $"нет зон крови {path}: крови на оружии «{weapons[w].Name}» не будет");
                continue;
            }
            try
            {
                using var s = content.OpenRead(path);
                into[w] = WeaponBloodData.Read(s, content.Describe(path));
            }
            catch (InvalidDataException e)
            {
                Log.Warning("blood", e.Message);
            }
        }
    }

    /// <summary>Зона лезвия («blade», плоскость); нет — null.</summary>
    public static WeaponBloodZone? Blade(WeaponBloodData? data)
    {
        if (data is null) return null;
        foreach (var z in data.Zones)
            if (z.Kind == BloodZoneKind.Plane && string.Equals(z.Name, "blade", StringComparison.OrdinalIgnoreCase)) return z;
        return null;
    }
}
