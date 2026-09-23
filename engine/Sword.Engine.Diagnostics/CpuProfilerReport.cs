using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Engine.Diagnostics;

public sealed partial class CpuProfiler
{
    /// <summary>Формат отчёта (<c>profile.json</c>, секция <c>profile</c> в <c>--json</c>, <c>profile.stages</c>).</summary>
    public const string ReportFormat = "sword-profile";

    /// <summary>
    /// Отчёт профайлера (не горячий путь — выделяет память): по кадрам и по тикам — число периодов (и служебных кадров),
    /// окно кольца, зоны (<see cref="Stats"/>: мс — среднее, медиана, p99, максимум, своё; байты и вызовы на период; доля
    /// периодов) и последние периоды кольца (кадр — тики кадра и доля тика). Нет периодов вида — его секции нет.
    /// </summary>
    /// <param name="history">Сколько последних периодов кольца выписать.</param>
    /// <param name="top">Сколько зон выписать (по среднему).</param>
    /// <param name="prefix">Только зоны с именем с этого начала (<c>stage.</c>, <c>render.</c>…); null — все.</param>
    public JsonObject Report(int history = int.MaxValue, int top = int.MaxValue, string? prefix = null)
    {
        var json = new JsonObject
        {
            ["format"] = ReportFormat,
            ["version"] = 2,
            ["enabled"] = Enabled,
            ["requested"] = Requested,
            ["timer"] = new JsonObject
            {
                ["frequency"] = Stopwatch.Frequency, ["resolutionUs"] = Round(1e6 / Stopwatch.Frequency, 4),
                ["tscMhz"] = Round(ThreadClock.CyclesPerSecond / 1e6, 1),
            },
            ["zones"] = _names.Count,
            ["mismatches"] = Mismatches,
            ["overflows"] = Overflows,
            ["abandoned"] = Abandoned,
            ["counterReads"] = CounterReads,
        };
        // Машина с начала окна (включение или сброс) — снимается здесь, на потоке владельца (точка Пб).
        if (Machine() is { } machine) json["machine"] = machine.ToJson();
        if (PeriodJson(ProfilePeriod.Frame, history, top, prefix) is { } frames) json["frames"] = frames;
        if (PeriodJson(ProfilePeriod.Tick, history, top, prefix) is { } ticks) json["ticks"] = ticks;
        if (_events is { } events) json["events"] = events.Summary(types: 40, recent: 100);
        // Точка Пв: захваченные аномалии (порог, счёт, последние с классом, главной зоной и файлом).
        if (_anomalies is { } anomalies) json["anomalies"] = anomalies.ToJson(max: 100);
        return json;
    }

    /// <summary>
    /// Коротко — для итога <c>--json</c>: периодов, служебных кадров и <paramref name="top"/> зон по среднему (кадра, если
    /// кадры были, иначе тика) без дочерних подробностей.
    /// </summary>
    public JsonObject Summary(int top = 10)
    {
        var period = Count(ProfilePeriod.Frame).Periods > 0 ? ProfilePeriod.Frame : ProfilePeriod.Tick;
        var (frames, service) = Count(ProfilePeriod.Frame);
        var json = new JsonObject
        {
            ["enabled"] = Enabled,
            ["frames"] = frames,
            ["serviceFrames"] = service,
            ["toolFrames"] = ToolFrames,
            ["ticks"] = Count(ProfilePeriod.Tick).Periods,
            ["mismatches"] = Mismatches,
            ["per"] = period == ProfilePeriod.Frame ? "frame" : "tick",
            ["top"] = new JsonArray([.. Stats(period).Where(z => z.Kind != ProfileZoneKind.Gpu).Take(top).Select(z => (JsonNode?)new JsonObject
            {
                ["name"] = z.Name, ["meanMs"] = Round(z.MeanMs, 5), ["p99Ms"] = Round(z.P99Ms, 5), ["selfMs"] = Round(z.SelfMs, 5),
                ["bytes"] = Round(z.Bytes, 1),
            })]),
            // Точка Пб: среда за период (CPU потока против настенного, GC, JIT) — коротко.
            ["runtime"] = RuntimeJson(RuntimeStats(period)),
        };
        // Точка Пг: GPU-время кадра и главные проходы (мс на кадр) — рядом с зонами CPU.
        if (period == ProfilePeriod.Frame && GpuJson(Stats(period), top: 5) is { } gpu) json["gpu"] = gpu;
        if (_events is { } events)
        {
            var summary = events.Summary(types: 5, recent: 3);
            json["events"] = new JsonObject
            {
                ["received"] = summary["received"]?.DeepClone(), ["late"] = summary["late"]?.DeepClone(),
                ["alloc"] = summary["alloc"]?["types"]?.DeepClone(), ["jit"] = summary["jit"]?.DeepClone(), ["gc"] = summary["gc"]?.DeepClone(),
            };
        }
        if (_anomalies is { } anomalies)
        {
            // Точка Пв: аномалии коротко — сколько, по классам, последние пять.
            var a = anomalies.ToJson(max: 5);
            json["anomalies"] = new JsonObject
            {
                ["detected"] = a["detected"]?.DeepClone(), ["sinceReset"] = a["sinceReset"]?.DeepClone(), ["written"] = a["written"]?.DeepClone(),
                ["byClass"] = a["byClass"]?.DeepClone(), ["noticeable"] = a["noticeable"]?.DeepClone(), ["display"] = a["display"]?.DeepClone(),
                ["threshold"] = a["threshold"]?.DeepClone(), ["limits"] = a["limits"]?.DeepClone(), ["directory"] = a["directory"]?.DeepClone(),
                ["last"] = a["list"]?.DeepClone(),
            };
        }
        return json;
    }

