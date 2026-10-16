using System.Text.Json;

namespace Sword.Game.Data;

/// <summary>
/// Слоты действий бойца: ключ слота в <c>combat/actions.json</c>, подпись для просмотрщика, клип пака по умолчанию, можно ли
/// играть слоем верха и слой по умолчанию. Таблицы общие для всех симуляций — наружу только <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public static class ActionSlots
{
    private static readonly string[] AllDirs = ["Forward", "ForwardRight", "Right", "BackRight", "Back", "BackLeft", "Left", "ForwardLeft"];

    private static readonly string[] DirKeys = ["forward", "forward_right", "right", "back_right", "back", "back_left", "left", "forward_left"];
    private static readonly string[] DirTitles = ["вперёд", "вперёд-вправо", "вправо", "назад-вправо", "назад", "назад-влево", "влево", "вперёд-влево"];
    private static readonly string[] AttackSlots = ["slash1", "slash2", "slash3", "slash4"], StabSlots = ["stab1", "stab2", "stab3"];

    private static readonly string[] AllNames, AllTitles, AllDefaults;
    private static readonly bool[] AllLayerable, AllDefaultUpper;

    /// <summary>Ключи слотов (в actions.json), подписи и клипы пака по умолчанию.</summary>
    public static ReadOnlySpan<string> Names => AllNames;
    public static ReadOnlySpan<string> Titles => AllTitles;
    public static ReadOnlySpan<string> Defaults => AllDefaults;

    /// <summary>Слот можно играть слоем верха тела; слой по умолчанию — верх.</summary>
    public static ReadOnlySpan<bool> Layerable => AllLayerable;
    public static ReadOnlySpan<bool> DefaultUpper => AllDefaultUpper;

    /// <summary>Номер слота стойки; ходьбы и бега в сторону <c>d</c> — <see cref="Walk"/>(d), <see cref="Run"/>(d).</summary>
    public const int Idle = 0;

    public static int Walk(int dir) => 1 + dir;
    public static int Run(int dir) => 9 + dir;

    /// <summary>Слоты поворота на месте: 90° влево, вправо, разворот.</summary>
    public static int TurnLeft { get; }
    public static int TurnRight { get; }
    public static int Turn180 { get; }

    /// <summary>Слоты уклонения — вид рывка.</summary>
    public static int EvadeForward { get; }
    public static int EvadeBack { get; }
    public static int EvadeLeft { get; }
    public static int EvadeRight { get; }

    /// <summary>
    /// Слоты реакции на попадание: спереди — два клипа (выбор — ГСЧ), сзади — один.
    /// </summary>
    public static int HitFront1 { get; }
    public static int HitFront2 { get; }
    public static int HitBack { get; }

    static ActionSlots()
    {
        var names = new List<string> { "idle" };
        var titles = new List<string> { "стойка" };
        var defaults = new List<string> { "1HM_Idle_Standing" };
        for (int d = 0; d < 8; d++) { names.Add("walk_" + DirKeys[d]); titles.Add("ходьба " + DirTitles[d]); defaults.Add("1HM_Walk" + AllDirs[d]); }
        for (int d = 0; d < 8; d++) { names.Add("run_" + DirKeys[d]); titles.Add("бег " + DirTitles[d]); defaults.Add("1HM_Run" + AllDirs[d]); }
        for (int i = 0; i < 4; i++) { names.Add(AttackSlots[i]); titles.Add($"рубящий {i + 1} (связка)"); defaults.Add($"1HM_Slash0{i + 1}"); }
        for (int i = 0; i < 3; i++) { names.Add(StabSlots[i]); titles.Add($"укол {i + 1} (Q)"); defaults.Add($"1HM_Stab0{i + 1}"); }
        names.AddRange(["block_left", "block_right", "block_left_low", "block_right_low", "hit_front1", "hit_front2", "hit_back",
            "evade_forward", "evade_back", "evade_left", "evade_right", "turn_left", "turn_right", "turn_180"]);
        titles.AddRange(["блок влево", "блок вправо", "блок влево низко", "блок вправо низко", "попадание спереди 1", "попадание спереди 2",
            "попадание сзади", "уклонение вперёд", "уклонение назад", "уклонение влево", "уклонение вправо",
            "поворот влево", "поворот вправо", "разворот"]);
        defaults.AddRange(["1HM_BlockLeft", "1HM_BlockRight", "1HM_BlockLeftLow", "1HM_BlockRightLow", "1HM_HitReact01", "1HM_HitReact02",
            "1HM_HitReact_Behind01", "1HM_Evade_Forward", "1HM_Evade_Back", "1HM_Evade_Left", "1HM_Evade_Right",
            "1HM_TurnLeft90", "1HM_TurnRight90", "1HM_Turn180"]);
        AllNames = [.. names];
        AllTitles = [.. titles];
        AllDefaults = [.. defaults];
        AllLayerable = AllNames.Select(n => n.StartsWith("slash") || n.StartsWith("stab") || n.StartsWith("block_") || n.StartsWith("hit_")).ToArray();
        AllDefaultUpper = AllNames.Select(n => n.StartsWith("slash") || n.StartsWith("stab") || n.StartsWith("block_")).ToArray();
        TurnLeft = Index("turn_left");
        TurnRight = Index("turn_right");
        Turn180 = Index("turn_180");
        EvadeForward = Index("evade_forward");
        EvadeBack = Index("evade_back");
        EvadeLeft = Index("evade_left");
        EvadeRight = Index("evade_right");
        HitFront1 = Index("hit_front1");
        HitFront2 = Index("hit_front2");
        HitBack = Index("hit_back");
    }

    /// <summary>Номер слота по ключу; нет — −1.</summary>
    public static int Index(string name) => Array.IndexOf(AllNames, name);
}

