using System.Numerics;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionFixtures;
using static Sword.Engine.Animation.Tests.MotionSearchFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Запрос: на ровном ходу совпадает с признаками кадра такого же синтетического цикла (траектория модели при 120 Гц против
/// графа клипа при 30 кадрах/с) и находит этот кадр; масштаб данных — запрос 1,6 м/с в масштабе 1,25 = запрос 2 м/с; ω — шаг
/// модели, зажатый пределом; прошлое — из истории «не позже тика», короткая и пустая история — назад по скорости; суставы —
/// признаки текущего кадра как есть (без кадра — средние); <see cref="MotionQuery.DataYawRate"/> — 99-й перцентиль, не меньше 1;
/// 0 Б.
/// </summary>
public sealed class MotionQueryTests
{
    private const float Tick = 1f / 120;
    private const long Now = 1000;

    private static readonly ClipSpec[] Cycles =
    [
        new("Walk_Fwd_Loop", 31, true, 1.5f),
        new("Walk_Left_Loop", 25, true, 2f, MathF.PI / 2),
        new("Walk_Turn_Loop", 40, true, 1f, 0, 1f),
        new("Walk_Stop", 45, false, 1.2f),
    ];

    /// <summary>История ровного хода: корень тика t — нынешний, сдвинутый назад на скорость × (Now − t) тиков.</summary>
    private static RootHistory Steady(Pose2 root, Vector3 velocity, int ticks = 60)
    {
        var history = new RootHistory(Tick, 1f / 3);
        for (long t = Now - ticks; t < Now; t++)
        {
            float back = (Now - t) * Tick;
            history.Add(t, new Pose2(root.X - velocity.X * back, root.Z - velocity.Z * back, root.Yaw));
        }
        return history;
    }

    [Theory]
    [InlineData(0, 0.7f)]
    [InlineData(0, -2.5f)]
    [InlineData(1, 0.7f)]
    [InlineData(1, 3.1f)]
    public void SteadyWalk_QueryMatchesFrameOfSameSyntheticCycle(int clip, float yaw)
    {
        var lib = Library(Cycles);
        var schema = Schema();
        var db = Walked(lib, schema);
        var spec = Cycles[clip];
        int frame = db.Clips[clip].Start + 12;
        var velocity = Pose2.Rotate(new Vector3(MathF.Sin(spec.Heading), 0, MathF.Cos(spec.Heading)) * spec.Speed, yaw);
        var root = new Pose2(3, -2, yaw);
        var query = new MotionQuery(db);
        // цель — та же скорость, курс — тот же через оборот (кратчайший путь — стоять)
        var model = new TrajectoryModel(14, 12, velocity, yaw + MathF.Tau);
        query.Fill(new TrajectoryState(root, velocity), model, Steady(root, velocity), Now, frame, 1, 1, query.DataYawRate);

        var row = db.FeatureRow(frame);
        foreach (var g in schema.Groups)
            for (int i = g.Offset; i < g.End; i++)
                if (MotionSchema.IsJoint(g.Kind)) AssertSameBits(row[i], query.Values[i], $"{g.Name}[{i - g.Offset}]");
                else Assert.True(MathF.Abs(row[i] - query.Values[i]) < 1e-3f, $"{g.Name}[{i - g.Offset}]: кадр {row[i]}, запрос {query.Values[i]}");
        Assert.Equal(frame, new MotionMatcher(db).Search(query.Values, schema.DefaultWeights, -1, 0, 0).Frame);
    }

    [Fact]
    public void DataScale_QueryAt16InScale125_EqualsQueryAt2()
    {
        var lib = MotionFixtures.Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        float[] Fill(float speed, float scale)
        {
            const float yaw = 1.1f;
            var direction = Pose2.Rotate(Vector3.Normalize(new Vector3(0.6f, 0, 0.8f)), yaw);   // вперёд-влево
            var goal = Pose2.Rotate(-Vector3.UnitX, yaw) * speed;   // хочет вправо и поворачивает
            var root = new Pose2(-4, 7, yaw);
            var query = new MotionQuery(db);
            query.Fill(new TrajectoryState(root, direction * speed), new TrajectoryModel(14, 12, goal, yaw + 0.8f),
                Steady(root, direction * speed), Now, 9, scale, scale, 3);
            return query.Values.ToArray();
        }
        var scaled = Fill(1.6f, 1.25f);
        var data = Fill(2, 1);
        for (int i = 0; i < data.Length; i++) Assert.True(MathF.Abs(data[i] - scaled[i]) < 1e-4f, $"признак {i}: {data[i]} против {scaled[i]}");
    }

    [Theory]
    [InlineData(2.5f, 1)]
    [InlineData(-2.5f, -1)]
    [InlineData(4f, -1)]   // +4 рад — короче через −2,28
    [InlineData(-4f, 1)]
    public void YawRate_IsNextModelStep_ClampedByLimit(float turn, int sign)
    {
        var lib = MotionFixtures.Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var query = new MotionQuery(db);
        int w = schema.Groups[schema.Find(FeatureKind.RootYawRate)].Offset;
        var state = new TrajectoryState(new Pose2(0, 0, 0.3f), Vector3.Zero);
        var model = new TrajectoryModel(14, 12, Vector3.Zero, 0.3f + turn);
        var history = new RootHistory(Tick, 1f / 3);

        query.Fill(state, model, history, Now, -1, 1, 1, 1.5f);
        Assert.Equal(sign * 1.5f, query.Raw[w]);
        query.Fill(state, model, history, Now, -1, 1, 1, float.PositiveInfinity);
        Assert.Equal(model.YawRate(state, Tick), query.Raw[w]);
        Assert.Equal(sign * 12f, query.Raw[w], 1e-3f);
        // поворот меньше шага — ω без зажима, в пределах
        query.Fill(state, model with { GoalYaw = 0.3f + sign * 0.01f }, history, Now, -1, 1, 1, 1.5f);
        Assert.Equal(sign * 0.01f / Tick, query.Raw[w], 1e-2f);
        Assert.True(MathF.Abs(query.Raw[w]) <= 1.5f);
    }

    [Fact]
    public void Past_FromHistoryNotLaterThanTick_ShortOrEmptyHistoryGoesBackByVelocity()
    {
        var lib = MotionFixtures.Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var query = new MotionQuery(db);
        int p = schema.Groups[schema.Find(FeatureKind.PastPosition)].Offset;
        var root = new Pose2(1, 2, 0.5f);
        var velocity = Pose2.Rotate(new Vector3(0.3f, 0, 1.5f), 0.5f);
        var state = new TrajectoryState(root, velocity);
        var model = new TrajectoryModel(14, 12, velocity, 0.5f);
        const float kv = 1.3f;
        long past = Now - 40;   // −1/3 с при 120 Гц
        void Expect(Pose2 at, string what)
        {
            var local = Pose2.Relative(root, at);
            Assert.True(MathF.Abs(local.X * kv - query.Raw[p]) < 1e-5f && MathF.Abs(local.Z * kv - query.Raw[p + 1]) < 1e-5f,
                $"{what}: ({query.Raw[p]}, {query.Raw[p + 1]}), а ждали ({local.X * kv}, {local.Z * kv})");
        }

        var history = new RootHistory(Tick, 1f / 3);
        var a = new Pose2(-3, 1, 0.2f);
        var b = new Pose2(-2, 1.5f, 0.4f);
        history.Add(past - 10, a);
        history.Add(past - 2, b);
        history.Add(past + 3, new Pose2(9, 9, 9));
        query.Fill(state, model, history, Now, -1, kv, 1, 3);
        Expect(b, "последняя не позже");

        history.Clear();
        history.Add(past + 10, a);
        history.Add(past + 20, b);
        query.Fill(state, model, history, Now, -1, kv, 1, 3);
        Expect(new Pose2(a.X - velocity.X * 10 * Tick, a.Z - velocity.Z * 10 * Tick, a.Yaw), "короткая");

        history.Clear();
        query.Fill(state, model, history, Now, -1, kv, 1, 3);
        Expect(new Pose2(root.X - velocity.X * 40 * Tick, root.Z - velocity.Z * 40 * Tick, root.Yaw), "пустая");
        Assert.Equal(-1.5f / 3 * kv, query.Raw[p + 1], 1e-5f);
    }

    [Fact]
    public void Joints_AreCurrentFrameFeaturesAsIs_MeansWithoutFrame_RestNormalized()
    {
        var lib = MotionFixtures.Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var query = new MotionQuery(db);
        var state = new TrajectoryState(new Pose2(5, 1, -0.4f), new Vector3(1, 0, 1));
        var model = new TrajectoryModel(10, 8, new Vector3(0, 0, 2), 1);
        var history = Steady(state.Root, state.Velocity);
        var raw = new float[db.FeatureCount];
        db.RawFeatures(17, raw);
        foreach (int current in new[] { 17, -1 })
        {
            query.Fill(state, model, history, Now, current, 1.1f, 0.9f, query.DataYawRate);
            foreach (var g in schema.Groups)
                for (int i = g.Offset; i < g.End; i++)
                {
                    if (!MotionSchema.IsJoint(g.Kind)) AssertSameBits((query.Raw[i] - db.Mean[i]) / db.Std[i], query.Values[i], $"{g.Name} нормализован");
                    else if (current < 0)
                    {
                        AssertSameBits(0, query.Values[i], $"{g.Name} без кадра");
                        AssertSameBits(db.Mean[i], query.Raw[i], $"{g.Name} без кадра, сырой");
                    }
                    else
                    {
                        AssertSameBits(db.FeatureRow(17)[i], query.Values[i], $"{g.Name} кадра 17");
                        AssertSameBits(raw[i], query.Raw[i], $"{g.Name} кадра 17, сырой");
                    }
                }
        }
    }

    [Fact]
    public void DataYawRate_Is99thPercentileOfFrames_AtLeastOne()
    {
        var schema = Schema();
        int fc = schema.FeatureCount, w = schema.Groups[schema.Find(FeatureKind.RootYawRate)].Offset;
        var lib = Library(new ClipSpec("Walk_Stop", 101, false, 1));   // 100 искомых
        MotionDatabase WithRates(float step)
        {
            var a = Plain(lib, schema, [0]);
            for (int f = 0; f < 100; f++) a.Features[f * fc + w] = ((f % 2 == 0 ? 1 : -1) * f * step - a.Mean[w]) / a.Std[w];
            return a.Build(lib, schema);
        }
        // |ω| = 0, 0,05 … 4,95: ближний ранг 99 из 100 — 4,9
        Assert.Equal(4.9f, new MotionQuery(WithRates(0.05f)).DataYawRate, 1e-4f);
        Assert.Equal(1f, new MotionQuery(WithRates(0.005f)).DataYawRate);

        // без группы ω — по повороту за интервал графа: 99 искомых, 20 из них поворачивают 3 рад/с
        var noYaw = new MotionSchema([1f / 3, 2f / 3, 1], [-1f / 3],
            [new("trajectory", FeatureKind.TrajectoryPosition, 1, 0.2f), new("joints", FeatureKind.JointPosition, 1, 0.03f, [Pelvis])], [Pelvis]);
        var turning = Library(new ClipSpec("Turn_Slow", 80, false, 1, 0, 0.5f), new ClipSpec("Turn_Fast", 21, false, 1, 0, 3));
        Assert.Equal(3f, new MotionQuery(Plain(turning, noYaw, [0, 1]).Build(turning, noYaw)).DataYawRate, 1e-3f);
        var slow = Library(new ClipSpec("Turn_Slow", 80, false, 1, 0, 0.5f));
        Assert.Equal(1f, new MotionQuery(Plain(slow, noYaw, [0]).Build(slow, noYaw)).DataYawRate);
    }

    [Theory]
    [InlineData("кадр вне базы")]
    [InlineData("кадр меньше −1")]
    [InlineData("масштаб не число")]
    [InlineData("масштаб цели бесконечен")]
    [InlineData("зажим меньше 0")]
    [InlineData("нет истории")]
    public void Fill_RejectsBadArguments(string what)
    {
        var lib = MotionFixtures.Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var query = new MotionQuery(db);
        var state = new TrajectoryState(Pose2.Identity, Vector3.Zero);
        var model = new TrajectoryModel(14, 12, Vector3.UnitZ, 0);
        var history = new RootHistory(Tick, 1f / 3);
        Action act = what switch
        {
            "кадр вне базы" => () => query.Fill(state, model, history, Now, db.FrameCount, 1, 1, 1),
            "кадр меньше −1" => () => query.Fill(state, model, history, Now, -2, 1, 1, 1),
            "масштаб не число" => () => query.Fill(state, model, history, Now, 0, float.NaN, 1, 1),
            "масштаб цели бесконечен" => () => query.Fill(state, model, history, Now, 0, 1, float.PositiveInfinity, 1),
            "зажим меньше 0" => () => query.Fill(state, model, history, Now, 0, 1, 1, -1),
            "нет истории" => () => query.Fill(state, model, null!, Now, 0, 1, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая"),
        };
        Assert.ThrowsAny<ArgumentException>(act);
    }

    [Fact]
    public void Fill_DoesNotAllocate()
    {
        var lib = Library(Cycles);
        var schema = Schema();
        var db = Walked(lib, schema);
        var query = new MotionQuery(db);
        var root = new Pose2(1, 1, 0.2f);
        var velocity = new Vector3(0.5f, 0, 1);
        var history = Steady(root, velocity);
        var model = new TrajectoryModel(14, 12, new Vector3(-1, 0, 2), 1.5f);
        long now = Now;
        int frame = 0;
        long bytes = Allocated(300, () =>
        {
            now++;
            query.Fill(new TrajectoryState(root, velocity), model, history, now, frame++ % db.FrameCount - 1, 1.2f, 1.1f, query.DataYawRate);
        });
        Assert.Equal(0, bytes);
    }
}
