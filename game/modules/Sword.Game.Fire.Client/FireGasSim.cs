using Sword.Engine.Client;
using Sword.Engine.Fluid3D;
using Sword.Engine.Render;

namespace Sword.Game.Fire;

/// <summary>Числа 3D-дыма, которые ручки F3 меняют на ходу (клиентские: в реплей и <c>sword.user.json</c> не идут).</summary>
public sealed class FireGasTuning
{
    /// <summary>
    /// Газ дыма факела: умолчания Fluid3D (шаблон Epic), затухание плотности k 2 вместо 1 — столб гаснет с высотой, как у LIVE-2; плавучесть
    /// β 1.5 вместо 0.5 — дым поднимается быстрее и тянется в нить, а не копится облаком над шишкой; вихревой шум 2.5 м/с² с шагом 40 см в
    /// дыме от ρ 0.08, узор поднимается 0.3 м/с — S-завитки столба (подгонка по кадрам, README модуля, «Дым»).
    /// </summary>
    public static Fluid3DParams DefaultParams { get; } = Fluid3DParams.Smoke with
    {
        DensityDecay = 2f, TemperatureBuoyancy = 1.5f, CurlNoise = 2.5f, CurlNoiseScale = 0.4f, CurlNoiseRise = 0.3f, CurlNoiseDensity = 0.08f,
    };

    /// <summary>
    /// Источник у шишки: шар 3 см вместо 5 (столб тоньше) с выбросом 15/с вместо 5 (дыма в секунду почти столько же), температура 30/с вместо
    /// 10 (быстрый подъём, с плавучестью β 1.5), раскачка 0.3 м/с узлами каждые 12 шагов (подгонка по кадрам, README модуля, «Дым»).
    /// </summary>
    public static Fluid3DEmitterParams DefaultEmitter { get; } = Fluid3DEmitterParams.Torch with
    {
        Radius = 0.03f, Density = 15f, Temperature = 30f, Sway = 0.3f, SwaySteps = 12,
    };

    private Fluid3DParams _params = DefaultParams;
    private Fluid3DEmitterParams _emitter = DefaultEmitter;
    private FireGasFeedParams _feed = FireGasFeedParams.Torch;

    /// <summary>Дым из пламени (<see cref="FireGasFeedParams"/>): карта источника по полю пламени и доля шара у шишки.</summary>
    public FireGasFeedParams Feed
    {
        get => _feed;
        set
        {
            value.Validate();
            _feed = value;
        }
    }

    /// <summary>Параметры газа: давление зажимается в устойчивое (<see cref="Fluid3DParams.WithStablePressure"/>); сетка не меняется.</summary>
    public Fluid3DParams Params
    {
        get => _params;
        set
        {
            var v = value.WithStablePressure();
            v.Validate();
            if (v.Nx != _params.Nx || v.Ny != _params.Ny || v.Nz != _params.Nz || v.CellSize != _params.CellSize || v.StepSeconds != _params.StepSeconds)
                throw new ArgumentException("сетка и шаг 3D-дыма огня не меняются на ходу");
            _params = v;
            Version++;
        }
    }

    /// <summary>Источник у шишки (умолчания — <see cref="Fluid3DEmitterParams.Torch"/>).</summary>
    public Fluid3DEmitterParams Emitter
    {
        get => _emitter;
        set
        {
            value.Validate();
            _emitter = value;
        }
    }

    /// <summary>Номер изменения параметров газа: сим сверяет его, а не записи целиком.</summary>
    public int Version { get; private set; }
}

/// <summary>
/// GPU-симуляция 3D-дыма одного экземпляра (<see cref="IGpuSimulation"/>, имя <c>fire.gas.N</c>): <see cref="Fluid3DSim"/> (сетка
/// 96×128×96 по 3 см в мире, окно — целыми ячейками) и <see cref="Fluid3DEmitters"/> с одной точкой — центром шишки шага (ключ — огня).
/// Шаги — по заявкам тиков кадра (<see cref="FireGasStep"/>, 60 Гц по <c>TickId</c>, те же тики, что у сима пламени). Ресурсы (≈42 МБ) — один
/// раз при создании: в кадре ни выделений, ни загрузок. Σρ и центроид — асинхронным чтением статистики шага (освобождение погасшего дыма,
/// метрика «стоит в мире»).
/// </summary>
internal sealed class FireGasSim(FireGasSlots slots, int index, FireGasTuning tuning) : IGpuSimulation
{
    /// <summary>Зерно мерцания источника.</summary>
    public const ulong Seed = 0x0F12E6A5;

    private Fluid3DSim? _sim;
    private readonly Fluid3DEmitters _emitters = new(Seed);
    private int _version = -1;
    private ulong _resetStep;
    private Fluid3DEmitterParams? _sphereFrom, _sphere;
    private float _sphereMix = float.NaN;

    /// <summary>Карты дыма из пламени экземпляра сима огня по его номеру (нет сима или карт — null).</summary>
    internal Func<int, FireGasFeed?>? Feeds;

    /// <summary>Шагов с картой из пламени (за прогон).</summary>
    public long FedSteps { get; private set; }

    public string Name { get; } = $"fire.gas.{index}";

