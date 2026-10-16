using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Fluid2D;
using Sword.Engine.Render;

namespace Sword.Game.Fire;

/// <summary>Числа огня, которые ручки F3 меняют на ходу (клиентские: в реплей и <c>sword.user.json</c> не идут).</summary>
public sealed class FireTuning
{
    /// <summary>
    /// Сим огня: факел LIVE-2 без бокового сноса — постоянная скорость (0, 0.2, 0) в мировых осях (L2 — (−0.1, 0.2, 0): пламя всегда клонится
    /// вбок, а 3D-дым — нет); снос — ветер модуля (<see cref="Wind"/>), он же у газа.
    /// </summary>
    public static Fluid2DParams DefaultParams { get; } = Fluid2DParams.Torch with { DirectVelocity = new Vector3(0, 0.2f, 0) };

    /// <summary>
    /// Ветер 1 м/с в постоянной скорости 2D-сима (ед. за шаг на единицу ρ, мировые оси). Втрое больше подъёма 0.2: дым над пламенем
    /// поднимается медленнее хвоста и клонится ветром сильнее — так пламя у стыка клонится как дым (README модуля, «Ветер»).
    /// </summary>
    public const float WindTo2D = 0.6f;

    private Fluid2DParams _params = DefaultParams;

    /// <summary>Параметры сима и кисти (умолчания — <see cref="DefaultParams"/>, FSR §3.4б/§3.5, BR §7.2).</summary>
    public Fluid2DParams Params
    {
        get => _params;
        set
        {
            value.Validate();
            if (value.Resolution != _params.Resolution || value.PaintBufferDownScaleFactor != _params.PaintBufferDownScaleFactor)
                throw new ArgumentException("сторона сима огня не меняется на ходу");
            _params = value;
            Version++;
        }
    }

    /// <summary>Номер изменения параметров: сим сверяет его, а не записи целиком.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Ветер, м/с, мир (горизонталь): 2D-сим — боковая постоянная скорость ветер·<see cref="WindTo2D"/> (подъём остаётся), газ — однородная
    /// скорость воздуха (<see cref="Sword.Engine.Fluid3D.Fluid3DParams.Wind"/>, ставит модуль).
    /// </summary>
    public Vector3 Wind
    {
        get => _wind;
        set
        {
            _wind = new Vector3(value.X, 0, value.Z);
            Params = Params with { DirectVelocity = new Vector3(_wind.X * WindTo2D, Params.DirectVelocity.Y, _wind.Z * WindTo2D) };
        }
    }

    private Vector3 _wind;

    /// <summary>Нижняя граница k по умолчанию: 1 — пламя стоит в мире, так лучше в движении персонажа (L2 — 0.35).</summary>
    public const float DefaultKMin = 1f;

    /// <summary>Нижняя граница доли сдвига буферов k.</summary>
    public float KMin { get; set; } = DefaultKMin;
}

/// <summary>Что GPU-симуляции огня нужно от модуля: ручки, шумы пака, камера кадра, нужен ли выход поля в этом кадре.</summary>
internal sealed class FireGpu
{
    public required FireTuning Tuning;
    public required Func<CameraView> Camera;
    public required Func<RenderFrame, bool> WantsField;
    public Fluid2DNoise Density, Amp, BrushNoise;
    public float BrushScale;

    /// <summary>Шумы пака загружены — сим создаётся.</summary>
    public bool Ready;

    /// <summary>Дым из пламени: газ есть — карта источника после каждого шага горящего огня (<see cref="FireGasFeed"/>); null — нет газа.</summary>
    public FireGasTuning? Gas;

    /// <summary>Числа пламени модуля (видимость пламени в карте дыма).</summary>
    public Func<FlameParams> Flame = () => FlameParams.Torch4;

    /// <summary>Выход, до освобождения полей: хеш полей, снимок последнего кадра.</summary>
    public Action<FireSim>? Closing;
}

