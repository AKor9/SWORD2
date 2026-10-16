using System.Numerics;

namespace Sword.Engine.Fluid3D;

/// <summary>Адвекция плотности и температуры.</summary>
public enum Fluid3DAdvection
{
    /// <summary>Полулагранжева, трилинейная выборка (один проход).</summary>
    SemiLagrangian,

    /// <summary>MacCormack с ограничителем по восьми углам (два прохода): резче, откат к полулагранжевой вне диапазона углов.</summary>
    MacCormack,
}

/// <summary>
/// Параметры 3D-газа (README.md): сетка, шаг, силы, давление, затухание. Единицы — метры и секунды: формулы шаблона Epic в сантиметрах
/// переходят без множителей (g 980 см/с² = 9.8 м/с², сила вихрей ε·dx·(N × ω) — в единицах dx). Умолчания — шаблон
/// <c>Grid3D_MovingGridFire</c> (α −0.5, β 0.5, ε 2.5, ρ: k 1, c 0; T: k 1, c 0.05) на сетке 3 см; давление — 10 полупроходов SOR с тёплым
/// стартом и весом 1.7 (у Epic — 6 с нуля и вес по формуле).
/// Смена размеров сетки — новый сим; остальное — на ходу.
/// </summary>
public sealed record Fluid3DParams
{
    /// <summary>Умолчания дыма факела.</summary>
    public static Fluid3DParams Smoke { get; } = new();

    /// <summary>Ячеек по осям мира x, y (вверх), z: 96×128×96 по 3 см — 2.88×3.84×2.88 м, шаг ≈0.28 мс GPU (RTX 5070, README).</summary>
    public int Nx { get; init; } = 96;
    public int Ny { get; init; } = 128;
    public int Nz { get; init; } = 96;

    /// <summary>Сторона ячейки, м.</summary>
    public float CellSize { get; init; } = 0.03f;

    /// <summary>Время шага, с.</summary>
    public float StepSeconds { get; init; } = 1f / 60f;

    /// <summary>α плавучести F = (α·ρ − β·T)·g: отрицательная — плотность поднимает (дым), положительная — тянет вниз.</summary>
    public float DensityBuoyancy { get; init; } = -0.5f;

    /// <summary>β плавучести: положительная — тёплый газ поднимается.</summary>
    public float TemperatureBuoyancy { get; init; } = 0.5f;

    /// <summary>Ускорение свободного падения в мировых осях, м/с² (y — вверх).</summary>
    public Vector3 Gravity { get; init; } = new(0, -9.8f, 0);

    /// <summary>ε вихревого удержания (0 — выкл.).</summary>
    public float VorticityConfinement { get; init; } = 2.5f;

    /// <summary>Полупроходов red-black SOR за шаг (у Epic Fire 10, MovingGridFire 6); 0 — без давления.</summary>
    public int PressureIterations { get; init; } = 10;

    /// <summary>
    /// Вес SOR; null — формула Epic ω = 2/(1 + √(1 − ρ²)), ρ = (cos π/Nx + cos π/Ny + cos π/Nz)/3, в [1, 1.99999]. Умолчание 1.7: тёплый
    /// старт устойчив, пока (ω − 1)^(<see cref="PressureIterations"/>/2) &lt; ⅓ (<see cref="Validate"/>; ручкам — <see cref="WithStablePressure"/>):
    /// при 1.7 не меньше 7 полупроходов; вес по формуле (1.94 на 96×128×96) раскачивает давление от шага к шагу (README, «Отступления»).
    /// </summary>
    public float? SorWeight { get; init; } = 1.7f;

    /// <summary>Давление шага начинается с итога прошлого (сдвинутого вместе с окном), а не с нуля (у Epic — с нуля).</summary>
    public bool PressureWarmStart { get; init; } = true;

    /// <summary>Затухание плотности: s/(1 + k·dt) − c·dt.</summary>
    public float DensityDecay { get; init; } = 1f;
    public float DensityDissipation { get; init; }

