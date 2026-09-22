using System.Diagnostics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime.Tests;

/// <summary>Очередь команд тика: диапазон одной постановкой, порядок внутри тика, стоимость — не квадрат.</summary>
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

    private static byte[] Raw(int value) => BitConverter.GetBytes(value);

    [Fact]
    public void Range_MergesIntoQueue_InTickThenPostingOrder()
    {
        var m = new Probe();
        var sim = Simulation.Build(ConfigSet.Empty(), ["Intent"], [m]);
        sim.EnqueueRaw(typeof(Ping), 3, Raw(1));
        sim.EnqueueRaw(typeof(Ping), 1, Raw(2), repeat: 4);    // тики 1…4 — в середину очереди, одним слиянием
        sim.EnqueueRaw(typeof(Ping), 4, Raw(3));
        sim.Enqueue(3, new Ping { Value = 7 });                 // клиент — раньше инструментов своего тика
        for (int i = 0; i < 6; i++) sim.Step();
        Assert.Equal([(1ul, 2), (2ul, 2), (3ul, 7), (3ul, 1), (3ul, 2), (4ul, 2), (4ul, 3)], m.Seen);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.EnqueueRaw(typeof(Ping), 10, Raw(1), repeat: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.EnqueueRaw(typeof(Ping), ulong.MaxValue, Raw(1), repeat: 2));
    }

    [Fact]
    public void LongRange_IsLinear()
    {
        // 100 000 тиков одной командой агента (command.send repeat) — раньше каждый тик сдвигал весь хвост (~5·10⁹ шагов).
        const int n = 100_000;
        var m = new Probe();
        var sim = Simulation.Build(ConfigSet.Empty(), ["Intent"], [m]);
        sim.EnqueueRaw(typeof(Ping), 5, Raw(1));
        var watch = Stopwatch.StartNew();
        sim.EnqueueRaw(typeof(Ping), 0, Raw(2), repeat: n);    // слияние с уже стоящей командой
        for (int i = 0; i < n; i++) sim.Step();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{n} тиков с очередью: {watch.Elapsed}");
        Assert.Equal(n + 1, m.Seen.Count);
        Assert.Equal([(5ul, 1), (5ul, 2)], m.Seen.Where(s => s.Tick == 5));
    }
}
