namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Что измеряет группа признаков. Все признаки — в системе корня кадра (<see cref="Pose2"/>: +Z вперёд, +X влево). Три вида
/// по источнику: траектория (<see cref="MotionSchema.IsTrajectory"/> — в базе по графу и политике прошлого, в запросе — из
/// модели предсказания и истории корня; цена шва графа её не сравнивает), корень (<see cref="MotionSchema.IsRoot"/> — в базе
/// из кинематики клипа, в запросе — из модели) и суставы (<see cref="MotionSchema.IsJoint"/> — в базе из кинематики, в запросе —
/// признаки текущего кадра базы).
/// </summary>
public enum FeatureKind : byte
{
    /// <summary>Положение корня (x, z) в моменты будущего <see cref="MotionSchema.FutureTimes"/> — 2 на момент.</summary>
    TrajectoryPosition,

    /// <summary>Направление «вперёд» корня (sin, cos курса) в моменты будущего — 2 на момент.</summary>
    TrajectoryDirection,

    /// <summary>Положение корня (x, z) в моменты прошлого <see cref="MotionSchema.PastTimes"/> — 2 на момент.</summary>
    PastPosition,

    /// <summary>Скорость корня (x, z), м/с — 2.</summary>
    RootVelocity,

    /// <summary>Угловая скорость курса корня, рад/с — 1.</summary>
    RootYawRate,

    /// <summary>Положение суставов группы (x, y, z), м — 3 на сустав.</summary>
    JointPosition,

    /// <summary>Скорость суставов группы (мировая, выраженная в системе корня кадра) (x, y, z), м/с — 3 на сустав.</summary>
    JointVelocity,

    /// <summary>
    /// Курс по паре суставов «левый, правый» (суставы группы — пары подряд): горизонтальное «вперёд» — (левый − правый),
    /// повёрнутое так, что +X (влево) переходит в +Z (вперёд), единичное, (x, z) — 2 на пару (курс таза по бёдрам, груди —
    /// по ключицам).
    /// </summary>
    JointPairFacing,
}

/// <summary>
/// Группа признаков — вход <see cref="MotionSchema"/>: имя (уникальное), что измеряет, вес по умолчанию (≥ 0) и пол разброса
/// при нормализации <paramref name="MinStd"/> (&gt; 0, в единицах группы: база без поворотов иначе делила бы курс на ноль);
/// <paramref name="Joints"/> — номера суставов скелета (только у видов суставов; массив копируется).
/// </summary>
public readonly record struct FeatureGroupSpec(string Name, FeatureKind Kind, float DefaultWeight, float MinStd, int[]? Joints = null);

/// <summary>
/// Группа признаков схемы: имя, вид, смещение и число признаков в строке, вес по умолчанию, пол разброса. Суставы группы —
/// <see cref="MotionSchema.GroupJoints"/>.
/// </summary>
public readonly record struct FeatureGroup(string Name, FeatureKind Kind, int Offset, int Size, float DefaultWeight, float MinStd)
{
    public int End => Offset + Size;
}

/// <summary>
/// Схема признаков Motion Matching — вход постройки базы (<see cref="MotionDatabase.Build"/>), а не константа движка: какие
/// группы, в каком порядке, с какими суставами (номерами скелета), моменты будущего и прошлого траектории, суставы контактов.
/// Движок не знает ни имён суставов, ни клипов — схему даёт модуль игры.
///
/// Группы лежат в строке признаков подряд в порядке задания; размер группы следует из вида (<see cref="SizeOf"/>).
/// Противоречивая схема — <see cref="ArgumentException"/> при создании. Неизменяема: входные массивы копируются, наружу —
/// только <see cref="ReadOnlySpan{T}"/> (её делят база и все симуляции).
/// </summary>
public sealed class MotionSchema
{
    /// <summary>Групп не больше: столько цен по группам несёт итог поиска (<see cref="MotionGroupCosts"/>).</summary>
    public const int MaxGroups = 16;

