using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Отделения последнего сыгранного тика (<see cref="ISeverFeed"/>) — из очереди кадра (<see cref="LimbDetached"/>); снимок — раз на тик,
/// без выделений после прогрева.
/// </summary>
public sealed class ArenaSeverFeed(SimulationLink link) : ISeverFeed
{
    private SeverRecord[] _detached = new SeverRecord[8];
    private int _count;
    private ulong _tick, _for = ulong.MaxValue;

    public ulong Tick
    {
        get
        {
            Refresh();
            return _tick;
        }
    }

    public ReadOnlySpan<SeverRecord> Detached
    {
        get
        {
            Refresh();
            return _detached.AsSpan(0, _count);
        }
    }

    private void Refresh()
    {
        ulong next = link.Tick;
        if (next == _for) return;
        _for = next;
        _count = 0;
        _tick = next > 0 ? next - 1 : 0;
        if (next == 0 || !link.Exports<Event<LimbDetached>>()) return;
        var records = link.Frame<Event<LimbDetached>>();
        int i = records.Length;
        while (i > 0 && records[i - 1].Tick >= _tick) i--;
        for (; i < records.Length && records[i].Tick == _tick; i++)
        {
            if (_count == _detached.Length) Array.Resize(ref _detached, _count * 2);
            var e = records[i].Record.Value;
            _detached[_count++] = new SeverRecord(FighterKey.Of(e.Victim), e);
        }
    }
}
