using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Engine.Physics;

// 7д: помощник тестов толчков и трассы до слияния 7г (шага ещё нет; README «Слияние, хеш и тесты до слияния»). Не шаг физики:
// без суставов, стены, колонн и пар тел на физике, земля — плоскость без ограничения выхода. Зовёт крючки 7д так, как их зовёт
// шаг 7г по контракту: начало шага трассы → [интеграция → BeginKinematicSubstep → (земля) → толчки пар → EndKinematicSubstep →
// скорости → TraceSubstep] × подшаги → TraceStep. Помощник — только для тестов: internal (игра не видит), в игре не зовётся.
// ВРЕМЕННЫЙ: при слиянии с 7г тесты 7д переводятся на Step и тик помощника (KinematicTestStep и его закрытые методы) удаляется
// (земля 7г выталкивает с пределом PopSpeed — ожидания TraceTests.Trace_GroundPushOut_… поменяются); подглядывания
// KinematicPairUsed/KinematicPairBudget при этом можно перенести в KinematicContacts.cs. Решает слияние.
public sealed partial class PhysicsWorld
{
    /// <summary>
    /// Тик для тестов 7д: <see cref="PhysicsSettings.Substeps"/> подшагов по <paramref name="dt"/>; тела на физике — интеграция
    /// по профилю и обновление скоростей; толчки — все пары «кинематическое тело ↔ тело на физике» разных сочленений (сочленения
    /// по слотам <c>i &lt; j</c>, тела — по порядку в сочленении), контакт капсул — ближайшие точки осей. <paramref name="floorY"/>
    /// — плоскость земли (торцы капсул, жёстко, трасса — <see cref="PhysicsSource.Ground"/>); NaN — без земли. 0 Б.
    /// </summary>
    internal void KinematicTestStep(float dt, float floorY = float.NaN)
    {
        int n = _settings.Substeps;
        float h = dt / n;
        for (int b = 0; b < _bodyCapacity; b++) KinematicTestStart(b);
        if (_trace is not null) TraceStepBegin();
        for (int k = 0; k < n; k++) KinematicTestSubstep(h, floorY);
        if (_trace is not null) TraceStep(dt);
    }

    /// <summary>Использовано бюджета пары в текущем подшаге толчков, кг·м (тесты).</summary>
    internal float KinematicPairUsed(ArticulationHandle pusher, ArticulationHandle target)
    {
        ref readonly var p = ref KinematicPairOf(pusher, target);
        return p.Stamp == _kinematicSubstep ? p.Used : 0;
    }

    /// <summary>Бюджет пары текущего подшага толчков, кг·м (тесты).</summary>
    internal float KinematicPairBudget => _kinematicPairBudget;

    private void KinematicTestStart(int b)
    {
        if (_bodyGeneration[b] <= 0) return;
        ref var s = ref _bodies[b];
        if (s.Mode != BodyMode.Simulated) return;
        s.StartPosition = s.Position;
        s.StartRotation = s.Rotation;
    }

    private void KinematicTestSubstep(float h, float floorY)
    {
        for (int b = 0; b < _bodyCapacity; b++) KinematicTestIntegrate(b, h);
        BeginKinematicSubstep(h);
        if (!float.IsNaN(floorY))
            for (int b = 0; b < _bodyCapacity; b++) KinematicTestFloor(b, floorY, h);
        for (int a = 0; a < _artCapacity; a++)
            for (int c = a + 1; c < _artCapacity; c++) KinematicTestPair(a, c, h);
        EndKinematicSubstep();
        for (int b = 0; b < _bodyCapacity; b++) KinematicTestVelocity(b, h);
        if (_trace is not null) TraceSubstep(h);
    }

    private void KinematicTestIntegrate(int b, float h)
    {
        if (_bodyGeneration[b] <= 0) return;
        ref var s = ref _bodies[b];
        if (s.Mode == BodyMode.Simulated) Dynamics.Integrate(ref s, in _dynamics[s.Profile], h);
    }

    private void KinematicTestVelocity(int b, float h)
    {
        if (_bodyGeneration[b] <= 0) return;
        ref var s = ref _bodies[b];
        if (s.Mode == BodyMode.Simulated) Dynamics.UpdateVelocity(ref s, in _dynamics[s.Profile], h);
    }

    private void KinematicTestFloor(int b, float floorY, float h)
    {
        if (_bodyGeneration[b] <= 0 || _bodies[b].Mode != BodyMode.Simulated) return;
        KinematicTestFloorEnd(b, -1, floorY, h);
        if (_bodies[b].HalfLength > 0) KinematicTestFloorEnd(b, 1, floorY, h);
    }

    private void KinematicTestFloorEnd(int b, int side, float floorY, float h)
    {
        ref var s = ref _bodies[b];
        var end = s.Position + s.Axis * (side * s.HalfLength);
        float depth = s.Radius - (end.Y - floorY);
        if (!(depth > 0)) return;
        var point = end - Vector3.UnitY * s.Radius;
        if (_trace is not null) TraceBegin(b);
        Xpbd.Contact(ref s, point, Vector3.UnitY, -depth, 0, h);
        s.Grounded = true;
        if (_trace is null) return;
        TraceEnd(b, PhysicsSource.Ground, h);
        TraceContact(PhysicsSource.Ground, b, -1, point, Vector3.UnitY, depth, depth);
    }

    private void KinematicTestPair(int a, int c, float h)
    {
        if (_artGeneration[a] <= 0 || _artGeneration[c] <= 0) return;
        var ba = _artBodies[a]!;
        var bc = _artBodies[c]!;
        for (int i = 0; i < _artCount[a]; i++)
            for (int j = 0; j < _artCount[c]; j++) KinematicTestContact(ba[i], bc[j], h);
    }

    private void KinematicTestContact(int x, int y, float h)
    {
        int kin, sim;
        if (_bodies[x].Mode == BodyMode.Kinematic && _bodies[y].Mode == BodyMode.Simulated) (kin, sim) = (x, y);
        else if (_bodies[y].Mode == BodyMode.Kinematic && _bodies[x].Mode == BodyMode.Simulated) (kin, sim) = (y, x);
        else return;
        ref readonly var k = ref _bodies[kin];
        ref readonly var s = ref _bodies[sim];
        Segments.SegSeg(k.EndA, k.EndB, s.EndA, s.EndB, out var ck, out var cs);
        var d = cs - ck;
        float dist = d.Length();
        float depth = k.Radius + s.Radius - dist;
        if (!(depth > 0) || dist < 1e-6f) return;
        var normal = d / dist;
        PushKinematic(kin, sim, ck + normal * k.Radius, cs - normal * s.Radius, normal, depth, h);
    }
}
