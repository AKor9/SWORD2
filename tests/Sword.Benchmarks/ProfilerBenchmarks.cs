using BenchmarkDotNet.Attributes;
using Sword.Engine.Diagnostics;

namespace Sword.Benchmarks;

/// <summary>
/// Цена пары <see cref="CpuProfiler.Begin"/>/<see cref="CpuProfiler.End"/>: выключенный — почти 0 (до
/// <see cref="Program.MaxDisabledPairNs"/>), включённый — 0 байт. <see cref="Loop"/> — тот же цикл без профайлера (вычитается).
/// </summary>
[MemoryDiagnoser]
public class ProfilerZone
{
    public const int Pairs = 1000;

    private CpuProfiler _off = null!, _on = null!;
    private int _offZone, _onZone;
    private int _sink;

    [GlobalSetup]
    public void Setup()
    {
        _off = new CpuProfiler();
        _offZone = _off.Zone("zone");
        _on = new CpuProfiler();
        _onZone = _on.Zone("zone");
        _on.SetEnabled(true);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Pairs)]
    public int Loop()
    {
        int s = _sink;
        for (int i = 0; i < Pairs; i++) s += i;
        return _sink = s;
    }

    [Benchmark(OperationsPerInvoke = Pairs)]
    public int Disabled() => Run(_off, _offZone);

    [Benchmark(OperationsPerInvoke = Pairs)]
    public int Enabled() => Run(_on, _onZone);

    private int Run(CpuProfiler p, int zone)
    {
        int s = _sink;
        p.BeginFrame(0);
        for (int i = 0; i < Pairs; i++)
        {
            p.Begin(zone);
            s += i;
            p.End(zone);
        }
        p.EndFrame(1, service: false);
        return _sink = s;
    }
}

/// <summary>Цена периода профайлера (кадр без зон) выключенного и включённого. Цель — 0 байт у обоих.</summary>
[MemoryDiagnoser]
public class ProfilerPeriod
{
    private CpuProfiler _off = null!, _on = null!;

    [GlobalSetup]
    public void Setup()
    {
        _off = new CpuProfiler();
        _on = new CpuProfiler();
        _on.SetEnabled(true);
    }

    [Benchmark]
    public void PeriodDisabled()
    {
        _off.BeginFrame(1);
        _off.EndFrame(1, service: false);
    }

    [Benchmark]
    public void PeriodEnabled()
    {
        _on.BeginFrame(1);
        _on.EndFrame(1, service: false);
    }
}
