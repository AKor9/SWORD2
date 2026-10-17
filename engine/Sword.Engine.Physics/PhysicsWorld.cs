using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Эталонная реализация <see cref="IPhysicsWorld"/>: тела — массив состояний по слотам (AoS); остаётся оракулом в тестах
/// для оптимизированной. Слоты — как у пула поз: удалённое сочленение возвращает тела с конца, и следующее того же размера
/// получает те же слоты; создание и удаление прежних размеров памяти не выделяет. Части класса — README «Раскладка файлов».
/// </summary>
public sealed partial class PhysicsWorld : IPhysicsWorld
{
    private BodyState[] _bodies = [];
    private int[] _bodyGeneration = [], _bodyOwner = [], _freeBodies = [];
    private int _freeBodyCount, _bodyCapacity;

    private int[] _artGeneration = [], _artCount = [], _freeArts = [];
    private int[]?[] _artBodies = [];
    private int _freeArtCount, _artCapacity;

    /// <summary>Трасса физики; null — выключена. Поле здесь — шаг и суставы проверяют его до сборки аргументов крючков.</summary>
    private PhysicsTrace? _trace = null;

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
            GroundProbesReset(b);
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
        for (int k = 0, n = _artCount[a]; k < n; k++) SleepWakeAround(members[k]);   // лежавшие на нём спящие теряют опору
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

    public int AddBody(ArticulationHandle articulation)
    {
        Check(articulation);
        int a = articulation.Index, k = _artCount[a];
        var members = _artBodies[a]!;
        if (members.Length <= k) Array.Resize(ref _artBodies[a], Math.Max(k + 1, members.Length * 2));
        if (_freeBodyCount == 0) GrowBodies(_bodyCapacity * 2);
        int b = _freeBodies[--_freeBodyCount];
        _bodyGeneration[b] = -_bodyGeneration[b] + 1;
        _bodyOwner[b] = a;
        _bodies[b] = BodyState.New(BodyMode.Kinematic);
        GroundProbesReset(b);
        _artBodies[a]![k] = b;
        _artCount[a] = k + 1;
        JointsReserve(a, k);
        SelfReserve(a);
        _trace?.ResetArticulation(a, _artBodies[a].AsSpan(0, k + 1));
        SleepWake(a, WakeReason.Command);
        return k;
    }

