using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Запрос поиска по схеме базы в системе корня <c>state.Root</c>: будущее — предсказание модели контроллера, прошлое — история
/// корня, скорости корня — из модели, суставы — признаки текущего кадра базы (а не видимой позы: иначе запрос проигрывает
/// продолжению и сбивает фазу шага); видимая поза — только по просьбе (<c>pose</c>): когда позу вёл не проигрыватель (действие
/// всем телом), признаки его кадра устарели. Скорость и прошлое × <c>velocityScale</c>, цель × <c>goalScale</c>, угловая скорость
/// зажата <c>maxYawRate</c>; итог нормализуется. <see cref="Fill"/> память не выделяет.
/// </summary>
public sealed class MotionQuery
{
    private readonly MotionDatabase _db;
    private readonly float[] _values;
    private readonly float[] _raw;
    private readonly TrajectoryState[] _future;

    public MotionQuery(MotionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
        _values = new float[database.FeatureCount];
        _raw = new float[database.FeatureCount];
        _future = new TrajectoryState[database.Schema.FutureTimes.Length];
        DataYawRate = MeasureYawRate(database);
    }

    public MotionDatabase Database => _db;

    /// <summary>Нормализованный запрос последнего <see cref="Fill"/> — вход <see cref="MotionMatcher.Search"/> (до первого — нули).</summary>
    public ReadOnlySpan<float> Values => _values;

    /// <summary>Тот же запрос в сырых единицах (м, м/с, рад/с) — для вида и оверлея траекторий (до первого <see cref="Fill"/> — нули).</summary>
    public ReadOnlySpan<float> Raw => _raw;

    /// <summary>
    /// 99-й перцентиль |ω| корня искомых кадров (не меньше 1 рад/с) — обычный зажим угловой скорости запроса; без группы
    /// <see cref="FeatureKind.RootYawRate"/> — по поворотам интервалов графа.
    /// </summary>
    public float DataYawRate { get; }

    /// <param name="state">Корень (мир) и его скорость сейчас.</param>
    /// <param name="model">Модель контроллера на этот тик (цели и пределы).</param>
    /// <param name="history">История корня; шаг предсказания — её <see cref="RootHistory.TickSeconds"/>. Пуста — прошлое по скорости состояния.</param>
    /// <param name="now">Номер текущего тика (прошлое — тики <c>now + round(t / тик)</c>).</param>
    /// <param name="current">Текущий кадр базы (признаки суставов); −1 — нет: суставы — средние по базе (нормализованные нули).</param>
    /// <param name="maxYawRate">Зажим угловой скорости корня, рад/с (обычно <see cref="DataYawRate"/>).</param>
    /// <param name="pose">
    /// Суставы схемы видимой позы в системе корня (порядок <see cref="MotionSchema.Joints"/>); пусто — признаки суставов текущего
    /// кадра базы.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Кадр вне базы, масштаб не конечен, зажим меньше 0 или не число, суставов видимой позы не столько, сколько в схеме.
    /// </exception>
    public void Fill(in TrajectoryState state, in TrajectoryModel model, RootHistory history, long now, int current, float velocityScale,
        float goalScale, float maxYawRate, PoseJoints pose = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (current < -1 || current >= _db.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(current), current, $"текущий кадр — −1 или кадр базы (кадров {_db.FrameCount})");
        if (!float.IsFinite(velocityScale)) throw new ArgumentOutOfRangeException(nameof(velocityScale), velocityScale, "масштаб не конечен");
        if (!float.IsFinite(goalScale)) throw new ArgumentOutOfRangeException(nameof(goalScale), goalScale, "масштаб не конечен");
        if (!(maxYawRate >= 0)) throw new ArgumentOutOfRangeException(nameof(maxYawRate), maxYawRate, "зажим ω — не меньше 0");
        int joints = _db.Schema.Joints.Length;
        if (!pose.IsEmpty && (pose.Positions.Length != joints || pose.Velocities.Length != joints))
            throw new ArgumentException($"суставов видимой позы {pose.Positions.Length}/{pose.Velocities.Length}, а в схеме {joints}", nameof(pose));

        var schema = _db.Schema;
        float dt = history.TickSeconds;
        var root = state.Root;
        var velocity = state.Velocity * velocityScale;
        if (_future.Length > 0)
        {
            var scaled = model with { GoalVelocity = model.GoalVelocity * goalScale };
            scaled.Predict(new TrajectoryState(root, velocity), schema.FutureTimes, dt, _future);
        }
        var mean = _db.Mean;
        var std = _db.Std;
        var groups = schema.Groups;
        for (int g = 0; g < groups.Length; g++)
        {
            var group = groups[g];
            var raw = _raw.AsSpan(group.Offset, group.Size);
            var values = _values.AsSpan(group.Offset, group.Size);
            bool joint = MotionSchema.IsJoint(group.Kind);
            if (joint && pose.IsEmpty)
            {
                JointFeatures(current, group, raw, values);
                continue;
            }
            if (joint) PoseFeatures(schema, g, pose, raw);
            else switch (group.Kind)
            {
                case FeatureKind.TrajectoryPosition:
                    for (int k = 0; k < _future.Length; k++)
                    {
                        var p = Pose2.Relative(root, _future[k].Root);
                        raw[2 * k] = p.X;
                        raw[2 * k + 1] = p.Z;
                    }
                    break;
                case FeatureKind.TrajectoryDirection:
                    for (int k = 0; k < _future.Length; k++)
                    {
                        float yaw = Pose2.Wrap(_future[k].Root.Yaw - root.Yaw);
                        raw[2 * k] = MathF.Sin(yaw);
                        raw[2 * k + 1] = MathF.Cos(yaw);
                    }
                    break;
                case FeatureKind.PastPosition:
                    var past = schema.PastTimes;
                    for (int k = 0; k < past.Length; k++)
                    {
                        long tick = now + (long)MathF.Round(past[k] / dt);
                        float back = (now - tick) * dt;
                        var fallback = new Pose2(root.X - state.Velocity.X * back, root.Z - state.Velocity.Z * back, root.Yaw);
                        var p = Pose2.Relative(root, history.At(tick, state.Velocity, fallback));
                        raw[2 * k] = p.X * velocityScale;
                        raw[2 * k + 1] = p.Z * velocityScale;
                    }
                    break;
                case FeatureKind.RootVelocity:
                    var local = Pose2.Rotate(velocity, -root.Yaw);
                    raw[0] = local.X;
                    raw[1] = local.Z;
                    break;
                case FeatureKind.RootYawRate:
                    raw[0] = Math.Clamp(model.YawRate(state, dt), -maxYawRate, maxYawRate);
                    break;
                default:
                    throw new InvalidOperationException($"группа «{group.Name}»: вид {group.Kind} запрос не заполняет");
            }
            for (int i = 0; i < values.Length; i++) values[i] = (raw[i] - mean[group.Offset + i]) / std[group.Offset + i];
        }
    }

