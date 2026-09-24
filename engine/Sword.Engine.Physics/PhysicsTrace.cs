using System.Numerics;

namespace Sword.Engine.Physics;

// 7д: трасса физики — сторона мира: включение, крючки Trace* (объявлены в PhysicsWorld.cs, поле _trace — там же) и начало
// шага для трассы. Данные и сводки — класс PhysicsTrace (PhysicsTrace.Data.cs, PhysicsTrace.Stats.cs). Идеи — RagTrace SWORD
// 2eab13b (свой путь — README «7д»).
public sealed partial class PhysicsWorld
{
    public PhysicsTrace EnableTrace(PhysicsTrace? trace = null)
    {
        trace ??= new PhysicsTrace();
        trace.Grow(_artCapacity, _bodyCapacity);
        _trace = trace;
        return trace;
    }

    public void DisableTrace() => _trace = null;

    /// <summary>
    /// Начало шага для трассы (зовёт <see cref="Step"/> под <c>if (_trace is not null)</c> — после позы начала тика, до
    /// подшага 0): энергия сочленений на физике до шага — от неё рост за подшаг 0. Изменения между шагами (удар, команда сцены,
    /// переход в труп) в рост не попадают (<c>TraceTests.Trace_EnergyGrowth_…</c>, <c>Trace_Step_MeasuresSubstepZero_…</c>).
    /// </summary>
    internal void TraceStepBegin()
    {
        var t = _trace;
        if (t is null) return;
        t.EnergyClear();
        for (int b = 0; b < _bodyCapacity; b++) TraceEnergyBody(t, b);
        for (int a = 0; a < _artCapacity; a++) t.EnergyBaseline(a);
    }

    partial void TraceBegin(int body)
    {
        if (_trace is not { } t) return;
        ref readonly var s = ref _bodies[body];
        t.Begin(body, new PhysicsTrace.Snap(s.Position, s.PrevPosition, s.Rotation));
    }

    partial void TraceEnd(int body, PhysicsSource source, float h)
    {
        if (_trace is not { } t) return;
        ref readonly var s = ref _bodies[body];
        t.Correction(body, source, h, in t.Snapshot(body), s.Position, s.PrevPosition, s.Rotation, s.Mass);
    }

    partial void TraceContact(PhysicsSource source, int body, int other, Vector3 point, Vector3 normal, float depth, float solved)
    {
        if (_trace is not { } t) return;
        t.RecordContact(source, TraceBody(body), TraceBody(other), point, normal, depth, solved);
        // 7з-2: пара тел на физике — оба сочленения в этом подшаге «в контакте с трупом» (энергия передаётся — рост сочленения не накачка)
        if (source == PhysicsSource.Pair) t.Paired(_bodyOwner[body], _bodyOwner[other]);
    }

    partial void TraceFriction(int body, float lambdaN, float lambdaT, bool held) => _trace?.Friction(held);

    partial void TraceVelocity(PhysicsSource source, int body, float normalSpeed) => _trace?.VelocitySolved(source, normalSpeed);

    partial void TraceSubstep(float h)
    {
        if (_trace is not { } t) return;
        t.EnergyClear();
        for (int b = 0; b < _bodyCapacity; b++) TraceSubstepBody(t, b);
        for (int a = 0; a < _artCapacity; a++) TraceSubstepArticulation(t, a);
        t.SubstepEnd();
    }

    /// <summary>Тело на физике в конце подшага: скорости, упор в потолки профиля, энергия — в сумму его сочленения.</summary>
    private void TraceSubstepBody(PhysicsTrace t, int b)
    {
        if (_bodyGeneration[b] <= 0) return;
        ref readonly var s = ref _bodies[b];
        if (s.Mode != BodyMode.Simulated) return;
        ref readonly var d = ref _dynamics[s.Profile];
        float v = s.Velocity.Length(), w = s.AngularVelocity.Length();
        bool capped = v >= d.MaxSpeed * PhysicsTrace.CappedShare || w >= d.MaxAngularSpeed * PhysicsTrace.CappedShare;
        t.Speed(b, TraceBody(b), v, w, capped);
        t.EnergyAdd(_bodyOwner[b], s.KineticEnergy + (double)s.Mass * d.Gravity * s.Position.Y);
    }

    private void TraceSubstepArticulation(PhysicsTrace t, int a)
    {
        int gen = _artGeneration[a];
        if (gen > 0) t.EnergyCompare(a, new ArticulationHandle(a, gen));
    }

    private void TraceEnergyBody(PhysicsTrace t, int b)
    {
        if (_bodyGeneration[b] <= 0) return;
        ref readonly var s = ref _bodies[b];
        if (s.Mode != BodyMode.Simulated) return;
        t.EnergyAdd(_bodyOwner[b], s.KineticEnergy + (double)s.Mass * _dynamics[s.Profile].Gravity * s.Position.Y);
    }

    partial void TracePhase(PhysicsPhase phase, float h)
    {
        if (_trace is not { } t) return;
        t.PhaseClear();
        bool moved = phase < PhysicsPhase.Velocity;
        for (int b = 0; b < _bodyCapacity; b++) TracePhaseBody(t, b, moved, h);
        for (int a = 0; a < _artCapacity; a++) t.PhaseEnd(a, phase);
    }

    /// <summary>
    /// Тело на физике после фазы: энергия (кинетическая + m·g·y) — в сумму его сочленения. После позиционных фаз (<paramref name="moved"/>)
    /// скорости — те, что дал бы сдвиг за подшаг (<see cref="Dynamics.UpdateVelocity"/> без демпфирования): поправка положения, которая
    /// станет скоростью, видна в той фазе, что её сделала.
    /// </summary>
    private void TracePhaseBody(PhysicsTrace t, int b, bool moved, float h)
    {
        if (_bodyGeneration[b] <= 0) return;
        ref readonly var s = ref _bodies[b];
        if (s.Mode != BodyMode.Simulated) return;
        float kinetic = s.KineticEnergy;
        if (moved)
        {
            var copy = s;
            copy.Velocity = (s.Position - s.PrevPosition) / h;
            var dq = s.Rotation * Quaternion.Conjugate(s.PrevRotation);
            if (dq.W < 0) dq = Quaternion.Negate(dq);
            copy.AngularVelocity = new Vector3(dq.X, dq.Y, dq.Z) * (2f / h);
            kinetic = copy.KineticEnergy;
        }
        t.PhaseAdd(_bodyOwner[b], kinetic + (double)s.Mass * _dynamics[s.Profile].Gravity * s.Position.Y);
    }

    partial void TraceStep(float dt)
    {
        if (_trace is not { } t) return;
        for (int k = 0; k < t.TouchedCount; k++) TraceStepBody(t, t.TouchedAt(k));
        t.StepEnd(dt);
    }

    /// <summary>Тело, которого касалась трасса в этом тике: быстрее порога — событие.</summary>
    private void TraceStepBody(PhysicsTrace t, int b)
    {
        if (_bodyGeneration[b] <= 0 || !(t.BodySpeed(b) > t.EventSpeed)) return;
        int a = _bodyOwner[b];
        t.AddEvent(b, TraceBody(b), new ArticulationHandle(a, _artGeneration[a]), _bodies[b].Mass);
    }

    private BodyHandle TraceBody(int b) => b < 0 ? BodyHandle.None : new BodyHandle(b, _bodyGeneration[b]);
}
