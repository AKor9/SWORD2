using System.Numerics;
using Sword.Engine.Diagnostics;
using Xunit.Abstractions;
using static Sword.Engine.Physics.Tests.ContactsKit;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Точка 7з-2 (скорость и покой физики трупа), свои тесты свойств: унаследованный разрыв сустава сводится не быстрее
/// <see cref="PhysicsSettings.JointPopSpeed"/> (до 1 см; новый и больший — сразу); трение между телами на физике — покоя (держит тело на
/// наклонной опоре другого тела) и скольжения (гасит относительную скорость, импульс пары сохраняется); трение о землю не топит застарелую
/// глубину; мир после пар — лёгкое тело под тяжёлым в землю не уходит; зоны профайлера по фазам подшага (выключенный и включённый — 0 Б); счёт сочленений с нечисловым телом на
/// физике (шаг и записи); рост энергии по фазам подшага и отдельно у трупов в контакте друг с другом (трасса).
/// </summary>
public class StepCorpseTests(ITestOutputHelper output)
{
    // ——— Разрыв сустава ———

    [Theory]
    [InlineData(0.004f)]   // больше JointPopSpeed·h (3,1 мм при 1,5 м/с и h = 1/480): сводится ровно JointPopSpeed·h
    [InlineData(0.03f)]    // больше предела отставания (1 см): сводится до него сразу
    public void Joint_InheritedGap_ClosesNoFasterThanJointPopSpeed_ExcessOverLagAtOnce(float gap)
    {
        var (w, art, _, child) = GapPair();
        var s = w.Get(child);
        w.Place(child, s.Position + new Vector3(gap, 0, 0), s.Rotation);   // и в прошлой позе: разрыв унаследован
        w.Step(TestBodies.H);
        float after = w.MeasureJoint(art, 0).PointError;
        float allowed = w.Settings.JointPopSpeed * TestBodies.H;
        float expected = MathF.Min(gap - allowed, PhysicsWorld.JointsPopLag);
        output.WriteLine($"разрыв {gap * 1000:F1} мм → {after * 1000:F3} мм (ожидание {expected * 1000:F3})");
        Assert.True(MathF.Abs(after - expected) <= 2e-4f, $"разрыв {gap} м после подшага {after} м, ожидалось {expected} м");
    }

    [Fact]
    public void Joint_InheritedGap_WithoutLimit_ClosesAtOnce_NewGapClosesAtOnce()
    {
        // без предела (∞) — как до 7з-2: весь унаследованный разрыв за подшаг
        var (w, art, _, child) = GapPair(PhysicsSettings.Default with { JointPopSpeed = float.PositiveInfinity });
        var s = w.Get(child);
        w.Place(child, s.Position + new Vector3(0.005f, 0, 0), s.Rotation);
        w.Step(TestBodies.H);
        Assert.True(w.MeasureJoint(art, 0).PointError <= 2e-4f, $"без предела разрыв остался {w.MeasureJoint(art, 0).PointError} м");

        // с пределом: новый разрыв этого подшага (тела разлетаются) — целиком, предел только для унаследованного
        (w, art, _, child) = GapPair();
        JointsRig.SetVelocity(w, child, new Vector3(0.006f / TestBodies.H, 0, 0), Vector3.Zero);   // за подшаг — 6 мм, в прошлой позе — 0
        w.Step(TestBodies.H);
        Assert.True(w.MeasureJoint(art, 0).PointError <= 2e-4f, $"новый разрыв не сведён: {w.MeasureJoint(art, 0).PointError} м");
    }

    /// <summary>Пара тел на физике в вакууме (один подшаг на шаг) с широким наклоном-скручиванием: предел не срабатывает, работает точка.</summary>
    private static (PhysicsWorld World, ArticulationHandle Art, BodyHandle Parent, BodyHandle Child) GapPair(PhysicsSettings? settings = null)
    {
        var (w, art, p, c) = JointsRig.Pair(BodyMode.Simulated, BodyMode.Simulated);
        w.Settings = (settings ?? PhysicsSettings.Default) with { Substeps = 1 };
        w.SetJoints(art, [JointDesc.SwingTwist(0, 1, JointsRig.ChildAnchor, -3, 3, 3, 3, 0)]);
        return (w, art, p, c);
    }

