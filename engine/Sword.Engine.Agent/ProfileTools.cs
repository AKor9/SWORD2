using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Agent;

/// <summary>
/// Профайлер CPU для агента (этап П, точка Па; раздел 8 плана — «диагностика»): <c>profile.stages</c> — зоны кадра и тика
/// (<see cref="CpuProfiler"/> хоста: у Headless — свой, подключается к каждой симуляции сеанса в <see cref="SimulationLoop"/>;
/// у живой игры — профайлер клиента, его задаёт exe). Включение — диагностика, а не команда симуляции: состояние и хеш от
/// профайлера не зависят. Итоги считаются в точке обслуживания (профайлер однопоточный), JSON — на потоке агента.
/// </summary>
internal static class ProfileTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "profile.stages", Title = "Профайлер CPU: зоны кадра и тика",
            Description = "Где время кадра и тика (профайлер CPU, этап П): зоны — tick, стадии stage.<стадия>, системы <метка>@<стадия>, барьеры " +
                "barrier.<стадия>, инварианты invariant.<id>, sim.record (реплей и хеш), sim.observers (агент); в App ещё фазы кадра " +
                "frame.messages/input/simulate/tools/extract/render/presented/wait, рендер frame.fenceWait/build/record/present, проходы " +
                "render.<проход>, игра arena.*. На зону за кадр или тик: мс — среднее, медиана, p99, максимум, своё (без дочерних); " +
                "байты, вызовы, доля периодов; history — последние периоды (кадр — тики кадра и доля тика). Выключен по умолчанию: " +
                "enable=true включает (App — с начала следующего кадра, Headless — следующего тика; включённый — 0 Б на тик и кадр), " +
                "затем time.step или просто время игры; reset — забыть итоги (после прогрева), write — profile.json в артефакты. " +
                "runtime — среда за период (точка Пб): CPU главного потока против настенного (onCpuPct), сборки и пауза GC, методы и мс JIT " +
                "(главный поток и процесс), выделения процесса; machine — машина с включения или reset: загрузка системы другими процессами, " +
                "частота CPU, GC и JIT, вердикт код/среда/машина с причинами. events=true — диагностический режим: события среды " +
                "(EventListener: выделения по типам, сборки и паузы, JIT с уровнями tier0/tier1/osr, фоновый ли) с кадром, тиком и зоной; " +
                "дорогой (JIT медленнее), events=false — выключить. anomalies — захват аномалий (точка Пв): кадр или тик дольше порога " +
                "(по умолчанию — медиана последних 128 + max(2 мс кадр / 1 мс тик, 0.75 медианы); threshold — абсолютный порог, мс, 0 — снова " +
                "по медиане) пишется файлом anomaly-<frame|tick>-N.json (зоны, причины, события окна, машина) и трассой; в ответе — список: " +
                "период, мс, класс причины (code/jit/gc/gpu/machine), главная зона, файл. trace=true — кольцо в Chrome Trace JSON " +
                "(Perfetto, chrome://tracing). gpu (App, точка Пг) — GPU-время кадра (frame) и проходов графа (passes, мс на кадр: " +
                "среднее, медиана, p99, максимум за окно; lagFrames — данные кадра N − 3, кадры в полёте); один кадр — render.graph. " +
                "Меняет только профайлер, не симуляцию.",
            Arguments = CoreTools.Props(
                ("enable", new JsonObject { ["type"] = "boolean", ["description"] = "включить (true) или выключить (false) профайлер" }),
                ("reset", new JsonObject { ["type"] = "boolean", ["description"] = "забыть итоги и кольцо (после прогрева)" }),
                ("per", new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("frame", "tick"),
                    ["description"] = "итоги на кадр (App) или на тик; по умолчанию — кадр, если кадры были, иначе тик",
                }),
                ("prefix", CoreTools.Str("только зоны с именем с этого начала: stage., render., invariant., frame., arena.; системы — метка@")),
                ("top", CoreTools.Int("сколько зон по среднему (по умолчанию 40)", 1, 1000)),
                ("history", CoreTools.Int("последних периодов кольца в ответе (по умолчанию 0)", 0, CpuProfiler.DefaultHistory)),
                ("write", new JsonObject { ["type"] = "boolean", ["description"] = "записать полный отчёт profile.json в артефакты сеанса" }),
                ("events", new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "диагностический режим: события среды (выделения по типам, GC, JIT) с местом — кадр, тик, зона; включает и профайлер",
                }),
                ("anomalies", CoreTools.Int("сколько последних аномалий в ответе (по умолчанию 10; 0 — только счёт)", 0, ProfileAnomalies.Capacity)),
                ("threshold", new JsonObject
                {
                    ["type"] = "number", ["minimum"] = 0,
                    ["description"] = "порог аномалии кадра и тика, мс (абсолютный); 0 — по медиане последних периодов (по умолчанию)",
                }),
                ("trace", new JsonObject { ["type"] = "boolean", ["description"] = "записать кольцо профайлера в Chrome Trace JSON (Perfetto) в артефакты сеанса" })),
            ReadOnly = false,
            Run = async call =>
            {
                bool? enable = call.Node("enable") is null ? null : call.Bool("enable", false);
                bool? events = call.Node("events") is null ? null : call.Bool("events", false);
                bool reset = call.Bool("reset", false), write = call.Bool("write", false);
                string? per = call.String("per");
                if (per is not null and not ("frame" or "tick")) throw new ArgumentException($"per: frame или tick, а не {per}");
                string? prefix = call.String("prefix");
                int top = (int)call.Integer("top", 40, 1, 1000);
                int history = (int)call.Integer("history", 0, 0, CpuProfiler.DefaultHistory);
                int anomalies = (int)call.Integer("anomalies", 10, 0, ProfileAnomalies.Capacity);
                double? threshold = call.Node("threshold") is { } t
                    ? t.GetValueKind() == System.Text.Json.JsonValueKind.Number && t.GetValue<double>() is var ms && ms >= 0 && double.IsFinite(ms)
                        ? ms : throw new ArgumentException("threshold: число мс ≥ 0 (0 — порог по медиане)")
                    : null;
                bool trace = call.Bool("trace", false);
                string stamp = $"{DateTime.UtcNow:HHmmss-fff}";
                string? file = write ? host.ArtifactPath($"profile-{stamp}.json") : null;
                string? traceFile = trace ? host.ArtifactPath($"trace-{stamp}.json") : null;
                var r = await host.Serve(ctx =>
                {
                    var p = host.Profiler;
                    if (enable is { } on) p.SetEnabled(on);
                    if (events is { } diag) p.SetEvents(diag);
                    if (reset) p.Reset();
                    // Точка Пв: порог аномалий; захват не настроен exe — файлы в артефакты сеанса.
                    if (threshold is { } limit)
                    {
                        if (p.Anomalies is null && !p.IsOff) p.CaptureAnomalies(new AnomalyOptions { Directory = host.Artifacts });
                        if (p.Anomalies is { } a) a.AbsoluteMs = limit;
                    }
                    // События приходят с задержкой (~15 мс): итог — после хвоста (метка компилируется здесь, на потоке владельца).
                    if (p.Events is not null) p.FlushEvents(TimeSpan.FromSeconds(1));
                    var period = per switch
                    {
                        "frame" => ProfilePeriod.Frame,
                        "tick" => ProfilePeriod.Tick,
                        _ => p.Count(ProfilePeriod.Frame).Periods > 0 ? ProfilePeriod.Frame : ProfilePeriod.Tick,
                    };
                    var report = p.Report(history, top, prefix);
                    int zones = prefix is null ? p.Stats(period).Count : p.Stats(period).Count(z => z.Name.StartsWith(prefix, StringComparison.Ordinal));
                    if (file is not null) p.Write(file, new JsonObject { ["process"] = host.Mode, ["build"] = host.BuildId, ["session"] = host.SessionId });
                    if (traceFile is not null)
                        p.WriteTrace(traceFile, new Dictionary<string, string> { ["process"] = host.Mode, ["session"] = host.SessionId, ["build"] = host.BuildId });
                    return (Report: report, Period: period, Zones: zones, Events: p.Events?.Summary(types: 10, recent: 8),
                        Anomalies: p.Anomalies?.ToJson(anomalies), ctx.Point);
                }, call.Cancel);
                var report = r.Report;
                string key = r.Period == ProfilePeriod.Frame ? "frames" : "ticks";
                var section = report[key]?.AsObject();
                var data = new JsonObject
                {
                    ["enabled"] = report["enabled"]?.DeepClone(),
                    ["requested"] = report["requested"]?.DeepClone(),
                    ["per"] = r.Period == ProfilePeriod.Frame ? "frame" : "tick",
                };
                if ((bool?)report["enabled"] != (bool?)report["requested"])
                    data["note"] = host.Mode == "app" ? "включение и выключение — с начала следующего кадра" : "включение и выключение — с начала следующего тика";
                data["periods"] = section?["count"]?.DeepClone() ?? 0;
                if (section?["service"] is { } service) data["serviceFrames"] = service.DeepClone();
                data["window"] = section?["window"]?.DeepClone() ?? 0;
                data["frames"] = report["frames"]?["count"]?.DeepClone() ?? 0;
                data["ticks"] = report["ticks"]?["count"]?.DeepClone() ?? 0;
                data["zonesTotal"] = r.Zones;
                // Точка Пб: среда за период и машина с включения или сброса (вердикт «код или машина»); события — в диагностическом режиме.
                if (section?["runtime"] is { } runtime) data["runtime"] = runtime.DeepClone();
                if (report["machine"] is { } machine) data["machine"] = machine.DeepClone();
                data["events"] = r.Events is { } ev ? ev : "выключены (events: true — диагностический режим)";
                // Точка Пв: аномалии — порог, счёт, последние (класс, главная зона, файл); файлы пишет фоновый поток: свежие — state queued.
                data["anomalies"] = r.Anomalies is { } an ? an : "захват не настроен (threshold — настроить, файлы — в артефакты сеанса)";
                if (traceFile is not null) data["trace"] = traceFile;
                data["zones"] = section?["zones"]?.DeepClone() ?? new JsonArray();
                // Точка Пг: GPU-время кадра и проходов (App) — среднее, медиана, p99 за окно, отставание данных (кадры в полёте).
                if (section?["gpu"] is { } gpu) data["gpu"] = gpu.DeepClone();
                if (history > 0) data["history"] = section?["history"]?.DeepClone() ?? new JsonArray();
                data["mismatches"] = report["mismatches"]?.DeepClone();
                data["abandoned"] = report["abandoned"]?.DeepClone();
                if (file is not null) data["file"] = file;
                return new ToolOutput(data) { Point = r.Point, Truncated = r.Zones > top, ArtifactId = file is null ? null : Path.GetFileName(file) };
            },
        });
    }
}
