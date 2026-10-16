using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// <c>locomotion.mm</c> (лёгкий, после End): у живого бойца на MM проигрыватель живой и свой, кадр — кадр базы, доля —
/// в [0, 1), все числа конечны и в пределах ручек (темп, потолок поворота, подгонка, выравнивание груди), клип поворота —
/// −1 или клип поворота базы. Бойцы на постройке заново и мёртвые не проверяются.
/// </summary>
public sealed class MotionMatchInvariant(MotionMatchRuntime runtime) : IInvariant
{
    /// <summary>Допуск темпа к пределам ручки.</summary>
    public const float RateTolerance = 1e-5f;

    /// <summary>Допуск расхождения к потолку ручки, рад.</summary>
    public const float TwistTolerance = 1e-5f;

    private ReadQuery? _query;
    private Entity[] _owner = new Entity[4];

    public string Id => "locomotion.mm";
    public InvariantCost Cost => InvariantCost.EveryTick;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        if (!runtime.Ready) return;
        var world = ctx.World;
        _query ??= world.Query().With<Fighter>().Without<Dead>().Read<MotionMatchState>();
        var rules = world.Resource<LocomotionRules>();
        float range = rules.RateRange, max = rules.TurnMax * MathF.PI / 180;
        var players = runtime.Players;
        if (_owner.Length < players.Capacity) Array.Resize(ref _owner, players.Capacity);
        Array.Clear(_owner);
        foreach (var chunk in world.Iterate(_query))
        {
            var entities = chunk.Entities;
            var states = chunk.Read<MotionMatchState>();
            for (int i = 0; i < entities.Length; i++)
            {
                if ((Problem(entities[i], states[i], range) ?? Turn(states[i], max)) is not { } problem) continue;
                violations.Report(entities[i], problem);
                if (violations.Full) return;
            }
        }
    }

    /// <summary>Первое нарушение бойца (тело цикла — свой метод); всё в порядке — null.</summary>
    private string? Problem(Entity e, in MotionMatchState s, float range)
    {
        if (s.Restart) return null;
        var players = runtime.Players;
        if (!players.IsAlive(s.Player)) return $"проигрыватель {s.Player} не жив";
        if (!_owner[s.Player.Index].IsNull) return $"проигрыватель {s.Player} — ещё и у {_owner[s.Player.Index]}";
        _owner[s.Player.Index] = e;
        var st = players.State(s.Player);
        if ((uint)st.Frame >= (uint)runtime.Database.FrameCount) return $"кадр {st.Frame} вне базы (кадров {runtime.Database.FrameCount})";
        if (!(st.Alpha >= 0 && st.Alpha < 1)) return Text($"доля {st.Alpha} вне [0, 1)");
        if (st.Transitions < 0) return $"переходов {st.Transitions}";
        if (!st.RootStep.IsFinite) return $"перенос корня {st.RootStep} не конечен";
        if (!(float.IsFinite(s.Rate) && s.Rate >= 1 - range - RateTolerance && s.Rate <= 1 + range + RateTolerance))
            return Text($"темп {s.Rate} вне 1 ± {range}");
        return Offsets(players.RotationOffsets(s.Player), players.PositionOffsets(s.Player));
    }

    /// <summary>Поворот на месте бойца (не на постройке): всё в порядке — null.</summary>
    private string? Turn(in MotionMatchState s, float max)
    {
        if (s.Restart) return null;
        if (!(float.IsFinite(s.FeetYaw) && float.IsFinite(s.Twist) && float.IsFinite(s.TwistBefore) && float.IsFinite(s.Chest)))
            return Text($"поворот на месте не конечен: курс ног {s.FeetYaw}, расхождение {s.Twist} (прошлое {s.TwistBefore}), грудь {s.Chest}");
        if (MathF.Abs(s.Chest) > GaspMotion.ChestMax + TwistTolerance) return Text($"выравнивание груди {s.Chest} рад за пределом {GaspMotion.ChestMax}");
        if (MathF.Abs(s.Twist) > max + TwistTolerance) return Text($"расхождение курса корня и ног {s.Twist} рад за потолком {max}");
        if (!(s.TurnWarp >= 0 && s.TurnWarp <= GaspMotion.TurnWarpMax)) return Text($"подгонка поворота {s.TurnWarp} вне 0…{GaspMotion.TurnWarpMax}");
        if (s.TurnClip >= 0 && !runtime.Turn.IsTurnClip(runtime.Database.FindClip(s.TurnClip)))
            return $"клип поворота {s.TurnClip} — не поворот на месте базы";
        return null;
    }

    private string? Offsets(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos)
    {
        var names = runtime.Database.Library.Skeleton.JointNames;
        for (int j = 0; j < rot.Length; j++)
        {
            var q = rot[j];
            if (!(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)))
                return Text($"смещение поворота сустава {names[j]} не конечно: {q}");
            var p = pos[j];
            if (!(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)))
                return Text($"смещение позиции сустава {names[j]} не конечно: {p}");
        }
        return null;
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
