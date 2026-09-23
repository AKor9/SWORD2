using System.Diagnostics;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// GPU-время проходов в профайлере кадра (этап П, точка Пг) без GPU: рендер пишет времена разобранного кадра зонами вида
/// <see cref="ProfileZoneKind.Gpu"/> (<see cref="CpuProfiler.RecordGpu"/>) — они вне дерева CPU (своё время кадра, главная
/// зона и сводка зон CPU их не видят), в отчёте — секция <c>frames.gpu</c> с отставанием, в сводке F2 — свои строки, в файле
/// аномалии «ожидание GPU» — разбор по проходам; 0 байт на кадр.
/// </summary>
[Collection(RuntimeEventsCollection.Name)]
public sealed class ProfilerGpuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-gpu-profile-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void SpinCpu(double ms)
    {
        ulong until = ThreadClock.Cycles() + (ulong)(ms / 1000 * ThreadClock.CyclesPerSecond);
        while (ThreadClock.Cycles() < until) { }
    }

    private sealed record Zones(int Work, int Wait, int Frame, int A, int B);

    private static Zones Register(CpuProfiler p) => new(p.Zone("work"), p.Zone("wait", ProfileZoneKind.GpuWait),
        p.Zone(CpuProfiler.GpuFrameName, ProfileZoneKind.Gpu), p.Zone("gpu.a", ProfileZoneKind.Gpu), p.Zone("gpu.b", ProfileZoneKind.Gpu));

    /// <summary>Кадр: работа, ожидание GPU; с <paramref name="gpu"/> — времена кадра на 3 раньше (кадр 2 мс: a — <paramref name="a"/>, b — 0,25).</summary>
    private static void Frame(CpuProfiler p, Zones z, ulong number, bool gpu, double a = 1.5, double wait = 0)
    {
        p.BeginFrame(number);
        p.Begin(z.Wait);
        if (wait > 0) SpinCpu(wait);
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
        var p = new CpuProfiler(64);
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
        long now = Stopwatch.GetTimestamp();
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

    [Fact]
    public void GpuWaitAnomaly_GpuSection_ByPasses_OfFrameItWaitedFor()
    {
        var p = new CpuProfiler(64);
        var anomalies = p.CaptureAnomalies(new AnomalyOptions { Directory = _dir });
        p.SetEnabled(true);
        var z = Register(p);
        for (ulong f = 1; f <= 100; f++) Frame(p, z, f, gpu: true);
        // Кадр 101 ждёт GPU 8 мс (ожидание — счётный цикл внутри зоны ожидания), а разобранный в нём кадр 98 — долгий проход a.
        Frame(p, z, 101, gpu: true, a: 6.5, wait: 8);
        for (ulong f = 102; f <= 120; f++) Frame(p, z, f, gpu: true);
        Assert.True(anomalies.Flush(TimeSpan.FromSeconds(10)));

        var list = anomalies.List();
        var x = list.Single(e => e.Number == 101);
        Assert.True(x.Class is AnomalyClass.GpuWait or AnomalyClass.Machine, $"{x.Text} [{x.Causes}]");
        Assert.True(x.Causes.GpuWaitMs >= 7, $"ожидание GPU в разборе {x.Causes.GpuWaitMs} мс");
        Assert.DoesNotContain("gpu.", x.ZoneName);   // GPU-время — не главная зона CPU
        Assert.Equal(AnomalyFile.Written, x.File);
        var json = JsonNode.Parse(File.ReadAllText(x.Path!))!.AsObject();
        Assert.DoesNotContain(json["zones"]!.AsArray(), n => n!["name"]!.GetValue<string>().StartsWith("gpu.", StringComparison.Ordinal));
        var gpu = json["gpu"]!;
        Assert.Equal(98ul, gpu["frame"]!.GetValue<ulong>());
        Assert.Equal(3, gpu["lagFrames"]!.GetValue<int>());
        Assert.Equal(7.0, gpu["frameMs"]!.GetValue<double>(), 3);
        Assert.Equal("a", gpu["passes"]![0]!["name"]!.GetValue<string>());   // по лишнему времени — наверху
        Assert.InRange(gpu["passes"]![0]!["excessMs"]!.GetValue<double>(), 4.9, 5.1);
        Assert.Equal(98ul, json["header"]!["gpuFrame"]!.GetValue<ulong>());
        anomalies.Dispose();
    }
}
