using System.Diagnostics;
using System.Numerics;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Diagnostics;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Диагностика Motion Matching симуляции (8з): по слоту проигрывателя — последний поиск (тик, повод, корень «сейчас» запроса, текущий кадр,
/// итог, сменил ли кадр, запрос в сырых единицах — для траекторий вида и оверлея), запрошенный переход тика (для события
/// <see cref="MotionTransition"/>) и время поиска; зона профайлера <see cref="Zone"/> вокруг запроса и перебора. Всё это — производное
/// авторитетного состояния тика (запрос и итог поиска повторяются при воспроизведении), кроме времени (от машины): в хеш и реплей не входит,
/// поведение от него не зависит. Запись — в <c>locomotion.mm</c> (поиск), чтение — вид, описание участника <c>locomotion</c>, событие перехода.
/// Память — при первом поиске слота; тик — 0 Б.
/// </summary>
public sealed class MotionMatchDiagnostics
{
    /// <summary>Зона профайлера поиска (<c>profile.stages</c>): запрос и перебор, под <c>locomotion.mm@Animation</c>.</summary>
    public const string Zone = "locomotion.search";

    /// <summary>Последний поиск слота (запись — только <see cref="Searched"/>).</summary>
    public sealed class Record(int features)
    {
        /// <summary>Поколение проигрывателя, чей это поиск (другое — поиск прошлой жизни слота: не показывать).</summary>
        public int Generation;
        public long Tick = -1;
        public MotionSearchReason Reason;

        /// <summary>Корень «сейчас» запроса (мир).</summary>
        public Pose2 Root;
        public int Current = -1;
        public MotionMatch Match;
        public bool Switched;

        /// <summary>Тик и кадр запрошенного перехода (поиск сменил кадр); −1 — не было.</summary>
        public long RequestedTick = -1;
        public int Requested = -1;

        /// <summary>Время поиска (запрос и перебор), мкс: последнего и наибольшее с постройки.</summary>
        public double Microseconds, MaxMicroseconds;

        /// <summary>Запрос в сырых единицах (<see cref="MotionQuery.Raw"/>) — копия.</summary>
        public readonly float[] Query = new float[features];
    }

    private readonly MotionDatabase _db;
    private Record?[] _records = [];
    private CpuProfiler _profiler = CpuProfiler.Off;
    private int _zone;
    private long _started;

    public MotionMatchDiagnostics(MotionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
    }

    /// <summary>Поиск слота <paramref name="player"/> этой жизни проигрывателя; не было — null.</summary>
    public Record? Last(MotionPlayerHandle player) =>
        !player.IsNone && player.Index < _records.Length && _records[player.Index] is { } r && r.Generation == player.Generation && r.Tick >= 0 ? r : null;

    /// <summary>
    /// Профайлер мира (<c>SystemContext.World.Profiler</c>) — в начале <c>locomotion.mm</c>: сменился — зона <see cref="Zone"/> заводится в
    /// нём (раз на профайлер; у <see cref="CpuProfiler.Off"/> — без регистрации).
    /// </summary>
    public void Attach(CpuProfiler profiler)
    {
        if (ReferenceEquals(profiler, _profiler)) return;
        _zone = profiler.Zone(Zone);
        _profiler = profiler;
    }

    /// <summary>
    /// Повод поиска — по памяти поиска до него: первый после постройки, конец действия всем телом (прошлый тик был без поиска), скачок цели,
    /// иначе расписание (порядок — от редкого к частому: условия могут совпасть).
    /// </summary>
    public static MotionSearchReason Reason(in MotionMatchState s, Vector3 goal) =>
        s.Searches == 0 ? MotionSearchReason.Start
            : !s.SearchWas ? MotionSearchReason.AfterAction
            : MotionMatchSystem.GoalJumped(s.SearchGoal, goal) ? MotionSearchReason.GoalJump
            : MotionSearchReason.Schedule;

    /// <summary>Начало поиска (запрос и перебор): зона профайлера и время.</summary>
    public void Begin()
    {
        _profiler.Begin(_zone);
        _started = Stopwatch.GetTimestamp();
    }

    /// <summary>Конец поиска; вернёт его время, мкс.</summary>
    public double End()
    {
        long ended = Stopwatch.GetTimestamp();
        _profiler.End(_zone);
        return (ended - _started) * 1e6 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Поиск проигрывателя <paramref name="player"/> в тике <paramref name="tick"/>: повод, корень запроса, текущий кадр, итог, сменил ли кадр
    /// (запрошенный переход в кадр итога — в этом тике), время, запрос (<paramref name="query"/> — сырые единицы).
    /// </summary>
    public void Searched(MotionPlayerHandle player, long tick, MotionSearchReason reason, Pose2 root, int current, in MotionMatch match,
        bool switched, double microseconds, ReadOnlySpan<float> query)
    {
        var r = Slot(player.Index);
        if (r.Generation != player.Generation)
        {
            r.Generation = player.Generation;
            r.MaxMicroseconds = 0;
            r.RequestedTick = -1;
            r.Requested = -1;
        }
        r.Tick = tick;
        r.Reason = reason;
        r.Root = root;
        r.Current = current;
        r.Match = match;
        r.Switched = switched;
        r.Microseconds = microseconds;
        r.MaxMicroseconds = Math.Max(r.MaxMicroseconds, microseconds);
        if (switched)
        {
            r.RequestedTick = tick;
            r.Requested = match.Frame;
        }
        query.CopyTo(r.Query);
    }

    private Record Slot(int index)
    {
        if (index >= _records.Length) Array.Resize(ref _records, Math.Max(index + 1, _records.Length * 2));
        return _records[index] ??= new Record(_db.FeatureCount);
    }
}
