using System.Numerics;

namespace Sword.Engine.Fluid2D;

/// <summary>
/// Параметры 2D-сима LIVE-2 — данные, а не код эффекта: огонь, кровь и песок отличаются только ими. Имена — как у параметров
/// LIVE-2 (FSR §3.4–§3.5, BR §6); умолчания — факел LIVE-2 (FSR §3.4б). Смена <see cref="Resolution"/> или
/// <see cref="PaintBufferDownScaleFactor"/> — новый сим; остальное меняется на ходу, со следующего шага.
/// </summary>
public sealed record Fluid2DParams
{
    /// <summary>Факел LIVE-2 (FSR §3.4б, §3.5).</summary>
    public static Fluid2DParams Torch { get; } = new();

    /// <summary>
    /// Вязкая кровь на полу (B1 LIVE-2, BVR §2.2, §2.7, пересчёт на сетку SWORD2 — BVR §6.8 с BR §9.2): горизонтальная площадка 32×32 м, 1024²
    /// (тексель 3.125 см), кисть без огрубления, SimSpeed 3 (d = 9.4 см); VF 0.9, DA — нейтраль 2/VF − 1 (масса не растёт и не тает), давление
    /// без памяти (KIO −2, DM 0.04); мировые числа L2 (Kill 2 м/с, ячейка шума скорости 6.25 см, период шума плотности 4 м, полосы краёв в
    /// метрах) — те же, доли стороны пересчитаны на 32 м; канал возраста включён. Выбор чисел, которых нет в BVR, — README.
    /// </summary>
    public static Fluid2DParams Blood { get; } = new()
    {
        Resolution = 1024,
        PaintBufferDownScaleFactor = 1,
        SimSpeed = 3,
        VelocityFeedback = 0.9f,
        VelocityClamp = 50,
        DensityAccumulation = 2f / 0.9f - 1f,
        EdgeMaskWidth = 0.03125f,
        EdgeMaskPow = 1,
        SimEdgeBounciness = 0,
        WetmapFeedback = 0.93f,
        WetMapEdgeMaskWidth = 0.0625f,
        VeloFromBrushMotion = 10,
        DirectVelocity = Vector3.Zero,
        VeloAmplitudeNoiseEnabled = false,
        VeloDirNoise = 100,
        VeloDirNoiseSize = 0.125f,
        VeloDirNoiseSpeed = 0,
        MaskDirNoiseWithSimVelocity = 1,
        DensityNoiseAmount = 1,
        DensityNoiseTile = 7,
        DensityNoiseSpeed = 0,
        DensityFloor = 0,
        DivergenceMult = 0.04f,
        BrushPuncture = 0,
        KernelMult = 1,
        KernelIndexOffset = -2,
        PressureEdgeMasking = true,
        PressureEdgeMaskWidth = 0.09375f,
        PressureEdgeMaskPow = 1,
        Extents = new Vector3(32, 32, 0.65f),
        CameraFacing = false,
        BrushPositionRandom = 0,
        BrushMaxPoints = 256,
        BrushMaxEntries = 4096,
        BrushHardness = 0.5f,
        KillBrushBelowThisVelocity = 0.1f,
        BrushDensityNoise = 0.4f,
        BrushVelocityMultXY = 4,
        KillInterpolatorAboveThisVelocity = 100,
        PersistencyOfFieldAndPointData = 0.05f,
        PainterEdgeMaskWidth = 0.15625f,
        DensityTxtMult = 0,
        AgeChannel = true,
        HalfRoundToNearest = true,
    };

    /// <summary>
    /// Кровь, которая растекается и течёт по уклону (A′, BVR §7): <see cref="Blood"/> + консервативный перенос ρ, толщина h = ρ·h_max без
    /// потолка 1, трение плёнки от толщины, пороги h_min и H, растекание по ∇(рельеф + h). Накопление 1 (масса — только из кисти и штампов),
    /// давление выключено (растекание — это дивергенция). Подбор чисел — README («Плёнка»).
    /// </summary>
    public static Fluid2DParams BloodSpread { get; } = Blood with
    {
        VelocityFeedback = 1,
        DensityAccumulation = 1,
        DivergenceMult = 0,
        ConservativeDensity = true,
        UnboundedDensity = true,
        FilmFlow = true,
        FilmSpread = 10,
    };

