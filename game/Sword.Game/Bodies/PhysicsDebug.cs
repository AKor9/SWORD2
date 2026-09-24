using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Диагностика физики для наблюдателей (7и): вид <see cref="IPhysicsDebugView"/> (суставы сочленений и трасса физики) и
/// диагностический режим трассы <see cref="PhysicsTraceMode"/>. Ни то ни другое не меняет ни мир, ни хеш, ни реплей: систем,
/// команд и ресурсов-значений нет — регистрация в <c>GameCore.Configure</c> одной строкой.
/// </summary>
public static class PhysicsDebug
{
    public static SimulationBuilder Register(SimulationBuilder b, IPhysicsWorld physics) => b
        .ReadView<IPhysicsDebugView>(new PhysicsDebugView(physics))
        .Diagnostic(new PhysicsTraceMode(physics));

    /// <summary>Хэндл сочленения — для наблюдателей (<see cref="BodyRef"/>; пустой — мир).</summary>
    public static BodyRef Ref(ArticulationHandle a) => new() { Index = a.Index, Generation = a.Generation };
}

/// <summary>
/// Диагностический режим <c>physics.trace</c> (7и): трасса физики мира (<see cref="IPhysicsWorld.EnableTrace"/>, 7д) — включить и
/// выключить между тиками, начать окно заново. Трасса одна на мир: режим — её выключатель (выключает и трассу, включённую иначе).
/// Объект трассы один на режим: повторное включение не выделяет памяти (пока не выросли слоты мира) и начинает накопление заново —
/// «прогон» трассы — с включения.
/// </summary>
public sealed class PhysicsTraceMode(IPhysicsWorld physics) : IDiagnosticMode
{
    private PhysicsTrace? _trace;

    public string Id => IPhysicsDebugView.TraceMode;

    public string Title => "трасса физики: контакты последнего подшага, поправки по источникам, скорости тел на физике, события";

    public bool Enabled => physics.Trace is not null;

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (physics.Trace is not null) physics.DisableTrace();
            return;
        }
        if (physics.Trace is not null) return;
        _trace ??= new PhysicsTrace();
        _trace.Clear();
        physics.EnableTrace(_trace);
    }

    public void ResetWindow() => physics.Trace?.ClearWindow();
}

/// <summary>
/// Вид <see cref="IPhysicsDebugView"/> — чтение подсистемы Physics (7и): суставы (<see cref="IPhysicsWorld.Joint"/>,
/// <see cref="IPhysicsWorld.Binding"/>, <see cref="IPhysicsWorld.MeasureJoint"/>) и трасса (<see cref="IPhysicsWorld.Trace"/>):
/// хэндлы тел — в сочленение (<see cref="BodyRef"/>) и номер тела в нём. Контакты переписываются в свой буфер (растёт удвоением —
/// установившийся кадр без выделений).
/// </summary>
public sealed class PhysicsDebugView(IPhysicsWorld physics) : IPhysicsDebugView
{
    private PhysicsContactView[] _contacts = [];

    public int JointCount(BodyRef body)
    {
        var a = FighterBodies.Handle(body);
        return physics.IsAlive(a) ? physics.JointCount(a) : 0;
    }

    public PhysicsJointView Joint(BodyRef body, int joint)
    {
        var a = FighterBodies.Handle(body);
        var d = physics.Joint(a, joint);
        var m = physics.MeasureJoint(a, joint);
        return new PhysicsJointView(d.Parent, d.Child, d.Kind == JointKind.Hinge, physics.IsJointEnabled(a, joint), d.ChildAnchor,
            physics.Binding(a, joint).ParentAnchor, m.PointError, m.Bend, m.Swing, m.Twist, m.Excess);
    }

    public bool TraceOn => physics.Trace is not null;

    public float TraceEventSpeed => physics.Trace?.EventSpeed ?? 0;

    public ReadOnlySpan<PhysicsContactView> Contacts
    {
        get
        {
            if (physics.Trace is not { } trace) return default;
            var last = trace.LastContacts;
            if (_contacts.Length < last.Length) Array.Resize(ref _contacts, Math.Max(last.Length, _contacts.Length * 2));
            for (int i = 0; i < last.Length; i++) _contacts[i] = Contact(last[i]);
            return _contacts.AsSpan(0, last.Length);
        }
    }

    private PhysicsContactView Contact(in PhysicsTrace.Contact c)
    {
        var (body, index) = Where(c.Body);
        var (other, otherIndex) = Where(c.Other);
        return new PhysicsContactView((PhysicsContactSource)c.Source, body, index, other, otherIndex, c.Point, c.Normal, c.Depth, c.Solved);
    }

    public int LostContacts => physics.Trace?.LostContacts ?? 0;

