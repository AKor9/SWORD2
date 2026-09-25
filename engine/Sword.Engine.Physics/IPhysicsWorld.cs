using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Физика одной симуляции (раздел 6 плана): тела-капсулы и сочленения за хэндлами (индекс + поколение). Интерфейс, а не
/// класс, — чтобы в одном процессе жили две реализации (эталон и оптимизированный кандидат: дифференциальный прогон,
/// раздел 8); у каждой симуляции свой экземпляр, общего состояния нет. Выдача слотов — часть договора: при одних вызовах
/// две реализации выдают одни хэндлы (свободные — с конца списка, первыми — младшие номера), иначе их не сравнить.
///
/// Кинематические тела — форма (<see cref="SetShape"/>), постановка (<see cref="Place"/>,
/// <see cref="SetKinematicTarget"/>), концы (<see cref="BodyState.EndA"/>, <see cref="BodyState.EndB"/>). Динамика тел и профили,
/// суставы, земля, стена, колонны и шаг, толчки живых и трасса. Устаревший хэндл — исключение (<see cref="InvalidOperationException"/>) в любом
/// вызове, кроме <c>IsAlive</c>. Установившийся режим (постановка, шаг, создание и удаление сочленений прежних размеров) не
/// выделяет памяти. Порядок обхода внутри шага — по слотам (сочленения по номеру слота, тела — по порядку в сочленении):
/// детерминирован.
/// </summary>
public interface IPhysicsWorld
{
    /// <summary>
    /// Новое сочленение из <paramref name="bodies"/> тел (от 1): тела кинематические, без формы, в начале координат
    /// (<see cref="BodyState.New"/>), без суставов. Тела берутся из свободных слотов по порядку: тело 0 — первым.
    /// </summary>
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

    /// <summary>
    /// Форма капсулы с массой (единственная форма): масса,
    /// кг — конечная и больше 0; радиус, м — конечный и больше 0; полудлина оси — конечная, ≥ 0. Обратная масса и обратная
    /// инерция капсулы — <see cref="Dynamics.CapsuleInvInertia"/>. Поза, скорости, режим и профиль не меняются.
    /// </summary>
    void SetShape(BodyHandle body, float mass, float radius, float halfLength);

    /// <summary>
    /// Поставить тело без скорости (<c>RigidBody.Place</c>): прошлая поза и поза начала тика — та же, скорости — 0. В любом
    /// режиме (постройка бойца, команда сцены «сдвинуть тела» — и трупа, пересборка нечисловых тел); форма, масса, режим и
    /// профиль не меняются.
    /// </summary>
    void Place(BodyHandle body, Vector3 position, Quaternion rotation);

    /// <summary>
    /// Кинематическое тело — в цель тика: прошлая постановка и поза начала тика ← нынешняя, скорость — разность за
    /// <paramref name="dt"/>, угловая — <see cref="PhysicsMath.RotationVector"/> / dt; тело сразу в цели (шаг физики
    /// кинематическое тело не двигает). <paramref name="dt"/> — конечный и больше 0. Тело на физике —
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    void SetKinematicTarget(BodyHandle body, Vector3 position, Quaternion rotation, float dt);

    // ——— Динамика тела, профили, настройки ———

    /// <summary>
    /// Режим тела. <see cref="BodyMode.Simulated"/> — только телу с массой (иначе <see cref="InvalidOperationException"/>);
    /// поза и скорости сохраняются (переход в труп: скорости — от последней постановки).
    /// </summary>
    void SetMode(BodyHandle body, BodyMode mode);

    /// <summary>Профиль динамики тела: номер из таблицы мира (0 … <see cref="DynamicsCount"/> − 1).</summary>
    void SetDynamics(BodyHandle body, int profile);

    /// <summary>
    /// Зарегистрировать профиль динамики: номер нового профиля (профиль 0 — по умолчанию, есть с создания мира; всего не
    /// больше 256). Профиль — годный (<see cref="BodyDynamics.Problem"/>), иначе <see cref="ArgumentException"/>. Сборка
    /// игры — не установившийся режим: таблица растёт.
    /// </summary>
    int RegisterDynamics(in BodyDynamics dynamics);

