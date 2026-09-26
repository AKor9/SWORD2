using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Оружие бойцов тика для клиентских модулей (<see cref="IWeaponTips"/>): мир читается в момент вызова — из наблюдателя тиков клиента это
/// состояние после своего тика. Живой — клинок тика (<see cref="Blade"/>: кончик и основание после постановки тела тика, тот же отрезок, что
/// у проходов клинка). Мёртвый — оружие выпало (<see cref="WeaponTip.Dropped"/>): клинок по телу клинка трупа (меч рисуется на нём же,
/// <see cref="ArenaView.BladeInHandInverse"/>), тела нет — последний клинок. Построен в тике (<see cref="WeaponTip.Built"/>) — есть запись
/// <see cref="Added{T}"/> бойца этого тика в очереди кадра (как клиент игры видит постройку, <see cref="ArenaView"/>). Только игрок — свой
/// запрос (с <see cref="PlayerTag"/>): остальные бойцы не обходятся вовсе. Без выделений: запросы строятся один раз.
/// </summary>
public sealed class ArenaWeaponTips(SimulationLink link) : IWeaponTips
{
    private readonly WorldReader _world = link.World;
    private ReadQuery? _query, _playerQuery;
    private Matrix4x4[] _inHand = [];

    public int WeaponCount => _world.TryView<ICombatView>(out var combat) ? combat.Weapons.Length : 0;

    public string WeaponName(int weapon) =>
        _world.TryView<ICombatView>(out var combat) && (uint)weapon < (uint)combat.Weapons.Length ? combat.Weapons[weapon].Name
            : throw new ArgumentOutOfRangeException(nameof(weapon), weapon, "нет такого оружия");

    public int FindWeapon(string name) => _world.TryView<ICombatView>(out var combat) ? combat.FindWeapon(name) : -1;

    public int Read(Span<WeaponTip> into, bool playerOnly)
    {
        if (!_world.Knows<WeaponSlot>() || !_world.Knows<Fighter>() || !_world.TryView<ICombatView>(out var combat)) return 0;
        if (playerOnly && !_world.Knows<PlayerTag>()) return 0;
        var query = playerOnly
            ? _playerQuery ??= _world.Query().With<Fighter>().With<PlayerTag>().Read<WeaponSlot>().Build()
            : _query ??= _world.Query().With<Fighter>().Read<WeaponSlot>().Build();
        _world.TryView<IBodyView>(out var bodies);
        var weapons = combat.Weapons;
        int count = 0;
        foreach (var chunk in _world.Iterate(query))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<WeaponSlot>();
            for (int i = 0; i < entities.Length; i++)
            {
                int weapon = slots[i].Index;
                if ((uint)weapon >= (uint)weapons.Length) continue;
                if (count < into.Length) into[count] = Tip(entities[i], weapon, weapons[weapon], bodies);
                count++;
            }
        }
        return count;
    }

    /// <summary>Оружие одного бойца (тело цикла — свой метод: цикл по бойцам раз в тик).</summary>
    private WeaponTip Tip(Entity e, int weapon, in WeaponInfo info, IBodyView? bodies)
    {
        ulong key = (ulong)(uint)e.Generation << 32 | (uint)e.Index;
        int slot = _world.TryGet<FighterSlot>(e, out var s) ? s.Index : -1;
        bool player = _world.Has<PlayerTag>(e), dead = _world.Has<Dead>(e);
        _world.TryGet<Blade>(e, out var blade);
        Vector3 tip = blade.Tip, @base = blade.Base;
        if (dead && bodies is not null && _world.TryGet<BodyRef>(e, out var body) && bodies.BodyCount(body) == BodySegs.Bodies)
        {
            var b = bodies.Body(body, BodySegs.Body(BodySegs.Weapon));
            var inWorld = InHand(weapon, info) * ArenaView.BodySystem(b.Position, b.Rotation);
            tip = Vector3.Transform(info.BladeTip, inWorld);
            @base = Vector3.Transform(info.BladeBase, inWorld);
        }
        return new WeaponTip(key, slot, player, weapon, @base, tip, !dead, dead, Built(e));
    }

    /// <summary>Боец построен в последнем сыгранном тике: запись <see cref="Added{T}"/> бойца с номером этого тика в очереди кадра.</summary>
    private bool Built(Entity e)
    {
        if (link.Tick == 0 || !link.Exports<Added<Fighter>>()) return false;
        ulong last = link.Tick - 1;
        foreach (var (tick, record) in link.Frame<Added<Fighter>>())
            if (tick == last && record.Entity == e) return true;
        return false;
    }

    /// <summary>Система кисти в системе тела клинка у оружия <paramref name="weapon"/> — считается раз на оружие.</summary>
    private Matrix4x4 InHand(int weapon, in WeaponInfo info)
    {
        if (_inHand.Length <= weapon)
        {
            int old = _inHand.Length;
            Array.Resize(ref _inHand, weapon + 1);
            for (int w = old; w < _inHand.Length; w++) _inHand[w] = new Matrix4x4();   // нулевая — ещё не посчитана
        }
        if (_inHand[weapon].M44 == 0) _inHand[weapon] = ArenaView.BladeInHandInverse(info.Body);
        return _inHand[weapon];
    }
}
