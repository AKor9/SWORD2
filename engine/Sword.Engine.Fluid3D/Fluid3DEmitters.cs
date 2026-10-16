using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// Настройки источника по траектории (README, «Источники»). Умолчания <see cref="Torch"/> — прикидка для огня факела (шишка ≈5 см, газ
/// горячий — поднимается); вид подгоняется у потребителя.
/// </summary>
public sealed record Fluid3DEmitterParams
{
    public static Fluid3DEmitterParams Torch { get; } = new();

    /// <summary>Радиус капсулы, м.</summary>
    public float Radius { get; init; } = 0.05f;

    /// <summary>Ширина сглаженного края, м; не больше ячейки — одна ячейка.</summary>
    public float Edge { get; init; }

    /// <summary>Плотность в секунду: ячейка внутри неподвижного источника получает Density·dt за шаг.</summary>
    public float Density { get; init; } = 5f;

    /// <summary>Температура в секунду (плавучесть — β·T).</summary>
    public float Temperature { get; init; } = 10f;

    /// <summary>Скорость газа у неподвижного источника, мир, м/с.</summary>
    public Vector3 Velocity { get; init; } = new(0, 0.5f, 0);

    /// <summary>Унос скоростью источника: скорость газа в капсуле тянется к <see cref="Velocity"/> + VelocityScale·V, V = Δp/dt.</summary>
    public float VelocityScale { get; init; } = 0.5f;

    /// <summary>Как быстро скорость газа в капсуле подтягивается к цели, 1/с (за шаг — min(1, rate·dt)).</summary>
    public float VelocityRate { get; init; } = 30f;

    /// <summary>Глубина мерцания: ρ и T шага умножаются на 1 − Flicker·u, u ∈ [0, 1) — шум по шагам (хеш зерна, ключа и узла).</summary>
    public float Flicker { get; init; } = 0.5f;

    /// <summary>Шагов между узлами шума мерцания (между узлами — гладко): 1 — новое u каждый шаг; 6 при 60 Гц — ≈10 Гц.</summary>
    public int FlickerSteps { get; init; } = 6;

    /// <summary>
    /// Раскачка, м/с: к горизонтальной скорости газа в капсуле — (2u − 1)·Sway по x и по z, u — свои шумы тех же узлов, что у мерцания,
    /// через <see cref="SwaySteps"/> шагов. Подъёмный столб от неподвижного источника иначе ламинарен и прям; раскачка у источника
    /// поднимается волной — S-завитки. 0 — без раскачки.
    /// </summary>
    public float Sway { get; init; }

    /// <summary>Шагов между узлами шума раскачки: с подъёмом ≈1 м/с период 2·SwaySteps шагов — длина волны столба.</summary>
    public int SwaySteps { get; init; } = 10;

    /// <summary>
    /// Выброс на отрезке: 1 — за шаг заметённая часть капсулы получает столько же, сколько неподвижный шар (ρ и T × min(1, 4r/3L); выброс
    /// в секунду не зависит от скорости, длинный след — тоньше); 0 — заметённое получает полный впрыск шага (взмах выбрасывает больше).
    /// </summary>
    public float TrailConservation { get; init; } = 1f;

    /// <summary>Скачок быстрее — телепорт, м/с: шаг без отрезка и без скорости.</summary>
    public float JumpSpeed { get; init; } = 40f;

