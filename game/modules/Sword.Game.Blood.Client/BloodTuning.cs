namespace Sword.Game.Blood;

/// <summary>
/// Ручки крови на теле (F3 «Кровь»): меняются на лету, читаются в шаге. Числа по умолчанию — WR §4.3 и решения 2, 3 плана этапа.
/// </summary>
public sealed class BloodTuning
{
    /// <summary>Сильная рана — урон удара не меньше (решение 2: рубящий игрока 60.8 и укол 62.4 — сильные, удар врага 30.4 — нет).</summary>
    public float StrongDamage { get; set; } = 45;

    /// <summary>Крови в рану на единицу урона, мл.</summary>
    public float VolumePerDamage { get; set; } = 0.35f;

    /// <summary>Вязкое гашение скорости потёка k, 1/с: v ← v·e^(−k·dt); на отвесной коже скорость устанавливается около g·dt·e^(−k·dt)/(1 − e^(−k·dt)).</summary>
    public float Viscosity { get; set; } = 40;

    /// <summary>Объём потёка, с которого он отрывается каплей с поверхности, смотрящей вниз, мл.</summary>
    public float DetachVolume { get; set; } = 0.25f;

    /// <summary>Поверхность смотрит вниз, если угол наружной нормали с «вниз» меньше, градусы.</summary>
    public float DetachAngle { get; set; } = 50;

    /// <summary>Рывок: ускорение кожи больше, м/с², срывает каплю с поверхности, смотрящей вниз, и при малом объёме.</summary>
    public float JerkAccel { get; set; } = 120;

    /// <summary>Меньший объём рывок не срывает, мл.</summary>
    public float MinDropVolume { get; set; } = 0.05f;

    /// <summary>Касательное ускорение слабее, м/с², — потёк стоит (кровь держится на коже).</summary>
    public float StickAccel { get; set; } = 2;

    /// <summary>Объём, который потёк оставляет на метр следа, мл/м (полоса ≈ 4 см × 0.06 мм).</summary>
    public float TrailCost { get; set; } = 2.5f;

    /// <summary>След потёка на коже — пятно этого радиуса в привязке, м (на 24 м полоса — 2–3 пикселя; 0 — три вершины треугольника).</summary>
    public float TrailRadius { get; set; } = 0.025f;

    /// <summary>Точки следа — не чаще, м.</summary>
    public float TrailStep { get; set; } = 0.01f;

    /// <summary>Потёк живёт не дольше, с.</summary>
    public float MaxLife { get; set; } = 30;

    /// <summary>Ускорение кожи для потёка ограничено, м/с² (SWORD 1: 400).</summary>
    public float MaxAccel { get; set; } = 400;

    /// <summary>Пятно у раны при попадании: крови на единицу урона, мл.</summary>
    public float SpotPerDamage { get; set; } = 0.1f;

    /// <summary>Радиус пятна у раны: наименьший, м, и прибавка на единицу урона, м (не больше <see cref="SpotRadiusMax"/>).</summary>
    public float SpotRadiusMin { get; set; } = 0.02f;
    public float SpotRadiusPerDamage { get; set; } = 0.0012f;
    public float SpotRadiusMax { get; set; } = 0.1f;

    /// <summary>Сочение сильной раны (решение 3): темп на единицу урона, мл/с; гаснет линейно до нуля за столько секунд.</summary>
    public float OozeRate { get; set; } = 0.03f;
    public float OozeSeconds { get; set; } = 20;

    /// <summary>У трупа темп не ниже этой доли начального — пока труп лежит (решение 4).</summary>
    public float OozeCorpseShare { get; set; } = 0.25f;

    /// <summary>Темп сочения всех ран бойца не больше, мл/с.</summary>
    public float OozeMaxRate { get; set; } = 3f;

    /// <summary>Доля выпущенного — в пятно у раны (растёт), остальное — потёками.</summary>
    public float OozeSpotShare { get; set; } = 0.3f;

