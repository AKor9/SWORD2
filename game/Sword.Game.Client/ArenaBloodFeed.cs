using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// События боя тика для крови (<see cref="IBloodFeed"/>) из очереди кадра (<see cref="SimulationLink.Frame{T}"/>): записи всех тиков кадра
/// с номерами тиков — берутся только записи последнего сыгранного тика (<c>link.Tick − 1</c>), поэтому в наблюдателе тиков каждое попадание
/// видно ровно раз, при любом числе тиков за кадр. Пара касания попадания — <see cref="StrikeContact"/> с тем же номером касания в том же
/// тике. Снимок — при первом обращении в тике; буферы растут удвоением (после прогрева 0 Б).
/// </summary>
public sealed class ArenaBloodFeed(SimulationLink link) : IBloodFeed
{
    private readonly WorldReader _world = link.World;
    private BloodHit[] _hits = new BloodHit[16];
    private ulong[] _deaths = new ulong[8], _built = new ulong[8], _removed = new ulong[8];
    private int _hitCount, _deathCount, _builtCount, _removedCount;
    private bool _reset;
    private ulong _tick;

    /// <summary><c>link.Tick</c>, для которого сделан снимок (MaxValue — ещё не было).</summary>
    private ulong _for = ulong.MaxValue;

    public ulong Tick
    {
        get
        {
            Refresh();
            return _tick;
        }
    }

    public ReadOnlySpan<BloodHit> Hits
    {
        get
        {
            Refresh();
            return _hits.AsSpan(0, _hitCount);
        }
    }

    public ReadOnlySpan<ulong> Deaths
    {
        get
        {
            Refresh();
            return _deaths.AsSpan(0, _deathCount);
        }
    }

    public ReadOnlySpan<ulong> Built
    {
        get
        {
            Refresh();
            return _built.AsSpan(0, _builtCount);
        }
    }

    public ReadOnlySpan<ulong> Removed
    {
        get
        {
            Refresh();
            return _removed.AsSpan(0, _removedCount);
        }
    }

    public bool ArenaReset
    {
        get
        {
            Refresh();
            return _reset;
        }
    }

    public bool TryBleed(ulong fighter, out float bleed, out bool dead)
    {
        var e = FighterKey.Entity(fighter);
        bleed = 0;
        dead = false;
        if (e.Index <= 0 || e.Generation <= 0 || !_world.IsAlive(e) || !_world.Has<Fighter>(e)) return false;
        if (_world.TryGet<Health>(e, out var health)) bleed = health.Bleed;
        dead = _world.Has<Dead>(e);
        return true;
    }

    /// <summary>Снимок событий последнего сыгранного тика — раз на тик.</summary>
    private void Refresh()
    {
        ulong next = link.Tick;
        if (next == _for) return;
        _for = next;
        _hitCount = _deathCount = _builtCount = _removedCount = 0;
        _reset = false;
        _tick = next > 0 ? next - 1 : 0;
        if (next == 0) return;
        ulong t = _tick;
        if (link.Exports<Event<Hit>>())
        {
            var hits = link.Frame<Event<Hit>>();
            var contacts = link.Exports<Event<StrikeContact>>() ? link.Frame<Event<StrikeContact>>() : default;
            int c0 = From(contacts, t);
            for (int i = From(hits, t); i < hits.Length && hits[i].Tick == t; i++) AddHit(hits[i].Record.Value, contacts[c0..], t);
        }
        if (link.Exports<Added<Dead>>())
        {
            var deaths = link.Frame<Added<Dead>>();
            for (int i = From(deaths, t); i < deaths.Length && deaths[i].Tick == t; i++) Push(ref _deaths, ref _deathCount, FighterKey.Of(deaths[i].Record.Entity));
        }
        if (link.Exports<Removed<Fighter>>())
        {
            var removed = link.Frame<Removed<Fighter>>();
            for (int i = From(removed, t); i < removed.Length && removed[i].Tick == t; i++)
                Push(ref _removed, ref _removedCount, FighterKey.Of(removed[i].Record.Entity));
        }
        if (link.Exports<Added<Fighter>>())
        {
            var added = link.Frame<Added<Fighter>>();
            for (int i = From(added, t); i < added.Length && added[i].Tick == t; i++) AddBuilt(added[i].Record.Entity);
        }
    }

    /// <summary>Первая запись тика <paramref name="tick"/> или позже (записи — по тикам по порядку; последний тик — в конце).</summary>
    private static int From<T>(ReadOnlySpan<(ulong Tick, T Record)> records, ulong tick)
    {
        int i = records.Length;
        while (i > 0 && records[i - 1].Tick >= tick) i--;
        return i;
    }

    private void AddHit(in Hit hit, ReadOnlySpan<(ulong Tick, Event<StrikeContact> Record)> contacts, ulong tick)
    {
        var blade = default(System.Numerics.Vector3);
        float speed = 0;
        for (int k = 0; k < contacts.Length && contacts[k].Tick == tick; k++)
        {
            var c = contacts[k].Record.Value;
            if (c.Id != hit.Contact) continue;
            blade = c.Blade;
            speed = c.Speed;
            break;
        }
        if (_hitCount == _hits.Length) Array.Resize(ref _hits, _hits.Length * 2);
        _hits[_hitCount++] = new BloodHit(tick, FighterKey.Of(hit.Victim), FighterKey.Of(hit.Attacker), (byte)hit.Part, hit.Point, hit.Dir, blade,
            speed, hit.Damage, hit.Kind, hit.Killed, hit.Probe, hit.Corpse, (uint)hit.Contact.Index);
    }

    /// <summary>Постройка бойца; игрок, снятый и построенный в том же тике (на прежней сущности), — сброс арены.</summary>
    private void AddBuilt(Entity e)
    {
        ulong key = FighterKey.Of(e);
        Push(ref _built, ref _builtCount, key);
        if (_reset || !_world.Has<PlayerTag>(e)) return;
        foreach (ulong removed in _removed.AsSpan(0, _removedCount))
            if (removed == key) _reset = true;
    }

    private static void Push(ref ulong[] list, ref int count, ulong key)
    {
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = key;
    }
}