    /// <summary>Заменить профиль <paramref name="profile"/> (в том числе 0 — ручки на ходу): тела с этим номером берут новый со следующего подшага.</summary>
    void ReplaceDynamics(int profile, in BodyDynamics dynamics);

    /// <summary>Профиль динамики по номеру.</summary>
    BodyDynamics GetDynamics(int profile);

    /// <summary>Профилей в таблице мира (от 1).</summary>
    int DynamicsCount { get; }

    /// <summary>
    /// Записать состояние тела целиком: тесты, команды сцен, переход в труп. Проверяется всё: поза и скорости конечны,
    /// повороты — единичные (±1e-3 по квадрату длины), форма, масса, обратные масса и инерция — конечные и не отрицательные,
    /// режим и толчок — из перечислений, профиль — из таблицы, тело на физике — с массой; негодное —
    /// <see cref="ArgumentException"/>, тело не меняется.
    /// </summary>
    void Set(BodyHandle body, in BodyState state);

    /// <summary>Чем толкает кинематическое тело. Каждый тик — по состоянию игры (клинок в ударе).</summary>
    void SetKinematicSource(BodyHandle body, KinematicSource source);

    /// <summary>
    /// Настройки физики: заданные при композиции; замена — целиком, между шагами (ручки игры). Негодные
    /// (<see cref="PhysicsSettings.Problem"/>) — <see cref="ArgumentException"/>.
    /// </summary>
    PhysicsSettings Settings { get; set; }

    // ——— Суставы ———

    /// <summary>
    /// Суставы сочленения: порядок — порядок решения (обычно от корня к листьям), номер сустава — индекс в
    /// <paramref name="joints"/>. Прежние суставы заменяются; все включены и привязаны по текущей позе
    /// (<see cref="RebindJoints"/>). Родитель и ребёнок — разные тела сочленения, у тела — не больше одного сустава, где оно
    /// ребёнок; вид — из перечисления; точка — конечная; пределы — конечные, как в <see cref="JointDesc"/>; у шарнира знак
    /// сгиба ±1 и ось единичная; негодное — <see cref="ArgumentException"/>, прежние суставы не меняются. Сочленение прежнего
    /// размера с тем же числом суставов — без выделения памяти (так же — новые описания по позе смерти).
    /// </summary>
    void SetJoints(ArticulationHandle articulation, ReadOnlySpan<JointDesc> joints);

    /// <summary>Суставов в сочленении.</summary>
    int JointCount(ArticulationHandle articulation);

    /// <summary>Описание сустава <paramref name="joint"/>.</summary>
    JointDesc Joint(ArticulationHandle articulation, int joint);

    /// <summary>
    /// Привязать суставы по текущей позе (переход в труп): точка на родителе — точка ребёнка в системе родителя, поза
    /// покоя — нынешний поворот ребёнка относительно родителя; сразу после привязки ошибка точки — ноль, пределы наклона и
    /// скручивания отсчитываются от этой позы, сгиб шарнира — от выпрямленного положения.
    /// </summary>
    void RebindJoints(ArticulationHandle articulation);

    /// <summary>Включить или выключить сустав: выключенный не держит ни точку, ни пределы (меч выпал).</summary>
    void SetJointEnabled(ArticulationHandle articulation, int joint, bool enabled);

    bool IsJointEnabled(ArticulationHandle articulation, int joint);

    /// <summary>
    /// Привязка сустава: точка на родителе и поза покоя, записанные последней привязкой (<see cref="SetJoints"/>,
    /// <see cref="RebindJoints"/>) — состояние для хеша, описания и сравнения двух реализаций.
    /// </summary>
    JointBinding Binding(ArticulationHandle articulation, int joint);

    /// <summary>Срабатывания пределов сочленения за последний шаг, по порядку; пусто — пределы не срабатывали.</summary>
    ReadOnlySpan<JointLimitHit> JointLimitHits(ArticulationHandle articulation);

    /// <summary>Замер сустава в текущей позе: ошибка точки, углы, выход за предел.</summary>
    JointMeasure MeasureJoint(ArticulationHandle articulation, int joint);

    // ——— Самоколлизия ———

