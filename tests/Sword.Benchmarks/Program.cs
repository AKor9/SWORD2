using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Sword.Benchmarks;

/// <summary>
/// Микробенчмарки ECS и вердикт: обход медленнее сырого цикла не более чем на <see cref="MaxRatio"/>, команды и журналы —
/// 0 байт на тик. Итог — <c>artifacts/bench/ecs.json</c>, код 0 — цели выполнены; <c>--quick</c> — короткий прогон.
/// </summary>
public static class Program
{
    /// <summary>Верх цели «20–30 %»: отношение медиан ECS / сырой цикл.</summary>
    public const double MaxRatio = 1.30;

    /// <summary>Пара Begin/End выключенного профайлера сверх пустого цикла, нс: ~60 пар на тик — меньше 0,1 мкс.</summary>
    public const double MaxDisabledPairNs = 1.5;

    public static int Main(string[] args)
    {
#if DEBUG
        Console.Error.WriteLine("бенчмарки — только Release: dotnet run -c Release --project tests/Sword.Benchmarks");
        return 2;
#else
        bool quick = args.Contains("--quick");
        string root = RepoRoot();
        string outDir = Path.Combine(root, "artifacts", "bench");
        Directory.CreateDirectory(outDir);

        // В процессе: BenchmarkDotNet не собирает отдельный проект (правила сборки репозитория ему ни к чему).
        var job = (quick ? Job.ShortRun : Job.Default.WithWarmupCount(6).WithIterationCount(20))
            .WithToolchain(InProcessEmitToolchain.Instance);
        var config = ManualConfig.CreateMinimumViable()
            .AddJob(job)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(MarkdownExporter.GitHub)
            .WithArtifactsPath(Path.Combine(outDir, "BenchmarkDotNet"));

        // Профайлер CPU — свой итог artifacts/bench/profiler.json; --profiler — только он. В полном прогоне — последним:
        // бенчмарки идут в одном процессе, и его прогон не должен стоять перед замерами ECS и поз.
        if (args.Contains("--profiler")) return Profiler(config, outDir, quick) ? 0 : 1;
        // Поиск Motion Matching — замер себе, не цель: свой итог artifacts/bench/motion.json, только по --motion.
        if (args.Contains("--motion")) return MotionSearchReport.Run(config, outDir, quick) ? 0 : 1;

        var iteration = BenchmarkRunner.Run<EcsIteration>(config);
        var commands = BenchmarkRunner.Run<EcsCommandsAndJournals>(config);

        var chunks = new List<object>();
        bool ok = true;
        foreach (int chunkBytes in new[] { 16 * 1024, 64 * 1024 })
        {
            double raw = Median(iteration, nameof(EcsIteration.RawSpan), chunkBytes);
            double ecs = Median(iteration, nameof(EcsIteration.EcsByColumnLength), chunkBytes);
            double naive = Median(iteration, nameof(EcsIteration.EcsByChunkCount), chunkBytes);
            double ratio = ecs / raw;
            ok &= ratio <= MaxRatio;
            chunks.Add(new
            {
                chunkBytes, rawNs = Math.Round(raw), ecsNs = Math.Round(ecs), ratio = Math.Round(ratio, 3),
                naiveNs = Math.Round(naive), naiveRatio = Math.Round(naive / raw, 3),
            });
        }
        var tick = commands.Reports.Single();
        long bytes = tick.GcStats.GetBytesAllocatedPerOperation(tick.BenchmarkCase) ?? -1;
        ok &= bytes == 0;

        // Сэмплинг поз (раздел 12): время — для сравнения будущих версий, цель — 0 байт на тик.
        var poses = BenchmarkRunner.Run<PoseSampling>(config);
        var animation = poses.Reports.Select(r =>
        {
            long b = r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) ?? -1;
            ok &= b == 0;
            return new
            {
                fighters = (int)r.BenchmarkCase.Parameters["Fighters"], joints = PoseSampling.Joints,
                tickNs = Math.Round(r.ResultStatistics!.Median), bytesPerTick = b,
            };
        }).OrderBy(x => x.fighters).ToList();

        var result = new
        {
            ok, quick, maxRatio = MaxRatio, entities = EcsIteration.Entities, iteration = chunks,
            commandsAndJournals = new { tickNs = Math.Round(tick.ResultStatistics!.Median), bytesPerTick = bytes },
            poseSampling = animation,
            runtime = Environment.Version.ToString(), machine = Environment.MachineName, date = DateTime.UtcNow.ToString("u"),
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(Path.Combine(outDir, "ecs.json"), json);
        Console.WriteLine(json);
        bool profilerOk = Profiler(config, outDir, quick);
        return ok && profilerOk ? 0 : 1;
#endif
    }

    /// <summary>Цена пары Begin/End сверх пустого цикла, выключенного и включённого; 0 байт. Итог — <c>artifacts/bench/profiler.json</c>.</summary>
    private static bool Profiler(IConfig config, string outDir, bool quick)
    {
        var summary = BenchmarkRunner.Run<ProfilerZone>(config);
        double Ns(string method) => summary.Reports.Single(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name == method).ResultStatistics!.Median;
        long Bytes(string method)
        {
            var r = summary.Reports.Single(x => x.BenchmarkCase.Descriptor.WorkloadMethod.Name == method);
            return r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) ?? -1;
        }
        double loop = Ns(nameof(ProfilerZone.Loop)), disabled = Ns(nameof(ProfilerZone.Disabled)), enabled = Ns(nameof(ProfilerZone.Enabled));
        long disabledBytes = Bytes(nameof(ProfilerZone.Disabled)), enabledBytes = Bytes(nameof(ProfilerZone.Enabled));
        // Период профайлера — время и 0 байт.
        var counters = BenchmarkRunner.Run<ProfilerPeriod>(config);
        var counterResults = counters.Reports.ToDictionary(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name, r => new
        {
            ns = Math.Round(r.ResultStatistics!.Median, 1), bytes = r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) ?? -1,
        });
        bool ok = disabled - loop <= MaxDisabledPairNs && disabledBytes == 0 && enabledBytes == 0 && counterResults.Values.All(c => c.bytes == 0);
        var result = new
        {
            ok, quick, maxDisabledPairNs = MaxDisabledPairNs,
            loopNs = Math.Round(loop, 3), disabledNs = Math.Round(disabled, 3), enabledNs = Math.Round(enabled, 3),
            disabledPairNs = Math.Round(disabled - loop, 3), enabledPairNs = Math.Round(enabled - loop, 3),
            disabledBytes, enabledBytes,
            counters = counterResults,
            timer = new { frequency = System.Diagnostics.Stopwatch.Frequency },
            runtime = Environment.Version.ToString(), machine = Environment.MachineName, date = DateTime.UtcNow.ToString("u"),
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(Path.Combine(outDir, "profiler.json"), json);
        Console.WriteLine(json);
        return ok;
    }

    private static double Median(Summary summary, string method, int chunkBytes) =>
        summary.Reports.Single(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name == method
            && (int)r.BenchmarkCase.Parameters["ChunkBytes"] == chunkBytes).ResultStatistics!.Median;

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SWORD.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("не найден корень репозитория (SWORD.slnx)");
    }
}
