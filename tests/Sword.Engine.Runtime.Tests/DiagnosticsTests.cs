using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Диагностические режимы симуляции (<see cref="SimulationDiagnostics"/>): регистрация ядром и модулем (Id по правилу инварианта,
/// без повторов, только до сборки мира), список с владельцами, включение и выключение, сброс окна — только между тиками; неизвестный
/// режим — ошибка со списком; включённый режим не меняет ни хеш, ни мир.
/// </summary>
public sealed class DiagnosticsTests
{
    /// <summary>Режим теста: включён ли, сколько раз сбрасывали окно.</summary>
    private sealed class Mode(string id) : IDiagnosticMode
    {
        public string Id => id;
        public string Title => "режим теста";
        public bool Enabled { get; private set; }
        public int Resets { get; private set; }

        public void SetEnabled(bool enabled) => Enabled = enabled;

        public void ResetWindow()
        {
            if (Enabled) Resets++;
        }
    }

    public struct Counter
    {
        public int Value;
    }

    /// <summary>Модуль: свой режим и система, которая считает тики (мир меняется — хеш сравним).</summary>
    private sealed class Module(IDiagnosticMode mode, Action<SimulationBuilder>? more = null) : IModule
    {
        public string Id => "diag";

        public void Configure(SimulationBuilder b)
        {
            b.Diagnostic(mode).Resource(new Counter()).System("A", new Count());
            more?.Invoke(b);
        }
    }

    private sealed class Count : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("diag.count")).SetsResource<Counter>();

        public void Run(in SystemContext ctx) => ctx.Commands.SetResource(new Counter { Value = ctx.World.Resource<Counter>().Value + 1 });
    }

    /// <summary>Наблюдатель: пробует включить режим посреди тика.</summary>
    private sealed class MidTick(string id) : ISimulationObserver
    {
        public Exception? Error;

        public void AfterStage(Simulation simulation, int stage)
        {
            try
            {
                simulation.Diagnostics.Set(id, true);
            }
            catch (Exception e)
            {
                Error ??= e;
            }
        }

        public void AfterTick(Simulation simulation)
        {
        }
    }

    private static Simulation Build(IDiagnosticMode mode, Action<SimulationBuilder>? core = null, Action<SimulationBuilder>? more = null) =>
        Simulation.Build(ConfigSet.Empty(), ["A"], core, [new Module(mode, more)]);

    [Fact]
    public void Modes_RegisteredListedSwitched_BetweenTicksOnly_HashUnchanged()
    {
        var mode = new Mode("test.trace");
        var core = new Mode("core.mode");
        var sim = Build(mode, b => b.Diagnostic(core));
        var plain = Build(new Mode("test.trace"));
        var d = sim.Diagnostics;
        Assert.Equal(2, d.Count);
        Assert.Equal(new DiagnosticInfo("core.mode", "режим теста", "core", false), d[0]);
        Assert.Equal(new DiagnosticInfo("test.trace", "режим теста", "diag", false), d[1]);
        Assert.True(d.Has("test.trace") && !d.Has("test.nope"));
        Assert.False(d.IsEnabled("test.trace") || d.IsEnabled("test.nope"));
        Assert.Empty(d.EnabledIds());

        d.Set("test.trace", true);
        Assert.True(mode.Enabled && d.IsEnabled("test.trace") && d[1].Enabled);
        Assert.Equal(["test.trace"], d.EnabledIds());
        d.ResetWindow("test.trace");
        Assert.Equal(1, mode.Resets);
        var error = Assert.Throws<ArgumentException>(() => d.Set("test.nope", true));
        Assert.Contains("core.mode, test.trace", error.Message);
        Assert.Throws<ArgumentException>(() => d.ResetWindow("test.nope"));

        // Режим не меняет ни мир, ни хеш: 30 тиков с ним и без — те же хеши.
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
            plain.Step();
            Assert.Equal(plain.HashState().Total, sim.HashState().Total);
        }

        // Посреди тика (наблюдатель после барьера) — нельзя.
        var observer = new MidTick("core.mode");
        sim.Observe(observer);
        sim.Step();
        Assert.IsType<InvalidOperationException>(observer.Error);
        Assert.False(core.Enabled);
        sim.Unobserve(observer);
        d.Set("test.trace", false);
        Assert.False(mode.Enabled);
    }

    [Fact]
    public void Registration_ChecksIdAndDuplicates_OnlyBeforeWorldIsBuilt()
    {
        Assert.Contains("нужно", Assert.Throws<InvalidOperationException>(() => Build(new Mode("Bad"))).Message);
        Assert.Contains("дважды", Assert.Throws<InvalidOperationException>(() => Build(new Mode("x.y"), more: b => b.Diagnostic(new Mode("x.y")))).Message);
        Assert.Throws<InvalidOperationException>(() => Build(new Mode("x.y"), more: b => b.OnWorldBuilt(_ => b.Diagnostic(new Mode("x.z")))));
        Assert.Equal(0, Simulation.Build(ConfigSet.Empty(), ["A"], []).Diagnostics.Count);
    }
}