    /// <summary>
    /// Самоколлизия сочленения: пары его тел, которые сталкиваются между собой, — какие пары, решает владелец (у трупа бойца — таблица
    /// игры: все пары, кроме связанных суставом и стоящих внахлёст). Пара действует, когда оба тела — на физике и сочленение не спит: контакт
    /// как у тел разных сочленений (податливость <see cref="PhysicsSettings.ContactCompliance"/>, застарелая глубина — не быстрее
    /// <see cref="PhysicsSettings.PopSpeed"/>, трение пар, скоростной проход), радиусы — × <see cref="PhysicsSettings.SelfContactScale"/>;
    /// порядок — порядок пар, после пар сочленений подшага. Прежние пары заменяются (другие — будят спящее). Номера — тела сочленения, разные,
    /// пара — не дважды (в любом порядке); негодное — <see cref="ArgumentException"/>, прежние не меняются. Место под все пары сочленения —
    /// за слотом с его создания: запись — без выделения памяти. Новое сочленение в слоте — без пар.
    /// </summary>
    void SetSelfContacts(ArticulationHandle articulation, ReadOnlySpan<BodyPair> pairs);

    /// <summary>Пары самоколлизии сочленения, по порядку решения; пусто — самоколлизии нет.</summary>
    ReadOnlySpan<BodyPair> SelfContacts(ArticulationHandle articulation);

    // ——— Статический мир, шаг, границы ———

    /// <summary>Земля: реализация игры; null — земли нет. Задаётся при сборке.</summary>
    void SetGround(IGround? ground);

    /// <summary>
    /// Стена: окружность радиуса <paramref name="radius"/>, м, вокруг начала координат в плоскости XZ — вся капсула тела
    /// (оба конца оси с радиусом тела) остаётся внутри. <see cref="float.PositiveInfinity"/> — стены нет (по умолчанию).
    /// </summary>
    void SetWall(float radius);

    /// <summary>Колонны: бесконечные вертикальные цилиндры — вся капсула тела остаётся снаружи (отрезок оси — не ближе <c>радиус колонны + радиус тела</c>). Копируются.</summary>
    void SetPillars(ReadOnlySpan<Pillar> pillars);

    /// <summary>
    /// Шаг физики на <paramref name="dt"/>, с (конечный, больше 0), подшагов — <see cref="PhysicsSettings.Substeps"/>:
    /// пробуждение спящих (тело живого или неспящее тело на физике у спящего, цепочка по куче) → поза начала тика у тел на физике
    /// неспящих сочленений ← нынешняя → [интеграция тел на физике → суставы (унаследованный разрыв точки — не
    /// быстрее <see cref="PhysicsSettings.JointPopSpeed"/>) → бюджеты толчков на подшаг → контакты с миром (земля — торцы, стена,
    /// колонны) → пары сочленений (тела на физике между собой; кинематическое ↔ на физике — толчок) → самоколлизия (пары
    /// <see cref="SetSelfContacts"/>) → трение покоя пар → мир после пар (тела, которых поправили пары, — только сделанное
    /// парами, от позы до пар) → итоги толчков → обновление скоростей по профилю → скоростной проход контактов (нормальная скорость в
    /// ноль, трение скольжения по Кулону — о мир и между телами на физике) → расслабление суставов (сначала трение суставов
    /// <see cref="PhysicsSettings.JointFriction"/>)] × подшаги → границы сочленений, счёт сочленений с
    /// нечисловым телом → сон (счётчики покоя, засыпание — скорости 0, поза начала тика и прошлая ← поза). Кинематические тела шаг не
    /// двигает; спящие сочленения шаг не считает — неспящему телу на физике спящее тело — неподвижная опора. Включённая трасса (<see cref="Trace"/>) пишет шаг целиком: энергия до шага
    /// (база роста за подшаг 0) → подшаги (с ростом по фазам) → итог тика; подключённый профайлер (<see cref="AttachProfiler"/>) — зоны
    /// фаз подшага.
    /// </summary>
    void Step(float dt);

    /// <summary>
    /// Границы сочленения — по позе конца последнего шага (шаг считает их для всех живых сочленений); сочленение,
    /// созданное после него, — пустые (центр в начале координат, радиус 0). Отсев пар внутри шага — по свежим границам.
    /// </summary>
    ArticulationBounds Bounds(ArticulationHandle articulation);

    // ——— Толчки живых ———