    /// <summary>
    /// Суставы группы <paramref name="g"/> из видимой позы — те же выражения, что у кадров базы (положение, скорость; курс пары —
    /// «левый − правый», повёрнутое +X → +Z).
    /// </summary>
    private static void PoseFeatures(MotionSchema schema, int g, PoseJoints pose, Span<float> raw)
    {
        var joints = schema.GroupJoints(g);
        var kind = schema.Groups[g].Kind;
        for (int k = 0; k < joints.Length; k++)
        {
            int slot = schema.JointSlot(joints[k]);
            if (kind == FeatureKind.JointPairFacing)
            {
                if (k % 2 != 0) continue;
                var d = pose.Positions[slot] - pose.Positions[schema.JointSlot(joints[k + 1])];
                float x = -d.Z, z = d.X, length = MathF.Sqrt(x * x + z * z);
                raw[k] = length > 1e-6f ? x / length : 0;
                raw[k + 1] = length > 1e-6f ? z / length : 1;
                continue;
            }
            var v = kind == FeatureKind.JointVelocity ? pose.Velocities[slot] : pose.Positions[slot];
            raw[3 * k] = v.X;
            raw[3 * k + 1] = v.Y;
            raw[3 * k + 2] = v.Z;
        }
    }

    /// <summary>Суставы — признаки текущего кадра базы как есть (нормализованные: самопопадание без ошибки округления); нет — нули.</summary>
    private void JointFeatures(int current, in FeatureGroup group, Span<float> raw, Span<float> values)
    {
        var mean = _db.Mean.Slice(group.Offset, group.Size);
        if (current < 0)
        {
            values.Clear();
            mean.CopyTo(raw);
            return;
        }
        var row = _db.FeatureRow(current).Slice(group.Offset, group.Size);
        var std = _db.Std.Slice(group.Offset, group.Size);
        row.CopyTo(values);
        for (int i = 0; i < raw.Length; i++) raw[i] = row[i] * std[i] + mean[i];
    }

    /// <summary>99-й перцентиль |ω| искомых кадров (ближний ранг: <c>ceil(0,99·n)</c>-е по возрастанию), не меньше 1 рад/с.</summary>
    private static float MeasureYawRate(MotionDatabase db)
    {
        var rows = db.Rows;
        if (rows.Length == 0) return 1;
        var rates = new float[rows.Length];
        int group = db.Schema.Find(FeatureKind.RootYawRate);
        if (group >= 0)
        {
            int i = db.Schema.Groups[group].Offset;
            float std = db.Std[i], mean = db.Mean[i];
            for (int r = 0; r < rows.Length; r++) rates[r] = MathF.Abs(db.FeatureRow(rows[r])[i] * std + mean);
        }
        else
        {
            for (int r = 0; r < rows.Length; r++) rates[r] = MathF.Abs(db.Interval[rows[r]].Yaw * db.SampleRate);
        }
        Array.Sort(rates);
        int rank = (int)Math.Ceiling(0.99 * rates.Length);
        return MathF.Max(1, rates[rank - 1]);
    }
}

/// <summary>
/// Суставы схемы видимой позы для запроса (<see cref="MotionQuery.Fill"/>): положения и скорости в системе корня, порядок —
/// <see cref="MotionSchema.Joints"/>. Пусто — признаки суставов кадра базы.
/// </summary>
public readonly ref struct PoseJoints(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> velocities)
{
    public ReadOnlySpan<Vector3> Positions { get; } = positions;
    public ReadOnlySpan<Vector3> Velocities { get; } = velocities;
    public bool IsEmpty => Positions.IsEmpty;
}