    private JsonObject? PeriodJson(ProfilePeriod period, int history, int top, string? prefix)
    {
        var (count, service) = Count(period);
        if (count == 0 && service == 0) return null;
        var recent = Recent(period, history);
        int window = Recent(period, int.MaxValue).Count(h => !h.Service);
        var stats = Stats(period);
        var zones = stats.Where(z => z.Kind != ProfileZoneKind.Gpu && (prefix is null || z.Name.StartsWith(prefix, StringComparison.Ordinal))).Take(top);
        var json = new JsonObject
        {
            ["count"] = count,
            ["window"] = window,
            ["runtime"] = RuntimeJson(RuntimeStats(period)),
            ["zones"] = new JsonArray([.. zones.Select(z => (JsonNode?)ZoneJson(z))]),
        };
        if (period == ProfilePeriod.Frame)
        {
            json.Insert(1, "service", service);
            json.Insert(2, "toolFrames", ToolFrames);   // после Пв: из служебных — кадры инструментов (запросы агента)
        }
        // Точка Пг: GPU-время кадра и проходов (зоны вида Gpu) — своей секцией рядом с зонами CPU.
        if (period == ProfilePeriod.Frame && GpuJson(stats, top) is { } gpu) json["gpu"] = gpu;
        json["history"] = new JsonArray([.. recent.Select(h =>
        {
            var entry = period == ProfilePeriod.Frame
                ? new JsonObject
                {
                    ["frame"] = h.Number,
                    ["ticks"] = new JsonArray(h.Ticks > 0 ? h.FirstTick : null, h.Ticks),
                    ["alpha"] = Round(h.Alpha, 4), ["ms"] = Round(Ms(h.Time), 5), ["bytes"] = h.Bytes, ["service"] = h.Service,
                    ["gpuFrame"] = h.GpuFrame == ulong.MaxValue ? null : h.GpuFrame,
                    ["tools"] = h.Tools ? true : null,   // после Пв: кадр инструментов — почему служебный
                }
                : new JsonObject
                {
                    ["tick"] = h.Number, ["frame"] = h.Frame == ulong.MaxValue ? null : h.Frame, ["ms"] = Round(Ms(h.Time), 5), ["bytes"] = h.Bytes,
                };
            // Точка Пб: время потока на CPU, выделения процесса, сборки, пауза, JIT периода.
            var c = h.Counters;
            entry["cpuMs"] = Round(c.CpuMs, 5);
            entry["processBytes"] = c.ProcessBytes;
            entry["gc"] = c.Collections;
            entry["pauseMs"] = Round(c.PauseMs, 4);
            entry["jit"] = c.JitMethods;
            entry["jitProcess"] = c.JitProcess;
            if (c.JitProcessTicks > 0) entry["jitMs"] = Round(c.JitProcessMs, 4);
            return (JsonNode?)entry;
        })]);
        return json;
    }

    /// <summary>
    /// Секция <c>gpu</c> кадров (точка Пг): кадр целиком (<c>gpu.frame</c>) и проходы (<c>gpu.&lt;проход&gt;</c>) — мс на кадр:
    /// среднее, медиана, p99, максимум (<c>presence</c> — доля кадров с данными); отставание данных. Нет — null.
    /// </summary>
    private JsonObject? GpuJson(IReadOnlyList<ProfileZoneStats> stats, int top)
    {
        var gpu = stats.Where(z => z.Kind == ProfileZoneKind.Gpu).ToList();
        if (gpu.Count == 0) return null;
        var frame = gpu.FirstOrDefault(z => z.Name == GpuFrameName);
        return new JsonObject
        {
            ["note"] = "GPU-время (таймстемпы D3D12, точка Пг): кадр N несёт времена кадра N − lagFrames (кадры в полёте; разбор — после " +
                "ожидания его слота, без стойла); frame — кадр целиком (проходы, барьеры, копии снимка), passes — проходы графа, мс на кадр " +
                "(по всем кадрам итогов; presence — доля кадров с данными); частота GPU плавает, часы CPU и GPU не сопоставлены",
            ["lagFrames"] = RecentGpuLag(),
            ["frame"] = frame is null ? null : ZoneJson(frame),
            ["passes"] = new JsonArray([.. gpu.Where(z => z.Name != GpuFrameName).Take(top).Select(z => (JsonNode?)ZoneJson(z))]),
        };
    }

