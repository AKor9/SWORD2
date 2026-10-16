using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;

namespace Sword.Game.Fire;

/// <summary>Состояние экземпляра 3D-дыма.</summary>
public enum FireGasState : byte
{
    /// <summary>Свободен: шагов нет.</summary>
    Free,

    /// <summary>Огонь горит: источник у центра шишки, окно едет за шишкой.</summary>
    Emitting,

    /// <summary>Огонь погас: источника нет, окно стоит, дым поднимается и рассеивается до освобождения.</summary>
    Fading,
}

/// <summary>Флаги заявки шага газа.</summary>
[Flags]
public enum FireGasFlags : uint
{
    None = 0,

    /// <summary>Источник у шишки шага.</summary>
    Emit = 1,

    /// <summary>Перед шагом — сброс поля и источников (новый огонь).</summary>
    Reset = 2,
}

/// <summary>Заявка шага газа (наблюдатель тиков → GPU-симуляция газа): тик и номер шага, ключ огня, центр шишки, цель окна, флаги.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FireGasStep
{
    public ulong Tick, Step, Key;
    public Vector3 Knob;
    public Vector3 Target;
    public FireGasFlags Flags;
}

/// <summary>Экземпляр 3D-дыма глазами CPU: чей огонь, состояние, шишка, сглаженная скорость, цель окна, шаги и угасание.</summary>
public sealed class FireGasSlot(int index)
{
    public int Index { get; } = index;
    public FireGasState State { get; internal set; }

    /// <summary>Огонь (как у <see cref="FireSlot"/>): ключ бойца, оружие, тик начала; экземпляр сима огня, от которого он заведён.</summary>
    public ulong Key { get; internal set; }
    public int Owner { get; internal set; }
    public int Weapon { get; internal set; }
    public string WeaponName { get; internal set; } = "";
    public ulong FirstTick { get; internal set; }
    public int FireSlot { get; internal set; }

    /// <summary>Центр шишки последнего тика горящего огня (источник); погас — последний, где горел.</summary>
    public Vector3 Knob { get; internal set; }

    /// <summary>Сглаженная горизонтальная скорость шишки по шагам, м/с (направление хода — итогу: место позади и впереди).</summary>
    public Vector3 Velocity { get; internal set; }

    /// <summary>Центр окна по горизонтали (y — 0): догоняет шишку (<see cref="FireGasSlots.Follow"/>).</summary>
    public Vector3 Follow { get; internal set; }

    /// <summary>Цель окна последнего шага (<see cref="FireGasSlots.WindowTarget"/>); погас — стоит.</summary>
    public Vector3 Target { get; internal set; }

    /// <summary>Центр шишки, тик, номер и флаги последнего шага.</summary>
    public Vector3 StepKnob { get; internal set; }
    public ulong StepTick { get; internal set; }
    public ulong Step { get; internal set; }
    public FireGasFlags StepFlags { get; internal set; }

    /// <summary>Шагов с начала огня, шагов угасания и номер шага, с которого гаснет.</summary>
    public long Steps { get; internal set; }
    public long FadeSteps { get; internal set; }
    public ulong FadeFrom { get; internal set; }

    /// <summary>Σρ последнего прочитанного шага (чтение GPU с задержкой) и его номер; чтения ещё нет — null.</summary>
    public float? DensitySum { get; internal set; }
    public ulong DensityStep { get; internal set; }

    internal bool Matched, PendingReset, HasStepKnob;
    internal SimRequests? Requests;
}

/// <summary>
/// Экземпляры 3D-дыма (без GPU — проверяется тестами отдельно): после тика и экземпляров сима огня (<see cref="FireSlots"/>) — горящему огню
/// экземпляр газа (свободный со сбросом; нет — самый давно гаснущий; нет и его — <see cref="OverLimit"/>), на тиках шага (60 Гц по
/// <c>TickId</c>, те же, что у сима пламени) — заявки: источник у центра шишки, цель окна — на <see cref="FireSettings.GasUp"/> выше шишки,
/// по горизонтали — центр, догоняющий шишку (<see cref="Follow"/>, <see cref="WindowTarget"/>). Огонь погас (оружие выпало или сменили, экземпляр сима огня гаснет или
/// отнят) — источник выключен, окно стоит, сим шагает, пока Σρ не ниже <see cref="FireSettings.GasFadeDensity"/> (не дольше
/// <see cref="FireSettings.GasFadeSeconds"/>), затем экземпляр свободен. <see cref="Enabled"/> false — все свободны, заявок нет. Тик — 0 Б.
/// </summary>
public sealed class FireGasSlots
{
    /// <summary>Скачок шишки быстрее — телепорт (как у источников газа): центр окна — на шишке, скорость — 0.</summary>
    public const float JumpSpeed = 40f;

