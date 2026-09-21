namespace Sword.Engine.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Запись журнала: уровень, категория (подсистема латиницей: «client», «d3d12»), текст.</summary>
public readonly record struct LogEntry(DateTime Utc, LogLevel Level, string Category, string Message);

/// <summary>Приёмник записей журнала.</summary>
public interface ILogSink
{
    void Write(in LogEntry entry);
}

/// <summary>
/// Журнал движка вместо <c>Console.WriteLine("[sword] …")</c>: уровень, категория, приёмники. По умолчанию пишет
/// в консоль. Кольцо последних записей для агента (<c>log.tail</c>) и запись без аллокаций — на следующих точках
/// (разделы 7 и 8 ARCHITECTURE_PLAN.md).
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static ILogSink[] _sinks = [new ConsoleLogSink()];

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void AddSink(ILogSink sink)
    {
        lock (Gate) _sinks = [.. _sinks, sink];
    }

    public static void RemoveSink(ILogSink sink)
    {
        lock (Gate) _sinks = [.. _sinks.Where(s => !ReferenceEquals(s, sink))];
    }

    /// <summary>Заменить все приёмники (например, журнал — в stderr, когда stdout занят JSON для агента).</summary>
    public static void SetSinks(params ILogSink[] sinks)
    {
        lock (Gate) _sinks = [.. sinks];
    }

    public static void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel) return;
        var entry = new LogEntry(DateTime.UtcNow, level, category, message);
        var sinks = Volatile.Read(ref _sinks);
        lock (Gate)
            foreach (var sink in sinks)
                sink.Write(entry);
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);

    public static void Warning(string category, string message) => Write(LogLevel.Warning, category, message);

    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
}

/// <summary>
/// Консоль: <c>[категория] текст</c>; предупреждения и ошибки помечены и идут в stderr.
/// </summary>
/// <param name="allToStandardError">Всё в stderr — когда stdout занят машинным выводом (JSON для агента).</param>
public sealed class ConsoleLogSink(bool allToStandardError = false) : ILogSink
{
    public void Write(in LogEntry entry)
    {
        string mark = entry.Level switch
        {
            LogLevel.Warning => "внимание: ",
            LogLevel.Error => "ошибка: ",
            _ => "",
        };
        var output = allToStandardError || entry.Level >= LogLevel.Warning ? Console.Error : Console.Out;
        output.WriteLine($"[{entry.Category}] {mark}{entry.Message}");
    }
}
