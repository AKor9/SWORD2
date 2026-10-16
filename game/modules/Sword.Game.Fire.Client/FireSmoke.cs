using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Fluid3D;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Fire;

/// <summary>
/// Числа дыма — материал <c>MI_HVOL_SIM_TorchSmoke</c> LIVE-2 (FSR §3.8) над плотностью 3D-газа: экстинкция σ = DensityMult·(ρ − срез)^DensityPow,
/// серое альбедо, «фальшивый ambient» неба как эмиссия, однократное рассеяние света огня (× рассеяние в объёмах, фаза 1/4π, без light
/// function) и солнца, без теней и самотени. Отступления от L2 — README модуля («Дым»): объём — окно газа в мире, а не слой вокруг площадки
/// (выдавливания слоя, маски краёв и градиента по глубине слоя нет); срез малой ρ; единицы экстинкции — множитель <see cref="Density"/>.
/// </summary>
public sealed record SmokeParams
{
    /// <summary>σ = DensityMult·(ρ − <see cref="DensityCut"/>)^DensityPow (<c>DensityMult</c>, <c>DensityPow</c>).</summary>
    public float DensityMult { get; init; } = 0.1f;
    public float DensityPow { get; init; } = 0.35f;

    /// <summary>
    /// Срез плотности: ρ не больше — σ = 0. У ρ^0.35 видна и ничтожная ρ (10⁻⁵ — 0.018 от ρ = 1): без среза хвост адвекции — туман во всё окно
    /// и ни одной пустой ячейки для пропуска. σ непрерывна на срезе.
    /// </summary>
    public float DensityCut { get; init; } = 0.004f;

    /// <summary>Альбедо рассеяния (серое).</summary>
    public float Albedo { get; init; } = 0.3646f;

    /// <summary>Фальшивый ambient: E = Mult·σ^Pow·(1 − σ^AoPow)·lerp(avg(небо), небо, SkyColour).</summary>
    public float AmbientMult { get; init; } = 0.3f;
    public float AmbientPow { get; init; } = 0.75f;
    public float AmbientAoPow { get; init; } = 1.4f;
    public float SkyColour { get; init; } = 0.5f;

    /// <summary>Шаг марша в ячейках газа (половина ячейки — 1.5 см на сетке 3 см).</summary>
    public float StepCells { get; init; } = 0.5f;

    /// <summary>Шагов на луч не больше: длиннее луч в окне — шаг длиннее (диагональ окна 5.6 м — 373 шага по 1.5 см).</summary>
    public int MaxSteps { get; init; } = 512;

    /// <summary>Марш кончается, когда пропускание ниже (остаток за ним не виден).</summary>
    public float MinTransmittance { get; init; } = 0.01f;

    /// <summary>
    /// Множитель экстинкции и эмиссии — 1/Scale объёма HV: локальных единиц на сантиметр пути (в LIVE-2 не установлен, FSR §3.8 «Единицы
    /// экстинкции»). σ на метр = 100·Density·DensityMult·ρ^0.35. Подобран по кадрам LIVE-2 (README, «Дым»).
    /// </summary>
    public float Density { get; init; } = 1.2f;

    /// <summary>Пропуск пустоты: сетка занятости и рамка непустого (выкл. — полный марш, для проверки).</summary>
    public bool SkipEmpty { get; init; } = true;

    /// <summary>
    /// Детализирующий шум: ρ' = ρ·sat(1 − a·(½ + n)), n — градиентный шум Перлина в мире (шаг решётки <see cref="DetailScale"/>),
    /// переносимый скоростью газа двухфазной flow-map (как <c>FlowMap3d</c> VolumeSmoke VFX Tools, плотность «выедается» как там); a = 0 —
    /// выкл. (кадр бит в бит без шума). Шум только уменьшает ρ: пропуск пустоты точен по-прежнему.
    /// </summary>
    public float Detail { get; init; }

    /// <summary>Шаг решётки детализирующего шума, м.</summary>
    public float DetailScale { get; init; } = 0.05f;

    /// <summary>
    /// Flow-map: фаз в секунду (период 1/это с; две фазы со сдвигом ½, вес — треугольник) и доля скорости газа, с которой шум уносится
    /// (1 — вместе с газом). Время — номер шага газа × шаг: кадр с тем же шагом газа — тот же шум.
    /// </summary>
    public float FlowSpeed { get; init; } = 2f;
    public float FlowStrength { get; init; } = 1f;

    /// <summary>Факел LIVE-2; детализирующий шум 0.5 с шагом 8 см — мягкие прожилки, а не зерно (подгонка по кадрам, README модуля, «Дым»).</summary>
    public static SmokeParams Torch { get; } = new() { Detail = 0.5f, DetailScale = 0.08f };

    /// <summary>Единиц σ и эмиссии на метр пути: 100·<see cref="Density"/>.</summary>
    public float UnitsPerMetre => 100f * Density;

    public void Validate()
    {
        if (!(DensityMult >= 0) || !(DensityPow > 0) || !(DensityCut >= 0) || !(Density >= 0))
            throw new ArgumentOutOfRangeException(nameof(Density), "дым: множители ≥ 0, степень > 0");
        if (!(StepCells > 0) || MaxSteps is < 1 or > 4096 || !(MinTransmittance is >= 0 and < 1))
            throw new ArgumentOutOfRangeException(nameof(StepCells), "дым: шаг > 0, шагов 1…4096, пропускание 0…1");
        if (!(Detail >= 0) || !float.IsFinite(Detail) || !(DetailScale > 0) || !float.IsFinite(DetailScale) || !(FlowSpeed > 0) ||
            !float.IsFinite(FlowSpeed) || !(FlowStrength >= 0) || !float.IsFinite(FlowStrength))
            throw new ArgumentOutOfRangeException(nameof(Detail), "дым: шум ≥ 0, шаг решётки > 0, фаз в секунду > 0, доля скорости ≥ 0");
    }
}

