using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Тела отделённых кусков бойцов последнего сыгранного тика (<see cref="IPiecePoses"/>): бойцы с <see cref="Severance"/> и кусками — тела
/// сочленения <c>BodySegs.Bodies + k</c> из вида тел (<see cref="IBodyView"/>). Только читает; снимок — при первом обращении в тике, буферы
/// растут удвоением (после прогрева 0 Б). Без модуля Cutting компоненты нет — кусков нет.
/// </summary>
public sealed class ArenaPiecePoses(SimulationLink link) : IPiecePoses
{
    private readonly WorldReader _world = link.World;
    private ReadQuery? _query;
    private IBodyView? _bodies;
    private PieceBody[] _pieces = new PieceBody[16];
    private (ulong Fighter, int Start, int Count)[] _fighters = new (ulong, int, int)[8];
    private int _fighterCount, _used;
    private ulong _tick, _for = ulong.MaxValue;

    public ulong Tick
    {
        get
        {
            Refresh();
            return _tick;
        }
    }

    public ReadOnlySpan<PieceBody> Pieces(ulong fighter)
    {
        Refresh();
        for (int i = 0; i < _fighterCount; i++)
            if (_fighters[i].Fighter == fighter) return _pieces.AsSpan(_fighters[i].Start, _fighters[i].Count);
        return default;
    }

    /// <summary>Снимок кусков последнего сыгранного тика — раз на тик.</summary>
    private void Refresh()
    {
        ulong next = link.Tick;
        if (next == _for) return;
        _for = next;
        _tick = next > 0 ? next - 1 : 0;
        _fighterCount = _used = 0;
        if (!_world.Knows<Fighter>() || !_world.Knows<Severance>() || !_world.TryView(out _bodies)) return;
        var query = _query ??= _world.Query().With<Fighter>().Read<Severance>().Build();
        foreach (var chunk in _world.Iterate(query))
        {
            var entities = chunk.Entities;
            var severances = chunk.Read<Severance>();
            for (int i = 0; i < entities.Length; i++) Capture(entities[i], severances[i]);
        }
    }

    /// <summary>Куски бойца (тело цикла по бойцам — свой метод: цикл по кускам).</summary>
    private void Capture(Entity e, in Severance severance)
    {
        if (severance.Pieces <= 0 || !_world.TryGet<BodyRef>(e, out var bodyRef)) return;
        int count = Math.Min(Math.Min(severance.Pieces, Severance.MaxPieces), _bodies!.BodyCount(bodyRef) - BodySegs.Bodies);
        if (count <= 0) return;
        if (_used + count > _pieces.Length) Array.Resize(ref _pieces, Math.Max(_pieces.Length * 2, _used + count));
        if (_fighterCount == _fighters.Length) Array.Resize(ref _fighters, _fighters.Length * 2);
        for (int k = 0; k < count; k++)
        {
            var b = _bodies.Body(bodyRef, BodySegs.Bodies + k);
            var piece = severance.Piece[k];
            _pieces[_used + k] = new PieceBody(k, piece.Segment, piece.Mode, b.Position, b.Rotation, b.World, b.Radius, b.HalfLength);
        }
        _fighters[_fighterCount++] = (FighterKey.Of(e), _used, count);
        _used += count;
    }
}
