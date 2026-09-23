using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Файл аномалии (точка Пв, формат <see cref="ProfileAnomalies.FileFormat"/>) и её трасса — на потоке писателя, из копии
/// владельца (<see cref="AnomalySlot"/>): период и порог, разбор причин и класс, главная зона с путём, зоны по своему лишнему
/// времени и дерево зон, тик кадра App, последние периоды и счётчики окна, события среды окна (диагностический режим) или
/// только счётчики, машина за окно с вердиктом.
/// </summary>
internal static class AnomalyReport
{
    /// <summary>Зон в плоском списке файла (по своему лишнему времени).</summary>
    public const int TopZones = 20;

    public static JsonObject Build(AnomalySlot slot, ProfileAnomaly record, Func<int, string> name, AnomalyOptions options, bool eventsFlushed,
        string? traceFile)
    {
        var ci = CultureInfo.InvariantCulture;
        int root = slot.Period == ProfilePeriod.Frame ? CpuProfiler.FrameZone : CpuProfiler.TickZone;
        var json = new JsonObject
        {
            ["format"] = ProfileAnomalies.FileFormat,
            ["version"] = 1,
        };
        if (options.Run is not null)
            foreach (var (k, v) in options.Run) json[k] = v;
        json["sequence"] = record.Sequence;
        json["epoch"] = slot.Epoch;
        json["period"] = slot.Period == ProfilePeriod.Frame ? "frame" : "tick";
        json["number"] = slot.Number;
        json["trigger"] = slot.TickTrigger ? "tick" : json["period"]!.GetValue<string>();
        json["text"] = record.Text;
        json["ms"] = R(CpuProfiler.Ms(slot.Header.Time));
        json["medianMs"] = R(CpuProfiler.Ms(slot.Median));
        json["thresholdMs"] = R(CpuProfiler.Ms(slot.Threshold));
        json["threshold"] = new JsonObject
        {
            ["mode"] = slot.AbsoluteMs > 0 ? "absolute" : "median",
            ["absoluteMs"] = slot.AbsoluteMs,
            ["relativeExcess"] = options.RelativeExcess,
            ["minExcessMs"] = slot.Period == ProfilePeriod.Frame ? options.FrameMinExcessMs : options.TickMinExcessMs,
            ["window"] = options.Window,
            ["rule"] = slot.AbsoluteMs > 0 ? "период дольше absoluteMs" : "период дольше медианы окна + max(minExcessMs, relativeExcess × медиана)",
        };
        json["class"] = AnomalyCauses.Key(record.Class);
        json["classText"] = AnomalyCauses.Word(record.Class);
        // После Пв: заметная — кадр App не короче интервала показа (порог суда — «дольше обычного», заметность — «видно глазу»).
        json["noticeable"] = record.Noticeable;
        json["display"] = new JsonObject
        {
            ["noticeableMs"] = R(CpuProfiler.Ms(slot.NoticeableTicks)),
            ["hz"] = slot.DisplayHz > 0 ? Math.Round(slot.DisplayHz, 3) : null,
            ["source"] = slot.DisplayHz > 0 ? "monitor" : "default",
            ["periodMs"] = R(CpuProfiler.Ms(slot.Header.Time)),
        };
        json["causes"] = record.Causes.ToJson();
        json["causesNote"] = "excessMs — сверх медианы; jitMs — компиляция на главном потоке; gcPauseMs — паузы сборок; gpuWaitMs — ожидание GPU " +
            "(frame.fenceWait, frame.present) сверх среднего — по проходам GPU: секция gpu; offCpuMs — поток вне CPU вне ожиданий (другие процессы, ОС); " +
            "codeMs — остаток: код";

        // Зоны аномального периода (у кадра со сработавшим тиком — и тика).
        var zones = Zones(slot.Zones, slot.Time, slot.Bytes, slot.Calls, slot.Mean, slot.Parent, slot.Kind, root, name);
        int main = slot.TickTrigger ? slot.TickZone : slot.MainZone;
        var mainZones = slot.TickTrigger && slot.HasTick
            ? Zones(slot.Zones, slot.TickTime, slot.TickBytes, slot.TickCalls, slot.TickMean, slot.Parent, slot.Kind, CpuProfiler.TickZone, name)
            : zones;
        json["mainZone"] = mainZones.ZoneJson(main, withPath: true);
        json["header"] = HeaderJson(slot.Header, slot.Period);
        json["zones"] = zones.Top(TopZones);
        json["tree"] = zones.Tree(root);
        // Точка Пг: GPU-время кадра, забора которого ждал этот кадр, — по проходам (кадр N − 3: запаздывание разбора).
        if (slot.Period == ProfilePeriod.Frame)
            json["gpu"] = zones.Gpu(slot.Number, slot.Header.GpuFrame) ?? (JsonNode)new JsonObject
            {
                ["note"] = "GPU-времён в этом кадре нет: профайлер включён меньше трёх кадров назад, кадр без рендера или разбор был раньше",
            };
        if (slot.HasTick)
        {
            var tick = Zones(slot.Zones, slot.TickTime, slot.TickBytes, slot.TickCalls, slot.TickMean, slot.Parent, slot.Kind, CpuProfiler.TickZone, name);
            json["tick"] = new JsonObject
            {
                ["number"] = slot.TickNumber,
                ["anomaly"] = true,
                ["ms"] = R(CpuProfiler.Ms(slot.TickHeader.Time)),
                ["medianMs"] = R(CpuProfiler.Ms(slot.TickMedian)),
                ["thresholdMs"] = R(CpuProfiler.Ms(slot.TickThreshold)),
                ["class"] = AnomalyCauses.Key(slot.TickCauses.Class),
                ["classText"] = AnomalyCauses.Word(slot.TickCauses.Class),
                ["causes"] = slot.TickCauses.ToJson(),
                ["mainZone"] = tick.ZoneJson(slot.TickZone, withPath: true),
                ["header"] = HeaderJson(slot.TickHeader, ProfilePeriod.Tick),
                ["zones"] = tick.Top(12),
            };
        }

        // Последние периоды (последний — аномальный) и счётчики окна.
        var recent = new JsonArray();
        var window = default(ProfileCounters);
        long windowTime = 0, windowBytes = 0;
        for (int i = 0; i < slot.Recent; i++)
        {
            var h = slot.RecentHeaders[i];
            var entry = HeaderJson(h, slot.Period);
            entry.Insert(0, slot.Period == ProfilePeriod.Frame ? "frame" : "tick", h.Number);
            entry["startMs"] = R(CpuProfiler.Ms(slot.RecentStart[i] - slot.WindowStart));
            if (i == slot.Recent - 1) entry["anomaly"] = true;
            recent.Add(entry);
            window = window.Plus(h.Counters);
            windowTime += h.Time;
            windowBytes += h.Bytes;
        }
        json["recent"] = recent;
        json["window"] = new JsonObject
        {
            ["periods"] = slot.Recent,
            ["spanMs"] = R(CpuProfiler.Ms(slot.End - slot.WindowStart)),
            ["periodsMs"] = R(CpuProfiler.Ms(windowTime)),
            ["cpuMs"] = R(window.CpuMs),
            ["gc"] = window.Collections,
            ["pauseMs"] = R(window.PauseMs),
            ["jit"] = window.JitMethods,
            ["jitMs"] = R(window.JitMs),
            ["jitProcess"] = window.JitProcess,
            ["jitProcessMs"] = R(window.JitProcessMs),
            ["processBytes"] = window.ProcessBytes,
            ["threadBytes"] = windowBytes,
        };
        if (slot.RecentTicks > 0)
        {
            var ticks = new JsonArray();
            for (int i = 0; i < slot.RecentTicks; i++)
            {
                var h = slot.RecentTickHeaders[i];
                var entry = HeaderJson(h, ProfilePeriod.Tick);
                entry.Insert(0, "tick", h.Number);
                ticks.Add(entry);
            }
            json["windowTicks"] = ticks;
        }

        json["events"] = EventsJson(slot, eventsFlushed);
        if (slot.HasBaseline)
        {
            var machine = MachineReport.Between(slot.Baseline, slot.Now, slot.ComputeBound).ToJson();
            machine.Insert(0, "window", "с последнего снимка машины (раз в секунду) до аномалии");
            json["machine"] = machine;
        }
        if (traceFile is not null) json["trace"] = traceFile;
        json["written"] = DateTime.Now.ToString("s", ci);
        return json;
    }