    // ——— Трение между телами на физике ———

    [Theory]
    [InlineData(0.7f, true)]    // tg 18,4° = 0,33 < μ: держит
    [InlineData(0f, false)]     // без трения — соскальзывает
    public void PairFriction_RodOnTwoBodies_Incline_Holds_WithoutFrictionSlides(float mu, bool holds)
    {
        // Две опоры (r 15 см, вдоль Z, 200 кг): одна на полу, другая на ступени 20 см, центры в 0,6 м; лёгкий стержень лежит на обеих
        // поперёк — наклон 18,4°, тяжесть тянет его вдоль оси вниз. Трение покоя пар (общий бюджет тела) держит, скольжения по Кулону — гасит;
        // без трения стержень съезжает. Опоры зажаты колоннами с обеих сторон: круглая опора под стержнем катится (стержень на катках — не
        // покой и в жизни; начальный толчок раскатывал опору без сопротивления), колонны держат центр, трение о землю — низ. Замеры 7з-2 до
        // исправлений: мир только после пар (опора осевшая на g·h² на время пар) — 13 мм за 2 с; мир до пар, но второй проход мира решал всю
        // глубину от начала подшага (поднимал опору на недотянутое первым проходом после трения пар) — 2 мм (опоры без колонн — ещё
        // раскатывались); с правкой — 0,14 мм.
        var w = World(PhysicsSettings.Default with { PairFriction = mu, PopSpeed = 1 }, new StepGround(new Vector3(0, -1, -3), new Vector3(3, 0.2f, 3)));
        const float ra = 0.15f, rb = 0.05f, rp = 0.05f;
        Vector3 low = new(-0.4f, ra, 0), high = new(0.2f, 0.2f + ra, 0);   // нижняя — на полу, верхняя — на ступени (ящик от x 0)
        Span<Pillar> pillars = stackalloc Pillar[8];   // по четыре на опору: с обеих сторон, в стороне от стержня (z ±0,4)
        int np = 0;
        foreach (var c in new[] { low, high })
            foreach (float side in new[] { -1f, 1f })
                foreach (float z in new[] { -0.4f, 0.4f })
                    pillars[np++] = new Pillar(c.X + side * (ra + rp), z, rp);
        w.SetPillars(pillars);
        var lowBody = Simulated(w, 200, ra, 0.6f, low, AxisAlong(Vector3.UnitZ));
        var highBody = Simulated(w, 200, ra, 0.6f, high, AxisAlong(Vector3.UnitZ));
        var axis = Vector3.Normalize(high - low);
        var normal = new Vector3(-axis.Y, axis.X, 0);
        var light = Simulated(w, 2, rb, 0.45f, (low + high) * 0.5f + normal * (ra + rb - 1e-4f), AxisAlong(axis), ProfileGravityOnly);
        var start = w.Get(light).Position;
        int held = 0, checks = 0;
        for (int t = 0; t < 240; t++)
        {
            w.Step(Dt);
            (held, checks) = (held + w.ContactsPairFrictionHeld, checks + w.ContactsPairFrictionChecks);
        }
        float slid = -Vector3.Dot(w.Get(light).Position - start, axis);   // вниз вдоль своей оси
        float supports = MathF.Max(Vector3.Distance(w.Get(lowBody).Position, low), Vector3.Distance(w.Get(highBody).Position, high));
        output.WriteLine($"μ пар {mu}: за 2 с съехала на {slid * 1000:F2} мм (опоры сдвинулись ≤ {supports * 1000:F2} мм); трение покоя пар держало {held} из {checks}");
        if (holds) Assert.True(slid <= 0.002f && held >= checks * 0.95f, $"μ {mu}: съехала на {slid} м; держало {held} из {checks}");
        else Assert.True(slid >= 0.05f && checks == 0, $"без трения не съехала: {slid} м (проверок трения {checks})");
    }

