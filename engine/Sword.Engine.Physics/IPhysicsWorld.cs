using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Физика одной симуляции: тела-капсулы и сочленения за хэндлами (индекс + поколение). Интерфейс — чтобы эталон и
/// оптимизированная реализация жили в одном процессе; при одних вызовах обе выдают одни хэндлы (свободные — с конца
/// списка). Устаревший хэндл — <see cref="InvalidOperationException"/> (кроме <c>IsAlive</c>). Установившийся режим не
/// выделяет памяти; обход внутри шага — по слотам, детерминирован.
/// </summary>
public interface IPhysicsWorld
{
    /// <summary>Новое сочленение из <paramref name="bodies"/> тел (от 1): кинематические, без формы, в начале координат, без суставов; тело 0 — первым свободным слотом.</summary>
    ArticulationHandle CreateArticulation(int bodies);

    /// <summary>Удалить сочленение вместе с его телами и суставами: их хэндлы устаревают.</summary>
    void DestroyArticulation(ArticulationHandle articulation);

    bool IsAlive(ArticulationHandle articulation);

    bool IsAlive(BodyHandle body);

    /// <summary>Тел в сочленении.</summary>
    int BodyCount(ArticulationHandle articulation);

    /// <summary>Тело <paramref name="index"/> сочленения (0 … <see cref="BodyCount"/> − 1).</summary>
    BodyHandle Body(ArticulationHandle articulation, int index);

    /// <summary>Сочленение, которому принадлежит тело.</summary>
    ArticulationHandle ArticulationOf(BodyHandle body);

    /// <summary>Состояние тела (копия).</summary>
    BodyState Get(BodyHandle body);

    /// <summary>Форма капсулы: масса, кг, и радиус, м, — больше 0; полудлина оси ≥ 0. Поза, скорости, режим и профиль не меняются.</summary>
    void SetShape(BodyHandle body, float mass, float radius, float halfLength);

    /// <summary>Поставить тело без скорости в любом режиме: прошлая поза и поза начала тика — та же, скорости — 0; форма, режим и профиль не меняются.</summary>
    void Place(BodyHandle body, Vector3 position, Quaternion rotation);

    /// <summary>
    /// Кинематическое тело — в цель тика: скорость — разность за <paramref name="dt"/> (&gt; 0), угловая —
    /// <see cref="PhysicsMath.RotationVector"/> / dt; шаг кинематическое тело не двигает. Тело на физике — <see cref="InvalidOperationException"/>.
    /// </summary>
    void SetKinematicTarget(BodyHandle body, Vector3 position, Quaternion rotation, float dt);

    // ——— Динамика тела, профили, настройки ———

    /// <summary>Режим тела; <see cref="BodyMode.Simulated"/> — только телу с массой. Поза и скорости сохраняются.</summary>
    void SetMode(BodyHandle body, BodyMode mode);

    /// <summary>Профиль динамики тела: номер из таблицы мира (0 … <see cref="DynamicsCount"/> − 1).</summary>
    void SetDynamics(BodyHandle body, int profile);

    /// <summary>Зарегистрировать профиль динамики (0 — по умолчанию; всего ≤ 256); негодный — <see cref="ArgumentException"/>. Таблица растёт: не установившийся режим.</summary>
    int RegisterDynamics(in BodyDynamics dynamics);

    /// <summary>Заменить профиль <paramref name="profile"/> (в том числе 0 — ручки на ходу): тела с этим номером берут новый со следующего подшага.</summary>
    void ReplaceDynamics(int profile, in BodyDynamics dynamics);

    BodyDynamics GetDynamics(int profile);

    /// <summary>Профилей в таблице мира (от 1).</summary>
    int DynamicsCount { get; }

    /// <summary>
    /// Записать состояние тела целиком (тесты, сцены, переход в труп). Негодное (нечисловое, неединичный поворот ±1e-3,
    /// отрицательные масса и инерция, тело на физике без массы) — <see cref="ArgumentException"/>, тело не меняется.
    /// </summary>
    void Set(BodyHandle body, in BodyState state);

    /// <summary>Чем толкает кинематическое тело. Каждый тик — по состоянию игры (клинок в ударе).</summary>
    void SetKinematicSource(BodyHandle body, KinematicSource source);

    /// <summary>Настройки физики; замена — целиком, между шагами. Негодные (<see cref="PhysicsSettings.Problem"/>) — <see cref="ArgumentException"/>.</summary>
    PhysicsSettings Settings { get; set; }

    // ——— Суставы ———

    /// <summary>
    /// Суставы сочленения в порядке решения (обычно от корня к листьям); прежние заменяются, все включены и привязаны по
    /// текущей позе (<see cref="RebindJoints"/>). У тела — не больше одного сустава, где оно ребёнок; негодное —
    /// <see cref="ArgumentException"/>. Прежний размер с тем же числом суставов — без выделения памяти.
    /// </summary>
    void SetJoints(ArticulationHandle articulation, ReadOnlySpan<JointDesc> joints);

