using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>Состояние корня для предсказания: система корня (мир) и скорость (мир, Y = 0), м/с.</summary>
public readonly record struct TrajectoryState(Pose2 Root, Vector3 Velocity);

/// <summary>
/// Модель контроллера корня на тик (точка 8в) — то, чем игра ведёт корень, чтобы будущее траектории запроса предсказывалось
/// той же моделью (одна правда): курс поворачивает к цели <see cref="GoalYaw"/> кратчайшим путём не быстрее
/// <see cref="TurnRate"/> (рад/с), скорость идёт к цели <see cref="GoalVelocity"/> экспоненциально с темпом <see cref="Rate"/>
/// (1/с) — по тикам: <c>v += (цель − v)·min(1, Rate·dt)</c>, затем корень — на новую скорость·dt. Параметры и цели — от
/// вызывающего (в игре — действующая модель контроллера бойца на этот тик, с масштабами действия). Без состояния: шаг и
/// предсказание возвращают новое состояние, исходное не меняется.
/// </summary>
/// <param name="Rate">Темп разгона к целевой скорости, 1/с (≥ 0).</param>
/// <param name="TurnRate">Предел скорости поворота курса, рад/с (≥ 0).</param>
/// <param name="GoalVelocity">Целевая скорость, мир, м/с (Y не учитывается).</param>
/// <param name="GoalYaw">Целевой курс, рад.</param>
public readonly record struct TrajectoryModel(float Rate, float TurnRate, Vector3 GoalVelocity, float GoalYaw)
{
    /// <summary>
    /// Один шаг на <paramref name="dt"/>: курс — на <c>clamp(Wrap(GoalYaw − курс), ±TurnRate·dt)</c>, скорость —
    /// <c>v + (GoalVelocity − v)·min(1, Rate·dt)</c> (Y = 0), корень — на новую скорость·dt.
    /// </summary>
    public TrajectoryState Step(in TrajectoryState state, float dt) => throw new NotImplementedException("8в: модель предсказания — TrajectoryModel");

    /// <summary>
    /// Состояние через <c>n = round(t / dt)</c> шагов по <paramref name="dt"/> (t ≥ 0) — то же, что n вызовов <see cref="Step"/>
    /// (в пределах округления float); <paramref name="state"/> не меняется. Будущее траектории запроса.
    /// </summary>
    public TrajectoryState Predict(in TrajectoryState state, float t, float dt) => throw new NotImplementedException("8в");

    /// <summary>Угловая скорость курса на следующем шаге, рад/с: <c>clamp(Wrap(GoalYaw − курс), ±TurnRate·dt) / dt</c>.</summary>
    public float YawRate(in TrajectoryState state, float dt) => throw new NotImplementedException("8в");
}
