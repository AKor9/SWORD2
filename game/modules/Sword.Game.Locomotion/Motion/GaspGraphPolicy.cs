using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>Какой веткой политики графа нашлось продолжение нециклического клипа (<see cref="GaspGraphPolicy"/>).</summary>
public enum GaspBranch : byte
{
    /// <summary>Выход медленнее <see cref="GaspMotion.StandSpeed"/> — стойка (клипы вида <see cref="GaspKind.Idle"/>).</summary>
    Stand,

    /// <summary>Циклы хода той же походки в пределах <see cref="GaspMotion.DirectionWindow"/> от направления выхода — основная ветка.</summary>
    SameGaitDirection,

    /// <summary>Запасная: циклы хода той же походки в любом направлении.</summary>
    SameGait,

    /// <summary>Запасная: любые циклы хода.</summary>
    AnyMoving,

    /// <summary>Запасная: стойка для движущегося выхода.</summary>
    StandFallback,

    /// <summary>Кандидатов нет: у клипа нет продолжения (кадры без полного горизонта будущего не ищутся).</summary>
    None,
}

/// <summary>
/// Политика графа GASP (<see cref="IMotionGraphPolicy"/>) — знание о контенте, которого нет у движка. Зовётся только при постройке базы.
///
/// Продолжение нециклического клипа — по измеренному выходу (<see cref="MotionClipInfo"/>): медленнее <see cref="GaspMotion.StandSpeed"/> —
/// стойка (вид <see cref="GaspKind.Idle"/> по имени); иначе циклы хода (цикл данных со средней скоростью не ниже порога) той же
/// походки по имени, чья средняя скорость — в пределах <see cref="GaspMotion.DirectionWindow"/> от направления выхода; нет — запасные
/// ветки: та же походка в любом направлении, любые циклы хода, стойка. Лучший кадр входа среди кандидатов база выбирает сама ценой шва.
///
/// Прошлое у начала клипа — по измеренной скорости начала, а не по виду из имени: медленнее порога — «стоял», иначе — «шёл со
/// скоростью начала» (идея — урок SWORD: <c>Run_Reface_Start_*</c> по имени — старт, а на паке входят с 0,41–0,49 м/с средней за
/// 0,1 с). Средняя за окно у ускоряющегося старта завышает скорость первого кадра (там 0,29–0,40 м/с) — точка прошлого у первых кадров
/// таких клипов отодвинута на 0,1–0,16 м больше правды; это ~0,2 разброса группы прошлого при весе 0,5 (README модуля).
///
/// Стойка — по имени, а не по измерению: медленный цикл — не обязательно стойка (стрейф по кругу почти на месте держал персонажа
/// на круге). Походка — по имени: скорость выхода нециклического клипа (разворот, смена направления, поворот) бывает между шагом и бегом.
/// </summary>
public sealed class GaspGraphPolicy : IMotionGraphPolicy
{
    private readonly Dictionary<int, GaspClipName> _names;
    private readonly int[] _branches = new int[(int)GaspBranch.None + 1];
    private int _standingPast, _movingPast;

    /// <param name="libraryClips">Клипы библиотеки базы (<see cref="GaspSelection.Clips"/>).</param>
    /// <param name="names">Их имена — в том же порядке.</param>
    public GaspGraphPolicy(ReadOnlySpan<int> libraryClips, ReadOnlySpan<GaspClipName> names)
    {
        if (libraryClips.Length != names.Length)
            throw new ArgumentException($"клипов {libraryClips.Length}, а имён {names.Length}", nameof(names));
        _names = new Dictionary<int, GaspClipName>(libraryClips.Length);
        for (int i = 0; i < libraryClips.Length; i++) _names.Add(libraryClips[i], names[i]);
    }

    public GaspPolicyStats Stats => new(_branches[(int)GaspBranch.Stand], _branches[(int)GaspBranch.SameGaitDirection],
        _branches[(int)GaspBranch.SameGait], _branches[(int)GaspBranch.AnyMoving], _branches[(int)GaspBranch.StandFallback],
        _branches[(int)GaspBranch.None], _standingPast, _movingPast);

