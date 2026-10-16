using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Engine.Diagnostics;

public sealed partial class CpuProfiler
{
    /// <summary>Формат отчёта (<c>profile.json</c>, секция <c>profile</c> в <c>--json</c>).</summary>
    public const string ReportFormat = "sword-profile";

    /// <summary>Отчёт профайлера (выделяет): по кадрам и тикам — периоды, окно кольца, зоны (<see cref="Stats"/>) и последние
    /// периоды. Нет периодов вида — нет секции.</summary>
    /// <param name="history">Сколько последних периодов кольца выписать.</param>
    /// <param name="top">Сколько зон выписать (по среднему).</param>
    /// <param name="prefix">Только зоны с этим началом имени (<c>stage.</c>, <c>render.</c>…); null — все.</param>
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
            },
            ["zones"] = _names.Count,
            ["mismatches"] = Mismatches,
            ["overflows"] = Overflows,
            ["abandoned"] = Abandoned,
        };
        if (PeriodJson(ProfilePeriod.Frame, history, top, prefix) is { } frames) json["frames"] = frames;
        if (PeriodJson(ProfilePeriod.Tick, history, top, prefix) is { } ticks) json["ticks"] = ticks;
        return json;
    }

    /// <summary>Коротко для итога <c>--json</c>: периоды, служебные кадры и <paramref name="top"/> зон по среднему (кадра,
    /// иначе тика).</summary>
    public JsonObject Summary(int top = 10)
    {
        var period = Count(ProfilePeriod.Frame).Periods > 0 ? ProfilePeriod.Frame : ProfilePeriod.Tick;
        var (frames, service) = Count(ProfilePeriod.Frame);
        var json = new JsonObject
        {
            ["enabled"] = Enabled,
            ["frames"] = frames,
            ["serviceFrames"] = service,
            ["ticks"] = Count(ProfilePeriod.Tick).Periods,
            ["mismatches"] = Mismatches,
            ["per"] = period == ProfilePeriod.Frame ? "frame" : "tick",
            ["top"] = new JsonArray([.. Stats(period).Where(z => z.Kind != ProfileZoneKind.Gpu).Take(top).Select(z => (JsonNode?)new JsonObject
            {
                ["name"] = z.Name, ["meanMs"] = Round(z.MeanMs, 5), ["p99Ms"] = Round(z.P99Ms, 5), ["selfMs"] = Round(z.SelfMs, 5),
                ["bytes"] = Round(z.Bytes, 1),
            })]),
        };
        // GPU-время кадра и главные проходы (мс на кадр) — рядом с зонами CPU.
        if (period == ProfilePeriod.Frame && GpuJson(Stats(period), top: 5) is { } gpu) json["gpu"] = gpu;
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
            ["zones"] = new JsonArray([.. zones.Select(z => (JsonNode?)ZoneJson(z))]),
        };
        if (period == ProfilePeriod.Frame) json.Insert(1, "service", service);
        // GPU-время кадра и проходов (зоны вида Gpu) — своей секцией рядом с зонами CPU.
        if (period == ProfilePeriod.Frame && GpuJson(stats, top) is { } gpu) json["gpu"] = gpu;
        json["history"] = new JsonArray([.. recent.Select(h => (JsonNode?)(period == ProfilePeriod.Frame
            ? new JsonObject
            {
                ["frame"] = h.Number,
                ["ticks"] = new JsonArray(h.Ticks > 0 ? h.FirstTick : null, h.Ticks),
                ["alpha"] = Round(h.Alpha, 4), ["ms"] = Round(Ms(h.Time), 5), ["bytes"] = h.Bytes, ["service"] = h.Service,
                ["gpuFrame"] = h.GpuFrame == ulong.MaxValue ? null : h.GpuFrame,
            }
            : new JsonObject
            {
                ["tick"] = h.Number, ["frame"] = h.Frame == ulong.MaxValue ? null : h.Frame, ["ms"] = Round(Ms(h.Time), 5), ["bytes"] = h.Bytes,
            }))]);
        return json;
    }

    /// <summary>Секция <c>gpu</c>: <c>gpu.frame</c> и проходы — мс на кадр (среднее, медиана, p99, максимум, доля кадров с
    /// данными) и отставание. Нет — null.</summary>
    private JsonObject? GpuJson(IReadOnlyList<ProfileZoneStats> stats, int top)
    {
        var gpu = stats.Where(z => z.Kind == ProfileZoneKind.Gpu).ToList();
        if (gpu.Count == 0) return null;
        var frame = gpu.FirstOrDefault(z => z.Name == GpuFrameName);
        return new JsonObject
        {
            ["note"] = "GPU-время (таймстемпы D3D12): кадр N несёт времена кадра N − lagFrames (кадры в полёте; разбор — после " +
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
        if (z.Kind != ProfileZoneKind.Work) json["kind"] = z.Kind.ToString();   // GpuWait, Idle, Gpu
        return json;
    }

    /// <summary>NaN (нет окна кольца) — null, а не число: JSON не знает NaN.</summary>
    private static JsonNode? Round(double v, int digits) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, digits)) : null;

    /// <summary>Записать <see cref="Report"/> в <paramref name="path"/>; <paramref name="header"/> — поля прогона в начале.
    /// Каталог создаётся.</summary>
    public string Write(string path, JsonObject? header = null)
    {
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

    /// <summary>Кольцо в Chrome Trace Event JSON (<see cref="ChromeTrace"/>). Не горячий путь, только на потоке владельца;
    /// каталог создаётся.</summary>
    public string WriteTrace(string path, IReadOnlyDictionary<string, string>? meta = null)
    {
        int zones = _names.Count;
        var parent = new int[zones];
        Array.Copy(_parent, parent, zones);
        var data = new TraceData { Name = NameOf, Parent = parent, Zones = zones, Meta = meta };
        AddRing(data, _frames, tick: false);
        AddRing(data, _ticks, tick: true);
        string full = Path.GetFullPath(path);
        ChromeTrace.Write(full, data);
        return full;
    }

    private static void AddRing(TraceData data, Series s, bool tick)
    {
        if (s.Headers is null) return;
        for (int i = 0; i < s.Filled; i++)
        {
            int slot = (s.Next - s.Filled + i + s.Capacity) % s.Capacity;
            var h = s.Headers[slot];
            data.Periods.Add(new TracePeriod(tick, h.Number, s.RingStart![slot], h, s.RingTime, s.RingCalls, slot * s.Stride));
        }
    }
}
