using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Fluid2D;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Заявка шага пола (60 Гц по <c>TickId</c>): номер шага сима, тик, сдвиг окна целыми текселями (окно ушло на столько в сторону +X, +Z),
/// центр окна после сдвига (в текселях: центр = тексель·<see cref="BloodFloor.Texel"/>), штампы шага (<see cref="BloodFloor.Stamps"/>
/// [first, first + count)), штампы мимо окна — прямо в карту (<see cref="BloodFloor.MapStamps"/>), сброс (первый шаг и <c>ArenaReset</c>:
/// поле и карта обнуляются), перенос засохшего из окна в карту в этом шаге, часы пола, с (шагов с сброса × шаг), эпоха рождений карты, с.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BloodFloorStep
{
    public ulong Step, Tick;
    public int ShiftX, ShiftZ, CenterX, CenterZ, StampFirst, StampCount, Reset;
    public float Now;
    public int MapStampFirst, MapStampCount, Dry;
    public float Epoch;
}

/// <summary>
/// Штамп пола: центр (x, z), м, полуоси, м, угол (рад, от +X к +Z: ось u формы — по нему), форма (0 — гаусс, k ≥ 1 — слой k − 1 форм),
/// объём ρ·м² (ρ — толщина / h_max), объём, мл.
/// </summary>
public readonly record struct BloodFloorStamp(Vector2 Xz, Vector2 HalfAxes, float Angle, int Shape, float Volume, float Millilitres);

/// <summary>Штамп прямо в карту пятен (капля легла вне окна): центр (x, z), м, объём ρ·м².</summary>
public readonly record struct BloodMapStamp(Vector2 Xz, float Volume);

/// <summary>Капля в полёте: отрыв (точка, скорость, тик), приземление (тик, время после отрыва, точка, скорость), объём, мл, зерно формы.</summary>
public readonly record struct BloodFlying(Vector3 X0, Vector3 V0, ulong Tick0, ulong LandTick, float LandTime, Vector3 Land, float Volume,
    Vector3 LandVelocity, uint Seed);

/// <summary>
/// Кровь на полу на CPU: капли от тела (<see cref="BloodDrips.Drops"/>) и из ран трупа — полёт замкнутой формулой (<see cref="BloodFlight"/>),
/// приземление по рельефу сразу при отрыве (тик, точка, скорость) → штамп формы пака в шаг после тика приземления (капля на экране — до
/// касания); окно сима 32 м у игрока: центр — тексель, пересадка целыми текселями, когда игрок ушёл от центра дальше <see cref="Hysteresis"/>
/// по оси; капля вне окна — прямо в карту пятен. Отладочная лужа — выливание из точки. Заявки шагов — GPU-симуляции (<see cref="BloodFloorGpu"/>).
/// Пауза — шагов нет, кровь стоит. Буферы растут удвоением.
/// </summary>
public sealed class BloodFloor(BloodTuning? tuning = null)
{
    /// <summary>Сторона окна, текселей, и его размер, м (тексель 3.125 см).</summary>
    public const int N = 1024;
    public const float Size = 32;
    public const float Texel = Size / N;

    /// <summary>Мировая карта засохших пятен: 4096² на ±64 м вокруг центра арены — тот же тексель, что у окна.</summary>
    public const int MapN = 4096;
    public const float MapSize = 128;

    /// <summary>Тексель окна (центр в текселях) → тексель карты: + этот сдвиг.</summary>
    public const int MapOffset = (MapN - N) / 2;

    /// <summary>Рождение в карте — half относительно эпохи: эпоха — часы пола, округлённые вниз до этого шага, с (half точен до 2–4 с).</summary>
    public const float EpochStep = 4096;

    /// <summary>Пересадка окна — игрок дальше от центра по оси, м.</summary>
    public const float Hysteresis = 2;

    /// <summary>Полёт не дольше, тиков (дальше капля пропадает).</summary>
    public const int MaxFlightTicks = 600;

    /// <summary>Точек приземления на оверлее (кольцо).</summary>
    public const int RecentLandings = 64;

