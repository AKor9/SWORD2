using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionFixtures;
using static Sword.Engine.Animation.Tests.MotionSearchFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Поиск: отсечение блоков нижней оценкой даёт тот же итог, что полный перебор, на любых запросах и весах, с продолжением и
/// окном; итог — действительно лучший из допустимых кадров (проверка ценой каждого кадра); самопопадание; ничья — ранний кадр
/// (и продолжение); окно своего клипа — по кругу через шов цикла; скидка продолжению; цены групп в сумме — цена; 0 Б.
/// </summary>
public sealed class MotionMatcherTests
{
    /// <summary>
    /// Итог — лучший допустимый кадр, проверка «снаружи» ценой каждого кадра (<see cref="MotionMatcher.Cost"/>) и своим окном: ни
    /// один допустимый кадр не дешевле (при равной цене — не раньше); продолжение осталось — никто не дешевле его цены со скидкой;
    /// ушли из продолжения — дешевле её строго.
    /// </summary>
    private static void AssertBest(MotionMatcher m, float[] q, float[] w, int current, float bias, int skip, in MotionMatch r)
    {
        var db = m.Database;
        float continuation = current >= 0 ? m.Cost(q, w, current, out _) : float.PositiveInfinity;
        Assert.Equal(continuation, r.ContinuationCost);
        bool stays = current >= 0 && db.Searchable[current];
        float limit = stays ? continuation * (1 - bias) : float.PositiveInfinity;
        if (r.Frame < 0)
        {
            Assert.False(stays);
            foreach (int f in db.Rows) Assert.True(f == current || InWindow(db, current, skip, f), $"кадр {f} допустим, а итог −1");
            return;
        }
        Assert.Equal(m.Cost(q, w, r.Frame, out _), r.Cost);
        if (r.Frame == current)
        {
            Assert.True(stays, "итог — текущий кадр, который не ищется");
            Assert.Equal(continuation, r.Cost);
        }
        else
        {
            Assert.True(db.Searchable[r.Frame], $"итог {r.Frame} не ищется");
            Assert.False(InWindow(db, current, skip, r.Frame), $"итог {r.Frame} в окне пропуска вокруг {current}");
            Assert.True(r.Cost < limit, $"ушли из продолжения {current} ({limit} со скидкой) в {r.Frame} ({r.Cost})");
        }
        foreach (int f in db.Rows)
        {
            if (f == current || f == r.Frame || InWindow(db, current, skip, f)) continue;
            float c = m.Cost(q, w, f, out _);
            if (r.Frame == current) Assert.False(c < limit, $"кадр {f} ({c}) дешевле продолжения со скидкой ({limit})");
            else
            {
                Assert.False(c < r.Cost, $"кадр {f} ({c}) дешевле итога {r.Frame} ({r.Cost})");
                if (c == r.Cost) Assert.True(f > r.Frame, $"ничья: кадр {f} раньше итога {r.Frame}");
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(300)]
    [InlineData(1500)]
    public void Search_EqualsBruteForce_AndIsTheBest(int frames)
    {
        var schema = Schema();
        int pruned = 0, searches = 0;
        for (int seed = 0; seed < 8; seed++)
        {
            var lib = RandomLibrary(seed * 31 + frames, frames);
            var db = seed % 2 == 0 ? Plain(lib, schema, All(lib), seed).Build(lib, schema) : Smooth(lib, schema, seed);
            var m = new MotionMatcher(db);
            var rng = new Random(seed);
            for (int k = 0; k < 40; k++)
            {
                float noise = (k % 4) switch { 0 => 0, 1 => 0.01f, 2 => 0.3f, _ => -1 };
                var q = RandomQuery(rng, db, noise);
                var w = k % 5 == 0 ? schema.DefaultWeights.ToArray() : RandomWeights(rng, schema);
                int current = rng.Next(4) == 0 ? -1 : rng.Next(db.FrameCount);
                float bias = (k % 3) switch { 0 => 0, 1 => (float)rng.NextDouble() * 0.6f, _ => rng.Next(10) == 0 ? 1 : 0.1f };
                int skip = rng.Next(13);
                var fast = m.Search(q, w, current, bias, skip);
                var brute = m.SearchBruteForce(q, w, current, bias, skip);
                string what = $"кадров {frames}, сид {seed}, запрос {k}";
                Assert.True(brute.Frame == fast.Frame, $"{what}: перебор {brute.Frame} ({brute.Cost}), поиск {fast.Frame} ({fast.Cost})");
                Assert.Equal(brute.Cost, fast.Cost);
                Assert.Equal(brute.ContinuationCost, fast.ContinuationCost);
                Assert.Equal(brute.Groups, fast.Groups);
                Assert.Equal(0, brute.Blocks);
                Assert.InRange(fast.Blocks, 0, db.BlockCount);
                Assert.InRange(fast.Frames, 0, brute.Frames);
                AssertBest(m, q, w, current, bias, skip, fast);
                // лучшая цена 0 (точное попадание, полная скидка) отсекает блоки и без нижней оценки — не в счёт
                float best = fast.Frame == current ? fast.Cost * (1 - bias) : fast.Cost;
                if (best > 0)
                {
                    pruned += db.BlockCount - fast.Blocks;
                    searches++;
                }
            }
        }
        // отсечение работает, а не только не мешает: на базах покрупнее часть блоков не смотрится и при лучшей цене больше 0
        if (frames >= 300) Assert.True(pruned > 0, $"за {searches} поисков с ценой больше 0 ни одного отсечённого блока");
    }

    [Fact]
    public void SelfHit_QueryFromFrameFeatures_FindsThatFrame()
    {
        var lib = Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var m = new MotionMatcher(db);
        var w = schema.DefaultWeights.ToArray();
        foreach (int f in db.Rows)
        {
            var q = db.FeatureRow(f).ToArray();
            var r = m.Search(q, w, -1, 0, 0);
            Assert.Equal(0f, r.Cost);
            Assert.True(r.Frame == f || db.FeatureRow(r.Frame).SequenceEqual(q), $"запрос кадра {f} нашёл {r.Frame}");
            // текущий — он же: продолжение с ценой 0 остаётся при любом окне
            var stay = m.Search(q, w, f, 0.1f, 6);
            Assert.Equal(f, stay.Frame);
            Assert.Equal(0f, stay.ContinuationCost);
        }
    }

    [Fact]
    public void Tie_GoesToEarlierFrame_AndContinuationKeepsItsTie()
    {
        var lib = Library();
        var schema = Schema();
        var a = Plain(lib, schema, [0, 1, 2]);
        int fc = schema.FeatureCount;
        foreach (int f in new[] { 5, 22, 40 }) Array.Copy(a.Features, 30 * fc, a.Features, f * fc, fc);
        // у кадров 12 и 33 равна только скорость корня
        var velocity = schema.Groups[schema.Find("root-velocity")];
        Array.Copy(a.Features, 33 * fc + velocity.Offset, a.Features, 12 * fc + velocity.Offset, velocity.Size);
        var db = a.Build(lib, schema);
        var m = new MotionMatcher(db);
        var q = db.FeatureRow(30).ToArray();
        var w = schema.DefaultWeights.ToArray();
        Assert.Equal(5, m.Search(q, w, -1, 0, 0).Frame);
        Assert.Equal(5, m.SearchBruteForce(q, w, -1, 0, 0).Frame);
        // кадр 5 в окне текущего 3 — следующий по порядку из равных
        Assert.Equal(22, m.Search(q, w, 3, 0, 4).Frame);
        // продолжение с той же ценой (без скидки) остаётся
        Assert.Equal(40, m.Search(q, w, 40, 0, 0).Frame);
        // ничья по весам: сравнивается только скорость корня — у 12 и 33 она одна, итог — ранний
        var only = new float[schema.GroupCount];
        only[schema.Find("root-velocity")] = 1;
        var r = m.Search(db.FeatureRow(33).ToArray(), only, -1, 0, 0);
        Assert.Equal(12, r.Frame);
        Assert.Equal(0f, r.Cost);
        Assert.Equal(33, m.Search(db.FeatureRow(33).ToArray(), only, 33, 0, 0).Frame);   // продолжение из равных остаётся
    }

    [Fact]
    public void SkipWindow_WrapsThroughLoopSeam_NotThroughClipEnd()
    {
        // цикл 20 кадров (0…19, 19 = 0, период 19), нециклический 12 (20…31), цикл 16 (32…47)
        var lib = Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var m = new MotionMatcher(db);
        var w = schema.DefaultWeights.ToArray();
        MotionMatch Find(int target, int current, int skip) => m.Search(db.FeatureRow(target).ToArray(), w, current, 0, skip);

        // текущий — место 1: окно 4 — места 17, 18, 0, 1, 2, 3, 4 (17 и 18 — через шов)
        foreach (int hidden in new[] { 17, 18, 0, 2, 3, 4 })
        {
            Assert.NotEqual(hidden, Find(hidden, 1, 4).Frame);
            Assert.Equal(hidden, Find(hidden, 1, 0).Frame);
            Assert.Equal(hidden, Find(hidden, -1, 4).Frame);
        }
        Assert.Equal(16, Find(16, 1, 4).Frame);
        Assert.Equal(5, Find(5, 1, 4).Frame);
        // текущий — последний кадр цикла (он же первый): окно вокруг места 0
        Assert.NotEqual(17, Find(17, 19, 3).Frame);
        Assert.Equal(16, Find(16, 19, 3).Frame);
        // нециклический клип не заворачивает: у места 1 последние кадры не в окне
        Assert.Equal(30, Find(30, 21, 4).Frame);
        Assert.NotEqual(23, Find(23, 21, 4).Frame);
        // окно — только свой клип: соседний клип рядом по номерам кадров не пропускается
        Assert.Equal(18, Find(18, 20, 4).Frame);
    }

    [Fact]
    public void ContinuationBias_KeepsCurrentUntilAnotherIsCheaperThanDiscounted()
    {
        var lib = Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var m = new MotionMatcher(db);
        var w = schema.DefaultWeights.ToArray();
        const int current = 7, target = 38;
        // запрос между текущим и целью, ближе к цели
        var q = new float[schema.FeatureCount];
        for (int i = 0; i < q.Length; i++) q[i] = db.FeatureRow(current)[i] * 0.45f + db.FeatureRow(target)[i] * 0.55f;
        float stay = m.Cost(q, w, current, out _);
        var other = m.Search(q, w, -1, 0, 0);   // лучший без продолжения
        Assert.NotEqual(current, other.Frame);
        Assert.True(other.Cost < stay);
        float edge = 1 - other.Cost / stay;   // скидка, при которой продолжение сравнялось бы с лучшим

        var kept = m.Search(q, w, current, MathF.Min(1, edge + 0.02f), 0);
        Assert.Equal(current, kept.Frame);
        Assert.Equal(stay, kept.Cost);   // цена — без скидки
        Assert.Equal(stay, kept.ContinuationCost);

        var left = m.Search(q, w, current, MathF.Max(0, edge - 0.02f), 0);
        Assert.Equal(other.Frame, left.Frame);
        Assert.Equal(other.Cost, left.Cost);
        Assert.Equal(stay, left.ContinuationCost);
        // полная скидка — продолжение не уступает никогда
        Assert.Equal(current, m.Search(q, w, current, 1, 0).Frame);
    }

    [Fact]
    public void GroupCosts_SumToCost_EachIsWeightTimesSquares()
    {
        var lib = Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var m = new MotionMatcher(db);
        var rng = new Random(3);
        for (int k = 0; k < 100; k++)
        {
            var q = RandomQuery(rng, db, k % 2 == 0 ? 0.5f : -1);
            var w = RandomWeights(rng, schema);
            var r = m.Search(q, w, -1, 0, 0);
            float sum = 0;
            for (int g = 0; g < schema.GroupCount; g++) sum += r.Groups[g];
            Assert.Equal(r.Cost, sum);   // тот же порядок сложения — ровно
            for (int g = schema.GroupCount; g < MotionSchema.MaxGroups; g++) Assert.Equal(0f, r.Groups[g]);
            var row = db.FeatureRow(r.Frame);
            for (int g = 0; g < schema.GroupCount; g++)
            {
                var group = schema.Groups[g];
                double squares = 0;
                for (int i = group.Offset; i < group.End; i++) squares += (row[i] - (double)q[i]) * (row[i] - (double)q[i]);
                Assert.Equal(w[g] * squares, r.Groups[g], Math.Max(1e-4, 1e-5 * w[g] * squares));
            }
            float cost = m.Cost(q, w, r.Frame, out var groups);
            Assert.Equal(r.Cost, cost);
            Assert.Equal(r.Groups, groups);
        }
    }

    [Fact]
    public void NothingToSearch_GivesMinusOne_ButContinuationCostIsKnown()
    {
        // один нециклический клип 12 кадров: искомые 0…10; текущий — последний (не ищется), окно накрывает весь клип
        var lib = Library();
        var schema = Schema();
        var db = Plain(lib, schema, [1]).Build(lib, schema);
        var m = new MotionMatcher(db);
        var w = schema.DefaultWeights.ToArray();
        var q = db.FeatureRow(4).ToArray();
        var r = m.Search(q, w, 11, 0.5f, 20);
        Assert.Equal(-1, r.Frame);
        Assert.Equal(float.PositiveInfinity, r.Cost);
        Assert.Equal(m.Cost(q, w, 11, out _), r.ContinuationCost);
        Assert.Equal(default, r.Groups);
        Assert.Equal(r, m.SearchBruteForce(q, w, 11, 0.5f, 20) with { Blocks = r.Blocks });
        // не ищется, но окно уже — продолжение не соревнуется, ищется другой кадр
        Assert.Equal(4, m.Search(q, w, 11, 0.5f, 3).Frame);
    }

    [Theory]
    [InlineData("запрос короче")]
    [InlineData("весов больше")]
    [InlineData("вес меньше нуля")]
    [InlineData("вес не число")]
    [InlineData("запрос не число")]
    [InlineData("текущий вне базы")]
    [InlineData("текущий меньше −1")]
    [InlineData("скидка больше 1")]
    [InlineData("окно меньше 0")]
    [InlineData("цена кадра вне базы")]
    public void RejectsBadArguments(string what)
    {
        var lib = Library();
        var schema = Schema();
        var db = Plain(lib, schema, [0, 1, 2]).Build(lib, schema);
        var m = new MotionMatcher(db);
        var q = db.FeatureRow(3).ToArray();
        var w = schema.DefaultWeights.ToArray();
        Action act = what switch
        {
            "запрос короче" => () => m.Search(q.AsSpan(1), w, -1, 0, 0),
            "весов больше" => () => m.Search(q, [.. w, 1], -1, 0, 0),
            "вес меньше нуля" => () => m.SearchBruteForce(q, [-1, .. w[1..]], -1, 0, 0),
            "вес не число" => () => m.Search(q, [float.NaN, .. w[1..]], -1, 0, 0),
            "запрос не число" => () => m.Search([float.PositiveInfinity, .. q[1..]], w, -1, 0, 0),
            "текущий вне базы" => () => m.Search(q, w, db.FrameCount, 0, 0),
            "текущий меньше −1" => () => m.Search(q, w, -2, 0, 0),
            "скидка больше 1" => () => m.Search(q, w, 3, 1.5f, 0),
            "окно меньше 0" => () => m.Search(q, w, 3, 0, -1),
            "цена кадра вне базы" => () => m.Cost(q, w, -1, out _),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая"),
        };
        Assert.ThrowsAny<ArgumentException>(act);
    }

    [Fact]
    public void SearchAndCost_DoNotAllocate()
    {
        var lib = RandomLibrary(4, 600);
        var schema = Schema();
        var db = Smooth(lib, schema, 4);
        var m = new MotionMatcher(db);
        var rng = new Random(5);
        var queries = Enumerable.Range(0, 16).Select(_ => RandomQuery(rng, db, 0.2f)).ToArray();
        var w = schema.DefaultWeights.ToArray();
        int k = 0, sink = 0;
        long bytes = Allocated(300, () =>
        {
            var q = queries[k++ % queries.Length];
            sink += m.Search(q, w, k % db.FrameCount, 0.1f, 6).Frame;
            sink += (int)m.Cost(q, w, k % db.FrameCount, out _);
        });
        Assert.Equal(0, bytes);
        bytes = Allocated(20, () => sink += m.SearchBruteForce(queries[k++ % queries.Length], w, -1, 0, 0).Frame);
        Assert.Equal(0, bytes);
        Assert.NotEqual(int.MinValue, sink);
    }
}