    /// <summary>
    /// Удар толкающего в этом тике (до шага): номер удара <paramref name="strike"/> (новый — проходы толкающего
    /// начинаются «снаружи»: не открыты, остатка и пустых тиков нет), потенциал этого тика —
    /// <c>PassShare · <paramref name="mass"/> · min(<paramref name="speed"/>, KinematicPointSpeedMax)</c>, Н·с — для всех пар
    /// толкающего: у закрытого прохода <see cref="PassState.Potential"/> ← потенциал тика; у открытого бюджет растёт до
    /// потенциала, если тот больше (остаток — на ту же прибавку), и не падает, когда клинок замедлился. Масса удара, кг, и
    /// скорость (кончика клинка), м/с, — от игры; без вызова в тике у закрытого прохода потенциала нет.
    /// </summary>
    void PreparePass(ArticulationHandle pusher, int strike, float mass, float speed);

    /// <summary>Засчитанное попадание: проход пары открывается, если закрыт (остаток ← потенциал), и тик отмечается.</summary>
    void PassHit(ArticulationHandle pusher, ArticulationHandle target);

    /// <summary>
    /// Списать импульс удара из прохода пары: выдаётся <c>min(impulse, остаток)</c>, Н·с, и списывается; проход закрыт
    /// или импульс не больше 0 — 0.
    /// </summary>
    float SpendHit(ArticulationHandle pusher, ArticulationHandle target, float impulse);

    /// <summary>
    /// Импульс удара в тело: <paramref name="impulse"/>, Н·с, в точке <paramref name="point"/> (мир). (1) При
    /// <paramref name="impactSpeed"/> &gt; 0 модуль урезается так, что Δv точки вдоль удара ≤ <c>HitDvShare · impactSpeed</c>;
    /// (2) тело на физике и <paramref name="pusher"/> жив (другое сочленение) — модуль списывается из прохода пары
    /// (<see cref="SpendHit"/>), а отдача (минус приложенный импульс) копится у толкающего; проход удар не открывает — сначала
    /// <see cref="PassHit"/> (закрытый проход — импульса нет); (3) импульс — в скорость и угловую скорость тела (и
    /// кинематического: тик смерти, без прохода и отдачи). Возвращает приложенный импульс.
    /// </summary>
    Vector3 ApplyHit(BodyHandle body, Vector3 point, Vector3 impulse, float impactSpeed, ArticulationHandle pusher);

    /// <summary>
    /// Проход пары (толкающий и цель — разные живые сочленения) — состояние для хеша, описания, сравнения двух реализаций
    /// и тестов.
    /// </summary>
    PassState Pass(ArticulationHandle pusher, ArticulationHandle target);

    /// <summary>Накопленная отдача сочленения, Н·с, не забирая — для хеша, описания и тестов.</summary>
    Vector3 Reaction(ArticulationHandle articulation);

    /// <summary>
    /// Забрать накопленную за тик отдачу сочленения: вектор, Н·с (толчки и удары) — сырой импульс; во что он
    /// превращается у живого (горизонталь, доля, масса бойца), решает игра. После — ноль.
    /// </summary>
    Vector3 TakeReaction(ArticulationHandle articulation);

    /// <summary>
    /// Конец тика толчков (после ударов и отдачи): открытый проход без контакта и попадания
    /// <see cref="PhysicsSettings.PassExitTicks"/> тиков подряд закрывается (остаток — 0); у закрытых проходов потенциал тика
    /// снимается (<see cref="PassState.Potential"/> ← 0); отметки тика сбрасываются.
    /// </summary>
    void EndKinematicTick();

    // ——— Трасса ———

    /// <summary>Трасса физики — диагностический режим; null — выключена (по умолчанию). В хеш не входит.</summary>
    PhysicsTrace? Trace { get; }

    /// <summary>Включить трассу (только владелец, между шагами): <paramref name="trace"/> или новая. Возвращает включённую.</summary>
    PhysicsTrace EnableTrace(PhysicsTrace? trace = null);

    /// <summary>Выключить трассу (между шагами).</summary>
    void DisableTrace();

    // ——— Профайлер шага, нечисловые тела ———