    /// <summary>Постоянная сглаживания скорости шишки (только итогу: направление хода), с.</summary>
    public const float VelocitySmoothing = 0.3f;

    private readonly FireSettings _settings;
    private readonly FireSlots _fires;
    private readonly FireGasSlot[] _slots;
    private readonly int[] _pending;
    private readonly int _tickRate;
    private readonly long _fadeSteps;
    private bool _enabled = true;

    public FireGasSlots(FireSettings settings, FireSlots fires, float tickDt)
    {
        settings.Validate();
        _settings = settings;
        _fires = fires;
        _slots = new FireGasSlot[settings.MaxFires];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new FireGasSlot(i);
        _pending = new int[settings.MaxFires];
        _tickRate = FireArea.TickRate(tickDt);
        _fadeSteps = Math.Max(1, (long)Math.Round(settings.GasFadeSeconds * FireArea.StepRate));
    }

    public ReadOnlySpan<FireGasSlot> Slots => _slots;

    public FireGasSlot this[int slot] => _slots[slot];

    public FireSettings Settings => _settings;

    /// <summary>Шагов угасания не больше.</summary>
    public long FadeSteps => _fadeSteps;

    /// <summary>3D-дым включён (ручка F3): выключили — экземпляры свободны сразу, горящему огню новый экземпляр — когда включат снова.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value) return;
            foreach (var s in _slots)
                if (s.State != FireGasState.Free)
                {
                    s.State = FireGasState.Free;
                    ReleasedBySwitch++;
                }
        }
    }

    /// <summary>Горящих огней без экземпляра (по тикам); отнято у гаснущих; освобождено по Σρ, по времени, ручкой.</summary>
    public long OverLimit { get; private set; }
    public long Stolen { get; private set; }
    public long ReleasedByDensity { get; private set; }
    public long ReleasedByTime { get; private set; }
    public long ReleasedBySwitch { get; private set; }

    /// <summary>Заявок шага положено (за прогон).</summary>
    public long StepRequests { get; private set; }

    /// <summary>Заявки экземпляра <paramref name="slot"/> — в его GPU-симуляцию.</summary>
    public void Bind(int slot, SimRequests requests) => _slots[slot].Requests = requests;

    /// <summary>Статистика поля экземпляра (чтение GPU): по Σρ погасший дым освобождается раньше срока.</summary>
    public void ReportDensity(int slot, ulong step, float densitySum)
    {
        var s = _slots[slot];
        s.DensitySum = densitySum;
        s.DensityStep = step;
    }

    /// <summary>Цель окна: по горизонтали — центр <paramref name="follow"/>, по высоте — шишка + <see cref="FireSettings.GasUp"/> (шишка в нижней части окна).</summary>
    public static Vector3 WindowTarget(Vector3 knob, Vector3 follow, FireSettings s) => new(follow.X, knob.Y + s.GasUp, follow.Z);

    /// <summary>
    /// Центр окна по горизонтали за шаг <paramref name="dt"/>: f + (шишка − f)·min(1, dt/τ), τ — <see cref="FireSettings.GasFollowSeconds"/>;
    /// шишка дальше <see cref="FireSettings.GasMaxLead"/> — центр на этом расстоянии позади неё. На бегу v — отставание v·(τ − dt), при вращении
    /// с угловой скоростью ω по кругу r — центр в ≈r/√(1 + (ωτ)²) от оси.
    /// </summary>
    public static Vector3 Follow(Vector3 follow, Vector3 knob, float dt, FireSettings s)
    {
        var k = new Vector3(knob.X, 0, knob.Z);
        var f = new Vector3(follow.X, 0, follow.Z);
        f += (k - f) * MathF.Min(1f, dt / s.GasFollowSeconds);
        var lag = f - k;
        float length = lag.Length();
        return length > s.GasMaxLead ? k + lag * (s.GasMaxLead / length) : f;
    }

    /// <summary>Сглаженная скорость шага: v + (сырая − v)·min(1, dt/τ); сырая — горизонтальная Δ/dt, скачок быстрее <see cref="JumpSpeed"/> — прежняя.</summary>
    public static Vector3 SmoothVelocity(Vector3 velocity, Vector3 from, Vector3 to, float dt, float tau)
    {
        var raw = new Vector3(to.X - from.X, 0, to.Z - from.Z) / dt;
        if (!(raw.Length() <= JumpSpeed)) return velocity;
        return velocity + (raw - velocity) * MathF.Min(1f, dt / tau);
    }

    /// <summary>После тика <paramref name="tick"/> и экземпляров сима огня этого тика: экземпляры газа и заявки шага.</summary>
    public void Observe(ulong tick)
    {
        if (!_enabled) return;
        foreach (var s in _slots) s.Matched = false;
        int pending = 0;
        foreach (var f in _fires.Slots)
        {
            if (f.State != FireState.Burning) continue;
            var gas = Find(f);
            if (gas is null)
            {
                if (pending < _pending.Length) _pending[pending++] = f.Index;
                continue;
            }
            gas.Matched = true;
            gas.Knob = f.Center;
        }
        foreach (var s in _slots)
            if (s.State == FireGasState.Emitting && !s.Matched) StartFade(s, tick);
        for (int p = 0; p < pending; p++)
        {
            var f = _fires[_pending[p]];
            var gas = Allocate();
            if (gas is null)
            {
                OverLimit++;
                continue;
            }
            Start(gas, f);
            gas.Matched = true;
            gas.Knob = f.Center;
        }
        if (FireArea.IsStep(tick, _tickRate)) StepAll(tick, FireArea.StepOf(tick, _tickRate));
    }

    private FireGasSlot? Find(FireSlot f)
    {
        foreach (var s in _slots)
            if (s.State == FireGasState.Emitting && s.Key == f.Key && s.Weapon == f.Weapon && s.FirstTick == f.FirstTick) return s;
        return null;
    }

    /// <summary>Свободный экземпляр, иначе самый давно гаснущий (при равенстве — меньший номер), иначе null.</summary>
    private FireGasSlot? Allocate()
    {
        FireGasSlot? oldest = null;
        foreach (var s in _slots)
        {
            if (s.State == FireGasState.Free) return s;
            if (s.State == FireGasState.Fading && (oldest is null || s.FadeSteps > oldest.FadeSteps)) oldest = s;
        }
        if (oldest is not null) Stolen++;
        return oldest;
    }

    /// <summary>Новый огонь в экземпляре — подпись строится здесь (редко), а не в кадре.</summary>
    private static void Start(FireGasSlot s, FireSlot f)
    {
        s.State = FireGasState.Emitting;
        s.Key = f.Key;
        s.Owner = f.Owner;
        s.Weapon = f.Weapon;
        s.WeaponName = f.WeaponName;
        s.FirstTick = f.FirstTick;
        s.FireSlot = f.Index;
        s.Steps = s.FadeSteps = 0;
        s.FadeFrom = 0;
        s.DensitySum = null;
        s.Velocity = Vector3.Zero;
        s.HasStepKnob = false;
        s.PendingReset = true;
    }

    private void StartFade(FireGasSlot s, ulong tick)
    {
        s.State = FireGasState.Fading;
        s.FadeSteps = 0;
        s.FadeFrom = FireArea.StepOf(tick, _tickRate);
    }

    private void StepAll(ulong tick, ulong step)
    {
        foreach (var s in _slots)
        {
            if (s.State == FireGasState.Free) continue;
            if (s.State == FireGasState.Fading)
            {
                bool faded = s.DensitySum is { } sum && s.DensityStep > s.FadeFrom && sum < _settings.GasFadeDensity;
                if (s.FadeSteps >= _fadeSteps || faded)
                {
                    if (s.FadeSteps >= _fadeSteps) ReleasedByTime++;
                    else ReleasedByDensity++;
                    s.State = FireGasState.Free;
                    continue;
                }
            }
            bool emitting = s.State == FireGasState.Emitting;
            if (emitting)
            {
                // Центр и скорость — по шишкам шагов (не тиков): тот же ряд при любом разбиении тиков по кадрам. Новый огонь или скачок
                // (телепорт) — центр на шишке.
                const float dt = 1f / FireArea.StepRate;
                bool follows = s.HasStepKnob && !s.PendingReset && Vector3.Distance(s.Knob, s.StepKnob) <= JumpSpeed * dt;
                s.Velocity = follows ? SmoothVelocity(s.Velocity, s.StepKnob, s.Knob, dt, VelocitySmoothing) : Vector3.Zero;
                s.Follow = follows ? Follow(s.Follow, s.Knob, dt, _settings) : new Vector3(s.Knob.X, 0, s.Knob.Z);
                s.Target = WindowTarget(s.Knob, s.Follow, _settings);
                s.StepKnob = s.Knob;
                s.HasStepKnob = true;
            }
            var flags = (emitting ? FireGasFlags.Emit : 0) | (s.PendingReset ? FireGasFlags.Reset : 0);
            s.Requests?.Add(tick, new FireGasStep { Tick = tick, Step = step, Key = s.Key, Knob = s.Knob, Target = s.Target, Flags = flags });
            StepRequests++;
            s.PendingReset = false;
            s.StepTick = tick;
            s.Step = step;
            s.StepFlags = flags;
            s.Steps++;
            if (!emitting) s.FadeSteps++;
        }
    }
}