/// <summary>
/// GPU-симуляция одного экземпляра огня (<see cref="IGpuSimulation"/>, имя <c>fire.N</c>): <see cref="Fluid2DSim"/> 368² факела и
/// <see cref="Fluid2DBrush"/> с одной точкой — центром шишки. Шаги — по заявкам тиков кадра (<see cref="FireStep"/>, 60 Гц по <c>TickId</c>):
/// площадка к камере кадра, центр — шишка на тике шага; сдвиг буферов k·R⁻¹·Δ/E (FSR §3.6), шумы в uv + WorldOffset; кадр без тиков — шагов
/// нет, поле стоит. Выход поля (<see cref="Output"/>) — когда он нужен кадру (вид поля). Σρ, max|v|, NaN, центроид — асинхронным чтением
/// статистики шага. Ресурсы — один раз при создании (поля ≈4.3 МБ): в кадре ни выделений, ни загрузок; освобождённый экземпляр — просто
/// без шагов.
/// </summary>
internal sealed class FireSim(FireSlots slots, int index, FireGpu gpu) : IGpuSimulation
{
    /// <summary>Зерно джиттера кисти (тот же джиттер у всех экземпляров при том же <c>TickId</c>).</summary>
    public const ulong Seed = 0x0F12E5EED;

    private Fluid2DSim? _sim;
    private Fluid2DBrush? _brush;
    private int _version = -1;
    private Vector3 _simPos;
    private Vector2 _worldOffset;
    private ulong _outputFrame = ulong.MaxValue;

    public string Name { get; } = $"fire.{index}";

    private Action<FireSim>? Closing => gpu.Closing;

    public FireSlot Slot => slots[index];

    public Fluid2DSim? Sim => _sim;
    public Fluid2DBrush? Brush => _brush;

    /// <summary>Карты дыма из пламени (кольцо кадра); газа нет — null.</summary>
    public FireGasFeed? Feed { get; private set; }

    /// <summary>Последний шаг: доля сдвига k, сдвиг площадки за шаг Δ (м) и в UV (WOD), накопленный WorldOffset, поворот площадки.</summary>
    public float K { get; private set; } = 1;
    public Vector3 Delta { get; private set; }
    public Vector2 Wod { get; private set; }
    public Vector2 WorldOffset => _worldOffset;
    public Quaternion Rotation { get; private set; } = Quaternion.Identity;

    /// <summary>Центр площадки последнего шага (центр шишки шага).</summary>
    public Vector3 Center => _simPos;

    /// <summary>Шагов на GPU (за прогон) и шагов в последнем кадре с тиками.</summary>
    public long GpuSteps { get; private set; }
    public int LastFrameSteps { get; private set; }

    /// <summary>Статистика последнего прочитанного шага (задержка чтения — кадры); чтения не было — null.</summary>
    public Fluid2DStats? Stats { get; private set; }

    /// <summary>Выход поля этого кадра (RGBA16F N²: v, ρ, wet) — только в кадре, где он нужен (<see cref="HasOutput"/>).</summary>
    public RenderTexture Output { get; private set; }

    public bool HasOutput(RenderFrame frame) => _outputFrame == frame.Number;

    /// <summary>Хеш полей и кисти на выходе (FNV-1a 64 по битам half) и статистика по полю тогда же; шагов не было — null.</summary>
    public ulong? FieldHash { get; internal set; }
    public Fluid2DStats? FinalStats { get; internal set; }

    /// <summary>Размеры пламени, ядра и столба дыма по полю на выходе (<see cref="FireMetrics"/>); шагов не было — null.</summary>
    public FireMetrics? FinalMetrics { get; internal set; }

    /// <summary>Стык пламени и дыма по полям на выходе (<see cref="FireMetrics.Joint"/>); газа или шагов не было — null.</summary>
    public FireJoint? FinalJoint { get; internal set; }