    [Fact]
    public void PairSliding_InVelocityPass_SlowsRelativeSliding_ConservesMomentum()
    {
        // Два тела в вакууме внахлёст (каждый подшаг — контакт с λ > 0), верхнее скользит поперёк нижнего: трение скольжения пар гасит
        // относительную касательную скорость, импульс пары сохраняется; без трения — касательная скорость та же.
        float Slide(float mu, out Vector3 momentum)
        {
            var w = World(PhysicsSettings.Default with { PairFriction = mu, PopSpeed = 0.1f });
            var a = Simulated(w, 5, 0.1f, 0.3f, Vector3.Zero, AxisAlong(Vector3.UnitX), ProfileVacuum);
            var b = Simulated(w, 2, 0.08f, 0.3f, new Vector3(0, 0.15f, 0), AxisAlong(Vector3.UnitZ), ProfileVacuum);
            JointsRig.SetVelocity(w, b, new Vector3(1.5f, 0, 0), Vector3.Zero);
            for (int t = 0; t < 6; t++) w.Step(Dt);
            var sa = w.Get(a);
            var sb = w.Get(b);
            momentum = Momentum(sa) + Momentum(sb);
            return sb.Velocity.X - sa.Velocity.X;
        }
        float with = Slide(0.7f, out var p1), without = Slide(0, out var p0);
        output.WriteLine($"относительная касательная скорость через 6 тиков: с трением {with:F3} м/с, без {without:F3} м/с");
        Assert.True(without >= 1.45f, $"без трения скольжение погасло: {without} м/с");
        Assert.True(with <= without - 0.3f, $"трение пар не гасит скольжение: {with} против {without} м/с");
        Assert.True(Vector3.Distance(p1, p0) <= 1e-3f && MathF.Abs(p1.X - 3) <= 1e-3f, $"импульс пары: {p1} (без трения {p0}, в начале (3; 0; 0))");
    }

    // ——— Трение о землю: не топит застарелую глубину ———

    [Fact]
    public void GroundFriction_DoesNotSinkOldDepth()
    {
        // Капсула (как стопа трупа) наклонена к торцу B (1 рад), он — в земле на 2 см и в прошлой позе (застарелая глубина: выходит не быстрее
        // PopSpeed — здесь 0,2 м/с, 0,4 мм за подшаг); каждый тик тело тянут к B и вниз (как сустав — стопу трупа в толпе). Трение покоя держит
        // точку касания, поворот от поправки клонит тело на B и вдавливает торец сильнее, чем нормаль выводит: до 7з-2 торец уходил глубже
        // начала (замер: 20 → 26 мм за 9 тиков; стопа в толпе трупов — 2 → 26 мм за 4 тика). С 7з-2 трение не делает торец глубже начала
        // подшага: глубина не растёт, торец выходит.
        var ground = new FlatGround();
        var w = World(PhysicsSettings.Default with { PopSpeed = 0.2f }, ground);
        const float r = 0.045f, half = 0.1f, deep = 0.02f, tilt = 1f;
        var axis = new Vector3(MathF.Cos(tilt), -MathF.Sin(tilt), 0);   // к торцу B (+X) вниз
        var body = Simulated(w, 1.2f, r, half, new Vector3(0, r - deep + half * MathF.Sin(tilt), 0), AxisAlong(axis));
        float d0 = Depth(ground, w.Get(body)), max = 0, d = d0;
        for (int t = 0; t < 30; t++)
        {
            JointsRig.SetVelocity(w, body, new Vector3(1.5f, -3f, 0), w.Get(body).AngularVelocity);   // тянут к B и вниз
            w.Step(Dt);
            d = Depth(ground, w.Get(body));
            max = MathF.Max(max, d);
        }
        output.WriteLine($"торец B: {d0 * 1000:F2} мм → тянули 30 тиков: глубина до {max * 1000:F2} мм, в конце {d * 1000:F2} мм");
        Assert.True(Math.Abs(d0 - deep) <= 1e-3f, $"сцена: торец B в земле на {d0} м");
        Assert.True(max <= d0 + 5e-4f, $"трение топило торец: глубина до {max} м (в начале {d0} м)");
        Assert.True(d <= 0.5f * d0, $"торец не вышел: {d} м");
    }
    // ——— Мир после пар ———