    int JointCount(ArticulationHandle articulation);

    JointDesc Joint(ArticulationHandle articulation, int joint);

    /// <summary>
    /// Привязать суставы по текущей позе (переход в труп): ошибка точки — ноль, пределы наклона и скручивания — от этой
    /// позы, сгиб шарнира — от выпрямленного положения.
    /// </summary>
    void RebindJoints(ArticulationHandle articulation);

    /// <summary>Включить или выключить сустав: выключенный не держит ни точку, ни пределы (меч выпал).</summary>
    void SetJointEnabled(ArticulationHandle articulation, int joint, bool enabled);

    bool IsJointEnabled(ArticulationHandle articulation, int joint);

    /// <summary>Привязка сустава последней <see cref="SetJoints"/> или <see cref="RebindJoints"/> — для хеша и сравнения реализаций.</summary>
    JointBinding Binding(ArticulationHandle articulation, int joint);

    /// <summary>Срабатывания пределов сочленения за последний шаг, по порядку; пусто — пределы не срабатывали.</summary>
    ReadOnlySpan<JointLimitHit> JointLimitHits(ArticulationHandle articulation);

    /// <summary>Замер сустава в текущей позе: ошибка точки, углы, выход за предел.</summary>
    JointMeasure MeasureJoint(ArticulationHandle articulation, int joint);

    // ——— Самоколлизия ———

    /// <summary>
    /// Пары самоколлизии сочленения (какие — решает владелец). Пара действует, когда оба тела на физике и сочленение не
    /// спит: контакт как у разных сочленений, радиусы × <see cref="PhysicsSettings.SelfContactScale"/>, после пар
    /// сочленений подшага. Негодное — <see cref="ArgumentException"/>. Место под пары — с создания слота: без выделения памяти.
    /// </summary>
    void SetSelfContacts(ArticulationHandle articulation, ReadOnlySpan<BodyPair> pairs);

    /// <summary>Пары самоколлизии сочленения, по порядку решения; пусто — самоколлизии нет.</summary>
    ReadOnlySpan<BodyPair> SelfContacts(ArticulationHandle articulation);

    // ——— Статический мир, шаг, границы ———

    /// <summary>Земля: реализация игры; null — земли нет. Задаётся при сборке.</summary>
    void SetGround(IGround? ground);

    /// <summary>Стена: окружность радиуса <paramref name="radius"/>, м, вокруг начала координат в XZ — вся капсула остаётся внутри; <see cref="float.PositiveInfinity"/> — стены нет.</summary>
    void SetWall(float radius);

    /// <summary>Колонны: бесконечные вертикальные цилиндры — вся капсула тела остаётся снаружи (отрезок оси — не ближе <c>радиус колонны + радиус тела</c>). Копируются.</summary>
    void SetPillars(ReadOnlySpan<Pillar> pillars);

    /// <summary>
    /// Шаг физики на <paramref name="dt"/>, с: пробуждение → [интеграция → суставы → контакты с миром → пары сочленений →
    /// самоколлизия → трение покоя → мир после пар → скорости по профилю → скоростной проход → расслабление суставов]
    /// × <see cref="PhysicsSettings.Substeps"/> → границы → сон. Кинематические тела и спящие сочленения шаг не двигает
    /// (спящее — неподвижная опора). Порядок фаз — в README физики.
    /// </summary>
    void Step(float dt);

    /// <summary>Границы сочленения по позе конца последнего шага; созданное после него — пустые (радиус 0).</summary>
    ArticulationBounds Bounds(ArticulationHandle articulation);

    // ——— Толчки живых ———

    /// <summary>
    /// Удар толкающего в этом тике (до шага). Новый <paramref name="strike"/> закрывает проходы толкающего. Потенциал тика —
    /// <c>PassShare · mass · min(speed, KinematicPointSpeedMax)</c>, Н·с: у закрытого прохода заменяет потенциал, у открытого
    /// поднимает бюджет (не опускает). Без вызова в тике у закрытого прохода потенциала нет.
    /// </summary>
    void PreparePass(ArticulationHandle pusher, int strike, float mass, float speed);

    /// <summary>Засчитанное попадание: проход пары открывается, если закрыт (остаток ← потенциал), и тик отмечается.</summary>
    void PassHit(ArticulationHandle pusher, ArticulationHandle target);

    /// <summary>Списать <c>min(impulse, остаток)</c>, Н·с, из прохода пары; закрыт или impulse ≤ 0 — 0.</summary>
    float SpendHit(ArticulationHandle pusher, ArticulationHandle target, float impulse);

    /// <summary>
    /// Импульс удара, Н·с, в точке (мир). При <paramref name="impactSpeed"/> &gt; 0 — урезается до Δv точки ≤
    /// <c>HitDvShare · impactSpeed</c>; тело на физике и живой <paramref name="pusher"/> — списывается из прохода пары
    /// (сначала <see cref="PassHit"/>), отдача копится у толкающего. Возвращает приложенный импульс.
    /// </summary>
    Vector3 ApplyHit(BodyHandle body, Vector3 point, Vector3 impulse, float impactSpeed, ArticulationHandle pusher);

