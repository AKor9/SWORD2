using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Вид Motion Matching для наблюдателей (клиент модуль не видит): состояние бойца, последний поиск и траектории из
/// диагностики, история корня, замер тика (свой <see cref="MotionMatchProbe"/>). Только читает; буферы ответов — до
/// следующего вызова. Мир — после <see cref="Attach"/>.
/// </summary>
public sealed class MotionMatchView(MotionMatchRuntime runtime) : IMotionMatchView
{
    private readonly MotionMatchProbe _probe = new(runtime);
    private WorldReader _world;
    private bool _attached;
    private MotionTrajectoryPoint[] _points = [];
    private float[] _raw = [];

    /// <summary>Мир симуляции (сборка мира, после <see cref="MotionMatchRuntime.Init"/>).</summary>
    public void Attach(WorldReader world)
    {
        _world = world;
        _attached = true;
        var schema = runtime.Database.Schema;
        _raw = new float[runtime.Database.FeatureCount];
        // Траектория — прошлое, «сейчас», будущее; история — ёмкость кольца (растёт в History, если частота тика выше).
        _points = new MotionTrajectoryPoint[Math.Max(schema.PastTimes.Length + 1 + schema.FutureTimes.Length, 64)];
    }

    public MotionMatchBase Base
    {
        get
        {
            var db = runtime.Database;
            var s = db.Summary;
            return new MotionMatchBase(s.Clips, s.Frames, s.Searchable, s.Edges, s.EarlyExits, s.BuildMilliseconds, db.Fingerprint);
        }
    }

    public string Pattern => runtime.Base.Pattern;

    public int GroupCount => runtime.Database.Schema.GroupCount;

    public string GroupName(int group) => runtime.Database.Schema.Groups[group].Name;

    public int ContactJointCount => runtime.Database.Schema.ContactJoints.Length;

    public int ContactJoint(int contact) => runtime.Database.Schema.ContactJoints[contact];

    /// <summary>Состояние MM живого (или мёртвого — стоит) бойца с начатым проигрывателем.</summary>
    private bool TryState(Entity fighter, out MotionMatchState s, out MotionPlayerState player)
    {
        player = default;
        s = default;
        if (!_attached || !_world.IsAlive(fighter) || !_world.TryGet(fighter, out s) || s.Restart || !runtime.Players.IsAlive(s.Player)) return false;
        player = runtime.Players.State(s.Player);
        return player.Frame >= 0;
    }

    public bool TryStatus(Entity fighter, out MotionMatchStatus status)
    {
        status = default;
        if (!TryState(fighter, out var s, out var p)) return false;
        var db = runtime.Database;
        status = new MotionMatchStatus(p.Frame, db.Clips[db.ClipOf[p.Frame]].LibraryClip, db.FrameInClip(p.Frame), p.Alpha, s.Rate,
            !_world.Has<Dead>(fighter), s.SearchWas, s.SinceSearch, s.Searches, s.Switches, p.Transitions, db.Contacts[p.Frame], s.Twist, s.Chest,
            s.TurnClip, s.Turns);
        return true;
    }

    public bool TrySearch(Entity fighter, out MotionMatchSearch search)
    {
        search = default;
        if (!TryState(fighter, out var s, out _) || runtime.Diagnostics.Last(s.Player) is not { } r) return false;
        var db = runtime.Database;
        int frame = r.Match.Frame;
        search = new MotionMatchSearch(r.Tick, r.Reason, r.Current, frame, frame >= 0 ? db.Clips[db.ClipOf[frame]].LibraryClip : -1,
            frame >= 0 ? db.FrameInClip(frame) : -1, r.Match.Cost, r.Match.ContinuationCost, r.Match.Blocks, r.Match.Frames, r.Switched,
            r.Microseconds, r.MaxMicroseconds);
        return true;
    }

    public float GroupCost(Entity fighter, int group)
    {
        if ((uint)group >= (uint)GroupCount || !TryState(fighter, out var s, out _) || runtime.Diagnostics.Last(s.Player) is not { } r) return float.NaN;
        var costs = r.Match.Groups;
        return ((ReadOnlySpan<float>)costs)[group];
    }

