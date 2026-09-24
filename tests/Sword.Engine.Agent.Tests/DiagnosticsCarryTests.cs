using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// Диагностические режимы в сеансе агента (7и, <see cref="AgentSimulation"/>): включённый у текущей симуляции режим включён и у новой —
/// после перемотки назад (повтор таймлайна) и после <c>session.start</c> — до её первого тика; выключенный не включается. Режим у
/// каждой симуляции свой (свой объект).
/// </summary>
public sealed class DiagnosticsCarryTests
{
    private sealed class Mode : IDiagnosticMode
    {
        public string Id => "test.trace";
        public string Title => "режим теста";
        public bool Enabled { get; private set; }

        public void SetEnabled(bool enabled) => Enabled = enabled;

        public void ResetWindow()
        {
        }
    }

    /// <summary>Модуль: свой режим у каждой сборки симуляции (список — по порядку сборок).</summary>
    private sealed class Module(List<Mode> modes) : IModule
    {
        public string Id => "diag";

        public void Configure(SimulationBuilder b)
        {
            var mode = new Mode();
            modes.Add(mode);
            b.Diagnostic(mode);
        }
    }

    [Fact]
    public void EnabledModes_CarriedIntoRebuiltSimulation_BeforeFirstTick()
    {
        var modes = new List<Mode>();
        Simulation Build(ConfigSet config)
        {
            var sim = Simulation.Build(config, ["S"], [new Module(modes)]);
            config.Complete();
            return sim;
        }
        var session = new AgentSimulation(Build(ConfigSet.Empty()), new ReplayInfo("1.0.0+test", "tests", "none"), Build,
            sets => ConfigSet.Empty([.. sets]));
        var first = session.Current;
        first.Diagnostics.Set("test.trace", true);
        for (int i = 0; i < 6; i++) first.Step();

        // Перемотка назад — новая симуляция (повтор таймлайна), режим включён до её первого тика.
        Assert.True(session.Seek(3));
        var replayed = session.Current;
        Assert.NotSame(first, replayed);
        Assert.Equal(3ul, replayed.Tick);
        Assert.Equal(2, modes.Count);
        Assert.True(modes[1].Enabled && replayed.Diagnostics.IsEnabled("test.trace"));

        // session.start без реплея — тоже; выключенный не переносится.
        var started = session.Start(ConfigSet.Empty(), null, null);
        Assert.True(started.Diagnostics.IsEnabled("test.trace") && modes[2].Enabled);
        started.Diagnostics.Set("test.trace", false);
        var plain = session.Start(ConfigSet.Empty(), null, null);
        Assert.False(plain.Diagnostics.IsEnabled("test.trace") || modes[3].Enabled);
    }
}
