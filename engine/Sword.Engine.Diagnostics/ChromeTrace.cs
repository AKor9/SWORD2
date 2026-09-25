using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Sword.Engine.Diagnostics;

/// <summary>Период для трассы: кадр или тик, начало (отметка <see cref="Stopwatch"/>), заголовок, строка зон (время, вызовы) или без неё.</summary>
internal readonly record struct TracePeriod(bool Tick, ulong Number, long Start, ProfileHeader Header, long[]? Time, int[]? Calls, int Offset,
    bool Anomaly);

/// <summary>Что пишется в трассу: имена и родители зон, периоды, лента зон (точно) и события среды.</summary>
internal sealed class TraceData
{
    public required Func<int, string> Name { get; init; }
    public required int[] Parent { get; init; }
    public required int Zones { get; init; }
    public List<TracePeriod> Periods { get; } = [];
    public List<TimelineEntry>? Timeline { get; init; }
    public IReadOnlyList<RuntimeEvent>? Events { get; init; }
    public IReadOnlyDictionary<string, string>? Meta { get; init; }
}

/// <summary>
/// Экспорт профайлера в Chrome Trace Event JSON — открывается в Perfetto (ui.perfetto.dev, «Open trace file») и
/// <c>chrome://tracing</c>; своей панели нет (панель — этап 14). Три процесса трассы:
/// <list type="bullet">
/// <item><b>кольцо</b> (pid 1): кадры (tid 1) и тики (tid 2) с настоящими началом и длительностью, зоны внутри — события «X»
/// из итогов периода: у зоны — сумма её вызовов за период, дочерние лежат подряд от начала родителя в порядке номеров зон
/// (порядок заведения ≈ порядок вызова). Вложенность верна (дочерние не длиннее родителя), место внутри периода — условное;</item>
/// <item><b>лента зон</b> (pid 2, только в диагностическом режиме — <c>--profile runtime</c>): каждый вызов зоны с настоящим
/// временем — пары «B»/«E» по ленте владельца;</item>
/// <item><b>события среды</b> (pid 3, диагностический режим): JIT, сборки и паузы — «X» на треке своего потока, выборки
/// выделений — мгновенные «i».</item>
/// </list>
/// Время — микросекунды от начала первого периода (<c>ts</c>, <c>dur</c>), с шагом часов (0,1 мкс).
/// </summary>
public static class ChromeTrace
{
    public const int RingPid = 1, TimelinePid = 2, EventsPid = 3;
    public const int FramesTid = 1, TicksTid = 2, TimelineTid = 1;

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
        // Начало трассы — самое раннее из периодов, ленты и событий (событие окна могло начаться раньше первого периода): ts ≥ 0.
        long origin = long.MaxValue;
        if (data.Periods.Count > 0) origin = data.Periods.Min(p => p.Start);
        if (data.Timeline is { Count: > 0 } t) origin = Math.Min(origin, t[0].Time);
        if (data.Events is { Count: > 0 } ev) origin = Math.Min(origin, ev.Min(e => e.Time));
        if (origin == long.MaxValue) origin = 0;
        w.WriteStartObject();
        w.WriteString("displayTimeUnit", "ms");
        w.WriteStartObject("otherData");
        w.WriteString("format", "sword-profile-trace");
        w.WriteString("note", "pid 1 — кольцо профайлера: кадры и тики с настоящим временем, зоны — суммой вызовов за период подряд " +
            "(место внутри периода условное); pid 2 — лента зон (точно, диагностический режим); pid 3 — события среды (JIT, GC)");
        if (data.Meta is not null)
            foreach (var (k, v) in data.Meta) w.WriteString(k, v);
        w.WriteEndObject();
        w.WriteStartArray("traceEvents");

        Meta(w, "process_name", RingPid, 0, "SWORD: кольцо профайлера");
        Meta(w, "thread_name", RingPid, FramesTid, "кадры");
        Meta(w, "thread_name", RingPid, TicksTid, "тики");
        var children = Children(data);
        foreach (var p in data.Periods) Period(w, data, children, p, origin);

