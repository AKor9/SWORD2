using Sword.Engine.Diagnostics;

namespace Sword.Engine.Physics;

// Шаг физики — порядок IPhysicsWorld.Step (идея — подшаг DuelArena.Tick SWORD 2eab13b, research-arena-tick §1.2). Суставы,
// толчки и трасса — через крючки PhysicsWorld.cs; контакты и границы — PhysicsWorld.Contacts.cs, Ground.cs.
// Зоны профайлера по фазам подшага, мир (земля, стена, колонны) — после пар, нечисловые тела на физике — флаг сочленения.
public sealed partial class PhysicsWorld
{
    private CpuProfiler _profiler = CpuProfiler.Off;
    private int _zoneIntegrate, _zoneJoints, _zoneBounds, _zonePairs, _zoneGround, _zoneVelocity, _zoneContactVelocity, _zoneRelax, _zoneSleep, _zoneSelf;

    // Нечисловые тела на физике — флаг по слотам сочленений (пересчёт — в конце шага и по записям тел, лениво), их число.
    private bool[] _stepNonFinite = [], _stepNonFiniteDirty = [];
    private int[] _stepDirtyList = [];
    private int _stepNonFiniteCount, _stepDirtyCount;

    /// <summary>
    /// Обратные инерции тел на физике в мире — после обновления скоростей подшага: поза до конца подшага не меняется, скоростной
    /// проход и расслабление берут матрицу тела, а не два поворота на каждый вызов. По слотам тел; у кинематических — не пишется.
    /// </summary>
    private InvInertiaMatrix[] _stepInertia = [];

    public void AttachProfiler(CpuProfiler profiler)
    {
        ArgumentNullException.ThrowIfNull(profiler);
        _zoneIntegrate = profiler.Zone("physics.integrate");
        _zoneJoints = profiler.Zone("physics.joints");
        _zoneBounds = profiler.Zone("physics.bounds");
        _zonePairs = profiler.Zone("physics.pairs");
        _zoneGround = profiler.Zone("physics.ground");
        _zoneVelocity = profiler.Zone("physics.velocity");
        _zoneContactVelocity = profiler.Zone("physics.contactVelocity");
        _zoneRelax = profiler.Zone("physics.relax");
        _zoneSleep = profiler.Zone("physics.sleep");
        _zoneSelf = profiler.Zone("physics.self");   // самоколлизия — внутри physics.pairs
        _profiler = profiler;
    }

    public int NonFiniteArticulations
    {
        get
        {
            StepFlushNonFinite();
            return _stepNonFiniteCount;
        }
    }

    public bool HasNonFinite(ArticulationHandle articulation)
    {
        Check(articulation);
        StepFlushNonFinite();
        return _stepNonFinite[articulation.Index];
    }

    public void Step(float dt)
    {
        if (!float.IsFinite(dt) || !(dt > 0)) throw new ArgumentOutOfRangeException(nameof(dt), dt, "шаг — конечный и больше 0");
        var settings = _settings;
        int substeps = settings.Substeps;
        float h = dt / substeps;
        _contactsPopSpeed = settings.PopSpeed;
        _contactsFriction = settings.StaticFriction;
        _contactsPairFriction = settings.PairFriction;
        _contactsCompliance = settings.ContactCompliance;
        _selfScale = settings.SelfContactScale;
        _contactsVelocityCount = 0;
        ContactsPairChecks = 0;
        ContactsBodyChecks = 0;
        ContactsNarrowChecks = 0;
        ContactsPairFrictionChecks = 0;
        ContactsPairFrictionHeld = 0;
        ContactsPressedChecks = 0;
        SleepContacts = 0;
        SelfNarrowChecks = 0;
        SelfTouches = 0;

        _stepRunning = true;
        // Спящих, которых может сдвинуть неспящее (толчок, касание), и касающихся проснувшихся (цепочка) — будить до подшагов.
        _profiler.Begin(_zoneSleep);
        SleepWakeBegin(dt);
        _profiler.End(_zoneSleep);
        StepBegin();
        // База энергии подшага 0 — поза начала тика: удары, команды сцен и переход в труп между шагами в рост не попадают.
        if (_trace is not null) TraceStepBegin();
        for (int substep = 0; substep < substeps; substep++) StepSubstep(h, substep);
        _profiler.Begin(_zoneBounds);
        StepFinish();
        _profiler.End(_zoneBounds);
        _profiler.Begin(_zoneSleep);
        SleepUpdate(dt);
        _profiler.End(_zoneSleep);
        _stepRunning = false;
        if (_trace is not null) TraceStep(dt);
    }

