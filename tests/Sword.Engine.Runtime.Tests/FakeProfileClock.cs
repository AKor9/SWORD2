using System.Diagnostics;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Часы профайлера, время которых двигает тест: длительность зоны задана, а не замерена, нагрузка машины на итоги не влияет.
/// Однопоточные, как профайлер.
/// </summary>
internal sealed class FakeProfileClock : ProfileClock
{
    private long _now = Stopwatch.Frequency;   // начало — 1 с: отметки положительные

    /// <summary>Сейчас, отсчёты <see cref="Stopwatch"/> (как у профайлера).</summary>
    public long Now => _now;

    public override long Timestamp() => _now;

    public static long Ticks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);

    public void Run(double ms) => _now += Ticks(ms);
}
