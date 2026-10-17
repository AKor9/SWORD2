using System.Text.RegularExpressions;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>Отбор клипов базы: номера клипов библиотеки по возрастанию и их разобранные имена (тот же порядок), регулярка отбора.</summary>
public sealed class GaspSelection
{
    private readonly int[] _clips;
    private readonly GaspClipName[] _names;

    internal GaspSelection(string pattern, int sourceClips, int[] clips, GaspClipName[] names)
    {
        Pattern = pattern;
        SourceClips = sourceClips;
        _clips = clips;
        _names = names;
    }

    /// <summary>Регулярка по короткому имени клипа.</summary>
    public string Pattern { get; }

    /// <summary>Клипов источника <see cref="GaspMotion.Source"/> в библиотеке (до отбора).</summary>
    public int SourceClips { get; }

    /// <summary>Номера клипов библиотеки по возрастанию — это и порядок клипов базы (<see cref="MotionDatabase.Clips"/>).</summary>
    public ReadOnlySpan<int> Clips => _clips;

    /// <summary>Имена клипов — в порядке <see cref="Clips"/> (номер клипа базы).</summary>
    public ReadOnlySpan<GaspClipName> Names => _names;

    public int Count => _clips.Length;
}

/// <summary>
/// Политика контента GASP для Motion Matching — одно место умолчаний модуля (у движка своих нет): отбор клипов, схема
/// признаков (суставы UE5 Manny по именам), параметры постройки базы, умолчания ручек <c>locomotion.*</c>. Значения — отправная точка.
/// </summary>
public static class GaspMotion
{
    /// <summary>Источник клипов локомоции в библиотеке (папка <c>anim/locomotion</c> пака).</summary>
    public const string Source = "locomotion";

    /// <summary>
    /// Отбор по умолчанию (<c>locomotion.clips</c>): все клипы, кроме <c>Circle_Strafe</c> — почти на месте, совпадали с
    /// запросом «стою» и держали персонажа на круге; и кроме стартов бегом — <c>Run_Start</c>, <c>Run_Reface_Start</c>,
    /// <c>Transition_Walk_to_Run</c>: рывок с места пригнувшись (таз ниже стойки на 15–22 см, наклон корпуса 35–62°), а у стартов шагом —
    /// 8–10 см и ~10°. Старт — шагом, разгон — циклами бега.
    /// </summary>
    public const string DefaultClips = "^(?!.*(Circle_Strafe|Run_Start|Run_Reface_Start|Transition_Walk_to_Run))";

    /// <summary>Порог «стоит», м/с: выход медленнее — продолжение в стойку; начало медленнее — прошлое «стоял»; цикл медленнее — не ход.</summary>
    public const float StandSpeed = 0.3f;

    /// <summary>Окно направления продолжения, рад (30°): цикл, чья средняя скорость отклоняется от направления выхода не больше.</summary>
    public const float DirectionWindow = MathF.PI / 6;

    /// <summary>Скорость ввода, выше которой модель скоростей данных берёт бег, м/с: между шагом (1,6) и бегом (4,2). Не ручка.</summary>
    public const float RunInputSpeed = 2.5f;

    /// <summary>
    /// Детектор контактов при 30 кадрах/с: сустав встаёт ниже 0,05 м над своим «полом» и медленнее 0,35 м/с, отрывается выше
    /// 0,07 м или быстрее 0,55 м/с; серия короче 2 кадров — шум.
    /// </summary>
    public static readonly ContactParams Contacts = new(0.05f, 0.07f, 0.35f, 0.55f, 2);

    /// <summary>
    /// Параметры постройки: ранний выход — последние 0,5 с клипа, скорости — средние за 0,1 с, движущийся сустав — дальше 1 мм
    /// от первого кадра, ничья шва — 10⁻⁴ от max(цена, 1) (граф тот же без AVX2).
    /// </summary>
    public static readonly MotionBuildParams BuildParams = new(Contacts, 0.5f, 0.1f, 0.001f, 1e-4f);

    /// <summary>Моменты будущего траектории, с.</summary>
    public static ReadOnlySpan<float> FutureTimes => [1f / 3, 2f / 3, 1f];

    /// <summary>Моменты прошлого траектории, с.</summary>
    public static ReadOnlySpan<float> PastTimes => [-1f / 3];

    /// <summary>
    /// Суставы контактов: биты 0, 1 — голеностопы (пятка), 2, 3 — носки. Стопа на земле — голеностоп или носок её стороны:
    /// голеностоп ловит только фазу пятки, носок — почти всю опору; разделение нужно фиксации стоп.
    /// </summary>
    public static readonly IReadOnlyList<string> ContactJoints = ["foot_l", "foot_r", "ball_l", "ball_r"];

