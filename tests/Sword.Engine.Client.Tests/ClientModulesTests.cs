using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Render;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client.Tests;

/// <summary>
/// Клиентские модули без окна и GPU (раздел 7 плана): наблюдатель тиков — каждый тик ровно раз, по порядку, с миром после своего тика, при
/// 0, 1, 2, 3 и 8 тиках за кадр и на паузе; тики кадра и заявки GPU-симуляции (<see cref="SimFrame"/>) — шаг «каждый второй тик» через
/// границы кадров; выключенный модуль не регистрирует ничего; Id, которого симуляция не знает, — ошибка; исключение при освобождении
/// ресурса модуля не мешает освободить остальные; кадр модулей — 0 Б.
/// </summary>
public sealed class ClientModulesTests
{
    private static readonly string[] Stages = ["Begin", "Late"];

    /// <summary>Модуль симуляции теста: ничего не делает — симуляции нужен хоть один модуль с Id, отличным от клиентского.</summary>
    private sealed class Sim : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b) { }
    }

    /// <summary>Ворота тиков теста: число тиков каждого кадра — из очереди (0 — пауза).</summary>
    private sealed class Gate(Queue<int> frames) : ITickGate
    {
        public int? Override(int maxPerFrame) => frames.Dequeue();

        public void Ran(ulong tick) { }
    }

    /// <summary>Заявка теста: номер тика, который её добавил.</summary>
    private struct Request
    {
        public ulong Tick;
    }

    /// <summary>GPU-симуляция теста: шаг 60 Гц — на каждом нечётном <c>TickId</c> кадра (после каждого второго тика при 120 тиках/с).</summary>
    private sealed class HalfRate : IGpuSimulation
    {
        public readonly List<ulong> Steps = [];
        public readonly List<string> Problems = [];
        public int Frames;

        public string Name => "test.half";

        public void Create(GpuSimContext context) { }

        public void AddPasses(RenderFrame frame, in SimFrame sim)
        {
            Frames++;
            // Заявки кадра — по одной на тик кадра, по порядку тиков: k-я — тика FirstTick + k.
            var requests = sim.Requests<Request>();
            if (requests.Length != sim.Ticks) Problems.Add($"кадр с тика {sim.FirstTick}: тиков {sim.Ticks}, заявок {requests.Length}");
            for (int k = 0; k < sim.Ticks; k++)
            {
                ulong tick = sim.FirstTick + (ulong)k;
                if (k < requests.Length && requests[k].Tick != tick) Problems.Add($"тик {tick}: заявка тика {requests[k].Tick}");
                if (tick % 2 == 1) Steps.Add(tick);
            }
        }

        public void OnReadback(ReadOnlySpan<byte> data) { }
    }

    /// <summary>
    /// Клиентский модуль теста: наблюдатель пишет <c>TickId</c> и то, что видит в мире (номер тика <c>SimTime</c>, тиков сыграно), добавляет
    /// заявку своей GPU-симуляции; регистрирует ещё проход, ручку и оверлей — у выключенного их быть не должно.
    /// </summary>
    private sealed class Probe(Func<SimulationLink> link) : IClientModule
    {
        public readonly List<(ulong Tick, ulong SimTime, ulong Played)> Seen = [];
        public readonly HalfRate Simulation = new();
        public int Configured;
        public SimRequests? Requests;

        public string Id => "fx";

        public void ConfigureClient(ClientBuilder b)
        {
            Configured++;
            Requests = b.GpuSimulation(Simulation);
            b.AfterTick(tick =>
            {
                var l = link();
                Seen.Add((tick, l.World.Resource<SimTime>().Tick, l.Tick));
                Requests.Add(tick, new Request { Tick = tick });
            });
            b.Pass(FramePhase.Debug, _ => { });
            b.Tuning(new TuningGroup("fx", "Тест"));
            b.Overlay("fx");
        }
    }

    private static (Simulation Sim, ClientModules Modules, SimulationClock Clock, Probe Probe, ConfigSet Config) Create(params string[] sets)
    {
        var config = ConfigSet.Empty(sets);
        var sim = Simulation.Build(config, Stages, null, [new Sim()], ["fx"]);
        SimulationClock? clock = null;
        var probe = new Probe(() => clock!.Link);
        var modules = ClientModules.Compose(config, sim, [probe]);
        clock = new SimulationClock(sim, (_, _) => { }, afterTick: modules.AfterTick);
        return (sim, modules, clock, probe, config);
    }

    /// <summary>
    /// Кадр хоста: начало кадра модулей, тики, шаг GPU-симуляции включённого модуля без рендера — с тиками и заявками кадра, как в
    /// <see cref="ClientModules.BuildFrame"/> (<see cref="ClientModules.Frame"/>).
    /// </summary>
    private static int Frame(Simulation sim, ClientModules modules, SimulationClock clock, Probe probe, double seconds = 0)
    {
        modules.BeginFrame(sim.Tick);
        int ticks = clock.RunFrame(seconds);
        if (probe.Requests is { } requests) probe.Simulation.AddPasses(null!, modules.Frame(requests));
        return ticks;
    }

    [Fact]
    public void Observer_EveryTickOnce_InOrder_WorldAfterItsTick_AtZeroToEightTicksAndPause()
    {
        var (sim, modules, clock, probe, _) = Create();
        int[] counts = [0, 1, 2, 8, 3, 0, 1];
        clock.Gate = new Gate(new Queue<int>(counts));
        var frames = new List<(ulong First, int Ticks)>();
        foreach (int n in counts)
        {
            Assert.Equal(n, Frame(sim, modules, clock, probe));
            var f = modules.Frame();
            frames.Add((f.FirstTick, f.Ticks));
        }
        ulong total = (ulong)counts.Sum();
        Assert.Equal(total, sim.Tick);
        // Каждый тик — ровно раз, по порядку; мир — после своего тика: SimTime того же тика, сыграно тиков — на один больше.
        Assert.Equal(Enumerable.Range(0, (int)total).Select(t => ((ulong)t, (ulong)t, (ulong)t + 1)).ToArray(), probe.Seen);
        // Тики кадра: первый и сколько; пауза (0) — тик, который будет следующим.
        Assert.Equal(new (ulong, int)[] { (0, 0), (0, 1), (1, 2), (3, 8), (11, 3), (14, 0), (14, 1) }, frames);

        // По часам: кадр 0,25 с — упор в sim.maxTicksPerFrame (8), наблюдатель — на каждом.
        clock.Gate = null;
        int before = probe.Seen.Count;
        Assert.Equal(8, Frame(sim, modules, clock, probe, SimulationClock.MaxFrameSeconds));
        Assert.Equal(Enumerable.Range(15, 8).Select(t => (ulong)t).ToArray(), probe.Seen.Skip(before).Select(s => s.Tick).ToArray());
        Assert.Equal(1, probe.Configured);
    }

    [Fact]
    public void SimFrame_HalfRateStep_StepsOnOddTicks_AcrossFrameBoundaries_RequestsOfItsTick()
    {
        var (sim, modules, clock, probe, _) = Create();
        int[] counts = [3, 0, 1, 2, 8, 5, 0];
        clock.Gate = new Gate(new Queue<int>(counts));
        foreach (int _ in counts) Frame(sim, modules, clock, probe);
        int total = counts.Sum();
        Assert.Empty(probe.Simulation.Problems);
        Assert.Equal(counts.Length, probe.Simulation.Frames);
        // ⌊19/2⌋ = 9 шагов — на тиках 1, 3, …, 17, в том числе когда пара тиков — в разных кадрах (кадр из 3, кадр из 1).
        Assert.Equal(total / 2, probe.Simulation.Steps.Count);
        Assert.Equal(Enumerable.Range(0, total / 2).Select(k => (ulong)(2 * k + 1)).ToArray(), probe.Simulation.Steps);
        // Заявки кадра — только его тиков: кадр начат заново — пусто.
        modules.BeginFrame(sim.Tick);
        Assert.Equal(0, modules.Frame(probe.Requests).Requests<Request>().Length);
    }

    [Fact]
    public void DisabledModule_RegistersNothing_ObserverNeverCalled_SectionSkipped()
    {
        var (sim, modules, clock, probe, config) = Create("modules.fx.enabled=false", "fx.anything=1");
        Assert.Equal(0, probe.Configured);
        Assert.Empty(modules.Active);
        Assert.Equal(["fx"], modules.Inactive);
        Assert.Empty(modules.Overlays);
        Assert.Empty(modules.TuningGroups);
        Assert.Equal(["fx"], sim.InactiveClientOnlyModules);
        clock.Gate = new Gate(new Queue<int>([3, 8]));
        Frame(sim, modules, clock, probe);
        Frame(sim, modules, clock, probe);
        Assert.Empty(probe.Seen);
        var report = Assert.Single(modules.Report());
        Assert.False(report.Enabled);
        Assert.Empty(report.Passes);
        Assert.Equal(0ul, report.ObservedTicks);
        config.Complete();   // секция выключенного модуля — не «неизвестная»
    }

    [Fact]
    public void EnabledModule_Report_ListsItsRegistrations()
    {
        var (sim, modules, clock, probe, _) = Create();
        clock.Gate = new Gate(new Queue<int>([2]));
        Frame(sim, modules, clock, probe);
        var report = Assert.Single(modules.Report());
        Assert.True(report.Enabled);
        Assert.Equal(["fx"], report.Overlays);
        Assert.Equal(["fx"], report.Tuning);
        Assert.Equal(["test.half"], report.GpuSimulations);
        Assert.Equal(1, report.Observers);
        Assert.Equal(2ul, report.ObservedTicks);
    }

    [Fact]
    public void UnknownOrClashingIds_AreErrors()
    {
        var config = ConfigSet.Empty();
        var sim = Simulation.Build(config, Stages, null, [new Sim()], ["fx"]);
        SimulationLink Link() => throw new InvalidOperationException();
        var unknown = new Probe(Link);
        // Клиентский модуль, которого симуляция не знает (не объявлен), — ошибка композиции.
        var other = Simulation.Build(ConfigSet.Empty(), Stages, null, [new Sim()]);
        Assert.Contains("симуляция его не знает", Assert.Throws<InvalidOperationException>(() => ClientModules.Compose(ConfigSet.Empty(), other, [unknown])).Message);
        // Клиентский Id совпадает с модулем симуляции или секцией движка — ошибка сборки симуляции.
        Assert.Throws<InvalidOperationException>(() => Simulation.Build(ConfigSet.Empty(), Stages, null, [new Sim()], ["probe"]));
        Assert.Throws<InvalidOperationException>(() => Simulation.Build(ConfigSet.Empty(), Stages, null, [new Sim()], ["modules"]));
        Assert.Throws<InvalidOperationException>(() => Simulation.Build(ConfigSet.Empty(), Stages, null, [new Sim()], ["fx", "fx"]));
        // Группа modules знает клиентский Id, в состав реплея (ActiveModules) он не входит.
        Assert.Equal(["probe"], sim.ActiveModules);
        Assert.Equal(["fx"], sim.ClientOnlyModules);
        Assert.Throws<InvalidDataException>(() => Simulation.Build(ConfigSet.Empty("modules.nope.enabled=false"), Stages, null, [new Sim()], ["fx"]));
    }

    /// <summary>Ресурс теста: пишет своё имя в журнал освобождения; <c>fail</c> — бросает после записи (закрытие модуля с ошибкой).</summary>
    private sealed class Resource(string name, List<string> log, bool fail = false) : IDisposable
    {
        public void Dispose()
        {
            log.Add(name);
            if (fail) throw new InvalidOperationException($"закрытие {name}");
        }
    }

    /// <summary>Модуль теста: при инициализации отдаёт во владение ресурсы по порядку.</summary>
    private sealed class Owner(string id, params IDisposable[] resources) : IClientModule
    {
        public string Id => id;

        public void ConfigureClient(ClientBuilder b) => b.Initialize(ctx =>
        {
            foreach (var r in resources) ctx.Own(r);
        });
    }

    [Fact]
    public void Dispose_FailingResource_OthersStillReleased_ExceptionAfter()
    {
        (ClientModules Modules, List<string> Log) Build(bool twoFail)
        {
            var log = new List<string>();
            var config = ConfigSet.Empty();
            var sim = Simulation.Build(config, Stages, null, [new Sim()], ["fa", "fb"]);
            var modules = ClientModules.Compose(config, sim,
            [
                new Owner("fa", new Resource("a1", log), new Resource("a2", log, fail: twoFail)),
                new Owner("fb", new Resource("b1", log), new Resource("b2", log, fail: true), new Resource("b3", log)),
            ]);
            modules.Initialize(null!, () => default);
            return (modules, log);
        }

        // Обратный порядок: модуль fb (b3, b2 — бросает, b1), затем fa; исключение — после всех, как есть.
        var (one, log) = Build(twoFail: false);
        Assert.Equal("закрытие b2", Assert.Throws<InvalidOperationException>(one.Dispose).Message);
        Assert.Equal(["b3", "b2", "b1", "a2", "a1"], log);
        one.Dispose();   // повторно — ничего
        Assert.Equal(5, log.Count);

        // Два исключения — вместе, в порядке освобождения.
        var (two, log2) = Build(twoFail: true);
        var all = Assert.Throws<AggregateException>(two.Dispose);
        Assert.Equal(["закрытие b2", "закрытие a2"], all.InnerExceptions.Select(e => e.Message));
        Assert.Equal(["b3", "b2", "b1", "a2", "a1"], log2);
    }

    [Fact]
    public void ModulesFrame_SteadyState_DoesNotAllocate()
    {
        var (sim, modules, clock, probe, _) = Create();
        clock.Gate = new Gate(new Queue<int>(Enumerable.Repeat(4, 400)));
        for (int i = 0; i < 100; i++) Frame(sim, modules, clock, probe);
        probe.Seen.Clear();
        probe.Seen.Capacity = 2000;   // списки теста — не часть кадра
        probe.Simulation.Steps.Capacity = 2000;
        // Только кадр модулей: начало кадра, наблюдатели, шаги по тикам (тики симуляции — свои проверки).
        ulong next = sim.Tick;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            modules.BeginFrame(next);
            for (int k = 0; k < 4; k++) modules.AfterTick(next++);
            probe.Simulation.AddPasses(null!, modules.Frame(probe.Requests));
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
