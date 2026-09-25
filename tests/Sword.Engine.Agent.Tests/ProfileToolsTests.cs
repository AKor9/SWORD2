using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// <c>profile.stages</c> на Headless: выключен по умолчанию; включение — сразу (симуляция на паузе, между
/// тиками); после шагов — зоны тика, стадий, систем, барьеров, инвариантов, реплея и наблюдения агента; фильтр по началу
/// имени, история, файл отчёта; выключение — тики больше не считаются; профайлер подключён и к новой симуляции сеанса.
/// </summary>
public class ProfileToolsTests
{
    [Fact]
    public async Task ProfileStages_Headless_EnableStepReport()
    {
        using var f = new AgentFixture();
        var (off, error) = await f.Tool("profile.stages");
        Assert.False(error, off.ToJsonString());
        Assert.False(off["enabled"]!.GetValue<bool>());
        Assert.Equal(0, off["ticks"]!.GetValue<int>());

        var (on, _) = await f.Tool("profile.stages", new JsonObject { ["enable"] = true });
        Assert.True(on["enabled"]!.GetValue<bool>());   // Headless на паузе — между тиками: сразу
        Assert.Null(on["note"]);
        await f.Tool("time.step", new JsonObject { ["ticks"] = 30 });

        var (r, _) = await f.Tool("profile.stages", new JsonObject { ["history"] = 5, ["write"] = true, ["top"] = 100 });
        Assert.Equal("tick", r["per"]!.GetValue<string>());
        Assert.Equal(30, r["periods"]!.GetValue<int>());
        Assert.Equal(5, r["history"]!.AsArray().Count);
        Assert.Equal(29, r["history"]![4]!["tick"]!.GetValue<int>());
        var zones = r["zones"]!.AsArray().ToDictionary(z => z!["name"]!.GetValue<string>(), z => z!.AsObject());
        foreach (string zone in (string[])["tick", "stage.S", "push.mover@S", "barrier.S", "stage.runtime.export", "invariant.ecs.finite",
                     "sim.record", "sim.observers"])
            Assert.True(zones.ContainsKey(zone), $"нет зоны {zone}: {string.Join(", ", zones.Keys)}");
        Assert.Equal("stage.S", zones["push.mover@S"]["parent"]!.GetValue<string>());
        Assert.True(zones["tick"]["meanMs"]!.GetValue<double>() >= zones["stage.S"]["meanMs"]!.GetValue<double>());
        Assert.Equal(0, r["mismatches"]!.GetValue<int>());
        string file = r["file"]!.GetValue<string>();
        var saved = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(CpuProfiler.ReportFormat, saved["format"]!.GetValue<string>());
        Assert.Equal("headless", saved["process"]!.GetValue<string>());
        Assert.Equal(30, saved["ticks"]!["count"]!.GetValue<int>());

        var (stages, _) = await f.Tool("profile.stages", new JsonObject { ["prefix"] = "stage." });
        Assert.All(stages["zones"]!.AsArray(), z => Assert.StartsWith("stage.", z!["name"]!.GetValue<string>()));

        var (disabled, _) = await f.Tool("profile.stages", new JsonObject { ["enable"] = false });
        Assert.False(disabled["enabled"]!.GetValue<bool>());
        await f.Tool("time.step", new JsonObject { ["ticks"] = 10 });
        var (after, _) = await f.Tool("profile.stages");
        Assert.Equal(30, after["ticks"]!.GetValue<int>());

        var (bad, isError) = await f.Tool("profile.stages", new JsonObject { ["per"] = "week" });
        Assert.True(isError, bad.ToJsonString());
    }

