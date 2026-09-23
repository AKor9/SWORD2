using System.Diagnostics;
using System.Reflection.Emit;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Профайлер: GC, JIT и машина (этап П, точка Пб). Счётчики на кадр и тик — у своего периода: сборка и пауза, метод JIT,
/// время потока на CPU против настенного (сон — поток почти не на CPU, счётный цикл — весь); выключенный не опрашивает
/// ничего, включённый и в диагностическом режиме — 0 байт на главном потоке; события среды (выделение, JIT, сборка) — в своём
/// кадре и своей зоне; хеши симуляции с событиями — те же; вердикт «код или машина» — по критериям.
///
/// Тесты одного класса идут последовательно, но классы — параллельно: сборки и JIT других потоков процесса возможны в любом
/// кадре, поэтому проверяется «у своего кадра есть», а «у чужого нет» — только для счётчиков текущего потока (JIT потока).
/// </summary>
[Collection(RuntimeEventsCollection.Name)]
public class ProfilerRuntimeTests
{
    private static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) { }
    }

    /// <summary>Новый метод, ещё не скомпилированный: вызов — компиляция JIT на этом потоке.</summary>
    private static Func<int> Fresh(string name)
    {
        var method = new DynamicMethod(name, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<int>>();
    }

    /// <summary>Кадр с одной зоной и действием в ней.</summary>
    private static void Frame(CpuProfiler p, ulong number, int zone, Action? work = null)
    {
        p.BeginFrame(number);
        p.Begin(zone);
        work?.Invoke();
        p.End(zone);
        p.EndFrame(1, service: false);
    }

    private static ProfileHeader FrameHeader(CpuProfiler p, ulong number) =>
        p.Recent(ProfilePeriod.Frame, p.History).Single(h => h.Number == number);

    [Fact]
    public void Disabled_PollsNothing_Enabled_TwoReadsPerPeriod()
    {
        var p = new CpuProfiler(history: 16);
        int z = p.Zone("z");
        for (ulong f = 1; f <= 50; f++)
        {
            p.BeginFrame(f);
            p.Begin(z);
            p.BeginTick(f);
            p.EndTick();
            p.End(z);
            p.EndFrame(1, false);
        }
        Assert.Equal(0, p.CounterReads);   // выключенный: ни одного опроса (цена ~0)
        p.SetEnabled(true);
        for (ulong f = 51; f <= 60; f++)
        {
            p.BeginFrame(f);
            p.BeginTick(f);
            p.EndTick();
            p.EndFrame(1, false);
        }
        Assert.Equal(2 * (10 + 10), p.CounterReads);   // начало и конец каждого кадра и тика
    }

    [Fact]
    public void Enabled_WithCountersAndEventsTimeline_DoesNotAllocateOnMainThread()
    {
        var p = new CpuProfiler();
        int[] zones = [.. Enumerable.Range(0, 30).Select(i => p.Zone("zone" + i))];
        p.SetEvents(true);   // диагностический режим: и лента зон, и слушатель событий (его выделения — на его потоке)
        try
        {
            void Frame(ulong f)
            {
                p.BeginFrame(f);
                foreach (int z in zones.AsSpan(0, 10))
                {
                    p.Begin(z);
                    p.End(z);
                }
                p.BeginTick(f);
                foreach (int z in zones.AsSpan(10))
                {
                    p.Begin(z);
                    p.End(z);
                }
                p.EndTick();
                p.EndFrame(0.5f, false);
            }
            for (ulong f = 0; f < 2000; f++) Frame(f);
            Assert.True(p.Timeline);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (ulong f = 2000; f < 5000; f++) Frame(f);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            p.SetEvents(false);
        }
    }

    [Fact]
    public void SleepInZone_WallMuchMoreThanThread_SpinAboutEqual()
    {
        var p = new CpuProfiler(history: 32);
        int z = p.Zone("z");
        p.SetEnabled(true);
        Frame(p, 1, z, () => Spin(1));
        Frame(p, 2, z, () => Thread.Sleep(10));
        var sleep = FrameHeader(p, 2);
        double sleepWall = CpuProfiler.Ms(sleep.Time);
        Assert.True(sleepWall >= 9.5, $"сон: настенное {sleepWall} мс");
        Assert.True(sleep.Counters.CpuMs < 0.2 * sleepWall, $"сон: поток на CPU {sleep.Counters.CpuMs} мс из {sleepWall}");
        // Счётный цикл: поток на CPU почти всё время. Классы и сборки тестов идут параллельно (verify.ps1 грузит все ядра) —
        // поток могли снять с процессора: лучший из двадцати кадров, запас до 70 %.
        double best = 0, spinCpu = 0, spinWall = 0;
        for (ulong f = 3; f <= 22 && best < 0.9; f++)
        {
            Frame(p, f, z, () => Spin(10));
            var spin = FrameHeader(p, f);
            double ratio = spin.Counters.CpuMs / CpuProfiler.Ms(spin.Time);
            if (ratio > best) (best, spinCpu, spinWall) = (ratio, spin.Counters.CpuMs, CpuProfiler.Ms(spin.Time));
        }
        Assert.True(best > 0.7 && best < 1.05, $"цикл: поток на CPU {spinCpu} мс из {spinWall}");
        var stats = p.RuntimeStats(ProfilePeriod.Frame);
        Assert.True(stats.Periods >= 3);
        Assert.InRange(stats.OnCpu, 0.05, 1);   // сон тянет долю вниз
    }

    [Fact]
    public void GcCollect_CollectionAndPause_InItsFrame()
    {
        var p = new CpuProfiler(history: 16);
        int z = p.Zone("z");
        p.SetEnabled(true);
        for (ulong f = 1; f <= 2; f++) Frame(p, f, z, () => GC.Collect(0));   // прогрев: код GC.Collect и профайлера
        for (ulong f = 3; f <= 4; f++) Frame(p, f, z);
        Frame(p, 5, z, () => GC.Collect(0));
        var gc = FrameHeader(p, 5).Counters;
        Assert.True(gc.Collections >= 1, $"сборок в кадре 5: {gc.Collections}");
        Assert.True(gc.PauseTicks > 0, "пауза сборки кадра 5 — больше 0");
        Assert.True(gc.Gen0 >= 1);
        var stats = p.RuntimeStats(ProfilePeriod.Frame);
        Assert.True(stats.GcPeriods >= 3 && stats.Totals.Collections >= 3, $"периодов со сборкой {stats.GcPeriods}");
    }

    [Fact]
    public void FreshMethod_JitInItsFrame_NotInNeighbours()
    {
        var p = new CpuProfiler(history: 16);
        int z = p.Zone("z");
        p.SetEnabled(true);
        var warm = Fresh("sword.test.warm");
        Action call = () => warm();   // одна лямбда: другая — новый метод, её первый вызов — тоже JIT
        for (ulong f = 1; f <= 3; f++) Frame(p, f, z, call);   // прогрев: всё, что зовёт кадр, скомпилировано
        var fresh = Fresh("sword.test.fresh");
        Frame(p, 4, z, () => fresh());
        Frame(p, 5, z, call);
        Frame(p, 6, z, call);
        var jit = FrameHeader(p, 4).Counters;
        Assert.True(jit.JitMethods >= 1, $"методов JIT главного потока в кадре 4: {jit.JitMethods}");
        Assert.True(jit.JitTicks > 0);
        Assert.Equal(0, FrameHeader(p, 5).Counters.JitMethods);
        Assert.Equal(0, FrameHeader(p, 6).Counters.JitMethods);
    }

    /// <summary>
    /// Диагностический режим: выделение 1 МБ <c>byte[]</c> в зоне — событие своего типа в своём кадре и зоне; свежий метод —
    /// событие JIT своего кадра и зоны; <c>GC.Collect</c> — сборка и пауза своего кадра и зоны, всё — на главном потоке.
    /// </summary>
    [Fact]
    public void Events_AllocJitGc_PlacedInTheirFrameAndZone()
    {
        var p = new CpuProfiler(history: 64);
        int idle = p.Zone("idle"), alloc = p.Zone("alloc"), jit = p.Zone("jit"), gc = p.Zone("gc"), outer = p.Zone("outer");
        p.SetEvents(true);
        try
        {
            Assert.True(p.Enabled && p.Timeline);
            byte[]? keep = null;
            var fresh = Fresh("sword.test.eventsFresh");
            for (ulong f = 1; f <= 3; f++) Frame(p, f, idle, () => Spin(0.2));
            // Место события — с точностью калибровки часов (единицы микросекунд): вокруг действия — по 0,2 мс внутри зоны.
            p.BeginFrame(4);
            p.Begin(outer);
            Spin(0.2);
            p.Begin(alloc);
            Spin(0.2);
            keep = new byte[1 << 20];
            Spin(0.2);
            p.End(alloc);
            Spin(0.2);
            p.End(outer);
            p.EndFrame(1, false);
            Frame(p, 5, jit, () =>
            {
                Spin(0.2);
                fresh();
                Spin(0.2);
            });
            Frame(p, 6, gc, () =>
            {
                Spin(0.2);
                GC.Collect(0);
                Spin(0.2);
            });
            for (ulong f = 7; f <= 8; f++) Frame(p, f, idle);
            Assert.True(p.FlushEvents(TimeSpan.FromSeconds(5)), "хвост событий не пришёл");
            GC.KeepAlive(keep);
            var events = p.Events!;
            Assert.True(events.Calibrated);

            var a = events.Recent("alloc").Where(e => e.Main && e.Name == "System.Byte[]" && e.Bytes >= 1 << 20).ToList();
            var ae = Assert.Single(a);
            Assert.Equal(4ul, ae.Place.Frame);
            Assert.Equal("alloc", ae.Zone);
            Assert.Equal("frame > outer > alloc", ae.PathText);

            var j = events.Recent("jit").Where(e => e.Name.Contains("sword.test.eventsFresh", StringComparison.Ordinal)).ToList();
            var je = Assert.Single(j);
            Assert.True(je.Main);
            Assert.Equal(5ul, je.Place.Frame);
            Assert.Equal("jit", je.Zone);
            Assert.Equal("optimized", je.Tier);   // динамический метод — сразу оптимизированным

            var g = events.Recent("gc").Where(e => e.Main && e.Name == "induced").ToList();
            var ge = Assert.Single(g);
            Assert.Equal(6ul, ge.Place.Frame);
            Assert.Equal("gc", ge.Zone);
            Assert.InRange(ge.Gen, 0, 2);   // сборка 0 может подняться до 1 (решает GC)
            Assert.True(ge.Ms > 0);
            Assert.Contains(events.Recent("pause"), e => e.Main && e.Place.Frame == 6 && e.Zone == "gc" && e.Ms > 0);

            // Отчёт: итоги событий с типами и местами.
            var report = p.Report();
            Assert.Contains(report["events"]!["alloc"]!["types"]!.AsArray(), t => t!["type"]!.GetValue<string>() == "System.Byte[]");
            Assert.Equal(0, report["events"]!["late"]!.GetValue<long>());
        }
        finally
        {
            p.SetEvents(false);
        }
        Assert.Null(p.Events);
        Assert.False(p.Timeline);
    }

    /// <summary>Отметка времени строго позже прошлой записи ленты (разрешение часов — 100 нс).</summary>
    private static long NextTimestamp()
    {
        long t0 = Stopwatch.GetTimestamp(), t;
        while ((t = Stopwatch.GetTimestamp()) == t0) { }
        while (Stopwatch.GetTimestamp() == t) { }
        return t;
    }

    /// <summary>
    /// Место по ленте — и в ленте без кадров (Headless: только тики), и в кадрах без тиков (App на паузе агента): кадр, тик и
    /// зона верны, а поиск ограничен одним кадром или тиком — не читает кольцо до края (было ~0,5 мс и 11 МБ на событие).
    /// </summary>
    [Fact]
    public void TimelinePlace_TicksWithoutFrames_FramesWithoutTicks_BoundedSearch()
    {
        var p = new CpuProfiler(history: 16);
        int[] zones = [.. Enumerable.Range(0, 60).Select(i => p.Zone("zone" + i))];
        int inner = p.Zone("inner");
        p.SetEvents(true);
        try
        {
            void Zones()
            {
                foreach (int z in zones)
                {
                    p.Begin(z);
                    p.End(z);
                }
            }

            // Headless: кольцо ленты (2¹⁹ записей) переполнено тиками несколько раз.
            for (ulong t = 0; t < 20_000; t++)
            {
                p.BeginTick(t);
                Zones();
                p.EndTick();
            }
            p.BeginTick(20_000);
            p.Begin(inner);
            long inTick = NextTimestamp();
            NextTimestamp();
            p.End(inner);
            p.EndTick();
            long betweenTicks = NextTimestamp();
            var a = p.Place(inTick);
            Assert.True(a.Known);
            Assert.Equal(20_000ul, a.Tick);
            Assert.Null(a.Frame);
            Assert.Equal(inner, a.Path[^1]);
            var b = p.Place(betweenTicks);
            Assert.True(b.Known);
            Assert.Null(b.Tick);
            Assert.Null(b.Frame);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) p.Place(inTick);
            Assert.True(sw.ElapsedMilliseconds < 200, $"1000 мест в ленте тиков — {sw.ElapsedMilliseconds} мс");

            // App на паузе: кадры без тиков.
            for (ulong f = 1; f <= 10_000; f++)
            {
                p.BeginFrame(f);
                Zones();
                p.EndFrame(1, false);
            }
            p.BeginFrame(10_001);
            p.Begin(inner);
            long inFrame = NextTimestamp();
            NextTimestamp();
            p.End(inner);
            p.EndFrame(1, false);
            var c = p.Place(inFrame);
            Assert.True(c.Known);
            Assert.Equal(10_001ul, c.Frame);
            Assert.Null(c.Tick);
            Assert.Equal(inner, c.Path[^1]);
            sw.Restart();
            for (int i = 0; i < 1000; i++) p.Place(inFrame);
            Assert.True(sw.ElapsedMilliseconds < 200, $"1000 мест в ленте кадров без тиков — {sw.ElapsedMilliseconds} мс");
        }
        finally
        {
            p.SetEvents(false);
        }
        Assert.False(p.Place(Stopwatch.GetTimestamp()).Known);   // ленты нет — места нет
    }

    [Fact]
    public void Simulation_WithCountersAndEvents_SameHashes()
    {
        var plain = SpinModule.Build();
        var profiled = SpinModule.Build();
        var p = new CpuProfiler(history: 64);
        profiled.AttachProfiler(p);
        p.SetEvents(true);
        try
        {
            Span<ulong> a = stackalloc ulong[plain.StateParts.Count], b = stackalloc ulong[profiled.StateParts.Count];
            for (int t = 0; t < 60; t++)
            {
                plain.Step();
                profiled.Step();
                Assert.Equal(plain.HashState(a), profiled.HashState(b));
            }
            var ticks = p.RuntimeStats(ProfilePeriod.Tick);
            Assert.Equal(60, ticks.Periods);
            Assert.True(ticks.CpuMs > 0 && ticks.OnCpu > 0);
        }
        finally
        {
            p.SetEvents(false);
        }
    }

    [Fact]
    public void Machine_SpinWindow_ThreadOnCpu_ReportJson()
    {
        // Тесты сборок идут параллельно (verify.ps1 грузит все ядра) — поток могли снять с процессора: лучшее из десяти окон;
        // не вышло и так — вердикт обязан это назвать (счётное окно, поток на CPU < 90 % — «машина»).
        MachineReport m = null!;
        for (int i = 0; i < 10 && (m is null || m.ThreadOnCpu < 0.9); i++)
        {
            var a = MachineSample.Take();
            Spin(30);
            var next = MachineReport.Between(a, MachineSample.Take(), computeBound: true);
            if (m is null || next.ThreadOnCpu > m.ThreadOnCpu) m = next;
        }
        Assert.InRange(m.WallMs, 29, 1000);
        Assert.True(m.ThreadOnCpu > 0.7 || (m.Verdict == MachineVerdict.Machine && m.Text.Contains("на CPU", StringComparison.Ordinal)),
            $"поток на CPU {m.ThreadOnCpu:P0}, вердикт: {m.Text}");
        Assert.InRange(m.SystemBusyShare, 0, 1);
        var json = m.ToJson();
        Assert.Contains(json["verdict"]!.GetValue<string>(), (string[])["код", "среда", "машина"]);
        Assert.NotNull(json["text"]);
        Assert.NotNull(json["mhz"]);
        Assert.NotNull(json["note"]);   // окно 30 мс — загрузка системы в вердикт не идёт
        Assert.True(ThreadClock.CyclesPerSecond > 1e8, $"частота TSC {ThreadClock.CyclesPerSecond}");
    }

    /// <summary>Снимок машины — искусственный: время, система и процесс (100 нс), такты, счётчики среды, частота.</summary>
    private static MachineSample Sample(double seconds, long idle, long kernel, long user, long process, double cpuSeconds = 0,
        int gc = 0, long jit = 0, double mhz = 4800, int limit = 4201) =>
        new((long)(seconds * Stopwatch.Frequency), idle, kernel, user, process,
            new ProfileCounters((long)(cpuSeconds * ThreadClock.CyclesPerSecond), gc, 0, 0, gc * 10_000L, 0, jit, jit, jit * 1000, jit * 1000),
            0, mhz, 4201, 4201, limit);

    [Fact]
    public void Verdict_Criteria()
    {
        var start = Sample(0, 0, 0, 0, 0);
        // Спокойно: система занята на 10 % (из них 8 — наш процесс), поток на CPU всё окно.
        var quiet = MachineReport.Between(start, Sample(1, 9_000, 10_000, 0, 800, cpuSeconds: 0.99), computeBound: true);
        Assert.Equal(MachineVerdict.Code, quiet.Verdict);
        Assert.StartsWith("код: машина спокойна", quiet.Text);
        // Другие процессы — 60 %.
        var busy = MachineReport.Between(start, Sample(1, 3_000, 10_000, 0, 1_000, cpuSeconds: 0.99), computeBound: true);
        Assert.Equal(MachineVerdict.Machine, busy.Verdict);
        Assert.Contains("загрузка системы другими процессами 60 %", busy.Text);
        // Окно 50 мс: загрузка по счётчикам ОС (шаг ~15,6 мс) в вердикт не идёт.
        var shortWindow = MachineReport.Between(start, Sample(0.05, 3_000, 10_000, 0, 1_000, cpuSeconds: 0.0495), computeBound: true);
        Assert.Equal(MachineVerdict.Code, shortWindow.Verdict);
        Assert.Contains("в вердикт не идёт", shortWindow.Note);
        // Счётный поток на CPU половину времени.
        var preempted = MachineReport.Between(start, Sample(1, 9_000, 10_000, 0, 800, cpuSeconds: 0.5), computeBound: true);
        Assert.Equal(MachineVerdict.Machine, preempted.Verdict);
        Assert.Contains("поток на CPU 50 % времени", preempted.Text);
        // Кадр App ждёт GPU: доля CPU потока в вердикт не идёт.
        Assert.Equal(MachineVerdict.Code, MachineReport.Between(start, Sample(1, 9_000, 10_000, 0, 800, cpuSeconds: 0.5), computeBound: false).Verdict);
        // Частота ниже базовой и предел ОС.
        var slow = MachineReport.Between(Sample(0, 0, 0, 0, 0, mhz: 2000), Sample(1, 9_000, 10_000, 0, 800, cpuSeconds: 0.99, mhz: 2000, limit: 3000), computeBound: true);
        Assert.Equal(MachineVerdict.Machine, slow.Verdict);
        Assert.Contains(slow.Reasons, r => r.StartsWith("частота CPU 2000 МГц", StringComparison.Ordinal));
        Assert.Contains(slow.Reasons, r => r.StartsWith("ОС ограничила частоту CPU: 3000 из 4201", StringComparison.Ordinal));
        // Провал частоты только на одной границе (ядро только что проснулось) — не снижение.
        Assert.Equal(MachineVerdict.Code, MachineReport.Between(start, Sample(1, 9_000, 10_000, 0, 800, cpuSeconds: 0.99, mhz: 2000), computeBound: true).Verdict);
        // JIT и GC во время замера — среда; машина спокойна.
        var runtime = MachineReport.Between(start, Sample(1, 9_000, 10_000, 0, 800, cpuSeconds: 0.99, gc: 2, jit: 3), computeBound: true);
        Assert.Equal(MachineVerdict.Runtime, runtime.Verdict);
        Assert.Contains("JIT во время замера на главном потоке: 3 метода", runtime.Text);   // JIT главного потока (фоновый — не вердикт, Пв)
        Assert.Contains("GC во время замера: сборок 2", runtime.Text);
        // Сумма окон: доли — по сумме времён.
        var sum = MachineReport.Combine([quiet, busy])!;
        Assert.Equal(2, sum.Windows);
        Assert.Equal(0.31, sum.OthersBusyShare, 3);   // занято (1000 + 7000) / 20000 минус процесс (800 + 1000) / 20000
    }

    [Fact]
    public void RuntimeMeter_SamplesOncePerWindow_NoAllocations()
    {
        var meter = new RuntimeMeter();
        long t = Stopwatch.GetTimestamp();
        meter.Frame(t);
        Assert.Equal(1, meter.Samples);
        Assert.False(meter.Line.Ready);   // первое окно ещё идёт
        for (int i = 0; i < 1000; i++) meter.Frame(t + i);
        Assert.Equal(1, meter.Samples);   // в кадрах окна — только сравнение времени
        long before = GC.GetAllocatedBytesForCurrentThread();
        meter.Frame(t + 2 * Stopwatch.Frequency);
        for (int i = 0; i < 1000; i++) meter.Frame(t + 2 * Stopwatch.Frequency + i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2, meter.Samples);
        Assert.True(meter.Line.Ready);
        Assert.InRange(meter.Line.OthersBusy, 0, 1);
    }
}