    /// <summary>Перенос засохшего из окна в карту — раз в столько шагов.</summary>
    public const int DryEvery = 30;

    /// <summary>Капля косая (форма вытянутых брызг), если синус угла её скорости с горизонтом меньше.</summary>
    public const float ObliqueSin = 0.9f;

    /// <summary>
    /// Сим пола: плёнка A′ на 32 м, 1024². Кисти нет — шум плотности выключен (текстура не нужна); маска края — один тексель: кровь у края
    /// окна не гаснет, а уходит в карту при сдвиге (с маской 50 см лужа у края теряла ~1 % до переноса).
    /// </summary>
    public static Fluid2DParams Params { get; } = Fluid2DParams.BloodSpread with { DensityNoiseEnabled = false, EdgeMaskWidth = 1f / N };

    private readonly BloodTuning _tuning = tuning ?? new BloodTuning();
    private BloodFlying[] _flying = new BloodFlying[64];
    private int _flyingCount;
    private BloodFloorStamp[] _stamps = new BloodFloorStamp[64];
    private int _stampCount;
    private BloodMapStamp[] _mapStamps = new BloodMapStamp[16];
    private int _mapStampCount;
    private readonly Vector3[] _recent = new Vector3[RecentLandings];
    private bool _window, _resetPending = true, _puddle;
    private int _cx, _cz;
    private ulong _floorSteps;
    private int _pourStep = int.MaxValue;
    private float _pourMl;
    private Vector2 _pourAt;
    private uint _pourSeed;

    /// <summary>Формы штампов (слои текстур пака); null — гаусс.</summary>
    public BloodStampShapes? Shapes { get; set; }

    /// <summary>Штампы, заявленные в этом кадре (заявки ссылаются на диапазоны).</summary>
    public ReadOnlySpan<BloodFloorStamp> Stamps => _stamps.AsSpan(0, _stampCount);

    /// <summary>Штампы мимо окна — в карту, заявленные в этом кадре.</summary>
    public ReadOnlySpan<BloodMapStamp> MapStamps => _mapStamps.AsSpan(0, _mapStampCount);

    /// <summary>Капли в полёте.</summary>
    public ReadOnlySpan<BloodFlying> Flying => _flying.AsSpan(0, _flyingCount);

    /// <summary>Точки последних приземлений (кольцо).</summary>
    public ReadOnlySpan<Vector3> RecentLandingPoints => _recent.AsSpan(0, (int)Math.Min(Landed, RecentLandings));

    /// <summary>Окно есть (первый шаг с позой игрока был); центр, м (x, z).</summary>
    public bool HasWindow => _window;
    public Vector2 Center => new(_cx * Texel, _cz * Texel);
    public int CenterTexelX => _cx;
    public int CenterTexelZ => _cz;

    /// <summary>
    /// Счёт с начала: оторвалось капель (и мл), приземлилось (и мл), в окно (мл), мимо окна — в карту (мл), мимо карты (мл), не коснулись
    /// рельефа, шагов, пересадок.
    /// </summary>
    public long Detached { get; private set; }
    public double DetachedMl { get; private set; }
    public long Landed { get; private set; }
    public double LandedMl { get; private set; }
    public double StampedMl { get; private set; }
    public double OutsideMl { get; private set; }
    public double OffMapMl { get; private set; }
    public long Lost { get; private set; }
    public long Steps { get; private set; }
    public long Shifts { get; private set; }

    /// <summary>Часы пола, с: шагов с последнего сброса × шаг.</summary>
    public float Now => (float)(_floorSteps * Params.StepSeconds);

    /// <summary>Эпоха рождений карты для часов <paramref name="now"/>, с.</summary>
    public static float Epoch(float now) => MathF.Floor(now / EpochStep) * EpochStep;

    /// <summary>Отладочная лужа: следующий шаг начинает выливание у ног игрока (объём — <see cref="BloodTuning.PuddleMl"/>).</summary>
    public bool PuddlePending => _puddle;
    public void RequestPuddle() => _puddle = true;