    [Fact]
    public void LightBodyUnderHeavy_NotPressedIntoGround()
    {
        // Клинок (1,4 кг, r 3 см) на земле, поперёк — тяжёлое тело (20 кг): мир после пар выводит прижатый парой клинок из земли к концу
        // подшага (7з-2; до — пара вдавливала его каждый подшаг, застарелая глубина выходила не быстрее PopSpeed: клинок под кучей тонул).
        var ground = new FlatGround();
        var w = World(PhysicsSettings.Default with { PopSpeed = 1 }, ground);
        var blade = Simulated(w, 1.4f, 0.03f, 0.5f, new Vector3(0, 0.03f, 0), AxisAlong(Vector3.UnitX));
        var heavy = Simulated(w, 20, 0.17f, 0.2f, new Vector3(0, 0.03f + 0.03f + 0.17f, 0), AxisAlong(Vector3.UnitZ));
        float depth = 0;
        for (int t = 0; t < 720; t++)
        {
            w.Step(Dt);
            depth = MathF.Max(depth, Depth(ground, w.Get(blade)));
        }
        output.WriteLine($"клинок под тяжёлым телом, 6 с: глубина ≤ {depth * 1000:F3} мм; тяжёлое — на высоте {w.Get(heavy).Position.Y:F3} м");
        Assert.True(depth <= 2e-3f, $"клинок под тяжёлым телом в земле на {depth} м");
        Assert.True(w.Get(heavy).Position.Y >= 0.2f, "тяжёлое тело провалилось сквозь клинок");
    }

    // ——— Зоны профайлера ———

    [Fact]
    public void Profiler_ZonesPerPhase_Recorded_DisabledAndOffZeroBytes()
    {
        var w = World(ground: new FlatGround());
        Simulated(w, 5, 0.1f, 0.3f, new Vector3(0, 0.3f, 0), AxisAlong(Vector3.UnitX));
        Simulated(w, 5, 0.1f, 0.3f, new Vector3(0.1f, 0.5f, 0), AxisAlong(Vector3.UnitZ));
        var profiler = new CpuProfiler(history: 0);
        w.AttachProfiler(profiler);
        profiler.SetEnabled(true);
        for (ulong t = 0; t < 20; t++) Tick(w, profiler, t);
        profiler.Reset();
        long bytes = 0;
        for (ulong t = 20; t < 120; t++) bytes += Allocated(() => Tick(w, profiler, t));
        var stats = profiler.Stats(ProfilePeriod.Tick);
        int substeps = w.Settings.Substeps;
        foreach (var zone in new[] { "physics.integrate", "physics.joints", "physics.pairs", "physics.velocity", "physics.contactVelocity",
                     "physics.relax" })
        {
            var z = stats.Single(s => s.Name == zone);
            Assert.True(z.Calls == substeps, $"{zone}: вызовов за тик {z.Calls}, подшагов {substeps}");
        }
        Assert.True(stats.Single(s => s.Name == "physics.ground").Calls == 2 * substeps, "physics.ground: мир и мир после пар в каждом подшаге");
        Assert.True(stats.Single(s => s.Name == "physics.bounds").Calls == substeps + 1, "physics.bounds: отсев подшагов и границы конца шага");
        Assert.True(bytes == 0, $"включённый профайлер: {bytes} Б за 100 тиков");

        profiler.SetEnabled(false);
        long disabled = 0;
        for (ulong t = 120; t < 220; t++) disabled += Allocated(() => Tick(w, profiler, t));
        w.AttachProfiler(CpuProfiler.Off);
        long off = 0;
        for (int t = 0; t < 100; t++) off += Allocated(() => w.Step(Dt));
        Assert.True(disabled == 0 && off == 0, $"выключенный профайлер {disabled} Б, без профайлера {off} Б за 100 тиков");
    }

    private static void Tick(PhysicsWorld w, CpuProfiler profiler, ulong t)
    {
        profiler.BeginTick(t);
        w.Step(Dt);
        profiler.EndTick();
    }

    private static long Allocated(Action act)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        act();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ——— Нечисловые тела ———

