using BenchmarkDotNet.Attributes;
using Sword.Engine.Diagnostics;

namespace Sword.Benchmarks;

/// <summary>
/// Цена пары <see cref="CpuProfiler.Begin"/>/<see cref="CpuProfiler.End"/> (этап П, точка Па): выключенный профайлер —
/// проверка поля на вызов (цель — почти 0, в пределах <see cref="Program.MaxDisabledPairNs"/>), включённый — время и
/// выделенные байты потока на начале и конце зоны, 0 байт. Цикл из <see cref="Pairs"/> пар внутри кадра профайлера;
/// <see cref="Loop"/> — тот же цикл без профайлера (его вычитают).
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

/// <summary>
/// Цена счётчиков точки Пб: опрос счётчиков среды без тактов потока и с ними (<see cref="ProfileCounters.Read"/>: сборки,
/// пауза, выделения процесса, JIT; такты — <c>QueryThreadCycleTime</c>), период профайлера (кадр без зон) выключенного и
/// включённого — два опроса на период, снимок машины на границе замера без пробы частоты и с ней, кадр строки HUD
/// (<see cref="RuntimeMeter.Frame"/> — сравнение времени, опрос раз в секунду). Цель — 0 байт у всех.
/// </summary>
[MemoryDiagnoser]
public class ProfilerCounters
{
    private CpuProfiler _off = null!, _on = null!;
    private readonly RuntimeMeter _meter = new();
    private long _sink;

    [GlobalSetup]
    public void Setup()
    {
        _off = new CpuProfiler();
        _on = new CpuProfiler();
        _on.SetEnabled(true);
        _meter.Frame(System.Diagnostics.Stopwatch.GetTimestamp());
    }

    [Benchmark]
    public long CountersManaged() => _sink += ProfileCounters.Read(cycles: false).Gen0;

    [Benchmark]
    public long CountersWithCycles() => _sink += ProfileCounters.Read().ThreadCycles;

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

    [Benchmark]
    public long MachineSampleNoProbe() => _sink += MachineSample.Take(probe: false).Time;

    [Benchmark]
    public long MachineSampleWithProbe() => _sink += MachineSample.Take().Time;

    [Benchmark]
    public void HudMeterFrame() => _meter.Frame(System.Diagnostics.Stopwatch.GetTimestamp());
}
