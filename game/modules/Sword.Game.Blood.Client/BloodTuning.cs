namespace Sword.Game.Blood;

/// <summary>
/// Ручки крови на теле (F3 «Кровь»): меняются на лету, читаются в шаге. Числа по умолчанию — WR §4.3 и решения 2, 3 плана этапа.
/// </summary>
public sealed class BloodTuning
{
    /// <summary>Сильная рана — урон удара не меньше (решение 2: рубящий игрока 60.8 и укол 62.4 — сильные, удар врага 30.4 — нет).</summary>
    public float StrongDamage { get; set; } = 45;

    /// <summary>Крови в рану на единицу урона, мл.</summary>
    public float VolumePerDamage { get; set; } = 0.05f;

    /// <summary>Вязкое гашение скорости потёка k, 1/с: v ← v·e^(−k·dt); на отвесной коже скорость устанавливается около g·dt·e^(−k·dt)/(1 − e^(−k·dt)).</summary>
    public float Viscosity { get; set; } = 80;

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

    /// <summary>Объём, который потёк оставляет на метр следа, мл/м (плёнка ≈ 2 мм × 1 мм).</summary>
    public float TrailCost { get; set; } = 2;

    /// <summary>Точки следа — не чаще, м.</summary>
    public float TrailStep { get; set; } = 0.01f;

    /// <summary>Потёк живёт не дольше, с.</summary>
    public float MaxLife { get; set; } = 30;

    /// <summary>Ускорение кожи для потёка ограничено, м/с² (SWORD 1: 400).</summary>
    public float MaxAccel { get; set; } = 400;

    /// <summary>Пятно у раны при попадании: крови на единицу урона, мл.</summary>
    public float SpotPerDamage { get; set; } = 0.02f;

    /// <summary>Радиус пятна у раны: наименьший, м, и прибавка на единицу урона, м (не больше <see cref="SpotRadiusMax"/>).</summary>
    public float SpotRadiusMin { get; set; } = 0.012f;
    public float SpotRadiusPerDamage { get; set; } = 0.0005f;
    public float SpotRadiusMax { get; set; } = 0.05f;

    /// <summary>Сочение сильной раны (решение 3): темп на единицу урона, мл/с; гаснет линейно до нуля за столько секунд.</summary>
    public float OozeRate { get; set; } = 0.004f;
    public float OozeSeconds { get; set; } = 20;

    /// <summary>У трупа темп не ниже этой доли начального — пока труп лежит (решение 4).</summary>
    public float OozeCorpseShare { get; set; } = 0.25f;

    /// <summary>Темп сочения всех ран бойца не больше, мл/с.</summary>
    public float OozeMaxRate { get; set; } = 1f;

    /// <summary>Доля выпущенного — в пятно у раны (растёт), остальное — потёками.</summary>
    public float OozeSpotShare { get; set; } = 0.3f;

    /// <summary>Порция нового потёка из сочения, мл (больше порога отрыва — у раны снизу капает).</summary>
    public float OozeFeedVolume { get; set; } = 0.3f;

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

    /// <summary>Отладочная лужа у ног игрока, мл.</summary>
    public float PuddleMl { get; set; } = 60;

    /// <summary>Плёнка на полу: приёмник не круче, градусы; не дальше от рельефа по высоте, м.</summary>
    public float FloorMaxSlope { get; set; } = 50;
    public float FloorHeightBand { get; set; } = 0.15f;

    /// <summary>
    /// Нормаль плёнки: уклон профиля толщины × усиление; профиль — h_c·(1 − e^(−h/h_c)) (<see cref="FloorMeniscusMm"/>): у тонкого края
    /// круче (мениск), толстая середина лужи плоская.
    /// </summary>
    public float FloorNormalScale { get; set; } = 8;
    public float FloorMeniscusMm { get; set; } = 0.5f;

    /// <summary>F0 блика плёнки (кровь n ≈ 1.40 — 0.028, BVR §5.3).</summary>
    public float FloorF0 { get; set; } = 0.028f;

    /// <summary>Сила отражения неба плёнкой (1 — как у сцены).</summary>
    public float FloorSkyGain { get; set; } = 2.5f;

    /// <summary>Мокрая подложка: множитель пола под свежей кровью и в ореоле вокруг (Lagarde: до 0.2 у пористой), радиус ореола, текселей.</summary>
    public float FloorWetDarken { get; set; } = 0.7f;
    public float FloorHalo { get; set; } = 1.5f;

    /// <summary>Рябь плёнки: уклон шума в мире (без анимации) и его размер, м.</summary>
    public float FloorRipple { get; set; } = 0.02f;
    public float FloorRippleSize { get; set; } = 0.15f;

    /// <summary>Функция свежести (кожа и пол).</summary>
    public BloodLookParams Look = BloodLookParams.Default;
}
