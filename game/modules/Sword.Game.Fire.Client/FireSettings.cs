namespace Sword.Game.Fire;

/// <summary>
/// Секция конфига <c>fire</c> (слои — как у всех секций: код → <c>sword.json</c> → <c>sword.user.json</c> → <c>SWORD_FIRE_*</c> →
/// <c>--set fire.ключ=значение</c>; список — литералом JSON: <c>--set fire.weapons=["sword_1h"]</c>).
/// </summary>
public sealed record FireSettings
{
    /// <summary>
    /// Горящее оружие — имена таблицы оружия (файл пака без расширения, регистр не важен). Имени нет в паке — предупреждение в журнал,
    /// такое оружие не горит (пак без факела — эталонный, синтетический тестов).
    /// </summary>
    public string[] Weapons { get; init; } = ["torch"];

    /// <summary>
    /// Одновременных огней не больше — экземпляров сима (по умолчанию горящий и гаснущий); сверх — новый огонь занимает самый давно
    /// гаснущий, а если гаснущих нет — не горит (счёт в итоге, <c>fire.overLimit</c>).
    /// </summary>
    public int MaxFires { get; init; } = 2;

    /// <summary>Предел <see cref="MaxFires"/>.</summary>
    public const int FiresLimit = 8;

    /// <summary>Центр шишки ближе к кисти, чем кончик клинка (дальний край шишки), м — вдоль клинка.</summary>
    public float HeadOffset { get; init; } = 0.02f;

    /// <summary>
    /// Радиус сферы границ шишки, см (эллипсоид 5×5×4 см — большая полуось) и множитель кисти примитива (<c>PrimitiveObjBrushSize</c> L2):
    /// размер точки кисти s = 0.07·R·множитель (BR §2.2) — 0.35, σ мазка 3.3 см.
    /// </summary>
    public float HeadRadiusCm { get; init; } = 2.5f;

    public float BrushObjectSize { get; init; } = 2;

    /// <summary>
    /// Гаснущий огонь (оружие выпало, боец мёртв, оружие сменили): кисть выключена, сим шагает не дольше стольких секунд (шагов ×60) —
    /// поле затухает ×0.94 за шаг, за 1.5 с до 0.4 %, — или раньше, когда Σρ поля (чтение GPU с задержкой) ниже <see cref="FadeDensity"/>.
    /// </summary>
    public float FadeSeconds { get; init; } = 1.5f;

    public float FadeDensity { get; init; } = 0.5f;

    /// <summary>
    /// Окно 3D-дыма (<see cref="FireGasSlots"/>) выше шишки: цель окна — шишка + столько вверх, м. Окно 2.88×3.84×2.88 м: при 1 м шишка
    /// на 0.92 м над низом окна, над ней 2.92 м — место столбу дыма 1.5–2 м.
    /// </summary>
    public float GasUp { get; init; } = 1f;

    /// <summary>
    /// Упреждение окна: центр окна по горизонтали догоняет шишку экспонентой с постоянной <see cref="GasFollowSeconds"/> с, шишка — не дальше
    /// <see cref="GasMaxLead"/> м от него. На бегу шишка впереди центра на v·τ, зажатое до предела, — шлейфу позади больше места; при вращении на
    /// месте центр окна — у оси вращения, а не на шишке: факел в руке ходит по кругу ≈1.15 м, кольцо дыма (Ø 2.3 м) влезает в окно 2.88 м только
    /// с центром у оси. Предел 1.1 м — шишка не ближе 0.34 м к грани окна.
    /// </summary>
    public float GasFollowSeconds { get; init; } = 1f;

    public float GasMaxLead { get; init; } = 1.1f;

    /// <summary>
    /// Дым погасшего огня (источник выключен, окно стоит): сим шагает, пока Σρ поля (чтение GPU с задержкой) не ниже
    /// <see cref="GasFadeDensity"/>, но не дольше <see cref="GasFadeSeconds"/>; затем экземпляр газа свободен.
    /// </summary>
    public float GasFadeSeconds { get; init; } = 8f;

    public float GasFadeDensity { get; init; } = 1f;

    /// <summary>Проверка чисел — до ресурсов.</summary>
    public void Validate()
    {
        if (MaxFires is < 1 or > FiresLimit) throw new InvalidDataException($"конфиг: fire.maxFires = {MaxFires}: 1…{FiresLimit}");
        if (!(HeadOffset >= 0 && HeadOffset <= 0.5f)) throw new InvalidDataException($"конфиг: fire.headOffset = {HeadOffset}: 0…0.5 м");
        if (!(HeadRadiusCm > 0 && HeadRadiusCm <= 50)) throw new InvalidDataException($"конфиг: fire.headRadiusCm = {HeadRadiusCm}: 0…50 см");
        if (!(BrushObjectSize > 0 && BrushObjectSize <= 20))
            throw new InvalidDataException($"конфиг: fire.brushObjectSize = {BrushObjectSize}: 0…20");
        if (!(FadeSeconds > 0 && FadeSeconds <= 30)) throw new InvalidDataException($"конфиг: fire.fadeSeconds = {FadeSeconds}: 0…30 с");
        if (!(FadeDensity >= 0)) throw new InvalidDataException($"конфиг: fire.fadeDensity = {FadeDensity}: ≥ 0");
        if (!(GasUp >= 0 && GasUp <= 1.8f)) throw new InvalidDataException($"конфиг: fire.gasUp = {GasUp}: 0…1.8 м");
        if (!(GasFollowSeconds > 0 && GasFollowSeconds <= 5))
            throw new InvalidDataException($"конфиг: fire.gasFollowSeconds = {GasFollowSeconds}: 0…5 с");
        if (!(GasMaxLead >= 0 && GasMaxLead <= 1.2f)) throw new InvalidDataException($"конфиг: fire.gasMaxLead = {GasMaxLead}: 0…1.2 м");
        if (!(GasFadeSeconds > 0 && GasFadeSeconds <= 60)) throw new InvalidDataException($"конфиг: fire.gasFadeSeconds = {GasFadeSeconds}: 0…60 с");
        if (!(GasFadeDensity >= 0)) throw new InvalidDataException($"конфиг: fire.gasFadeDensity = {GasFadeDensity}: ≥ 0");
    }
}