    /// <summary>
    /// Схема признаков GASP (45): группы, их суставы по именам скелета UE5 Manny, веса по умолчанию и пол разброса <c>MinStd</c>.
    /// Веса — «управление важнее позы»: траектория 3,5 и скорость корня 4 против 0,25–0,5 у суставов.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, FeatureKind Kind, float Weight, float MinStd, IReadOnlyList<string> Joints)> Groups =
    [
        ("траектория", FeatureKind.TrajectoryPosition, 3.5f, 0.2f, []),
        ("курс траектории", FeatureKind.TrajectoryDirection, 1.5f, 0.2f, []),
        ("прошлое", FeatureKind.PastPosition, 0.5f, 0.1f, []),
        ("скорость корня", FeatureKind.RootVelocity, 4f, 0.2f, []),
        ("ω корня", FeatureKind.RootYawRate, 0.5f, 0.5f, []),
        ("стопы", FeatureKind.JointPosition, 0.4f, 0.03f, ["foot_l", "foot_r"]),
        ("v стоп", FeatureKind.JointVelocity, 0.5f, 0.2f, ["foot_l", "foot_r"]),
        ("колени", FeatureKind.JointPosition, 0.25f, 0.03f, ["calf_l", "calf_r"]),
        ("таз", FeatureKind.JointPosition, 0.25f, 0.02f, ["pelvis"]),
        ("v таза", FeatureKind.JointVelocity, 0.5f, 0.1f, ["pelvis"]),
        ("курс таза", FeatureKind.JointPairFacing, 0.5f, 0.1f, ["thigh_l", "thigh_r"]),
        ("курс груди", FeatureKind.JointPairFacing, 0.25f, 0.1f, ["clavicle_l", "clavicle_r"]),
    ];

    /// <summary>Ключи ручек весов групп (<c>locomotion.weight…</c>) в порядке <see cref="Groups"/>.</summary>
    public static readonly IReadOnlyList<string> WeightKeys =
    [
        "weightTrajectory", "weightTrajectoryFacing", "weightPast", "weightRootVelocity", "weightRootYawRate", "weightFeet",
        "weightFeetVelocity", "weightKnees", "weightPelvis", "weightPelvisVelocity", "weightPelvisFacing", "weightChestFacing",
    ];

    // Проигрывание игрока в тике: умолчания ручек «Локомоция» (locomotion.*) и постоянные системы Motion Matching игрока.

    /// <summary>
    /// Период поиска, с (<c>locomotion.searchPeriod</c>): поиск — раз в <c>round(период × частота тика)</c> ≥ 1 тиков, счётчиком
    /// тиков, а не накоплением времени во float.
    /// </summary>
    public const float SearchPeriod = 0.1f;

    /// <summary>
    /// Порог смены (<c>locomotion.switchGain</c>) в единицах цены поиска: найденный кадр сменяет текущий, только если дешевле
    /// больше чем на порог. Единственный механизм держаться своего клипа: скидка продолжению хуже — чем хуже текущий кадр, тем
    /// труднее с него уйти.
    /// </summary>
    public const float SwitchGain = 2f;

    /// <summary>
    /// Фиксация опорной стопы (<see cref="FootLock"/>): цель голеностопа отходит от анимации не дальше, м (<c>locomotion.footLock</c>;
    /// 0 — без фиксации), дальше точка опоры тянется за анимацией. Скольжение опорной стопы (среднее по 7 прогонам замеров, м/с): без
    /// фиксации 1,21, 0,06 м — 0,83, 0,1 м — 0,72, 0,2 м — 0,61; но с 0,2 м на беге по малому кругу (клип идёт вбок, корень ещё и по дуге)
    /// удержанная стопа уходила на 20 см назад сверх позы клипа — ноги перекрещивались. 0,1 м — поза как у клипа.
    /// </summary>
    public const float FootLockDistance = 0.1f;

    /// <summary>Курс опорной стопы отходит от анимации не дальше, градусы (<c>locomotion.footYaw</c>).</summary>
    public const float FootYaw = 45;

    /// <summary>Полупериод возврата стопы к анимации после опоры, с (<c>locomotion.footBlend</c>).</summary>
    public const float FootBlend = 0.08f;

    /// <summary>
    /// «У земли» в позе, м: голеностоп не выше (стоя — 0,079), носок не выше (стоя — 0,008); выше — не опора, даже если кадр базы
    /// говорит «стоит» (позу ещё догоняет переход).
    /// </summary>
    public const float FootHeelHeight = 0.15f, FootBallHeight = 0.06f;