    public void Validate()
    {
        if (!(Radius > 0) || !float.IsFinite(Radius)) throw new ArgumentOutOfRangeException(nameof(Radius), Radius, "радиус > 0");
        if (!(Edge >= 0) || !float.IsFinite(Edge)) throw new ArgumentOutOfRangeException(nameof(Edge), Edge, "край ≥ 0");
        if (!float.IsFinite(Density) || !float.IsFinite(Temperature)) throw new ArgumentOutOfRangeException(nameof(Density), "впрыск — конечные числа");
        if (!float.IsFinite(Velocity.Length()) || !float.IsFinite(VelocityScale))
            throw new ArgumentOutOfRangeException(nameof(Velocity), "скорость — конечные числа");
        if (!(VelocityRate >= 0) || !float.IsFinite(VelocityRate)) throw new ArgumentOutOfRangeException(nameof(VelocityRate), VelocityRate, "≥ 0");
        if (!(Flicker is >= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(Flicker), Flicker, "мерцание в [0, 1]");
        if (FlickerSteps < 1) throw new ArgumentOutOfRangeException(nameof(FlickerSteps), FlickerSteps, "шагов ≥ 1");
        if (!(Sway >= 0) || !float.IsFinite(Sway)) throw new ArgumentOutOfRangeException(nameof(Sway), Sway, "раскачка ≥ 0");
        if (SwaySteps < 1) throw new ArgumentOutOfRangeException(nameof(SwaySteps), SwaySteps, "шагов ≥ 1");
        if (!(TrailConservation is >= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(TrailConservation), TrailConservation, "в [0, 1]");
        if (!(JumpSpeed > 0)) throw new ArgumentOutOfRangeException(nameof(JumpSpeed), JumpSpeed, "> 0");
    }
}

/// <summary>
/// Источник шага для отладки: ключ, позиция, начало отрезка (null — шар), скорость V = Δp/dt, множитель мерцания и выброса на отрезке
/// (итог — множитель ρ и T без интенсивности), скачок (телепорт).
/// </summary>
public readonly record struct Fluid3DEmitterInfo(ulong Key, Vector3 Position, Vector3? From, Vector3 Velocity, float Flicker, float Trail,
    bool Jump);

/// <summary>Итог шага источников: добавлено, в шаге (≤ <see cref="Fluid3DSim.MaxSources"/>, меньшие ключи), отброшено, с отрезком.</summary>
public readonly record struct Fluid3DEmitterStats(ulong Step, int Added, int Kept, int Dropped, int Segments);

/// <summary>
/// Источники по траектории: шаг — <see cref="Begin"/> → <see cref="Add"/> ×n → <see cref="End"/> → <see cref="Fluid3DSim.Step(Sword.Engine.Render.RenderFrame, Vector3, Fluid3DEmitters)"/>
/// (или <see cref="Sources"/> в <see cref="Fluid3DReference.Step"/>). Прошлая позиция — по ключу: ключ прошлого шага даёт капсулу от прошлой
/// позиции к новой (быстрый взмах — непрерывный след) и скорость V = Δp/dt; новый ключ (не было в прошлом шаге, <see cref="Forget"/>,
/// скачок быстрее <see cref="Fluid3DEmitterParams.JumpSpeed"/>) — шар, V = 0; ключа нет в шаге — источника нет. Порядок — по ключу, сверх
/// <see cref="Fluid3DSim.MaxSources"/> остаются меньшие ключи. Мерцание — хеш (зерно, ключ, узел шага): те же зерно и шаги — те же числа бит
/// в бит. Кадр — 0 Б после прогрева.
/// </summary>
public sealed class Fluid3DEmitters
{
    private struct KeyState
    {
        public Vector3 Position;
        public ulong Step;
        public bool Forgotten;
    }

    private struct Pending
    {
        public Vector3 Position;
        public Fluid3DEmitterParams Params;
        public float Intensity;
    }

    private readonly Dictionary<ulong, KeyState> _states = new(2 * Fluid3DSim.MaxSources);
    private ulong[] _keys = new ulong[2 * Fluid3DSim.MaxSources];
    private int[] _order = new int[2 * Fluid3DSim.MaxSources];
    private Pending[] _pending = new Pending[2 * Fluid3DSim.MaxSources];
    private ulong[] _stale = new ulong[2 * Fluid3DSim.MaxSources];
    private readonly Fluid3DSource[] _sources = new Fluid3DSource[Fluid3DSim.MaxSources];
    private readonly Fluid3DEmitterInfo[] _infos = new Fluid3DEmitterInfo[Fluid3DSim.MaxSources];
    private int _count, _pendingCount;
    private bool _open;
    private float _dt;

    public Fluid3DEmitters(ulong seed) => Seed = seed;

    /// <summary>Зерно мерцания: другое зерно — другое мерцание.</summary>
    public ulong Seed { get; }

    /// <summary>Шаг готов (<see cref="End"/> после <see cref="Begin"/>).</summary>
    public bool Ready { get; private set; }

    /// <summary>Номер шага (<see cref="Begin"/>).</summary>
    public ulong Step { get; private set; }

    public Fluid3DEmitterStats Stats { get; private set; }

    /// <summary>Источники шага в порядке ключа — вход <see cref="Fluid3DSim.Step(Sword.Engine.Render.RenderFrame, ulong, Vector3, ReadOnlySpan{Fluid3DSource})"/>.</summary>
    public ReadOnlySpan<Fluid3DSource> Sources => _sources.AsSpan(0, _count);

    /// <summary>Источники шага для отладки, в том же порядке.</summary>
    public ReadOnlySpan<Fluid3DEmitterInfo> Points => _infos.AsSpan(0, _count);

    /// <summary>Забыть все ключи (перезапуск эффекта): следующие точки — новые.</summary>
    public void Reset()
    {
        _states.Clear();
        _open = false;
        Ready = false;
        _count = 0;
    }

    /// <summary>Ключ пересоздан: следующий <see cref="Add"/> — новая точка, без отрезка и скорости.</summary>
    public void Forget(ulong key)
    {
        ref var s = ref CollectionsMarshal.GetValueRefOrNullRef(_states, key);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref s)) s.Forgotten = true;
    }

