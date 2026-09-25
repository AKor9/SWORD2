using System.Diagnostics;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// Время стадий тика (бюджеты фич, <c>experiment.sweep</c>): наблюдатель тиков отмечает время после барьера каждой стадии;
/// время стадии — от предыдущей отметки (начало тика — <see cref="Begin"/> перед <see cref="Simulation.Step"/>) до её
/// барьера, то есть системы стадии, её барьер и проверки инвариантов после него. Время систем — профайлер CPU
/// (<see cref="Sword.Engine.Diagnostics.CpuProfiler"/>) отдельным прогоном (<see cref="SimulationMeasure.Run"/> с <c>profile</c>):
/// таймер стадий остаётся — медианы бюджетов меряются без профайлера.
/// Память не выделяет; мир не меняет.
/// </summary>
public sealed class StageTimer : ISimulationObserver
{
    private readonly long[] _elapsed;
    private long _mark;
    private bool _open;

    public StageTimer(Simulation simulation) : this(simulation.World.Stages)
    {
    }

    public StageTimer(IReadOnlyList<string> stages)
    {
        Stages = stages;
        _elapsed = new long[stages.Count];
    }

    public IReadOnlyList<string> Stages { get; }

    /// <summary>Тиков с отметками (после <see cref="Begin"/>).</summary>
    public long Ticks { get; private set; }

    /// <summary>Начало тика: следующий <see cref="Simulation.Step"/> меряется.</summary>
    public void Begin()
    {
        _mark = Stopwatch.GetTimestamp();
        _open = true;
    }

    /// <summary>Забыть замеры (после прогрева).</summary>
    public void Reset()
    {
        Array.Clear(_elapsed);
        Ticks = 0;
    }

    public void AfterStage(Simulation simulation, int stage)
    {
        if (!_open) return;
        long now = Stopwatch.GetTimestamp();
        _elapsed[stage] += now - _mark;
        _mark = now;
    }

    public void AfterTick(Simulation simulation)
    {
        if (!_open) return;
        _open = false;
        Ticks++;
    }

    /// <summary>Среднее время стадии на тик, мс.</summary>
    public double MeanMilliseconds(int stage) => Ticks == 0 ? 0 : _elapsed[stage] * 1000.0 / Stopwatch.Frequency / Ticks;

    /// <summary>Средние по стадиям, мс на тик.</summary>
    public Dictionary<string, double> Means() => Stages.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => MeanMilliseconds(x.i));
}
