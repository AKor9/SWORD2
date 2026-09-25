using System.Reflection.Emit;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests;

/// <summary>
/// Машина в замерах: строка перебора и бюджет фичи несут поля машины и вердикт; метод, скомпилированный
/// посреди замера, отмечается «JIT во время замера»; провал времени бюджета называет вердикт. Подопытный — система, которая
/// на тике 10 (после прогрева 5) компилирует и зовёт новый метод: у каждой симуляции — свой, JIT — в каждом прогоне.
/// </summary>
public sealed class MachineVerdictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-machine-tests", Guid.NewGuid().ToString("N")[..8]);

    public MachineVerdictTests() => Directory.CreateDirectory(Path.Combine(_root, "feat"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class JitModule : IModule
    {
        public string Id => "jit";

        public void Configure(SimulationBuilder b) => b.System("S1", new JitAt(10));
    }

    private sealed class JitAt(ulong tick) : IContinuousSystem
    {
        private static int _methods;

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("test.jit"));

        public void Run(in SystemContext ctx)
        {
            if (ctx.World.Resource<SimTime>().Tick != tick) return;
            var method = new DynamicMethod($"sword.test.jitAt{Interlocked.Increment(ref _methods)}", typeof(int), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4_7);
            il.Emit(OpCodes.Ret);
            GC.KeepAlive(method.CreateDelegate<Func<int>>()());
        }
    }

    private static Simulation Build(ConfigSet config)
    {
        var sim = Simulation.Build(config, ["S1"], [new JitModule()]);
        config.Complete();
        return sim;
    }

    [Fact]
    public void SweepRow_MarksJitDuringMeasurement()
    {
        var (rows, _) = Experiments.Sweep(null, 20, [("sim.seed", ["1", "2"])], 2, 5, Build, systems: false);
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.Machine);
            Assert.Contains(r.Machine!.Reasons, x => x.StartsWith("JIT во время замера", StringComparison.Ordinal));
            Assert.True(r.Machine.Runtime.JitMethods >= 2, $"методов главного потока {r.Machine.Runtime.JitMethods}");   // по одному в повторе
            Assert.Equal(2, r.Machine.Windows);
            Assert.NotEqual(MachineVerdict.Code, r.Machine.Verdict);
        });
    }

    [Fact]
    public void Budget_MachineFieldsAndVerdict_FailureNamesIt()
    {
        File.WriteAllText(Path.Combine(_root, "feat", "README.md"), "# фича\r\n");
        string path = Path.Combine(_root, "feat", FeatureManifest.FileName);
        File.WriteAllText(path, """
            {
              "format": "sword-feature", "version": 1, "id": "jit", "title": "JIT посреди замера", "doc": "README.md",
              "sourceRoots": ["feat"],
              "scenarios": [{ "id": "run", "ticks": 20 }],
              "budgets": [{ "id": "run", "scenario": "run", "msPerTick": 0.000001, "allocatedBytesPerTick": 100000, "repeats": 2, "warmup": 5 }],
              "checklist": ["посмотреть"]
            }
            """);
        var m = FeatureManifest.Load(path);
        // Optimized — время проверяется (и в Debug-прогоне теста): предел заведомо мал, провал должен назвать вердикт.
        var env = new FeatureEnvironment { RepoRoot = _root, Build = Build, Info = new ReplayInfo("1.0.0+test", "tests", "none"), Optimized = true };
        var report = FeatureVerifier.Verify(m, env, new FeatureVerifyOptions { OutputDirectory = Path.Combine(_root, "out"), Views = false });
        var budget = report.Json["budgets"]![0]!;
        var machine = budget["machine"]!;
        Assert.Contains(machine["reasons"]!.AsArray(), r => r!.GetValue<string>().StartsWith("JIT во время замера", StringComparison.Ordinal));
        Assert.Equal(2, machine["runs"]!.AsArray().Count);
        Assert.NotNull(machine["threadOnCpuPct"]);
        Assert.NotNull(machine["othersBusyPct"]);
        Assert.NotNull(budget["profiled"]!["runtime"]);   // прогон с профайлером — среда тиков
        var time = Assert.Single(report.Failures, f => f.Contains("мс/тик (медиана)", StringComparison.Ordinal));
        Assert.Contains("JIT во время замера", time);   // «— среда: …» или «— машина: …; JIT …»
    }
}