    /// <summary>Остывание: T/(1 + k·dt) − c·dt.</summary>
    public float TemperatureDecay { get; init; } = 1f;
    public float TemperatureDissipation { get; init; } = 0.05f;

    /// <summary>Предел |v| после проекции, м/с.</summary>
    public float MaxSpeed { get; init; } = 6f;

    public Fluid3DAdvection ScalarAdvection { get; init; } = Fluid3DAdvection.MacCormack;

    /// <summary>
    /// Вихревой шум (<see cref="Fluid3DCurlNoise"/>): ускорение A·curl ψ, в среднем по объёму A м/с² (0 — выкл.). ψ — в мировых координатах:
    /// узор стоит в мире, окно его не двигает; бездивергентен, давление его не гасит. У Epic нет; по смыслу — <c>VeloDirNoise</c> 2D-сима
    /// LIVE-2 (README, «Вихревой шум»).
    /// </summary>
    public float CurlNoise { get; init; }

    /// <summary>Шаг решётки шума, м: вихри размером ≈ половина шага (на сетке 3 см мельче ≈4 ячеек — не разрешается).</summary>
    public float CurlNoiseScale { get; init; } = 0.2f;

    /// <summary>Узор шума поднимается в мире со скоростью, м/с (время — номер шага × <see cref="StepSeconds"/>): вихри уходят вверх вместе с дымом.</summary>
    public float CurlNoiseRise { get; init; }

    /// <summary>Плотность полной силы шума: сила × min(ρ/это, 1) — шум мешает только дым, а не воздух всего окна; 0 — везде.</summary>
    public float CurlNoiseDensity { get; init; }

    /// <summary>
    /// Ветер — однородная скорость воздуха в мире, м/с: газ переносится скоростью поля плюс ветер (адвекция скорости и скаляров); поле хранит
    /// скорость относительно воздуха. Однородное поле бездивергентно — давление его не видит; 0 — шаг бит в бит как без ветра.
    /// </summary>
    public Vector3 Wind { get; init; }

    /// <summary>Без проверки устойчивости тёплого старта — только контроли тестов, которые показывают раскачку.</summary>
    internal bool UncheckedPressure { get; init; }

    public int Cells => Nx * Ny * Nz;

    /// <summary>Размер сетки в мире, м.</summary>
    public Vector3 Size => new(Nx * CellSize, Ny * CellSize, Nz * CellSize);

    /// <summary>Вес SOR, который действует (<see cref="SorWeight"/> или формула Epic).</summary>
    public float SorWeightValue
    {
        get
        {
            if (SorWeight is { } w) return w;
            double rho = (Math.Cos(Math.PI / Nx) + Math.Cos(Math.PI / Ny) + Math.Cos(Math.PI / Nz)) / 3;
            double omega = 2 / (1 + Math.Sqrt(1 - rho * rho));
            return (float)Math.Clamp(Math.Clamp(omega - 1, 0, 1) + 1, 0, 1.99999);
        }
    }

    /// <summary>
    /// Тёплый старт давления устойчив: (ω − 1)^(полупроходов/2) &lt; ⅓. Без тёплого старта, без давления и при ω ≤ 1 — всегда: итог прошлого
    /// шага не раскачивается.
    /// </summary>
    public static bool IsPressureStable(float sorWeight, int halfPasses, bool warmStart) =>
        !warmStart || halfPasses <= 0 || sorWeight <= 1 || PressureSwing(sorWeight, halfPasses) < 1.0 / 3;

    /// <summary>(ω − 1)^(полупроходов/2) — доля ошибки тёплого старта, которая остаётся после шага.</summary>
    public static double PressureSwing(float sorWeight, int halfPasses) => Math.Pow(sorWeight - 1.0, halfPasses / 2.0);