    // ── Сетка и такт ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Сторона гридов, текселей (факел — 368: тексель 0.815 см на площадке 3 м).</summary>
    public int Resolution { get; init; } = 368;

    /// <summary>Во сколько раз грид кисти грубее сима (факел — 2: кисть 184²).</summary>
    public int PaintBufferDownScaleFactor { get; init; } = 2;

    /// <summary>Шаг соседей и адвекции, текселей: адвекция uv − v·SimSpeed/N, соседи дивергенции, давления, градиента — через SimSpeed.</summary>
    public float SimSpeed { get; init; } = 2;

    /// <summary>Время шага, с: t шумов = номер шага × StepSeconds (60 Гц, как L2), а не часы кадра.</summary>
    public double StepSeconds { get; init; } = 1.0 / 60;

    // ── Composite ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Множитель скорости и плотности в адвекции (за шаг).</summary>
    public float VelocityFeedback { get; init; } = 0.99f;

    /// <summary>Предел каждой компоненты скорости, ед. (1 ед. = SimSpeed текселей за шаг).</summary>
    public float VelocityClamp { get; init; } = 4;

    /// <summary>Накопление плотности: ×(0.5 + 0.5·DA·max(e^pow, 0.7)) за шаг (в глубине площадки 0.95 у факела).</summary>
    public float DensityAccumulation { get; init; } = 0.9f;

    /// <summary>Полоса спада плотности у края площадки (доля полуширины) и её степень.</summary>
    public float EdgeMaskWidth { get; init; } = 0.05f;

    public float EdgeMaskPow { get; init; } = 0.5f;

    /// <summary>Упругость края: скорость на крайнем кольце ×(1 − √b) (факел — 0.68).</summary>
    public float SimEdgeBounciness { get; init; } = 0.1f;

    /// <summary>Нижняя граница маски столкновений скорости.</summary>
    public float WeakenCollisionMask { get; init; }

    /// <summary>Мокрая карта: wet' = (0.982·wet + (1 − WetmapFeedback)·ρ')·маска края шириной WetMapEdgeMaskWidth.</summary>
    public float WetmapFeedback { get; init; } = 0.93f;

    public float WetMapEdgeMaskWidth { get; init; } = 0.1f;

    /// <summary>Скорость кисти: слои 0, 1 × 20·VeloFromBrushMotion.</summary>
    public float VeloFromBrushMotion { get; init; } = 1.5f;

    /// <summary>Множитель скорости из битмапа (слои кисти 4, 5; <c>VeloFromBitmaps</c>).</summary>
    public float VeloStrength { get; init; } = 1;

    /// <summary>Ластик (<c>InvertFieldAndPointDensity</c>): кисть вычитает плотность и толкает и по пустому полю.</summary>
    public float EraserStrength { get; init; }

    /// <summary>Инерция от движения площадки: in −= VeloFromSimAreaMotion·<see cref="Fluid2DStepInput.VelocityOffsetDelta"/>.</summary>
    public float VeloFromSimAreaMotion { get; init; }

    /// <summary>
    /// Постоянный ввод скорости (∝ плотности). В мировых осях вызывающего при <see cref="DirectVelocityInWorldSpace"/>
    /// (переводится поворотом площадки), иначе — в осях площадки: x — вдоль u, y — вверх по площадке (v растёт вниз).
    /// Факел: подъём 0.2 и снос 0.1 (FSR §3.5: UE (0, 0.1, 0.2) = оси SWORD2 (−0.1, 0.2, 0), Y — вверх).
    /// </summary>
    public Vector3 DirectVelocity { get; init; } = new(-0.1f, 0.2f, 0);

    public bool DirectVelocityInWorldSpace { get; init; } = true;

    /// <summary>Amp-шум скорости: in ×saturate(1 + шум − VeloAmpNoise); UV (uv + WO)·Size + t·Speed·(0.5, 0.2).</summary>
    public bool VeloAmplitudeNoiseEnabled { get; init; } = true;

    public float VeloAmpNoise { get; init; } = 0.25f;
    public float VeloAmpNoiseSize { get; init; } = 1;
    public float VeloAmpNoiseSpeed { get; init; } = 1;