    public PhysicsTraceSummary TraceSummary(PhysicsTracePeriod period)
    {
        if (Stats(period) is not { } s) return default;
        var (speedBody, speedIndex) = Where(s.SpeedBody);
        return new PhysicsTraceSummary(s.Steps, s.Substeps, s.Seconds, s.Events,
            s.SpeedMax, speedBody, speedIndex, s.SpeedPercentile(0.99f), s.SpeedPercentile(0.999f), s.AngularSpeedMax, s.AngularPercentile(0.99f),
            s.Capped, s.SpeedSamples, s.NonFinite,
            s.PairShare, s.PairSubsteps, s.PassShare, s.PushPopSpeed,
            s.ReactionMax, PhysicsDebug.Ref(s.ReactionArticulation), s.ReactionSum,
            s.Hits, s.HitsCapped, s.HitsBudgeted, s.HitDvShare, s.HitDv, s.HitDw,
            s.FrictionContacts, s.FrictionHeld,
            s.EnergyGrowth, PhysicsDebug.Ref(s.EnergyArticulation), s.EnergyGrowthTotal, s.EnergyGrowthPushed);
    }

    public PhysicsTraceEnergy TraceEnergy(PhysicsTracePeriod period)
    {
        if (Stats(period) is not { } s) return default;
        return new PhysicsTraceEnergy(s.EnergyGrowthPaired, PhysicsDebug.Ref(s.EnergyPairedArticulation), s.EnergyGrowthPairedTotal,
            s.PhaseGrowth(PhysicsPhase.Integrate), s.PhaseGrowth(PhysicsPhase.Joints), s.PhaseGrowth(PhysicsPhase.World), s.PhaseGrowth(PhysicsPhase.Pairs),
            s.PhaseGrowth(PhysicsPhase.Pressed), s.PhaseGrowth(PhysicsPhase.Velocity), s.PhaseGrowth(PhysicsPhase.ContactVelocity),
            s.PhaseGrowth(PhysicsPhase.Relax));
    }

    public PhysicsTraceSleep TraceSleep(PhysicsTracePeriod period)
    {
        if (Stats(period) is not { } s) return default;
        return new PhysicsTraceSleep(s.Sleeps, s.AsleepMax, s.SleepContacts, s.Wakes(WakeReason.Push), s.Wakes(WakeReason.Hit),
            s.Wakes(WakeReason.Touch), s.Wakes(WakeReason.Chain), s.Wakes(WakeReason.Command), s.Wakes(WakeReason.Settings));
    }

    public PhysicsTraceSource TraceSource(PhysicsTracePeriod period, PhysicsContactSource source)
    {
        if (Stats(period) is not { } s || (int)source >= PhysicsTrace.SourceCount) return default;
        var x = s.Source((PhysicsSource)source);
        return new PhysicsTraceSource(x.Count, x.Linear, x.Angular, x.Hidden, x.ImpulseTickMax, x.ImpulseSum, x.Contacts, x.Depth, x.Deferred,
            x.VelocitySolved, x.Applied);
    }

    public int TraceTopCount(PhysicsTracePeriod period) => Stats(period)?.TopCount ?? 0;

    public PhysicsTraceEvent TraceTop(PhysicsTracePeriod period, int k) =>
        Stats(period) is { } s ? Event(s.Top(k)) : throw new ArgumentOutOfRangeException(nameof(k), k, "трасса физики выключена");

    public int RecentEventCount => physics.Trace?.RecentEventCount ?? 0;

    public PhysicsTraceEvent RecentEvent(int k) =>
        physics.Trace is { } t ? Event(t.RecentEvent(k)) : throw new ArgumentOutOfRangeException(nameof(k), k, "трасса физики выключена");

    private PhysicsTrace.Stats? Stats(PhysicsTracePeriod period) => physics.Trace is not { } t ? null : period switch
    {
        PhysicsTracePeriod.Last => t.Last,
        PhysicsTracePeriod.Window => t.Window,
        _ => t.Run,
    };

    /// <summary>Событие: главная поправка — источник с наибольшим сдвигом / h (при равенстве — первый).</summary>
    private PhysicsTraceEvent Event(in PhysicsTrace.Event e)
    {
        int main = 0;
        for (int i = 1; i < PhysicsTrace.SourceCount; i++)
            if (e.Linear((PhysicsSource)i) > e.Linear((PhysicsSource)main)) main = i;
        var (body, index) = Where(e.Body);
        if (body.Generation == 0) body = PhysicsDebug.Ref(e.Articulation);   // тела уже нет — сочленение из события
        return new PhysicsTraceEvent(e.Step, body, index, e.Mass, e.Speed, e.AngularSpeed, e.HitDv, (PhysicsContactSource)main,
            e.Linear((PhysicsSource)main), e.Angular((PhysicsSource)main));
    }

    /// <summary>Тело — сочленение и номер тела в нём; пустой или устаревший хэндл — пустое сочленение и −1.</summary>
    private (BodyRef Articulation, int Index) Where(BodyHandle body)
    {
        if (body.IsNone || !physics.IsAlive(body)) return (default, -1);
        var a = physics.ArticulationOf(body);
        int n = physics.BodyCount(a);
        for (int i = 0; i < n; i++)
            if (physics.Body(a, i) == body) return (PhysicsDebug.Ref(a), i);
        return (PhysicsDebug.Ref(a), -1);
    }
}
