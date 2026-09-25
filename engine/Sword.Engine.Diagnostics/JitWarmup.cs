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
/// Прогрев до затишья JIT: звать шаг, пока число скомпилированных методов процесса (<see cref="JitInfo.GetCompiledMethodCount"/>)
/// не стоит <see cref="QuietMs"/> по часам подряд — дольше задержки многоуровневой компиляции (перекомпиляция на уровень 1 начинается
/// ~через 100 мс после вызовов), с пределом <see cref="MaxSeconds"/>. Зачем: замер «установившегося режима» (0 байт на тик, мс на тик)
/// после фиксированного числа тиков прогрева шёл, пока фоновый JIT ещё работал, — разовое выделение среды .NET фиксированного размера
/// (~24,6 КБ, рост её внутренней таблицы) попадало в тик того, кто её переполнил: флейк «0 Б/тик» <c>headless-smoke</c> (разные системы
/// в разных прогонах). Прогрев идёт отдельной симуляцией той же композиции (JIT — общий на процесс): замеряемая симуляция и её хеши не
/// меняются. Шаг — тик одной и той же тёплой симуляции: постройка заново каждые N тиков будила JIT снова (методы постройки
/// переходили на уровень 1), и затишье почти не наступало.
///
/// Предел — 1 с (замер Release, прогон Headless 600 тиков): JIT полностью утихает за ~1,8 с (волны перекомпиляции идут и
/// от новых событий боя далеко за концом замера); за 1 с методы тика — на уровне 1: у 10 врагов фонового JIT в окне замера нет, у толпы
/// из 32 — ~80 методов хвоста (без прогрева — ~1 550, с прежним пределом 3 с — 0). Схема «120 тиков → пауза 200 мс → 120 тиков» хуже:
/// в окне ~400–1 000 методов (пауза без вызовов не переводит методы на уровень 1). Греют только замеры, где 0 Б/тик судится: прогон
/// Headless без <c>--replay</c> от 240 тиков, бюджеты фич и <c>experiment.sweep</c> — в Release.
/// </summary>
public static class JitWarmup
{
    /// <summary>Сколько JIT должен молчать, мс (задержка многоуровневой компиляции — 100 мс, с запасом).</summary>
    public const double QuietMs = 250;

    /// <summary>Предел прогрева, с: полного затишья (~1,8 с в Release) не ждём — к пределу методы тика уже на уровне 1.</summary>
    public const double MaxSeconds = 1;

    /// <summary>
    /// Звать <paramref name="step"/>, пока JIT процесса не утих (методов не прибавилось <paramref name="quietMs"/> подряд) или не прошло
    /// <paramref name="maxSeconds"/>; шаг вернул ложь — шагов больше нет (итог — не утих).
    /// </summary>
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