    /// <summary>Направленный шум скорости: 0.005·VeloDirNoise·симплекс((uv + WO)·128·0.5/Size, t·Speed) (факел — ±0.05 ед.).</summary>
    public float VeloDirNoise { get; init; } = 10;

    public float VeloDirNoiseSize { get; init; } = 0.5f;
    public float VeloDirNoiseSpeed { get; init; } = 0.1f;

    /// <summary>1 — шум только там, где поле движется (множитель max(min(0.2|v|², 1), 1 − Mask)).</summary>
    public float MaskDirNoiseWithSimVelocity { get; init; }

    /// <summary>Шум плотности: ввод кисти ×(1 + Amount·(шум − 0.5)); UV (uv + WO)·(Tile + 1) + t·Speed·(0.5, 1).</summary>
    public bool DensityNoiseEnabled { get; init; } = true;

    public float DensityNoiseAmount { get; init; } = 1;
    public float DensityNoiseTile { get; init; }
    public float DensityNoiseSpeed { get; init; } = 0.2f;

    /// <summary>Сдвиг UV обоих текстурных шумов (и их пана ×0.1·t).</summary>
    public float RandomizeNoiseOffsets { get; init; }

    /// <summary>Нижняя граница плотности после Composite (литерал L2 1e-6); 0 — чистый ноль.</summary>
    public float DensityFloor { get; init; } = 1e-6f;

    /// <summary>
    /// Канал возраста E (float32 на тексель, секунды): переносится выборкой адвекции, взвешенной по ρ; растёт на <see cref="StepSeconds"/> за
    /// шаг; приток омолаживает: age' = (age + dt)·ρ_без притока/ρ_новая; где ρ = 0 — 0. Меняется только с новым симом.
    /// </summary>
    public bool AgeChannel { get; init; }

    /// <summary>
    /// C и A пишутся в half с округлением к ближайшему (как эталон); иначе — f32tof16 железа, к нулю (огонь). Нужен балансу массы при
    /// нейтральном накоплении: к нулю лужа в покое теряет 0.26 % за 600 шагов, к ближайшему — 0.0001 %.
    /// </summary>
    public bool HalfRoundToNearest { get; init; }

    // ── Плёнка (A′) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Консервативный перенос ρ: Advection — потоки через грани (донорская схема, скорость грани — среднее двух текселей, у края грида —
    /// стенка) вместо полулагранжевой выборки, ρ не умножается на VelocityFeedback; возраст — те же потоки × возраст донора. Скорость —
    /// прежней выборкой; Composite зажимает её по CFL (<see cref="FilmCourant"/>). Масса сохраняется до округления half.
    /// </summary>
    public bool ConservativeDensity { get; init; }

    /// <summary>ρ без потолка 1 (только ρ ≥ 0): ρ — толщина h = ρ·<see cref="FilmThicknessPerDensity"/>.</summary>
    public bool UnboundedDensity { get; init; }

    /// <summary>Толщина плёнки на ρ = 1 (h_max), м.</summary>
    public float FilmThicknessPerDensity { get; init; } = 0.003f;

    /// <summary>
    /// Течение плёнки в Composite: тоньше <see cref="FilmMinThickness"/> — v = 0; иначе v − g·∇(рельеф + max(h − H, 0))·dt (высоты —
    /// <see cref="Fluid2DSim.Heights"/>; нет — уклон 0), затем трение Пуазейля неявно v/(1 + k·dt/h²).
    /// </summary>
    public bool FilmFlow { get; init; }

    /// <summary>Трение плёнки k (= 3ν у настоящей), м²/с: установившаяся скорость на уклоне — g·sinα·h²/k.</summary>
    public float FilmFriction { get; init; } = 1e-4f;

    /// <summary>h_min, м: тоньше не течёт (мазок стоит).</summary>
    public float FilmMinThickness { get; init; } = 0.0003f;

    /// <summary>H, м: толщина лужи — растекание собственным весом идёт только от избытка max(h − H, 0).</summary>
    public float FilmPuddleThickness { get; init; } = 0.001f;

