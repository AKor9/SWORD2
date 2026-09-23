using System.Diagnostics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime.Tests;

public struct Spin
{
    public int Value;
}

/// <summary>
/// Подопытная симуляция профайлера: в стадии A система крутит счётчик у 64 сущностей (с прибавкой, зависящей от тика), в
/// стадии B вторая система складывает их; на тике <c>throwTick</c> (если задан) первая один раз бросает исключение.
/// </summary>
public sealed class SpinModule(ulong throwTick = ulong.MaxValue) : IModule
{
    public string Id => "spin";

    public void Configure(SimulationBuilder b) =>
        b.Component<Spin>()
            .System("A", new Turn(throwTick))
            .System("B", new Sum())
            .OnWorldBuilt(w =>
            {
                for (int i = 0; i < 64; i++) w.AddImmediate(w.CreateImmediate(), new Spin { Value = i });
            });

    private sealed class Turn(ulong throwTick) : IContinuousSystem
    {
        private Query _query = null!;
        private bool _thrown;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("spin.turn")).Writes<Spin>();
            _query = s.Query().Write<Spin>();
        }

        public void Run(in SystemContext ctx)
        {
            ulong tick = ctx.World.Resource<SimTime>().Tick;
            if (tick == throwTick && !_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("сбой системы на тике " + tick);
            }
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var s in chunk.Write<Spin>())
                    for (int k = 0; k < 50; k++) s.Value = s.Value * 31 + (int)tick + k;
        }
    }

    private sealed class Sum : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("spin.sum")).Reads<Spin>();
            _query = s.Query().Read<Spin>();
        }

        public void Run(in SystemContext ctx)
        {
            long total = 0;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (var s in chunk.Read<Spin>()) total += s.Value;
            GC.KeepAlive(total);
        }
    }

    public static Simulation Build(ulong throwTick = ulong.MaxValue) =>
        Simulation.Build(ConfigSet.Empty(), ["A", "B"], [new SpinModule(throwTick)]);
}

/// <summary>
/// Профайлер CPU (этап П, точка Па): зоны и их вложенность, периоды кадра и тика, кольцо, включение на границе периода,
/// несогласованный конец зоны, 0 байт включённого, тик симуляции с профайлером — те же хеши, зоны стадий, систем, барьеров,
/// инвариантов; исключение в системе бросает тик, следующий — чистый.
/// </summary>
public class ProfilerTests
{
    private static void Busy(double microseconds)
    {
        long until = Stopwatch.GetTimestamp() + (long)(microseconds * Stopwatch.Frequency / 1e6);
        while (Stopwatch.GetTimestamp() < until) { }
    }

    [Fact]
    public void Disabled_ByDefault_RecordsNothing()
    {
        var p = new CpuProfiler(history: 8);
        int z = p.Zone("work");
        Assert.False(p.Enabled);
        p.BeginFrame(1);
        p.Begin(z);
        p.End(z);
        p.EndFrame(1, service: false);
        Assert.Equal((0, 0), p.Count(ProfilePeriod.Frame));
        Assert.Empty(p.Stats(ProfilePeriod.Frame));
        Assert.Equal(0, p.Depth);
        // Общий выключенный не заводит зон и не включается.
        Assert.Equal(0, CpuProfiler.Off.Zone("что-то"));
        Assert.Throws<InvalidOperationException>(() => CpuProfiler.Off.SetEnabled(true));
    }

