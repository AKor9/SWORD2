using System.Numerics;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionSearchFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Предсказание — ровно round(t / dt) шагов той же модели (побитово); курс к цели кратчайшим путём не быстрее предела
/// (полуоборот — по <c>IEEERemainder</c>, как у контроллера игры); без выделений.
/// </summary>
public sealed class TrajectoryModelTests
{
    private static readonly float[] Ticks = [1f / 30, 1f / 60, 1f / 120, 1f / 240];

    private static TrajectoryState RandomState(Random rng) => new(
        new Pose2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 40 - 20)),
        new Vector3((float)(rng.NextDouble() * 10 - 5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() * 10 - 5)));

    private static TrajectoryModel RandomModel(Random rng) => new((float)(rng.NextDouble() * 30), (float)(rng.NextDouble() * 15),
        new Vector3((float)(rng.NextDouble() * 12 - 6), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 12 - 6)),
        (float)(rng.NextDouble() * 40 - 20));

    [Fact]
    public void Predict_IsExactlyRoundedNumberOfSteps()
    {
        var rng = new Random(8);
        for (int c = 0; c < 400; c++)
        {
            var state = RandomState(rng);
            var model = RandomModel(rng);
            float dt = Ticks[c % Ticks.Length];
            float t = c % 5 switch { 0 => 0, 1 => 1f / 3, 2 => 2f / 3, 3 => 1, _ => (float)(rng.NextDouble() * 1.5) };
            int n = (int)MathF.Round(t / dt);
            var stepped = state;
            for (int i = 0; i < n; i++) stepped = model.Step(stepped, dt);
            AssertSameBits(stepped, model.Predict(state, t, dt), $"случай {c}: t = {t}, dt = {dt}, n = {n}");
        }
    }

    [Fact]
    public void Predict_ManyTimesInOnePass_EqualsSinglePredictions()
    {
        var rng = new Random(9);
        float[] times = [0, 1f / 3, 1f / 3, 2f / 3, 1, 1.25f];
        var into = new TrajectoryState[times.Length];
        for (int c = 0; c < 200; c++)
        {
            var state = RandomState(rng);
            var model = RandomModel(rng);
            float dt = Ticks[c % Ticks.Length];
            model.Predict(state, times, dt, into);
            for (int k = 0; k < times.Length; k++) AssertSameBits(model.Predict(state, times[k], dt), into[k], $"случай {c}, момент {times[k]}");
        }
    }

    [Fact]
    public void Predict_RoundsStepsToNearest_AndZeroIsState()
    {
        var state = new TrajectoryState(new Pose2(1, 2, 0.3f), new Vector3(1, 0, 2));
        var model = new TrajectoryModel(14, 12, new Vector3(3, 0, -1), 1.5f);
        const float dt = 1f / 120;
        Assert.Equal(state, model.Predict(state, 0, dt));
        Assert.Equal(state, model.Predict(state, 0.4f * dt, dt));
        Assert.Equal(model.Step(state, dt), model.Predict(state, 0.6f * dt, dt));
        Assert.Equal(model.Step(model.Step(state, dt), dt), model.Predict(state, 2.3f * dt, dt));
    }

    [Fact]
    public void YawRate_IsClampedByTurnRate_AndMatchesTheStep()
    {
        var rng = new Random(10);
        for (int c = 0; c < 500; c++)
        {
            var state = RandomState(rng);
            var model = RandomModel(rng);
            float dt = Ticks[c % Ticks.Length];
            float rate = model.YawRate(state, dt);
            Assert.True(MathF.Abs(rate) <= model.TurnRate * (1 + 1e-5f), $"случай {c}: ω {rate} при пределе {model.TurnRate}");
            float turned = model.Step(state, dt).Root.Yaw - state.Root.Yaw;
            Assert.Equal(turned, rate * dt, 1e-4f);
            // не упирается в предел — ровно доворот до цели
            float left = Pose2.Wrap(model.GoalYaw - state.Root.Yaw);
            if (MathF.Abs(left) < model.TurnRate * dt) Assert.Equal(left, rate * dt, 1e-5f);
        }
    }

    [Theory]
    [InlineData(3.0f, -3.0f, 1)]         // через π: короче вверх
    [InlineData(-3.0f, 3.0f, -1)]        // через −π: короче вниз
    [InlineData(0.5f, 0.5f + 4, -1)]     // +4 рад — это −2,28
    [InlineData(0.5f, 0.5f - 4, 1)]
    [InlineData(1.0f, 1.0f + 6 * MathF.PI, 0)]   // та же цель через три оборота — не крутится
    // ровно полуоборот — как у контроллера игры (остаток IEEERemainder): разница −π — к −, +π — к +
    [InlineData(0f, -MathF.PI, -1)]
    [InlineData(MathF.PI, 0f, -1)]
    [InlineData(0f, MathF.PI, 1)]
    [InlineData(-MathF.PI, 0f, 1)]
    public void Step_TurnsTowardGoalTheShortestWay(float yaw, float goal, int sign)
    {
        var model = new TrajectoryModel(14, 12, Vector3.Zero, goal);
        var state = new TrajectoryState(new Pose2(0, 0, yaw), Vector3.Zero);
        const float dt = 1f / 120;
        float first = model.Step(state, dt).Root.Yaw - yaw;
        if (sign == 0) Assert.True(MathF.Abs(first) < 1e-5f, $"повернул на {first}");
        else Assert.Equal(sign, MathF.Sign(first));
        // доходит до цели (по модулю 2π) не дальше, чем за |путь| / предел, и дальше стоит
        var s = state;
        for (int i = 0; i < 120; i++) s = model.Step(s, dt);
        Assert.True(MathF.Abs(Pose2.Wrap(goal - s.Root.Yaw)) < 1e-4f, $"курс {s.Root.Yaw}, цель {goal}");
        Assert.True(MathF.Abs(s.Root.Yaw - yaw) <= MathF.PI + 1e-4f, "повернул дальше полуоборота — не кратчайшим путём");
    }

    [Fact]
    public void Step_VelocityGoesToGoalByTicks_RootMovesOnNewVelocity_YIgnored()
    {
        var model = new TrajectoryModel(14, 12, new Vector3(0, 5, 3), 0);
        var state = new TrajectoryState(new Pose2(2, -1, 0), new Vector3(1, 2, 0));
        const float dt = 1f / 120;
        float a = MathF.Min(1, 14 * dt);
        var one = model.Step(state, dt);
        Assert.Equal(0, one.Velocity.Y);
        Assert.Equal(1 + (0 - 1) * a, one.Velocity.X, 1e-6f);
        Assert.Equal(0 + (3 - 0) * a, one.Velocity.Z, 1e-6f);
        Assert.Equal(2 + one.Velocity.X * dt, one.Root.X, 1e-6f);
        Assert.Equal(-1 + one.Velocity.Z * dt, one.Root.Z, 1e-6f);
        // n шагов — геометрическая прогрессия к цели
        var s = state;
        for (int n = 1; n <= 60; n++)
        {
            s = model.Step(s, dt);
            float k = MathF.Pow(1 - a, n);
            Assert.Equal(0 + (1 - 0) * k, s.Velocity.X, 1e-5f);
            Assert.Equal(3 + (0 - 3) * k, s.Velocity.Z, 1e-5f);
        }
        // темп·dt ≥ 1 — цель за один шаг
        var fast = new TrajectoryModel(200, 12, new Vector3(-2, 7, 4), 0).Step(state, dt);
        Assert.Equal(new Vector3(-2, 0, 4), fast.Velocity);
    }

    [Theory]
    [InlineData("t меньше 0")]
    [InlineData("t не число")]
    [InlineData("dt ноль")]
    [InlineData("моменты убывают")]
    [InlineData("мест не столько")]
    [InlineData("dt моментов ноль")]
    [InlineData("шаг: dt ноль")]
    [InlineData("ω: dt не число")]
    [InlineData("темп меньше 0")]
    [InlineData("предел поворота не число")]
    public void RejectsBadArguments(string what)
    {
        var model = new TrajectoryModel(14, 12, Vector3.UnitZ, 0);
        var state = new TrajectoryState(Pose2.Identity, Vector3.Zero);
        Action act = what switch
        {
            "t меньше 0" => () => model.Predict(state, -0.1f, 0.01f),
            "t не число" => () => model.Predict(state, float.NaN, 0.01f),
            "dt ноль" => () => model.Predict(state, 1, 0),
            "моменты убывают" => () => model.Predict(state, [0.5f, 0.2f], 0.01f, new TrajectoryState[2]),
            "мест не столько" => () => model.Predict(state, [0.5f, 1], 0.01f, new TrajectoryState[3]),
            "dt моментов ноль" => () => model.Predict(state, [0.5f], 0, new TrajectoryState[1]),
            "шаг: dt ноль" => () => model.Step(state, 0),
            "ω: dt не число" => () => model.YawRate(state, float.NaN),
            "темп меньше 0" => () => (model with { Rate = -1 }).Predict(state, 1, 0.01f),
            "предел поворота не число" => () => (model with { TurnRate = float.NaN }).Step(state, 0.01f),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая"),
        };
        Assert.ThrowsAny<ArgumentException>(act);
    }

    [Fact]
    public void StepPredictYawRate_DoNotAllocate()
    {
        var model = new TrajectoryModel(14, 12, new Vector3(1, 0, 4), 2);
        var state = new TrajectoryState(new Pose2(1, 2, 3), new Vector3(0, 0, 1));
        float[] times = [1f / 3, 2f / 3, 1];
        var into = new TrajectoryState[3];
        float sink = 0;
        long bytes = Allocated(200, () =>
        {
            var s = model.Step(state, 1f / 120);
            s = model.Predict(s, 1, 1f / 120);
            model.Predict(s, times, 1f / 120, into);
            sink += model.YawRate(s, 1f / 120) + into[2].Root.X;
        });
        Assert.Equal(0, bytes);
        Assert.True(float.IsFinite(sink));
    }
}