/// <summary>
/// Метрика «дым стоит в мире» за окно шагов: путь и сдвиг шишки по горизонтали (по каждому шагу) против пути и сдвига центроида ρ газа в мире
/// (по прочитанной статистике шагов), м; скорости — на секунду окна. Дым, который стоит в мире, при вращении шишки на месте почти не
/// двигает центроид (отношение путей ≪ 1); слой, привязанный к шишке, двигал бы его вместе с ней (отношение ≈ 1).
/// </summary>
public readonly record struct FireGasDriftStats(ulong FromStep, ulong ToStep, float Seconds, float KnobPath, float KnobShift, float CentroidPath,
    float CentroidShift, int Samples)
{
    public float KnobSpeed => Seconds > 0 ? KnobPath / Seconds : 0;
    public float CentroidSpeed => Seconds > 0 ? CentroidPath / Seconds : 0;

    /// <summary>Путь центроида / путь шишки (шишка почти стоит — 0).</summary>
    public float Ratio => KnobPath > 0.05f ? CentroidPath / KnobPath : 0;
}

/// <summary>
/// Кольцо шагов газа для <see cref="FireGasDriftStats"/>: шишка каждого шага (CPU) и центроид ρ шага, если его статистика прочитана (чтение
/// GPU раз в кадр — шаги без чтения пропускаются). Сброс — новый огонь. 0 Б на шаг.
/// </summary>
public sealed class FireGasDrift
{
    public const int Capacity = 256;