    /// <summary>
    /// Общий <see cref="CpuProfiler.Off"/> не пишется и периодами: его делят потоки (ветки <c>experiment.fork</c> — Parallel.For,
    /// у каждой симуляции без своего профайлера — Off). Поля периода — закрытые, поэтому через отражение.
    /// </summary>
    [Fact]
    public void Off_PeriodsDoNotWriteSharedState()
    {
        var off = CpuProfiler.Off;
        bool Flag(string name) => (bool)typeof(CpuProfiler).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(off)!;
        off.BeginFrame(1);
        off.BeginTick(1);
        Assert.False(Flag("_inFrame"));
        Assert.False(Flag("_inTick"));
        off.EndTick();
        off.EndFrame(1, service: false);
        off.AbandonFrame();
        var sim = SpinModule.Build();
        Assert.Same(off, sim.Profiler);
        Parallel.For(0, 4, _ =>
        {
            var branch = SpinModule.Build();
            for (int t = 0; t < 20; t++) branch.Step();
        });
        Assert.False(Flag("_inTick"));
        Assert.False(off.Enabled);
        Assert.Equal(0, off.Depth);
    }

    [Fact]
    public void Zones_Nest_ChildrenNotLongerThanParent_PerFrame()
    {
        var p = new CpuProfiler(history: 16);
        int outer = p.Zone("outer"), inner = p.Zone("inner"), leaf = p.Zone("leaf");
        p.SetEnabled(true);
        for (ulong f = 1; f <= 10; f++)
        {
            p.BeginFrame(f);
            p.Begin(outer);
            Busy(20);
            for (int i = 0; i < 3; i++)
            {
                p.Begin(inner);
                Busy(10);
                p.Begin(leaf);
                Busy(5);
                p.End(leaf);
                p.End(inner);
            }
            p.End(outer);
            p.EndFrame(0.5f, service: f == 1);
        }
        Assert.Equal((9, 1), p.Count(ProfilePeriod.Frame));
        for (int back = 0; back < 10; back++)
        {
            var frame = p.InRecent(ProfilePeriod.Frame, back, CpuProfiler.FrameZone);
            var o = p.InRecent(ProfilePeriod.Frame, back, outer);
            var i = p.InRecent(ProfilePeriod.Frame, back, inner);
            var l = p.InRecent(ProfilePeriod.Frame, back, leaf);
            Assert.True(o.Ms <= frame.Ms && i.Ms <= o.Ms && l.Ms <= i.Ms, $"кадр −{back}: {frame.Ms} ≥ {o.Ms} ≥ {i.Ms} ≥ {l.Ms}");
            Assert.Equal((1, 1, 3, 3), (frame.Calls, o.Calls, i.Calls, l.Calls));
        }
        var stats = p.Stats(ProfilePeriod.Frame).ToDictionary(s => s.Name);
        Assert.Equal("frame", stats["outer"].Parent);
        Assert.Equal("outer", stats["inner"].Parent);
        Assert.Equal("inner", stats["leaf"].Parent);
        Assert.Equal(3, stats["leaf"].Calls);
        Assert.True(stats["outer"].MeanMs >= 0.020 + 3 * 0.015, $"outer {stats["outer"].MeanMs} мс");
        Assert.True(stats["inner"].SelfMs >= 3 * 0.009, $"своё inner {stats["inner"].SelfMs} мс");
        Assert.True(stats["outer"].SelfMs < stats["outer"].MeanMs);
        Assert.True(stats["leaf"].MedianMs <= stats["leaf"].P99Ms && stats["leaf"].P99Ms <= stats["leaf"].MaxMs);
        // Служебный кадр — в кольце, но не в итогах; заголовок кадра — доля тика.
        var recent = p.Recent(ProfilePeriod.Frame, 10);
        Assert.True(recent[0].Service);
        Assert.Equal(0.5f, recent[^1].Alpha);
    }