    public void RemoveBodies(ArticulationHandle articulation, int keep)
    {
        Check(articulation);
        int a = articulation.Index, n = _artCount[a];
        if (keep < 1 || keep > n) throw new ArgumentOutOfRangeException(nameof(keep), keep, $"в сочленении {articulation} тел {n}");
        if (keep == n) return;
        var members = _artBodies[a]!;
        for (int k = keep; k < n; k++) SleepWakeAround(members[k]);
        JointsDropBodies(a, keep);
        SelfDropBodies(a, keep);
        for (int k = n - 1; k >= keep; k--)
        {
            int b = members[k];
            _bodyGeneration[b] = -_bodyGeneration[b];
            _bodyOwner[b] = -1;
            _bodies[b] = default;
            _freeBodies[_freeBodyCount++] = b;
        }
        _artCount[a] = keep;
        _trace?.ResetArticulation(a, members.AsSpan(0, keep));
        SleepWake(a, WakeReason.Command);
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
        SleepWakeBody(body.Index, WakeReason.Command);   // запись тела будит
        ref var s = ref _bodies[body.Index];
        if (s.Position != position || s.Rotation != rotation) SleepWakeAround(body.Index);   // лежавшие на нём спящие — по позе до записи
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
        // Поза начала тика кинематического тела — прошлая постановка.
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

    // ——— Связь частей класса ———
    // Крючки — partial-методы: объявлены здесь, реализованы частями; без реализации вызов исчезает вместе с аргументами.
    // Хэндлы не проверяют (номера — слоты живых, их проверил вызывающий). Внутри шага — без выделения памяти.

    /// <summary>Слоты выросли: массивы суставов дорастить. Только рост; зовётся и из конструктора.</summary>
    partial void JointsGrow(int articulations, int bodies);

    /// <summary>Сочленение в слоте создано (после выдачи тел) или удаляется (до возврата тел): суставов нет, журнал пуст.</summary>
    partial void JointsReset(int articulation);

    /// <summary>Слоты выросли: границы сочленений и списки контактов подшага — дорастить.</summary>
    partial void ContactsGrow(int articulations, int bodies);

    /// <summary>Сочленение в слоте создано или удаляется: границы — пустые.</summary>
    partial void ContactsReset(int articulation);

    /// <summary>Слоты выросли: проходы и бюджеты пар, отдача, снимки трассы — дорастить.</summary>
    partial void KinematicGrow(int articulations, int bodies);

    /// <summary>Сочленение в слоте создано или удаляется: проходы всех пар с ним и его отдача — в начальное состояние (новое сочленение чужого прохода не наследует).</summary>
    partial void KinematicReset(int articulation);

    /// <summary>
    /// Подшаг (после интеграции): точки и пределы суставов по порядку, срабатывания — в журнал (подшаг 0 очищает журнал);
    /// кинематическое тело — неподвижная сторона.
    /// </summary>
    partial void SolveJoints(float h, int substep);

    /// <summary>Подшаг <paramref name="substep"/> (из шага, после скоростного прохода контактов): расслабление скоростей суставов.</summary>
    partial void RelaxJoints(float h, int substep);

    /// <summary>Подшаг (из шага, до контактов): бюджеты пар толчков — на подшаг, <c>PushForce · h²</c>.</summary>
    partial void BeginKinematicSubstep(float h);

    /// <summary>
    /// Толчок: контакт кинематического тела с телом на физике; нормаль — от кинематического, <paramref name="depth"/> — полная
    /// глубина, м. Предел λ — бюджет пары, прохода и скорость толкающей точки; отдача толкающему. Трассу пишет сам.
    /// </summary>
    partial void PushKinematic(int kinematic, int body, Vector3 kinematicPoint, Vector3 point, Vector3 normal, float depth, float h);

    /// <summary>Подшаг (из шага, после контактов): итоги бюджетов пар подшага — трассе.</summary>
    partial void EndKinematicSubstep();

    /// <summary>Трасса: снимок тела перед этапом подшага; пара — два снимка, затем два <see cref="TraceEnd"/>.</summary>
    partial void TraceBegin(int body);

    /// <summary>Трасса: поправка тела этапом <paramref name="source"/> с последнего <see cref="TraceBegin"/> — сдвиг и угол за h.</summary>
    partial void TraceEnd(int body, PhysicsSource source, float h);

    /// <summary>Трасса: контакт подшага — тело, второе тело (−1 — мир), точка и нормаль (мир), глубина настоящая и решённая, м.</summary>
    partial void TraceContact(PhysicsSource source, int body, int other, Vector3 point, Vector3 normal, float depth, float solved);

    /// <summary>Трасса: трение торца тела <paramref name="body"/> о землю — нормальная и касательная λ, держало ли.</summary>
    partial void TraceFriction(int body, float lambdaN, float lambdaT, bool held);

    /// <summary>Трасса: скоростной проход погасил нормальную скорость контакта тела <paramref name="body"/>, м/с.</summary>
    partial void TraceVelocity(PhysicsSource source, int body, float normalSpeed);

    /// <summary>Трасса (конец подшага): итоговые скорости тел на физике, упоры в потолки, рост энергии.</summary>
    partial void TraceSubstep(float h);

    /// <summary>Трасса (из шага, в конце шага): итог тика — окно и прогон, события быстрее порога.</summary>
    partial void TraceStep(float dt);

    /// <summary>Трасса (после каждой фазы подшага): энергия сочленений на физике после фазы <paramref name="phase"/>.</summary>
    partial void TracePhase(PhysicsPhase phase, float h);
}
