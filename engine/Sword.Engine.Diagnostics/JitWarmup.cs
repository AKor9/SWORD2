using System.Diagnostics;
using System.Runtime;

namespace Sword.Engine.Diagnostics;

/// <summary>Итог прогрева до затишья JIT: шагов сделано, секунд прошло, утих ли JIT (иначе — упёрся в предел или шаги кончились).</summary>
public readonly record struct JitWarmupResult(int Steps, double Seconds, bool Settled)
{
    public override string ToString() =>
        $"прогрев JIT: {Steps} шагов, {Seconds:0.###} с, {(Settled ? "JIT утих" : "JIT не утих — предел")}";
}

/// <summary>
/// Прогрев до затишья JIT: звать шаг, пока число скомпилированных методов не стоит <see cref="QuietMs"/> подряд (дольше
/// задержки перекомпиляции на уровень 1, ~100 мс), не дольше <see cref="MaxSeconds"/>. Иначе разовое выделение среды
/// .NET (~24,6 КБ) попадает в замеряемый тик — флейк «0 Б/тик». Греет отдельная тёплая симуляция той же композиции:
/// замеряемая и её хеши не меняются. Предел 1 с: методы тика уже на уровне 1, хвост толпы — ~80 методов.
/// </summary>
public static class JitWarmup
{
    /// <summary>Сколько JIT должен молчать, мс (задержка многоуровневой компиляции — 100 мс, с запасом).</summary>
    public const double QuietMs = 250;

    /// <summary>Предел прогрева, с: полного затишья (~1,8 с в Release) не ждём — к пределу методы тика уже на уровне 1.</summary>
    public const double MaxSeconds = 1;

    /// <summary>Звать <paramref name="step"/>, пока JIT не утих или не прошло <paramref name="maxSeconds"/>; шаг вернул
    /// ложь — шагов больше нет (итог — не утих).</summary>
    public static JitWarmupResult Run(Func<bool> step, double quietMs = QuietMs, double maxSeconds = MaxSeconds)
    {
        ArgumentNullException.ThrowIfNull(step);
        long start = Stopwatch.GetTimestamp(), lastChange = start;
        long count = JitInfo.GetCompiledMethodCount(currentThread: false);
        int steps = 0;
        while (true)
        {
            if (!step()) return new JitWarmupResult(steps, Stopwatch.GetElapsedTime(start).TotalSeconds, false);
            steps++;
            long now = Stopwatch.GetTimestamp();
            long compiled = JitInfo.GetCompiledMethodCount(currentThread: false);
            if (compiled != count)
            {
                count = compiled;
                lastChange = now;
            }
            else if (Stopwatch.GetElapsedTime(lastChange, now).TotalMilliseconds >= quietMs)
                return new JitWarmupResult(steps, Stopwatch.GetElapsedTime(start, now).TotalSeconds, true);
            if (Stopwatch.GetElapsedTime(start, now).TotalSeconds >= maxSeconds)
                return new JitWarmupResult(steps, Stopwatch.GetElapsedTime(start, now).TotalSeconds, false);
        }
    }
}