    /// <summary>Ускорение тяжести плёнки, м/с² (масштаб силы).</summary>
    public float FilmGravity { get; init; } = 9.81f;

    /// <summary>
    /// Множитель растекания собственным весом (члена ∇max(h − H, 0)), уклон рельефа — без него: лужа быстрее выходит на толщину H, а скорость
    /// на склоне (g·sinα·h²/k) не меняется.
    /// </summary>
    public float FilmSpread { get; init; } = 1;

    /// <summary>Предел компоненты скорости при консервативном переносе, текселей за шаг (CFL: сумма по четырём граням ≤ 4·Courant ≤ 1).</summary>
    public float FilmCourant { get; init; } = 0.25f;

    // ── Divergence и Pressure ───────────────────────────────────────────────────────────────────────────────────

    public float DivergenceMult { get; init; } = 1;

    /// <summary>Прокол: дивергенция +0.01·BrushPuncture·плотность кисти (ближайший тексель).</summary>
    public float BrushPuncture { get; init; } = 0.05f;

    /// <summary>Грубый решатель (<c>EnableCrudePressureSolver</c>): один проход давления за шаг, X и Y попеременно; DivergenceMult ≤ 0.5.</summary>
    public bool CrudePressureSolver { get; init; }

    /// <summary>Размер ядра давления: clamp(15·KernelMult, 5, 15) → 15/13/11/9/7/5 тапов; KernelIndexOffset сдвигает ряд весов P.</summary>
    public float KernelMult { get; init; } = 1;

    public float KernelIndexOffset { get; init; }
    public float PressureFeedback { get; init; } = 1;
    public float DivergenceFeedback { get; init; } = 1;

    /// <summary>Маска давления у краёв (у факела выкл.; ширина и степень — числа крови BVR, для факела не сверены).</summary>
    public bool PressureEdgeMasking { get; init; }

    public float PressureEdgeMaskWidth { get; init; } = 0.15f;
    public float PressureEdgeMaskPow { get; init; } = 1;

    // ── Export ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Выход: ρ^Contrast; InvertFluidDensity — 1 − ρ^Contrast; CopyInvertedDensityToAlpha — альфа = 1 − ρ^Contrast.</summary>
    public float FluidDensityContrast { get; init; } = 1;

    public bool InvertFluidDensity { get; init; }
    public bool CopyInvertedDensityToAlpha { get; init; }

    // ── Кисть: площадка и точки (CPU, BR §3.1, §8.2) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Площадка E, м: x, y — стороны (UV), z — полоса отбора точек ±z/2 вдоль нормали (у площадки к камере — по глубине). Факел — 3×3×3.
    /// Квадратный тексель кисти — только при x = y (грид квадратный).
    /// </summary>
    public Vector3 Extents { get; init; } = new(3, 3, 3);

    /// <summary>
    /// Площадка к камере (<c>CameraFacing</c>): скорость в UV — 0.1·(R⁻¹V)/E (факел: 1 м/с → 0.0333), позиция — перспективный перенос на
    /// глубину центра, prev отрезка — прошлая позиция точки с её джиттером. Иначе — 0.05·R⁻¹V (1 м/с → 0.05) и prev — прошлая мировая
    /// позиция в текущих осях площадки.
    /// </summary>
    public bool CameraFacing { get; init; } = true;

    /// <summary>Джиттер точек-массивов: (hash(зерно, TickId, ключ) − 0.5)·20·BPR см по каждой оси; при флаге ×min(5·|UV.xy|, 1).</summary>
    public float BrushPositionRandom { get; init; } = 0.5f;

    public bool BrushRndScaledByVelocity { get; init; }

    /// <summary>Точек на шаг после отбора (лишние — «отброшено» в статистике кисти); записей бининга — не меньше точек.</summary>
    public int BrushMaxPoints { get; init; } = 64;

    public int BrushMaxEntries { get; init; } = 1024;

    // ── Кисть: Painter (BR §4.2, §7.1; умолчания — факел BR §7.2) ───────────────────────────────────────────────

    /// <summary><c>GlobalBrushSize</c>: σ мазка = 9.496·s·BrushSize·stretch см.</summary>
    public float BrushSize { get; init; } = 1;