    /// <summary>
    /// <c>profile.stages</c> несёт среду за тик (CPU потока против настенного, GC, JIT), машину с включения (вердикт
    /// «код или машина») и — в диагностическом режиме (<c>events: true</c>) — события среды с местами; выключение режима.
    /// </summary>
    [Fact]
    public async Task ProfileStages_RuntimeMachineAndEvents()
    {
        using var f = new AgentFixture();
        var (on, error) = await f.Tool("profile.stages", new JsonObject { ["enable"] = true, ["events"] = true });
        Assert.False(error, on.ToJsonString());
        Assert.True(on["enabled"]!.GetValue<bool>());
        await f.Tool("time.step", new JsonObject { ["ticks"] = 20 });

        var (r, _) = await f.Tool("profile.stages");
        var runtime = r["runtime"]!;
        Assert.InRange(runtime["onCpuPct"]!.GetValue<double>(), 0, 100);
        Assert.True(runtime["cpuMs"]!.GetValue<double>() > 0);
        Assert.NotNull(runtime["gc"]!["collections"]);
        Assert.NotNull(runtime["jit"]!["mainThread"]);
        var machine = r["machine"]!;
        Assert.Contains(machine["verdict"]!.GetValue<string>(), (string[])["код", "среда", "машина"]);
        Assert.False(string.IsNullOrEmpty(machine["text"]!.GetValue<string>()));
        Assert.NotNull(machine["ticksOnCpuPct"]);   // доля CPU для вердикта — тиков (счётная работа), не окна с паузами агента
        var events = Assert.IsType<JsonObject>(r["events"]);
        Assert.True(events["calibrated"]!.GetValue<bool>());
        Assert.NotNull(events["alloc"]!["types"]);
        Assert.NotNull(events["recent"]!["jit"]);

        var (off, _) = await f.Tool("profile.stages", new JsonObject { ["events"] = false });
        Assert.Equal(JsonValueKind.String, off["events"]!.GetValueKind());
        Assert.True(off["enabled"]!.GetValue<bool>());   // профайлер остаётся включённым
    }

    /// <summary>
    /// Поддельные часы профайлера сеанса (<see cref="ProfileClock"/>): каждое чтение времени — шаг <see cref="StepTicks"/> (зоны и
    /// тики длятся столько, сколько в них чтений, — у одинаковых тиков одинаково), поток всё время на CPU; сборок и JIT нет. Шаг
    /// меняет тест между вызовами инструментов (симуляция стоит в точке обслуживания). Настоящего времени профайлер на них не
    /// читает — суд, класс, главная зона и ведро жетонов не зависят от нагрузки машины.
    /// </summary>
    private sealed class SteppingClock : ProfileClock
    {
        private long _now = System.Diagnostics.Stopwatch.Frequency, _cycles, _step;

        /// <summary>Шаг часов на чтение, отсчёты <see cref="System.Diagnostics.Stopwatch"/>.</summary>
        public long StepTicks
        {
            get => Volatile.Read(ref _step);
            set => Volatile.Write(ref _step, value);
        }

        public override long Timestamp()
        {
            long step = StepTicks;
            _now += step;
            _cycles += (long)Math.Ceiling(step * ThreadClock.CyclesPerSecond / System.Diagnostics.Stopwatch.Frequency) + 1;   // на CPU весь шаг
            return _now;
        }