    public FireGasSlot Slot => slots[index];

    public Fluid3DSim? Sim => _sim;

    public Fluid3DEmitters Emitters => _emitters;

    /// <summary>Кольцо шишек и центроидов шагов — метрика «дым стоит в мире».</summary>
    public FireGasDrift Drift { get; } = new();

    /// <summary>Шагов на GPU (за прогон) и шагов в последнем кадре с тиками.</summary>
    public long GpuSteps { get; private set; }
    public int LastFrameSteps { get; private set; }

    /// <summary>Статистика последнего прочитанного шага этого огня (задержка чтения — кадры); чтения не было — null.</summary>
    public Fluid3DStats? Stats { get; private set; }

    /// <summary>Хеш полей на выходе (FNV-1a 64 по битам half скорости и ρ, T) и статистика по полю тогда же; шагов не было — null.</summary>
    public ulong? FieldHash { get; internal set; }
    public Fluid3DStats? FinalStats { get; internal set; }

    /// <summary>Столб дыма по полю на выходе (<see cref="FireMetrics.SmokeColumn"/>); шагов не было — null.</summary>
    public FireRegion? FinalColumn { get; internal set; }

    /// <summary>Профиль столба дыма по высоте на выходе (<see cref="FireMetrics.SmokeProfile"/>); шагов не было — null.</summary>
    public SmokeLayer[]? FinalProfile { get; internal set; }

    /// <summary>Плотность газа на выходе (стык с пламенем считается при выходе сима пламени, он освобождается позже); шагов не было — null.</summary>
    internal SmokeGrid? FinalGrid { get; set; }

    /// <summary>Выход, до освобождения полей (хеш, итог).</summary>
    internal Action<FireGasSim>? Closing;

    public void Create(GpuSimContext context)
    {
        _sim = context.Own(new Fluid3DSim(context.Renderer, $"fire{index}", tuning.Params));
        _version = tuning.Version;
        context.Readback(_sim.StatsStorage, Fluid3DStats.Bytes);
        // Освобождается раньше сима (обратный порядок): поля ещё можно прочитать.
        context.Own(new Closer(this));
    }

    private sealed class Closer(FireGasSim sim) : IDisposable
    {
        public void Dispose() => sim.Closing?.Invoke(sim);
    }

    public void AddPasses(RenderFrame frame, in SimFrame sim)
    {
        if (_sim is null) return;
        var steps = sim.Requests<FireGasStep>();
        if (steps.Length > 0 || sim.Ticks > 0) LastFrameSteps = steps.Length;
        if (_version != tuning.Version)
        {
            _sim.Params = tuning.Params;
            _version = tuning.Version;
        }
        foreach (ref readonly var step in steps) Step(frame, step);
    }

    private void Step(RenderFrame frame, in FireGasStep step)
    {
        var sim = _sim!;
        if ((step.Flags & FireGasFlags.Reset) != 0)
        {
            sim.Reset();
            _emitters.Reset();
            Drift.Reset();
            Stats = null;
            _resetStep = step.Step;
        }
        bool emit = (step.Flags & FireGasFlags.Emit) != 0;
        var feedParams = tuning.Feed;
        float mix = feedParams.Mix;
        _emitters.Begin(step.Step, sim.Params.StepSeconds);
        if (emit && mix < 1) _emitters.Add(step.Key, step.Knob, Sphere(mix));
        _emitters.End();
        // Карта шага из пламени: сим огня записал её раньше в этом кадре (его проходы — до проходов газа).
        if (emit && mix > 0 && Feeds?.Invoke(Slot.FireSlot) is { } feed && feed.Find(frame.Number, step.Step, out var map))
        {
            sim.Step(frame, step.Target, _emitters, map.Plane, feed.Map, map.Offset);
            FedSteps++;
        }
        else sim.Step(frame, step.Target, _emitters);
        Drift.Knob(step.Step, step.Knob);
        GpuSteps++;
    }

    /// <summary>Шар у шишки с долей 1 − <paramref name="mix"/> выброса (запись — только при смене ручек: кадр — 0 Б).</summary>
    private Fluid3DEmitterParams Sphere(float mix)
    {
        var source = tuning.Emitter;
        if (!ReferenceEquals(source, _sphereFrom) || mix != _sphereMix)
        {
            _sphereFrom = source;
            _sphereMix = mix;
            _sphere = mix > 0 ? source with { Density = source.Density * (1 - mix), Temperature = source.Temperature * (1 - mix) } : source;
        }
        return _sphere!;
    }

    public void OnReadback(ReadOnlySpan<byte> data)
    {
        var stats = Fluid3DStats.Parse(data);
        // Шагов ещё не было (нули), дым свободен, статистика прежнего огня (до сброса) или тот же шаг ещё раз — пропуск.
        if (stats.Window.Nx == 0 || GpuSteps == 0 || Slot.State == FireGasState.Free || stats.Step < _resetStep) return;
        if (Stats is { } last && last.Step == stats.Step) return;
        Stats = stats;
        Drift.Centroid(stats.Step, stats.Centroid);
        slots.ReportDensity(index, stats.Step, stats.DensitySum);
    }
}