    /// <summary>
    /// Подшаг: интеграция тел на физике → суставы → бюджеты толчков → мир (земля, стена, колонны) → отсев сочленений → пары
    /// сочленений (тела на физике между собой и толчки живых; самоколлизия; трение покоя пар) → мир после пар для прижатых (тела,
    /// касавшиеся земли и поправленные парами) → итоги толчков → обновление скоростей по профилю тела → скоростной проход контактов →
    /// расслабление суставов (с трением суставов) → трасса. Мир после пар: пара (тяжёлое тело на лёгком, толчок живого) не оставляет тело в земле к концу подшага — иначе вдавленное
    /// стало бы «застарелой» глубиной, выходило не быстрее <see cref="PhysicsSettings.PopSpeed"/>, а пара вдавливала бы его снова (клинок под
    /// кучей тонул). Первый проход мира — до пар: опора на время пар уже поднята землёй (<see cref="ContactsPressedWorld"/>).
    /// </summary>
    private void StepSubstep(float h, int substep)
    {
        var p = _profiler;
        p.Begin(_zoneIntegrate);
        StepIntegrate(h);
        p.End(_zoneIntegrate);
        if (_trace is not null) TracePhase(PhysicsPhase.Integrate, h);
        p.Begin(_zoneJoints);
        SolveJoints(h, substep);
        p.End(_zoneJoints);
        if (_trace is not null) TracePhase(PhysicsPhase.Joints, h);
        BeginKinematicSubstep(h);
        p.Begin(_zoneGround);
        ContactsWorld(h);
        p.End(_zoneGround);
        if (_trace is not null) TracePhase(PhysicsPhase.World, h);
        p.Begin(_zoneBounds);
        ContactsBroadphase();
        p.End(_zoneBounds);
        p.Begin(_zonePairs);
        ContactsPairs(h);
        p.End(_zonePairs);
        if (_trace is not null) TracePhase(PhysicsPhase.Pairs, h);
        p.Begin(_zoneGround);
        ContactsPressedWorld(h);
        p.End(_zoneGround);
        if (_trace is not null) TracePhase(PhysicsPhase.Pressed, h);
        EndKinematicSubstep();
        p.Begin(_zoneVelocity);
        StepUpdateVelocity(h);
        p.End(_zoneVelocity);
        if (_trace is not null) TracePhase(PhysicsPhase.Velocity, h);
        p.Begin(_zoneContactVelocity);
        ContactsVelocityPass();
        p.End(_zoneContactVelocity);
        if (_trace is not null) TracePhase(PhysicsPhase.ContactVelocity, h);
        p.Begin(_zoneRelax);
        RelaxJoints(h, substep);
        p.End(_zoneRelax);
        if (_trace is not null)
        {
            TracePhase(PhysicsPhase.Relax, h);
            TraceSubstep(h);
        }
    }

    /// <summary>
    /// Начало шага: у тел на физике поза начала тика ← нынешняя (интерполяция клиента); тел на физике по сочленениям — отсев
    /// сочленений, которые шаг не двигает; границы сочленений без тел на физике — на весь шаг (их тела шаг не двигает). Трасса (если
    /// включена) берёт отсюда энергию до шага — <see cref="TraceStepBegin"/>. Спящее сочленение — без тел на физике для шага: его тела
    /// шаг не двигает, неспящим оно — неподвижная опора.
    /// </summary>
    private void StepBegin()
    {
        int live = 0;
        for (int a = 0; a < _artCapacity; a++)
        {
            int simulated = _artGeneration[a] > 0 && !_sleepAsleep[a] ? StepBeginArticulation(a) : 0;
            _contactsSimulated[a] = simulated;
            if (simulated > 0) live++;
        }
        _contactsSimulatedArticulations = live;
        if (live > 0) ContactsKinematicBounds();
    }

