using System.Numerics;
using static Sword.Engine.Physics.Tests.KinematicScene;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Трасса физики (7д): выключена по умолчанию и не меняет симуляцию; выключенная и включённая — 0 Б в установившемся режиме;
/// выталкивание из земли — с глубиной и контактами последнего подшага; окно чистится отдельно от прогона; события быстрее
/// порога считаются и держатся в «самых быстрых»; толчки — источник, точный импульс, доли бюджетов, выталкивание; удары и
/// отдача — в последний тик; рост энергии за подшаг — отдельно у толкаемых живыми.
/// </summary>
public class TraceTests
{
    [Fact]
    public void Trace_OffByDefault_EnableAndDisableByOwner()
    {
        IPhysicsWorld w = new PhysicsWorld();
        Assert.Null(w.Trace);
        var t = w.EnableTrace();
        Assert.Same(t, w.Trace);
        var mine = new PhysicsTrace(eventSpeed: 3);
        Assert.Same(mine, w.EnableTrace(mine));
        Assert.Same(mine, w.Trace);
        w.DisableTrace();
        Assert.Null(w.Trace);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsTrace(eventSpeed: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mine.EventSpeed = float.NaN);
    }

    [Fact]
    public void Trace_DoesNotChangeSimulation()
    {
        var plain = World();
        var traced = World();
        traced.EnableTrace(new PhysicsTrace(eventSpeed: 0));
        var (p1, t1) = KinematicPassTests.Crowd(plain);
        var (p2, t2) = KinematicPassTests.Crowd(traced);
        for (int t = 0; t < 60; t++)
        {
            Assert.Equal(KinematicPassTests.CrowdTick(plain, p1, t1, t), KinematicPassTests.CrowdTick(traced, p2, t2, t));
            for (int i = 0; i < plain.BodyCapacity; i++)
                if (plain.BodyAt(i) is { IsNone: false } b) Assert.Equal(plain.Get(b), traced.Get(traced.BodyAt(i)));
        }
        Assert.True(traced.Trace!.Run.Steps == 60);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyState_WithTraceOffOrOn_DoesNotAllocate(bool on)
    {
        var w = World();
        // порог 0 — каждое тело на физике каждый тик даёт событие: проходят все ветки записи
        if (on) w.EnableTrace(new PhysicsTrace(eventSpeed: 0));
        var (pushers, targets) = KinematicPassTests.Crowd(w);
        float sink = 0;
        for (int t = 0; t < 100; t++) sink += KinematicPassTests.CrowdTick(w, pushers, targets, t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 100; t < 1100; t++) sink += KinematicPassTests.CrowdTick(w, pushers, targets, t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"трасса {(on ? "включена" : "выключена")}: 1000 тиков выделили {allocated} Б");
        Assert.True(float.IsFinite(sink));
        if (!on) return;
        var run = w.Trace!.Run;
        Assert.Equal(1100, run.Steps);
        Assert.Equal(1100, run.Hits);
        Assert.True(run.Events > 0, "события");
        Assert.True(run.Source(PhysicsSource.Ground).Contacts > 0, "земля");
        Assert.True(run.Source(PhysicsSource.PushBody).Applied > 0, "толчок тела");
        Assert.True(run.Source(PhysicsSource.PushPass).Applied > 0, "толчок прохода");
    }

    [Fact]
    public void Trace_GroundPushOut_RecordedWithDepth_ContactsOfLastSubstep()
    {
        var w = World();
        var trace = w.EnableTrace();
        // шар утоплен в землю на 3 см: выталкивание с глубиной (у помощника — сразу целиком, без предела выхода 7г)
        var sunk = w.Body(Target(w, 5, 0.1f, Vacuum, new Vector3(0, 0.07f, 0)), 0);
        w.KinematicTestStep(Dt, floorY: 0);
        var ground = trace.Last.Source(PhysicsSource.Ground);
        Assert.True(MathF.Abs(ground.Depth - 0.03f) < 1e-5f, $"глубина {ground.Depth}");
        Assert.True(ground.Contacts >= 1 && ground.Count >= 1, $"контактов {ground.Contacts}, поправок {ground.Count}");
        Near(0.03f / TestBodies.H, ground.Linear, 1e-3f, "сдвиг выталкивания за подшаг / h");
        Assert.Equal(0f, ground.Hidden);   // прошлую позу выталкивание не трогает
        Near(5 * 0.03f / TestBodies.H, ground.ImpulseTickMax, 1e-3f, "псевдоимпульс тика");
        Assert.Equal(0f, ground.Deferred);
        Assert.Equal((1, 4), (trace.Last.Steps, trace.Last.Substeps));
        Assert.True(w.Get(sunk).Velocity.Y > 10);

        // лежачая капсула под тяжестью касается земли обоими торцами в каждом подшаге: контактов тика 8, последних — 2
        var lying = w.CreateArticulation(1);
        var b = w.Body(lying, 0);
        w.SetShape(b, 5, 0.1f, 0.3f);
        w.Place(b, new Vector3(10, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        w.SetMode(b, BodyMode.Simulated);
        w.KinematicTestStep(Dt, floorY: 0);
        Assert.Equal(8, trace.Last.Source(PhysicsSource.Ground).Contacts);
        Assert.Equal((2, 0), (trace.LastContacts.Length, trace.LostContacts));
        foreach (var c in trace.LastContacts)
        {
            Assert.Equal((PhysicsSource.Ground, b, BodyHandle.None, Vector3.UnitY), (c.Source, c.Body, c.Other, c.Normal));
            Assert.True(c.Depth is > 0 and < 1e-3f, $"глубина лёжа {c.Depth}");
            Assert.True(MathF.Abs(c.Point.Y) < 1e-3f);
        }

        // вместимость контактов — фиксированная: лишние считаются
        var small = new PhysicsWorld();
        var st = small.EnableTrace(new PhysicsTrace(contactCapacity: 1));
        var sb = small.Body(small.CreateArticulation(1), 0);
        small.SetShape(sb, 5, 0.1f, 0.3f);
        small.Place(sb, new Vector3(0, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        small.SetMode(sb, BodyMode.Simulated);
        small.KinematicTestStep(Dt, floorY: 0);
        Assert.Equal((1, 1), (st.LastContacts.Length, st.LostContacts));
    }

    [Fact]
    public void Trace_WindowClearedSeparatelyFromRun()
    {
        var w = World();
        var trace = w.EnableTrace();
        var b = w.Body(Target(w, 2, 0.1f, 0, new Vector3(0, 0.05f, 0)), 0);
        for (int t = 0; t < 3; t++) w.KinematicTestStep(Dt, floorY: 0);
        Assert.Equal((3, 3), (trace.Window.Steps, trace.Run.Steps));
        int contacts = trace.Run.Source(PhysicsSource.Ground).Contacts;
        Assert.True(contacts > 0);
        Assert.Equal(contacts, trace.Window.Source(PhysicsSource.Ground).Contacts);

        trace.ClearWindow();
        Assert.Equal((0, 0, 0L), (trace.Window.Steps, trace.Window.Source(PhysicsSource.Ground).Contacts, trace.Window.SpeedSamples));
        Assert.Equal((3, contacts), (trace.Run.Steps, trace.Run.Source(PhysicsSource.Ground).Contacts));
        Assert.Equal(1, trace.Last.Steps);   // последний тик окно не трогает

        w.Place(b, new Vector3(0, 0.05f, 0), Quaternion.Identity);
        w.KinematicTestStep(Dt, floorY: 0);
        w.KinematicTestStep(Dt, floorY: 0);
        Assert.Equal((2, 5), (trace.Window.Steps, trace.Run.Steps));
        Assert.True(trace.Window.Source(PhysicsSource.Ground).Contacts > 0);

        trace.Clear();
        Assert.Equal((0, 0, 0, 0), (trace.Window.Steps, trace.Run.Steps, trace.Last.Steps, trace.LastContacts.Length));
    }

    [Fact]
    public void Trace_EventsOverThreshold_CountedAndKeptInFastest()
    {
        var w = World();
        var trace = w.EnableTrace(new PhysicsTrace(eventSpeed: 2.5f, recentEvents: 16));
        // 12 свободных шаров со скоростями 1…12 м/с — быстрее порога 10 (3…12)
        var bodies = new BodyHandle[12];
        for (int i = 0; i < 12; i++)
        {
            bodies[i] = w.Body(Target(w, 1, 0.1f, Vacuum, new Vector3(i * 10, 0, 0)), 0);
            var s = w.Get(bodies[i]);
            s.Velocity = new Vector3(0, 0, i + 1);
            w.Set(bodies[i], s);
        }
        w.KinematicTestStep(Dt);

        Assert.Equal((10, 10, 10), (trace.Last.Events, trace.Window.Events, trace.Run.Events));
        Assert.Equal(PhysicsTrace.TopCapacity, trace.Run.TopCount);
        for (int k = 0; k < PhysicsTrace.TopCapacity; k++)
        {
            var e = trace.Run.Top(k);
            Assert.True(MathF.Abs(e.Speed - (12 - k)) < 1e-3f, $"место {k}: {e.Speed}");   // по убыванию: 12 … 5
            Assert.Equal(bodies[11 - k], e.Body);
            Assert.Equal(w.ArticulationOf(bodies[11 - k]), e.Articulation);
            Assert.Equal((1f, 0L, 0f), (e.Mass, e.Step, e.HitDv));
        }
        Assert.Equal(10, trace.RecentEventCount);
        for (int k = 0; k < 10; k++) Assert.True(trace.RecentEvent(k).Speed > 2.5f);
        Assert.Contains("топ:", trace.Run.Summary("[физика]"));

        // второй тик: счёт растёт, «самые быстрые» — те же 8 скоростей, кольцо держит последние 16
        w.KinematicTestStep(Dt);
        Assert.Equal((10, 20, 20), (trace.Last.Events, trace.Window.Events, trace.Run.Events));
        // «самые быстрые» из 20 событий двух тиков — по два на 12, 11, 10, 9 м/с
        for (int k = 0; k < PhysicsTrace.TopCapacity; k++)
            Assert.True(MathF.Abs(trace.Run.Top(k).Speed - (12 - k / 2)) < 1e-3f, $"место {k}: {trace.Run.Top(k).Speed}");
        Assert.Equal(16, trace.RecentEventCount);
        Assert.Equal(1L, trace.RecentEvent(0).Step);
        // удар перед шагом — в строке события
        w.ApplyHit(bodies[11], w.Get(bodies[11]).Position, new Vector3(0, 0, 3), 0, ArticulationHandle.None);
        w.KinematicTestStep(Dt);
        Assert.True(trace.Last.Top(0).HitDv > 2.9f);
        Assert.Contains("удар Δv", trace.Last.Top(0).ToString());
    }

    [Fact]
    public void Trace_Push_RecordsSourceImpulseBudgetSharesAndPopSpeed()
    {
        var s = PhysicsSettings.Default;
        var w = World();
        var trace = w.EnableTrace();
        var body = Pusher(w, 0.1f, Vector3.Zero);
        var blade = Pusher(w, 0.1f, new Vector3(3, 0, 0));
        Source(w, blade, KinematicSource.Pass);
        var heavy = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        // лёгкая капсула задета у торца: толчок крутит — предел скорости держит точку, а не центр
        var light = w.CreateArticulation(1);
        var lb = w.Body(light, 0);
        w.SetShape(lb, 0.3f, 0.03f, 0.15f);
        w.Place(lb, new Vector3(3.05f, 0, 0.11f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        w.SetMode(lb, BodyMode.Simulated);
        w.SetDynamics(lb, Vacuum);
        w.PreparePass(blade, 1, 2, 16);
        for (int t = 0; t < 3; t++) w.KinematicTestStep(Dt);

        var run = trace.Run;
        Near(Momentum(w, heavy).Z, run.Source(PhysicsSource.PushBody).Applied, 1e-3f, "Σλ/h толчка тела — импульс цели");
        Assert.True(run.Source(PhysicsSource.PushPass).Applied > 0 && run.Source(PhysicsSource.PushPass).Count > 0);
        Assert.Equal(0, run.Source(PhysicsSource.PushNoReaction).Contacts);
        Assert.Equal(1f, run.PairShare);   // тяжёлая цель упирается в бюджет пары
        Assert.True(run.PassShare is > 0 and <= 1, $"доля прохода {run.PassShare}");
        Near(1 - w.Pass(blade, light).Remaining / w.Pass(blade, light).Potential, run.PassShare, 1e-4f, "доля прохода");
        Assert.True(run.PushPopSpeed <= s.PopSpeed + 0.05f, $"выталкивание {run.PushPopSpeed} м/с");
        Assert.True(run.PushPopSpeed > s.PopSpeed - 0.5f, $"предел скорости не упёрся: {run.PushPopSpeed}");
        Assert.True(run.Source(PhysicsSource.PushBody).Depth > 0.04f);
        Assert.Contains(trace.LastContacts.ToArray(), c => c.Source == PhysicsSource.PushBody && c.Other == w.Body(body, 0));
        Assert.Contains("толчок прохода", run.Summary("[физика]"));

        // отдача — в последний тик, когда её забирают
        var j = w.TakeReaction(body);
        Near(j.Length(), trace.Last.ReactionMax, 1e-6f, "отдача в трассе");
        Assert.Equal(body, trace.Last.ReactionArticulation);
    }

    [Fact]
    public void Trace_Hits_GoToLastTick_WithDvShareAndCuts()
    {
        var s = PhysicsSettings.Default;
        var w = World();
        var trace = w.EnableTrace();
        var blade = Pusher(w, 0.1f, new Vector3(5, 0, 0));
        var part = Target(w, 0.3f, 0.05f, Vacuum, Vector3.Zero);
        var pb = w.Body(part, 0);
        w.KinematicTestStep(Dt);
        w.PreparePass(blade, 1, 1, 2);   // 1 Н·с
        w.PassHit(blade, part);
        w.ApplyHit(pb, Vector3.Zero, new Vector3(0, 0, 5), 4, blade);   // потолок Δv 2 м/с → 0,6 Н·с, из прохода
        w.ApplyHit(pb, Vector3.Zero, new Vector3(0, 0, 5), 0, blade);   // без потолка — урезан остатком 0,4
        var last = trace.Last;
        Assert.Equal((2, 1, 1), (last.Hits, last.HitsCapped, last.HitsBudgeted));
        Near(s.HitDvShare, last.HitDvShare, 1e-4f, "доля Δv");
        Near(1, last.PassShare, 1e-4f, "проход исчерпан");
        Near(0.6f / 0.3f, last.HitDv, 1e-4f, "Δv тела");
        // следующий шаг — новый последний тик
        w.KinematicTestStep(Dt);
        Assert.Equal((0, 0f), (trace.Last.Hits, trace.Last.HitDvShare));
        Assert.Equal(2, trace.Run.Hits);

        // удар по части с закрытым проходом (потенциал тика есть, остатка нет): урезан проходом до нуля, доли прохода не тратит
        var other = w.Body(Target(w, 0.3f, 0.05f, Vacuum, new Vector3(0, 0, 3)), 0);
        w.PreparePass(blade, 1, 1, 2);
        Assert.Equal(Vector3.Zero, w.ApplyHit(other, w.Get(other).Position, new Vector3(0, 0, 5), 0, blade));
        Assert.Equal((1, 1, 0f), (trace.Last.Hits, trace.Last.HitsBudgeted, trace.Last.PassShare));
    }

    [Fact]
    public void Trace_HiddenShiftOfPrevPose_RecordedEvenWithoutCorrection()
    {
        // этап сдвинул только прошлую позу (скрытая скорость) — поправка пишется, Hidden = сдвиг / h
        const float h = TestBodies.H;
        var trace = new PhysicsTrace();
        trace.Grow(1, 1);
        var before = new PhysicsTrace.Snap(Vector3.Zero, Vector3.Zero, Quaternion.Identity);
        trace.Correction(0, PhysicsSource.Pair, h, in before, Vector3.Zero, new Vector3(0, 0, 1e-3f), Quaternion.Identity, 1);
        var pair = trace.Window.Source(PhysicsSource.Pair);
        Assert.Equal((1, 0f), (pair.Count, pair.Linear));
        Near(1e-3f / h, pair.Hidden, 1e-4f, "скрытый сдвиг / h");
        // ничего не сдвинуто — не поправка
        trace.Correction(0, PhysicsSource.Pair, h, in before, Vector3.Zero, Vector3.Zero, Quaternion.Identity, 1);
        Assert.Equal(1, trace.Window.Source(PhysicsSource.Pair).Count);
    }

    [Fact]
    public void Trace_EnergyGrowth_PerSubstep_PushedSeparately_HitsBetweenStepsExcluded()
    {
        var w = World();
        var trace = w.EnableTrace();
        var pusher = Pusher(w, 0.1f, Vector3.Zero);
        var pushed = Target(w, 10, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        var drifting = Target(w, 10, 0.1f, Vacuum, new Vector3(5, 0, 0));
        var falling = Target(w, 10, 0.1f, 0, new Vector3(-5, 3, 0));   // профиль по умолчанию: гравитация
        var d = w.Get(w.Body(drifting, 0));
        d.Velocity = new Vector3(2, 0, 1);
        w.Set(w.Body(drifting, 0), d);
        for (int t = 0; t < 10; t++)
        {
            w.KinematicTestStep(Dt);
            // удар между шагами добавляет энергию, но не «за подшаг»: база энергии — начало шага
            w.ApplyHit(w.Body(drifting, 0), w.Get(w.Body(drifting, 0)).Position, new Vector3(0, 0, 3), 0, ArticulationHandle.None);
        }
        var run = trace.Run;
        Assert.True(run.EnergyGrowthPushed > 1, $"толкаемое живыми набирает энергию: {run.EnergyGrowthPushed} Дж");
        // без толчков энергия не растёт (свободный полёт — постоянна до округления, падение — теряет ½mg²h² за подшаг)
        Assert.True(run.EnergyGrowth < 1e-3f, $"рост без толчков {run.EnergyGrowth} Дж ({run.EnergyArticulation})");
        Assert.True(run.EnergyGrowthTotal < 1e-3f, $"рост всех без толчков {run.EnergyGrowthTotal} Дж");
        Assert.Contains("рост энергии", run.Summary("[физика]"));
        _ = pusher;
        _ = pushed;
        _ = falling;
    }
}
