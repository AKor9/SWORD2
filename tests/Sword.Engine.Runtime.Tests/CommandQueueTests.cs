using System.Diagnostics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime.Tests;

/// <summary>Очередь команд тика: порядок по тику, внутри тика — по постановке; стоимость — не квадрат.</summary>
public class CommandQueueTests
{
    private sealed class Probe : IModule, IContinuousSystem
    {
        public readonly List<(ulong Tick, int Value)> Seen = [];

        public string Id => "probe";

        public void Configure(SimulationBuilder b) => b.Command<Ping>().System("Intent", this);

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("probe")).ReadsJournal<Event<Ping>>();

        public void Run(in SystemContext ctx)
        {
            foreach (var p in ctx.Journal<Event<Ping>>()) Seen.Add((ctx.Tick, p.Value.Value));
        }
    }

    [Fact]
    public void Commands_InTickThenPostingOrder()
    {
        var m = new Probe();
        var sim = Simulation.Build(ConfigSet.Empty(), ["Intent"], [m]);
        sim.Enqueue(3, new Ping { Value = 1 });
        for (ulong t = 1; t <= 4; t++) sim.Enqueue(t, new Ping { Value = 2 });   // тики 1…4 — и в середину очереди
        sim.Enqueue(4, new Ping { Value = 3 });
        sim.Enqueue(3, new Ping { Value = 7 });
        for (int i = 0; i < 6; i++) sim.Step();
        Assert.Equal([(1ul, 2), (2ul, 2), (3ul, 1), (3ul, 2), (3ul, 7), (4ul, 2), (4ul, 3)], m.Seen);
    }

    [Fact]
    public void LongQueue_IsLinear()
    {
        // 100 000 тиков вперёд: тик берёт свои записи с головы — без сдвига всего хвоста на каждый тик (~5·10⁹ шагов).
        const int n = 100_000;
        var m = new Probe();
        var sim = Simulation.Build(ConfigSet.Empty(), ["Intent"], [m]);
        sim.Enqueue(5, new Ping { Value = 1 });
        var watch = Stopwatch.StartNew();
        for (ulong t = 0; t < n; t++) sim.Enqueue(t, new Ping { Value = 2 });   // тики 0…4 — перед уже стоящей командой
        for (int i = 0; i < n; i++) sim.Step();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{n} тиков с очередью: {watch.Elapsed}");
        Assert.Equal(n + 1, m.Seen.Count);
        Assert.Equal([(5ul, 1), (5ul, 2)], m.Seen.Where(s => s.Tick == 5));
    }
}