    /// <summary>Идёт выливание отладочной лужи.</summary>
    public bool Pouring => _pourStep < PourSteps;

    private int PourSteps => Math.Max(1, (int)MathF.Round(_tuning.PourSeconds * BloodModule.StepRate));

    /// <summary>Первый тик кадра: штампы прошлого кадра забраны (заявки кадра очищает хост).</summary>
    public void BeginFrame() => _stampCount = _mapStampCount = 0;

    /// <summary>Сброс арены (R): капли в полёте пропадают, выливание кончается, поле и карта обнуляются следующим шагом.</summary>
    public void Reset()
    {
        _flyingCount = 0;
        _pourStep = int.MaxValue;
        _resetPending = true;
    }

    /// <summary>Отрывы шага: приземление по рельефу на CPU (тик, точка, скорость) — сразу; в полёт. Зерно формы — (боец, тик, номер в шаге).</summary>
    public void Detach(ReadOnlySpan<BloodDrop> drops, ITerrainView? terrain, float drag, float tickDt)
    {
        for (int i = 0; i < drops.Length; i++) DetachOne(drops[i], i, terrain, drag, tickDt);
    }

    private void DetachOne(in BloodDrop d, int index, ITerrainView? terrain, float drag, float tickDt)
    {
        Detached++;
        DetachedMl += d.Volume;
        if (!BloodFlight.Land(terrain, d.Position, d.Velocity, drag, tickDt, MaxFlightTicks, out int ticks, out float t, out var point))
        {
            Lost++;
            return;
        }
        if (_flyingCount == _flying.Length) Array.Resize(ref _flying, _flying.Length * 2);
        uint seed = BloodHash.Combine(BloodHash.Seed(d.Fighter, d.Tick, 0), (uint)index);
        _flying[_flyingCount++] = new BloodFlying(d.Position, d.Velocity, d.Tick, d.Tick + (ulong)ticks, t, point, d.Volume,
            BloodFlight.Velocity(d.Velocity, drag, t), seed);
    }

    /// <summary>
    /// Шаг пола на тике <paramref name="tick"/>: капли, коснувшиеся рельефа до этого тика, — в штампы (капля на экране видна до касания, поле
    /// без доли тика — штамп на шаг позже), выливание отладочной лужи, окно у игрока (<paramref name="player"/>, x, z; нет — окно стоит),
    /// заявка шага. Без окна (позы игрока ещё не было) шага нет.
    /// </summary>
    public bool Step(ulong tick, ulong step, Vector2? player, float spread, float puddleMl, SimRequests? requests)
    {
        if (!_window)
        {
            if (player is not { } first) return false;
            _cx = (int)MathF.Round(first.X / Texel);
            _cz = (int)MathF.Round(first.Y / Texel);
            _window = true;
        }
        int sx = 0, sz = 0;
        if (player is { } p && (MathF.Abs(p.X - _cx * Texel) > Hysteresis || MathF.Abs(p.Y - _cz * Texel) > Hysteresis))
        {
            sx = (int)MathF.Round(p.X / Texel) - _cx;
            sz = (int)MathF.Round(p.Y / Texel) - _cz;
        }
        bool reset = _resetPending;
        _resetPending = false;
        if (reset)
        {
            _floorSteps = 0;
            sx = sz = 0;
            if (player is { } r)
            {
                _cx = (int)MathF.Round(r.X / Texel);
                _cz = (int)MathF.Round(r.Y / Texel);
            }
        }
        if (sx != 0 || sz != 0) Shifts++;
        _cx += sx;
        _cz += sz;
        int first0 = _stampCount, map0 = _mapStampCount;
        int w = 0;
        for (int i = 0; i < _flyingCount; i++)
        {
            if (_flying[i].LandTick < tick) Landing(_flying[i], spread);
            else _flying[w++] = _flying[i];
        }
        _flyingCount = w;
        if (_puddle && player is { } feet)
        {
            _puddle = false;
            (_pourStep, _pourMl, _pourAt, _pourSeed) = (0, puddleMl, feet, BloodHash.Combine((uint)tick, 0x50u));
        }
        if (Pouring) Pour(spread);
        _floorSteps++;
        Steps++;
        requests?.Add(tick, new BloodFloorStep
        {
            Step = step, Tick = tick, ShiftX = sx, ShiftZ = sz, CenterX = _cx, CenterZ = _cz, StampFirst = first0, StampCount = _stampCount - first0,
            Reset = reset ? 1 : 0, Now = Now, MapStampFirst = map0, MapStampCount = _mapStampCount - map0,
            Dry = _floorSteps % DryEvery == 0 ? 1 : 0, Epoch = Epoch(Now),
        });
        return true;
    }

