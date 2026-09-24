using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Эталонная реализация <see cref="IPhysicsWorld"/>: тела — массив состояний по слотам (AoS), идеи — <c>RigidBody</c> SWORD
/// 2eab13b. Оптимизированная раскладка (SoA, острова) — отдельной реализацией рядом, эта остаётся оракулом в тестах
/// (раздел 6 плана).
///
/// Слоты тел и сочленений — как у пула поз: поколение слота (у свободного — со знаком минус), список свободных, из
/// которого берут с конца; рост — удвоением, новые свободные кладутся так, чтобы первыми выдавались младшие номера.
/// Удалённое сочленение возвращает тела с конца, поэтому следующее того же размера получает те же слоты тем же порядком.
/// Массив тел сочленения остаётся за его слотом и переиспользуется — создание и удаление сочленений прежних размеров
/// памяти не выделяет.
///
/// Класс разбит на части по точкам этапа 7 (README «Раскладка файлов»): здесь — слоты и связь частей (крючки в конце
/// файла); <c>PhysicsWorld.Dynamics.cs</c> — динамика тел, профили, настройки (7б); <c>PhysicsWorld.Joints.cs</c> — суставы
/// (7в); <c>PhysicsWorld.Contacts.cs</c>, <c>PhysicsWorld.Step.cs</c> — статический мир, контакты, шаг, границы (7г);
/// <c>KinematicContacts.cs</c>, <c>PhysicsTrace.cs</c> — толчки живых и трасса (7д).
/// </summary>
public sealed partial class PhysicsWorld : IPhysicsWorld
{
    private BodyState[] _bodies = [];
    private int[] _bodyGeneration = [], _bodyOwner = [], _freeBodies = [];
    private int _freeBodyCount, _bodyCapacity;

    private int[] _artGeneration = [], _artCount = [], _freeArts = [];
    private int[]?[] _artBodies = [];
    private int _freeArtCount, _artCapacity;

    /// <summary>
    /// Трасса физики (7д); null — выключена. Поле — здесь (7б), чтобы шаг (7г) и суставы (7в) проверяли его до того, как
    /// собирать аргументы крючков трассы. Пишет только 7д (<c>EnableTrace</c>, <c>DisableTrace</c>).
    /// </summary>
    private PhysicsTrace? _trace = null;

    /// <param name="articulations">Слотов сочленений на старте (растёт удвоением).</param>
    /// <param name="bodies">Слотов тел на старте (растёт удвоением).</param>
    /// <param name="settings">Настройки физики (null — <see cref="PhysicsSettings.Default"/>); негодные — <see cref="ArgumentException"/>.</param>
    public PhysicsWorld(int articulations = 16, int bodies = 256, PhysicsSettings? settings = null)
    {
        Settings = settings ?? PhysicsSettings.Default;
        GrowArticulations(Math.Max(articulations, 1));
        GrowBodies(Math.Max(bodies, 1));
    }

    public int ArticulationCount => _artCapacity - _freeArtCount;
    public int LiveBodyCount => _bodyCapacity - _freeBodyCount;
    public int ArticulationCapacity => _artCapacity;
    public int BodyCapacity => _bodyCapacity;
    public ReadOnlySpan<int> FreeArticulations => _freeArts.AsSpan(0, _freeArtCount);
    public ReadOnlySpan<int> FreeBodies => _freeBodies.AsSpan(0, _freeBodyCount);
    public PhysicsTrace? Trace => _trace;

    public int ArticulationGenerationAt(int index) => _artGeneration[index];
    public int BodyGenerationAt(int index) => _bodyGeneration[index];

    public ArticulationHandle ArticulationAt(int index) =>
        (uint)index < (uint)_artCapacity && _artGeneration[index] > 0 ? new ArticulationHandle(index, _artGeneration[index]) : ArticulationHandle.None;

    public BodyHandle BodyAt(int index) =>
        (uint)index < (uint)_bodyCapacity && _bodyGeneration[index] > 0 ? new BodyHandle(index, _bodyGeneration[index]) : BodyHandle.None;

    public bool IsAlive(ArticulationHandle a) =>
        a.Generation > 0 && (uint)a.Index < (uint)_artCapacity && _artGeneration[a.Index] == a.Generation;