    /// <summary>Проход пары (толкающий и цель — разные живые сочленения) — для хеша и тестов.</summary>
    PassState Pass(ArticulationHandle pusher, ArticulationHandle target);

    /// <summary>Накопленная отдача сочленения, Н·с, не забирая — для хеша, описания и тестов.</summary>
    Vector3 Reaction(ArticulationHandle articulation);

    /// <summary>Забрать накопленную за тик отдачу сочленения, Н·с (сырой импульс; что с ним делать живому — решает игра). После — ноль.</summary>
    Vector3 TakeReaction(ArticulationHandle articulation);

    /// <summary>
    /// Конец тика толчков: открытый проход без контакта и попадания <see cref="PhysicsSettings.PassExitTicks"/> тиков подряд
    /// закрывается; у закрытых потенциал тика снимается; отметки тика сбрасываются.
    /// </summary>
    void EndKinematicTick();

    // ——— Трасса ———

    /// <summary>Трасса физики — диагностический режим; null — выключена (по умолчанию). В хеш не входит.</summary>
    PhysicsTrace? Trace { get; }

    /// <summary>Включить трассу (только владелец, между шагами): <paramref name="trace"/> или новая. Возвращает включённую.</summary>
    PhysicsTrace EnableTrace(PhysicsTrace? trace = null);

    void DisableTrace();

    // ——— Профайлер шага, нечисловые тела ———

    /// <summary>
    /// Профайлер CPU зон шага (<c>physics.integrate</c>, <c>joints</c>, <c>bounds</c>, <c>pairs</c>, <c>ground</c>,
    /// <c>velocity</c>, <c>contactVelocity</c>, <c>relax</c>, <c>sleep</c>); зоны заводятся здесь, не в шаге.
    /// <see cref="CpuProfiler.Off"/> — без зон, 0 Б; симуляцию не меняет.
    /// </summary>
    void AttachProfiler(Sword.Engine.Diagnostics.CpuProfiler profiler);

    /// <summary>Сочленений с нечисловым телом на физике (по концу шага и записям после него); 0 — проверять тела поштучно не нужно.</summary>
    int NonFiniteArticulations { get; }

    /// <summary>Есть ли у сочленения нечисловое тело на физике — то, что считает <see cref="NonFiniteArticulations"/>.</summary>
    bool HasNonFinite(ArticulationHandle articulation);

    // ——— Сон ———

    /// <summary>
    /// Спит ли сочленение: все тела на физике медленнее порогов сна <see cref="PhysicsSettings.SleepTime"/> подряд и оно
    /// опирается на землю или спящих. Спящее шаг не считает: скорости 0, неспящим — неподвижная опора. Причины пробуждения —
    /// <see cref="WakeReason"/>; подробно — README физики, «Сон».
    /// </summary>
    bool IsAsleep(ArticulationHandle articulation);

    /// <summary>Счётчик покоя, с: сколько подряд тела ниже порогов сна (у спящего растёт дальше, у проснувшегося — 0).</summary>
    float RestTime(ArticulationHandle articulation);

    /// <summary>Разбудить сочленение (между шагами; не спит — ничего): причина — <see cref="WakeReason.Command"/>, касающиеся спящие проснутся цепочкой в начале следующего шага.</summary>
    void Wake(ArticulationHandle articulation);

    int AsleepCount { get; }

    // ——— слоты ———

    int ArticulationCount { get; }

    int LiveBodyCount { get; }

    /// <summary>Слотов сочленений (живых и свободных).</summary>
    int ArticulationCapacity { get; }

    /// <summary>Поколение слота сочленения: у живого — поколение, у свободного — со знаком минус, у ни разу не выданного — 0.</summary>
    int ArticulationGenerationAt(int index);

    /// <summary>Живое сочленение в слоте; свободный — <see cref="ArticulationHandle.None"/> (обход по слотам — стабильный порядок).</summary>
    ArticulationHandle ArticulationAt(int index);

    /// <summary>Свободные слоты сочленений в порядке выдачи с конца (часть состояния: от него зависят будущие хэндлы).</summary>
    ReadOnlySpan<int> FreeArticulations { get; }

    /// <summary>Слотов тел (живых и свободных).</summary>
    int BodyCapacity { get; }

    /// <summary>Поколение слота тела — как у <see cref="ArticulationGenerationAt"/>.</summary>
    int BodyGenerationAt(int index);

    /// <summary>Живое тело в слоте; свободный — <see cref="BodyHandle.None"/>.</summary>
    BodyHandle BodyAt(int index);

    /// <summary>Свободные слоты тел в порядке выдачи с конца.</summary>
    ReadOnlySpan<int> FreeBodies { get; }
}
