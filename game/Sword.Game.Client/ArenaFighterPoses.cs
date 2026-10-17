using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Позы бойцов последнего сыгранного тика (<see cref="IFighterPoses"/>): мир после тика — тот же, что снимает кадр (<see cref="ArenaView"/>),
/// и те же выражения суставов (<see cref="ArenaView.LivingJoints"/>, <see cref="ArenaView.CorpseJoints"/>), только без переноса по доле
/// тика: живой — модельная поза тика · мир бойца на тике (<see cref="ArenaView.TickWorld"/>), позы нет — поза привязки; труп с привязкой
/// скина и полным телом — привязка · система тела тика. Снимок — при первом обращении в тике; буферы растут удвоением (после прогрева 0 Б).
/// </summary>
public sealed class ArenaFighterPoses(SimulationLink link) : IFighterPoses
{
    private readonly WorldReader _world = link.World;
    private ReadQuery? _query;
    private PosedFighter[] _fighters = new PosedFighter[8];
    private int[] _jointStart = new int[8], _segmentStart = new int[8];
    private Matrix4x4[] _joints = [];
    private SegmentBody[] _segments = [];
    private readonly Matrix4x4[] _bodies = new Matrix4x4[BodySegs.Bodies];
    /// <summary>Тело сочленения каждого сустава скелета (сегмент сустава — <see cref="IBodyView.JointSegment"/>).</summary>
    private int[] _jointBodies = [];
    private int _count, _jointsUsed, _segmentsUsed;
    private ulong _tick;
    private ulong _for = ulong.MaxValue;
    /// <summary>Высота рельефа — делегат один раз (снимок без выделений).</summary>
    private static readonly Func<float, float, float> Height = ArenaTerrain.Height;

    // Виды симуляции — на время снимка.
    private IAnimationView? _animation;
    private IBodyView? _bodyView;
    private ISkinView? _skins;

    public ulong Tick
    {
        get
        {
            Refresh();
            return _tick;
        }
    }

    public int Count
    {
        get
        {
            Refresh();
            return _count;
        }
    }

    public PosedFighter Fighter(int index)
    {
        Refresh();
        return (uint)index < (uint)_count ? _fighters[index] : throw new ArgumentOutOfRangeException(nameof(index), index, $"бойцов {_count}");
    }

    public int Find(ulong fighter)
    {
        Refresh();
        for (int i = 0; i < _count; i++)
            if (_fighters[i].Fighter == fighter) return i;
        return -1;
    }

    public ReadOnlySpan<Matrix4x4> Joints(int index)
    {
        var f = Fighter(index);
        return _joints.AsSpan(_jointStart[index], f.JointCount);
    }

    public ReadOnlySpan<SegmentBody> Segments(int index)
    {
        var f = Fighter(index);
        return _segments.AsSpan(_segmentStart[index], f.SegmentCount);
    }

    /// <summary>Снимок поз последнего сыгранного тика — раз на тик.</summary>
    private void Refresh()
    {
        ulong next = link.Tick;
        if (next == _for) return;
        _for = next;
        _tick = next > 0 ? next - 1 : 0;
        _count = _jointsUsed = _segmentsUsed = 0;
        if (!_world.Knows<Fighter>() || !_world.Knows<Transform>()) return;
        _world.TryView(out _animation);
        _world.TryView(out _bodyView);
        _world.TryView(out _skins);
        if (_bodyView is { } bodies && _jointBodies.Length != bodies.JointCount)
        {
            _jointBodies = new int[bodies.JointCount];
            for (int j = 0; j < _jointBodies.Length; j++) _jointBodies[j] = BodySegs.Body(bodies.JointSegment(j));
            // Буферы снимка — сразу на всех бойцов арены: снимок, впервые взятый посреди боя, не выделяет.
            int most = ArenaState.MaxEnemies + 1;
            Grow(ref _joints, most * bodies.JointCount);
            Grow(ref _segments, most * BodySegs.Bodies);
            if (_fighters.Length < most)
            {
                Array.Resize(ref _fighters, most);
                Array.Resize(ref _jointStart, most);
                Array.Resize(ref _segmentStart, most);
            }
        }
        var query = _query ??= _world.Query().With<Fighter>().Read<Transform>().Build();
        foreach (var chunk in _world.Iterate(query))
        {
            var entities = chunk.Entities;
            var roots = chunk.Read<Transform>();
            for (int i = 0; i < entities.Length; i++) Capture(entities[i], roots[i]);
        }
    }