    /// <summary>
    /// Профайлер CPU зон внутри шага (владелец — между шагами): по подшагам <c>physics.integrate</c> (интеграция),
    /// <c>physics.joints</c> (суставы), <c>physics.bounds</c> (отсев сочленений: коробки тел и сочленений, пары коробок; и границы конца
    /// шага), <c>physics.pairs</c> (пары тел: отсев тел по коробке чужого сочленения, ближайшие точки, контакты, толчки живых, трение покоя
    /// пар), <c>physics.ground</c> (земля, стена, колонны — мир и мир после пар, два вызова на подшаг), <c>physics.velocity</c> (скорости по
    /// профилю), <c>physics.contactVelocity</c> (скоростной проход),
    /// <c>physics.relax</c> (расслабление суставов), <c>physics.sleep</c> (сон: пробуждение до подшагов и засыпание в конце шага, два вызова на
    /// шаг). Зоны заводятся здесь (память — при заведении, не в шаге); <see cref="CpuProfiler.Off"/> —
    /// без зон (по умолчанию). Выключенный профайлер — проверка поля на зону, 0 Б; симуляцию профайлер не меняет.
    /// </summary>
    void AttachProfiler(Sword.Engine.Diagnostics.CpuProfiler profiler);

    /// <summary>
    /// Сочленений, у которых хоть одно тело на физике нечисловое (<see cref="BodyState.IsFinite"/>) — по концу последнего шага и записям
    /// тел после него (<see cref="Place"/>, <see cref="Set"/>, <see cref="SetMode"/>, <see cref="ApplyHit"/>). 0 — все тела на физике
    /// конечны: проверять тела поштучно не нужно (пересборка трупа). Кинематические тела не считаются.
    /// </summary>
    int NonFiniteArticulations { get; }

    /// <summary>Есть ли у сочленения нечисловое тело на физике — то, что считает <see cref="NonFiniteArticulations"/>.</summary>
    bool HasNonFinite(ArticulationHandle articulation);

    // ——— Сон ———

    /// <summary>
    /// Спит ли сочленение (README физики, «Устройство» → «Сон»): все его тела на физике (кинематических нет), каждое медленнее
    /// <see cref="PhysicsSettings.SleepBodySpeed"/> (самая быстрая точка) и всё сочленение медленнее <see cref="PhysicsSettings.SleepSpeed"/>
    /// (среднеквадратичная скорость массы), <see cref="PhysicsSettings.SleepTime"/> подряд, рядом нет того, что его разбудило бы, и оно опирается
    /// на землю или спящих. Спящее шаг не считает (ни интеграции, ни суставов, ни земли, ни пар, ни расслабления): скорости — 0, поза, прошлая
    /// поза и поза начала тика — одна; неспящему телу на физике оно — неподвижная опора (как земля). Будят — <see cref="WakeReason"/>: до
    /// подшагов шага — тело живого или неспящее тело на физике у спящего (движется быстрее порога части или вошло глубже допуска), падающий
    /// труп под спящим и цепочка на одно касание (проснувшееся будит касающихся спящих в том же тике); между шагами — удар, запись тела, режим,
    /// профиль, суставы, настройки, <see cref="Wake"/>, а запись, уводящая тело на физике (<see cref="Place"/>, <see cref="Set"/>,
    /// <see cref="SetShape"/>, <see cref="SetMode"/> в кинематику, <see cref="DestroyArticulation"/>), — и спящих, лежавших на нём (по позе до
    /// записи). Состояние для хеша, описания и клиента.
    /// </summary>
    bool IsAsleep(ArticulationHandle articulation);

    /// <summary>
    /// Счётчик покоя, с: сколько подряд все тела сочленения на физике медленнее порогов сна (у спящего — считает дальше, пока его не
    /// разбудят; проснувшееся — 0). Состояние для хеша, описания и инвариантов (непрерывный сон — счётчик не убывал).
    /// </summary>
    float RestTime(ArticulationHandle articulation);

    /// <summary>Разбудить сочленение (между шагами; не спит — ничего): причина — <see cref="WakeReason.Command"/>, касающиеся спящие проснутся цепочкой в начале следующего шага.</summary>
    void Wake(ArticulationHandle articulation);

    /// <summary>Спящих сочленений.</summary>
    int AsleepCount { get; }

    // ——— слоты ———

    /// <summary>Живых сочленений.</summary>
    int ArticulationCount { get; }

    /// <summary>Живых тел.</summary>
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
