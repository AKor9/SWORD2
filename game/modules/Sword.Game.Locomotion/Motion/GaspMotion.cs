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
/// Политика контента GASP для Motion Matching — одно место умолчаний модуля (у движка своих нет): источник и отбор клипов, схема
/// признаков (45 признаков: траектория, прошлое, корень, стопы, колени, таз, курс таза и груди — суставы UE5 Manny по именам),
/// параметры постройки базы, порог «стоит» и окно направления политики графа, порог бега модели скоростей. Идеи — <c>LocoFeatures</c>,
/// <c>LocoKinematics</c>, <c>LocoDatabase</c>, <c>LocomotionModule</c> SWORD 2eab13b; значения — отправная точка, ручки — с 8е.
/// </summary>
public static class GaspMotion
{
    /// <summary>Источник клипов локомоции в библиотеке (папка <c>anim/locomotion</c> пака).</summary>
    public const string Source = "locomotion";

    /// <summary>
    /// Отбор по умолчанию — регулярка по короткому имени: все клипы, кроме стрейфа по кругу (<c>Circle_Strafe</c>: циклы 2,6–14 с почти на
    /// месте — совпадали с запросом «стою» и держали персонажа на круге). Ключ конфига <c>locomotion.clips</c> — с 8е.
    /// </summary>
    public const string DefaultClips = "^(?!.*Circle_Strafe)";

    /// <summary>Порог «стоит», м/с: выход медленнее — продолжение в стойку; начало медленнее — прошлое «стоял»; цикл медленнее — не ход.</summary>
    public const float StandSpeed = 0.3f;

    /// <summary>Окно направления продолжения, рад (30°): цикл, чья средняя скорость отклоняется от направления выхода не больше.</summary>
    public const float DirectionWindow = MathF.PI / 6;

    /// <summary>
    /// Скорость ввода, выше которой модель скоростей данных берёт бег, м/с: между шагом (1,6) и бегом (4,2) контроллера ядра;
    /// значим только на разгоне и торможении (ручка — с 8е).
    /// </summary>
    public const float RunInputSpeed = 2.5f;

    /// <summary>
    /// Детектор контактов (<see cref="ContactJoints"/>) при 30 кадрах/с: сустав встаёт ниже 0,05 м над своим «полом» в клипе и медленнее
    /// 0,35 м/с по земле, отрывается выше 0,07 м или быстрее 0,55 м/с; серия короче 2 кадров — шум. Значения — для голеностопа; носку те
    /// же (высота — над его собственным «полом»): на паке стойка — 100 %, циклы шага — ~50 %, бега — 21–33 % кадров на стопу.
    /// </summary>
    public static readonly ContactParams Contacts = new(0.05f, 0.07f, 0.35f, 0.55f, 2);

    /// <summary>
    /// Параметры постройки: контакты <see cref="Contacts"/>, ранний выход — в последних 0,5 с клипа, скорости начала и выхода — средние за
    /// 0,1 с, движущееся смещение сустава — дальше 1 мм от первого кадра, ничья шва — 10⁻⁴ от max(цена, 1) (как тест 8б на паке: граф тот
    /// же без AVX2).
    /// </summary>
    public static readonly MotionBuildParams BuildParams = new(Contacts, 0.5f, 0.1f, 0.001f, 1e-4f);

    /// <summary>Моменты будущего траектории, с.</summary>
    public static ReadOnlySpan<float> FutureTimes => [1f / 3, 2f / 3, 1f];

    /// <summary>Моменты прошлого траектории, с.</summary>
    public static ReadOnlySpan<float> PastTimes => [-1f / 3];

    /// <summary>
    /// Суставы контактов с землёй: биты 0, 1 — голеностопы (<c>foot_l</c>, <c>foot_r</c>: пятка), 2, 3 — носки (<c>ball_l</c>, <c>ball_r</c>).
    /// Стопа на земле — голеностоп или носок её стороны. Голеностоп один ловит только фазу пятки (на паке: циклы бега — 7–21 % кадров,
    /// шага — 39–47 %), носок — почти всю опору (бега — 21–33 %, шага — 47–54 %) и почти целиком покрывает контакты голеностопа; разделение
    /// «пятка / носок» нужно фиксации стоп (этап 9).
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
    /// Отбор клипов базы: клипы источника <see cref="Source"/>, чьё короткое имя подходит под регулярку <paramref name="pattern"/> (null —
    /// <see cref="DefaultClips"/>), с разобранными именами.
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