    /// <summary>Порция нового потёка из сочения, мл (больше порога отрыва — у раны снизу капает).</summary>
    public float OozeFeedVolume { get; set; } = 0.3f;

    /// <summary>
    /// Лужа под трупом: из ран трупа (по урону²) каплями выливается столько, мл, — после задержки <see cref="CorpsePoolDelay"/>, с (труп
    /// падает), темп спадает как e^(−t/τ) (к 5 с от смерти — 80 %, к 7.5 с — 93 %), τ — <see cref="CorpsePoolSeconds"/>; капля —
    /// <see cref="CorpsePourDropMl"/>.
    /// </summary>
    public float CorpsePoolMl { get; set; } = 600;
    public float CorpsePoolSeconds { get; set; } = 2.5f;
    public float CorpsePoolDelay { get; set; } = 1;
    public float CorpsePourDropMl { get; set; } = 1;

    /// <summary>
    /// Брызги из раны при ударе: крови на единицу урона, мл (капли — пока не наберётся; число ∝ урону), не больше капель на удар; диаметр
    /// капли — лог-нормально: медиана, мм, σ логарифма, в пределах <see cref="SprayMinMm"/>…<see cref="SprayMaxMm"/>.
    /// </summary>
    public float SprayPerDamage { get; set; } = 0.08f;
    public int SprayMaxDrops { get; set; } = 256;
    public float SprayMedianMm { get; set; } = 2.5f;
    public float SpraySigma { get; set; } = 0.6f;
    public float SprayMinMm { get; set; } = 1;
    public float SprayMaxMm { get; set; } = 6;

    /// <summary>Скорость капли брызг — доля скорости кончика клинка (от, до), в пределах <see cref="SprayMinSpeed"/>…<see cref="SprayMaxSpeed"/>, м/с.</summary>
    public float SpraySpeedMin { get; set; } = 0.2f;
    public float SpraySpeedMax { get; set; } = 0.6f;
    public float SprayMinSpeed { get; set; } = 1;
    public float SprayMaxSpeed { get; set; } = 8;

    /// <summary>
    /// Веер брызг: полуугол в плоскости хода клинка (Dir, Blade), рад; разброс поперёк неё, рад; доля скорости наружу по нормали кожи.
    /// Укол — струя назад по ходу клинка в конусе <see cref="SprayThrustCone"/>, рад.
    /// </summary>
    public float SprayFan { get; set; } = 0.6f;
    public float SprayCross { get; set; } = 0.2f;
    public float SprayOutward { get; set; } = 0.5f;
    public float SprayThrustCone { get; set; } = 0.35f;

    /// <summary>
    /// Обратный конус к атакующему: доля капель брызг, которые летят в его торс, голову и руки (прицел по замкнутой формуле полёта с разбросом
    /// <see cref="SprayBackJitter"/>, м); атакующий дальше <see cref="SprayBackRange"/>, м, — нет.
    /// </summary>
    public float SprayBackShare { get; set; } = 0.2f;
    public float SprayBackJitter { get; set; } = 0.08f;
    public float SprayBackRange { get; set; } = 2.5f;

    /// <summary>Капля брызг не липнет к своему телу, пока не пролетела столько, м (вылетает из раны).</summary>
    public float SprayOwnSkip { get; set; } = 0.06f;

    /// <summary>Прилипшая к коже капля — пятно толщины не больше этой, мм (радиус — от объёма, 0.8…3 см).</summary>
    public float SprayStickMm { get; set; } = 0.15f;

    /// <summary>Сопротивление полёта капли k, 1/с: dv/dt = g − k·v (предельная скорость g/k).</summary>
    public float DropDrag { get; set; } = 2;

    /// <summary>Капля в полёте вытянута по скорости: длина = 2r + скорость × столько, с (не больше 6r).</summary>
    public float DropStretch { get; set; } = 0.012f;

    /// <summary>Радиус капли на экране — радиус шара её объёма × это.</summary>
    public float DropRadiusScale { get; set; } = 1.5f;

    /// <summary>Капля на экране — толщина для функции свежести, мм.</summary>
    public float DropThickMm { get; set; } = 1;

