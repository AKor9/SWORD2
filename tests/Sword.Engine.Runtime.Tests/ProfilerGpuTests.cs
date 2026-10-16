using System.Diagnostics;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// GPU-время в профайлере: зоны <see cref="ProfileZoneKind.Gpu"/> (<see cref="CpuProfiler.RecordGpu"/>) вне дерева CPU, в отчёте —
/// <c>frames.gpu</c> с отставанием, в сводке F2 — свои строки; 0 байт на кадр. Часы — <see cref="FakeProfileClock"/>.
/// </summary>
public sealed class ProfilerGpuTests
{
    private readonly FakeProfileClock _clock = new();

    private sealed record Zones(int Work, int Wait, int Frame, int A, int B);

    private static Zones Register(CpuProfiler p) => new(p.Zone("work"), p.Zone("wait", ProfileZoneKind.GpuWait),
        p.Zone(CpuProfiler.GpuFrameName, ProfileZoneKind.Gpu), p.Zone("gpu.a", ProfileZoneKind.Gpu), p.Zone("gpu.b", ProfileZoneKind.Gpu));

    /// <summary>
    /// Кадр: ожидание GPU, работа, 10 мкс своего (вне зон); с <paramref name="gpu"/> — времена кадра на 3 раньше (кадр 2 мс: a —
    /// <paramref name="a"/>, b — 0,25).
    /// </summary>
    private void Frame(CpuProfiler p, Zones z, ulong number, bool gpu, double a = 1.5, double wait = 0)
    {
        p.BeginFrame(number);
        _clock.Run(0.01);
        p.Begin(z.Wait);
        if (wait > 0) _clock.Run(wait);
        p.End(z.Wait);
        if (gpu)
        {
            p.NoteGpuFrame(3);
            p.RecordGpu(z.Frame, a + 0.5);
            p.RecordGpu(z.A, a);
            p.RecordGpu(z.B, 0.25);
        }
        p.Begin(z.Work);
        p.End(z.Work);
        p.EndFrame(1, service: false);
    }

    [Fact]
    public void GpuZones_OutsideCpuTree_ReportSection_Digest_ZeroBytes()
    {
        var p = new CpuProfiler(64, _clock);
        p.SetEnabled(true);
        var z = Register(p);
        var digest = new ProfileDigest(p);
        for (ulong f = 1; f <= 3; f++) Frame(p, z, f, gpu: false);   // первые кадры — времён GPU ещё нет
        for (ulong f = 4; f <= 40; f++) Frame(p, z, f, gpu: true);

        var stats = p.Stats(ProfilePeriod.Frame);
        var a = stats.Single(s => s.Name == "gpu.a");
        Assert.Equal(ProfileZoneKind.Gpu, a.Kind);
        Assert.Null(a.Parent);
        Assert.Equal(1.5 * 37 / 40, a.MeanMs, 3);   // мс на кадр — по всем кадрам итогов
        Assert.Equal(37 / 40.0, a.Presence, 3);
        Assert.Equal(1.5, a.MaxMs, 3);
        Assert.Equal(0, a.Bytes);
        var frame = stats.Single(s => s.Name == "frame");
        // GPU-время — не дочернее кадра: своё время кадра не съедено двумя миллисекундами GPU.
        Assert.True(frame.MeanMs < 1 && frame.SelfMs > 0, $"кадр {frame.MeanMs} мс, своё {frame.SelfMs}");
        Assert.Equal(0.01, frame.SelfMs, 4);

        var report = p.Report();
        var frames = report["frames"]!;
        Assert.DoesNotContain(frames["zones"]!.AsArray(), n => n!["name"]!.GetValue<string>().StartsWith("gpu.", StringComparison.Ordinal));
        var gpu = frames["gpu"]!;
        Assert.Equal(3, gpu["lagFrames"]!.GetValue<int>());
        Assert.Equal(CpuProfiler.GpuFrameName, gpu["frame"]!["name"]!.GetValue<string>());
        Assert.Equal("Gpu", gpu["frame"]!["kind"]!.GetValue<string>());
        Assert.Equal(["gpu.a", "gpu.b"], gpu["passes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
        var last = frames["history"]!.AsArray()[^1]!;
        Assert.Equal(40ul, last["frame"]!.GetValue<ulong>());
        Assert.Equal(37ul, last["gpuFrame"]!.GetValue<ulong>());
        Assert.Null(frames["history"]!.AsArray()[0]!["gpuFrame"]);
        var summary = p.Summary();
        Assert.DoesNotContain(summary["top"]!.AsArray(), n => n!["name"]!.GetValue<string>().StartsWith("gpu.", StringComparison.Ordinal));
        Assert.Equal("gpu.a", summary["gpu"]!["passes"]![0]!["name"]!.GetValue<string>());

        // Сводка F2: зоны CPU — без GPU, проходы GPU — свои строки (имя без «gpu.»).
        long now = _clock.Now;
        digest.Update(now);
        for (ulong f = 41; f <= 60; f++) Frame(p, z, f, gpu: true);
        now += Stopwatch.Frequency;
        long before = GC.GetAllocatedBytesForCurrentThread();
        digest.Update(now);
        for (ulong f = 61; f <= 80; f++) Frame(p, z, f, gpu: true);   // запись GPU-времён — 0 байт
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(digest.Ready);
        Assert.Equal(2.0, digest.GpuFrameMs, 3);
        Assert.Equal(2, digest.GpuCount);
        Assert.Equal("a", digest.GpuName(0).ToString());
        Assert.Equal(1.5, digest.GpuMs(0), 3);
        for (int i = 0; i < digest.Count; i++) Assert.NotEqual(ProfileZoneKind.Gpu, p.KindOf(digest.Zone(i)));

        // Выключенный профайлер и общий Off — ничего не пишут.
        p.SetEnabled(false);
        p.BeginFrame(81);
        p.RecordGpu(z.A, 100);
        p.NoteGpuFrame(3);
        p.EndFrame(1, false);
        CpuProfiler.Off.RecordGpu(0, 100);
        Assert.Equal(1.5, p.Stats(ProfilePeriod.Frame).Single(s => s.Name == "gpu.a").MaxMs, 3);
    }
}
