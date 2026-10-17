using System.Numerics;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Client;

/// <summary>
/// Слой крови на оружии (<see cref="IWeaponBloodLayer"/>): лезвия оружия таблицы — зона «blade» их <c>.swblood</c> (<see cref="WeaponBloodZones"/>)
/// и система тела клинка в пространстве меша (<see cref="ArenaView.BladeInHandInverse"/>, обратная); источник SRV подключает модуль крови,
/// клиент арены спрашивает его при сборке частей оружия. Источника или зоны нет — слоя нет.
/// </summary>
public sealed class ArenaWeaponBloodLayer : IWeaponBloodLayer
{
    private Func<ulong, int, int>? _srvOf;
    private BladeBloodZone?[] _zones = [];

    public void Attach(Func<ulong, int, int> srvOf) => _srvOf = srvOf;

    /// <summary>Подключён ли источник.</summary>
    public bool Attached => _srvOf is not null;

    /// <summary>Оружия с зоной лезвия.</summary>
    public int Blades { get; private set; }

    /// <summary>Лезвия оружия таблицы по зонам крови (null — у оружия зон нет).</summary>
    public void Load(ReadOnlySpan<WeaponInfo> weapons, ReadOnlySpan<WeaponBloodData?> blood)
    {
        _zones = new BladeBloodZone?[weapons.Length];
        Blades = 0;
        for (int w = 0; w < weapons.Length; w++)
        {
            if (w >= blood.Length || WeaponBloodZones.Blade(blood[w]) is not { } z) continue;
            _zones[w] = new BladeBloodZone(z.Origin, z.AxisU, z.AxisV, z.Length, z.Half, BodyToMesh(weapons[w].Body));
            Blades++;
        }
    }

    /// <summary>Точка системы тела клинка → пространство меша оружия (система кисти).</summary>
    public static Matrix4x4 BodyToMesh(in BladeBody body) =>
        Matrix4x4.Invert(ArenaView.BladeInHandInverse(body), out var m) ? m : Matrix4x4.Identity;

    public bool TryBlade(int weapon, out BladeBloodZone zone)
    {
        if ((uint)weapon < (uint)_zones.Length && _zones[weapon] is { } z)
        {
            zone = z;
            return true;
        }
        zone = default;
        return false;
    }

    /// <summary>Поле <c>owner</c> экземпляров частей оружия бойца: SRV слоя + 1, 0 — слоя нет.</summary>
    public uint OwnerOf(ulong fighter, int weapon)
    {
        if ((uint)weapon >= (uint)_zones.Length || _zones[weapon] is null) return 0;
        int srv = _srvOf?.Invoke(fighter, weapon) ?? -1;
        return srv >= 0 ? (uint)srv + 1 : 0;
    }
}