    /// <summary>Сила: плотность мазка = s_h·kv·BrushStrength·(1 + 0.1·pz); меньше 0 — ластик.</summary>
    public float BrushStrength { get; init; } = 1;

    /// <summary>Жёсткость края: hs = clamp(BrushHardness + min(BrushHardnessScaledByObjSize·s, 0.5), 0, 0.95), s_h = smoothstep.</summary>
    public float BrushHardness { get; init; }

    public float BrushHardnessScaledByObjSize { get; init; }

    /// <summary>Ниже этой |UV| скорости плотность ×|UV|^1.5 (0 — стоящая точка рисует).</summary>
    public float KillBrushBelowThisVelocity { get; init; }

    /// <summary>Только скорость по классу источника: 1 — массивы, 2 — частицы, 3 — обломки, 4 — 1 и 3, 5 — все.</summary>
    public int SelectivelyKillBrushDensityKeepVelocity { get; init; }

    /// <summary>Ослабление по высоте над плоскостью: f ×(1 − sat(|U.z − 0.5|·10/FadeBrushByZPosMult)).</summary>
    public bool FadeBrushByZPos { get; init; }

    public float FadeBrushByZPosMult { get; init; } = 1;

    /// <summary>Шумы кисти в мировых координатах (uv + WorldOffset), иначе — 0.5·uv.</summary>
    public bool BrushNoiseInWorldSpace { get; init; } = true;

    /// <summary>Шум плотности кисти (канал R <c>T_NoiseTemplate2</c>): dn = max(1 − BDN, R^Pow), узор меняется Freq раз в секунду на 0.1 UV.</summary>
    public float BrushDensityNoise { get; init; } = 0.05f;

    public float BrushDensityNoiseScale { get; init; } = 3;
    public float BrushDensityNoiseFreq { get; init; } = 40;
    public float BrushDensityNoisePow { get; init; } = 2;

    /// <summary>Шум скорости кисти (RG <c>T_NoiseTemplate2</c>): 0.1·BVN·(RG − 0.5), только в радиальном члене прокола.</summary>
    public float BrushVelocityNoise { get; init; } = 0.5f;

    public float BrushVelocityNoiseFreq { get; init; } = 0.1f;
    public float BrushVelocityNoiseScale { get; init; } = 30;

    /// <summary>Толчок: sign·|clamp(UV, ±Clamp)|^Pow покомпонентно, итог зажат ±Clamp и ×MultXY; MultZ — «удар» вертикальной скоростью.</summary>
    public float BrushVelocityMultXY { get; init; } = 1;

    public float BrushVelocityMultZ { get; init; } = 1;
    public float BrushVelocityPow { get; init; } = 1.6f;
    public float BrushVelocityClamp { get; init; } = 2;

    /// <summary>Прокол со знаком вертикальной скорости (втягивание).</summary>
    public bool NegativePuncture { get; init; }

    /// <summary>Вес скорости по классу источника: массивы и частицы.</summary>
    public float ArrayBrushVelocity { get; init; } = 1;

    public float ParticleBrushVelocity { get; init; } = 1;

    /// <summary>Мазки ячейки складываются (иначе — слияние: плотность по наибольшему |ρ|, скорость средневзвешенная по s_h).</summary>
    public bool ForceAdditiveStrokeBlending { get; init; }

    /// <summary>Мазок отрезком prev→pos (после 0.25 с с активации; выключается при |UV| &gt; KillInterpolatorAboveThisVelocity).</summary>
    public bool PosInterpolation { get; init; } = true;

    public float KillInterpolatorAboveThisVelocity { get; init; } = 10;

    /// <summary>Память краски IF: b = (1 − IF)³ — пол α и затухание (краска держится 1 − b за шаг); IF &gt; 0.5 заостряет профиль скорости.</summary>
    public float PersistencyOfFieldAndPointData { get; init; } = 0.2f;

    /// <summary>Маска края кисти: ≤ 0.05 — ступень (0 во внешней полосе 0.505·w стороны), иначе m^0.1.</summary>
    public float PainterEdgeMaskWidth { get; init; } = 0.05f;

    /// <summary>Режим painter без сима: мазки складываются, Paint = Σ·A^0.25 + old·(1 − A).</summary>
    public bool SimplePainterMode { get; init; }