        public override ProfileCounters Counters() => new(_cycles, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// <c>profile.stages</c> — аномалии (без настройки exe — «не настроен»; <c>threshold</c> настраивает захват в
    /// артефакты сеанса и ставит абсолютный порог; список — период, мс, класс, главная зона, файл; <c>anomalies</c> — сколько);
    /// <c>threshold: 0</c> — снова по медиане; <c>trace</c> — кольцо в Chrome Trace JSON; порог меньше нуля — ошибка. Профайлер
    /// сеанса — на поддельных часах (решение пользователя: под нагрузкой тик 32 бывал не дольше среднего — главной зоны
    /// не было): судимые тики вдвое дольше несудимых — у каждого своё лишнее и главная зона.
    /// </summary>
    [Fact]
    public async Task ProfileStages_AnomaliesThresholdAndTrace()
    {
        var clock = new SteppingClock { StepTicks = System.Diagnostics.Stopwatch.Frequency / 1_000_000 };   // 1 мкс на чтение
        using var f = new AgentFixture(profiler: new CpuProfiler(CpuProfiler.DefaultHistory, clock));
        var (on, error) = await f.Tool("profile.stages", new JsonObject { ["enable"] = true });
        Assert.False(error, on.ToJsonString());
        Assert.Equal(JsonValueKind.String, on["anomalies"]!.GetValueKind());   // захват не настроен (exe не задал каталог)

        var (set, _) = await f.Tool("profile.stages", new JsonObject { ["threshold"] = 0.0001 });
        Assert.Equal("absolute", set["anomalies"]!["threshold"]!["mode"]!.GetValue<string>());
        await f.Tool("time.step", new JsonObject { ["ticks"] = 32 });   // первые 32 тика не судятся
        clock.StepTicks *= 2;                                           // судимые — вдвое дольше: дольше своего среднего
        await f.Tool("time.step", new JsonObject { ["ticks"] = 28 });
        Assert.True(f.Host.Profiler.FlushAnomalies(TimeSpan.FromSeconds(10)));

        var (r, _) = await f.Tool("profile.stages", new JsonObject { ["anomalies"] = 100 });
        var a = r["anomalies"]!;
        Assert.Equal(28, a["detected"]!.GetValue<int>());
        var list = a["list"]!.AsArray();
        Assert.Equal(28, list.Count);
        var first = list[0]!;
        Assert.Equal("tick", first["period"]!.GetValue<string>());
        Assert.Equal(32, first["number"]!.GetValue<int>());
        Assert.Contains(first["class"]!.GetValue<string>(), (string[])["code", "jit", "gc", "machine"]);
        Assert.All(list, x => Assert.Equal("code", x!["class"]!.GetValue<string>()));   // поток на CPU весь тик, JIT и GC нет
        Assert.All(list, x => Assert.False(string.IsNullOrEmpty(x!["zone"]?.GetValue<string>()), x!.ToJsonString()));
        string file = first["file"]!.GetValue<string>();
        Assert.StartsWith(f.Host.Artifacts, file);
        Assert.Equal(ProfileAnomalies.FileFormat, JsonNode.Parse(File.ReadAllText(file))!["format"]!.GetValue<string>());
        // Ведро: до 5 файлов сразу, дальше — 1 в секунду; писатель занят всеми тремя местами — «busy». Список — все 28. Тики на
        // поддельных часах — миллисекунды: новых жетонов нет — 5 отданы (записаны или «busy»: первые три места свободны), 23 — по частоте.
        int written = a["written"]!.GetValue<int>(), rate = a["suppressed"]!["rate"]!.GetValue<int>(), busy = a["suppressed"]!["busy"]!.GetValue<int>();
        Assert.InRange(written, 1, 5);
        Assert.InRange(written, 3, 5);
        Assert.Equal(28, written + rate + busy);
        Assert.True(rate > 0);
        Assert.Equal(23, rate);

        var (brief, _) = await f.Tool("profile.stages", new JsonObject { ["anomalies"] = 0, ["threshold"] = 0, ["trace"] = true });
        Assert.Empty(brief["anomalies"]!["list"]!.AsArray());
        Assert.Equal("median", brief["anomalies"]!["threshold"]!["mode"]!.GetValue<string>());
        string trace = brief["trace"]!.GetValue<string>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(trace)))
        {
            var events = doc.RootElement.GetProperty("traceEvents").EnumerateArray().ToList();
            Assert.Contains(events, e => e.GetProperty("ph").GetString() == "X" && e.GetProperty("name").GetString() == "tick 59");
            Assert.Contains(events, e => e.GetProperty("ph").GetString() == "X" && e.GetProperty("name").GetString() == "push.mover@S");
        }

        var (bad, isError) = await f.Tool("profile.stages", new JsonObject { ["threshold"] = -1 });
        Assert.True(isError, bad.ToJsonString());
        f.Host.Profiler.StopAnomalies();
    }

    [Fact]
    public async Task ProfileStages_FollowsSessionSimulation()
    {
        static Runtime.Simulation Build(Runtime.ConfigSet config)
        {
            var sim = Runtime.Simulation.Build(config, ["S"], [new PushModule()]);
            config.Complete();
            return sim;
        }
        var first = Build(Runtime.ConfigSet.Empty());
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Introspection.Catalog(first.World, first.CommandTypes),
            Time = new Introspection.TimeControl(paused: true, 0), Service = new Introspection.ServiceQueue(), Log = new LogRing(16),
            ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-agent-tests"), Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        var session = new AgentSimulation(first, new Runtime.ReplayInfo("1.0.0+test", "tests", "none"), Build, sets => Runtime.ConfigSet.Empty([.. sets]));
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(session, host, stop.Token)) { IsBackground = true };
        loop.Start();
        int id = 0;
        async Task<JsonObject> Tool(string name, JsonObject arguments)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            };
            var result = JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
            Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }
        try
        {
            await Tool("profile.stages", new JsonObject { ["enable"] = true });
            await Tool("time.step", new JsonObject { ["ticks"] = 10 });
            // Перемотка назад — новая симуляция сеанса: профайлер подключён к ней до первого её тика (повтор до тика 4 — тоже).
            await Tool("time.seek", new JsonObject { ["tick"] = 4 });
            Assert.NotSame(first, session.Current);
            await Tool("profile.stages", new JsonObject { ["reset"] = true });
            await Tool("time.step", new JsonObject { ["ticks"] = 6 });
            var r = await Tool("profile.stages", new JsonObject { ["prefix"] = "push." });
            Assert.Equal(6, r["periods"]!.GetValue<int>());
            Assert.Equal("push.mover@S", r["zones"]![0]!["name"]!.GetValue<string>());
            Assert.Same(host.Profiler, session.Current.Profiler);
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }
}