    public bool IsAlive(BodyHandle b) =>
        b.Generation > 0 && (uint)b.Index < (uint)_bodyCapacity && _bodyGeneration[b.Index] == b.Generation;

    public ArticulationHandle CreateArticulation(int bodies)
    {
        if (bodies < 1) throw new ArgumentOutOfRangeException(nameof(bodies), bodies, "в сочленении — от одного тела");
        if (_freeArtCount == 0) GrowArticulations(_artCapacity * 2);
        int a = _freeArts[--_freeArtCount];
        int gen = -_artGeneration[a] + 1;   // у свободного слота поколение хранится со знаком минус
        _artGeneration[a] = gen;
        var members = _artBodies[a];
        if (members is null || members.Length < bodies) _artBodies[a] = members = new int[bodies];
        _artCount[a] = bodies;
        for (int k = 0; k < bodies; k++)
        {
            if (_freeBodyCount == 0) GrowBodies(_bodyCapacity * 2);
            int b = _freeBodies[--_freeBodyCount];
            _bodyGeneration[b] = -_bodyGeneration[b] + 1;
            _bodyOwner[b] = a;
            _bodies[b] = BodyState.New(BodyMode.Kinematic);
            members[k] = b;
        }
        JointsReset(a);
        ContactsReset(a);
        KinematicReset(a);
        return new ArticulationHandle(a, gen);
    }