    public bool PressureStable => IsPressureStable(SorWeightValue, PressureIterations, PressureWarmStart);

    /// <summary>
    /// Параметры в устойчивой области давления (ручки): вес SOR уменьшен до наибольшего устойчивого при тех же полупроходах
    /// (1 + (⅓)^(2/N) без последнего ULP float); устойчивые — те же.
    /// </summary>
    public Fluid3DParams WithStablePressure()
    {
        if (PressureStable) return this;
        float w = (float)(1 + Math.Pow(1.0 / 3, 2.0 / PressureIterations));
        while (!IsPressureStable(w, PressureIterations, true)) w = MathF.BitDecrement(w);
        return this with { SorWeight = w };
    }

    public void Validate()
    {
        if (Nx is < 4 or > 512 || Ny is < 4 or > 512 || Nz is < 4 or > 512)
            throw new ArgumentOutOfRangeException(nameof(Nx), $"{Nx}×{Ny}×{Nz}", "ячеек по оси 4…512");
        if ((long)Nx * Ny * Nz > 16_777_216) throw new ArgumentOutOfRangeException(nameof(Nx), $"{Nx}×{Ny}×{Nz}", "ячеек не больше 2²⁴");
        if (!(CellSize > 0)) throw new ArgumentOutOfRangeException(nameof(CellSize), CellSize, "ячейка > 0");
        if (!(StepSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(StepSeconds), StepSeconds, "время шага > 0");
        if (!float.IsFinite(DensityBuoyancy) || !float.IsFinite(TemperatureBuoyancy) || !float.IsFinite(Gravity.Length()))
            throw new ArgumentOutOfRangeException(nameof(Gravity), "плавучесть — конечные числа");
        if (!(VorticityConfinement >= 0)) throw new ArgumentOutOfRangeException(nameof(VorticityConfinement), VorticityConfinement, "ε ≥ 0");
        if (PressureIterations is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(PressureIterations), PressureIterations, "полупроходов 0…64");
        if (SorWeight is { } w && !(w > 0 && w < 2)) throw new ArgumentOutOfRangeException(nameof(SorWeight), w, "вес SOR в (0, 2)");
        if (!PressureStable && !UncheckedPressure)
        {
            float omega = SorWeightValue;
            float max = WithStablePressure().SorWeightValue;
            throw new ArgumentOutOfRangeException(nameof(SorWeight), omega,
                $"тёплый старт давления раскачивается: (ω − 1)^(полупроходов/2) = {PressureSwing(omega, PressureIterations):G3} ≥ ⅓ при ω {omega} и " +
                $"{PressureIterations} полупроходах — вес не больше {max}, больше полупроходов или без тёплого старта (WithStablePressure)");
        }
        if (!(DensityDecay >= 0) || !(DensityDissipation >= 0) || !(TemperatureDecay >= 0) || !(TemperatureDissipation >= 0))
            throw new ArgumentOutOfRangeException(nameof(DensityDecay), "затухание ≥ 0");
        if (!(MaxSpeed > 0)) throw new ArgumentOutOfRangeException(nameof(MaxSpeed), MaxSpeed, "предел скорости > 0");
        if (!(CurlNoise >= 0) || !float.IsFinite(CurlNoise) || !(CurlNoiseScale > 0) || !float.IsFinite(CurlNoiseScale) ||
            !float.IsFinite(CurlNoiseRise) || !(CurlNoiseDensity >= 0) || !float.IsFinite(CurlNoiseDensity))
            throw new ArgumentOutOfRangeException(nameof(CurlNoise), "вихревой шум: сила ≥ 0, шаг > 0, подъём конечен, плотность ≥ 0");
        if (!float.IsFinite(Wind.X + Wind.Y + Wind.Z) || !(Wind.Length() <= MaxSpeed))
            throw new ArgumentOutOfRangeException(nameof(Wind), Wind, "ветер конечен и не быстрее предела скорости");
    }
}
