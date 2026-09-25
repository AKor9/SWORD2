using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// База Motion Matching на процесс: отбор клипов GASP, база (<see cref="MotionDatabase"/>) с политикой GASP, модель скоростей данных,
/// счёт политики. Неизменяема и общая — её делят все симуляции процесса (как <see cref="ClipLibrary"/>).
///
/// Кэш — по экземпляру <see cref="ClipLibrary"/> (<see cref="ConditionalWeakTable{TKey, TValue}"/>: библиотека не держится кэшем) и
/// регулярке отбора; постройка ленивая и потокобезопасная (<see cref="Lazy{T}"/>, одна на ключ даже при одновременных запросах).
/// Модуль берёт библиотеку из ресурса <see cref="PosePool"/> мира (<see cref="PosePool.Clips"/> — у всех симуляций процесса один
/// экземпляр <c>CharacterAssets.Clips</c>): две симуляции строят базу один раз. Ошибка данных тоже запоминается — повторный запрос
/// бросает то же исключение, не строя заново (данные те же — итог тот же).
/// </summary>
public sealed class GaspMotionBase
{
    private static readonly ConditionalWeakTable<ClipLibrary, ConcurrentDictionary<string, Lazy<GaspMotionBase>>> Cache = new();

    private readonly GaspClipName[] _names;
    private readonly int[] _withoutContinuation;

    private GaspMotionBase(GaspSelection selection, MotionDatabase database, GaitSpeedModel speeds, GaspPolicyStats policy)
    {
        Pattern = selection.Pattern;
        SourceClips = selection.SourceClips;
        _names = selection.Names.ToArray();
        Database = database;
        Speeds = speeds;
        Policy = policy;
        var without = new List<int>();
        var clips = database.Clips;
        var entry = database.Entry;
        for (int c = 0; c < clips.Length; c++)
        {
            if (clips[c].Loop) continue;
            bool edge = false;
            for (int f = clips[c].Start; f < clips[c].End && !edge; f++) edge = entry[f] >= 0;
            if (!edge) without.Add(c);
        }
        _withoutContinuation = [.. without];
    }

    /// <summary>Регулярка отбора клипов.</summary>
    public string Pattern { get; }

    /// <summary>Клипов источника <see cref="GaspMotion.Source"/> в библиотеке (до отбора).</summary>
    public int SourceClips { get; }

    public MotionDatabase Database { get; }

    public GaitSpeedModel Speeds { get; }

    /// <summary>Счёт политики графа за постройку: ветки продолжений, прошлое.</summary>
    public GaspPolicyStats Policy { get; }

    /// <summary>Имена клипов базы — по номеру клипа базы (<see cref="MotionDatabase.Clips"/>).</summary>
    public ReadOnlySpan<GaspClipName> Names => _names;

    /// <summary>Нециклические клипы базы без продолжения (ни одного кадра выхода) — номера клипов базы.</summary>
    public ReadOnlySpan<int> WithoutContinuation => _withoutContinuation;

    /// <summary>
    /// База для библиотеки и отбора — из кэша процесса, при первом запросе строится (<see cref="Build"/>). <paramref name="pattern"/> null —
    /// <see cref="GaspMotion.DefaultClips"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные не дают базы (нет клипов, нет сустава, имя не разбирается…) — с понятной причиной.</exception>
    /// <exception cref="ArgumentException">Отбор — не регулярка.</exception>
    public static GaspMotionBase For(ClipLibrary library, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        string key = pattern ?? GaspMotion.DefaultClips;
        var byPattern = Cache.GetValue(library, _ => new ConcurrentDictionary<string, Lazy<GaspMotionBase>>(StringComparer.Ordinal));
        return byPattern.GetOrAdd(key, k => new Lazy<GaspMotionBase>(() => Build(library, k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// Постройка без кэша: отбор (<see cref="GaspMotion.Select"/>), схема GASP (<see cref="GaspMotion.Schema"/>), политика
    /// (<see cref="GaspGraphPolicy"/>), параметры (<see cref="GaspMotion.BuildParams"/>), модель скоростей. Игра зовёт <see cref="For"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные не дают базы — с понятной причиной.</exception>
    /// <exception cref="ArgumentException">Отбор — не регулярка.</exception>
    public static GaspMotionBase Build(ClipLibrary library, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        var selection = GaspMotion.Select(library, pattern);
        var schema = GaspMotion.Schema(library.Skeleton);
        var policy = new GaspGraphPolicy(selection.Clips, selection.Names);
        MotionDatabase database;
        GaitSpeedModel speeds;
        try
        {
            database = MotionDatabase.Build(library, selection.Clips, schema, policy, GaspMotion.BuildParams);
            speeds = GaitSpeedModel.From(database, selection.Names, GaspMotion.RunInputSpeed);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"база Motion Matching (отбор «{selection.Pattern}») не строится: {e.Message}", e);
        }
        return new GaspMotionBase(selection, database, speeds, policy.Stats);
    }

    /// <summary>
    /// Строка старта (журнал): клипов, кадров, искомых, рёбер, ранних выходов, время постройки, отпечаток, отбор; клипы без продолжения —
    /// если есть.
    /// </summary>
    public string StartLine()
    {
        var s = Database.Summary;
        var inv = CultureInfo.InvariantCulture;
        string without = _withoutContinuation.Length == 0 ? "" : $", без продолжения {_withoutContinuation.Length}";
        return string.Create(inv, $"база MM: клипов {s.Clips} из {SourceClips} «{GaspMotion.Source}», кадров {s.Frames}, искомых {s.Searchable}, " +
            $"рёбер {s.Edges}, ранних выходов {s.EarlyExits}{without}, постройка {s.BuildMilliseconds:F0} мс, отпечаток {Database.Fingerprint:X16}, " +
            $"отбор «{Pattern}»");
    }

    /// <summary>Модель скоростей одной строкой: шаг и бег по секторам (вперёд, влево…, по 45°), м/с; «*» — сектор достроен.</summary>
    public string SpeedsLine()
    {
        return $"скорости данных: шаг [{Row(Speeds.WalkSectors, Speeds.WalkLoops)}], бег [{Row(Speeds.RunSectors, Speeds.RunLoops)}], " +
            string.Create(CultureInfo.InvariantCulture, $"бег при вводе > {Speeds.RunInputSpeed:0.##} м/с");

        static string Row(ReadOnlySpan<float> sectors, ReadOnlySpan<int> loops)
        {
            var parts = new string[sectors.Length];
            for (int s = 0; s < sectors.Length; s++)
                parts[s] = string.Create(CultureInfo.InvariantCulture, $"{s * 45}° {sectors[s]:0.00}{(loops[s] == 0 ? "*" : "")}");
            return string.Join(", ", parts);
        }
    }
}
