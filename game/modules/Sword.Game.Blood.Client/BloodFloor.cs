using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Fluid2D;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Заявка шага пола (60 Гц по <c>TickId</c>): номер шага сима, тик, сдвиг окна целыми текселями (окно ушло на столько в сторону +X, +Z),
/// центр окна после сдвига (в текселях: центр = тексель·<see cref="BloodFloor.Texel"/>), штампы шага (<see cref="BloodFloor.Stamps"/>
/// [first, first + count)), сброс (первый шаг и <c>ArenaReset</c>: поле и карта обнуляются), часы пола, с (шагов с сброса × шаг).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BloodFloorStep
{
    public ulong Step, Tick;
    public int ShiftX, ShiftZ, CenterX, CenterZ, StampFirst, StampCount, Reset;
    public float Now;
}

/// <summary>Штамп пола: центр (x, z), м, полуось гаусса, м, объём ρ·м² (ρ — толщина / h_max), объём, мл.</summary>
public readonly record struct BloodFloorStamp(Vector2 Xz, float HalfAxis, float Volume, float Millilitres);

/// <summary>Капля в полёте: отрыв (точка, скорость, тик), приземление (тик, время после отрыва, точка), объём, мл.</summary>
public readonly record struct BloodFlying(Vector3 X0, Vector3 V0, ulong Tick0, ulong LandTick, float LandTime, Vector3 Land, float Volume);

