namespace Sword.Engine.Client.Tests;

/// <summary>Числа кадра для HUD (<see cref="FrameStatsMeter"/>) и бенч кадра (<see cref="FrameBench"/>) — без окна и GPU.</summary>
public class FrameToolsTests
{
    [Fact]
    public void Fps_IsSmoothedFrom60_Like_SWORD()
    {
        var m = new FrameStatsMeter();
        m.Frame(10, 1, 0);   // 100 кадров/с: 60 + (100 − 60)·0,08
        Assert.Equal(63.2, m.Fps, 9);
        for (int i = 0; i < 300; i++) m.Frame(10, 1, 0);
        Assert.Equal(100, m.Fps, 6);
        m.Frame(0, 1, 0);    // нулевой шаг — 1e-4 с, не бесконечность
        Assert.True(double.IsFinite(m.Fps));
    }

    [Fact]
    public void FrameMsMax_IsWorstOfPreviousWindowOf60()
    {
        var m = new FrameStatsMeter();
        m.Frame(5, 0, 0);                              // кадр 0 закрывает пустое окно: худший — 5
        Assert.Equal(5, m.FrameMsMax);
        m.Frame(40, 0, 0);                             // рывок в окне 1…60
        for (int i = 2; i < 60; i++) m.Frame(5, 0, 0);
        Assert.Equal(5, m.FrameMsMax);                 // окно ещё не закрыто
        m.Frame(5, 0, 0);                              // кадр 60 закрывает окно
        Assert.Equal(40, m.FrameMsMax);
        for (int i = 61; i <= 120; i++) m.Frame(5, 0, 0);
        Assert.Equal(5, m.FrameMsMax);                 // рывок ушёл с окном
    }

    [Fact]
    public void Stats_CarryLastFrame_TicksAndVsync()
    {
        var m = new FrameStatsMeter();
        m.Frame(16, 3.5, 1.25);
        var s = m.Stats(ticks: 2, vsync: true);
        Assert.Equal((16.0, 3.5, 1.25, 2, true), (s.FrameMs, s.CpuMs, s.WaitMs, s.Ticks, s.Vsync));
    }

    [Fact]
    public void Bench_SkipsWarmup_EndsAtGameTime_PercentilesLike_SWORD()
    {
        var bench = new FrameBench(5);
        bench.Frame(3.9, 100, 50, 1);                  // прогрев — не в счёт
        Assert.Equal(0, bench.Frames);
        Assert.False(bench.Done(6));                   // кадров замера нет — не конец
        for (int i = 1; i <= 100; i++) bench.Frame(4 + i * 0.01, i, 2, 1);
        Assert.False(bench.Done(4.99));
        Assert.True(bench.Done(5));
        var r = bench.Result()!;
        Assert.Equal(100, r.Frames);
        Assert.Equal(50.5, r.AverageMs);
        Assert.Equal(51, r.P50Ms);                     // [n/2] сортированных
        Assert.Equal(100, r.P99Ms);                    // [⌊0,99·n⌋]
        Assert.Equal(100, r.MaxMs);
        Assert.Equal((2.0, 1.0), (r.CpuMs, r.WaitMs));
        Assert.Equal(Math.Round(1000 / 50.5, 1), r.Fps);
        Assert.Equal(0, r.GpuFrames);                  // времён GPU не было — нулевые GPU-поля, проходов нет
        Assert.Empty(r.Passes!);
    }

    [Fact]
    public void Bench_GpuTime_AverageP99_PassesPerFrame_Like_BenchRecorder()
    {
        // Точка Пг: разобранные кадры GPU (раз на новые данные) — среднее и p99 кадра, проходы — сумма за замер / кадров GPU.
        var bench = new FrameBench(5);
        bench.GpuFrame(3.9, 50);                        // прогрев — не в счёт, и его проходы тоже
        bench.GpuPass("scene", 40);
        for (int i = 1; i <= 100; i++)
        {
            bench.Frame(4 + i * 0.01, 10, 2, 1);
            bench.GpuFrame(4 + i * 0.01, i * 0.01);
            bench.GpuPass("scene", 0.5);
            if (i % 2 == 0) bench.GpuPass("debug", 0.2);   // проход не в каждом кадре — мс на кадр по всем
            bench.GpuPass("hud", 0.1);
            bench.GpuPass("hud", 0.1);                       // одноимённые в кадре складываются
        }
        var r = bench.Result()!;
        Assert.Equal(100, r.GpuFrames);
        Assert.Equal(0.505, r.GpuMs, 4);
        Assert.Equal(1.0, r.GpuP99Ms, 4);                // [⌊0,99·n⌋] сортированных
        Assert.Equal(["scene", "hud", "debug"], r.Passes!.Select(p => p.Name));   // порядок первой встречи
        Assert.Equal([0.5, 0.2, 0.1], r.Passes!.Select(p => p.Ms));
        Assert.EndsWith($"; GPU {r.GpuMs:F2} мс, p99 {r.GpuP99Ms:F2}", r.ToString());
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bench_NeedsMoreThanWarmup(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBench(seconds));

    [Fact]
    public void ScreenshotAction_IsEngineAction_BoundWithGameActions()
    {
        // Композиция разрешает привязки игры и клиента вместе: снимок — F4 по умолчанию, меняется секцией input.
        var game = new InputAction("move", "W");
        var bindings = InputBindings.Resolve([game, .. ClientHost.Actions], null);
        Assert.Equal("F4", bindings.Keys["screenshot"]);
        Assert.Throws<ArgumentException>(() => InputBindings.Resolve([new InputAction("screenshot", "F5"), .. ClientHost.Actions], null));
    }

    /// <summary>
    /// 6ж: сторож кадров — кадры идут, он молчит; кадров нет дольше срока — ровно один вызов со словами «нет кадров N с» и тем, свёрнуто ли
    /// окно (бенч в свёрнутом окне висел 28 мин: кадров нет, игровое время бенча стоит).
    /// </summary>
    [Fact]
    public void Watchdog_SilentWhileFrames_FiresOnceWhenFramesStop()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool minimized = false;
        using (var w = new FrameWatchdog(1, () => minimized, calls.Enqueue))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 2)
            {
                w.Frame();
                Thread.Sleep(10);
            }
            Assert.Empty(calls);
            Assert.False(w.Fired);
            minimized = true;
            clock.Restart();
            while (calls.IsEmpty && clock.Elapsed.TotalSeconds < 5) Thread.Sleep(10);
            Thread.Sleep(1200);   // второго вызова нет
            Assert.True(w.Fired);
            string message = Assert.Single(calls);
            Assert.Contains("нет кадров 1 с", message);
            Assert.Contains("окно свёрнуто", message);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameWatchdog(0, () => false, _ => { }));
    }
}
