using System.Numerics;

namespace Sword.Engine.Physics;

// Открытые типы контракта физики: суставы, контакты и шаг, толчки живых и трасса.

/// <summary>Вид сустава.</summary>
public enum JointKind : byte
{
    /// <summary>
    /// Шарнир (колено, локоть): сгиб вокруг <see cref="JointDesc.HingeAxis"/> в <c>[BendMin, BendMax]</c>; ось шарнира ребёнка
    /// держится у оси родителя с люфтом <see cref="JointDesc.AxisSlack"/>.
    /// </summary>
    Hinge = 1,

    /// <summary>Наклон в эллипсе (X — <c>[SwingXMin, SwingXMax]</c>, Z — <c>±SwingZ</c>) и скручивание вокруг Y в <c>±Twist</c> относительно позы привязки.</summary>
    SwingTwist = 2,
}

/// <summary>
/// Описание сустава: точка задана на ребёнке (<see cref="ChildAnchor"/>), на родителе вычисляется при привязке
/// (<see cref="IPhysicsWorld.RebindJoints"/>). Оси — в системе ребёнка: Y — ось капсулы (скручивание), X и Z — наклон; углы —
/// рад. Точка — жёстко, предел — с податливостью <see cref="LimitCompliance"/>. Создаётся фабриками; поля другого вида — 0.
/// </summary>
public readonly record struct JointDesc
{
    public int Parent { get; init; }
    public int Child { get; init; }

    /// <summary>Точка сустава на ребёнке, система ребёнка, м (обычно нижний конец оси: <c>(0, −HalfLength, 0)</c>).</summary>
    public Vector3 ChildAnchor { get; init; }

    public JointKind Kind { get; init; }

    /// <summary>Шарнир: пределы угла сгиба, рад (<c>BendMin ≤ BendMax</c>); сгиб — угол от оси родителя к оси ребёнка вокруг оси шарнира, умноженный на <see cref="BendSign"/>.</summary>
    public float BendMin { get; init; }

    public float BendMax { get; init; }

    /// <summary>Шарнир: люфт оси — насколько ось ребёнка может уйти от оси шарнира родителя без поправки, рад (≥ 0).</summary>
    public float AxisSlack { get; init; }

    /// <summary>Шарнир: ось шарнира в системе ребёнка, единичная (фабрика — <see cref="Vector3.UnitX"/>, ось колена; другая — через <c>with</c>).</summary>
    public Vector3 HingeAxis { get; init; }

    /// <summary>Шарнир: знак сгиба (+1 или −1) — анатомический, из описания, а не по позе смерти (иначе прямое колено сгибалось бы назад).</summary>
    public sbyte BendSign { get; init; }

    /// <summary>Наклон-скручивание: предел наклона по X, рад (<c>SwingXMin ≤ 0 ≤ SwingXMax</c>).</summary>
    public float SwingXMin { get; init; }

    public float SwingXMax { get; init; }

    /// <summary>Наклон-скручивание: предел наклона по Z (±), рад (&gt; 0).</summary>
    public float SwingZ { get; init; }

    /// <summary>Наклон-скручивание: предел скручивания вокруг Y (±), рад (≥ 0).</summary>
    public float Twist { get; init; }

    /// <summary>Податливость предела, рад/(Н·м) (≥ 0); null — <see cref="PhysicsSettings.JointLimitCompliance"/>.</summary>
    public float? LimitCompliance { get; init; }

    /// <summary>Шарнир (по умолчанию жёсткий: сгиб и ось без податливости).</summary>
    public static JointDesc Hinge(int parent, int child, Vector3 childAnchor, float bendMin, float bendMax, float axisSlack, sbyte bendSign,
        float? limitCompliance = 0) => new()
    {
        Parent = parent, Child = child, ChildAnchor = childAnchor, Kind = JointKind.Hinge,
        BendMin = bendMin, BendMax = bendMax, AxisSlack = axisSlack, BendSign = bendSign, HingeAxis = Vector3.UnitX,
        LimitCompliance = limitCompliance,
    };

    /// <summary>Наклон-скручивание (по умолчанию податливость предела — из настроек).</summary>
    public static JointDesc SwingTwist(int parent, int child, Vector3 childAnchor, float swingXMin, float swingXMax, float swingZ, float twist,
        float? limitCompliance = null) => new()
    {
        Parent = parent, Child = child, ChildAnchor = childAnchor, Kind = JointKind.SwingTwist,
        SwingXMin = swingXMin, SwingXMax = swingXMax, SwingZ = swingZ, Twist = twist, LimitCompliance = limitCompliance,
    };
}

/// <summary>Срабатывание предела: номер сустава, подшаг последнего шага, ось поправки (единичная, мир).</summary>
public readonly record struct JointLimitHit(int Joint, int Substep, Vector3 Axis);

/// <summary>
/// Замер сустава в текущей позе: ошибка точки, м; сгиб со знаком, наклон и скручивание, рад; выход за предел — наибольший
/// по всем пределам с люфтом оси, рад (0 — в пределах).
/// </summary>
public readonly record struct JointMeasure(float PointError, float Bend, float Swing, float Twist, float Excess);

/// <summary>
/// Привязка сустава: точка на родителе (система родителя, м) и поза покоя <c>conj(parent) · child</c>. От неё зависит шаг,
/// поэтому она в хеше участника <c>physics</c>.
/// </summary>
public readonly record struct JointBinding(Vector3 ParentAnchor, Quaternion Rest);

