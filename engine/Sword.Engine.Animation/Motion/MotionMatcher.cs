namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Поиск Motion Matching (точка 8в): лучший искомый кадр базы для нормализованного запроса. Цена кадра — взвешенная сумма
/// квадратов разниц нормализованных признаков (вес признака — вес его группы). Блоки по <see cref="MotionDatabase.BlockSize"/>
/// строк с нижней оценкой по границам (взвешенной теми же весами): блок, чья оценка не лучше лучшей цены, пропускается — итог
/// тот же, что у полного перебора (<see cref="SearchBruteForce"/>). Продолжение (текущий кадр) — со скидкой; кадры своего
/// клипа в окне вокруг текущего пропускаются (у цикла — по кругу через шов). Строгое «меньше»: при равной цене — более ранний
/// кадр. Память не выделяет; тело цикла по блокам — в своём методе (правило OSR). Экземпляр — у каждой симуляции свой (база
/// общая).
/// </summary>
public sealed class MotionMatcher
{
    public MotionMatcher(MotionDatabase database) => throw new NotImplementedException("8в: поиск — MotionMatcher");

    public MotionDatabase Database => throw new NotImplementedException("8в");

    /// <summary>
    /// Лучший кадр для запроса <paramref name="query"/> (нормализованного, <see cref="MotionDatabase.FeatureCount"/> чисел).
    /// </summary>
    /// <param name="groupWeights">Веса групп схемы (по одному на группу, ≥ 0) — параметр вызова (ручки модуля).</param>
    /// <param name="current">Текущий кадр проигрывателя (продолжение); −1 — нет: без скидки и без окна пропуска.</param>
    /// <param name="continuationBias">Скидка продолжению: его цена в сравнении — <c>цена·(1 − bias)</c>, 0…1.</param>
    /// <param name="skipFrames">Кадры клипа <paramref name="current"/> ближе стольких кадров к нему (у цикла — по кругу) не рассматриваются, ≥ 0.</param>
    public MotionMatch Search(ReadOnlySpan<float> query, ReadOnlySpan<float> groupWeights, int current, float continuationBias, int skipFrames) =>
        throw new NotImplementedException("8в");

    /// <summary>То же полным перебором всех искомых кадров — образец для тестов и дифференциального прогона (<see cref="MotionMatch.Blocks"/> — 0).</summary>
    public MotionMatch SearchBruteForce(ReadOnlySpan<float> query, ReadOnlySpan<float> groupWeights, int current, float continuationBias, int skipFrames) =>
        throw new NotImplementedException("8в");

    /// <summary>Цена кадра <paramref name="frame"/> для запроса (без скидки), по группам — в <paramref name="groups"/>.</summary>
    public float Cost(ReadOnlySpan<float> query, ReadOnlySpan<float> groupWeights, int frame, out MotionGroupCosts groups) =>
        throw new NotImplementedException("8в");
}
