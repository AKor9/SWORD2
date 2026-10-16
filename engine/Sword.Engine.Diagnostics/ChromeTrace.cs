using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Sword.Engine.Diagnostics;

/// <summary>Период для трассы: кадр или тик, начало (отметка <see cref="Stopwatch"/>), заголовок, строка зон (время, вызовы) или без неё.</summary>
internal readonly record struct TracePeriod(bool Tick, ulong Number, long Start, ProfileHeader Header, long[]? Time, int[]? Calls, int Offset);

/// <summary>Что пишется в трассу: имена и родители зон, периоды.</summary>
internal sealed class TraceData
{
    public required Func<int, string> Name { get; init; }
    public required int[] Parent { get; init; }
    public required int Zones { get; init; }
    public List<TracePeriod> Periods { get; } = [];
    public IReadOnlyDictionary<string, string>? Meta { get; init; }
}

/// <summary>
/// Экспорт профайлера в Chrome Trace Event JSON (Perfetto, <c>chrome://tracing</c>): кадры (tid 1) и тики (tid 2) кольца с
/// настоящим временем; зоны — события «X» из итогов периода, дочерние подряд от начала родителя (вложенность верна, место
/// условное). Время — мкс от начала первого периода.
/// </summary>
public static class ChromeTrace
{
    public const int RingPid = 1;
    public const int FramesTid = 1, TicksTid = 2;

    internal static void Write(string path, TraceData data)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        Write(stream, data);
    }

    internal static void Write(Stream stream, TraceData data)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        // Начало трассы — начало первого периода: ts ≥ 0.
        long origin = data.Periods.Count > 0 ? data.Periods.Min(p => p.Start) : 0;
        w.WriteStartObject();
        w.WriteString("displayTimeUnit", "ms");
        w.WriteStartObject("otherData");
        w.WriteString("format", "sword-profile-trace");
        w.WriteString("note", "кольцо профайлера: кадры и тики с настоящим временем, зоны — суммой вызовов за период подряд " +
            "(место внутри периода условное)");
        if (data.Meta is not null)
            foreach (var (k, v) in data.Meta) w.WriteString(k, v);
        w.WriteEndObject();
        w.WriteStartArray("traceEvents");

        Meta(w, "process_name", RingPid, 0, "SWORD: кольцо профайлера");
        Meta(w, "thread_name", RingPid, FramesTid, "кадры");
        Meta(w, "thread_name", RingPid, TicksTid, "тики");
        var children = Children(data);
        foreach (var p in data.Periods) Period(w, data, children, p, origin);

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Отсчёты <see cref="Stopwatch"/> от начала — микросекунды (у часов 10 МГц — ровно десятые доли).</summary>
    private static double Us(long ticks, long origin) => (double)((ticks - origin) * 1_000_000) / Stopwatch.Frequency;

    private static double UsSpan(long ticks) => (double)(ticks * 1_000_000) / Stopwatch.Frequency;

    private static void Meta(Utf8JsonWriter w, string name, int pid, int tid, string value)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("ph", "M");
        w.WriteNumber("pid", pid);
        w.WriteNumber("tid", tid);
        w.WriteStartObject("args");
        w.WriteString("name", value);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>Дочерние зоны по родителю — в порядке номеров; зоны с разными родителями не вкладываются.</summary>
    private static List<int>[] Children(TraceData data)
    {
        var children = new List<int>[data.Zones];
        for (int z = 0; z < data.Zones; z++) children[z] = [];
        for (int z = 0; z < data.Zones; z++)
        {
            int p = data.Parent[z];
            if (p >= 0 && p < data.Zones && p != z) children[p].Add(z);
        }
        return children;
    }

    private static void Period(Utf8JsonWriter w, TraceData data, List<int>[] children, TracePeriod p, long origin)
    {
        int root = p.Tick ? CpuProfiler.TickZone : CpuProfiler.FrameZone;
        int tid = p.Tick ? TicksTid : FramesTid;
        long start = p.Start, time = p.Header.Time;
        X(w, (p.Tick ? "tick " : "frame ") + p.Number.ToString(CultureInfo.InvariantCulture), p.Tick ? "tick" : "frame", tid, start, time, origin);
        w.WriteStartObject("args");
        w.WriteNumber("ms", Math.Round(CpuProfiler.Ms(time), 5));
        w.WriteNumber("bytes", p.Header.Bytes);
        if (!p.Tick)
        {
            w.WriteNumber("ticks", p.Header.Ticks);
            w.WriteNumber("alpha", Math.Round(p.Header.Alpha, 4));
            w.WriteBoolean("service", p.Header.Service);
        }
        w.WriteEndObject();
        w.WriteEndObject();
        if (p.Time is null || p.Calls is null) return;
        Children(w, data, children, p, root, start, start + time, tid, origin, depth: 0);
    }

    /// <summary>Дочерние зоны <paramref name="zone"/> — подряд от <paramref name="from"/>, не дальше <paramref name="to"/>.</summary>
    private static void Children(Utf8JsonWriter w, TraceData data, List<int>[] children, TracePeriod p, int zone, long from, long to, int tid,
        long origin, int depth)
    {
        if (depth > CpuProfiler.MaxDepth) return;
        long cursor = from;
        foreach (int c in children[zone])
        {
            int calls = p.Calls![p.Offset + c];
            if (calls == 0) continue;
            long real = p.Time![p.Offset + c];
            long dur = Math.Min(real, to - cursor);
            if (dur <= 0) continue;
            X(w, data.Name(c), "zone", tid, cursor, dur, origin);
            w.WriteStartObject("args");
            w.WriteNumber("ms", Math.Round(CpuProfiler.Ms(real), 5));
            w.WriteNumber("calls", calls);
            w.WriteEndObject();
            w.WriteEndObject();
            Children(w, data, children, p, c, cursor, cursor + dur, tid, origin, depth + 1);
            cursor += dur;
        }
    }

    /// <summary>Начало события «X» (объект открыт: вызывающий дописывает args и закрывает).</summary>
    private static void X(Utf8JsonWriter w, string name, string cat, int tid, long start, long dur, long origin)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("cat", cat);
        w.WriteString("ph", "X");
        w.WriteNumber("ts", Us(start, origin));
        w.WriteNumber("dur", UsSpan(dur));
        w.WriteNumber("pid", RingPid);
        w.WriteNumber("tid", tid);
    }
}
