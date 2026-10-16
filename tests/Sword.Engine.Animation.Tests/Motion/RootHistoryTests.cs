using System.Numerics;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionSearchFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// История корня: кольцо по номерам тиков, ёмкость от частоты тика и глубины, «последняя запись не позже тика», короткая и
/// пустая история, переполнение; без выделений.
/// </summary>
public sealed class RootHistoryTests
{
    private static Pose2 Root(long tick) => new(tick * 0.1f, -tick * 0.05f, tick * 0.01f);

    [Theory]
    [InlineData(1f / 120, 1f / 3, 42)]
    [InlineData(1f / 60, 1f / 3, 22)]
    [InlineData(1f / 240, 1f / 3, 82)]
    [InlineData(1f / 30, 1f / 3, 12)]
    [InlineData(1f / 120, 0.5f, 62)]
    [InlineData(0.1f, 0.25f, 5)]
    public void Capacity_FollowsTickRateAndDepth(float tick, float depth, int capacity) =>
        Assert.Equal(capacity, new RootHistory(tick, depth).Capacity);

    /// <summary>
    /// При любой частоте тика самый дальний момент прошлого — настоящая запись, а не экстраполяция; последняя запись — текущего
    /// тика или прошлого.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    [InlineData(1000)]
    public void FullHistory_DeepestPastMoment_IsARealRecord(int hz)
    {
        float tick = 1f / hz, depth = 1f / 3;
        var history = new RootHistory(tick, depth);
        long deepest = (long)MathF.Round(-depth / tick);
        for (long now = 1; now <= 5L * history.Capacity; now++)
        {
            foreach (bool written in new[] { false, true })
            {
                if (written) history.Add(now, Root(now));
                long at = now + deepest;
                if (at < 1) continue;
                var p = history.At(at, new Vector3(100, 0, 100), new Pose2(1e6f, 1e6f, 0));
                Assert.True(p == Root(at), $"{hz} Гц, тик {now} (записан: {written}): прошлое {at} — {p}, а записано {Root(at)}");
            }
        }
        Assert.Equal(history.Capacity, history.Count);
    }

    [Fact]
    public void At_ReturnsLastRecordNotLaterThanTick()
    {
        var history = new RootHistory(1f / 120, 1f / 3);
        history.Add(10, Root(10));
        history.Add(12, Root(12));
        history.Add(15, Root(15));
        var v = new Vector3(1, 0, 2);
        Assert.Equal(Root(10), history.At(10, v, default));
        Assert.Equal(Root(10), history.At(11, v, default));
        Assert.Equal(Root(12), history.At(12, v, default));
        Assert.Equal(Root(12), history.At(14, v, default));
        Assert.Equal(Root(15), history.At(15, v, default));
        Assert.Equal(Root(15), history.At(1000, v, default));
    }

    [Fact]
    public void ShortHistory_ExtrapolatesOldestBackByVelocity_EmptyGivesFallback()
    {
        const float tick = 1f / 120;
        var history = new RootHistory(tick, 1f / 3);
        var v = new Vector3(1.5f, 7, -2);
        var fallback = new Pose2(3, 4, 5);
        Assert.Equal(fallback, history.At(100, v, fallback));

        history.Add(10, Root(10));
        history.Add(11, Root(11));
        var p = history.At(4, v, fallback);
        Assert.Equal(Root(10).X - 1.5f * 6 * tick, p.X, 1e-6f);
        Assert.Equal(Root(10).Z + 2f * 6 * tick, p.Z, 1e-6f);
        Assert.Equal(Root(10).Yaw, p.Yaw);

        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(fallback, history.At(11, v, fallback));
        history.Add(3, Root(3));   // после сброса — снова с любого тика
        Assert.Equal(new RootSample(3, Root(3)), history[0]);
    }

    [Fact]
    public void Overflow_LosesOldest_KeepsOrder()
    {
        var history = new RootHistory(1f / 30, 0.1f);   // 3 тика + 2 = 5
        Assert.Equal(5, history.Capacity);
        for (long t = 1; t <= 12; t++) history.Add(t * 2, Root(t * 2));
        Assert.Equal(5, history.Count);
        for (int i = 0; i < 5; i++) Assert.Equal(new RootSample(2 * (8 + i), Root(2 * (8 + i))), history[i]);
        // старше старейшей — экстраполяция от неё, а не потерянная запись
        Assert.Equal(Root(16), history.At(16, Vector3.Zero, default));
        Assert.Equal(Root(16), history.At(10, Vector3.Zero, default));
    }

    [Theory]
    [InlineData("тик тот же")]
    [InlineData("тик раньше")]
    [InlineData("корень не конечен")]
    [InlineData("индекс вне")]
    [InlineData("тик ноль")]
    [InlineData("глубина ноль")]
    public void RejectsBadArguments(string what)
    {
        var history = new RootHistory(1f / 120, 1f / 3);
        history.Add(5, Root(5));
        Action act = what switch
        {
            "тик тот же" => () => history.Add(5, Root(5)),
            "тик раньше" => () => history.Add(4, Root(4)),
            "корень не конечен" => () => history.Add(6, new Pose2(float.NaN, 0, 0)),
            "индекс вне" => () => _ = history[1],
            "тик ноль" => () => _ = new RootHistory(0, 1),
            "глубина ноль" => () => _ = new RootHistory(0.01f, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая"),
        };
        Assert.ThrowsAny<ArgumentException>(act);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void AddAndAt_DoNotAllocate()
    {
        var history = new RootHistory(1f / 120, 1f / 3);
        long now = 0;
        float sink = 0;
        long bytes = Allocated(500, () =>
        {
            now++;
            history.Add(now, Root(now));
            sink += history.At(now - 40, Vector3.UnitZ, default).X + history[0].Root.Z;
        });
        Assert.Equal(0, bytes);
        Assert.True(float.IsFinite(sink));
    }
}