    /// <summary>Боец снимка (тело цикла по бойцам — свой метод: циклы по телам и суставам).</summary>
    private void Capture(Entity e, in Transform cur)
    {
        if (_count == _fighters.Length)
        {
            Array.Resize(ref _fighters, _count * 2);
            Array.Resize(ref _jointStart, _count * 2);
            Array.Resize(ref _segmentStart, _count * 2);
        }
        bool dead = _world.Has<Dead>(e), player = _world.Has<PlayerTag>(e);
        int slot = _world.TryGet<FighterSlot>(e, out var s) ? s.Index : -1;

        // Тела сегментов тика.
        int segments = 0;
        bool hasBody = _world.TryGet<BodyRef>(e, out var bodyRef) && _bodyView is not null && _bodyView.BodyCount(bodyRef) >= BodySegs.Bodies;
        if (hasBody)
        {
            Grow(ref _segments, _segmentsUsed + BodySegs.Bodies);
            for (int k = 0; k < BodySegs.Bodies; k++)
            {
                var b = _bodyView!.Body(bodyRef, k);
                var world = b.World;
                _bodies[k] = world;
                _segments[_segmentsUsed + k] = new SegmentBody(BodySegs.Segment(k), b.Position, b.Rotation, world, b.Radius, b.HalfLength);
            }
            segments = BodySegs.Bodies;
        }

        // Суставы: труп — по телам, живой — модельная поза тика в мире бойца на тике.
        int joints = 0;
        bool corpse = false;
        if (dead && hasBody && _skins is not null && _skins.TryGet(bodyRef, out var binding, out _) && binding.Length == _jointBodies.Length)
        {
            Grow(ref _joints, _jointsUsed + binding.Length);
            ArenaView.CorpseJoints(binding, _jointBodies, _bodies, _joints.AsSpan(_jointsUsed, binding.Length));
            joints = binding.Length;
            corpse = true;
        }
        else if (_animation is { } animation)
        {
            var model = _world.TryGet<PoseRef>(e, out var pose) ? animation.Model(pose) : default;
            if (model.IsEmpty) model = animation.ReferenceModel();
            Grow(ref _joints, _jointsUsed + model.Length);
            ArenaView.LivingJoints(model, ArenaView.TickWorld(cur, Height), _joints.AsSpan(_jointsUsed, model.Length));
            joints = model.Length;
            // Живой с отделёнными частями: их суставы — по телам (привязка снята при отделении).
            if (hasBody && _world.TryGet<Severance>(e, out var severance) && _skins is not null && _skins.TryGet(bodyRef, out var detached, out _)
                && detached.Length == model.Length && detached.Length == _jointBodies.Length)
                ArenaView.DetachedJoints(detached, _jointBodies, _bodies, severance.Detached, _joints.AsSpan(_jointsUsed, model.Length));
        }

        _fighters[_count] = new PosedFighter(FighterKey.Of(e), slot, player, dead, corpse, joints, segments);
        _jointStart[_count] = _jointsUsed;
        _segmentStart[_count] = _segmentsUsed;
        _jointsUsed += joints;
        _segmentsUsed += segments;
        _count++;
    }

    private static void Grow<T>(ref T[] array, int needed)
    {
        if (array.Length < needed) Array.Resize(ref array, Math.Max(array.Length * 2, needed));
    }
}
