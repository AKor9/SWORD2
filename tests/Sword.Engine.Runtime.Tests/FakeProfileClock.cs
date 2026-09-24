using System.Diagnostics;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Поддельные часы профайлера (<see cref="ProfileClock"/>): время и счётчики среды идут только тогда, когда их двигает тест, —
/// длительность каждой зоны и её доля на CPU заданы, а не замерены; сборок и JIT нет. Снятие потока тестов с процессора, сборки и
/// JIT других потоков процесса, частота машины на суд аномалий, их класс, ведро жетонов и лимиты файлов не влияют: настоящего
/// времени профайлер на этих часах не читает. Однопоточные — как профайлер (двигает и читает поток владельца).
/// </summary>
internal sealed class FakeProfileClock : ProfileClock
{
    private long _now = Stopwatch.Frequency;   // начало — 1 с: отметки положительные
    private long _cycles;

    /// <summary>Сейчас, отсчёты <see cref="Stopwatch"/> (как у профайлера).</summary>
    public long Now => _now;

    public override long Timestamp() => _now;

    /// <summary>Такты потока — от <see cref="Run"/>; сборок, паузы GC и JIT нет (их не бывает, пока тест их не задал).</summary>
    public override ProfileCounters Counters() => new(_cycles, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Мс в отсчёты <see cref="Stopwatch"/>.</summary>
    public static long Ticks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);

    /// <summary>
    /// Поток на CPU <paramref name="ms"/> мс: настенное время и время потока. Такты — с запасом в один (округление вверх): время
    /// потока не меньше настенного, «вне CPU» у счёта — ровно 0.
    /// </summary>
    public void Run(double ms)
    {
        long ticks = Ticks(ms);
        _now += ticks;
        _cycles += (long)Math.Ceiling(ticks * ThreadClock.CyclesPerSecond / Stopwatch.Frequency) + 1;
    }

    /// <summary>Поток вне CPU <paramref name="ms"/> мс (сон, снятие с процессора, ожидание агента): только настенное время.</summary>
    public void OffCpu(double ms) => _now += Ticks(ms);
}