    /// <summary>
    /// Колено над опорной стопой (<c>locomotion.kneeLimit</c>), градусы: в клипах смены направления (Hourglass, Box, Diamond, Pivot,
    /// Shuffle) колено опорной ноги уходит от носка до 50–67° (p95; по всем клипам — 34°), а фиксация курса стопы добавляет своё — колени
    /// «смотрят в разные стороны». Сверх предела нога поворачивается вокруг линии бедро — голеностоп; вход — мягкий на <see cref="KneeSoft"/>.
    /// </summary>
    public const float KneeLimit = 30, KneeSoft = 10;

    /// <summary>Доля правки колена по высоте носка в позе, м: ниже первого — вся, выше второго — нет (нога в махе — как в клипе).</summary>
    public const float KneeGround = 0.06f, KneeAir = 0.16f;

    /// <summary>
    /// Удержанная стопа уходит от анимации внутрь, к другой ноге, не дальше, м: на беге по малому кругу тело уходит вбок от опорной
    /// стопы, и с пределом 0,2 м ноги перекрещивались.
    /// </summary>
    public const float FootInward = 0.03f;

    /// <summary>Мягкая доля хода ноги у предела IK (колено не щёлкает).</summary>
    public const float FootSoft = 0.05f;

    /// <summary>Скачок корня за тик дальше, м, — фиксация с нуля (телепорт, расстановка).</summary>
    public const float FootTeleport = 1;

    /// <summary>Полупериод затухания смещений инерциализации, с (ручка <c>locomotion.inertialHalflife</c>).</summary>
    public const float InertialHalflife = 0.1f;

    /// <summary>Размах темпа (<c>locomotion.rateRange</c>): темп — скорость корня / скорость корня клипа в 1 ± размах.</summary>
    public const float RateRange = 0.4f;

    /// <summary>
    /// Сглаживание темпа — полупериод, с: темп идёт к цели экспонентой (0 — без сглаживания). Выбран по замеру (README модуля).
    /// </summary>
    public const float RateHalflife = 0.05f;

    /// <summary>Окно пропуска своего клипа, с: кадры своего клипа ближе к текущему (у цикла — по кругу) не кандидаты поиска.</summary>
    public const float SkipSeconds = 0.2f;

    /// <summary>Скачок цели скорости: <c>|цель − цель прошлого поиска| &gt; доля·max(1, |цель|)</c> м/с — поиск в этом же тике.</summary>
    public const float GoalJump = 0.5f;

    // Поворот на месте (TurnInPlace): умолчания ручек (locomotion.turn*) и постоянные; клипы — Stand_Turn_<угол>_<L|R> отбора базы.

    /// <summary>
    /// Порог поворота, градусы (<c>locomotion.turnAngle</c>): стоя расхождение курса корня и ног больше — клип поворота на месте;
    /// меньше — корпус доворачивается скручиванием. В GASP шаг поворота — с 50°; 45 — скручивание корпуса над ногами заметно меньше.
    /// </summary>
    public const float TurnAngle = 45;

    /// <summary>Малый угол, градусы (ручка <c>locomotion.turnSmallAngle</c>): курс стоит дольше выдержки, а расхождение больше — клип поворота.</summary>
    public const float TurnSmallAngle = 15;

    /// <summary>Выдержка, с (ручка <c>locomotion.turnHold</c>): сколько курс корня стоит, прежде чем ноги доворачиваются на малое расхождение.</summary>
    public const float TurnHold = 0.4f;

    /// <summary>
    /// Потолок расхождения, градусы (<c>locomotion.turnMax</c>): при резком рывке курса ноги подтягиваются к потолку (скользят);
    /// 0 — без скручивания и клипов поворота. Как у зажима смещения корня в MM Холдена — 90°.
    /// </summary>
    public const float TurnMax = 90;

    /// <summary>Возврат на ходу, с (ручка <c>locomotion.turnReturn</c>): пошёл — расхождение гаснет к 0 не дольше этого (с потолка — ровно за него).</summary>
    public const float TurnReturn = 0.15f;

    /// <summary>Курс корня «стоит» (для выдержки): поворот за тик не больше этой скорости, рад/с (6°/с — дрожь мыши не сбрасывает выдержку).</summary>
    public const float TurnStillRate = 0.1f;

    /// <summary>
    /// Предел подгонки клипа поворота: курс ног идёт за корнем клипа с долей k = расхождение / остаток поворота клипа в 0…предел
    /// (k ≠ 1 — стопы скользят на долю |k − 1|).
    /// </summary>
    public const float TurnWarpMax = 2;

    /// <summary>Поворот клипа кончился: остаток поворота его корня до выхода меньше этого, рад (1°) — поиск снова идёт, можно новый поворот.</summary>
    public const float TurnDone = MathF.PI / 180;