    /// <summary>Трасса окна аномалии: кольцо (последние периоды с зонами, тики окна App), лента зон и события среды (диагностический режим).</summary>
    public static void WriteTrace(AnomalySlot slot, Func<int, string> name, string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        WriteTrace(slot, name, stream);
    }

    /// <summary>Трасса окна аномалии — в поток (прогрев писателя пишет её в <see cref="Stream.Null"/>).</summary>
    public static void WriteTrace(AnomalySlot slot, Func<int, string> name, Stream stream)
    {
        List<TimelineEntry>? timeline = null;
        if (slot.Timeline is { } t)
        {
            timeline = [];
            if (!t.Read(slot.WindowStart, slot.End, timeline)) timeline = null;
        }
        var data = new TraceData
        {
            Name = name, Parent = slot.Parent, Zones = slot.Zones, Timeline = timeline, Events = WindowEvents(slot),
            Meta = new Dictionary<string, string> { ["anomaly"] = $"{(slot.Period == ProfilePeriod.Frame ? "frame" : "tick")} {slot.Number}" },
        };
        bool tick = slot.Period == ProfilePeriod.Tick;
        for (int i = 0; i < slot.Recent; i++)
        {
            var h = slot.RecentHeaders[i];
            data.Periods.Add(new TracePeriod(tick, h.Number, slot.RecentStart[i], h, slot.RecentTime, slot.RecentCalls, i * slot.Stride, i == slot.Recent - 1));
        }
        for (int i = 0; i < slot.RecentTicks; i++)
        {
            var h = slot.RecentTickHeaders[i];
            data.Periods.Add(new TracePeriod(true, h.Number, slot.RecentTickStart[i], h, null, null, 0, slot.HasTick && h.Number == slot.TickNumber));
        }
        ChromeTrace.Write(stream, data);
    }

