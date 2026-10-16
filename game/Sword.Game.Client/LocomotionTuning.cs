using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Вкладка «Локомоция» панели ручек F3 (механизм — как <see cref="CombatTuning"/>): ручки секции <c>locomotion</c> (клиент
/// модуль не видит — ручки берутся по секции, <see cref="TuningBinding.AddSection"/>) и строки состояния Motion Matching
/// игрока по <see cref="IMotionMatchView"/>; переходов в секунду — за последние 2 с игрового времени. Группа есть, только
/// когда есть вид (<c>locomotion.player = mm</c>). Кадр панели без выделений.
/// </summary>
public sealed class LocomotionTuning
{
    public const string Id = "locomotion", Title = "Локомоция";

    /// <summary>Окно счёта переходов в секунду, с игрового времени.</summary>
    public const float RateWindow = 2f;

    private readonly SimulationLink _link;
    private readonly ArenaView _view;
    private float _windowStart = float.NaN;
    private int _windowTransitions;
    private float _rate = float.NaN;
    private Sword.Engine.Ecs.Entity _rateOf;

    private LocomotionTuning(SimulationLink link, ArenaView view)
    {
        _link = link;
        _view = view;
    }

    /// <summary>Группа «Локомоция»; вида MM нет (игрок клипами) — null.</summary>
    public static TuningGroup? Build(TuningBinding tuning, SimulationLink link, ArenaView view)
    {
        if (!link.World.TryView<IMotionMatchView>(out _)) return null;
        var rows = new LocomotionTuning(link, view);
        var g = new TuningGroup(Id, Title);
        g.Header("MOTION MATCHING ИГРОКА");
        g.Info("клип", rows.Clip, "клип базы, который играет проигрыватель (имя без источника), и кадр в клипе");
        g.Info("поиск", rows.Search,
            "последний поиск: цена найденного кадра / цена продолжения текущего (меньше — лучше), повод, сменил ли кадр");
        g.Info("время поиска", rows.SearchTime, "запрос и перебор последнего поиска и наибольшее с постройки, мс (зона профайлера locomotion.search)");
        g.Info("переходов/с", rows.Transitions, "переходов проигрывателя (поиском и по рёбрам графа) в секунду за последние 2 с игры; всего с постройки");
        g.Info("поворот на месте", rows.Turn,
            "стоя: δ — насколько курс корня (взгляд) ушёл от ног (корпус скручен, ноги стоят), грудь — выравнивание груди к курсу корня; поворотов клипом Stand_Turn с постройки (* — идёт)");
        // Ручки секции — по порядку регистрации модуля: поиск и переход, веса групп («вес: …»), вес руки с мечом, поворот на месте; клиент
        // Runtime не видит — по секции.
        g.Header("ПОИСК, ПЕРЕХОД, ВЕСА ГРУПП, РУКА, ПОВОРОТ");
        tuning.AddSection(g, Id);
        return g;
    }

    private bool Status(out IMotionMatchView view, out Sword.Engine.Ecs.Entity player, out MotionMatchStatus status)
    {
        status = default;
        player = default;
        view = null!;
        if (_view.Player is not { } p || !_link.World.TryView(out IMotionMatchView? mm) || !mm.TryStatus(p.Entity, out status)) return false;
        view = mm;
        player = p.Entity;
        return true;
    }

    /// <summary>Клип в строке — имя без источника, не длиннее стольких знаков (длиннее — «..» и хвост: строка панели узкая).</summary>
    public const int ClipChars = 30;

    /// <summary>«M_Neutral_Run_Loop_F · 17» — клип игрока (имя без источника; длинное — «..» и хвост) и кадр в клипе; нет — «—».</summary>
    private int Clip(Span<char> d)
    {
        if (!Status(out _, out _, out var s) || !_link.World.TryView<IAnimationView>(out var anim) || (uint)s.Clip >= (uint)anim.ClipCount)
            return Length(new Line(d).S("—").Text);
        var name = anim.ClipName(s.Clip).AsSpan();
        name = name[(name.LastIndexOf('/') + 1)..];
        var line = new Line(d);
        if (name.Length > ClipChars)
        {
            line = line.S("..");
            name = name[^(ClipChars - 2)..];
        }
        return Length(line.S(name).S(" · ").I(s.ClipFrame).Text);
    }

    /// <summary>«1.27 / 3.40, расписание, сменил» — цена найденного / продолжения, повод, сменил ли; поиска не было — «—».</summary>
    private int Search(Span<char> d)
    {
        if (!Status(out var mm, out var player, out _) || !mm.TrySearch(player, out var q)) return Length(new Line(d).S("—").Text);
        var line = new Line(d).F(q.Cost, "F2").S(" / ");
        line = q.ContinuationCost < 0 ? line.S("—") : line.F(q.ContinuationCost, "F2");
        return Length(line.S(", ").S(Reason(q.Reason)).S(q.Switched ? ", сменил" : "").Text);
    }

    /// <summary>«0.041 мс (макс 0.210)» — время последнего поиска и наибольшее; поиска не было — «—».</summary>
    private int SearchTime(Span<char> d)
    {
        if (!Status(out var mm, out var player, out _) || !mm.TrySearch(player, out var q)) return Length(new Line(d).S("—").Text);
        return Length(new Line(d).F(q.Microseconds / 1000, "F3").S(" мс (макс ").F(q.MaxMicroseconds / 1000, "F3").S(")").Text);
    }

    /// <summary>«δ 23.4° · грудь 1.2° · поворотов 3*» — расхождение корня и ног, выравнивание груди, поворотов (* — клип поворота идёт); нет — «—».</summary>
    private int Turn(Span<char> d)
    {
        if (!Status(out _, out _, out var s)) return Length(new Line(d).S("—").Text);
        const float Deg = 180 / MathF.PI;
        return Length(new Line(d).S("δ ").F(s.Twist * Deg, "F1").S("° · грудь ").F(s.Chest * Deg, "F1").S("° · поворотов ").I(s.Turns)
            .S(s.TurnClip >= 0 ? "*" : "").Text);
    }

    /// <summary>«2.5 (всего 41)» — переходов в секунду за окно и всего с постройки; окно ещё не набрано — «— (всего N)».</summary>
    private int Transitions(Span<char> d)
    {
        if (!Status(out _, out var player, out var s)) return Length(new Line(d).S("—").Text);
        float time = _link.Time;
        // Окно: счётчик переходов в начале окна; прошло 2 с — частота за окно, новое окно. Постройка заново (счётчик меньше) — окно заново.
        if (player != _rateOf || float.IsNaN(_windowStart) || s.Transitions < _windowTransitions || time < _windowStart)
        {
            _rateOf = player;
            _windowStart = time;
            _windowTransitions = s.Transitions;
            _rate = float.NaN;
        }
        else if (time - _windowStart >= RateWindow)
        {
            _rate = (s.Transitions - _windowTransitions) / (time - _windowStart);
            _windowStart = time;
            _windowTransitions = s.Transitions;
        }
        var line = float.IsNaN(_rate) ? new Line(d).S("—") : new Line(d).F(_rate, "F1");
        return Length(line.S(" (всего ").I(s.Transitions).S(")").Text);
    }

    /// <summary>Повод поиска словами (коротко — строка панели).</summary>
    public static string Reason(MotionSearchReason reason) => reason switch
    {
        MotionSearchReason.Start => "после постройки",
        MotionSearchReason.GoalJump => "скачок цели",
        MotionSearchReason.AfterAction => "после действия",
        MotionSearchReason.Schedule => "расписание",
        _ => "—",
    };

    private static int Length(ReadOnlySpan<char> text) => text.Length;
}
