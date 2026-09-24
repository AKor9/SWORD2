using System.Numerics;

namespace Sword.Engine.Physics;

// Открытые типы контракта этапа 7 (зафиксированы на 7б) для точек 7в (суставы), 7г (контакты и шаг) и 7д (толчки живых и
// трасса); реализованы и сведены после слияния 7в–7д. Файл, как и IPhysicsWorld.cs, меняется отдельным маленьким коммитом
// «контракт: …» — его зовут параллельные точки игры и клиента (раздел 3.1 плана этапа, README «Этап 7: контракт»).

/// <summary>Вид сустава (7в).</summary>
public enum JointKind : byte
{
    /// <summary>
    /// Шарнир (колено, локоть): сгиб вокруг оси шарнира ребёнка (<see cref="JointDesc.HingeAxis"/>, по умолчанию X) в
    /// пределах <c>[BendMin, BendMax]</c> со знаком сгиба; ось шарнира ребёнка держится у оси шарнира родителя (перенесённой
    /// позой привязки) с люфтом <see cref="JointDesc.AxisSlack"/>.
    /// </summary>
    Hinge = 1,

    /// <summary>
    /// Наклон и скручивание (позвоночник, шея, плечо, бедро, голеностоп, запястье): наклон относительно позы привязки — внутри
    /// эллипса (по X — <c>[SwingXMin, SwingXMax]</c>, по Z — <c>±SwingZ</c>), скручивание вокруг оси Y — в <c>±Twist</c>.
    /// </summary>
    SwingTwist = 2,
}

/// <summary>
/// Описание сустава сочленения (7в; идеи — <c>Fighter.Joints</c> SWORD). Родитель и ребёнок — номера тел сочленения (0 …
/// <see cref="IPhysicsWorld.BodyCount"/> − 1); точка сустава задана на ребёнке (<see cref="ChildAnchor"/>, в системе ребёнка),
/// на родителе она вычисляется при привязке (<see cref="IPhysicsWorld.RebindJoints"/>) по текущей позе (читается —
/// <see cref="IPhysicsWorld.Binding"/>). Оси — в системе ребёнка: Y — ось капсулы (скручивание), X и Z — оси наклона, ось
/// шарнира — <see cref="HingeAxis"/> (фабрика — X); углы — рад. Сустав держит точку жёстко; предел — с податливостью
/// <see cref="LimitCompliance"/> (null — <see cref="PhysicsSettings.JointLimitCompliance"/>, меняется ручками на ходу).
/// Создаётся фабриками <see cref="Hinge"/> и <see cref="SwingTwist"/>; поля другого вида — 0.
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

    /// <summary>
    /// Шарнир: ось шарнира в системе ребёнка, единичная (фабрика — <see cref="Vector3.UnitX"/>: у капсул бойца X — «вправо»,
    /// ось колена). Другая ось — через <c>with</c>; игра может задать её и по позе смерти (<see cref="IPhysicsWorld.SetJoints"/>
    /// привязывает заново), если X плохо ложится на локоть.
    /// </summary>
    public Vector3 HingeAxis { get; init; }

    /// <summary>
    /// Шарнир: знак сгиба (+1 или −1) — анатомический, из описания (у SWORD — знак сгиба в позе смерти, из-за чего почти
    /// прямое колено сгибалось «назад»).
    /// </summary>
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

    /// <summary>Шарнир (по умолчанию жёсткий: у SWORD сгиб и ось шарнира — без податливости).</summary>
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

/// <summary>
/// Срабатывание предела сустава (7в): номер сустава в сочленении, подшаг последнего шага (0 …), ось поправки (единичная,
/// мир). Журнал (<see cref="IPhysicsWorld.JointLimitHits"/>) — за последний шаг, по порядку.
/// </summary>
public readonly record struct JointLimitHit(int Joint, int Substep, Vector3 Axis);