    public void Continuations(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates)
    {
        var branch = Choose(clip, clips, candidates);
        _branches[(int)branch]++;
    }

    /// <summary>Кандидаты и ветка — без счёта (для тестов и <see cref="Continuations"/>).</summary>
    public GaspBranch Choose(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates)
    {
        candidates.Clear();
        var exit = clips[clip];
        if (exit.ExitSpeed < GaspMotion.StandSpeed)
            return AddStand(clips, candidates) ? GaspBranch.Stand : GaspBranch.None;
        var gait = Name(exit.LibraryClip).Gait;
        float direction = exit.ExitDirection;
        for (int d = 0; d < clips.Length; d++)
            if (IsMovingLoop(clips[d]) && Name(clips[d].LibraryClip).Gait == gait
                && MathF.Abs(Pose2.Wrap(clips[d].MeanDirection - direction)) <= GaspMotion.DirectionWindow)
                candidates.Add(d);
        if (candidates.Count > 0) return GaspBranch.SameGaitDirection;
        for (int d = 0; d < clips.Length; d++)
            if (IsMovingLoop(clips[d]) && Name(clips[d].LibraryClip).Gait == gait) candidates.Add(d);
        if (candidates.Count > 0) return GaspBranch.SameGait;
        for (int d = 0; d < clips.Length; d++)
            if (IsMovingLoop(clips[d])) candidates.Add(d);
        if (candidates.Count > 0) return GaspBranch.AnyMoving;
        return AddStand(clips, candidates) ? GaspBranch.StandFallback : GaspBranch.None;
    }

    public MotionPast Past(int clip, ReadOnlySpan<MotionClipInfo> clips)
    {
        var past = PastOf(clips[clip]);
        if (past == MotionPast.Standing) _standingPast++;
        else _movingPast++;
        return past;
    }

    /// <summary>Прошлое по измеренной скорости начала: медленнее <see cref="GaspMotion.StandSpeed"/> — «стоял».</summary>
    public static MotionPast PastOf(in MotionClipInfo clip) =>
        clip.StartVelocity.Length() < GaspMotion.StandSpeed ? MotionPast.Standing : MotionPast.StartVelocity;

    /// <summary>Цикл хода: цикл данных со средней скоростью не ниже <see cref="GaspMotion.StandSpeed"/>.</summary>
    public static bool IsMovingLoop(in MotionClipInfo clip) => clip.Loop && clip.MeanSpeed >= GaspMotion.StandSpeed;

    private bool AddStand(ReadOnlySpan<MotionClipInfo> clips, List<int> candidates)
    {
        for (int d = 0; d < clips.Length; d++)
            if (Name(clips[d].LibraryClip).Kind == GaspKind.Idle) candidates.Add(d);
        return candidates.Count > 0;
    }

    private GaspClipName Name(int libraryClip) => _names.TryGetValue(libraryClip, out var name)
        ? name
        : throw new ArgumentException($"политика GASP: клип библиотеки {libraryClip} не из отбора базы", nameof(libraryClip));
}

/// <summary>
/// Счёт политики графа за постройку: продолжений по веткам (<see cref="GaspBranch"/>) и прошлого у начала нециклических клипов
/// («стоял» / «шёл со скоростью начала»).
/// </summary>
public readonly record struct GaspPolicyStats(int Stand, int SameGaitDirection, int SameGait, int AnyMoving, int StandFallback, int None,
    int StandingPast, int MovingPast)
{
    public override string ToString() =>
        $"продолжения: стойка {Stand}, та же походка ±30° {SameGaitDirection}, та же походка {SameGait}, любой ход {AnyMoving}, " +
        $"стойка запасная {StandFallback}, нет {None}; прошлое: стоял {StandingPast}, шёл {MovingPast}";
}