    /// <summary>Опора скручивания — корень скелета (начало модели): ноги и таз поворачиваются вокруг вертикали через него.</summary>
    public const string TwistRoot = "root";

    /// <summary>
    /// Суставы скручивания UE5 Manny и доли обратного поворота (сумма 1); выше <c>spine_05</c> всё смотрит по курсу корня.
    /// </summary>
    public static readonly IReadOnlyList<(string Joint, float Weight)> TwistSpine =
        [("spine_01", 0.1f), ("spine_02", 0.15f), ("spine_03", 0.2f), ("spine_04", 0.25f), ("spine_05", 0.3f)];

    /// <summary>Курс груди — по паре ключиц (как признак «курс груди» схемы): вперёд — (левая − правая), повёрнутая +X → +Z.</summary>
    public const string ChestLeft = "clavicle_l", ChestRight = "clavicle_r";

    /// <summary>
    /// Выравнивание груди стоя: клип сам уводит грудь от своего корня; скручивание доворачивает её к курсу груди позы
    /// постройки не больше чем на этот угол, рад (у клипов поворота базы грудь уходит до 45°).
    /// </summary>
    public const float ChestMax = MathF.PI / 2;

    /// <summary>Скорость выравнивания груди, рад/с (6 — 344°/с: клип поворота уводит грудь до ~150°/с; смена стоя/на ходу — за доли 0,1 с).</summary>
    public const float ChestRate = 6;

    /// <summary>Схема GASP на скелете: суставы — <see cref="Skeleton.Find"/> по именам <see cref="Groups"/> и <see cref="ContactJoints"/>.</summary>
    /// <exception cref="InvalidDataException">В скелете нет сустава — сообщение называет сустав и группу.</exception>
    public static MotionSchema Schema(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var groups = new FeatureGroupSpec[Groups.Count];
        for (int g = 0; g < groups.Length; g++)
        {
            var (name, kind, weight, minStd, joints) = Groups[g];
            groups[g] = new FeatureGroupSpec(name, kind, weight, minStd,
                MotionSchema.IsJoint(kind) ? [.. joints.Select(j => Joint(skeleton, j, $"группа признаков «{name}»"))] : null);
        }
        int[] contacts = [.. ContactJoints.Select(j => Joint(skeleton, j, "контакты стоп"))];
        return new MotionSchema(FutureTimes, PastTimes, groups, contacts);
    }

    private static int Joint(Skeleton skeleton, string name, string where) => skeleton.Find(name) is var j and >= 0
        ? j
        : throw new InvalidDataException($"в скелете {skeleton.Id} нет сустава «{name}» ({where} схемы Motion Matching)");

    /// <summary>
    /// Отбор клипов базы: клипы <see cref="Source"/> с коротким именем под <paramref name="pattern"/> (null — <see cref="DefaultClips"/>).
    /// </summary>
    /// <exception cref="ArgumentException">Регулярка не разбирается.</exception>
    /// <exception cref="InvalidDataException">Нет клипов источника, отбор пуст или имя отобранного клипа не разбирается.</exception>
    public static GaspSelection Select(ClipLibrary library, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        pattern ??= DefaultClips;
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"отбор клипов Motion Matching «{pattern}» — не регулярка: {e.Message}", nameof(pattern), e);
        }
        var clips = new List<int>();
        var names = new List<GaspClipName>();
        var bad = new List<string>();
        int source = 0;
        var all = library.Clips;
        for (int c = 0; c < all.Length; c++)
        {
            if (all[c].Source != Source) continue;
            source++;
            string shortName = all[c].ShortName;
            if (!regex.IsMatch(shortName)) continue;
            if (GaspClipName.TryParse(shortName, out var name, out string error))
            {
                clips.Add(c);
                names.Add(name);
            }
            else bad.Add($"{shortName} — {error}");
        }
        if (source == 0)
            throw new InvalidDataException($"в библиотеке клипов скелета {library.Skeleton.Id} нет клипов источника «{Source}» (anim/{Source}/*.swclip) — базе Motion Matching не из чего строиться");
        if (bad.Count > 0)
            throw new InvalidDataException($"имена {bad.Count} клипов отбора «{pattern}» не разбираются как GASP: {string.Join("; ", bad.Take(5))}{(bad.Count > 5 ? "; …" : "")}");
        if (clips.Count == 0)
            throw new InvalidDataException($"отбор клипов Motion Matching «{pattern}» не взял ни одного из {source} клипов «{Source}»");
        return new GaspSelection(pattern, source, [.. clips], [.. names]);
    }
}