    /// <summary>Суставов контактов не больше: контакты кадра — биты байта (<see cref="MotionDatabase.Contacts"/>).</summary>
    public const int MaxContactJoints = 8;

    private readonly float[] _future;
    private readonly float[] _past;
    private readonly FeatureGroup[] _groups;
    private readonly int[] _groupJointStart;
    private readonly int[] _groupJoints;
    private readonly int[] _contactJoints;
    private readonly int[] _joints;
    private readonly float[] _defaultWeights;

    /// <param name="futureTimes">Моменты будущего траектории, с: больше 0, строго по возрастанию (у SWORD — 1/3, 2/3, 1).</param>
    /// <param name="pastTimes">Моменты прошлого, с: меньше 0, от ближнего к дальнему (строго убывают; у SWORD — −1/3).</param>
    /// <param name="groups">Группы в порядке строки признаков: от 1 до <see cref="MaxGroups"/>.</param>
    /// <param name="contactJoints">Суставы, у которых база размечает контакт с землёй (стопы): до <see cref="MaxContactJoints"/>, без повторов.</param>
    /// <exception cref="ArgumentException">Схема противоречива — сообщение называет, чем.</exception>
    public MotionSchema(ReadOnlySpan<float> futureTimes, ReadOnlySpan<float> pastTimes, ReadOnlySpan<FeatureGroupSpec> groups,
        ReadOnlySpan<int> contactJoints = default)
    {
        for (int i = 0; i < futureTimes.Length; i++)
            if (!float.IsFinite(futureTimes[i]) || futureTimes[i] <= 0 || i > 0 && futureTimes[i] <= futureTimes[i - 1])
                throw new ArgumentException($"моменты будущего — больше 0 и строго по возрастанию, а {i}-й — {futureTimes[i]}", nameof(futureTimes));
        for (int i = 0; i < pastTimes.Length; i++)
            if (!float.IsFinite(pastTimes[i]) || pastTimes[i] >= 0 || i > 0 && pastTimes[i] >= pastTimes[i - 1])
                throw new ArgumentException($"моменты прошлого — меньше 0 и от ближнего к дальнему, а {i}-й — {pastTimes[i]}", nameof(pastTimes));
        if (groups.Length is 0 or > MaxGroups)
            throw new ArgumentException($"групп {groups.Length}, а нужно от 1 до {MaxGroups}", nameof(groups));
        if (contactJoints.Length > MaxContactJoints)
            throw new ArgumentException($"суставов контактов {contactJoints.Length}, а можно до {MaxContactJoints}", nameof(contactJoints));
        for (int i = 0; i < contactJoints.Length; i++)
        {
            if (contactJoints[i] < 0) throw new ArgumentException($"сустав контакта {contactJoints[i]} — номер меньше 0", nameof(contactJoints));
            if (contactJoints[..i].Contains(contactJoints[i]))
                throw new ArgumentException($"сустав контакта {contactJoints[i]} повторяется", nameof(contactJoints));
        }

        _future = futureTimes.ToArray();
        _past = pastTimes.ToArray();
        _groups = new FeatureGroup[groups.Length];
        _groupJointStart = new int[groups.Length + 1];
        _defaultWeights = new float[groups.Length];
        var joints = new List<int>();
        var used = new HashSet<(FeatureKind, int)>();
        int offset = 0;
        for (int g = 0; g < groups.Length; g++)
        {
            var spec = groups[g];
            var gj = spec.Joints ?? [];
            Validate(spec, gj, groups[..g]);
            foreach (int j in gj)
                if (spec.Kind != FeatureKind.JointPairFacing && !used.Add((spec.Kind, j)))
                    throw new ArgumentException($"группа «{spec.Name}»: сустав {j} уже измеряется так же другой группой", nameof(groups));
            int size = SizeOf(spec.Kind, gj.Length, _future.Length, _past.Length);
            _groups[g] = new FeatureGroup(spec.Name, spec.Kind, offset, size, spec.DefaultWeight, spec.MinStd);
            _defaultWeights[g] = spec.DefaultWeight;
            _groupJointStart[g] = joints.Count;
            joints.AddRange(gj);
            offset += size;
        }
        _groupJointStart[groups.Length] = joints.Count;
        _groupJoints = [.. joints];
        _contactJoints = contactJoints.ToArray();
        FeatureCount = offset;

        var distinct = new List<int>();
        foreach (int j in _groupJoints) if (!distinct.Contains(j)) distinct.Add(j);
        foreach (int j in _contactJoints) if (!distinct.Contains(j)) distinct.Add(j);
        _joints = [.. distinct];
    }