    /// <summary>Начало шага <paramref name="step"/> длиной <paramref name="stepSeconds"/>, с (V = Δp/dt; отрезок — только от шага step − 1).</summary>
    public void Begin(ulong step, float stepSeconds)
    {
        if (_open) throw new InvalidOperationException("источники: Begin без End");
        if (!(stepSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "время шага > 0");
        _open = true;
        Ready = false;
        Step = step;
        _dt = stepSeconds;
        _pendingCount = 0;
    }

    /// <summary>Точка шага: ключ (один раз за шаг), мировая позиция, м, настройки и интенсивность (множитель ρ и T, огонь разгорается и гаснет).</summary>
    public void Add(ulong key, Vector3 position, Fluid3DEmitterParams parameters, float intensity = 1)
    {
        if (!_open) throw new InvalidOperationException("источники: Add вне Begin/End");
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        if (!float.IsFinite(position.X + position.Y + position.Z)) throw new ArgumentOutOfRangeException(nameof(position), position, "позиция — конечные числа");
        if (!(intensity >= 0) || !float.IsFinite(intensity)) throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "≥ 0");
        if (_pendingCount == _pending.Length)
        {
            Array.Resize(ref _pending, 2 * _pending.Length);
            Array.Resize(ref _keys, 2 * _keys.Length);
            Array.Resize(ref _order, 2 * _order.Length);
        }
        _keys[_pendingCount] = key;
        _order[_pendingCount] = _pendingCount;
        _pending[_pendingCount++] = new Pending { Position = position, Params = parameters, Intensity = intensity };
    }

    /// <summary>Конец шага: сортировка по ключу, отрезки и скорости по прошлым позициям, мерцание; состояние ключей, которых нет, — забыто.</summary>
    public void End()
    {
        if (!_open) throw new InvalidOperationException("источники: End без Begin");
        _open = false;
        int n = _pendingCount;
        _keys.AsSpan(0, n).Sort(_order.AsSpan(0, n));
        for (int i = 1; i < n; i++)
            if (_keys[i] == _keys[i - 1]) throw new InvalidOperationException($"источники: ключ {_keys[i]} дважды за шаг");
        _count = 0;
        int segments = 0;
        for (int i = 0; i < n; i++)
            if (Prepare(i) && _sources[_count - 1].From is not null) segments++;
        PruneStates();
        Ready = true;
        Stats = new Fluid3DEmitterStats(Step, n, _count, n - _count, segments);
    }