    [Fact]
    public void TicksInFrame_FrameHeaderHasTickRange_TickSeriesSeparate()
    {
        var p = new CpuProfiler(history: 16);
        int sim = p.Zone("sim"), system = p.Zone("system");
        p.SetEnabled(true);
        ulong tick = 100;
        for (ulong f = 1; f <= 4; f++)
        {
            p.BeginFrame(f);
            p.Begin(sim);
            for (int t = 0; t < (int)f - 1; t++)   // кадры с 0, 1, 2, 3 тиками
            {
                p.BeginTick(tick++);
                p.Begin(system);
                Busy(5);
                p.End(system);
                p.EndTick();
            }
            p.End(sim);
            p.EndFrame(0.25f, service: false);
        }
        var frames = p.Recent(ProfilePeriod.Frame, 4);
        Assert.Equal([(0, ulong.MaxValue), (1, 100ul), (2, 101ul), (3, 103ul)], frames.Select(h => (h.Ticks, h.FirstTick)));
        Assert.Equal((6, 0), p.Count(ProfilePeriod.Tick));
        var ticks = p.Recent(ProfilePeriod.Tick, 6);
        Assert.Equal([100ul, 101, 102, 103, 104, 105], ticks.Select(h => h.Number));
        Assert.Equal([2ul, 3, 3, 4, 4, 4], ticks.Select(h => h.Frame));
        var perFrame = p.Stats(ProfilePeriod.Frame).ToDictionary(s => s.Name);
        Assert.Equal(1.5, perFrame["tick"].Calls);      // 6 тиков на 4 кадра
        Assert.Equal("sim", perFrame["tick"].Parent);
        Assert.Equal(0.75, perFrame["tick"].Presence);  // тики были в 3 кадрах из 4
        var perTick = p.Stats(ProfilePeriod.Tick).ToDictionary(s => s.Name);
        Assert.Equal(1, perTick["system"].Calls);
        Assert.True(perTick["system"].MeanMs <= perTick["tick"].MeanMs);
        Assert.DoesNotContain("sim", perTick.Keys);     // зона вне тика в итоги тика не идёт
        var report = p.Report(history: 2);
        Assert.Equal(CpuProfiler.ReportFormat, report["format"]!.GetValue<string>());
        Assert.Equal(2, report["frames"]!["history"]!.AsArray().Count);
        Assert.Equal(4, report["frames"]!["count"]!.GetValue<int>());
        Assert.Equal(6, report["ticks"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void EnableDuringFrame_TakesEffectNextFrame()
    {
        var p = new CpuProfiler(history: 4);
        int z = p.Zone("z");
        p.BeginFrame(1);
        p.Begin(z);
        p.SetEnabled(true);          // посреди кадра (точка обслуживания агента) — со следующего кадра
        Assert.False(p.Enabled);
        Assert.True(p.Requested);
        p.End(z);
        p.EndFrame(1, false);
        p.BeginFrame(2);
        Assert.True(p.Enabled);
        p.Begin(z);
        p.End(z);
        p.EndFrame(1, false);
        Assert.Equal((1, 0), p.Count(ProfilePeriod.Frame));
        p.BeginFrame(3);
        p.SetEnabled(false);         // выключение — тоже с границы: кадр 3 досчитывается
        Assert.True(p.Enabled);
        p.EndFrame(1, false);
        p.BeginFrame(4);
        Assert.False(p.Enabled);
        Assert.Equal((2, 0), p.Count(ProfilePeriod.Frame));
    }

    [Fact]
    public void MismatchedEnd_IsAnError()
    {
        var p = new CpuProfiler(history: 4);
        int a = p.Zone("a"), b = p.Zone("b");
        p.SetEnabled(true);
        p.BeginFrame(1);
        p.Begin(a);
        p.Begin(b);
#if DEBUG
        var e = Assert.Throws<InvalidOperationException>(() => p.End(a));
        Assert.Contains("End(a)", e.Message);
        Assert.Contains("b", e.Message);
#else
        p.End(a);                    // Release: счёт и раскрутка до a
        Assert.Equal(1, p.Mismatches);
        p.EndFrame(1, false);
        Assert.Equal(0, p.Depth);
#endif
    }

    [Fact]
    public void Enabled_SteadyFramesAndTicks_DoNotAllocate()
    {
        var p = new CpuProfiler();
        int[] zones = [.. Enumerable.Range(0, 40).Select(i => p.Zone("zone" + i))];
        p.SetEnabled(true);
        void Frame(ulong f)
        {
            p.BeginFrame(f);
            foreach (int z in zones.AsSpan(0, 10))
            {
                p.Begin(z);
                p.End(z);
            }
            for (int t = 0; t < 2; t++)
            {
                p.BeginTick(f * 2 + (ulong)t);
                foreach (int z in zones.AsSpan(10))
                {
                    p.Begin(z);
                    p.End(z);
                }
                p.EndTick();
            }
            p.EndFrame(0.5f, false);
        }
        for (ulong f = 0; f < 2000; f++) Frame(f);   // кольцо обошло круг
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong f = 2000; f < 5000; f++) Frame(f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Simulation_Profiled_SameHashes_ZonesOfStagesSystemsInvariants()
    {
        var plain = SpinModule.Build();
        var profiled = SpinModule.Build();
        var p = new CpuProfiler(history: 64);
        profiled.AttachProfiler(p);
        p.SetEnabled(true);
        Span<ulong> a = stackalloc ulong[plain.StateParts.Count], b = stackalloc ulong[profiled.StateParts.Count];
        for (int t = 0; t < 100; t++)
        {
            plain.Step();
            profiled.Step();
            Assert.Equal(plain.HashState(a), profiled.HashState(b));
        }
        Assert.Equal((100, 0), p.Count(ProfilePeriod.Tick));
        var stats = p.Stats(ProfilePeriod.Tick).ToDictionary(s => s.Name);
        foreach (string zone in (string[])["tick", "stage.runtime.commands", "stage.A", "stage.B", "stage.runtime.export", "spin.turn@A",
                     "spin.sum@B", "barrier.A", "invariant.ecs.finite"])
            Assert.True(stats.ContainsKey(zone), $"нет зоны {zone}; есть: {string.Join(", ", stats.Keys)}");
        Assert.Equal("stage.A", stats["spin.turn@A"].Parent);
        Assert.Equal("stage.runtime.export", stats["invariant.ecs.finite"].Parent);   // ecs.finite — после конца тика
        Assert.Equal("tick", stats["stage.A"].Parent);
        // Дочерние зоны не дольше родителя — в каждом тике кольца.
        var names = p.Zones;
        for (int back = 0; back < 64; back++)
            foreach (var parent in stats.Values)
            {
                double own = p.InRecent(ProfilePeriod.Tick, back, p.Find(parent.Name)).Ms, children = 0;
                foreach (var child in stats.Values.Where(c => c.Parent == parent.Name))
                    children += p.InRecent(ProfilePeriod.Tick, back, p.Find(child.Name)).Ms;
                Assert.True(children <= own + 1e-9, $"тик −{back}: {parent.Name} {own} мс < дочерних {children} мс");
            }
        Assert.Equal(0, p.Mismatches);
        Assert.Equal(names.Count, p.Zones.Count);
    }

    [Fact]
    public void ExceptionInSystem_AbandonsTick_NextTickClean()
    {
        var sim = SpinModule.Build(throwTick: 3);
        var p = new CpuProfiler(history: 16);
        sim.AttachProfiler(p);
        p.SetEnabled(true);
        for (int t = 0; t < 3; t++) sim.Step();
        Assert.Throws<InvalidOperationException>(sim.Step);
        Assert.True(p.Depth > 0);                                 // тик с исключением остался открытым
        // Тик 3 не сыгран (исключение); дальше — снова тик 3 и следующие: брошенный тик в итоги не попал, стек чист.
        for (int t = 0; t < 3; t++) sim.Step();
        Assert.Equal(6ul, sim.Tick);
        Assert.Equal(1, p.Abandoned);
        Assert.Equal(0, p.Mismatches);
        Assert.Equal(0, p.Depth);
        Assert.Equal((6, 0), p.Count(ProfilePeriod.Tick));
    }
}