    private readonly ulong[] _steps = new ulong[Capacity];
    private readonly Vector3[] _knobs = new Vector3[Capacity];
    private readonly Vector3[] _centroids = new Vector3[Capacity];
    private readonly bool[] _hasCentroid = new bool[Capacity];

    public FireGasDrift() => Reset();

    public void Reset()
    {
        Array.Fill(_steps, ulong.MaxValue);
        Array.Clear(_hasCentroid);
    }

    /// <summary>Шишка шага <paramref name="step"/>.</summary>
    public void Knob(ulong step, Vector3 knob)
    {
        int i = (int)(step % Capacity);
        _steps[i] = step;
        _knobs[i] = knob;
        _hasCentroid[i] = false;
    }

    /// <summary>Центроид ρ шага <paramref name="step"/> (статистика); шага нет в кольце — пропуск.</summary>
    public void Centroid(ulong step, Vector3 centroid)
    {
        int i = (int)(step % Capacity);
        if (_steps[i] != step) return;
        _centroids[i] = centroid;
        _hasCentroid[i] = true;
    }

    /// <summary>
    /// Метрика за последние <paramref name="window"/> шагов до последнего шага с центроидом: от самого раннего шага окна с центроидом; меньше
    /// двух таких шагов — null.
    /// </summary>
    public FireGasDriftStats? Compute(int window = 60)
    {
        window = Math.Clamp(window, 1, Capacity - 1);
        ulong last = ulong.MaxValue;
        for (int i = 0; i < Capacity; i++)
            if (_hasCentroid[i] && _steps[i] != ulong.MaxValue && (last == ulong.MaxValue || _steps[i] > last)) last = _steps[i];
        if (last == ulong.MaxValue) return null;
        ulong from = last;
        for (ulong s = last; s + (ulong)window >= last && s != ulong.MaxValue; s--)
        {
            int i = (int)(s % Capacity);
            if (_steps[i] != s) break;
            if (_hasCentroid[i]) from = s;
            if (s == 0) break;
        }
        if (from == last) return null;
        float knobPath = 0, centroidPath = 0;
        int samples = 1;
        var knob = _knobs[(int)(from % Capacity)];
        var centroid = _centroids[(int)(from % Capacity)];
        var firstKnob = knob;
        var firstCentroid = centroid;
        for (ulong s = from + 1; s <= last; s++)
        {
            int i = (int)(s % Capacity);
            knobPath += Horizontal(_knobs[i] - knob);
            knob = _knobs[i];
            if (!_hasCentroid[i]) continue;
            centroidPath += Horizontal(_centroids[i] - centroid);
            centroid = _centroids[i];
            samples++;
        }
        return new FireGasDriftStats(from, last, (last - from) / (float)FireArea.StepRate, knobPath, Horizontal(knob - firstKnob), centroidPath,
            Horizontal(centroid - firstCentroid), samples);
    }

    private static float Horizontal(Vector3 d) => MathF.Sqrt(d.X * d.X + d.Z * d.Z);
}