/// <summary>Касание сферы гранью земли: номер грани (у реализации свой, постоянный), нормаль из земли, глубина, м (&gt; 0).</summary>
public readonly record struct GroundTouch(int Face, Vector3 Normal, float Depth);

/// <summary>
/// Земля для контактов (реализация — игра). Физика решает каждую грань отдельно со своей старой глубиной, поэтому глубина
/// грани — непрерывная функция центра сферы, меняющаяся не быстрее его смещения (константа Липшица около 1): шаг пропускает
/// старую глубину, если <c>глубина + 2·смещение ≤ PopSpeed·h</c>. Глубина <see cref="Touches"/> — та же, что
/// <see cref="Depth"/>. Без выделения памяти и без состояния, меняющегося от вызовов (детерминизм).
/// </summary>
public interface IGround
{
    const int MaxTouches = 6;

    /// <summary>Грани, в которые вошла сфера, — не больше <see cref="MaxTouches"/>, по возрастанию номера. Возвращает число.</summary>
    int Touches(Vector3 center, float radius, Span<GroundTouch> touches);

    /// <summary>Глубина сферы в грань <paramref name="face"/>, м (≤ 0 — не касается), и нормаль грани в этой точке.</summary>
    float Depth(Vector3 center, float radius, int face, out Vector3 normal);
}

/// <summary>Колонна: бесконечный вертикальный цилиндр с осью в (<see cref="X"/>, <see cref="Z"/>), радиус, м.</summary>
public readonly record struct Pillar(float X, float Z, float Radius);

/// <summary>Границы сочленения: сфера, в которой все его капсулы (центр, радиус, м) — для отсева пар.</summary>
public readonly record struct ArticulationBounds(Vector3 Center, float Radius);

/// <summary>
/// Проход пары сочленений: толкающий в ударе и сочленение на физике. <see cref="Potential"/>, Н·с, — у закрытого потенциал
/// тика, у открытого — бюджет (наибольший потенциал с открытия); <see cref="Remaining"/> — остаток; <see cref="EmptyTicks"/> —
/// тиков подряд без контакта. Скрытого состояния прохода нет.
/// </summary>
public readonly record struct PassState(int Strike, int Number, bool Inside, float Potential, float Remaining, int EmptyTicks);

/// <summary>Источник поправки для трассы физики; толчки кинематических — <c>PushBody + (int)source</c>.</summary>
public enum PhysicsSource : byte
{
    Joints = 0,
    Ground = 1,
    Wall = 2,
    Pair = 3,
    PushBody = 4,
    PushPass = 5,
    PushNoReaction = 6,
    Self = 7,
}

/// <summary>Пара разных тел одного сочленения для самоколлизии (номера тел в сочленении).</summary>
public readonly record struct BodyPair(int A, int B);

/// <summary>
/// Фаза подшага для трассы: рост энергии по фазам показывает, где энергия появляется. Порядок — порядок подшага. После
/// позиционных фаз энергия — по скоростям <c>сдвиг / h</c>, после скоростных — по настоящим.
/// </summary>
public enum PhysicsPhase : byte
{
    Integrate = 0,
    Joints = 1,
    World = 2,
    Pairs = 3,
    Pressed = 4,
    Velocity = 5,
    ContactVelocity = 6,
    Relax = 7,
}

/// <summary>Почему проснулось спящее сочленение (трасса — счётчики по причинам).</summary>
public enum WakeReason : byte
{
    /// <summary>Толчок живого: кинематическое тело у спящего движется быстрее <see cref="PhysicsSettings.SleepBodySpeed"/> или вошло глубже <see cref="PhysicsWorld.SleepSlop"/>.</summary>
    Push = 0,

    /// <summary>Удар: <see cref="IPhysicsWorld.PassHit"/> по спящей цели, <see cref="IPhysicsWorld.ApplyHit"/> в её тело.</summary>
    Hit = 1,

    /// <summary>
    /// Касание неспящего тела, быстрее порога или глубже допуска; падающий труп (центр масс вниз быстрее
    /// <see cref="PhysicsSettings.SleepSpeed"/>) — любое касание спящего на нём.
    /// </summary>
    Touch = 2,

    /// <summary>Цепочка на одно касание: проснувшееся в тике будит касающихся спящих; запись, уводящая тело между шагами, — спящих на нём.</summary>
    Chain = 3,

    /// <summary>Запись состояния тела, режима, профиля, суставов или <see cref="IPhysicsWorld.Wake"/>.</summary>
    Command = 4,

    /// <summary>Смена настроек физики, профиля динамики, земли, стены или колонн.</summary>
    Settings = 5,
}

/// <summary>
/// Трасса физики — диагностический режим владельца: источники поправок, скорости, бюджеты, рост энергии, события быстрее
/// порога. Включается между шагами (<see cref="IPhysicsWorld.EnableTrace"/>). В хеш не входит, симуляцию не меняет;
/// выключенная — одна проверка и 0 Б, включённая — 0 Б в установившемся режиме. Накопители <see cref="Stats"/>:
/// <c>Last</c> — последний тик, <c>Window</c> — окно сценария, <c>Run</c> — с включения.
/// </summary>
public sealed partial class PhysicsTrace
{
}
