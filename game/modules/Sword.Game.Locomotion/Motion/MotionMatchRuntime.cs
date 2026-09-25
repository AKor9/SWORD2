using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Motion Matching симуляции (8е): общая база на процесс (<see cref="GaspMotionBase"/>) и своё у симуляции — хранилище проигрывателей
/// (<see cref="MotionPlayers"/>), поиск (<see cref="MotionMatcher"/>), запрос (<see cref="MotionQuery"/>), истории корня по проигрывателям
/// (<see cref="RootHistory"/>, номер — слот проигрывателя) и буфер весов. Авторитетное состояние — проигрыватели и истории: их хеширует и
/// описывает участник <c>locomotion</c> (<see cref="MotionMatchHash"/>); поиск и запрос — рабочие буферы (их содержимое между тиками ни на
/// что не влияет). Заводится при сборке мира (<see cref="Init"/>): база строится из библиотеки клипов пула поз (кэш процесса), ошибка данных
/// — <see cref="InvalidDataException"/> (ошибка старта). Память — при сборке и при первом появлении слота проигрывателя; тик — 0 Б.
/// </summary>
public sealed class MotionMatchRuntime
{
    private RootHistory?[] _histories = [];
    private float _tick;

    public GaspMotionBase Base { get; private set; } = null!;
    public MotionDatabase Database { get; private set; } = null!;
    public MotionPlayers Players { get; private set; } = null!;
    public MotionMatcher Matcher { get; private set; } = null!;
    public MotionQuery Query { get; private set; } = null!;

    /// <summary>Веса групп поиска — буфер на тик (из ручек <see cref="LocomotionRules"/>).</summary>
    public float[] Weights { get; private set; } = [];

    /// <summary>
    /// Кадр позы постройки — первый кадр первого клипа стойки базы (вид <see cref="GaspKind.Idle"/>); стойки в отборе нет — первый кадр
    /// базы. С него начинает проигрыватель после постройки (без инерциализации): это и поза постройки игрока (<see cref="StartPose"/>).
    /// </summary>
    public int StartFrame { get; private set; }

    /// <summary>
    /// Поза постройки игрока на MM — клип библиотеки и момент кадра <see cref="StartFrame"/> (компонент <see cref="FighterStartPose"/>); 8ж — со
    /// слоем руки с мечом (<see cref="SwordArm.Init"/>).
    /// </summary>
    public FighterStartPose StartPose { get; internal set; }

    /// <summary>Окно пропуска своего клипа, кадров (<see cref="GaspMotion.SkipSeconds"/> × частота клипов).</summary>
    public int SkipFrames { get; private set; }

    /// <summary>Заведён (<see cref="Init"/> прошёл).</summary>
    public bool Ready => Database is not null;

    /// <summary>
    /// База из библиотеки пула поз и отбора <paramref name="pattern"/> (кэш процесса — две симуляции строят её один раз), хранилище на
    /// одного бойца с запасом, поиск, запрос, кадр постройки.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные не дают базы MM (нет клипов GASP, нет сустава, имя не разбирается, отбор — не регулярка).</exception>
    public void Init(PosePool pool, string pattern, float tickSeconds)
    {
        ArgumentNullException.ThrowIfNull(pool);
        try
        {
            Base = GaspMotionBase.For(pool.Clips, pattern);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"Motion Matching игрока (locomotion.player = mm) не стартует: {e.Message}", e);
        }
        Database = Base.Database;
        if (Database.Rows.Length == 0)
            throw new InvalidDataException($"Motion Matching игрока (locomotion.player = mm) не стартует: в базе (отбор «{pattern}») нет искомых кадров");
        _tick = tickSeconds;
        Players = new MotionPlayers(Database);
        Players.Reserve(1);
        Matcher = new MotionMatcher(Database);
        Query = new MotionQuery(Database);
        Weights = new float[Database.Schema.GroupCount];
        if (Weights.Length != GaspMotion.WeightKeys.Count)
            throw new InvalidDataException($"схема MM — {Weights.Length} групп, а ручек весов {GaspMotion.WeightKeys.Count}");
        SkipFrames = (int)MathF.Round(GaspMotion.SkipSeconds * Database.SampleRate);
        var names = Base.Names;
        int idle = -1;
        for (int c = 0; c < names.Length && idle < 0; c++)
            if (names[c].Kind == GaspKind.Idle) idle = c;
        var clip = Database.Clips[Math.Max(idle, 0)];
        StartFrame = clip.Start;
        StartPose = new FighterStartPose { Clip = clip.LibraryClip, Time = 0 };
        _histories = new RootHistory?[Players.Capacity];
    }

    /// <summary>История корня проигрывателя <paramref name="player"/> (по слоту; новый слот — новая история).</summary>
    public RootHistory History(MotionPlayerHandle player)
    {
        int i = player.Index;
        if (i >= _histories.Length) Array.Resize(ref _histories, Math.Max(i + 1, _histories.Length * 2));
        return _histories[i] ??= new RootHistory(_tick, Database.Schema.PastDepth > 0 ? Database.Schema.PastDepth : _tick);
    }

    /// <summary>История слота <paramref name="index"/>, если заведена (хеш и описание; свободный слот — не читать).</summary>
    public RootHistory? HistoryAt(int index) => index < _histories.Length ? _histories[index] : null;

    /// <summary>Текущий кадр для запроса и продолжения: ближний к положению проигрывателя; кадр выхода — его вход (цепочка — с предохранителем).</summary>
    public int CurrentFrame(MotionPlayerHandle player)
    {
        int frame = Players.NearestFrame(player);
        if (frame < 0) return frame;
        var entry = Database.Entry;
        for (int n = 0; n < 16 && entry[frame] >= 0; n++) frame = entry[frame];
        return frame;
    }

    /// <summary>
    /// Масштаб данных (8е): скорость клипов шага или бега базы в направлении <paramref name="velocity"/> относительно курса
    /// <paramref name="yaw"/> (<see cref="GaitSpeedModel"/>, походка — по скорости управления) / скорость управления <paramref name="control"/>.
    /// Скорость управления не задана (0) или данных нет — 1. Без него запрос в скорости управления цеплялся бы за середины разгонов клипов.
    /// </summary>
    public float DataScale(Vector3 velocity, float yaw, float control)
    {
        if (!(control > 0)) return 1;
        float direction = Pose2.Wrap(MathF.Atan2(velocity.X, velocity.Z) - yaw);
        float data = Base.Speeds.Speed(direction, control);
        return float.IsFinite(data) && data > 0 ? data / control : 1;
    }
}