/// <summary>
/// Кровь на полу на CPU: капли от тела (<see cref="BloodDrips.Drops"/>) — полёт замкнутой формулой (<see cref="BloodFlight"/>), приземление
/// по рельефу сразу при отрыве (тик и точка) → штамп в шаг, в котором тик приземления наступил; окно сима 32 м у игрока: центр — тексель,
/// пересадка целыми текселями, когда игрок ушёл от центра дальше <see cref="Hysteresis"/> по оси. Заявки шагов — GPU-симуляции
/// (<see cref="BloodFloorGpu"/>). Пауза — шагов нет, кровь стоит. Буферы растут удвоением.
/// </summary>
public sealed class BloodFloor
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

    /// <summary>Пересадка окна — игрок дальше от центра по оси, м.</summary>
    public const float Hysteresis = 2;

    /// <summary>Полёт не дольше, тиков (дальше капля пропадает).</summary>
    public const int MaxFlightTicks = 600;

    /// <summary>Точек приземления на оверлее (кольцо).</summary>
    public const int RecentLandings = 64;

    /// <summary>
    /// Сим пола: плёнка A′ на 32 м, 1024². Кисти нет — шум плотности выключен (текстура не нужна); маска края — один тексель: кровь у края
    /// окна не гаснет, а уходит в карту при сдвиге (с маской 50 см лужа у края теряла ~1 % до переноса).
    /// </summary>
    public static Fluid2DParams Params { get; } = Fluid2DParams.BloodSpread with { DensityNoiseEnabled = false, EdgeMaskWidth = 1f / N };

    private BloodFlying[] _flying = new BloodFlying[64];
    private int _flyingCount;
    private BloodFloorStamp[] _stamps = new BloodFloorStamp[64];
    private int _stampCount;
    private readonly Vector3[] _recent = new Vector3[RecentLandings];
    private bool _window, _resetPending = true, _puddle;
    private int _cx, _cz;
    private ulong _floorSteps;

    /// <summary>Штампы, заявленные в этом кадре (заявки ссылаются на диапазоны).</summary>
    public ReadOnlySpan<BloodFloorStamp> Stamps => _stamps.AsSpan(0, _stampCount);

    /// <summary>Капли в полёте.</summary>
    public ReadOnlySpan<BloodFlying> Flying => _flying.AsSpan(0, _flyingCount);

    /// <summary>Точки последних приземлений (кольцо).</summary>
    public ReadOnlySpan<Vector3> RecentLandingPoints => _recent.AsSpan(0, (int)Math.Min(Landed, RecentLandings));

    /// <summary>Окно есть (первый шаг с позой игрока был); центр, м (x, z).</summary>
    public bool HasWindow => _window;
    public Vector2 Center => new(_cx * Texel, _cz * Texel);
    public int CenterTexelX => _cx;
    public int CenterTexelZ => _cz;

    /// <summary>Счёт с начала: оторвалось капель (и мл), приземлилось (и мл), в окно (мл), мимо окна (мл), не коснулись рельефа, шагов, пересадок.</summary>
    public long Detached { get; private set; }
    public double DetachedMl { get; private set; }
    public long Landed { get; private set; }
    public double LandedMl { get; private set; }
    public double StampedMl { get; private set; }
    public double OutsideMl { get; private set; }
    public long Lost { get; private set; }
    public long Steps { get; private set; }
    public long Shifts { get; private set; }

    /// <summary>Часы пола, с: шагов с последнего сброса × шаг.</summary>
    public float Now => (float)(_floorSteps * Params.StepSeconds);

    /// <summary>Отладочная лужа: следующий шаг кладёт штамп объёма <paramref name="ml"/> у ног игрока.</summary>
    public bool PuddlePending => _puddle;
    public void RequestPuddle() => _puddle = true;

    /// <summary>Первый тик кадра: штампы прошлого кадра забраны (заявки кадра очищает хост).</summary>
    public void BeginFrame() => _stampCount = 0;

    /// <summary>Сброс арены (R): капли в полёте пропадают, поле и карта обнуляются следующим шагом.</summary>
    public void Reset()
    {
        _flyingCount = 0;
        _resetPending = true;
    }

    /// <summary>Отрывы шага: приземление по рельефу на CPU (тик и точка) — сразу; в полёт.</summary>
    public void Detach(ReadOnlySpan<BloodDrop> drops, ITerrainView? terrain, float drag, float tickDt)
    {
        for (int i = 0; i < drops.Length; i++) DetachOne(drops[i], terrain, drag, tickDt);
    }

    private void DetachOne(in BloodDrop d, ITerrainView? terrain, float drag, float tickDt)
    {
        Detached++;
        DetachedMl += d.Volume;
        if (!BloodFlight.Land(terrain, d.Position, d.Velocity, drag, tickDt, MaxFlightTicks, out int ticks, out float t, out var point))
        {
            Lost++;
            return;
        }
        if (_flyingCount == _flying.Length) Array.Resize(ref _flying, _flying.Length * 2);
        _flying[_flyingCount++] = new BloodFlying(d.Position, d.Velocity, d.Tick, d.Tick + (ulong)ticks, t, point, d.Volume);
    }

    /// <summary>
    /// Шаг пола на тике <paramref name="tick"/>: приземлившиеся к этому тику — в штампы, отладочная лужа, окно у игрока (<paramref name="player"/>,
    /// x, z; нет — окно стоит), заявка шага. Без окна (позы игрока ещё не было) шага нет.
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
        int first0 = _stampCount;
        int w = 0;
        for (int i = 0; i < _flyingCount; i++)
        {
            if (_flying[i].LandTick <= tick) Landing(_flying[i], spread);
            else _flying[w++] = _flying[i];
        }
        _flyingCount = w;
        if (_puddle && player is { } feet)
        {
            _puddle = false;
            AddStamp(feet, puddleMl, spread);
        }
        _floorSteps++;
        Steps++;
        requests?.Add(tick, new BloodFloorStep
        {
            Step = step, Tick = tick, ShiftX = sx, ShiftZ = sz, CenterX = _cx, CenterZ = _cz, StampFirst = first0, StampCount = _stampCount - first0,
            Reset = reset ? 1 : 0, Now = Now,
        });
        return true;
    }

    private void Landing(in BloodFlying f, float spread)
    {
        _recent[Landed % RecentLandings] = f.Land;
        Landed++;
        LandedMl += f.Volume;
        AddStamp(new Vector2(f.Land.X, f.Land.Z), f.Volume, spread);
    }

    private void AddStamp(Vector2 xz, float ml, float spread)
    {
        var c = Center;
        if (MathF.Abs(xz.X - c.X) > 0.5f * Size || MathF.Abs(xz.Y - c.Y) > 0.5f * Size)
        {
            OutsideMl += ml;
            return;
        }
        if (_stampCount == _stamps.Length) Array.Resize(ref _stamps, _stamps.Length * 2);
        _stamps[_stampCount++] = new BloodFloorStamp(xz, HalfAxis(ml, spread), DensityVolume(ml), ml);
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

    /// <summary>Полуось пятна капли, м: радиус диска толщины лужи H с этим объёмом × <paramref name="spread"/>.</summary>
    public static float HalfAxis(float ml, float spread) =>
        spread * MathF.Sqrt(ml * 1e-6f / (MathF.PI * Params.FilmPuddleThickness));
}
