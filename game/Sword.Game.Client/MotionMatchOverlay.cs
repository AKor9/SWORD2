using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Оверлей Motion Matching игрока (<c>mm</c>) по <see cref="IMotionMatchView"/>: желаемая траектория, траектория найденного
/// кадра, история корня, суставы контакта стоп. Траектории — на рельефе (+ <see cref="Lift"/>) в мире тика последнего
/// поиска, стопы — в показанном мире бойца. Снимается в Extract только когда нужен; кадр без выделений. Вида нет — пусто.
/// </summary>
public sealed class MotionMatchOverlay(Func<float, float, float> height)
{
    /// <summary>Цвета (<c>0xAABBGGRR</c>): желаемая — голубая, найденный кадр — оранжевая, история — серая, стопа на земле — зелёная, в воздухе — тёмная.</summary>
    public const uint DesiredColour = 0xFFFFE040, MatchedColour = 0xFF2090FF, HistoryColour = 0xD0C0C0C0, GroundColour = 0xFF40FF40,
        AirColour = 0xA0606060;

    /// <summary>Подъём траекторий над рельефом, м; длина засечки курса точки траектории, м.</summary>
    public const float Lift = 0.03f, Tick = 0.25f;

    private MotionTrajectoryPoint[] _desired = new MotionTrajectoryPoint[16], _matched = new MotionTrajectoryPoint[16], _history = new MotionTrajectoryPoint[64];
    private Vector3[] _feet = new Vector3[8];
    private bool[] _ground = new bool[8];
    private int _desiredCount, _matchedCount, _historyCount, _feetCount;
    private int _clip = -1;

    /// <summary>Игрок кадра на Motion Matching и оверлей снят (<see cref="Extract"/>).</summary>
    public bool Available { get; private set; }

    /// <summary>Точек траекторий (желаемая, найденный кадр, история) и суставов контакта, снятых кадром.</summary>
    public int Points => _desiredCount + _matchedCount + _historyCount;
    public int Feet => _feetCount;

    /// <summary>Из них на земле по разметке кадра.</summary>
    public int FeetOnGround { get; private set; }

    /// <summary>Клип библиотеки игрока (подпись слоя); −1 — нет.</summary>
    public int Clip => _clip;

    /// <summary>Ничего не показывать (оверлей не нужен кадру).</summary>
    public void Clear()
    {
        Available = false;
        _desiredCount = _matchedCount = _historyCount = _feetCount = FeetOnGround = 0;
        _clip = -1;
    }

    /// <summary>Снять оверлей игрока кадра: траектории — из вида, суставы контакта — из показанной позы игрока (<see cref="ArenaView.Pose"/>).</summary>
    public void Extract(WorldReader world, ArenaView view)
    {
        Clear();
        if (!world.TryView<IMotionMatchView>(out var mm) || view.Player is not { } player || !mm.TryStatus(player.Entity, out var status)) return;
        Available = true;
        _clip = status.Clip;
        _desiredCount = Copy(mm.Desired(player.Entity), ref _desired);
        _matchedCount = Copy(mm.Matched(player.Entity), ref _matched);
        _historyCount = Copy(mm.History(player.Entity), ref _history);
        var pose = view.Pose(player);
        int n = mm.ContactJointCount;
        if (_feet.Length < n)
        {
            _feet = new Vector3[n];
            _ground = new bool[n];
        }
        for (int k = 0; k < n; k++)
        {
            int joint = mm.ContactJoint(k);
            if ((uint)joint >= (uint)pose.Length) continue;
            _feet[_feetCount] = Vector3.Transform(pose[joint].Translation, player.Corpse ? Matrix4x4.Identity : player.World);
            bool ground = (status.Contacts >> k & 1) != 0;
            _ground[_feetCount++] = ground;
            if (ground) FeetOnGround++;
        }
    }

    private static int Copy(ReadOnlySpan<MotionTrajectoryPoint> from, ref MotionTrajectoryPoint[] into)
    {
        if (into.Length < from.Length) Array.Resize(ref into, Math.Max(into.Length * 2, from.Length));
        from.CopyTo(into);
        return from.Length;
    }

    /// <summary>Нарисовать снятое; вернёт, сколько примитивов (отрезков и шариков).</summary>
    public int Draw(DebugDraw debug)
    {
        if (!Available) return 0;
        int drawn = Polyline(debug, _history.AsSpan(0, _historyCount), HistoryColour, ticks: false);
        drawn += Polyline(debug, _desired.AsSpan(0, _desiredCount), DesiredColour, ticks: true);
        drawn += Polyline(debug, _matched.AsSpan(0, _matchedCount), MatchedColour, ticks: true);
        for (int k = 0; k < _feetCount; k++)
        {
            debug.Capsule(_feet[k], Quaternion.Identity, _ground[k] ? 0.045f : 0.025f, 0, _ground[k] ? GroundColour : AirColour);
            drawn++;
        }
        return drawn;
    }

    /// <summary>Отрезки по точкам траектории на рельефе; с засечками курса и шариками точек будущего и прошлого.</summary>
    private int Polyline(DebugDraw debug, ReadOnlySpan<MotionTrajectoryPoint> points, uint colour, bool ticks)
    {
        int drawn = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var p = OnGround(points[i].Position);
            if (i > 0)
            {
                debug.Line(OnGround(points[i - 1].Position), p, colour);
                drawn++;
            }
            if (!ticks) continue;
            float yaw = points[i].Yaw;
            debug.Line(p, p + new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)) * Tick, colour);
            debug.Capsule(p, Quaternion.Identity, points[i].Time == 0 ? 0.04f : 0.025f, 0, colour);
            drawn += 2;
        }
        return drawn;
    }

    private Vector3 OnGround(Vector3 p) => p with { Y = height(p.X, p.Z) + Lift };
}