    /// <summary>Пятно капли на полу: полуось гаусса — радиус диска толщины лужи H с её объёмом × это.</summary>
    public float DropSpread { get; set; } = 1.5f;

    /// <summary>Отладочная лужа у ног игрока, мл, — выливается за столько секунд.</summary>
    public float PuddleMl { get; set; } = 120;
    public float PourSeconds { get; set; } = 1;

    /// <summary>Плёнка на полу: приёмник не круче, градусы; не дальше от рельефа по высоте, м.</summary>
    public float FloorMaxSlope { get; set; } = 50;
    public float FloorHeightBand { get; set; } = 0.15f;

    /// <summary>
    /// Нормаль плёнки: уклон профиля толщины × усиление; профиль — h_c·(1 − e^(−h/h_c)) (<see cref="FloorMeniscusMm"/>): у тонкого края
    /// круче (мениск), толстая середина лужи плоская.
    /// </summary>
    public float FloorNormalScale { get; set; } = 30;
    public float FloorMeniscusMm { get; set; } = 0.5f;

    /// <summary>F0 блика плёнки (кровь n ≈ 1.40 — 0.028, BVR §5.3).</summary>
    public float FloorF0 { get; set; } = 0.028f;

    /// <summary>Сила отражения неба плёнкой (1 — как у сцены).</summary>
    public float FloorSkyGain { get; set; } = 2.5f;

    /// <summary>
    /// Яркость горизонта в отражении неба (× верх неба сцены, растёт к горизонту как (1 − r.y)⁴): на скользящих углах френель плёнки
    /// отражает светлое низкое небо — мокрое читается и в тени.
    /// </summary>
    public float FloorHorizon { get; set; } = 1.5f;

    /// <summary>
    /// Второй, широкий блик солнца свежей плёнки (рябь и мениск размывают блик вокруг зеркального направления): вес и прибавка к
    /// шероховатости слоя.
    /// </summary>
    public float FloorGlint { get; set; } = 0.4f;
    public float FloorGlintRoughness { get; set; } = 0.3f;

    /// <summary>Наклон нормали плёнки (мениск, рябь) не больше, градусы.</summary>
    public float FloorMaxTilt { get; set; } = 30f;

    /// <summary>
    /// Мокрая подложка: множитель пола под свежей кровью и в ореоле вокруг (Lagarde: до 0.2 у пористой; с оттенком
    /// <see cref="BloodLook.WetTint"/> — темнее и насыщеннее, не светлее), радиус ореола, текселей (ореол — среднее толщины по кругу:
    /// мягко гаснет наружу).
    /// </summary>
    public float FloorWetDarken { get; set; } = 0.75f;
    public float FloorHalo { get; set; } = 2f;

    /// <summary>Сдвиг выборки поля статичным шумом в мире, текселей: край рваный внутри текселя, а не по сетке поля.</summary>
    public float FloorEdgeWarp { get; set; } = 0.6f;

    /// <summary>Рябь плёнки: уклон шума в мире (без анимации) и его размер, м.</summary>
    public float FloorRipple { get; set; } = 0.03f;
    public float FloorRippleSize { get; set; } = 0.15f;

    /// <summary>
    /// Неровность плёнки на экране (статичный шум толщины в мире, 4 октавы): толщина × (1 ± <see cref="FloorNoise"/>) минус до
    /// <see cref="FloorErodeMm"/> по шуму — край лужи рваный, толщина неравномерная; размер крупной октавы, м.
    /// </summary>
    public float FloorNoise { get; set; } = 0.5f;
    public float FloorNoiseSize { get; set; } = 0.12f;
    public float FloorErodeMm { get; set; } = 0.15f;

    /// <summary>Засохшее в окне уходит в карту пятен: возраст не меньше, с (тонкое, стоящее; толще — вдвое старше).</summary>
    public float FloorDryAge { get; set; } = 240;

    /// <summary>Функция свежести (кожа и пол).</summary>
    public BloodLookParams Look = BloodLookParams.Default;
}