    public void DestroyArticulation(ArticulationHandle articulation)
    {
        Check(articulation);
        int a = articulation.Index;
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++) SleepWakeAround(members[k]);   // 7з-3: лежавшие на нём спящие теряют опору
        JointsReset(a);
        ContactsReset(a);
        KinematicReset(a);
        // С конца: следующее сочленение того же размера возьмёт те же слоты тем же порядком.
        for (int k = _artCount[a] - 1; k >= 0; k--)
        {
            int b = members[k];
            _bodyGeneration[b] = -_bodyGeneration[b];
            _bodyOwner[b] = -1;
            _bodies[b] = default;
            _freeBodies[_freeBodyCount++] = b;
        }
        _artCount[a] = 0;
        _artGeneration[a] = -_artGeneration[a];
        _freeArts[_freeArtCount++] = a;
    }

    public int BodyCount(ArticulationHandle articulation)
    {
        Check(articulation);
        return _artCount[articulation.Index];
    }

    public BodyHandle Body(ArticulationHandle articulation, int index)
    {
        Check(articulation);
        if ((uint)index >= (uint)_artCount[articulation.Index])
            throw new ArgumentOutOfRangeException(nameof(index), index, $"в сочленении {articulation} тел {_artCount[articulation.Index]}");
        int b = _artBodies[articulation.Index]![index];
        return new BodyHandle(b, _bodyGeneration[b]);
    }

    public ArticulationHandle ArticulationOf(BodyHandle body)
    {
        Check(body);
        int a = _bodyOwner[body.Index];
        return new ArticulationHandle(a, _artGeneration[a]);
    }

    public BodyState Get(BodyHandle body)
    {
        Check(body);
        return _bodies[body.Index];
    }

    public void Place(BodyHandle body, Vector3 position, Quaternion rotation)
    {
        Check(body);
        SleepWakeBody(body.Index, WakeReason.Command);   // 7з-3: запись тела будит
        ref var s = ref _bodies[body.Index];
        if (s.Position != position || s.Rotation != rotation) SleepWakeAround(body.Index);   // 7з-3: лежавшие на нём спящие — по позе до записи
        s.Position = s.PrevPosition = s.StartPosition = position;
        s.Rotation = s.PrevRotation = s.StartRotation = rotation;
        s.Velocity = s.AngularVelocity = Vector3.Zero;
        if (s.Mode == BodyMode.Simulated) StepMarkNonFinite(body.Index);
    }

    public void SetKinematicTarget(BodyHandle body, Vector3 position, Quaternion rotation, float dt)
    {
        Check(body);
        if (!float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt), dt, "шаг — конечный и больше 0");
        ref var s = ref _bodies[body.Index];
        if (s.Mode != BodyMode.Kinematic) throw new InvalidOperationException($"тело {body} не кинематическое ({s.Mode})");
        // Порядок и арифметика — кинематическая ветка Place в PlaceBodies SWORD; поза начала тика — прошлая постановка.
        s.PrevPosition = s.StartPosition = s.Position;
        s.PrevRotation = s.StartRotation = s.Rotation;
        s.Velocity = (position - s.Position) / dt;
        s.AngularVelocity = PhysicsMath.RotationVector(s.Rotation, rotation) / dt;
        s.Position = position;
        s.Rotation = rotation;
    }

    private void Check(ArticulationHandle a)
    {
        if (!IsAlive(a)) throw new InvalidOperationException($"сочленение {a} не живо (слотов {_artCapacity})");
    }

    private void Check(BodyHandle b)
    {
        if (!IsAlive(b)) throw new InvalidOperationException($"тело {b} не живо (слотов {_bodyCapacity})");
    }

    private void GrowArticulations(int capacity)
    {
        int old = _artCapacity;
        Array.Resize(ref _artGeneration, capacity);
        Array.Resize(ref _artCount, capacity);
        Array.Resize(ref _artBodies, capacity);
        Array.Resize(ref _freeArts, capacity);
        // свободные — с конца списка, чтобы первыми выдавались младшие номера
        for (int i = capacity - 1; i >= old; i--) _freeArts[_freeArtCount++] = i;
        _artCapacity = capacity;
        GrowParts();
    }

    private void GrowBodies(int capacity)
    {
        int old = _bodyCapacity;
        Array.Resize(ref _bodies, capacity);
        Array.Resize(ref _bodyGeneration, capacity);
        Array.Resize(ref _bodyOwner, capacity);
        Array.Resize(ref _freeBodies, capacity);
        for (int i = old; i < capacity; i++) _bodyOwner[i] = -1;
        for (int i = capacity - 1; i >= old; i--) _freeBodies[_freeBodyCount++] = i;
        _bodyCapacity = capacity;
        GrowParts();
    }

    private void GrowParts()
    {
        JointsGrow(_artCapacity, _bodyCapacity);
        ContactsGrow(_artCapacity, _bodyCapacity);
        KinematicGrow(_artCapacity, _bodyCapacity);
    }

    // ——— Связь частей класса (этап 7, контракт 7б) ———
    // Крючки — partial-методы без модификатора доступа: объявлены здесь, реализуют точки в своих файлах; пока реализации нет,
    // вызов исчезает вместе с аргументами. Так шаг (7г) пишется, не дожидаясь суставов (7в) и толчков (7д). Реализация у
    // крючка одна; новый крючок — правка контракта (отдельный коммит). Крючки не проверяют хэндлы: номера — слоты живых тел и
    // сочленений, их проверил вызывающий. Внутри шага — без выделения памяти.

    /// <summary>
    /// Слоты выросли (7в): сочленений <paramref name="articulations"/>, тел <paramref name="bodies"/> — массивы суставов по
    /// слотам дорастить. Только рост; зовётся и из конструктора, пока тел ещё 0.
    /// </summary>
    partial void JointsGrow(int articulations, int bodies);

    /// <summary>Сочленение в слоте создано (после выдачи тел) или удаляется (до возврата тел) (7в): суставов нет, журнал пуст.</summary>
    partial void JointsReset(int articulation);

    /// <summary>Слоты выросли (7г): границы сочленений и списки контактов подшага — дорастить.</summary>
    partial void ContactsGrow(int articulations, int bodies);

    /// <summary>Сочленение в слоте создано или удаляется (7г): границы — пустые.</summary>
    partial void ContactsReset(int articulation);

    /// <summary>Слоты выросли (7д): проходы и бюджеты пар, отдача, снимки трассы — дорастить.</summary>
    partial void KinematicGrow(int articulations, int bodies);

    /// <summary>
    /// Сочленение в слоте создано или удаляется (7д): проходы и бюджеты всех пар с этим слотом (в обе стороны) и его отдача —
    /// в начальное состояние: новое сочленение в том же слоте чужого прохода не наследует (у SWORD наследовало).
    /// </summary>
    partial void KinematicReset(int articulation);

    /// <summary>
    /// Подшаг <paramref name="substep"/> (0 …; из шага 7г, после интеграции): проход суставов (7в) всех сочленений с суставами
    /// и телами на физике — точка сустава и пределы по порядку суставов, срабатывания — в журнал (подшаг 0 — начало шага:
    /// журнал прошлого шага очищается); кинематическое тело — неподвижная сторона.
    /// </summary>
    partial void SolveJoints(float h, int substep);

    /// <summary>Подшаг <paramref name="substep"/> (из шага 7г, после скоростного прохода контактов): расслабление скоростей суставов (7в).</summary>
    partial void RelaxJoints(float h, int substep);

    /// <summary>Подшаг (из шага 7г, до контактов): бюджеты пар толчков — на подшаг, <c>PushForce · h²</c> (7д).</summary>
    partial void BeginKinematicSubstep(float h);

    /// <summary>
    /// Контакт кинематического тела <paramref name="kinematic"/> с телом на физике <paramref name="body"/> (из прохода пар 7г;
    /// номера — слоты тел разных сочленений): точки — на поверхностях капсул, нормаль <paramref name="normal"/> — от
    /// кинематического к телу на физике (куда его толкают), <paramref name="depth"/> — полная глубина, м (&gt; 0). Толчок
    /// (7д): предел λ — бюджет пары, бюджет прохода (<see cref="BodyState.Source"/>), скорость толкающей точки; отдача
    /// толкающему. Трассу толчка (снимок и поправка источника <c>PushBody + Source</c>, контакт) пишет сам 7д внутри крючка;
    /// 7г вокруг вызова трассу не зовёт.
    /// </summary>
    partial void PushKinematic(int kinematic, int body, Vector3 kinematicPoint, Vector3 point, Vector3 normal, float depth, float h);

    /// <summary>Подшаг (из шага 7г, после контактов): итоги бюджетов пар подшага — трассе (7д).</summary>
    partial void EndKinematicSubstep();

    /// <summary>
    /// Трасса (7д; зовут 7в и 7г при <c>_trace</c> не null): снимок тела перед этапом подшага. Пара — два снимка
    /// (<c>TraceBegin(a); TraceBegin(b)</c>), затем два <see cref="TraceEnd"/>.
    /// </summary>
    partial void TraceBegin(int body);

    /// <summary>Трасса: поправка тела этапом <paramref name="source"/> с последнего <see cref="TraceBegin"/> — сдвиг и угол за h.</summary>
    partial void TraceEnd(int body, PhysicsSource source, float h);

    /// <summary>
    /// Трасса: контакт источника <paramref name="source"/> в подшаге — тело <paramref name="body"/> и второе тело пары
    /// <paramref name="other"/> (−1 — мир: земля, стена, колонна), точка на теле и нормаль к нему (мир), настоящая глубина и
    /// решённая в подшаге, м. Из них — статистика глубин и контакты последнего шага (оверлей <c>contacts</c>, 7и).
    /// </summary>
    partial void TraceContact(PhysicsSource source, int body, int other, Vector3 point, Vector3 normal, float depth, float solved);

    /// <summary>Трасса: трение торца тела <paramref name="body"/> о землю — нормальная и касательная λ, держало ли.</summary>
    partial void TraceFriction(int body, float lambdaN, float lambdaT, bool held);

    /// <summary>Трасса: скоростной проход погасил нормальную скорость контакта тела <paramref name="body"/>, м/с.</summary>
    partial void TraceVelocity(PhysicsSource source, int body, float normalSpeed);

    /// <summary>
    /// Трасса (из шага 7г, в конце подшага — после расслабления суставов): итоговые скорости тел на физике, упоры в потолки,
    /// энергия сочленений на физике (рост за подшаг — инвариант <c>physics.energy</c>).
    /// </summary>
    partial void TraceSubstep(float h);

    /// <summary>Трасса (из шага 7г, в конце шага): итог тика — окно и прогон, события быстрее порога.</summary>
    partial void TraceStep(float dt);

    /// <summary>
    /// Трасса (7з-2; из шага после каждой фазы подшага): энергия сочленений на физике после фазы <paramref name="phase"/> — рост энергии
    /// по фазам (после позиционных фаз — по скоростям <c>сдвиг / h</c>, после скоростных — по настоящим).
    /// </summary>
    partial void TracePhase(PhysicsPhase phase, float h);
}
