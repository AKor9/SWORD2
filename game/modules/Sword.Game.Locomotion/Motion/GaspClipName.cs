namespace Sword.Game.Locomotion.Motion;

/// <summary>Вид клипа локомоции GASP — по имени (<see cref="GaspClipName"/>).</summary>
public enum GaspKind : byte
{
    /// <summary>Стойка: <c>Stand_Idle[_Loop]</c>.</summary>
    Idle,

    /// <summary>Цикл хода: <c>&lt;Походка&gt;_Loop_&lt;Направление&gt;[_&lt;L|R&gt;_&lt;угол&gt;]</c>.</summary>
    Loop,

    /// <summary>Старт с места: <c>&lt;Походка&gt;_Start_&lt;Направление&gt;_&lt;Нога&gt;</c>.</summary>
    Start,

    /// <summary>Остановка: <c>&lt;Походка&gt;_Stop_&lt;Направление&gt;_&lt;Нога&gt;</c>.</summary>
    Stop,

    /// <summary>Разворот хода: <c>&lt;Походка&gt;_Pivot_&lt;Откуда&gt;_&lt;Куда&gt;_&lt;Нога&gt;</c>.</summary>
    Pivot,

    /// <summary>Смена лица при ходе: <c>&lt;Походка&gt;_Reface[_Start]_&lt;Направление&gt;_&lt;L|R&gt;_&lt;угол&gt;[_хвост]</c>.</summary>
    Reface,

    /// <summary>Поворот на месте: <c>Stand_Turn_&lt;угол&gt;_&lt;L|R&gt;</c>.</summary>
    TurnInPlace,

    /// <summary>
    /// Смена направления на ходу — фигуры <c>Box</c>, <c>Diamond</c>, <c>Hourglass</c>, <c>Prism</c>, <c>Shuffle</c>, <c>Spin</c>:
    /// <c>&lt;Походка&gt;_&lt;Фигура&gt;_&lt;Откуда&gt;[_метка]_[to_]&lt;Куда|метка&gt;_&lt;Нога&gt;</c>.
    /// </summary>
    DirectionChange,

    /// <summary>Дуга: <c>&lt;Походка&gt;_Arc_[&lt;Направление&gt;_]&lt;Размер&gt;_&lt;L|R&gt;</c>.</summary>
    Arc,

    /// <summary>Стрейф по кругу: <c>&lt;Походка&gt;_Circle_Strafe_&lt;L|R&gt;</c>.</summary>
    CircleStrafe,

    /// <summary>Поворот на ходу: <c>&lt;Походка&gt;_Turn_&lt;L|R&gt;_&lt;угол&gt;_&lt;Нога&gt;</c>.</summary>
    MoveTurn,

    /// <summary>Смена походки: <c>Transition_&lt;Из&gt;_to_&lt;В&gt;_&lt;Нога&gt;</c> (походка клипа — целевая).</summary>
    GaitTransition,
}

/// <summary>Походка клипа GASP.</summary>
public enum GaspGait : byte
{
    Stand,
    Walk,
    Run,
}

/// <summary>
/// Направление хода относительно лица в имени GASP: F — вперёд, FL/FR — вперёд-влево/вправо, LL/LR — влево (таз к левой
/// или правой стороне), RL/RR — вправо, B — назад, BL/BR — назад-влево/вправо.
/// </summary>
public enum GaspDirection : byte
{
    None,
    F,
    FL,
    LL,
    LR,
    BL,
    B,
    BR,
    RL,
    RR,
    FR,
}

/// <summary>Сторона в имени: ведущая нога (<c>Lfoot</c>/<c>Rfoot</c>) или сторона поворота, дуги, стрейфа (<c>L</c>/<c>R</c>).</summary>
public enum GaspSide : byte
{
    None,
    Left,
    Right,
}

