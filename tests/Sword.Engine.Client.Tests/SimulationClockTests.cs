using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client.Tests;

public struct Ping { public ulong SentForTick; }
public struct Pong { public ulong Tick; }

/// <summary>
/// Очередь кадра через клиент (раздел 5 плана: «экспорт тика в очередь кадра клиента при кадрах с 0 и 3 тиками»):
/// <see cref="SimulationClock"/> делает тики кадра, игра читает записи через <see cref="SimulationLink.Frame{T}"/>.
/// </summary>
public class SimulationClockTests
{
    private static readonly string[] Stages = ["Begin", "Late"];

    /// <summary>Команду тика читает первая стадия игры; последняя пишет событие каждый тик — оно экспортируется.</summary>
    private sealed class Probe : IModule
    {
        public readonly List<(ulong Tick, ulong SentForTick)> Commands = [];

        public string Id => "probe";

        public void Configure(SimulationBuilder b) =>
            b.Command<Ping>().Event<Pong>()
                .System("Begin", new Reader(this))
                .System("Late", new Writer())
                .Export<Event<Pong>>();

        private sealed class Reader(Probe p) : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("reader")).ReadsJournal<Event<Ping>>();

            public void Run(in SystemContext ctx)
            {
                foreach (var c in ctx.Journal<Event<Ping>>()) p.Commands.Add((ctx.Tick, c.Value.SentForTick));
            }
        }

        private sealed class Writer : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("writer")).Emits<Pong>();

            public void Run(in SystemContext ctx) => ctx.Commands.Emit(new Pong { Tick = ctx.Tick });
        }
    }

    private static (SimulationClock Clock, Probe Probe) Create(int ticksPerFrame = 0)
    {
        var probe = new Probe();
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, [probe]);
        // Команда игры на тик — с номером тика, который клиент видит перед ним.
        var clock = new SimulationClock(sim, link => link.Enqueue(new Ping { SentForTick = link.Tick }), ticksPerFrame);
        return (clock, probe);
    }

    private static List<(ulong Tick, ulong Recorded)> Frame(SimulationLink link)
    {
        var list = new List<(ulong, ulong)>();
        foreach (var (tick, record) in link.Frame<Event<Pong>>()) list.Add((tick, record.Value.Tick));
        return list;
    }

    [Fact]
    public void FrameOfThreeTicks_GetsAllThree_WithTheirTicks()
    {
        var (clock, _) = Create(ticksPerFrame: 3);
        Assert.Equal(3, clock.RunFrame(0));
        Assert.Equal([(0ul, 0ul), (1ul, 1ul), (2ul, 2ul)], Frame(clock.Link));
        Assert.Equal(3, clock.RunFrame(0));
        Assert.Equal([(3ul, 3ul), (4ul, 4ul), (5ul, 5ul)], Frame(clock.Link));   // прошлый кадр не повторяется
    }

    [Fact]
    public void FrameOfZeroTicks_IsEmpty_EvenAfterFrameWithRecords()
    {
        var (clock, _) = Create();
        double dt = clock.Link.Dt;
        Assert.Equal(3, clock.RunFrame(3.5 * dt));
        Assert.Equal(3, Frame(clock.Link).Count);

        Assert.Equal(0, clock.RunFrame(0.1 * dt));   // накопитель ещё не набрал тик
        Assert.Empty(Frame(clock.Link));

        Assert.Equal(1, clock.RunFrame(0.5 * dt));   // 0,5 + 0,1 + 0,5 — тик
        Assert.Equal([(3ul, 3ul)], Frame(clock.Link));

        clock.EmptyFrame();                          // перерисовка при перетаскивании рамки
        Assert.Empty(Frame(clock.Link));
    }

    [Fact]
    public void SingleTickFrame_DeliversLastStageEventOfThatTick()
    {
        // Событие последней стадии игры попадает в очередь кадра своего тика, даже если дальше тиков нет.
        var (clock, _) = Create(ticksPerFrame: 1);
        clock.RunFrame(0);
        Assert.Equal([(0ul, 0ul)], Frame(clock.Link));
    }

    [Fact]
    public void BeforeTick_CommandReachesFirstStageOfItsTick()
    {
        var (clock, probe) = Create(ticksPerFrame: 3);
        clock.RunFrame(0);
        Assert.Equal([(0ul, 0ul), (1ul, 1ul), (2ul, 2ul)], probe.Commands);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(double.NegativeInfinity)]
    public void BadFrameTime_CountsAsZero_ClockKeepsGoing(double bad)
    {
        var (clock, _) = Create();
        Assert.Equal(0, clock.RunFrame(bad));
        Assert.Equal(1, clock.RunFrame(1.5 * clock.Link.Dt));   // часы не встали
    }

    [Fact]
    public void Accumulator_CapsTicksPerFrame_AndTrimsTheRest()
    {
        // Program.cs SWORD: шаг кадра до 0,25 с, не больше MaxTicksPerFrame тиков, остаток — до одного тика.
        var (clock, _) = Create();
        Assert.Equal(8, clock.RunFrame(1.0));        // 0,25 с = 30 тиков, потолок — 8
        Assert.Equal(8, Frame(clock.Link).Count);
        Assert.True(clock.RunFrame(0) <= 1, "после упора остаток не больше одного тика");
    }
}