/// <summary>
/// Замер сустава в текущей позе (7в): ошибка точки сустава (расстояние между точками на ребёнке и родителе, м); углы — сгиб
/// шарнира со знаком, наклон (длина вектора наклона) и скручивание, рад; выход за предел — наибольший по всем пределам
/// сустава, включая люфт оси, рад (0 — в пределах). Выключенный сустав замеряется так же.
/// </summary>
public readonly record struct JointMeasure(float PointError, float Bend, float Swing, float Twist, float Excess);

/// <summary>
/// Привязка сустава (7в) — состояние, которое пишут <see cref="IPhysicsWorld.SetJoints"/> и
/// <see cref="IPhysicsWorld.RebindJoints"/>: точка сустава на родителе (система родителя, м) и поза покоя — поворот ребёнка
/// относительно родителя в позе привязки (<c>conj(parent) · child</c>). От неё зависит дальнейший шаг, поэтому она видна:
/// хеш участника <c>physics</c>, описание, сравнение двух реализаций, оверлей суставов.
/// </summary>
public readonly record struct JointBinding(Vector3 ParentAnchor, Quaternion Rest);

/// <summary>Касание сферы гранью земли (7г, 7е): номер грани (у реализации свой, постоянный), нормаль из земли, глубина, м (&gt; 0).</summary>
public readonly record struct GroundTouch(int Face, Vector3 Normal, float Depth);

/// <summary>
/// Земля для контактов физики (7г; реализация — игра, 7е: аналитический рельеф и ярусы ящиками). Физика проверяет сферы
/// (торцы капсул) и решает каждую грань отдельно: у каждой своя старая глубина (та же грань в прошлой позе). Грань — часть
/// земли с постоянным номером и своей непрерывной функцией глубины от центра сферы: гладкий рельеф — одна грань (касательная
/// плоскость под сферой), ярус пирамиды — одна грань-ящик (точное расстояние до ящика: у верха нормаль вверх, у подступенка —
/// горизонтальная, за кромкой — от ребра). Непрерывность нужна, чтобы глубина той же грани в прошлой позе
/// (<see cref="Depth"/>) была осмысленной. 7з-2: глубина грани меняется не быстрее смещения центра сферы (липшицева с константой около 1 —
/// у знакового расстояния ровно 1, у касательной плоскости рельефа — 1 плюс кривизна × радиус): шаг не считает старую глубину, если она
/// заведомо мала (<c>глубина + 2·смещение ≤ PopSpeed·h</c>). Глубина <see cref="Touches"/> — та же, что у <see cref="Depth"/> в той же
/// точке (шаг берёт её у первой грани). Вызовы — из шага, без выделения памяти; реализация — без состояния, меняющегося от вызовов
/// (детерминизм).
/// </summary>
public interface IGround
{
    /// <summary>Наибольшее число касаний одной сферы.</summary>
    const int MaxTouches = 6;

    /// <summary>
    /// Все грани, в которые вошла сфера (<paramref name="center"/>, <paramref name="radius"/>): не больше
    /// <see cref="MaxTouches"/> и не больше длины <paramref name="touches"/>, по возрастанию номера грани. Возвращает число.
    /// </summary>
    int Touches(Vector3 center, float radius, Span<GroundTouch> touches);

    /// <summary>Глубина сферы в грань <paramref name="face"/>, м (≤ 0 — не касается), и нормаль грани в этой точке.</summary>
    float Depth(Vector3 center, float radius, int face, out Vector3 normal);
}

/// <summary>Колонна (7г): бесконечный вертикальный цилиндр с осью в (<see cref="X"/>, <see cref="Z"/>), радиус, м.</summary>
public readonly record struct Pillar(float X, float Z, float Radius);

/// <summary>Границы сочленения (7г): сфера, в которой все его капсулы (центр, радиус, м) — для отсева пар.</summary>
public readonly record struct ArticulationBounds(Vector3 Center, float Radius);