    /// <summary>
    /// Шаг выливания: каждый шаг — брызги вокруг точки (четверть объёма), каждый шестой — пятно формы лужи у точки с разбросом (остальное):
    /// растекание A′ сливает их в лужу неровной формы.
    /// </summary>
    private void Pour(float spread)
    {
        int steps = PourSteps, k = _pourStep++;
        uint seed = BloodHash.Combine(_pourSeed, (uint)k);
        int puddles = (steps + 5) / 6;
        if (k % 6 == 0)
        {
            var at = _pourAt + Jitter(seed, 10, 0.1f);
            AddStamp(at, 0.75f * _pourMl / puddles, BloodShapeGroup.Puddles, Angle(seed), 1, BloodHash.Combine(seed, 11), 1, PuddleThickness);
        }
        float r = 0.04f + 0.2f * BloodHash.Unit(seed, 12);
        var dir = Direction(seed, 13);
        AddStamp(_pourAt + dir * r, 0.25f * _pourMl / steps, BloodHash.Unit(seed, 14) < 0.5f ? BloodShapeGroup.Omni : BloodShapeGroup.Spots,
            Angle(BloodHash.Combine(seed, 15)), 1, BloodHash.Combine(seed, 16), spread, Params.FilmPuddleThickness);
    }

    /// <summary>Капля медленнее, м/с, ложится пятном без брызг.</summary>
    public const float GentleSpeed = 2.5f;

    /// <summary>Пятно формы лужи — средняя толщина под ним, м.</summary>
    private const float PuddleThickness = 1.2e-3f;

    private static float Angle(uint seed) => 2 * MathF.PI * BloodHash.Unit(seed, 0);