    /// <summary>Битмап кисти (слои 4–6): выборка в (uv + WorldOffset)·(Scale + 1) + Offset·t + RandomOffset; слой 6 = 0.5·Mult·td·(1 − sat(ρ)).</summary>
    public float DensityTxtMult { get; init; } = 0.5f;

    public float DensityTxtOffsetX { get; init; }
    public float DensityTxtOffsetY { get; init; }
    public float DensityTxtRandomOffset { get; init; }
    public float DensityTxtScale { get; init; }

    /// <summary>Битмап — только плотность из R (слои 4, 5 = 0); иначе скорость — RG, плотность — B.</summary>
    public bool UseInputTextureOnlyAsDensity { get; init; }

    /// <summary>Сторона грида кисти.</summary>
    public int BrushResolution => Resolution / PaintBufferDownScaleFactor;

    /// <summary>Шумовая текстура кисти нужна (шум скорости или плотности кисти включён).</summary>
    public bool BrushNoiseUsed => BrushVelocityNoise != 0 || BrushDensityNoise != 0;

    /// <summary>Проверка чисел — до создания ресурсов.</summary>
    public void Validate()
    {
        if (Resolution is < 8 or > 2048) throw new ArgumentOutOfRangeException(nameof(Resolution), Resolution, "сторона грида 8…2048");
        if (PaintBufferDownScaleFactor is not (1 or 2 or 4) || Resolution % PaintBufferDownScaleFactor != 0)
            throw new ArgumentOutOfRangeException(nameof(PaintBufferDownScaleFactor), PaintBufferDownScaleFactor,
                $"грид кисти — 1, 2 или 4 и делит сторону {Resolution}");
        if (!(SimSpeed > 0) || SimSpeed > 64) throw new ArgumentOutOfRangeException(nameof(SimSpeed), SimSpeed, "шаг соседей 0…64 текселей");
        if (!(StepSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(StepSeconds), StepSeconds, "время шага > 0");
        if (!(VelocityClamp >= 0)) throw new ArgumentOutOfRangeException(nameof(VelocityClamp), VelocityClamp, "предел скорости ≥ 0");
        if (!(Extents.X > 0 && Extents.Y > 0 && Extents.Z >= 0)) throw new ArgumentOutOfRangeException(nameof(Extents), Extents, "площадка > 0");
        if (!(BrushSize > 0)) throw new ArgumentOutOfRangeException(nameof(BrushSize), BrushSize, "размер кисти > 0");
        if (!(BrushVelocityClamp >= 0)) throw new ArgumentOutOfRangeException(nameof(BrushVelocityClamp), BrushVelocityClamp, "предел толчка ≥ 0");
        if (!(FluidDensityContrast > 0) || !float.IsFinite(FluidDensityContrast))
            throw new ArgumentOutOfRangeException(nameof(FluidDensityContrast), FluidDensityContrast, "контраст выхода > 0 (pow(0, 0) на GPU — NaN)");
        if (!(VeloDirNoiseSize > 0) || !float.IsFinite(VeloDirNoiseSize))
            throw new ArgumentOutOfRangeException(nameof(VeloDirNoiseSize), VeloDirNoiseSize, "размер направленного шума > 0");
        if (BrushMaxPoints is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(BrushMaxPoints), BrushMaxPoints, "точек кисти 1…4096");
        if (!(FilmCourant > 0 && FilmCourant <= 0.25f)) throw new ArgumentOutOfRangeException(nameof(FilmCourant), FilmCourant, "CFL 0…0.25 текселя за шаг");
        if (FilmFlow && !(FilmThicknessPerDensity > 0 && FilmMinThickness > 0 && FilmFriction >= 0 && FilmPuddleThickness >= 0))
            throw new ArgumentOutOfRangeException(nameof(FilmFlow), FilmFlow, "плёнка: h_max > 0, h_min > 0, k ≥ 0, H ≥ 0");
        if (BrushMaxEntries < BrushMaxPoints || BrushMaxEntries > 65535)
            throw new ArgumentOutOfRangeException(nameof(BrushMaxEntries), BrushMaxEntries, "записей бининга — от числа точек до 65535");
    }
}