    private static List<RuntimeEvent>? WindowEvents(AnomalySlot slot)
    {
        if (slot.Events is not { } events) return null;
        long from = slot.WindowStart, to = slot.End;
        return [.. events.Recent().Where(e => e.Time + (long)(e.Ms * Stopwatch.Frequency / 1000) >= from && e.Time <= to)];
    }

    /// <summary>События среды: в аномальном периоде и в окне; без диагностического режима — только счётчики (заголовки).</summary>
    private static JsonNode EventsJson(AnomalySlot slot, bool flushed)
    {
        if (slot.Events is null)
            return new JsonObject
            {
                ["mode"] = "counters",
                ["note"] = "событий нет (диагностический режим выключен): JIT, GC и выделения — счётчики периода (header) и окна (window); " +
                    "что именно — --profile runtime или profile.stages {\"events\":true}",
            };
        var all = WindowEvents(slot)!;
        long start = slot.Start, end = slot.End;
        bool InPeriod(RuntimeEvent e) => e.Time + (long)(e.Ms * Stopwatch.Frequency / 1000) >= start && e.Time <= end;
        var inPeriod = all.Where(InPeriod).ToList();
        return new JsonObject
        {
            ["mode"] = "events",
            ["flushed"] = flushed,
            ["period"] = Summary(inPeriod),
            ["window"] = Summary(all),
        };
    }