/// <summary>
/// Разбор короткого имени клипа локомоции GASP: <c>&lt;Стиль&gt;_&lt;Походка&gt;_&lt;Вид&gt;_…</c> (<c>M_Neutral_Walk_Start_F_Lfoot</c>);
/// стиль пропускается. Обязательные части строгие: опечатка — ошибка, а не «неизвестное»; лишнее — в <see cref="Tail"/>.
/// Имя — только первичная классификация: продолжение и прошлое клипа политика графа берёт из измеренного движения.
/// </summary>
/// <param name="FromGait">Походка в начале клипа: у смены походки — исходная, у остальных — та же <see cref="Gait"/>.</param>
/// <param name="In">Направление хода на входе (<see cref="GaspDirection.None"/> — имя не говорит или стоит).</param>
/// <param name="Degrees">Угол поворота или отклонения, градусы (0 — нет в имени).</param>
/// <param name="Figure">Фигура смены направления (<c>Box</c>…<c>Spin</c>) или размер дуги (<c>Small</c>, <c>Tight</c>, <c>Wide</c>).</param>
public readonly record struct GaspClipName(string Style, GaspKind Kind, GaspGait Gait, GaspGait FromGait, GaspDirection In,
    GaspDirection Out, GaspSide Foot, GaspSide Side, int Degrees, string Figure, string Tail)
{
    /// <summary>Фигуры смены направления на ходу.</summary>
    public static readonly IReadOnlyList<string> Figures = ["Box", "Diamond", "Hourglass", "Prism", "Shuffle", "Spin"];

    /// <summary>Направление хода, рад, относительно лица: 0 — вперёд, + — влево (от +Z к +X), в (−π, π]; <see cref="GaspDirection.None"/> — 0.</summary>
    public static float Angle(GaspDirection d) => d switch
    {
        GaspDirection.FL => MathF.PI / 4,
        GaspDirection.LL or GaspDirection.LR => MathF.PI / 2,
        GaspDirection.BL => 3 * MathF.PI / 4,
        GaspDirection.B => MathF.PI,
        GaspDirection.BR => -3 * MathF.PI / 4,
        GaspDirection.RL or GaspDirection.RR => -MathF.PI / 2,
        GaspDirection.FR => -MathF.PI / 4,
        _ => 0,
    };

    /// <summary>Разбор имени; не разбирается — <see cref="InvalidDataException"/> с причиной.</summary>
    public static GaspClipName Parse(string shortName) =>
        TryParse(shortName, out var name, out string error) ? name : throw new InvalidDataException($"имя клипа GASP «{shortName}» не разбирается: {error}");

    /// <summary>Разбор имени; false — не разбирается, <paramref name="error"/> — чем.</summary>
    public static bool TryParse(string shortName, out GaspClipName name, out string error)
    {
        ArgumentNullException.ThrowIfNull(shortName);
        name = default;
        string[] t = shortName.Split('_');
        int head = 0;
        while (head < t.Length && !IsGait(t[head], out _) && t[head] != "Transition") head++;
        if (head == t.Length)
        {
            error = "нет походки (Stand, Walk, Run) или Transition";
            return false;
        }
        var r = new Reader(t, head + 1);
        string style = string.Join('_', t, 0, head);
        if (t[head] == "Transition")
        {
            error = Transition(ref r, style, out name);
            return error.Length == 0;
        }
        IsGait(t[head], out var gait);
        if (!r.Next(out string kind))
        {
            error = $"после походки {gait} нет вида";
            return false;
        }
        var n = new GaspClipName(style, default, gait, gait, GaspDirection.None, GaspDirection.None, GaspSide.None, GaspSide.None, 0, "", "");
        error = kind switch
        {
            "Idle" => Idle(ref r, ref n),
            "Loop" => Loop(ref r, ref n),
            "Start" => Start(ref r, ref n),
            "Stop" => Stop(ref r, ref n),
            "Pivot" => Pivot(ref r, ref n),
            "Reface" => Reface(ref r, ref n),
            "Turn" => gait == GaspGait.Stand ? TurnInPlace(ref r, ref n) : MoveTurn(ref r, ref n),
            "Arc" => Arc(ref r, ref n),
            "Circle" => Circle(ref r, ref n),
            _ when Figures.Contains(kind) => DirectionChange(ref r, ref n, kind),
            _ => $"неизвестный вид «{kind}»",
        };
        if (error.Length > 0) return false;
        if (n.Kind is not (GaspKind.Idle or GaspKind.TurnInPlace) && gait == GaspGait.Stand)
        {
            error = $"вид {n.Kind} у походки Stand";
            return false;
        }
        if (n.Kind is GaspKind.Idle or GaspKind.TurnInPlace && gait != GaspGait.Stand)
        {
            error = $"вид {n.Kind} у походки {gait} (нужна Stand)";
            return false;
        }
        name = n with { Tail = JoinTail(n.Tail, r.Rest()) };
        return true;
    }

    // ── виды ─────────────────────────────────────────────────────────────────────────────

    private static string Idle(ref Reader r, ref GaspClipName n)
    {
        n = n with { Kind = GaspKind.Idle };
        r.Accept("Loop");
        return "";
    }

    private static string Loop(ref Reader r, ref GaspClipName n)
    {
        if (!r.Direction(out var d)) return "у цикла нет направления";
        n = n with { Kind = GaspKind.Loop, In = d, Out = d };
        if (r.Side(out var side))
        {
            if (!r.Degrees(out int degrees)) return "у отклонения цикла нет угла";
            n = n with { Side = side, Degrees = degrees };
        }
        return "";
    }

    private static string Start(ref Reader r, ref GaspClipName n)
    {
        if (!r.Direction(out var d)) return "у старта нет направления";
        if (!r.Foot(out var foot)) return "у старта нет ведущей ноги (Lfoot/Rfoot)";
        n = n with { Kind = GaspKind.Start, Out = d, Foot = foot };
        return "";
    }

    private static string Stop(ref Reader r, ref GaspClipName n)
    {
        if (!r.Direction(out var d)) return "у остановки нет направления";
        if (!r.Foot(out var foot)) return "у остановки нет ведущей ноги (Lfoot/Rfoot)";
        n = n with { Kind = GaspKind.Stop, In = d, Foot = foot };
        return "";
    }

    private static string Pivot(ref Reader r, ref GaspClipName n)
    {
        if (!r.Direction(out var from) || !r.Direction(out var to)) return "у разворота нет двух направлений";
        if (!r.Foot(out var foot)) return "у разворота нет ведущей ноги (Lfoot/Rfoot)";
        n = n with { Kind = GaspKind.Pivot, In = from, Out = to, Foot = foot };
        return "";
    }

    private static string Reface(ref Reader r, ref GaspClipName n)
    {
        r.Accept("Start");
        if (!r.Direction(out var d)) return "у смены лица нет направления";
        if (!r.Side(out var side)) return "у смены лица нет стороны (L/R)";
        if (!r.Degrees(out int degrees)) return "у смены лица нет угла";
        n = n with { Kind = GaspKind.Reface, Out = d, Side = side, Degrees = degrees };
        return "";
    }

    private static string TurnInPlace(ref Reader r, ref GaspClipName n)
    {
        if (!r.Degrees(out int degrees)) return "у поворота на месте нет угла";
        if (!r.Side(out var side)) return "у поворота на месте нет стороны (L/R)";
        n = n with { Kind = GaspKind.TurnInPlace, Side = side, Degrees = degrees };
        return "";
    }

    private static string MoveTurn(ref Reader r, ref GaspClipName n)
    {
        if (!r.Side(out var side)) return "у поворота на ходу нет стороны (L/R)";
        if (!r.Degrees(out int degrees)) return "у поворота на ходу нет угла";
        if (!r.Foot(out var foot)) return "у поворота на ходу нет ведущей ноги (Lfoot/Rfoot)";
        n = n with { Kind = GaspKind.MoveTurn, Side = side, Degrees = degrees, Foot = foot };
        return "";
    }

    private static string Arc(ref Reader r, ref GaspClipName n)
    {
        r.Direction(out var d);
        if (!r.Next(out string size) || IsSide(size, out _) || size.Length == 0) return "у дуги нет размера";
        if (!r.Side(out var side)) return "у дуги нет стороны (L/R)";
        n = n with { Kind = GaspKind.Arc, In = d, Out = d, Figure = size, Side = side };
        return "";
    }

    private static string Circle(ref Reader r, ref GaspClipName n)
    {
        if (!r.Accept("Strafe")) return "Circle без Strafe";
        if (!r.Side(out var side)) return "у стрейфа по кругу нет стороны (L/R)";
        n = n with { Kind = GaspKind.CircleStrafe, Side = side };
        return "";
    }

    /// <summary>
    /// Смена направления: <c>&lt;Фигура&gt;_&lt;Откуда&gt;[_метки]_[to_]&lt;Куда|метки&gt;_&lt;Нога&gt;</c>. Без <c>to</c> — ровно два направления;
    /// с <c>to</c> — слева направление и метки, справа направление или только метки (тогда «куда» — то же направление).
    /// </summary>
    private static string DirectionChange(ref Reader r, ref GaspClipName n, string figure)
    {
        int foot = r.LastFoot();
        if (foot < 0) return $"у смены направления {figure} нет ведущей ноги (Lfoot/Rfoot)";
        var body = r.Take(foot - r.Position);
        r.Foot(out var leg);
        int to = Array.IndexOf(body, "to");
        string[] left = to < 0 ? body[..Math.Min(1, body.Length)] : body[..to];
        string[] right = to < 0 ? body[Math.Min(1, body.Length)..] : body[(to + 1)..];
        if (to < 0 && body.Length != 2) return $"у смены направления {figure} без «to» нужно два направления, а слов {body.Length}";
        if (left.Length == 0 || !IsDirection(left[0], out var from)) return $"у смены направления {figure} нет направления «откуда»";
        if (right.Length == 0) return $"у смены направления {figure} нет «куда»";
        bool rightDirection = IsDirection(right[0], out var into);
        if (to < 0 && !rightDirection) return $"у смены направления {figure} без «to» нет направления «куда» («{right[0]}»)";
        if (!rightDirection) into = from;
        var marks = left[1..];
        var intoMarks = rightDirection ? right[1..] : right;
        foreach (string m in marks.Concat(intoMarks))
            if (IsDirection(m, out _) || IsFoot(m, out _)) return $"у смены направления {figure} лишнее направление или нога «{m}»";
        string tail = marks.Length + intoMarks.Length == 0 ? "" : $"{string.Join('_', marks)}_to_{string.Join('_', intoMarks)}".Trim('_');
        n = n with { Kind = GaspKind.DirectionChange, In = from, Out = into, Foot = leg, Figure = figure, Tail = tail };
        return "";
    }

    private static string Transition(ref Reader r, string style, out GaspClipName name)
    {
        name = default;
        if (!r.Next(out string a) || !IsGait(a, out var from)) return "у смены походки нет исходной походки";
        if (!r.Accept("to")) return "у смены походки нет «to»";
        if (!r.Next(out string b) || !IsGait(b, out var to)) return "у смены походки нет целевой походки";
        if (from == to) return $"смена походки {from} → {to}";
        if (!r.Foot(out var foot)) return "у смены походки нет ведущей ноги (Lfoot/Rfoot)";
        name = new GaspClipName(style, GaspKind.GaitTransition, to, from, GaspDirection.None, GaspDirection.None, foot, GaspSide.None, 0, "",
            string.Join('_', r.Rest()));
        return "";
    }

    // ── слова ─────────────────────────────────────────────────────────────────────────────

    private static string JoinTail(string tail, string[] rest) =>
        rest.Length == 0 ? tail : tail.Length == 0 ? string.Join('_', rest) : tail + "_" + string.Join('_', rest);

    private static bool IsGait(string s, out GaspGait gait)
    {
        (bool ok, gait) = s switch
        {
            "Stand" => (true, GaspGait.Stand),
            "Walk" => (true, GaspGait.Walk),
            "Run" => (true, GaspGait.Run),
            _ => (false, default),
        };
        return ok;
    }

    private static bool IsDirection(string s, out GaspDirection d)
    {
        d = s switch
        {
            "F" => GaspDirection.F,
            "FL" => GaspDirection.FL,
            "LL" => GaspDirection.LL,
            "LR" => GaspDirection.LR,
            "BL" => GaspDirection.BL,
            "B" => GaspDirection.B,
            "BR" => GaspDirection.BR,
            "RL" => GaspDirection.RL,
            "RR" => GaspDirection.RR,
            "FR" => GaspDirection.FR,
            _ => GaspDirection.None,
        };
        return d != GaspDirection.None;
    }

    private static bool IsFoot(string s, out GaspSide foot)
    {
        foot = s switch
        {
            "Lfoot" => GaspSide.Left,
            "Rfoot" => GaspSide.Right,
            _ => GaspSide.None,
        };
        return foot != GaspSide.None;
    }

    private static bool IsSide(string s, out GaspSide side)
    {
        side = s switch
        {
            "L" => GaspSide.Left,
            "R" => GaspSide.Right,
            _ => GaspSide.None,
        };
        return side != GaspSide.None;
    }

    /// <summary>Чтение слов имени по порядку: «взять, если подходит» — иначе позиция не двигается.</summary>
    private struct Reader(string[] tokens, int position)
    {
        public int Position { readonly get; private set; } = position;

        public bool Next(out string token)
        {
            token = Position < tokens.Length ? tokens[Position] : "";
            if (Position >= tokens.Length) return false;
            Position++;
            return true;
        }

        public bool Accept(string token)
        {
            if (Position >= tokens.Length || tokens[Position] != token) return false;
            Position++;
            return true;
        }

        public bool Direction(out GaspDirection d) => Try(IsDirection, out d);

        public bool Foot(out GaspSide foot) => Try(IsFoot, out foot);

        public bool Side(out GaspSide side) => Try(IsSide, out side);

        /// <summary>Угол — 1–3 цифры (<c>045</c>, <c>20</c>, <c>180</c>).</summary>
        public bool Degrees(out int degrees)
        {
            degrees = 0;
            if (Position >= tokens.Length) return false;
            string s = tokens[Position];
            if (s.Length is 0 or > 3 || !s.All(char.IsAsciiDigit)) return false;
            degrees = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            Position++;
            return true;
        }

        /// <summary>Номер последнего слова-ноги от позиции; нет — −1.</summary>
        public readonly int LastFoot()
        {
            for (int i = tokens.Length - 1; i >= Position; i--)
                if (IsFoot(tokens[i], out _)) return i;
            return -1;
        }

        /// <summary>Следующие <paramref name="count"/> слов.</summary>
        public string[] Take(int count)
        {
            var taken = tokens[Position..(Position + count)];
            Position += count;
            return taken;
        }

        /// <summary>Оставшиеся слова (хвост).</summary>
        public string[] Rest()
        {
            var rest = tokens[Position..];
            Position = tokens.Length;
            return rest;
        }

        private delegate bool Match<T>(string s, out T value);

        private bool Try<T>(Match<T> match, out T value)
        {
            value = default!;
            if (Position >= tokens.Length || !match(tokens[Position], out value)) return false;
            Position++;
            return true;
        }
    }
}