    /// <summary>Зона за период — объект отчёта (мс — до 10 нс, байты — до 0,1).</summary>
    public static JsonObject ZoneJson(ProfileZoneStats z)
    {
        var json = new JsonObject
        {
            ["name"] = z.Name,
            ["parent"] = z.Parent,
            ["meanMs"] = Round(z.MeanMs, 5),
            ["medianMs"] = Round(z.MedianMs, 5),
            ["p99Ms"] = Round(z.P99Ms, 5),
            ["maxMs"] = Round(z.MaxMs, 5),
            ["selfMs"] = Round(z.SelfMs, 5),
            ["bytes"] = Round(z.Bytes, 1),
            ["calls"] = Round(z.Calls, 3),
            ["presence"] = Round(z.Presence, 3),
        };
        if (z.Kind != ProfileZoneKind.Work) json["kind"] = z.Kind.ToString();   // GpuWait, Idle (Пв), Gpu (Пг)
        return json;
    }

    /// <summary>NaN (нет окна кольца) — null, а не число: JSON не знает NaN.</summary>
    private static JsonNode? Round(double v, int digits) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, digits)) : null;

    /// <summary>
    /// Записать отчёт (<see cref="Report"/>) в <paramref name="path"/> с отступами; <paramref name="header"/> — поля прогона
    /// (процесс, сборка, ключи) в начале объекта. Каталог создаётся.
    /// </summary>
    public string Write(string path, JsonObject? header = null)
    {
        FlushEvents();   // события среды приходят с задержкой: отчёт — после хвоста
        FlushAnomalies(TimeSpan.FromSeconds(2));   // файлы аномалий — дописаны: в отчёте их пути
        var report = Report();
        if (header is not null)
        {
            int at = 2;
            foreach (var (key, value) in header.ToList())
            {
                header.Remove(key);
                report.Insert(at++, key, value);
            }
        }
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, report.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).ReplaceLineEndings("\r\n") + "\r\n");
        return full;
    }

    /// <summary>
    /// Кольцо профайлера — в Chrome Trace Event JSON (точка Пв, <see cref="ChromeTrace"/>: Perfetto, <c>chrome://tracing</c>): кадры
    /// и тики кольца с настоящим временем и зонами (суммой вызовов за период), в диагностическом режиме — события среды окна. Не
    /// горячий путь (выделяет, пишет файл); только на потоке владельца. Каталог создаётся.
    /// </summary>
    public string WriteTrace(string path, IReadOnlyDictionary<string, string>? meta = null)
    {
        FlushEvents();
        int zones = _names.Count;
        var parent = new int[zones];
        Array.Copy(_parent, parent, zones);
        long from = Math.Min(Oldest(_frames), Oldest(_ticks));
        var data = new TraceData
        {
            Name = SharedName, Parent = parent, Zones = zones, Meta = meta,
            Events = _events is { } events && from != long.MaxValue ? [.. events.Recent().Where(e => e.Time >= from)] : null,
        };
        AddRing(data, _frames, tick: false);
        AddRing(data, _ticks, tick: true);
        string full = Path.GetFullPath(path);
        ChromeTrace.Write(full, data);
        return full;
    }

    /// <summary>Начало самого старого периода кольца; пусто — <see cref="long.MaxValue"/>.</summary>
    private static long Oldest(Series s) => s.Headers is null || s.Filled == 0 ? long.MaxValue : s.RingStart![(s.Next - s.Filled + s.Capacity) % s.Capacity];

    private static void AddRing(TraceData data, Series s, bool tick)
    {
        if (s.Headers is null) return;
        for (int i = 0; i < s.Filled; i++)
        {
            int slot = (s.Next - s.Filled + i + s.Capacity) % s.Capacity;
            var h = s.Headers[slot];
            data.Periods.Add(new TracePeriod(tick, h.Number, s.RingStart![slot], h, s.RingTime, s.RingCalls, slot * s.Stride, false));
        }
    }
}
