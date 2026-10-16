using System.Numerics;
using static Sword.Engine.Physics.Tests.KinematicScene;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Трасса физики: выключена по умолчанию и не меняет симуляцию, 0 Б в обоих режимах; выталкивание, толчки, удары, рост
/// энергии за подшаг и суставы — с точными числами. Шаг — настоящий <see cref="PhysicsWorld.Step"/>, земля — <see cref="FlatGround"/>.
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
        Assert.True(float.IsFinite(sink) && sink > 0);
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
    public void Trace_GroundPushOut_RecordedWithDepth_ExitNoFasterThanPopSpeed_ContactsOfLastSubstep()
    {
        // Шар утоплен в землю на 3 см. Шаг выводит застарелую глубину не быстрее PopSpeed (README физики, «Устройство» → «Земля», выход старой глубины):
        // за подшаг — PopSpeed·h, остальное откладывается; скоростной проход гасит скорость выталкивания (e = 0).
        // Ожидания — свойство настоящего шага: сдвиг / h = PopSpeed в каждом подшаге, отложенное = глубина −
        // PopSpeed·h, скорость выталкивания погашена.
        var s = PhysicsSettings.Default;
        float perSubstep = s.PopSpeed * TestBodies.H;
        var w = World();
        w.SetGround(new FlatGround());
        var trace = w.EnableTrace();
        var sunk = w.Body(Target(w, 5, 0.1f, Vacuum, new Vector3(0, 0.07f, 0)), 0);
        w.Step(Dt);
        var ground = trace.Last.Source(PhysicsSource.Ground);
        Assert.True(MathF.Abs(ground.Depth - 0.03f) < 1e-5f, $"глубина {ground.Depth}");
        Assert.Equal((4, 4), (ground.Contacts, ground.Count));   // контакт и поправка — в каждом подшаге
        Near(s.PopSpeed, ground.Linear, 1e-3f, "сдвиг выталкивания за подшаг / h — не больше PopSpeed");
        Assert.Equal(0f, ground.Hidden);   // прошлую позу выталкивание не трогает
        Near(5 * 4 * s.PopSpeed, ground.ImpulseTickMax, 1e-3f, "псевдоимпульс тика — 4 подшага по m·PopSpeed");
        Near(0.03f - perSubstep, ground.Deferred, 1e-3f, "отложено в подшаге 0: глубина − PopSpeed·h");
        Near(s.PopSpeed, ground.VelocitySolved, 1e-3f, "скорость выталкивания погашена скоростным проходом");
        Assert.Equal((1, 4), (trace.Last.Steps, trace.Last.Substeps));
        var after = w.Get(sunk);
        Near(0.07f + 4 * perSubstep, after.Position.Y, 1e-4f, "за тик вышел на 4·PopSpeed·h");
        Assert.True(MathF.Abs(after.Velocity.Y) < 1e-3f, $"скорость после выхода {after.Velocity.Y} м/с");
        // остаток (5 мм) меньше PopSpeed·h — решается целиком в подшаге 0 следующего тика
        w.Step(Dt);
        Assert.True(ContactsKit.Depth(new FlatGround(), w.Get(sunk)) < 1e-5f, $"не вышел: {ContactsKit.Depth(new FlatGround(), w.Get(sunk))}");
        Assert.Equal(1, trace.Last.Source(PhysicsSource.Ground).Count);

        // лежачая капсула под тяжестью касается земли обоими торцами в каждом подшаге: контактов тика 8, последних — 2
        w = World();
        w.SetGround(new FlatGround());
        trace = w.EnableTrace();
        var lying = w.CreateArticulation(1);
        var b = w.Body(lying, 0);
        w.SetShape(b, 5, 0.1f, 0.3f);
        w.Place(b, new Vector3(10, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        w.SetMode(b, BodyMode.Simulated);
        w.Step(Dt);
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
        small.SetGround(new FlatGround());
        var st = small.EnableTrace(new PhysicsTrace(contactCapacity: 1));
        var sb = small.Body(small.CreateArticulation(1), 0);
        small.SetShape(sb, 5, 0.1f, 0.3f);
        small.Place(sb, new Vector3(0, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        small.SetMode(sb, BodyMode.Simulated);
        small.Step(Dt);
        Assert.Equal((1, 1), (st.LastContacts.Length, st.LostContacts));
    }

    [Fact]
    public void Trace_Step_MeasuresSubstepZero_FromStepStart()
    {
        // Шаг зовёт TraceStepBegin: база энергии подшага 0 — начало шага. Шар утоплен на 1 мм (меньше PopSpeed·h) — весь выход в
        // подшаге 0, рост энергии за подшаг — m·g·1 мм; без базы начала шага подшаг 0 не мерился бы (рост ≈ 0).
        const float m = 5, depth = 1e-3f;
        var w = World();
        w.SetGround(new FlatGround());
        var trace = w.EnableTrace();
        var ball = w.Body(Target(w, m, 0.1f, 0, new Vector3(0, 0.1f - depth, 0)), 0);
        float g = w.GetDynamics(0).Gravity;
        w.Step(Dt);
        Near(m * g * depth, trace.Last.EnergyGrowth, 0.02f, "рост энергии за подшаг 0 — выход из земли");
        Assert.Equal(w.ArticulationOf(ball), trace.Last.EnergyArticulation);
        // перенос между шагами (команда сцены, переход в труп) — не рост за подшаг: база — начало следующего шага
        w.Place(ball, new Vector3(0, 2, 0), Quaternion.Identity);
        w.Step(Dt);
        Assert.True(trace.Last.EnergyGrowth < 1e-4f, $"перенос между шагами попал в рост: {trace.Last.EnergyGrowth} Дж");
    }

    [Fact]
    public void Trace_Joints_EveryBodyOfArticulationSnapshotted()
    {
        // Суставы открывают снимки у всех тел сочленения на физике (N TraceBegin), проход, затем N TraceEnd: поправка каждого
        // тела мерится от его собственного снимка. Три шара цепочкой, средний сдвинут вбок на 2 см; шаг из одного подшага, без
        // гравитации и скоростей — сдвиг тела за шаг и есть поправка суставов. Разрыв унаследован (сдвиг — и прошлой позы): сводится
        // целиком только без предела скорости сведения (JointPopSpeed) — здесь проверяется трасса, а не сведение.
        var w = World(substeps: 1, PhysicsSettings.Default with { JointPopSpeed = float.PositiveInfinity });
        var trace = w.EnableTrace();
        var art = Target(w, 2, 0.05f, Vacuum, Vector3.Zero, new Vector3(0, 0.2f, 0), new Vector3(0, 0.4f, 0));
        var anchor = new Vector3(0, -0.1f, 0);
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, anchor, -3, 3, 3, 3, 0), JointDesc.SwingTwist(1, 2, anchor, -3, 3, 3, 3, 0)]);
        w.Place(w.Body(art, 1), new Vector3(0.02f, 0.2f, 0), Quaternion.Identity);
        var before = new BodyState[3];
        for (int i = 0; i < 3; i++) before[i] = w.Get(w.Body(art, i));
        w.Step(TestBodies.H);

        float linear = 0, angular = 0;
        for (int i = 0; i < 3; i++)
        {
            var s = w.Get(w.Body(art, i));
            linear = MathF.Max(linear, Vector3.Distance(before[i].Position, s.Position) / TestBodies.H);
            angular = MathF.Max(angular, PhysicsMath.RotationVector(before[i].Rotation, s.Rotation).Length() / TestBodies.H);
        }
        var joints = trace.Last.Source(PhysicsSource.Joints);
        Assert.Equal(3, joints.Count);   // по поправке на тело
        Near(linear, joints.Linear, 1e-3f, "сдвиг суставами за подшаг / h — от снимка своего тела");
        Near(angular, joints.Angular, 1e-3f, "угол суставами за подшаг / h");
        Assert.Equal(0f, joints.Hidden);
        Assert.True(linear > 1, $"суставы почти не двигали: {linear} м/с");
    }

    [Fact]
    public void Trace_WindowClearedSeparatelyFromRun()
    {
        var w = World();
        w.SetGround(new FlatGround());
        var trace = w.EnableTrace();
        var b = w.Body(Target(w, 2, 0.1f, 0, new Vector3(0, 0.05f, 0)), 0);
        for (int t = 0; t < 3; t++) w.Step(Dt);
        Assert.Equal((3, 3), (trace.Window.Steps, trace.Run.Steps));
        int contacts = trace.Run.Source(PhysicsSource.Ground).Contacts;
        Assert.True(contacts > 0);
        Assert.Equal(contacts, trace.Window.Source(PhysicsSource.Ground).Contacts);

        trace.ClearWindow();
        Assert.Equal((0, 0, 0L), (trace.Window.Steps, trace.Window.Source(PhysicsSource.Ground).Contacts, trace.Window.SpeedSamples));
        Assert.Equal((3, contacts), (trace.Run.Steps, trace.Run.Source(PhysicsSource.Ground).Contacts));
        Assert.Equal(1, trace.Last.Steps);   // последний тик окно не трогает

        w.Place(b, new Vector3(0, 0.05f, 0), Quaternion.Identity);
        w.Step(Dt);
        w.Step(Dt);
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
        w.Step(Dt);

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
        w.Step(Dt);
        Assert.Equal((10, 20, 20), (trace.Last.Events, trace.Window.Events, trace.Run.Events));
        // «самые быстрые» из 20 событий двух тиков — по два на 12, 11, 10, 9 м/с
        for (int k = 0; k < PhysicsTrace.TopCapacity; k++)
            Assert.True(MathF.Abs(trace.Run.Top(k).Speed - (12 - k / 2)) < 1e-3f, $"место {k}: {trace.Run.Top(k).Speed}");
        Assert.Equal(16, trace.RecentEventCount);
        Assert.Equal(1L, trace.RecentEvent(0).Step);
        // удар перед шагом — в строке события
        w.ApplyHit(bodies[11], w.Get(bodies[11]).Position, new Vector3(0, 0, 3), 0, ArticulationHandle.None);
        w.Step(Dt);
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
        for (int t = 0; t < 3; t++) w.Step(Dt);

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
        w.Step(Dt);
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
        w.Step(Dt);
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
            w.Step(Dt);
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