    private void Validate(in FeatureGroupSpec spec, int[] joints, ReadOnlySpan<FeatureGroupSpec> before)
    {
        if (string.IsNullOrWhiteSpace(spec.Name)) throw new ArgumentException("у группы признаков нет имени", "groups");
        if (!Enum.IsDefined(spec.Kind)) throw new ArgumentException($"группа «{spec.Name}»: неизвестный вид {spec.Kind}", "groups");
        foreach (var other in before)
        {
            if (other.Name == spec.Name) throw new ArgumentException($"группа «{spec.Name}» повторяется", "groups");
            if (!IsJoint(spec.Kind) && other.Kind == spec.Kind)
                throw new ArgumentException($"группа «{spec.Name}»: {spec.Kind} уже есть в группе «{other.Name}»", "groups");
        }
        if (!float.IsFinite(spec.DefaultWeight) || spec.DefaultWeight < 0)
            throw new ArgumentException($"группа «{spec.Name}»: вес {spec.DefaultWeight}, а нужен конечный и не меньше 0", "groups");
        if (!float.IsFinite(spec.MinStd) || spec.MinStd <= 0)
            throw new ArgumentException($"группа «{spec.Name}»: пол разброса {spec.MinStd}, а нужен конечный и больше 0", "groups");
        if (spec.Kind is FeatureKind.TrajectoryPosition or FeatureKind.TrajectoryDirection && _future.Length == 0)
            throw new ArgumentException($"группа «{spec.Name}»: {spec.Kind} без моментов будущего", "groups");
        if (spec.Kind == FeatureKind.PastPosition && _past.Length == 0)
            throw new ArgumentException($"группа «{spec.Name}»: {spec.Kind} без моментов прошлого", "groups");
        if (!IsJoint(spec.Kind))
        {
            if (joints.Length > 0) throw new ArgumentException($"группа «{spec.Name}»: у {spec.Kind} суставов нет, а задано {joints.Length}", "groups");
            return;
        }
        if (joints.Length == 0) throw new ArgumentException($"группа «{spec.Name}»: {spec.Kind} без суставов", "groups");
        if (spec.Kind == FeatureKind.JointPairFacing && joints.Length % 2 != 0)
            throw new ArgumentException($"группа «{spec.Name}»: суставов {joints.Length} — не пары «левый, правый»", "groups");
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] < 0) throw new ArgumentException($"группа «{spec.Name}»: сустав {joints[i]} — номер меньше 0", "groups");
            if (spec.Kind == FeatureKind.JointPairFacing)
            {
                if (i % 2 == 1 && joints[i] == joints[i - 1])
                    throw new ArgumentException($"группа «{spec.Name}»: пара из одного сустава {joints[i]}", "groups");
            }
            else if (joints.AsSpan(0, i).Contains(joints[i]))
                throw new ArgumentException($"группа «{spec.Name}»: сустав {joints[i]} повторяется", "groups");
        }
    }

    /// <summary>Признаков в строке — сумма размеров групп.</summary>
    public int FeatureCount { get; }

    public int GroupCount => _groups.Length;

    /// <summary>Группы в порядке строки признаков.</summary>
    public ReadOnlySpan<FeatureGroup> Groups => _groups;

    /// <summary>Суставы группы <paramref name="group"/> — номера скелета в порядке задания (у пар — «левый, правый» подряд).</summary>
    public ReadOnlySpan<int> GroupJoints(int group) =>
        _groupJoints.AsSpan(_groupJointStart[group], _groupJointStart[group + 1] - _groupJointStart[group]);

    /// <summary>Веса групп по умолчанию — в порядке групп (веса поиска — параметр вызова, это только исходные значения).</summary>
    public ReadOnlySpan<float> DefaultWeights => _defaultWeights;

    /// <summary>Моменты будущего траектории, с (по возрастанию).</summary>
    public ReadOnlySpan<float> FutureTimes => _future;

    /// <summary>Моменты прошлого, с (меньше 0, от ближнего к дальнему).</summary>
    public ReadOnlySpan<float> PastTimes => _past;

    /// <summary>Суставы контактов с землёй — номера скелета; бит i контактов кадра — сустав i этого списка.</summary>
    public ReadOnlySpan<int> ContactJoints => _contactJoints;

    /// <summary>
    /// Все суставы, которые база измеряет (признаки и контакты) — без повторов, в порядке первого появления: групп, затем
    /// контактов. Кинематика клипов считает положения и скорости только их.
    /// </summary>
    public ReadOnlySpan<int> Joints => _joints;

    /// <summary>Горизонт будущего, с — последний момент будущего (0 — будущего нет).</summary>
    public float Horizon => _future.Length > 0 ? _future[^1] : 0;

    /// <summary>Глубина прошлого, с — модуль дальнего момента прошлого (0 — прошлого нет).</summary>
    public float PastDepth => _past.Length > 0 ? -_past[^1] : 0;

    /// <summary>Место сустава скелета <paramref name="joint"/> в <see cref="Joints"/>; не измеряется — −1.</summary>
    public int JointSlot(int joint) => Array.IndexOf(_joints, joint);

    /// <summary>Группа по имени; нет — −1.</summary>
    public int Find(string name)
    {
        for (int g = 0; g < _groups.Length; g++)
            if (_groups[g].Name == name) return g;
        return -1;
    }

    /// <summary>Первая группа вида <paramref name="kind"/>; нет — −1 (у видов траектории и корня группа одна).</summary>
    public int Find(FeatureKind kind)
    {
        for (int g = 0; g < _groups.Length; g++)
            if (_groups[g].Kind == kind) return g;
        return -1;
    }

    /// <summary>Траектория: будущее и прошлое корня — в базе по графу, в запросе — из модели и истории; шов графа её не сравнивает.</summary>
    public static bool IsTrajectory(FeatureKind kind) => kind is FeatureKind.TrajectoryPosition or FeatureKind.TrajectoryDirection or FeatureKind.PastPosition;

    /// <summary>Корень кадра: скорость и угловая скорость — в базе из клипа, в запросе — из модели.</summary>
    public static bool IsRoot(FeatureKind kind) => kind is FeatureKind.RootVelocity or FeatureKind.RootYawRate;

    /// <summary>Суставы: в базе из кинематики клипа, в запросе — признаки текущего кадра базы.</summary>
    public static bool IsJoint(FeatureKind kind) => kind is FeatureKind.JointPosition or FeatureKind.JointVelocity or FeatureKind.JointPairFacing;

    /// <summary>Признаков в группе вида <paramref name="kind"/>.</summary>
    public static int SizeOf(FeatureKind kind, int joints, int futureTimes, int pastTimes) => kind switch
    {
        FeatureKind.TrajectoryPosition or FeatureKind.TrajectoryDirection => 2 * futureTimes,
        FeatureKind.PastPosition => 2 * pastTimes,
        FeatureKind.RootVelocity => 2,
        FeatureKind.RootYawRate => 1,
        FeatureKind.JointPosition or FeatureKind.JointVelocity => 3 * joints,
        FeatureKind.JointPairFacing => joints,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "неизвестный вид группы"),
    };
}