/// <summary>Источник рассеяния в дыме: позиция, излучение (цвет × интенсивность × угасание × рассеяние в объёмах), 1/R², показатель «экспоненты».</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SmokeLight
{
    public Vector3 Position; public float InvRadiusSq;
    public Vector3 Radiance; public float Exponent;

    /// <summary>Точечный источник в дыме: без light function (в HV её нет) и без дальности показа; множитель — <see cref="PointLight.VolumetricScattering"/>.</summary>
    public static SmokeLight From(in PointLight light) => new()
    {
        Position = light.Position,
        InvRadiusSq = light.Radius > 0 ? 1 / (light.Radius * light.Radius) : 0,
        Radiance = light.Color * (light.Intensity * light.VolumetricScattering),
        Exponent = light.Exponent,
    };
}

/// <summary>
/// Объём дыма кадра — окно газа одного экземпляра: номер экземпляра (его сетка занятости), ρ и T газа (<see cref="Fluid3DSim.Scalars"/>),
/// скорость газа (<see cref="Fluid3DSim.Velocity"/>, перенос детализирующего шума), окно в мире, номер содержимого (сменился — сетка
/// занятости строится заново) и время газа, с (номер шага × шаг — фаза flow-map).
/// </summary>
internal struct SmokeVolume
{
    public int Slot;
    public GpuStorage Scalars;
    public GpuStorage Velocity;
    public Fluid3DWindow Window;
    public ulong Version;
    public double Time;
}

/// <summary>
/// Фазы flow-map кадра: сдвиги шума двух фаз, с/м (× скорость газа, м/с — сдвиг в узлах решётки шума), и вес второй фазы. Фаза f = frac(τ) − ½,
/// τ = время·FlowSpeed, вторая — со сдвигом ½; вес второй — |2f| (первая свежая — 0, первая на сбросе — 1).
/// </summary>
public readonly record struct SmokeFlow(float Shift0, float Shift1, float Blend);

/// <summary>Камера и окружение дыма кадра: вид-проекция, глаз, ось взгляда, туман, солнце и небо (k линейной глубины — у цели ½).</summary>
internal readonly record struct SmokeView(Matrix4x4 ViewProj, Vector3 Eye, Vector3 Forward, SceneFog Fog, SceneSky Sky);

/// <summary>Результат марша одного луча: излучение (премультиплицированное), пропускание, шагов по лучу, выборок с σ &gt; 0.</summary>
public readonly record struct SmokeSample(Vector3 Light, float Transmittance, int Steps, int Samples)
{
    /// <summary>Непрозрачность 1 − T.</summary>
    public float Alpha => 1 - Transmittance;
}

/// <summary>ρ газа на CPU (эталон марша): значения ячеек (x + Nx·(y + Ny·z)), окно в мире; скорость газа — перенос детализирующего шума.</summary>
public sealed record SmokeGrid(float[] Rho, int Nx, int Ny, int Nz, float CellSize, Vector3 Min)
{
    /// <summary>Скорость газа: vx, vy, vz ячейки подряд, м/с; null — ноль.</summary>
    public float[]? Velocity { get; init; }

    /// <summary>Та же сетка со скоростью газа (half: vx, vy, vz, 0 на ячейку — <see cref="Fluid3DFields.Velocity"/>).</summary>
    public SmokeGrid WithVelocity(ReadOnlySpan<Half> velocity)
    {
        var v = new float[3 * Rho.Length];
        for (int i = 0; i < Rho.Length; i++)
        {
            v[3 * i] = (float)velocity[4 * i];
            v[3 * i + 1] = (float)velocity[4 * i + 1];
            v[3 * i + 2] = (float)velocity[4 * i + 2];
        }
        return this with { Velocity = v };
    }

    /// <summary>Скорость ячейки, индекс зажат в окно (как у сима).</summary>
    public Vector3 VelocityCell(int x, int y, int z)
    {
        if (Velocity is null) return Vector3.Zero;
        int i = Math.Clamp(x, 0, Nx - 1) + Nx * (Math.Clamp(y, 0, Ny - 1) + Ny * Math.Clamp(z, 0, Nz - 1));
        return new Vector3(Velocity[3 * i], Velocity[3 * i + 1], Velocity[3 * i + 2]);
    }

