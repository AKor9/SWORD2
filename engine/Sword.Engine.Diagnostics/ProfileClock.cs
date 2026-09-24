namespace Sword.Engine.Diagnostics;

/// <summary>
/// Источник времени и счётчиков среды профайлера — подмена для тестов (поддельные часы): длительности зон и периодов, время
/// потока на CPU, GC и JIT задаёт тест, а не машина, — суд аномалий, их класс, ведро жетонов и лимиты файлов не зависят от
/// нагрузки (параллельные сборки и тесты, чужие процессы). Только внутренний: профайлер на часах — внутренний конструктор
/// <see cref="CpuProfiler(int, ProfileClock)"/> (тесты — через <c>InternalsVisibleTo</c>); у продукта часов нет, и профайлер
/// читает <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> и <see cref="ProfileCounters.Read"/> (проверка поля на null —
/// в невстраиваемых <c>Push/Pop</c>, опросе счётчиков и суде включённого; выключенный её не делает). Не для диагностического
/// режима: события среды и снимки машины идут по настоящему времени.
/// </summary>
internal abstract class ProfileClock
{
    /// <summary>Сейчас — отсчёты <see cref="System.Diagnostics.Stopwatch"/>.</summary>
    public abstract long Timestamp();

    /// <summary>Счётчики среды потока-владельца сейчас (такты потока, сборки и пауза GC, JIT).</summary>
    public abstract ProfileCounters Counters();
}