        if (data.Timeline is { Count: > 0 } timeline)
        {
            Meta(w, "process_name", TimelinePid, 0, "SWORD: лента зон (точно)");
            Meta(w, "thread_name", TimelinePid, TimelineTid, "главный поток");
            Timeline(w, data, timeline, origin);
        }
        if (data.Events is { Count: > 0 } events)
        {
            Meta(w, "process_name", EventsPid, 0, "SWORD: события среды");
            foreach (var thread in events.GroupBy(e => e.Thread))
                Meta(w, "thread_name", EventsPid, (int)thread.Key, thread.First().Main ? "среда: главный поток" : $"среда: поток {thread.Key}");
            foreach (var e in events) Event(w, e, origin);
        }
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
        var c = p.Header.Counters;
        w.WriteNumber("cpuMs", Math.Round(c.CpuMs, 5));
        w.WriteNumber("bytes", p.Header.Bytes);
        if (!p.Tick)
        {
            w.WriteNumber("ticks", p.Header.Ticks);
            w.WriteNumber("alpha", Math.Round(p.Header.Alpha, 4));
            w.WriteBoolean("service", p.Header.Service);
            if (p.Header.Tools) w.WriteBoolean("tools", true);   // кадр инструментов (запросы агента)
        }
        w.WriteNumber("gc", c.Collections);
        w.WriteNumber("jit", c.JitMethods);
        w.WriteNumber("jitProcess", c.JitProcess);
        if (p.Anomaly) w.WriteBoolean("anomaly", true);
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

    /// <summary>
    /// Лента зон — пары «B»/«E»: с первого открытия кадра или тика на глубине 0 (стек известен); раскрутка стека — концы зон выше
    /// новой глубины; открытое на конце окна закрывается последней отметкой.
    /// </summary>
    private static void Timeline(Utf8JsonWriter w, TraceData data, List<TimelineEntry> entries, long origin)
    {
        int first = entries.FindIndex(e => e.Depth == 0 && e.Kind is ProfileTimeline.Kind.FrameOpen or ProfileTimeline.Kind.TickOpen);
        if (first < 0) return;
        int open = 0;
        ulong frame = 0, tick = 0;
        long last = entries[first].Time;
        for (int i = first; i < entries.Count; i++)
        {
            var e = entries[i];
            last = e.Time;
            switch (e.Kind)
            {
                case ProfileTimeline.Kind.FrameOpen:
                    frame = (ulong)e.Arg;
                    break;
                case ProfileTimeline.Kind.TickOpen:
                    tick = (ulong)e.Arg;
                    break;
                case ProfileTimeline.Kind.Begin:
                    while (open > e.Depth - 1) EndEvent(w, e.Time, origin, ref open);
                    string name = e.Zone == CpuProfiler.FrameZone ? "frame " + frame.ToString(CultureInfo.InvariantCulture)
                        : e.Zone == CpuProfiler.TickZone ? "tick " + tick.ToString(CultureInfo.InvariantCulture) : data.Name(e.Zone);
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("cat", "zone");
                    w.WriteString("ph", "B");
                    w.WriteNumber("ts", Us(e.Time, origin));
                    w.WriteNumber("pid", TimelinePid);
                    w.WriteNumber("tid", TimelineTid);
                    w.WriteEndObject();
                    open++;
                    break;
                case ProfileTimeline.Kind.End:
                case ProfileTimeline.Kind.Depth:
                    while (open > e.Depth) EndEvent(w, e.Time, origin, ref open);
                    break;
            }
        }
        while (open > 0) EndEvent(w, last, origin, ref open);
    }

    private static void EndEvent(Utf8JsonWriter w, long time, long origin, ref int open)
    {
        w.WriteStartObject();
        w.WriteString("ph", "E");
        w.WriteNumber("ts", Us(time, origin));
        w.WriteNumber("pid", TimelinePid);
        w.WriteNumber("tid", TimelineTid);
        w.WriteEndObject();
        open--;
    }

    /// <summary>Событие среды: JIT, сборка, пауза — «X» на треке своего потока; выборка выделений — мгновенное «i».</summary>
    private static void Event(Utf8JsonWriter w, RuntimeEvent e, long origin)
    {
        w.WriteStartObject();
        string name = e.Kind switch
        {
            "jit" => $"JIT {e.Name} ({e.Tier})",
            "gc" => $"GC gen{e.Gen} ({e.Name})",
            "pause" => $"пауза GC ({e.Name})",
            _ => $"выделение {e.Name}",
        };
        w.WriteString("name", name);
        w.WriteString("cat", e.Kind);
        if (e.Kind == "alloc")
        {
            w.WriteString("ph", "i");
            w.WriteString("s", "t");
        }
        else
        {
            w.WriteString("ph", "X");
            w.WriteNumber("dur", Math.Round(e.Ms * 1000, 1));
        }
        w.WriteNumber("ts", Math.Round(Us(e.Time, origin), 1));
        w.WriteNumber("pid", EventsPid);
        w.WriteNumber("tid", (int)e.Thread);
        w.WriteStartObject("args");
        if (e.Bytes > 0) w.WriteNumber("bytes", e.Bytes);
        if (e.Zone is not null) w.WriteString("zone", e.Zone);
        if (e.Place.Frame is { } f) w.WriteNumber("frame", f);
        if (e.Place.Tick is { } t) w.WriteNumber("tick", t);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