/// <summary>
/// Клипы слотов действий для одной библиотеки клипов: клип по умолчанию (нет — клип 0 с предупреждением), поверх — выбор из
/// <c>combat/actions.json</c>. Ошибки выбора — предупреждения, прежнее значение остаётся. Клип ищет переданная функция.
/// </summary>
public sealed class ActionTable
{
    /// <summary>Выбор действий в контенте (копия <c>_Sword/anim/actions.json</c>, рецепт <c>Bake/recipes/src/_Sword/anim/actions.json.bake.json</c>).</summary>
    public const string ActionsFile = "data/combat/actions.json";

    private const string LayerSuffix = ".layer";

    private readonly int[] _clip = new int[ActionSlots.Names.Length];
    private readonly bool[] _upper = new bool[ActionSlots.Names.Length];

    /// <summary>Клип каждого слота (<see cref="ActionSlots.Names"/>) в библиотеке. Таблица неизменна после <see cref="Resolve"/>.</summary>
    public ReadOnlySpan<int> Clip => _clip;

    /// <summary>Слой слота: true — верх тела.</summary>
    public ReadOnlySpan<bool> Upper => _upper;

    /// <summary>Клип стойки и клипы ходьбы и бега в сторону <paramref name="dir"/> — слоты локомоции.</summary>
    public int Idle => Clip[ActionSlots.Idle];
    public int Walk(int dir) => Clip[ActionSlots.Walk(dir)];
    public int Run(int dir) => Clip[ActionSlots.Run(dir)];

    /// <summary>Клипы поворота на месте: 90° влево, вправо и разворот.</summary>
    public int TurnLeft => Clip[ActionSlots.TurnLeft];
    public int TurnRight => Clip[ActionSlots.TurnRight];
    public int Turn180 => Clip[ActionSlots.Turn180];

    /// <param name="findClip">Клип по ID («источник/имя» или имя, если оно одно); −1 — нет.</param>
    /// <param name="clipName">Имя клипа по номеру — для предупреждений.</param>
    /// <param name="actionsJson">Содержимое actions.json; null — только клипы по умолчанию.</param>
    /// <exception cref="InvalidDataException">actions.json — не объект «строка → строка».</exception>
    public static ActionTable Resolve(Func<string, int> findClip, Func<int, string> clipName, string? actionsJson, ICollection<string> warnings)
    {
        var t = new ActionTable();
        for (int i = 0; i < ActionSlots.Names.Length; i++)
        {
            int c = findClip(ActionSlots.Defaults[i]);
            if (c < 0) warnings.Add($"нет клипа {ActionSlots.Defaults[i]} для «{ActionSlots.Titles[i]}» — стоит {clipName(0)}");
            t._clip[i] = Math.Max(c, 0);
            t._upper[i] = ActionSlots.DefaultUpper[i];
        }
        if (actionsJson is null) return t;

        Dictionary<string, string> map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, string>>(actionsJson) ?? [];
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{ActionsFile}: {e.Message}", e);
        }
        foreach (var (slot, value) in map)
        {
            if (slot.EndsWith(LayerSuffix, StringComparison.Ordinal))
            {
                int s = ActionSlots.Index(slot[..^LayerSuffix.Length]);
                if (s < 0 || !ActionSlots.Layerable[s]) warnings.Add($"{ActionsFile}: слой «{slot}» — нет такого действия со слоями");
                else if (value is not ("upper" or "full")) warnings.Add($"{ActionsFile}: слой «{slot}» = «{value}» — ожидается upper или full");
                else t._upper[s] = value == "upper";
                continue;
            }
            int i = ActionSlots.Index(slot), clip = findClip(value);
            if (i < 0) warnings.Add($"{ActionsFile}: неизвестное действие «{slot}»");
            else if (clip < 0) warnings.Add($"{ActionsFile}: нет клипа «{value}» для «{slot}» — оставлен {clipName(t._clip[i])}");
            else t._clip[i] = clip;
        }
        return t;
    }
}