    /// <summary>Источник точки i (в порядке ключа); true — точка попала в шаг.</summary>
    private bool Prepare(int i)
    {
        ulong key = _keys[i];
        ref readonly var a = ref _pending[_order[i]];
        var p = a.Params;
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_states, key, out bool exists);
        bool continuous = exists && !state.Forgotten && state.Step + 1 == Step;
        var from = state.Position;
        state = new KeyState { Position = a.Position, Step = Step };
        if (_count == Fluid3DSim.MaxSources) return false;

        var delta = continuous ? a.Position - from : Vector3.Zero;
        var velocity = delta / _dt;
        bool jump = continuous && !(velocity.Length() <= p.JumpSpeed);
        if (jump)
        {
            continuous = false;
            delta = velocity = Vector3.Zero;
        }
        float length = delta.Length();
        bool segment = continuous && length > 0;
        // Объём шара к заметённому за шаг цилиндру: (4/3)πr³ / πr²L.
        float trail = segment ? 1f + (MathF.Min(1f, 4f * p.Radius / (3f * length)) - 1f) * p.TrailConservation : 1f;
        float flicker = 1f - p.Flicker * Noise(Seed, key, Step, p.FlickerSteps);
        float scale = a.Intensity * flicker * trail;
        var sway = p.Sway > 0
            ? new Vector3(2f * Noise(Seed, key ^ SwayKeyX, Step, p.SwaySteps) - 1f, 0, 2f * Noise(Seed, key ^ SwayKeyZ, Step, p.SwaySteps) - 1f) * p.Sway
            : Vector3.Zero;
        _sources[_count] = new Fluid3DSource(a.Position, p.Radius, p.Density * scale, p.Temperature * scale,
            p.Velocity + velocity * p.VelocityScale + sway, p.VelocityRate)
        {
            From = segment ? from : null,
            Edge = p.Edge,
        };
        _infos[_count] = new Fluid3DEmitterInfo(key, a.Position, segment ? from : null, velocity, flicker, trail, jump);
        _count++;
        return true;
    }

    /// <summary>Ключи шумов раскачки по x и z: свои ряды, не совпадающие с мерцанием того же ключа.</summary>
    private const ulong SwayKeyX = 0x5A7E_0001_0000_0000UL, SwayKeyZ = 0x5A7E_0002_0000_0000UL;

    /// <summary>
    /// Шум мерцания в [0, 1): узлы каждые <paramref name="period"/> шагов — равномерное из SplitMix64 (зерно, ключ, узел), между узлами —
    /// гладкая ступень 3f² − 2f³.
    /// </summary>
    internal static float Noise(ulong seed, ulong key, ulong step, int period)
    {
        ulong node = step / (ulong)period;
        float f = (float)(step % (ulong)period) / period;
        float a = Hash01(seed, key, node), b = Hash01(seed, key, node + 1);
        return a + (b - a) * (f * f * (3f - 2f * f));
    }

    private static float Hash01(ulong seed, ulong key, ulong node) => (Mix(Mix(Mix(seed) ^ key) ^ node) >> 40) * (1f / (1 << 24));

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Ключи, которых нет в этом шаге, в следующем всё равно новые — их состояние не нужно.</summary>
    private void PruneStates()
    {
        int stale = 0;
        foreach (var (key, s) in _states)
        {
            if (s.Step == Step) continue;
            if (stale == _stale.Length) Array.Resize(ref _stale, 2 * _stale.Length);
            _stale[stale++] = key;
        }
        for (int i = 0; i < stale; i++) _states.Remove(_stale[i]);
    }
}