    /// <summary>Скорость трилинейно в координатах индексов, как шейдер.</summary>
    public Vector3 VelocityAt(Vector3 q)
    {
        float fx = MathF.Floor(q.X), fy = MathF.Floor(q.Y), fz = MathF.Floor(q.Z);
        int x = (int)fx, y = (int)fy, z = (int)fz;
        float wx = q.X - fx, wy = q.Y - fy, wz = q.Z - fz;
        var x00 = Lerp(VelocityCell(x, y, z), VelocityCell(x + 1, y, z), wx);
        var x10 = Lerp(VelocityCell(x, y + 1, z), VelocityCell(x + 1, y + 1, z), wx);
        var x01 = Lerp(VelocityCell(x, y, z + 1), VelocityCell(x + 1, y, z + 1), wx);
        var x11 = Lerp(VelocityCell(x, y + 1, z + 1), VelocityCell(x + 1, y + 1, z + 1), wx);
        return Lerp(Lerp(x00, x10, wy), Lerp(x01, x11, wy), wz);
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    /// <summary>ρ из скаляров газа (half ρ в младших 16 битах, T — в старших).</summary>
    public static SmokeGrid FromScalars(ReadOnlySpan<Half> scalars, Fluid3DWindow window)
    {
        var rho = new float[window.Nx * window.Ny * window.Nz];
        for (int i = 0; i < rho.Length; i++) rho[i] = (float)scalars[2 * i];
        return new SmokeGrid(rho, window.Nx, window.Ny, window.Nz, window.CellSize, window.Min);
    }

    public Vector3 Max => Min + new Vector3(Nx, Ny, Nz) * CellSize;

    /// <summary>ρ ячейки; за окном — 0.</summary>
    public float Cell(int x, int y, int z) =>
        x < 0 || y < 0 || z < 0 || x >= Nx || y >= Ny || z >= Nz ? 0 : Rho[x + Nx * (y + Ny * z)];

    /// <summary>ρ трилинейно в координатах индексов (центры ячеек — целые), как шейдер.</summary>
    public float Density(Vector3 q)
    {
        float fx = MathF.Floor(q.X), fy = MathF.Floor(q.Y), fz = MathF.Floor(q.Z);
        int x = (int)fx, y = (int)fy, z = (int)fz;
        float wx = q.X - fx, wy = q.Y - fy, wz = q.Z - fz;
        float x00 = Lerp(Cell(x, y, z), Cell(x + 1, y, z), wx);
        float x10 = Lerp(Cell(x, y + 1, z), Cell(x + 1, y + 1, z), wx);
        float x01 = Lerp(Cell(x, y, z + 1), Cell(x + 1, y, z + 1), wx);
        float x11 = Lerp(Cell(x, y + 1, z + 1), Cell(x + 1, y + 1, z + 1), wx);
        return MathF.Max(Lerp(Lerp(x00, x10, wy), Lerp(x01, x11, wy), wz), 0f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// CPU-эталон дыма (<c>fire/fire_smoke.hlsl</c>): те же шаги и числа, что шейдер, — отрезок луча в боксе окна, обрезанный камерой и сценой,
/// равные шаги по нему, ρ трилинейно, σ, эмиссия, рассеяние; интеграл HV (L += T·(E + S)·k, T ·= exp(−σk)) по серединам шагов до T &lt;
/// <see cref="SmokeParams.MinTransmittance"/>. Пропуска пустоты в эталоне нет: пропуск не меняет результат (выборки там — σ = 0).
/// </summary>
public static class SmokeMath
{
    /// <summary>Фаза изотропного рассеяния 1/4π.</summary>
    public const float Phase = 1f / (4f * MathF.PI);

    /// <summary>Сторона кирпича сетки занятости, ячеек газа.</summary>
    public const int Brick = 4;

    /// <summary>σ материала при плотности <paramref name="rho"/>: DensityMult·(ρ − срез)^DensityPow, не больше среза — 0.</summary>
    public static float Sigma(float rho, SmokeParams p) => rho > p.DensityCut ? p.DensityMult * MathF.Pow(rho - p.DensityCut, p.DensityPow) : 0f;

    /// <summary>Небо фальшивого ambient: lerp(среднее каналов, небо, <see cref="SmokeParams.SkyColour"/>).</summary>
    public static Vector3 Ambient(Vector3 sky, SmokeParams p)
    {
        float avg = (sky.X + sky.Y + sky.Z) / 3;
        return new Vector3(avg) + (sky - new Vector3(avg)) * p.SkyColour;
    }

    /// <summary>Солнце как источник рассеяния: освещённость UE-света = π × множитель сцены, рассеяние в объёмах 1.</summary>
    public static Vector3 SunRadiance(Vector3 sun) => sun * MathF.PI;

    /// <summary>Эмиссия материала: Mult·σ^Pow·(1 − σ^AoPow)·ambient.</summary>
    public static Vector3 Emission(float sigma, Vector3 ambient, SmokeParams p) =>
        ambient * (p.AmbientMult * MathF.Pow(sigma, p.AmbientPow) * (1 - MathF.Pow(sigma, p.AmbientAoPow)));

    /// <summary>Излучение источников в точке <paramref name="world"/>: солнце + Σ излучение·затухание «экспоненты».</summary>
    public static Vector3 Incident(Vector3 world, Vector3 sun, ReadOnlySpan<SmokeLight> lights)
    {
        var sum = sun;
        foreach (ref readonly var l in lights)
        {
            float distSq = Vector3.DistanceSquared(l.Position, world);
            float b = 1 - Math.Clamp(distSq * l.InvRadiusSq, 0f, 1f);
            sum += l.Radiance * (b > 0 ? MathF.Pow(b, l.Exponent) : 0);
        }
        return sum;
    }

    /// <summary>Компонента направления без нуля: деление на ноль дало бы NaN у луча вдоль грани.</summary>
    public static float Safe(float x) => MathF.Abs(x) < 1e-8f ? (x < 0 ? -1e-8f : 1e-8f) : x;

    /// <summary>Отрезок луча в боксе [<paramref name="lo"/>, <paramref name="hi"/>] внутри [<paramref name="near"/>, <paramref name="far"/>]; пусто — false.</summary>
    public static bool Box(Vector3 eye, Vector3 dir, Vector3 lo, Vector3 hi, float near, float far, out float t0, out float t1)
    {
        var inv = new Vector3(1 / Safe(dir.X), 1 / Safe(dir.Y), 1 / Safe(dir.Z));
        var ta = (lo - eye) * inv;
        var tb = (hi - eye) * inv;
        var tmin = Vector3.Min(ta, tb);
        var tmax = Vector3.Max(ta, tb);
        t0 = MathF.Max(MathF.Max(tmin.X, tmin.Y), MathF.Max(tmin.Z, near));
        t1 = MathF.Min(MathF.Min(tmax.X, tmax.Y), MathF.Min(tmax.Z, far));
        return t1 > t0;
    }

    /// <summary>Шаг решётки детализирующего шума на экране меньше стольких пикселей ½ — шум у объёма выключен (не виден, только мерцал бы).</summary>
    public const float MinDetailPixels = 3;

    /// <summary>
    /// Шаг решётки <paramref name="scale"/>, м, у точки <paramref name="at"/> — в пикселях цели ½ (кадр шириной <paramref name="width"/>): отрезок
    /// поперёк взгляда через вид-проекцию; точка за камерой — бесконечность.
    /// </summary>
    public static float DetailPixels(Matrix4x4 viewProj, Vector3 forward, Vector3 at, float scale, int width)
    {
        var right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
        var a = Vector4.Transform(new Vector4(at, 1), viewProj);
        var b = Vector4.Transform(new Vector4(at + right * scale, 1), viewProj);
        if (a.W <= 1e-4f || b.W <= 1e-4f) return float.PositiveInfinity;
        var d = new Vector2(a.X / a.W - b.X / b.W, a.Y / a.W - b.Y / b.W);
        return d.Length() * 0.5f * width * 0.5f;
    }

    /// <summary>Фазы flow-map во время газа <paramref name="time"/>, с (<see cref="SmokeFlow"/>).</summary>
    public static SmokeFlow Flow(double time, SmokeParams p)
    {
        double tau = time * p.FlowSpeed;
        double f0 = tau - Math.Floor(tau) - 0.5, f1 = tau + 0.5 - Math.Floor(tau + 0.5) - 0.5;
        double s = p.FlowStrength / ((double)p.FlowSpeed * p.DetailScale);
        return new SmokeFlow((float)(f0 * s), (float)(f1 * s), (float)Math.Abs(2 * f0));
    }

    /// <summary>
    /// Шаг и начало координат детализирующего шума: узлов решётки на индекс сетки (ячейка / шаг решётки) и координата шума центра ячейки 0
    /// по модулю периода 256. Угол окна — целые ячейки (округление угла в float до ячейки точно), координата — в double: окно за километр
    /// от начала координат не теряет точности, а сдвиг окна целыми ячейками не сдвигает шум.
    /// </summary>
    public static (float Step, Vector3 Origin) DetailFrame(Vector3 min, float cellSize, SmokeParams p)
    {
        double l = p.DetailScale, cell = cellSize;
        return ((float)(cell / l), new Vector3(Origin(min.X, cell, l), Origin(min.Y, cell, l), Origin(min.Z, cell, l)));
    }

    private static float Origin(float min, double cell, double l) => Wrap((Math.Round(min / cell) + 0.5) * cell / l);

    private static float Wrap(double q)
    {
        double r = q - 256 * Math.Floor(q / 256);
        float f = (float)r;
        return f >= 256 ? 0f : f;
    }

    /// <summary>Хеш узла решётки шума (pcg3d по координатам по модулю 256, одно слово), как <c>SmokeHash</c> шейдера.</summary>
    public static uint Hash(int x, int y, int z)
    {
        uint a = (uint)x & 255, b = (uint)y & 255, c = (uint)z & 255;
        a = a * 1664525u + 1013904223u;
        b = b * 1664525u + 1013904223u;
        c = c * 1664525u + 1013904223u;
        a += b * c;
        b += c * a;
        c += a * b;
        a ^= a >> 16;
        b ^= b >> 16;
        c ^= c >> 16;
        return a + b * c;
    }

    /// <summary>Градиентный шум Перлина (квинтика) в точке шума <paramref name="q"/>, примерно −1…1.</summary>
    public static float Noise(Vector3 q)
    {
        float fx = MathF.Floor(q.X), fy = MathF.Floor(q.Y), fz = MathF.Floor(q.Z);
        int x = (int)fx, y = (int)fy, z = (int)fz;
        float tx = q.X - fx, ty = q.Y - fy, tz = q.Z - fz;
        float ux = Fade(tx), uy = Fade(ty), uz = Fade(tz);
        float x00 = Lerp(Corner(x, y, z, tx, ty, tz), Corner(x + 1, y, z, tx - 1, ty, tz), ux);
        float x10 = Lerp(Corner(x, y + 1, z, tx, ty - 1, tz), Corner(x + 1, y + 1, z, tx - 1, ty - 1, tz), ux);
        float x01 = Lerp(Corner(x, y, z + 1, tx, ty, tz - 1), Corner(x + 1, y, z + 1, tx - 1, ty, tz - 1), ux);
        float x11 = Lerp(Corner(x, y + 1, z + 1, tx, ty - 1, tz - 1), Corner(x + 1, y + 1, z + 1, tx - 1, ty - 1, tz - 1), ux);
        return Lerp(Lerp(x00, x10, uy), Lerp(x01, x11, uy), uz);
    }

    /// <summary>g·d градиента Перлина узла (12 рёбер куба и 4 повтора): ±u ± v выборами, как шейдер (без таблицы).</summary>
    private static float Corner(int x, int y, int z, float dx, float dy, float dz)
    {
        uint h = Hash(x, y, z) >> 28;
        float u = h < 8 ? dx : dy;
        float v = h < 4 ? dy : h == 12 || h == 14 ? dx : dz;
        return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
    }


    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// ρ с детализирующим шумом в точке индексов <paramref name="q"/>: ρ·sat(1 − a·(½ + n)), n — шум в точке шума, сдвинутой скоростью
    /// газа двух фаз flow-map, смешанные весом второй фазы. ρ не больше среза или a = 0 — ρ без шума.
    /// </summary>
    public static float DetailDensity(float rho, Vector3 q, SmokeGrid grid, SmokeFlow flow, SmokeParams p)
    {
        if (!(p.Detail > 0) || !(rho > p.DensityCut)) return rho;
        var (step, origin) = DetailFrame(grid.Min, grid.CellSize, p);
        var qn = q * step + origin;
        var v = grid.VelocityAt(q);
        float n0 = Noise(qn - v * flow.Shift0), n1 = Noise(qn - v * flow.Shift1);
        float n = n0 + (n1 - n0) * flow.Blend;
        return rho * Math.Clamp(1 - p.Detail * (0.5f + n), 0f, 1f);
    }

    /// <summary>Отрезок луча в окне (камера — t ≥ 0, сцена — t ≤ <paramref name="sceneT"/>) и шагов: ⌈длина/шаг⌉, не больше MaxSteps.</summary>
    public static (float T0, float T1, int Steps) Segment(Vector3 eye, Vector3 dir, float sceneT, SmokeGrid grid, SmokeParams p)
    {
        if (!Box(eye, dir, grid.Min, grid.Max, 0, sceneT, out float t0, out float t1)) return (t0, t1, 0);
        int n = (int)MathF.Min(MathF.Ceiling((t1 - t0) / (p.StepCells * grid.CellSize)), p.MaxSteps);
        return (t0, t1, Math.Max(n, 1));
    }

    /// <summary>Марш одного луча (как <c>MarchSmoke</c> шейдера без пропуска пустоты); <paramref name="flow"/> — фазы детализирующего шума.</summary>
    public static SmokeSample March(Vector3 eye, Vector3 dir, float sceneT, SmokeGrid grid, Vector3 sunRadiance, Vector3 ambient,
        ReadOnlySpan<SmokeLight> lights, SmokeParams p, SmokeFlow flow = default)
    {
        var (t0, t1, n) = Segment(eye, dir, sceneT, grid, p);
        if (n == 0) return new SmokeSample(Vector3.Zero, 1, 0, 0);
        float dt = (t1 - t0) / n;
        float k = p.UnitsPerMetre * dt;
        var q0 = (eye - grid.Min) / grid.CellSize - new Vector3(0.5f);
        var dq = dir / grid.CellSize;
        Vector3 light = Vector3.Zero;
        float transmittance = 1;
        int samples = 0;
        for (int i = 0; i < n; i++)
        {
            float t = t0 + (i + 0.5f) * dt;
            var q = q0 + dq * t;
            float sigma = Sigma(DetailDensity(grid.Density(q), q, grid, flow, p), p);
            if (!(sigma > 0)) continue;
            samples++;
            var emission = Emission(sigma, ambient, p);
            var scatter = Incident(eye + dir * t, sunRadiance, lights) * (p.Albedo * sigma * Phase);
            light += transmittance * (emission + scatter) * k;
            transmittance *= MathF.Exp(-sigma * k);
            if (transmittance < p.MinTransmittance) break;
        }
        return new SmokeSample(light, transmittance, n, samples);
    }

    /// <summary>Туман сцены поверх дыма (как у пламени — lerp к цвету тумана): премультиплицированный вклад (L·(1 − f) + туман·f·α, α).</summary>
    public static Vector4 Composite(SmokeSample s, float fog, Vector3 fogColour) =>
        new(s.Light * (1 - fog) + fogColour * (fog * s.Alpha), s.Alpha);

    /// <summary>Кирпичей сетки занятости по оси: ⌈ячеек / <see cref="Brick"/>⌉.</summary>
    public static int Bricks(int cells) => (cells + Brick - 1) / Brick;
}

/// <summary>Константы объёма дыма — <c>SmokeConstants</c> шейдера (строки по 16 байт; размер сверяет контракт).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SmokeConstants
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    public Vector3 Eye; public float StepCells;
    public Vector3 Forward; public uint MaxSteps;
    public Vector3 BoxMin; public float CellSize;
    public uint Nx, Ny, Nz, Scalars;
    public uint Bx, By, Bz, Bricks;
    public Vector3 Sun; public float Albedo;
    public Vector3 Ambient; public float UnitsPerMetre;
    public Vector3 FogColour; public float Fog;
    public float DensityMult, DensityPow, DensityCut, AmbientMult;
    public float AmbientPow, AmbientAoPow, InvWidth, InvHeight;
    public Vector2 ViewDepth; public uint SceneDepth; public uint Bounds;
    public uint Lights, LightCount, Skip; public float MinTransmittance;
    public Vector3 NoiseOrigin; public float NoiseStep;
    public float Detail, FlowShift0, FlowShift1, FlowBlend;
    public uint Velocity, Pad0, Pad1, Pad2;
}

/// <summary>
/// Итог марша по сетке лучей кадра (<see cref="FireSmoke.Stats"/>): лучей, из них в окне, шагов на такой луч (средн., макс.), выборок с σ &gt; 0
/// на луч в окне (средн., макс.), покрытие (доля лучей с α ≥ 1/255), средняя и наибольшая α; у первого объёма — непустых кирпичей сетки
/// занятости из всех и рамка непустых (кирпичи, включительно).
/// </summary>
public readonly record struct SmokeStats(ulong Frame, int Rays, int Hit, double MeanSteps, int MaxSteps, double MeanSamples, int MaxSamples,
    double Coverage, double MeanAlpha, float MaxAlpha, int Bricks, int TotalBricks, (int X, int Y, int Z) BrickMin, (int X, int Y, int Z) BrickMax);

/// <summary>
/// Дым огня (фаза Transparent, только с HDR): объём на экземпляр газа — окно 3D-газа в мире; рэймарч по формуле HV LIVE-2 (<see cref="SmokeParams"/>,
/// CPU-эталон — <see cref="SmokeMath"/>) в цель ½ прозрачного (<see cref="HalfResTarget"/>, премультиплицированно, дальние объёмы раньше); марш
/// обрезает глубина ½ (ближайшая в блоке). Пропуск пустоты: сетка занятости — max ρ кирпича 4³ ячеек с запасом ячейки (проход
/// <see cref="BricksPassName"/>, только когда содержимое газа сменилось) и рамка непустых кирпичей (<see cref="BoundsPassName"/>); луч идёт
/// только в рамке, пустой кирпич перескакивает целиком — выборки те же, что у полного марша. Проход — бокс окна (36 вершин, задние грани:
/// пиксель под окном — один раз, и камера внутри окна тоже), марш — только в пикселях под ним.
/// Статистика — проход <see cref="StatsPassName"/>: тот же марш по сетке 64×36 лучей, асинхронное чтение → <see cref="Stats"/>; оверлей —
/// вклад ½ в LDR (цвет x/(1 + x), альфа). Кадр — 0 Б: массивы и делегаты — поля.
/// </summary>
internal sealed class FireSmoke : IDisposable
{
    public const string ShaderFile = "fire/fire_smoke.hlsl", PassName = "fire.smoke", StatsPassName = "fire.smoke.stats",
        BricksPassName = "fire.smoke.bricks", BoundsPassName = "fire.smoke.bounds";

    /// <summary>Сетка лучей статистики.</summary>
    public const int GridX = 64, GridY = 36, StatsGroup = 8, BoundsThreads = 256;

    /// <summary>Слов статистики: по три на луч (шагов, выборок, α) и рамка занятости первого объёма.</summary>
    private const int StatsWords = GridX * GridY * 3 + 8;

    private const int SlotData = 0, SlotIndex = 1, SlotCount = 2, SlotOut = 3, SlotColor = 4, SlotScalars = 5, SlotNx = 6, SlotNy = 7, SlotNz = 8,
        SlotBx = 9, SlotBy = 10, SlotBz = 11, SlotCut = 12, SlotBricks = 13;

    private static readonly GraphicsPipelineDesc Pipeline = new(ShaderFile, "VsSmokeBox", "PsSmoke", BlendMode.Premultiplied);
    private static readonly GraphicsPipelineDesc LayerPipeline = new(ShaderFile, "VsSmoke", "PsSmokeLayer", BlendMode.Premultiplied);
    private static readonly ComputePipelineDesc StatsPipeline = new(ShaderFile, "CsSmokeStats");
    private static readonly ComputePipelineDesc BricksPipeline = new(ShaderFile, "CsSmokeBricks");
    private static readonly ComputePipelineDesc BoundsPipeline = new(ShaderFile, "CsSmokeBounds");

    /// <summary>Проходы сетки занятости одного объёма: скаляры (SRV), кирпичи (UAV — запись, SRV — рамка), рамка (UAV), размеры, срез.</summary>
    private struct BrickArgs
    {
        public int Scalars, Bricks, BricksSrv, Bounds;
        public uint Nx, Ny, Nz, Bx, By, Bz;
        public float Cut;
    }

    private readonly FrameData<SmokeConstants> _constants;
    private readonly FrameData<SmokeLight> _lights;
    private readonly GpuStorage _stats;
    private readonly GpuReadback _readback;
    private readonly GpuStorage[] _bricks, _bounds;
    private readonly ulong[] _builtVersion;
    private readonly float[] _builtCut;
    private readonly int _maxBricks;
    private readonly Action<RenderPassContext> _record, _recordStats, _recordLayer, _recordBricks, _recordBounds;
    private readonly RenderBuffer[] _scalarBuffers, _velocityBuffers, _brickBuffers, _boundBuffers;
    private readonly BrickArgs[] _brickArgs;
    private readonly int[] _order;
    private readonly float[] _distance;
    private readonly ulong[] _statsFrames = new ulong[8];
    private int _count, _data, _statsUav, _layerColor, _brickCount, _bricksRun, _boundsRun;
    private ulong _frame = ulong.MaxValue;

    /// <param name="maxCells">Сетка газа (ячеек по осям) не больше: по ней — сетки занятости экземпляров.</param>
    public FireSmoke(Renderer renderer, int capacity, int maxLights, (int X, int Y, int Z) maxCells)
    {
        capacity = Math.Max(capacity, 1);
        _constants = renderer.CreateFrameData<SmokeConstants>("fire.smoke.constants", (uint)capacity);
        _lights = renderer.CreateFrameData<SmokeLight>("fire.smoke.lights", (uint)Math.Max(maxLights, 1));
        _stats = renderer.CreateStorage<uint>("fire.smoke.stats", StatsWords);
        _readback = renderer.CreateReadback(_stats, StatsWords * 4, OnStats);
        _maxBricks = SmokeMath.Bricks(maxCells.X) * SmokeMath.Bricks(maxCells.Y) * SmokeMath.Bricks(maxCells.Z);
        _bricks = new GpuStorage[capacity];
        _bounds = new GpuStorage[capacity];
        for (int i = 0; i < capacity; i++)
        {
            _bricks[i] = renderer.CreateStorage<float>($"fire.smoke.bricks{i}", (uint)_maxBricks);
            _bounds[i] = renderer.CreateStorage<uint>($"fire.smoke.bounds{i}", 8);
        }
        _builtVersion = new ulong[capacity];
        _builtCut = new float[capacity];
        Array.Fill(_builtVersion, ulong.MaxValue);
        _scalarBuffers = new RenderBuffer[capacity];
        _velocityBuffers = new RenderBuffer[capacity];
        _brickBuffers = new RenderBuffer[capacity];
        _boundBuffers = new RenderBuffer[capacity];
        _brickArgs = new BrickArgs[capacity];
        _order = new int[capacity];
        _distance = new float[capacity];
        _record = Record;
        _recordStats = RecordStats;
        _recordLayer = RecordLayer;
        _recordBricks = RecordBricks;
        _recordBounds = RecordBounds;
    }

    public SmokeParams Params { get; set; } = SmokeParams.Torch;

    /// <summary>
    /// Объёмов в последнем кадре с дымом; из них с детализирующим шумом (далёкие — без: <see cref="SmokeMath.MinDetailPixels"/>); источников
    /// рассеяния в нём (без солнца); сеток занятости, построенных в нём.
    /// </summary>
    public int LastVolumes { get; private set; }
    public int LastDetailed { get; private set; }
    public int LastLights { get; private set; }
    public int LastBuilt { get; private set; }

    /// <summary>Объёмов не больше.</summary>
    public int Capacity => (int)_constants.Capacity;

    /// <summary>Последняя статистика марша по сетке (приходит через <see cref="GpuReadback.Latency"/> кадров); null — ещё не было.</summary>
    public SmokeStats? Stats { get; private set; }

    /// <summary>Солнце (как источник) и небо (ambient) последнего кадра — для итога.</summary>
    public Vector3 LastSun { get; private set; }
    public Vector3 LastAmbient { get; private set; }

    /// <summary>Слоты и размеры структур в контракт шейдеров.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fire: smoke (Sword.Game.Fire.Client)")
        .AddSlot("gSmokeData", SlotData, "SRV констант объёмов кадра")
        .AddSlot("gSmokeIndex", SlotIndex, "номер объёма")
        .AddSlot("gSmokeCount", SlotCount, "объёмов (статистика)")
        .AddSlot("gSmokeOut", SlotOut, "UAV статистики, сетки занятости или рамки")
        .AddSlot("gSmokeColor", SlotColor, "SRV цели 1/2 (слой)")
        .AddSlot("gSmokeScalars", SlotScalars, "SRV скаляров газа (сетка занятости)")
        .AddSlot("gSmokeNx", SlotNx, "ячеек газа по x")
        .AddSlot("gSmokeNy", SlotNy, "ячеек газа по y")
        .AddSlot("gSmokeNz", SlotNz, "ячеек газа по z")
        .AddSlot("gSmokeBx", SlotBx, "кирпичей по x")
        .AddSlot("gSmokeBy", SlotBy, "кирпичей по y")
        .AddSlot("gSmokeBz", SlotBz, "кирпичей по z")
        .AddSlot("gSmokeCut", SlotCut, "срез плотности (float)")
        .AddSlot("gSmokeBricks", SlotBricks, "SRV сетки занятости (рамка)")
        .AddSize<SmokeConstants>("FIRE_SMOKE_CONSTANTS_SIZE")
        .AddSize<SmokeLight>("FIRE_SMOKE_LIGHT_SIZE")
        .Add("FIRE_SMOKE_GRID_X", GridX)
        .Add("FIRE_SMOKE_GRID_Y", GridY)
        .Add("FIRE_SMOKE_GROUP", StatsGroup)
        .Add("FIRE_SMOKE_BRICK", SmokeMath.Brick)
        .Add("FIRE_SMOKE_BOUNDS_THREADS", BoundsThreads);

    /// <summary>
    /// Проходы дыма кадра в цель ½ <paramref name="half"/>: сетки занятости сменившихся объёмов, объёмы <paramref name="volumes"/> от дальнего
    /// к ближнему, источники рассеяния <paramref name="lights"/>; затем проход статистики. Объёмов нет — ничего.
    /// </summary>
    public void Add(RenderFrame frame, in HalfResTarget half, in SmokeView view, ReadOnlySpan<SmokeVolume> volumes, ReadOnlySpan<SmokeLight> lights)
    {
        LastVolumes = 0;
        LastDetailed = 0;
        LastBuilt = 0;
        _frame = frame.Number;
        int count = Math.Min(volumes.Length, Capacity);
        if (count == 0) return;
        var g = frame.Graph;
        var p = Params;
        Matrix4x4.Invert(view.ViewProj, out var inv);
        int lightCount = Math.Min(lights.Length, (int)_lights.Capacity);
        var lightSlot = _lights.Slot;
        for (int i = 0; i < lightCount; i++) lightSlot[i] = lights[i];
        LastLights = lightCount;
        var sun = SmokeMath.SunRadiance(view.Sky.Sun);
        var ambient = SmokeMath.Ambient(view.Sky.Sky, p);
        LastSun = sun;
        LastAmbient = ambient;

        // Дальние раньше: слой ½ смешивается «поверх» в порядке рисования.
        for (int i = 0; i < count; i++)
        {
            _order[i] = i;
            _distance[i] = Vector3.DistanceSquared(volumes[i].Window.Center, view.Eye);
        }
        for (int i = 1; i < count; i++)
            for (int j = i; j > 0 && _distance[_order[j]] > _distance[_order[j - 1]]; j--)
                (_order[j], _order[j - 1]) = (_order[j - 1], _order[j]);

        _brickCount = _bricksRun = _boundsRun = 0;
        var slot = _constants.Slot;
        for (int i = 0; i < count; i++)
        {
            ref readonly var v = ref volumes[_order[i]];
            var w = v.Window;
            int bx = SmokeMath.Bricks(w.Nx), by = SmokeMath.Bricks(w.Ny), bz = SmokeMath.Bricks(w.Nz);
            if (bx * by * bz > _maxBricks) throw new ArgumentException($"окно газа {w.Nx}×{w.Ny}×{w.Nz} больше сетки дыма", nameof(volumes));
            var scalars = _scalarBuffers[i] = g.Import(v.Scalars);
            var velocity = _velocityBuffers[i] = g.Import(v.Velocity);
            var bricks = _brickBuffers[i] = g.Import(_bricks[v.Slot]);
            var bounds = _boundBuffers[i] = g.Import(_bounds[v.Slot]);
            if (_builtVersion[v.Slot] != v.Version || _builtCut[v.Slot] != p.DensityCut)
            {
                // Сетка занятости — только когда содержимое газа сменилось (шаг 60 Гц), а не каждый кадр.
                _brickArgs[_brickCount++] = new BrickArgs
                {
                    Scalars = g.SrvOf(scalars), Bricks = g.UavOf(bricks), BricksSrv = g.SrvOf(bricks), Bounds = g.UavOf(bounds), Nx = (uint)w.Nx,
                    Ny = (uint)w.Ny, Nz = (uint)w.Nz,
                    Bx = (uint)bx, By = (uint)by, Bz = (uint)bz, Cut = p.DensityCut,
                };
                g.AddComputePass(BricksPassName, FramePhase.Transparent).Read(scalars).Write(bricks).Execute(_recordBricks);
                g.AddComputePass(BoundsPassName, FramePhase.Transparent).Read(bricks).Write(bounds).Execute(_recordBounds);
                _builtVersion[v.Slot] = v.Version;
                _builtCut[v.Slot] = p.DensityCut;
                LastBuilt++;
            }
            var (noiseStep, noiseOrigin) = SmokeMath.DetailFrame(w.Min, w.CellSize, p);
            var flow = SmokeMath.Flow(v.Time, p);
            // Далёкий объём — без детализирующего шума: его решётка на экране мельче пары пикселей ½.
            float detail = p.Detail > 0 && SmokeMath.DetailPixels(view.ViewProj, view.Forward, w.Center, p.DetailScale, frame.Width) >= SmokeMath.MinDetailPixels
                ? p.Detail : 0;
            if (detail > 0) LastDetailed++;
            slot[i] = new SmokeConstants
            {
                ViewProj = view.ViewProj,
                InvViewProj = inv,
                Eye = view.Eye, StepCells = p.StepCells,
                Forward = view.Forward, MaxSteps = (uint)p.MaxSteps,
                BoxMin = w.Min, CellSize = w.CellSize,
                Nx = (uint)w.Nx, Ny = (uint)w.Ny, Nz = (uint)w.Nz, Scalars = (uint)g.SrvOf(scalars),
                Bx = (uint)bx, By = (uint)by, Bz = (uint)bz, Bricks = (uint)g.SrvOf(bricks),
                Sun = sun, Albedo = p.Albedo,
                Ambient = ambient, UnitsPerMetre = p.UnitsPerMetre,
                FogColour = view.Fog.Colour, Fog = view.Fog.Factor(Vector3.Distance(w.Center, view.Eye)),
                DensityMult = p.DensityMult, DensityPow = p.DensityPow, DensityCut = p.DensityCut, AmbientMult = p.AmbientMult,
                AmbientPow = p.AmbientPow, AmbientAoPow = p.AmbientAoPow, InvWidth = 1f / frame.Width, InvHeight = 1f / frame.Height,
                ViewDepth = half.ViewDepth, SceneDepth = (uint)half.DepthSrv, Bounds = (uint)g.SrvOf(bounds),
                Lights = (uint)_lights.Srv, LightCount = (uint)lightCount, Skip = p.SkipEmpty ? 1u : 0u, MinTransmittance = p.MinTransmittance,
                NoiseOrigin = noiseOrigin, NoiseStep = noiseStep,
                Detail = detail, FlowShift0 = flow.Shift0, FlowShift1 = flow.Shift1, FlowBlend = flow.Blend,
                Velocity = (uint)g.SrvOf(velocity),
            };
        }
        _count = count;
        _data = _constants.Srv;

        var pass = g.AddPass(PassName, FramePhase.Transparent).Color(half.Color).Read(half.Depth);
        for (int i = 0; i < count; i++) pass.Read(_scalarBuffers[i]).Read(_velocityBuffers[i]).Read(_brickBuffers[i]).Read(_boundBuffers[i]);
        pass.Execute(_record);

        var stats = g.Import(_stats);
        var statsPass = g.AddComputePass(StatsPassName, FramePhase.Transparent).Read(half.Depth).Write(stats);
        for (int i = 0; i < count; i++) statsPass.Read(_scalarBuffers[i]).Read(_velocityBuffers[i]).Read(_brickBuffers[i]).Read(_boundBuffers[i]);
        _statsUav = g.UavOf(stats);
        statsPass.Execute(_recordStats);
        _statsFrames[frame.Number % (ulong)_statsFrames.Length] = frame.Number;
        LastVolumes = count;
    }

    /// <summary>Кадр без дыма (выключен, огня нет): объёмов 0.</summary>
    public void Skip(RenderFrame frame)
    {
        LastVolumes = 0;
        LastDetailed = 0;
        LastBuilt = 0;
        _frame = frame.Number;
    }

    /// <summary>
    /// Вклад слоя ½ этого кадра (после <see cref="Add"/>) в LDR-цель <paramref name="target"/> полного размера: цвет x/(1 + x), альфа —
    /// экран поверх кадра. Дыма в кадре нет — ничего.
    /// </summary>
    public void AddLayer(RenderFrame frame, in HalfResTarget half, RenderTexture target, FramePhase phase, string name, Vector4? clear)
    {
        if (_frame != frame.Number || LastVolumes == 0) return;
        _layerColor = half.ColorSrv;
        frame.Graph.AddPass(name, phase).Color(target, clear).Read(half.Color).Execute(_recordLayer);
    }

    private void Record(RenderPassContext ctx)
    {
        ctx.SetPipeline(Pipeline);
        ctx.SetConstant(SlotData, _data);
        for (int i = 0; i < _count; i++)
        {
            ctx.SetConstant(SlotIndex, i);
            ctx.Draw(36);
        }
    }

    private void RecordBricks(RenderPassContext ctx)
    {
        ref readonly var a = ref _brickArgs[_bricksRun++];
        ctx.SetPipeline(BricksPipeline);
        ctx.SetConstant(SlotScalars, a.Scalars);
        ctx.SetConstant(SlotOut, a.Bricks);
        ctx.SetConstant(SlotNx, a.Nx);
        ctx.SetConstant(SlotNy, a.Ny);
        ctx.SetConstant(SlotNz, a.Nz);
        ctx.SetConstant(SlotBx, a.Bx);
        ctx.SetConstant(SlotBy, a.By);
        ctx.Dispatch(a.Bx, a.By, a.Bz);
    }

    private void RecordBounds(RenderPassContext ctx)
    {
        ref readonly var a = ref _brickArgs[_boundsRun++];
        ctx.SetPipeline(BoundsPipeline);
        ctx.SetConstant(SlotBricks, a.BricksSrv);
        ctx.SetConstant(SlotOut, a.Bounds);
        ctx.SetConstant(SlotBx, a.Bx);
        ctx.SetConstant(SlotBy, a.By);
        ctx.SetConstant(SlotBz, a.Bz);
        ctx.SetConstant(SlotCut, a.Cut);
        ctx.Dispatch(1);
    }

    private void RecordStats(RenderPassContext ctx)
    {
        ctx.SetPipeline(StatsPipeline);
        ctx.SetConstant(SlotData, _data);
        ctx.SetConstant(SlotCount, _count);
        ctx.SetConstant(SlotOut, _statsUav);
        ctx.Dispatch((GridX + StatsGroup - 1) / StatsGroup, (GridY + StatsGroup - 1) / StatsGroup);
    }

    private void RecordLayer(RenderPassContext ctx)
    {
        ctx.SetPipeline(LayerPipeline);
        ctx.SetConstant(SlotColor, _layerColor);
        ctx.Draw(3);
    }

    /// <summary>Данные сетки кадра <paramref name="frame"/>: (шагов, выборок, альфа) на луч и рамка занятости. Кадры без прохода статистики пропускаются.</summary>
    private void OnStats(ReadOnlySpan<byte> data, ulong frame)
    {
        if (_statsFrames[frame % (ulong)_statsFrames.Length] != frame) return;
        var cells = MemoryMarshal.Cast<byte, uint>(data);
        const int rays = GridX * GridY;
        var sum = new RaySum();
        for (int i = 0; i < rays; i++) sum.Add((int)cells[3 * i], (int)cells[3 * i + 1], BitConverter.UInt32BitsToSingle(cells[3 * i + 2]));
        var b = cells.Slice(3 * rays, 8);
        Stats = new SmokeStats(frame, rays, sum.Hit, sum.Hit > 0 ? (double)sum.Steps / sum.Hit : 0, sum.MaxSteps,
            sum.Hit > 0 ? (double)sum.Samples / sum.Hit : 0, sum.MaxSamples, (double)sum.Covered / rays, sum.AlphaSum / rays, sum.MaxAlpha,
            (int)b[6], (int)b[7], ((int)b[0], (int)b[1], (int)b[2]), ((int)b[3], (int)b[4], (int)b[5]));
    }

    /// <summary>Сумма по лучам сетки: тело цикла по 2304 лучам каждого кадра — свой метод (правило OSR).</summary>
    private struct RaySum
    {
        public int Hit, MaxSteps, MaxSamples, Covered;
        public long Steps, Samples;
        public double AlphaSum;
        public float MaxAlpha;

        public void Add(int steps, int samples, float alpha)
        {
            if (steps > 0)
            {
                Hit++;
                Steps += steps;
                Samples += samples;
                MaxSteps = Math.Max(MaxSteps, steps);
                MaxSamples = Math.Max(MaxSamples, samples);
            }
            if (alpha >= 1f / 255) Covered++;
            AlphaSum += alpha;
            MaxAlpha = MathF.Max(MaxAlpha, alpha);
        }
    }

    public void Dispose()
    {
        _readback.Dispose();
        _stats.Dispose();
        foreach (var b in _bricks) b.Dispose();
        foreach (var b in _bounds) b.Dispose();
        _lights.Dispose();
        _constants.Dispose();
    }
}