    public void Create(GpuSimContext context)
    {
        if (!gpu.Ready) return;
        var p = gpu.Tuning.Params;
        _sim = context.Own(new Fluid2DSim(context.Renderer, Name, p, gpu.Density, gpu.Amp, gpu.BrushNoise));
        _brush = new Fluid2DBrush(p, Seed);
        _version = gpu.Tuning.Version;
        context.Readback(_sim.StatsStorage, Fluid2DStats.Bytes);
        if (gpu.Gas is not null) Feed = context.Own(new FireGasFeed(context.Renderer, Name, gpu.Gas.Feed.Side));
        // Освобождается раньше сима (обратный порядок): поля ещё можно прочитать.
        context.Own(new Closer(this));
    }

    private sealed class Closer(FireSim sim) : IDisposable
    {
        public void Dispose() => sim.Closing?.Invoke(sim);
    }

    public void AddPasses(RenderFrame frame, in SimFrame sim)
    {
        if (_sim is null || _brush is null) return;
        var steps = sim.Requests<FireStep>();
        if (steps.Length > 0 || sim.Ticks > 0) LastFrameSteps = steps.Length;
        if (_version != gpu.Tuning.Version)
        {
            _sim.Params = _brush.Params = gpu.Tuning.Params;
            _version = gpu.Tuning.Version;
        }
        if (steps.Length > 0)
        {
            // Площадка к глазу кадра с мировой вертикалью (L2 — к камере, ComposeRotators(Rot(0, 90, 90), CameraRotation)): поворот поле
            // не пересэмплирует. Глаз следящей камеры зависит от доли тика кадра: поле повторяется при том же темпе кадров.
            var camera = gpu.Camera();
            foreach (ref readonly var step in steps)
            {
                Rotation = FireArea.Plane(camera.Rotation, camera.Eye, step.Center);
                Step(frame, step);
            }
        }
        if (Slot.State != FireState.Free && gpu.WantsField(frame))
        {
            Output = _sim.Export(frame);
            _outputFrame = frame.Number;
        }
    }

    private void Step(RenderFrame frame, in FireStep step)
    {
        var sim = _sim!;
        var brush = _brush!;
        var p = sim.Params;
        if ((step.Flags & FireStepFlags.Reset) != 0)
        {
            sim.Reset();
            brush.Reset();
            _simPos = step.Center;
            _worldOffset = Vector2.Zero;
        }
        var delta = step.Center - _simPos;
        float k = FireArea.Follow(delta, p.Extents, p.Resolution, gpu.Tuning.KMin);
        var wod = FireArea.OffsetDelta(k, Rotation, delta, p.Extents);
        _worldOffset += wod;
        _simPos = step.Center;
        K = k;
        Delta = delta;
        Wod = wod;
        var input = new Fluid2DStepInput(step.Step, _worldOffset, wod, Rotation);
        // Точка одна и в центре площадки: перспективный перенос на глубину центра её не двигает — камера кисти не нужна.
        brush.Begin(input, step.Tick, step.Center);
        if ((step.Flags & FireStepFlags.Burning) != 0)
            brush.Add(new Fluid2DBrushKey(Fluid2DBrushSource.Array, (uint)Slot.Key, 0), step.Center, gpu.BrushScale);
        brush.End();
        sim.Step(frame, input, brush);
        if (Feed is not null && gpu.Gas is { } gas && (step.Flags & FireStepFlags.Burning) != 0)
        {
            var feed = gas.Feed;
            Feed.Add(frame, step.Step, sim.Composite, step.Center, Rotation,
                FireFeedConstants.Build(p, p.Extents, Rotation, gpu.Flame(), feed, gas.Params.StepSeconds, FireMetricsParams.Default.FlameK, 0), feed);
        }
        GpuSteps++;
    }

    public void OnReadback(ReadOnlySpan<byte> data)
    {
        var stats = Fluid2DStats.Parse(data);
        if (stats.Resolution == 0) return;   // шагов ещё не было
        Stats = stats;
        slots.ReportDensity(index, stats.Step, stats.DensitySum);
    }
}
