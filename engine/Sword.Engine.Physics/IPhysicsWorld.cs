using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Физика одной симуляции (раздел 6 плана): тела-капсулы и сочленения за хэндлами (индекс + поколение). Интерфейс, а не
/// класс, — чтобы в одном процессе жили две реализации (эталон и оптимизированный кандидат: дифференциальный прогон,
/// раздел 8); у каждой симуляции свой экземпляр, общего состояния нет. Выдача слотов — часть договора: при одних вызовах
/// две реализации выдают одни хэндлы (свободные — с конца списка, первыми — младшие номера), иначе их не сравнить.
///
/// Этап 5: кинематические тела — форма (<see cref="SetShape(BodyHandle, float, float)"/>), постановка (<see cref="Place"/>,
/// <see cref="SetKinematicTarget"/>), концы (<see cref="BodyState.EndA"/>, <see cref="BodyState.EndB"/>). Этап 7 (контракт
/// зафиксирован на 7б, README «Этап 7: контракт»): динамика тел и профили (7б), суставы (7в), земля, стена, колонны и шаг
/// (7г), толчки живых и трасса (7д). Устаревший хэндл — исключение (<see cref="InvalidOperationException"/>) в любом
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
    /// Форма капсулы без массы: радиус и полудлина оси, м, конечные и не отрицательные. Масса тела не меняется; если она
    /// есть, инерция пересчитывается под новую форму (тогда радиус — больше 0). Перегрузка этапа 5 — до 7ж (игра перейдёт на
    /// форму с массой).
    /// </summary>
    void SetShape(BodyHandle body, float radius, float halfLength);

    /// <summary>
    /// Форма с массой (7б): масса, кг — конечная и больше 0; радиус, м — конечный и больше 0; полудлина — конечная, ≥ 0.
    /// Обратная масса и обратная инерция капсулы — <see cref="Dynamics.CapsuleInvInertia"/>.
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

    // ——— 7б: динамика тела, профили, настройки ———

    /// <summary>
    /// Режим тела (7б). <see cref="BodyMode.Simulated"/> — только телу с массой (иначе <see cref="InvalidOperationException"/>);
    /// поза и скорости сохраняются (переход в труп: скорости — от последней постановки).
    /// </summary>
    void SetMode(BodyHandle body, BodyMode mode);

    /// <summary>Профиль динамики тела (7б): номер из таблицы мира (0 … <see cref="DynamicsCount"/> − 1).</summary>
    void SetDynamics(BodyHandle body, int profile);

    /// <summary>
    /// Зарегистрировать профиль динамики (7б): номер нового профиля (профиль 0 — по умолчанию, есть с создания мира; всего не
    /// больше 256). Профиль — годный (<see cref="BodyDynamics.Problem"/>), иначе <see cref="ArgumentException"/>. Сборка
    /// игры — не установившийся режим: таблица растёт.
    /// </summary>
    int RegisterDynamics(in BodyDynamics dynamics);

    /// <summary>Заменить профиль <paramref name="profile"/> (7б; в том числе 0 — ручки на ходу): тела с этим номером берут новый со следующего подшага.</summary>
    void ReplaceDynamics(int profile, in BodyDynamics dynamics);

    /// <summary>Профиль динамики по номеру (7б).</summary>
    BodyDynamics GetDynamics(int profile);

    /// <summary>Профилей в таблице мира (7б; от 1).</summary>
    int DynamicsCount { get; }

    /// <summary>
    /// Записать состояние тела целиком (7б): тесты, команды сцен, переход в труп. Проверяется всё: поза и скорости конечны,
    /// повороты — единичные (±1e-3 по квадрату длины), форма, масса, обратные масса и инерция — конечные и не отрицательные,
    /// режим и толчок — из перечислений, профиль — из таблицы, тело на физике — с массой; негодное —
    /// <see cref="ArgumentException"/>, тело не меняется.
    /// </summary>
    void Set(BodyHandle body, in BodyState state);

    /// <summary>Чем толкает кинематическое тело (7б — запись; смысл толчка — 7д). Каждый тик — по состоянию игры (клинок в ударе).</summary>
    void SetKinematicSource(BodyHandle body, KinematicSource source);

    /// <summary>
    /// Настройки физики (7б): заданные при композиции; замена — целиком, между шагами (ручки игры). Негодные
    /// (<see cref="PhysicsSettings.Problem"/>) — <see cref="ArgumentException"/>.
    /// </summary>
    PhysicsSettings Settings { get; set; }

    // ——— 7в: суставы ———

    /// <summary>
    /// Суставы сочленения (7в): порядок — порядок решения (обычно от корня к листьям), номер сустава — индекс в
    /// <paramref name="joints"/>. Прежние суставы заменяются; все включены и привязаны по текущей позе
    /// (<see cref="RebindJoints"/>). Родитель и ребёнок — разные тела сочленения, у тела — не больше одного сустава, где оно
    /// ребёнок; вид — из перечисления; точка — конечная; пределы — конечные, как в <see cref="JointDesc"/>; у шарнира знак
    /// сгиба ±1 и ось единичная; негодное — <see cref="ArgumentException"/>, прежние суставы не меняются. Сочленение прежнего
    /// размера с тем же числом суставов — без выделения памяти (так же — новые описания по позе смерти).
    /// </summary>
    void SetJoints(ArticulationHandle articulation, ReadOnlySpan<JointDesc> joints);

    /// <summary>Суставов в сочленении (7в).</summary>
    int JointCount(ArticulationHandle articulation);

    /// <summary>Описание сустава <paramref name="joint"/> (7в).</summary>
    JointDesc Joint(ArticulationHandle articulation, int joint);

    /// <summary>
    /// Привязать суставы по текущей позе (7в; переход в труп): точка на родителе — точка ребёнка в системе родителя, поза
    /// покоя — нынешний поворот ребёнка относительно родителя; сразу после привязки ошибка точки — ноль, пределы наклона и
    /// скручивания отсчитываются от этой позы, сгиб шарнира — от выпрямленного положения.
    /// </summary>
    void RebindJoints(ArticulationHandle articulation);

    /// <summary>Включить или выключить сустав (7в): выключенный не держит ни точку, ни пределы (меч выпал).</summary>
    void SetJointEnabled(ArticulationHandle articulation, int joint, bool enabled);

    bool IsJointEnabled(ArticulationHandle articulation, int joint);

    /// <summary>
    /// Привязка сустава (7в): точка на родителе и поза покоя, записанные последней привязкой (<see cref="SetJoints"/>,
    /// <see cref="RebindJoints"/>) — состояние для хеша, описания и сравнения двух реализаций.
    /// </summary>
    JointBinding Binding(ArticulationHandle articulation, int joint);

    /// <summary>Срабатывания пределов сочленения за последний шаг (7в), по порядку; пусто — пределы не срабатывали.</summary>
    ReadOnlySpan<JointLimitHit> JointLimitHits(ArticulationHandle articulation);

    /// <summary>Замер сустава в текущей позе (7в): ошибка точки, углы, выход за предел.</summary>
    JointMeasure MeasureJoint(ArticulationHandle articulation, int joint);

    // ——— 7г: статический мир, шаг, границы ———

    /// <summary>Земля (7г): реализация игры; null — земли нет. Задаётся при сборке.</summary>
    void SetGround(IGround? ground);

    /// <summary>
    /// Стена (7г): окружность радиуса <paramref name="radius"/>, м, вокруг начала координат в плоскости XZ — вся капсула тела
    /// (оба конца оси с радиусом тела) остаётся внутри. <see cref="float.PositiveInfinity"/> — стены нет (по умолчанию).
    /// </summary>
    void SetWall(float radius);

    /// <summary>Колонны (7г): бесконечные вертикальные цилиндры — вся капсула тела остаётся снаружи (отрезок оси — не ближе <c>радиус колонны + радиус тела</c>). Копируются.</summary>
    void SetPillars(ReadOnlySpan<Pillar> pillars);

    /// <summary>
    /// Шаг физики на <paramref name="dt"/>, с (7г; конечный, больше 0), подшагов — <see cref="PhysicsSettings.Substeps"/>:
    /// поза начала тика у тел на физике ← нынешняя → [интеграция тел на физике → суставы (7в) → бюджеты толчков на подшаг
    /// (7д) → контакты с миром (земля — торцы, стена, колонны) → пары сочленений (тела на физике между собой; кинематическое
    /// ↔ на физике — толчок 7д) → итоги толчков (7д) → обновление скоростей по профилю → скоростной проход контактов (нормальная скорость в ноль, трение скольжения по Кулону) →
    /// расслабление суставов (7в)] × подшаги → границы сочленений. Кинематические тела шаг не двигает.
    /// </summary>
    void Step(float dt);

    /// <summary>
    /// Границы сочленения (7г) — по позе конца последнего шага (шаг считает их для всех живых сочленений); сочленение,
    /// созданное после него, — пустые (центр в начале координат, радиус 0). Отсев пар внутри шага — по свежим границам.
    /// </summary>
    ArticulationBounds Bounds(ArticulationHandle articulation);

    // ——— 7д: толчки живых ———

    /// <summary>
    /// Удар толкающего в этом тике (7д; до шага): номер удара <paramref name="strike"/> (новый — проходы толкающего
    /// начинаются «снаружи»), потенциал прохода этого тика —
    /// <c>PassShare · <paramref name="mass"/> · min(<paramref name="speed"/>, KinematicPointSpeedMax)</c>, Н·с — для всех пар
    /// толкающего. Масса удара, кг, и скорость (кончика клинка), м/с, — от игры.
    /// </summary>
    void PreparePass(ArticulationHandle pusher, int strike, float mass, float speed);

    /// <summary>Засчитанное попадание (7д): проход пары открывается, если закрыт (остаток ← потенциал), и тик отмечается.</summary>
    void PassHit(ArticulationHandle pusher, ArticulationHandle target);

    /// <summary>
    /// Списать импульс удара из прохода пары (7д): выдаётся <c>min(impulse, остаток)</c>, Н·с, и списывается; проход закрыт
    /// или импульс не больше 0 — 0.
    /// </summary>
    float SpendHit(ArticulationHandle pusher, ArticulationHandle target, float impulse);

    /// <summary>
    /// Импульс удара в тело (7д): <paramref name="impulse"/>, Н·с, в точке <paramref name="point"/> (мир). (1) При
    /// <paramref name="impactSpeed"/> &gt; 0 модуль урезается так, что Δv точки вдоль удара ≤ <c>HitDvShare · impactSpeed</c>;
    /// (2) тело на физике и <paramref name="pusher"/> жив — модуль списывается из прохода пары (<see cref="SpendHit"/>), а
    /// отдача (минус приложенный импульс) копится у толкающего; (3) импульс — в скорость и угловую скорость тела (и
    /// кинематического: тик смерти). Возвращает приложенный импульс.
    /// </summary>
    Vector3 ApplyHit(BodyHandle body, Vector3 point, Vector3 impulse, float impactSpeed, ArticulationHandle pusher);

    /// <summary>
    /// Проход пары (7д; толкающий и цель — разные живые сочленения) — состояние для хеша, описания, сравнения двух реализаций
    /// и тестов.
    /// </summary>
    PassState Pass(ArticulationHandle pusher, ArticulationHandle target);

    /// <summary>Накопленная отдача сочленения (7д), Н·с, не забирая — для хеша, описания и тестов.</summary>
    Vector3 Reaction(ArticulationHandle articulation);

    /// <summary>
    /// Забрать накопленную за тик отдачу сочленения (7д): вектор, Н·с (толчки и удары) — сырой импульс; во что он
    /// превращается у живого (горизонталь, доля, масса бойца), решает игра. После — ноль.
    /// </summary>
    Vector3 TakeReaction(ArticulationHandle articulation);

    /// <summary>
    /// Конец тика толчков (7д; после ударов и отдачи): проход без контакта и попадания <see cref="PhysicsSettings.PassExitTicks"/>
    /// тиков подряд закрывается, отметки тика сбрасываются.
    /// </summary>
    void EndKinematicTick();

    // ——— 7д: трасса ———

    /// <summary>Трасса физики — диагностический режим (7д); null — выключена (по умолчанию). В хеш не входит.</summary>
    PhysicsTrace? Trace { get; }

    /// <summary>Включить трассу (7д; только владелец, между шагами): <paramref name="trace"/> или новая. Возвращает включённую.</summary>
    PhysicsTrace EnableTrace(PhysicsTrace? trace = null);

    /// <summary>Выключить трассу (7д; между шагами).</summary>
    void DisableTrace();

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
