namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Поиск Motion Matching: лучший искомый кадр для нормализованного запроса; цена — взвешенная сумма квадратов разниц. Блоки
/// с нижней оценкой по границам отсекаются — итог тот же, что у <see cref="SearchBruteForce"/>: порядок сложения один на цену,
/// оценку и цены групп, поэтому оценка не больше цены строки и во float. Продолжение — со скидкой; кадры своего клипа в окне
/// вокруг текущего пропускаются. При равной цене — более ранний кадр. Память не выделяет.
/// </summary>
public sealed class MotionMatcher
{
    private readonly MotionDatabase _db;
    private readonly int[] _groupStart;
    private readonly int[] _groupEnd;
    private readonly int[] _active;

    public MotionMatcher(MotionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
        var groups = database.Schema.Groups;
        _groupStart = new int[groups.Length];
        _groupEnd = new int[groups.Length];
        _active = new int[groups.Length];
        for (int g = 0; g < groups.Length; g++)
        {
            _groupStart[g] = groups[g].Offset;
            _groupEnd[g] = groups[g].End;
        }
    }

    public MotionDatabase Database => _db;

    /// <summary>
    /// Лучший кадр для нормализованного запроса. Продолжение соревнуется, только если текущий кадр искомый; его цена
    /// <see cref="MotionMatch.ContinuationCost"/> считается всегда. При равной цене остаётся продолжение, затем — более ранний кадр.
    /// </summary>
    /// <param name="groupWeights">Веса групп схемы (≥ 0) — параметр вызова.</param>
    /// <param name="current">Текущий кадр проигрывателя (продолжение); −1 — нет: без скидки и без окна пропуска.</param>
    /// <param name="continuationBias">Скидка продолжению: его цена в сравнении — <c>цена·(1 − bias)</c>, 0…1.</param>
    /// <param name="skipFrames">Кадры клипа <paramref name="current"/> ближе стольких кадров к нему (у цикла — по кругу) не рассматриваются, ≥ 0.</param>
    /// <exception cref="ArgumentException">Длины не сходятся, запрос не конечен, вес меньше 0 или не число, параметр вне пределов.</exception>
    public MotionMatch Search(ReadOnlySpan<float> query, ReadOnlySpan<float> groupWeights, int current, float continuationBias, int skipFrames)
    {
        var active = Prepare(query, groupWeights, current, continuationBias, skipFrames, out var window);
        Continuation(query, groupWeights, active, current, continuationBias, out float continuation, out float best, out int frame, out int frames);
        int blocks = 0, count = _db.BlockCount;
        for (int b = 0; b < count; b++) ScanBlock(b, query, groupWeights, active, window, current, ref best, ref frame, ref blocks, ref frames);
        return Result(query, groupWeights, active, frame, continuation, blocks, frames);
    }

    /// <summary>То же полным перебором всех искомых кадров — образец для тестов и дифференциального прогона (<see cref="MotionMatch.Blocks"/> — 0).</summary>
    public MotionMatch SearchBruteForce(ReadOnlySpan<float> query, ReadOnlySpan<float> groupWeights, int current, float continuationBias, int skipFrames)
    {
        var active = Prepare(query, groupWeights, current, continuationBias, skipFrames, out var window);
        Continuation(query, groupWeights, active, current, continuationBias, out float continuation, out float best, out int frame, out int frames);
        foreach (int f in _db.Rows)
        {
            if (f == current || window.Skips(f)) continue;
            frames++;
            float cost = Distance(query, _db.FeatureRow(f), groupWeights, active, float.PositiveInfinity);
            if (cost < best)
            {
                best = cost;
                frame = f;
            }
        }
        return Result(query, groupWeights, active, frame, continuation, 0, frames);
    }

