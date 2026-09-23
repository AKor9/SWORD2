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
                ("write", new JsonObject { ["type"] = "boolean", ["description"] = "записать полный отчёт profile.json в артефакты сеанса" })),
            ReadOnly = false,
            Run = async call =>
            {
                bool? enable = call.Node("enable") is null ? null : call.Bool("enable", false);
                bool reset = call.Bool("reset", false), write = call.Bool("write", false);
                string? per = call.String("per");
                if (per is not null and not ("frame" or "tick")) throw new ArgumentException($"per: frame или tick, а не {per}");
                string? prefix = call.String("prefix");
                int top = (int)call.Integer("top", 40, 1, 1000);
                int history = (int)call.Integer("history", 0, 0, CpuProfiler.DefaultHistory);
                string? file = write ? host.ArtifactPath($"profile-{DateTime.UtcNow:HHmmss-fff}.json") : null;
                var r = await host.Serve(ctx =>
                {
                    var p = host.Profiler;
                    if (enable is { } on) p.SetEnabled(on);
                    if (reset) p.Reset();
                    var period = per switch
                    {
                        "frame" => ProfilePeriod.Frame,
                        "tick" => ProfilePeriod.Tick,
                        _ => p.Count(ProfilePeriod.Frame).Periods > 0 ? ProfilePeriod.Frame : ProfilePeriod.Tick,
                    };
                    var report = p.Report(history, top, prefix);
                    int zones = prefix is null ? p.Stats(period).Count : p.Stats(period).Count(z => z.Name.StartsWith(prefix, StringComparison.Ordinal));
                    if (file is not null) p.Write(file, new JsonObject { ["process"] = host.Mode, ["build"] = host.BuildId, ["session"] = host.SessionId });
                    return (Report: report, Period: period, Zones: zones, ctx.Point);
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
                data["zones"] = section?["zones"]?.DeepClone() ?? new JsonArray();
                if (history > 0) data["history"] = section?["history"]?.DeepClone() ?? new JsonArray();
                data["mismatches"] = report["mismatches"]?.DeepClone();
                data["abandoned"] = report["abandoned"]?.DeepClone();
                if (file is not null) data["file"] = file;
                return new ToolOutput(data) { Point = r.Point, Truncated = r.Zones > top, ArtifactId = file is null ? null : Path.GetFileName(file) };
            },
        });
    }
}