    [Fact]
    public void NonFinite_CountsArticulationsWithNonFiniteSimulatedBody_StepAndWrites()
    {
        var w = World(ground: new FlatGround());
        var a = Simulated(w, 5, 0.1f, 0.3f, new Vector3(0, 0.3f, 0), AxisAlong(Vector3.UnitX));
        var b = Simulated(w, 5, 0.1f, 0.3f, new Vector3(3, 0.3f, 0), AxisAlong(Vector3.UnitX));
        var kin = w.Body(w.CreateArticulation(1), 0);
        w.SetShape(kin, 1, 0.1f, 0.1f);
        w.Step(Dt);
        Assert.Equal(0, w.NonFiniteArticulations);

        // запись вне шага: нечисловое тело на физике — сразу в счёте; кинематическое — нет
        w.Place(a, new Vector3(float.NaN), Quaternion.Identity);
        w.Place(kin, new Vector3(float.NaN), Quaternion.Identity);
        Assert.Equal(1, w.NonFiniteArticulations);
        Assert.True(w.HasNonFinite(w.ArticulationOf(a)));
        Assert.False(w.HasNonFinite(w.ArticulationOf(b)));
        Assert.False(w.HasNonFinite(w.ArticulationOf(kin)));

        // шаг: нечисловое остаётся нечисловым (соседей не заражает), нечисловое кинематическое, ставшее телом на физике, — в счёт
        w.Step(Dt);
        Assert.Equal(1, w.NonFiniteArticulations);
        Assert.True(w.Get(b).IsFinite);
        w.SetMode(kin, BodyMode.Simulated);
        Assert.Equal(2, w.NonFiniteArticulations);

        // починили (поставили заново) и удалили — счёт снимается
        w.Place(a, new Vector3(0, 0.3f, 0), Quaternion.Identity);
        Assert.Equal(1, w.NonFiniteArticulations);
        w.DestroyArticulation(w.ArticulationOf(kin));
        Assert.Equal(0, w.NonFiniteArticulations);
        w.Step(Dt);
        Assert.Equal(0, w.NonFiniteArticulations);
    }

    // ——— Трасса: рост энергии по фазам и у трупов в контакте ———

    [Fact]
    public void Trace_EnergyByPhase_InheritedJointGapShowsInJointsPhase()
    {
        // Унаследованный разрыв сустава без предела сводится одной поправкой: «виртуальная» энергия (скорости сдвига за подшаг) растёт в
        // фазе суставов — трасса называет фазу.
        var (w, art, _, child) = GapPair(PhysicsSettings.Default with { JointPopSpeed = float.PositiveInfinity });
        var trace = w.EnableTrace();
        var s = w.Get(child);
        w.Place(child, s.Position + new Vector3(0.01f, 0, 0), s.Rotation);
        w.Step(TestBodies.H);
        var last = trace.Last;
        output.WriteLine(last.Summary("  "));
        Assert.True(last.PhaseGrowth(PhysicsPhase.Joints) > 0.01f, $"фаза суставов: {last.PhaseGrowth(PhysicsPhase.Joints)} Дж");
        Assert.Equal(art, last.PhaseArticulation(PhysicsPhase.Joints));
        Assert.True(last.PhaseGrowth(PhysicsPhase.Pairs) == 0 && last.PhaseGrowth(PhysicsPhase.World) == 0, "пар и мира нет — их фазы пусты");
    }

    [Fact]
    public void Trace_PairedArticulations_GrowthApartFromSingle()
    {
        // Два тела на физике внахлёст (разные сочленения) выходят друг из друга: рост их энергии — «в контакте» (передача, сумма касавшихся),
        // а не рост одиночного сочленения (инвариант physics.energy, 7з-2).
        var w = World(PhysicsSettings.Default with { PopSpeed = 1 });
        var trace = w.EnableTrace();
        Simulated(w, 5, 0.1f, 0.3f, Vector3.Zero, AxisAlong(Vector3.UnitX), ProfileVacuum);
        Simulated(w, 2, 0.08f, 0.3f, new Vector3(0, 0.1f, 0), AxisAlong(Vector3.UnitZ), ProfileVacuum);
        for (int t = 0; t < 5; t++) w.Step(Dt);
        var run = trace.Run;
        Assert.True(run.EnergyGrowthPaired > 0 && run.EnergyGrowthPairedTotal > 0, $"в контакте: {run.EnergyGrowthPaired} / {run.EnergyGrowthPairedTotal} Дж");
        Assert.Equal(0f, run.EnergyGrowth);
        Assert.Equal(ArticulationHandle.None, run.EnergyArticulation);
    }
}