    private static JsonObject Summary(List<RuntimeEvent> events)
    {
        var alloc = events.Where(e => e.Kind == "alloc").ToList();
        return new JsonObject
        {
            ["jit"] = new JsonArray([.. events.Where(e => e.Kind == "jit").OrderByDescending(e => e.Ms).Take(40).Select(e => (JsonNode?)RuntimeEvents.ToJson(e))]),
            ["gc"] = new JsonArray([.. events.Where(e => e.Kind is "gc" or "pause").Take(40).Select(e => (JsonNode?)RuntimeEvents.ToJson(e))]),
            ["alloc"] = new JsonObject
            {
                ["samples"] = alloc.Count,
                ["bytes"] = alloc.Sum(e => e.Bytes),
                ["types"] = new JsonArray([.. alloc.GroupBy(e => e.Name).OrderByDescending(g => g.Sum(e => e.Bytes)).Take(15).Select(g => (JsonNode?)new JsonObject
                {
                    ["type"] = g.Key, ["samples"] = g.Count(), ["bytes"] = g.Sum(e => e.Bytes), ["mainThread"] = g.Count(e => e.Main),
                    ["zones"] = new JsonArray([.. g.Where(e => e.Zone is not null).Select(e => e.Zone!).Distinct().Take(5).Select(z => (JsonNode?)z)]),
                })]),
            },
        };
    }

    private static JsonObject HeaderJson(in ProfileHeader h, ProfilePeriod period)
    {
        var c = h.Counters;
        var json = new JsonObject { ["ms"] = R(CpuProfiler.Ms(h.Time)) };
        if (period == ProfilePeriod.Frame)
        {
            json["ticks"] = new JsonArray(h.Ticks > 0 ? h.FirstTick : null, h.Ticks);
            json["alpha"] = Math.Round(h.Alpha, 4);
            json["service"] = h.Service;
            if (h.Tools) json["tools"] = true;   // после Пв: кадр инструментов (запросы агента) — служебный, как кадр не судится
            if (h.GpuFrame != ulong.MaxValue) json["gpuFrame"] = h.GpuFrame;   // точка Пг: чьи GPU-времена в кадре
        }
        else if (h.Frame != ulong.MaxValue) json["inFrame"] = h.Frame;
        json["cpuMs"] = R(c.CpuMs);
        json["onCpuPct"] = h.Time > 0 ? Math.Round(Math.Min(1, c.CpuMs / CpuProfiler.Ms(h.Time)) * 100, 1) : 0;
        json["bytes"] = h.Bytes;
        json["processBytes"] = c.ProcessBytes;
        json["gc"] = c.Collections;
        json["pauseMs"] = R(c.PauseMs);
        json["jit"] = c.JitMethods;
        json["jitMs"] = R(c.JitMs);
        json["jitProcess"] = c.JitProcess;
        json["jitProcessMs"] = R(c.JitProcessMs);
        return json;
    }