    private int StepBeginArticulation(int a)
    {
        var members = _artBodies[a]!;
        int simulated = 0;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            ref var s = ref _bodies[members[k]];
            if (s.Mode != BodyMode.Simulated) continue;
            s.StartPosition = s.Position;
            s.StartRotation = s.Rotation;
            simulated++;
        }
        return simulated;
    }

    /// <summary>
    /// Конец шага: границы всех живых сочленений (<see cref="Bounds"/>) и флаги нечисловых тел на физике: у сочленения с телами на
    /// физике — есть ли среди них нечисловое. Хватает положения, поворота и обеих скоростей: к концу шага нечисловое в прошлой позе или позе
    /// начала тика тянет за собой нечисловое положение (интеграция и поправки его не лечат). Спящие — пропуск: поза та же, тела конечны.
    /// </summary>
    private void StepFinish()
    {
        StepFlushNonFinite();
        for (int a = 0; a < _artCapacity; a++)
        {
            if (_artGeneration[a] <= 0 || _sleepAsleep[a]) continue;
            _contactsBounds[a] = ContactsBoundsOf(a);
            bool bad = _contactsSimulated[a] > 0 && !StepFiniteSimulated(a);
            if (bad != _stepNonFinite[a]) StepSetNonFinite(a, bad);
        }
    }

    /// <summary>Тела на физике сочленения конечны: положение, поворот, скорости (сумма — одна проверка на тело).</summary>
    private bool StepFiniteSimulated(int a)
    {
        var members = _artBodies[a]!;
        float sum = 0;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            ref readonly var s = ref _bodies[members[k]];
            if (s.Mode != BodyMode.Simulated) continue;
            var v = s.Position + s.Velocity + s.AngularVelocity;
            sum += v.X + v.Y + v.Z + s.Rotation.X + s.Rotation.Y + s.Rotation.Z + s.Rotation.W;
        }
        return float.IsFinite(sum);
    }

    /// <summary>Тело записали вне шага (<see cref="Place"/>, <see cref="Set"/>, <see cref="SetMode"/>, удар): флаг его сочленения — пересчитать при чтении.</summary>
    private void StepMarkNonFinite(int body)
    {
        int a = _bodyOwner[body];
        if (a < 0 || _stepNonFiniteDirty[a]) return;
        _stepNonFiniteDirty[a] = true;
        _stepDirtyList[_stepDirtyCount++] = a;
    }

    /// <summary>Пересчитать флаги записанных вне шага сочленений — все поля тел на физике (<see cref="BodyState.IsFinite"/>).</summary>
    private void StepFlushNonFinite()
    {
        for (int i = 0; i < _stepDirtyCount; i++)
        {
            int a = _stepDirtyList[i];
            _stepNonFiniteDirty[a] = false;
            bool bad = _artGeneration[a] > 0 && StepAnyNonFinite(a);
            if (bad != _stepNonFinite[a]) StepSetNonFinite(a, bad);
        }
        _stepDirtyCount = 0;
    }

    private bool StepAnyNonFinite(int a)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            ref readonly var s = ref _bodies[members[k]];
            if (s.Mode == BodyMode.Simulated && !s.IsFinite) return true;
        }
        return false;
    }

    private void StepSetNonFinite(int a, bool bad)
    {
        _stepNonFinite[a] = bad;
        _stepNonFiniteCount += bad ? 1 : -1;
    }

    /// <summary>
    /// Слоты выросли: флаги нечисловых и список записанных — по слотам сочленений, обратные инерции подшага — по слотам тел (зовёт
    /// <see cref="ContactsGrow"/>).
    /// </summary>
    private void StepGrow(int articulations, int bodies)
    {
        if (_stepInertia.Length < bodies) Array.Resize(ref _stepInertia, bodies);
        if (_stepNonFinite.Length >= articulations) return;
        Array.Resize(ref _stepNonFinite, articulations);
        Array.Resize(ref _stepNonFiniteDirty, articulations);
        Array.Resize(ref _stepDirtyList, articulations);
    }

    /// <summary>Сочленение в слоте создано или удаляется: флаг нечисловых снят (зовёт <see cref="ContactsReset"/>).</summary>
    private void StepReset(int a)
    {
        if (_stepNonFinite[a]) StepSetNonFinite(a, false);
    }

    /// <summary>Интеграция подшага (<see cref="Dynamics.Integrate"/>) тел на физике по профилю тела.</summary>
    private void StepIntegrate(float h)
    {
        if (_contactsSimulatedArticulations == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_contactsSimulated[a] > 0) StepIntegrateArticulation(a, h);
    }

    private void StepIntegrateArticulation(int a, float h)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            ref var s = ref _bodies[members[k]];
            if (s.Mode == BodyMode.Simulated) Dynamics.Integrate(ref s, in _dynamics[s.Profile], h);
        }
    }

    /// <summary>
    /// Обновление скоростей подшага (<see cref="Dynamics.UpdateVelocity"/>) тел на физике по профилю тела; поза подшага дальше не меняется —
    /// обратная инерция в мире для скоростного прохода и расслабления (<see cref="_stepInertia"/>).
    /// </summary>
    private void StepUpdateVelocity(float h)
    {
        if (_contactsSimulatedArticulations == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_contactsSimulated[a] > 0) StepUpdateVelocityArticulation(a, h);
    }

    private void StepUpdateVelocityArticulation(int a, float h)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            int b = members[k];
            ref var s = ref _bodies[b];
            if (s.Mode != BodyMode.Simulated) continue;
            Dynamics.UpdateVelocity(ref s, in _dynamics[s.Profile], h);
            _stepInertia[b] = new InvInertiaMatrix(in s);
        }
    }
}
