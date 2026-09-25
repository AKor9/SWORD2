namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Запрос поиска по схеме базы (точка 8в), в системе корня <c>state.Root</c>: траектория будущего — предсказание модели
/// контроллера (<see cref="TrajectoryModel.Predict"/>) в моменты <see cref="MotionSchema.FutureTimes"/>; прошлое — из истории
/// корня (<see cref="RootHistory.At"/>) в моменты <see cref="MotionSchema.PastTimes"/>; скорость и угловая скорость корня — из
/// модели; суставы — признаки текущего кадра базы (а не видимой позы после инерциализации: иначе поза запроса проигрывает
/// продолжению и сбивает фазу шага). Масштаб данных: скорость состояния и прошлое умножаются на <c>velocityScale</c>, цель — на
/// <c>goalScale</c> (скорость управления → скорость клипов в этом направлении); угловая скорость зажата
/// <c>maxYawRate</c>. Итог нормализуется <see cref="MotionDatabase.Mean"/>/<see cref="MotionDatabase.Std"/>. Буферы — свои,
/// <see cref="Fill"/> память не выделяет; экземпляр — у каждой симуляции свой.
/// </summary>
public sealed class MotionQuery
{
    public MotionQuery(MotionDatabase database) => throw new NotImplementedException("8в: запрос — MotionQuery");

    public MotionDatabase Database => throw new NotImplementedException("8в");

    /// <summary>Нормализованный запрос последнего <see cref="Fill"/> — вход <see cref="MotionMatcher.Search"/>.</summary>
    public ReadOnlySpan<float> Values => throw new NotImplementedException("8в");

    /// <summary>Тот же запрос в сырых единицах (м, м/с, рад/с) — для вида и оверлея траекторий.</summary>
    public ReadOnlySpan<float> Raw => throw new NotImplementedException("8в");

    /// <summary>|ω| корня искомых кадров базы, 99-й перцентиль, не меньше 1 рад/с, — обычный зажим угловой скорости запроса.</summary>
    public float DataYawRate => throw new NotImplementedException("8в");

    /// <summary>Заполнить запрос.</summary>
    /// <param name="state">Корень (мир) и его скорость сейчас.</param>
    /// <param name="model">Модель контроллера на этот тик (цели и пределы).</param>
    /// <param name="history">История корня; шаг предсказания — её <see cref="RootHistory.TickSeconds"/>.</param>
    /// <param name="now">Номер текущего тика (прошлое — тики <c>now + round(t / тик)</c>).</param>
    /// <param name="current">Текущий кадр базы (признаки суставов); −1 — нет: суставы — средние по базе (нормализованные нули).</param>
    /// <param name="velocityScale">Масштаб скорости состояния и прошлого (kv).</param>
    /// <param name="goalScale">Масштаб целевой скорости (kg).</param>
    /// <param name="maxYawRate">Зажим угловой скорости корня, рад/с (обычно <see cref="DataYawRate"/>).</param>
    public void Fill(in TrajectoryState state, in TrajectoryModel model, RootHistory history, long now, int current, float velocityScale,
        float goalScale, float maxYawRate) => throw new NotImplementedException("8в");
}
