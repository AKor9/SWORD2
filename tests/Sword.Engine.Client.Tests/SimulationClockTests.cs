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
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(new Ping { SentForTick = link.Tick }), ticksPerFrame);
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

    [Fact]
    public void Alpha_IsShareOfNextTick_OneWithFixedTicks()
    {
        var (clock, _) = Create();
        double dt = clock.Link.Dt;
        Assert.Equal(2, clock.RunFrame(2.25 * dt));
        Assert.Equal(0.25f, clock.Alpha, 4);          // accumulator / dt после тиков кадра (Program.cs SWORD)
        Assert.Equal(1, clock.RunFrame(0.9 * dt));
        Assert.Equal(0.15f, clock.Alpha, 4);
        Assert.Equal((float)(0.25 * dt), clock.FrameSeconds(0.25 * dt));
        Assert.Equal(5f, clock.FrameSeconds(5));   // камере — сырой шаг кадра, как в SWORD (тики — не больше 0,25 с)

        var (fixedClock, _) = Create(ticksPerFrame: 3);
        fixedClock.RunFrame(0.001);
        Assert.Equal(1f, fixedClock.Alpha);               // заданные тики — кадр показывает ровно тик
        Assert.Equal(3 * fixedClock.Link.Dt, fixedClock.FrameSeconds(0.001));
    }

    [Theory]
    [InlineData(60, 2)]
    [InlineData(120, 1)]
    [InlineData(30, 4)]
    public void FixedFrameStep_GivesExactTicks_AndZeroAlpha(double fps, int ticks)
    {
        // Шаг тика — float (1/120 ≈ 0,0083333338), шаг кадра — double: без допуска накопителя «кадр 1/60 с» набирал
        // 1,99999994 тика — первый кадр терял тик, доля тика была ≈1 вместо 0.
        var (clock, _) = Create();
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(ticks, clock.RunFrame(1 / fps));
            Assert.Equal(0f, clock.Alpha, 5);
        }
    }

    private sealed class Gate(int? ticks) : ITickGate
    {
        public int? Ticks = ticks;
        public readonly List<ulong> Reported = [];
        public int? Override(int maxPerFrame) => Ticks is { } t ? Math.Min(t, maxPerFrame) : null;
        public void Ran(ulong tick) => Reported.Add(tick);
    }

    [Fact]
    public void Gate_PausesAndStepsInsteadOfClock_ShowsLastTick()
    {
        // Инструмент (агент): пауза — ноль тиков при любом шаге кадра, шаги — ровно заданные; часы стоят, доля тика 1.
        var (clock, _) = Create();
        double dt = clock.Link.Dt;
        var gate = new Gate(0);
        clock.Gate = gate;
        Assert.Equal(0, clock.RunFrame(5 * dt));
        Assert.Equal(1f, clock.Alpha);
        gate.Ticks = 3;
        Assert.Equal(3, clock.RunFrame(0));
        Assert.Equal(3, Frame(clock.Link).Count);                    // очередь кадра — как у обычных тиков
        gate.Ticks = 100;
        Assert.Equal(8, clock.RunFrame(0));                          // не больше MaxTicksPerFrame
        gate.Ticks = null;                                           // снова по часам — накопитель пуст
        Assert.Equal(0, clock.RunFrame(0.5 * dt));
        Assert.Equal(0.5f, clock.Alpha, 4);
        Assert.Equal([0ul, 3ul, 11ul, 11ul], gate.Reported);        // сколько сыграно — каждому кадру
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
    private sealed class StoppingGate(int? ticks, ulong stopAt) : ITickGate
    {
        public int? Override(int maxPerFrame) => ticks is { } t ? Math.Min(t, maxPerFrame) : null;
        public void Ran(ulong tick) { }
        public bool StopAfterTick(ulong tick) => tick >= stopAt;
    }

    [Fact]
    public void Gate_StopAfterTick_EndsFrameOnThatTick()
    {
        // Условие наблюдения или пауза агента встают на своём тике, а не в конце кадра.
        var (clock, _) = Create();
        double dt = clock.Link.Dt;
        clock.Gate = new StoppingGate(5, stopAt: 2);
        Assert.Equal(2, clock.RunFrame(0));                  // шагов 5, встали на тике 2
        Assert.Equal(2, Frame(clock.Link).Count);
        clock.Gate = new StoppingGate(null, stopAt: 4);
        Assert.Equal(2, clock.RunFrame(8 * dt));             // по часам 8 тиков, встали на тике 4
        Assert.Equal(0f, clock.Alpha);                       // несыгранное время кадра не догоняется
        clock.Gate = null;
        Assert.Equal(1, clock.RunFrame(dt));
    }
}