    /// <summary>Цена кадра <paramref name="frame"/> для запроса (без скидки), по группам — в <paramref name="groups"/>.</summary>
    /// <exception cref="ArgumentException">Длины не сходятся, запрос не конечен, вес меньше 0 или не число, кадр вне базы.</exception>
    public float Cost(ReadOnlySpan<float> query, ReadOnlySpan<float> groupWeights, int frame, out MotionGroupCosts groups)
    {
        if ((uint)frame >= (uint)_db.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame), frame, $"кадров {_db.FrameCount}");
        var active = Check(query, groupWeights);
        return GroupCosts(query, _db.FeatureRow(frame), groupWeights, active, out groups);
    }

    /// <summary>Проверка входа поиска; активные группы (вес больше 0) и окно пропуска вокруг текущего кадра.</summary>
    private ReadOnlySpan<int> Prepare(ReadOnlySpan<float> query, ReadOnlySpan<float> weights, int current, float bias, int skip, out Window window)
    {
        if (current < -1 || current >= _db.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(current), current, $"текущий кадр — −1 или кадр базы (кадров {_db.FrameCount})");
        if (!(bias >= 0 && bias <= 1)) throw new ArgumentOutOfRangeException("continuationBias", bias, "скидка продолжению — от 0 до 1");
        if (skip < 0) throw new ArgumentOutOfRangeException("skipFrames", skip, "окно пропуска — не меньше 0 кадров");
        var active = Check(query, weights);
        window = default;
        if (current >= 0)
        {
            var clip = _db.Clips[_db.ClipOf[current]];
            window = new Window(clip.Start, clip.Count, clip.Loop, current - clip.Start, skip);
        }
        return active;
    }

    private ReadOnlySpan<int> Check(ReadOnlySpan<float> query, ReadOnlySpan<float> weights)
    {
        if (query.Length != _db.FeatureCount)
            throw new ArgumentException($"признаков в запросе {query.Length}, а в базе {_db.FeatureCount}", nameof(query));
        if (weights.Length != _groupStart.Length)
            throw new ArgumentException($"весов {weights.Length}, а групп {_groupStart.Length}", "groupWeights");
        for (int i = 0; i < query.Length; i++)
            if (!float.IsFinite(query[i])) throw new ArgumentException($"признак запроса {i}: {query[i]}", nameof(query));
        int n = 0;
        for (int g = 0; g < weights.Length; g++)
        {
            if (!(weights[g] >= 0) || !float.IsFinite(weights[g]))
                throw new ArgumentException($"вес группы {g}: {weights[g]}, а нужен конечный и не меньше 0", "groupWeights");
            if (weights[g] > 0) _active[n++] = g;
        }
        return _active.AsSpan(0, n);
    }

    /// <summary>Цена продолжения (у любого текущего кадра) и начальная лучшая цена — продолжения со скидкой, если он искомый.</summary>
    private void Continuation(ReadOnlySpan<float> query, ReadOnlySpan<float> weights, ReadOnlySpan<int> active, int current, float bias,
        out float continuation, out float best, out int frame, out int frames)
    {
        continuation = best = float.PositiveInfinity;
        frame = -1;
        frames = 0;
        if (current < 0) return;
        continuation = Distance(query, _db.FeatureRow(current), weights, active, float.PositiveInfinity);
        frames = 1;
        if (!_db.Searchable[current]) return;
        best = continuation * (1 - bias);
        frame = current;
    }

    /// <summary>Блок поиска: нижняя оценка по границам; не лучше лучшей цены — пропуск, иначе строки блока с ранним выходом.</summary>
    private void ScanBlock(int block, ReadOnlySpan<float> query, ReadOnlySpan<float> weights, ReadOnlySpan<int> active, in Window window,
        int current, ref float best, ref int frame, ref int blocks, ref int frames)
    {
        int fc = _db.FeatureCount;
        if (LowerBound(query, _db.BoundsMin.Slice(block * fc, fc), _db.BoundsMax.Slice(block * fc, fc), weights, active, best) >= best) return;
        blocks++;
        foreach (int f in _db.BlockRows(block))
        {
            if (f == current || window.Skips(f)) continue;
            frames++;
            float cost = Distance(query, _db.FeatureRow(f), weights, active, best);
            if (cost < best)
            {
                best = cost;
                frame = f;
            }
        }
    }

    /// <summary>Цена строки; как только частичная сумма не меньше <paramref name="limit"/> — она (кадр уже не лучше).</summary>
    private float Distance(ReadOnlySpan<float> query, ReadOnlySpan<float> row, ReadOnlySpan<float> weights, ReadOnlySpan<int> active, float limit)
    {
        float total = 0;
        foreach (int g in active)
        {
            float sum = 0;
            for (int i = _groupStart[g], end = _groupEnd[g]; i < end; i++)
            {
                float d = row[i] - query[i];
                sum += d * d;
            }
            total += weights[g] * sum;
            if (total >= limit) break;
        }
        return total;
    }

    /// <summary>Нижняя оценка цены строк блока: разница запроса с отрезком [min, max] (0 внутри), тем же порядком, что <see cref="Distance"/>.</summary>
    private float LowerBound(ReadOnlySpan<float> query, ReadOnlySpan<float> min, ReadOnlySpan<float> max, ReadOnlySpan<float> weights,
        ReadOnlySpan<int> active, float limit)
    {
        float total = 0;
        foreach (int g in active)
        {
            float sum = 0;
            for (int i = _groupStart[g], end = _groupEnd[g]; i < end; i++)
            {
                float q = query[i], below = min[i] - q, above = q - max[i];
                float gap = below > 0 ? below : above > 0 ? above : 0;
                sum += gap * gap;
            }
            total += weights[g] * sum;
            if (total >= limit) break;
        }
        return total;
    }

    private float GroupCosts(ReadOnlySpan<float> query, ReadOnlySpan<float> row, ReadOnlySpan<float> weights, ReadOnlySpan<int> active,
        out MotionGroupCosts groups)
    {
        groups = default;
        float total = 0;
        foreach (int g in active)
        {
            float sum = 0;
            for (int i = _groupStart[g], end = _groupEnd[g]; i < end; i++)
            {
                float d = row[i] - query[i];
                sum += d * d;
            }
            groups[g] = weights[g] * sum;
            total += groups[g];
        }
        return total;
    }

    private MotionMatch Result(ReadOnlySpan<float> query, ReadOnlySpan<float> weights, ReadOnlySpan<int> active, int frame, float continuation,
        int blocks, int frames)
    {
        if (frame < 0) return new MotionMatch(-1, float.PositiveInfinity, continuation, blocks, frames, default);
        // у победителя раннего выхода не было: полная цена — те же числа, что в сравнении
        float cost = GroupCosts(query, _db.FeatureRow(frame), weights, active, out var groups);
        return new MotionMatch(frame, cost, continuation, blocks, frames, groups);
    }

    /// <summary>Окно пропуска вокруг текущего кадра в его клипе; у цикла — по кругу с периодом <c>кадров − 1</c> через шов.</summary>
    private readonly struct Window
    {
        private readonly int _start, _count, _position, _period, _skip;
        private readonly bool _loop;

        public Window(int start, int count, bool loop, int frameInClip, int skip)
        {
            _start = start;
            _count = count;
            _loop = loop;
            _period = Math.Max(1, count - 1);
            _position = loop && frameInClip == count - 1 ? 0 : frameInClip;
            _skip = skip;
        }

        public bool Skips(int frame)
        {
            int i = frame - _start;
            if ((uint)i >= (uint)_count) return false;
            if (_loop && i == _count - 1) i = 0;
            int d = Math.Abs(i - _position);
            if (_loop) d = Math.Min(d, _period - d);
            return d < _skip;
        }
    }
}
