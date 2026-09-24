namespace Sword.Engine.Physics;

// 7г: шаг физики — порядок IPhysicsWorld.Step (идея — подшаг DuelArena.Tick SWORD 2eab13b, research-arena-tick §1.2). Суставы
// (7в), толчки и трасса (7д) — через крючки PhysicsWorld.cs; контакты и границы — PhysicsWorld.Contacts.cs, Ground.cs.
public sealed partial class PhysicsWorld
{
    public void Step(float dt)
    {
        if (!float.IsFinite(dt) || !(dt > 0)) throw new ArgumentOutOfRangeException(nameof(dt), dt, "шаг — конечный и больше 0");
        var settings = _settings;
        int substeps = settings.Substeps;
        float h = dt / substeps;
        _contactsPopSpeed = settings.PopSpeed;
        _contactsFriction = settings.StaticFriction;
        _contactsCompliance = settings.ContactCompliance;
        _contactsVelocityCount = 0;
        ContactsPairChecks = 0;
        ContactsNarrowChecks = 0;

        StepBegin();
        for (int substep = 0; substep < substeps; substep++) StepSubstep(h, substep);
        ContactsUpdateBounds();
        if (_trace is not null) TraceStep(dt);
    }

    /// <summary>
    /// Подшаг: интеграция тел на физике → суставы (7в) → бюджеты толчков (7д) → контакты с миром → пары сочленений → итоги
    /// толчков (7д) → обновление скоростей по профилю тела → скоростной проход контактов → расслабление суставов (7в) → трасса.
    /// </summary>
    private void StepSubstep(float h, int substep)
    {
        StepIntegrate(h);
        SolveJoints(h, substep);
        BeginKinematicSubstep(h);
        ContactsWorld(h);
        ContactsPairs(h);
        EndKinematicSubstep();
        StepUpdateVelocity(h);
        ContactsVelocityPass();
        RelaxJoints(h, substep);
        if (_trace is not null) TraceSubstep(h);
    }

    /// <summary>
    /// Начало шага: у тел на физике поза начала тика ← нынешняя (интерполяция клиента); тел на физике по сочленениям — отсев
    /// сочленений, которые шаг не двигает.
    /// </summary>
    private void StepBegin()
    {
        int live = 0;
        for (int a = 0; a < _artCapacity; a++)
        {
            int simulated = _artGeneration[a] > 0 ? StepBeginArticulation(a) : 0;
            _contactsSimulated[a] = simulated;
            if (simulated > 0) live++;
        }
        _contactsSimulatedArticulations = live;
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

    /// <summary>Обновление скоростей подшага (<see cref="Dynamics.UpdateVelocity"/>) тел на физике по профилю тела.</summary>
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
            ref var s = ref _bodies[members[k]];
            if (s.Mode == BodyMode.Simulated) Dynamics.UpdateVelocity(ref s, in _dynamics[s.Profile], h);
        }
    }
}
