using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>Состояние корня для предсказания: система корня (мир) и скорость (мир, Y = 0), м/с.</summary>
public readonly record struct TrajectoryState(Pose2 Root, Vector3 Velocity);

/// <summary>
/// Модель контроллера корня на тик — та же, что ведёт корень в игре (одна правда для предсказания): курс к <see cref="GoalYaw"/>
/// не быстрее <see cref="TurnRate"/>, скорость к <see cref="GoalVelocity"/>: <c>v += (цель − v)·min(1, Rate·dt)</c>. Без
/// состояния; курс не заворачивается (цепочка шагов непрерывна). Память не выделяет.
/// </summary>
/// <param name="Rate">Темп разгона к целевой скорости, 1/с (≥ 0).</param>
/// <param name="TurnRate">Предел скорости поворота курса, рад/с (≥ 0).</param>
/// <param name="GoalVelocity">Целевая скорость, мир, м/с (Y не учитывается).</param>
/// <param name="GoalYaw">Целевой курс, рад.</param>
public readonly record struct TrajectoryModel(float Rate, float TurnRate, Vector3 GoalVelocity, float GoalYaw)
{
    /// <summary>
    /// Шаг на <paramref name="dt"/>: курс — на <c>clamp(IEEERemainder(GoalYaw − курс, 2π), ±TurnRate·dt)</c>, скорость — к цели,
    /// корень — на новую скорость·dt. Выражения и порядок — как у контроллера игры: побитово тот же шаг, в том числе на полуобороте.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">dt не больше 0 или не конечен, темп или предел поворота меньше 0 или не число.</exception>
    public TrajectoryState Step(in TrajectoryState state, float dt)
    {
        Check(dt);
        return Advance(state, TurnRate * dt, MathF.Min(1, Rate * dt), Goal, dt);
    }

    /// <summary>Состояние через <c>round(t / dt)</c> шагов — побитово то же, что столько вызовов <see cref="Step"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">t меньше 0 или не число, dt не больше 0, темп или предел поворота меньше 0.</exception>
    public TrajectoryState Predict(in TrajectoryState state, float t, float dt)
    {
        Check(dt);
        int n = Steps(t, dt, nameof(t));
        float turn = TurnRate * dt, blend = MathF.Min(1, Rate * dt);
        var goal = Goal;
        var s = state;
        for (int i = 0; i < n; i++) s = Advance(s, turn, blend, goal, dt);
        return s;
    }

    /// <summary>
    /// Предсказание в несколько моментов одним проходом: <paramref name="into"/>[i] — побитово как
    /// <see cref="Predict(in TrajectoryState, float, float)"/> с <c>times[i]</c>. Моменты — по неубыванию. Память не выделяет.
    /// </summary>
    /// <exception cref="ArgumentException">Длины не сходятся, моменты убывают, момент меньше 0, dt не больше 0, темп или предел поворота меньше 0.</exception>
    public void Predict(in TrajectoryState state, ReadOnlySpan<float> times, float dt, Span<TrajectoryState> into)
    {
        if (into.Length != times.Length)
            throw new ArgumentException($"моментов {times.Length}, а мест под итог {into.Length}", nameof(into));
        Check(dt);
        float turn = TurnRate * dt, blend = MathF.Min(1, Rate * dt);
        var goal = Goal;
        var s = state;
        for (int k = 0, done = 0; k < times.Length; k++)
        {
            int n = Steps(times[k], dt, nameof(times));
            if (n < done) throw new ArgumentException($"моменты убывают: {k}-й — {times[k]} с", nameof(times));
            for (; done < n; done++) s = Advance(s, turn, blend, goal, dt);
            into[k] = s;
        }
    }

    /// <summary>Угловая скорость курса на следующем шаге, рад/с: <c>clamp(IEEERemainder(GoalYaw − курс, 2π), ±TurnRate·dt) / dt</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">dt не больше 0 или не конечен, темп или предел поворота меньше 0 или не число.</exception>
    public float YawRate(in TrajectoryState state, float dt)
    {
        Check(dt);
        return Turn(state.Root.Yaw, TurnRate * dt) / dt;
    }

    private Vector3 Goal => new(GoalVelocity.X, 0, GoalVelocity.Z);

    /// <summary>Поворот курса за шаг — выражение контроллера игры: остаток <c>IEEERemainder</c> (в [−π, π]), зажатый пределом.</summary>
    private float Turn(float yaw, float turn) => Math.Clamp(MathF.IEEERemainder(GoalYaw - yaw, MathF.Tau), -turn, turn);

    private TrajectoryState Advance(in TrajectoryState state, float turn, float blend, Vector3 goal, float dt)
    {
        var root = state.Root;
        float yaw = root.Yaw + Turn(root.Yaw, turn);
        var velocity = state.Velocity;
        velocity += (goal - velocity) * blend;
        velocity.Y = 0;
        return new TrajectoryState(new Pose2(root.X + velocity.X * dt, root.Z + velocity.Z * dt, yaw), velocity);
    }

    private void Check(float dt)
    {
        if (!(dt > 0) || !float.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt), dt, "шаг — конечный и больше 0");
        if (!(Rate >= 0)) throw new ArgumentOutOfRangeException(nameof(Rate), Rate, "темп разгона — не меньше 0");
        if (!(TurnRate >= 0)) throw new ArgumentOutOfRangeException(nameof(TurnRate), TurnRate, "предел поворота — не меньше 0");
    }

    private static int Steps(float t, float dt, string name)
    {
        if (!(t >= 0) || !float.IsFinite(t)) throw new ArgumentOutOfRangeException(name, t, "момент — конечный и не меньше 0");
        return (int)MathF.Round(t / dt);
    }
}