/// <summary>
/// Проход пары сочленений (7д): толкающий в ударе (<see cref="Strike"/> — номер удара от игры) и сочленение на физике.
/// <see cref="Number"/> — сколько раз проход открывался; <see cref="Inside"/> — открыт; <see cref="Potential"/>, Н·с — у
/// закрытого прохода потенциал этого тика (<see cref="IPhysicsWorld.PreparePass"/>; конец тика его снимает), у открытого —
/// бюджет прохода: наибольший потенциал с открытия; <see cref="Remaining"/> — остаток бюджета открытого прохода, Н·с
/// (потрачено за проход — <c>Potential − Remaining</c>, ≥ 0); <see cref="EmptyTicks"/> — тиков подряд без контакта и
/// попадания. Скрытого состояния прохода нет: всё, от чего зависит следующий тик, — здесь.
/// </summary>
public readonly record struct PassState(int Strike, int Number, bool Inside, float Potential, float Remaining, int EmptyTicks);

/// <summary>
/// Источник поправки для трассы физики (7д): суставы, земля, стена и колонны, тело ↔ тело на физике, толчки кинематических
/// тел по <see cref="KinematicSource"/> (<c>PushBody + (int)source</c>).
/// </summary>
public enum PhysicsSource : byte
{
    Joints = 0,
    Ground = 1,
    Wall = 2,
    Pair = 3,
    PushBody = 4,
    PushPass = 5,
    PushNoReaction = 6,
}

/// <summary>
/// Фаза подшага (7з-2) — для трассы физики: рост энергии сочленения по фазам показывает, где энергия появляется (суставы сводят разрыв,
/// выталкивание, скоростной проход…). Порядок — порядок подшага (<see cref="IPhysicsWorld.Step"/>): интеграция, суставы, мир (земля,
/// стена, колонны), пары тел (и толчки живых, и трение покоя пар), мир после пар (тела, прижатые парами к земле), скорости по профилю,
/// скоростной проход контактов, расслабление суставов. После позиционных фаз (до скоростей) энергия — по скоростям, которые дал бы сдвиг
/// за подшаг (<c>сдвиг / h</c>, как <see cref="Dynamics.UpdateVelocity"/> без демпфирования), после скоростных — по настоящим.
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

/// <summary>
/// Трасса физики — диагностический режим владельца (7д; как журнал причинности): источники поправок, сдвиги и углы по этапам
/// подшага, скорости и гистограммы, бюджеты, события быстрее порога. Включает и выключает владелец между шагами
/// (<see cref="IPhysicsWorld.EnableTrace"/>, <see cref="IPhysicsWorld.DisableTrace"/>). В хеш не входит и симуляцию не меняет;
/// выключенная — одна проверка и 0 Б, включённая — 0 Б в установившемся режиме (строки — только по запросу). Члены —
/// <c>PhysicsTrace.Data.cs</c>, <c>PhysicsTrace.Stats.cs</c>:
/// <list type="bullet">
/// <item>накопители <see cref="Stats"/>: <c>Last</c> — последний тик (шаг и то, что было после него до следующего шага: удары,
/// списание прохода, отдача; инварианты игры читают его после тика), <c>Window</c> — окно сценария (<c>ClearWindow</c>),
/// <c>Run</c> — с включения (<c>Clear</c> — всё); в каждом — по источникам <see cref="PhysicsSource"/> поправки, контакты и
/// глубины, псевдоимпульс, точный импульс толчков; скорости; доли бюджетов пары и прохода, выталкивание толчка против
/// <see cref="PhysicsSettings.PopSpeed"/>; доля Δv удара; трение покоя; рост энергии сочленения на физике за подшаг
/// (кинетическая + <c>m·g·y</c>; без толчков и толкаемого живыми отдельно — инвариант <c>physics.energy</c>); события и самые
/// быстрые;</item>
/// <item><c>LastContacts</c> — контакты последнего подшага последнего шага (источник, тела, точка, нормаль, глубина, решённая;
/// оверлей <c>contacts</c>), лишние сверх вместимости — <c>LostContacts</c>; кольцо последних событий — <c>RecentEvent</c>;</item>
/// <item><c>Stats.Summary(prefix)</c> — сводка строками для агента.</item>
/// </list>
/// </summary>
public sealed partial class PhysicsTrace
{
}