    public ReadOnlySpan<MotionTrajectoryPoint> Desired(Entity fighter)
    {
        if (!TryState(fighter, out var s, out _) || runtime.Diagnostics.Last(s.Player) is not { } r) return [];
        return Trajectory(r.Query, r.Root);
    }

    public ReadOnlySpan<MotionTrajectoryPoint> Matched(Entity fighter)
    {
        if (!TryState(fighter, out var s, out _) || runtime.Diagnostics.Last(s.Player) is not { Match.Frame: >= 0 } r) return [];
        runtime.Database.RawFeatures(r.Match.Frame, _raw);
        return Trajectory(_raw, r.Root);
    }

    /// <summary>
    /// Траектория признаков (сырые единицы, система корня <paramref name="root"/>) в мире: прошлое (от дальнего), «сейчас» — сам корень,
    /// будущее — положения и курсы групп траектории схемы. Курс прошлого в признаках не записан — курс корня.
    /// </summary>
    private ReadOnlySpan<MotionTrajectoryPoint> Trajectory(ReadOnlySpan<float> raw, Pose2 root)
    {
        var schema = runtime.Database.Schema;
        var groups = schema.Groups;
        int n = 0;
        int past = schema.Find(FeatureKind.PastPosition);
        if (past >= 0)
        {
            var times = schema.PastTimes;
            int o = groups[past].Offset;
            for (int k = times.Length - 1; k >= 0; k--)
                _points[n++] = new MotionTrajectoryPoint(times[k], root.ToParent(new Vector3(raw[o + 2 * k], 0, raw[o + 2 * k + 1])), root.Yaw);
        }
        _points[n++] = new MotionTrajectoryPoint(0, root.Position, root.Yaw);
        int position = schema.Find(FeatureKind.TrajectoryPosition), direction = schema.Find(FeatureKind.TrajectoryDirection);
        if (position >= 0)
        {
            var times = schema.FutureTimes;
            int o = groups[position].Offset, d = direction >= 0 ? groups[direction].Offset : -1;
            for (int k = 0; k < times.Length; k++)
            {
                float yaw = d >= 0 ? root.Yaw + MathF.Atan2(raw[d + 2 * k], raw[d + 2 * k + 1]) : root.Yaw;
                _points[n++] = new MotionTrajectoryPoint(times[k], root.ToParent(new Vector3(raw[o + 2 * k], 0, raw[o + 2 * k + 1])), yaw);
            }
        }
        return _points.AsSpan(0, n);
    }

    public ReadOnlySpan<MotionTrajectoryPoint> History(Entity fighter)
    {
        if (!TryState(fighter, out var s, out _) || runtime.HistoryAt(s.Player.Index) is not { Count: > 0 } h) return [];
        if (_points.Length < h.Count) Array.Resize(ref _points, h.Count);
        long last = h[h.Count - 1].Tick;
        float dt = h.TickSeconds;
        for (int k = 0; k < h.Count; k++)
        {
            var r = h[k];
            _points[k] = new MotionTrajectoryPoint((r.Tick - last) * dt, r.Root.Position, r.Root.Yaw);
        }
        return _points.AsSpan(0, h.Count);
    }

    public MotionMatchMeasure Measure(Entity fighter)
    {
        if (!TryState(fighter, out var s, out _) || _world.Has<Dead>(fighter)) return MotionMatchMeasure.None;
        var history = runtime.HistoryAt(s.Player.Index);
        float dt = _world.Resource<SimTime>().Dt;
        var m = _probe.Measure(s.Player, history, dt, s.Twist, s.TwistBefore);
        var pool = runtime.Pool;
        var pose = _world.TryGet<PoseRef>(fighter, out var p) ? new PoseHandle(p.Index, p.Generation) : PoseHandle.None;
        if (!pool.IsAlive(pose) || !pool.HasPreviousModel(pose)) return m with { FootSlide = float.NaN, SlideJoint = -1, SourceSlide = m.FootSlide };
        var (slide, joint) = _probe.PoseSlide(s.Player, pool.Model(pose), pool.PreviousModel(pose), history, dt);
        return m with { FootSlide = slide, SlideJoint = joint, SourceSlide = m.FootSlide };
    }
}