    private static double R(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0;

    private static ZoneTable Zones(int zones, long[] time, long[] bytes, int[] calls, long[] mean, int[] parent, byte[] kind, int root, Func<int, string> name)
    {
        var excess = new long[zones];
        var self = new long[zones];
        for (int z = 0; z < zones; z++) self[z] = excess[z] = time[z] - mean[z];
        for (int z = 0; z < zones; z++)
            if (parent[z] >= 0 && z != root) self[parent[z]] -= excess[z];
        return new ZoneTable(zones, time, bytes, calls, mean, parent, kind, excess, self, name);
    }

    /// <summary>Зоны периода с лишним временем (против среднего) и своим лишним (без дочерних).</summary>
    private sealed record ZoneTable(int Count, long[] Time, long[] Bytes, int[] Calls, long[] Mean, int[] Parent, byte[] Kind, long[] Excess,
        long[] Self, Func<int, string> Name)
    {
        public JsonObject ZoneJson(int z, bool withPath)
        {
            var json = new JsonObject
            {
                ["name"] = Name(z),
                ["ms"] = R(CpuProfiler.Ms(Time[z])),
                ["meanMs"] = R(CpuProfiler.Ms(Mean[z])),
                ["excessMs"] = R(CpuProfiler.Ms(Excess[z])),
                ["selfExcessMs"] = R(CpuProfiler.Ms(Self[z])),
                ["calls"] = Calls[z],
                ["bytes"] = Bytes[z],
            };
            if (Kind[z] != 0) json["kind"] = ((ProfileZoneKind)Kind[z]).ToString();
            if (withPath) json["path"] = Path(z);
            else json["parent"] = Parent[z] >= 0 ? Name(Parent[z]) : null;
            return json;
        }

        public string Path(int z)
        {
            var names = new List<string>();
            for (int i = 0; z >= 0 && i <= CpuProfiler.MaxDepth; i++, z = Parent[z]) names.Add(Name(z));
            names.Reverse();
            return string.Join(" > ", names);
        }

        /// <summary>Зоны периода по своему лишнему времени (первая — главная).</summary>
        public JsonArray Top(int n) => new([.. Enumerable.Range(0, Count).Where(z => Calls[z] > 0 && Kind[z] != (byte)ProfileZoneKind.Gpu)
            .OrderByDescending(z => Self[z]).Take(n).Select(z => (JsonNode?)ZoneJson(z, withPath: false))]);

        /// <summary>
        /// GPU-времена, записанные в кадр (точка Пг, зоны вида <see cref="ProfileZoneKind.Gpu"/>): кадр целиком и проходы по лишнему
        /// времени против среднего. Нет — null.
        /// </summary>
        public JsonObject? Gpu(ulong number, ulong gpuFrame)
        {
            var gpu = Enumerable.Range(0, Count).Where(z => Kind[z] == (byte)ProfileZoneKind.Gpu && Calls[z] > 0).ToList();
            if (gpu.Count == 0) return null;
            int frame = gpu.FirstOrDefault(z => Name(z) == CpuProfiler.GpuFrameName, -1);
            bool known = gpuFrame != ulong.MaxValue && gpuFrame <= number;
            var json = new JsonObject
            {
                ["frame"] = known ? gpuFrame : null,
                ["lagFrames"] = known ? (long)(number - gpuFrame) : null,
                ["note"] = "GPU-время кадра frame (точка Пг), разобранного в этом кадре после ожидания его слота: frame.fenceWait этого кадра ждал " +
                    "именно его (и кадры до него) — при классе gpu лишнее GPU-время видно здесь (excessMs — против среднего); frame.present ждёт " +
                    "очередь показа — её кадры (этот и два до него) ещё не разобраны, их GPU-времени здесь нет; частота GPU плавает",
            };
            if (frame >= 0)
            {
                json["frameMs"] = R(CpuProfiler.Ms(Time[frame]));
                json["meanFrameMs"] = R(CpuProfiler.Ms(Mean[frame]));
                json["excessFrameMs"] = R(CpuProfiler.Ms(Excess[frame]));
            }
            json["passes"] = new JsonArray([.. gpu.Where(z => z != frame).OrderByDescending(z => Excess[z]).Select(z => (JsonNode?)new JsonObject
            {
                ["name"] = Name(z).StartsWith(CpuProfiler.GpuPrefix, StringComparison.Ordinal) ? Name(z)[CpuProfiler.GpuPrefix.Length..] : Name(z),
                ["ms"] = R(CpuProfiler.Ms(Time[z])),
                ["meanMs"] = R(CpuProfiler.Ms(Mean[z])),
                ["excessMs"] = R(CpuProfiler.Ms(Excess[z])),
                ["calls"] = Calls[z],
            })]);
            return json;
        }

        /// <summary>Дерево зон от корня периода: дочерние по времени; зоны без вызовов в периоде — только с заметным лишним.</summary>
        public JsonObject Tree(int root) => Node(root, 0);

        private JsonObject Node(int z, int depth)
        {
            var json = ZoneJson(z, withPath: false);
            json.Remove("parent");
            if (depth >= CpuProfiler.MaxDepth) return json;
            var kids = Enumerable.Range(0, Count)
                .Where(c => c != z && Parent[c] == z && (Calls[c] > 0 || Math.Abs(CpuProfiler.Ms(Excess[c])) >= 0.001))
                .OrderByDescending(c => Time[c]).ToList();
            if (kids.Count > 0) json["children"] = new JsonArray([.. kids.Select(c => (JsonNode?)Node(c, depth + 1))]);
            return json;
        }
    }
}