    private static Vector2 Direction(uint seed, uint index)
    {
        float a = 2 * MathF.PI * BloodHash.Unit(seed, index);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    private static Vector2 Jitter(uint seed, uint index, float radius) =>
        Direction(seed, index) * (radius * MathF.Sqrt(BloodHash.Unit(seed, index + 1)));

    /// <summary>
    /// Капля коснулась: медленная (упала с высоты меньше ~30 см, лужа под трупом) — мягкое пятно формы лужи без брызг; косая — вытянутые
    /// брызги по горизонтальной скорости (хвост по ходу); отвесная — круглые брызги или капли.
    /// </summary>
    private void Landing(in BloodFlying f, float spread)
    {
        _recent[Landed % RecentLandings] = f.Land;
        Landed++;
        LandedMl += f.Volume;
        var v = f.LandVelocity;
        float horizontal = MathF.Sqrt(v.X * v.X + v.Z * v.Z), speed = v.Length();
        float sin = speed > 1e-4f ? MathF.Abs(v.Y) / speed : 1;
        var xz = new Vector2(f.Land.X, f.Land.Z);
        if (speed < GentleSpeed)
            AddStamp(xz, f.Volume, BloodShapeGroup.Puddles, Angle(BloodHash.Combine(f.Seed, 9)), 1, f.Seed, 1, PuddleThickness);
        else if (sin < ObliqueSin && horizontal > 0.05f)
        {
            float angle = MathF.Atan2(v.Z, v.X) + 0.3f * (BloodHash.Unit(f.Seed, 5) - 0.5f);
            AddStamp(xz, f.Volume, BloodShapeGroup.Directional, angle, Math.Clamp(1 / MathF.Max(sin, 1e-3f), 1, 4), f.Seed, spread,
                Params.FilmPuddleThickness);
        }
        else
        {
            var group = BloodHash.Unit(f.Seed, 6) < 0.5f ? BloodShapeGroup.Omni : BloodShapeGroup.Spots;
            AddStamp(xz, f.Volume, group, Angle(BloodHash.Combine(f.Seed, 7)), 1, f.Seed, spread, Params.FilmPuddleThickness);
        }
    }

    /// <summary>
    /// Штамп объёма <paramref name="ml"/>: форма группы по зерну (масштаб 0.8…1.25, соотношение сторон слоя × <paramref name="stretch"/> по оси u),
    /// размер — средняя толщина <paramref name="thickness"/> в «крови» формы (квадрат 2a, доля — среднее слоя) × <paramref name="spread"/>; форм нет —
    /// гаусс прежнего размера. Вне окна — в карту.
    /// </summary>
    private void AddStamp(Vector2 xz, float ml, BloodShapeGroup group, float angle, float stretch, uint seed, float spread, float thickness)
    {
        var c = Center;
        if (MathF.Abs(xz.X - c.X) > 0.5f * Size || MathF.Abs(xz.Y - c.Y) > 0.5f * Size)
        {
            if (MathF.Abs(xz.X) >= 0.5f * MapSize || MathF.Abs(xz.Y) >= 0.5f * MapSize)
            {
                OffMapMl += ml;
                return;
            }
            if (_mapStampCount == _mapStamps.Length) Array.Resize(ref _mapStamps, _mapStamps.Length * 2);
            _mapStamps[_mapStampCount++] = new BloodMapStamp(xz, DensityVolume(ml));
            OutsideMl += ml;
            return;
        }
        int shape = Shapes?.Pick(group, seed) ?? 0;
        Vector2 half;
        if (shape == 0)
        {
            half = new Vector2(HalfAxis(ml, spread));
            angle = 0;
        }
        else
        {
            float a = spread * MathF.Sqrt(ml * 1e-6f / (4 * Shapes!.Mean(shape) * thickness)) * (0.8f + 0.45f * BloodHash.Unit(seed, 8));
            float s = MathF.Sqrt(Shapes.Aspect(shape) * stretch);
            half = new Vector2(a * s, a / s);
        }
        if (_stampCount == _stamps.Length) Array.Resize(ref _stamps, _stamps.Length * 2);
        _stamps[_stampCount++] = new BloodFloorStamp(xz, half, angle, shape, DensityVolume(ml), ml);
        StampedMl += ml;
    }

    /// <summary>
    /// Пиксель принимает плёнку — та же проверка, что в <c>PsFloor</c>: бит «принимает декали» (кожа и оружие — нет), нормаль не круче
    /// предела (бока колонн и подступёнки — нет), точка у рельефа по высоте (верх колонны — нет).
    /// </summary>
    public static bool Receives(bool receiver, Vector3 normal, float y, float ground, float cosMax, float band) =>
        receiver && normal.Y >= cosMax && MathF.Abs(y - ground) <= band;

    /// <summary>Объём, мл → объём штампа ρ·м² (ρ — толщина / <see cref="Fluid2DParams.FilmThicknessPerDensity"/>).</summary>
    public static float DensityVolume(float ml) => ml * 1e-6f / Params.FilmThicknessPerDensity;

    /// <summary>Объём ρ·м² → мл.</summary>
    public static double Millilitres(double densityVolume) => densityVolume * Params.FilmThicknessPerDensity * 1e6;

    /// <summary>Полуось гаусса пятна капли, м: радиус диска толщины лужи H с этим объёмом × <paramref name="spread"/>.</summary>
    public static float HalfAxis(float ml, float spread) =>
        spread * MathF.Sqrt(ml * 1e-6f / (MathF.PI * Params.FilmPuddleThickness));
}
