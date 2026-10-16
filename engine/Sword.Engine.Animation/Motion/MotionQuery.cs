namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Запрос поиска по схеме базы в системе корня <c>state.Root</c>: будущее — предсказание модели контроллера, прошлое — история
/// корня, скорости корня — из модели, суставы — признаки текущего кадра базы (а не видимой позы: иначе запрос проигрывает
/// продолжению и сбивает фазу шага). Скорость и прошлое × <c>velocityScale</c>, цель × <c>goalScale</c>, угловая скорость
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
    /// <exception cref="ArgumentException">Кадр вне базы, масштаб не конечен, зажим меньше 0 или не число.</exception>
    public void Fill(in TrajectoryState state, in TrajectoryModel model, RootHistory history, long now, int current, float velocityScale,
        float goalScale, float maxYawRate)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (current < -1 || current >= _db.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(current), current, $"текущий кадр — −1 или кадр базы (кадров {_db.FrameCount})");
        if (!float.IsFinite(velocityScale)) throw new ArgumentOutOfRangeException(nameof(velocityScale), velocityScale, "масштаб не конечен");
        if (!float.IsFinite(goalScale)) throw new ArgumentOutOfRangeException(nameof(goalScale), goalScale, "масштаб не конечен");
        if (!(maxYawRate >= 0)) throw new ArgumentOutOfRangeException(nameof(maxYawRate), maxYawRate, "зажим ω — не меньше 0");

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
        foreach (var group in schema.Groups)
        {
            var raw = _raw.AsSpan(group.Offset, group.Size);
            var values = _values.AsSpan(group.Offset, group.Size);
            if (MotionSchema.IsJoint(group.Kind))
            {
                JointFeatures(current, group, raw, values);
                continue;
            }
            switch (group.Kind)
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
