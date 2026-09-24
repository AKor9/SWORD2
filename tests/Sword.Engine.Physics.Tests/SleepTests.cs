using System.Diagnostics;
using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Physics.Tests.ContactsKit;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Точка 7з-3 (сон трупа), свои тесты свойств: лежащий труп засыпает за заданное время и сам не просыпается; сползающий — не засыпает; спящий
/// не меняет позы (поза начала тика и прошлая — поза, скорости 0), меняется только счётчик покоя; будят толчок живого (быстрее порога части
/// или глубже допуска), удар, касание движущегося тела на физике, запись тела, режим, профиль, суставы, <see cref="IPhysicsWorld.Wake"/>,
/// настройки, профиль динамики, земля, стена, колонны; цепочка по куче — на одно касание; из-под спящих уходит опора (возрождение, сдвиг,
/// удаление нижнего) — лежавшие на нём просыпаются и ложатся, падающий будит лежащих на нём (проверка 7з-3); спящее — неподвижная опора медленному неспящему;
/// детерминизм с пробуждениями; 0 Б на шаг; трасса — пробуждения по причинам, спящие не в скоростях. Замер — куча 32 трупов засыпает, шаг
/// спящей кучи (в выводе; Release — docs/history/engine-physics.md, «7з-3»).
/// </summary>
public class SleepTests(ITestOutputHelper output)
{
    /// <summary>Настройки со сном, как у игры: пороги по умолчанию (0,05 м/с трупа, 0,5 м/с части), 0,5 с покоя, выталкивание 1 м/с.</summary>
    private static readonly PhysicsSettings Sleepy = PhysicsSettings.Default with { SleepTime = 0.5f, PopSpeed = 1 };

    // ——— Засыпание ———

    [Fact]
    public void LyingCorpse_FallsAsleep_StaysAsleep_PoseFrozen_OnlyRestTimeChanges()
    {
        var w = World(Sleepy, new FlatGround());
        var trace = w.EnableTrace();
        var art = Corpse(w, new Vector3(0, 0.4f, 0), Lying(0.3f));
        int asleepAt = StepUntilAsleep(w, art, 600);
        output.WriteLine($"лежащий труп уснул через {asleepAt / 120f:F2} с после падения с 0,4 м");
        Assert.True(asleepAt > 0 && asleepAt <= 480, $"не уснул за 4 с (тик {asleepAt})");

        var frozen = States(w, art);
        float rest = w.RestTime(art);
        for (int k = 0; k < 14; k++)
        {
            var s = frozen[k];
            Assert.Equal((Vector3.Zero, Vector3.Zero), (s.Velocity, s.AngularVelocity));
            Assert.Equal((s.Position, s.Position, s.Rotation, s.Rotation), (s.PrevPosition, s.StartPosition, s.PrevRotation, s.StartRotation));
        }
        trace.ClearWindow();
        for (int t = 0; t < 1200; t++)
        {
            w.Step(Dt);
            Assert.True(w.IsAsleep(art), $"проснулся сам на тике {t}");
        }
        Assert.Equal(frozen, States(w, art));   // поза и все поля тел — бит в бит
        Assert.Equal(rest + 1200 * Dt, w.RestTime(art), 1e-3f);   // меняется только счётчик покоя
        Assert.Equal((0, 0L), (trace.Window.WakesTotal, trace.Window.SpeedSamples));   // спящие не в скоростях трассы
        Assert.Equal(1, w.AsleepCount);
    }

    [Fact]
    public void SlidingCapsule_SlowlyAccelerating_DoesNotFallAsleep()
    {
        // склон чуть круче трения покоя: тело срывается и разгоняется медленно (g·(sin − μ_ск·cos) ~ 0,2…0,3 м/с²) — при сне по одной скорости
        // «медленнее порога» оно уснуло бы на старте, а счётчик покоя сбрасывается, едва скорость выше 0,05 м/с
        const float mu = 0.3f;
        float angle = MathF.Atan(1.08f * mu);
        var slope = new SlopeGround(angle);
        var w = World(Sleepy with { StaticFriction = mu }, slope);
        var b = Simulated(w, 5, 0.1f, 0.3f, slope.Normal * 0.1f, AxisAlong(slope.Downhill), ProfileGravityOnly);   // вдоль склона — скользит, не катится
        var art = w.ArticulationOf(b);
        float rest = 0, second = 0;
        for (int t = 0; t < 600; t++)
        {
            w.Step(Dt);
            rest = MathF.Max(rest, w.RestTime(art));
            if (t == 119) second = w.Get(b).Velocity.Length();
            Assert.False(w.IsAsleep(art), $"сползающее тело уснуло на тике {t} (скорость {w.Get(b).Velocity.Length():F3} м/с)");
        }
        output.WriteLine($"сползает: через 1 с {second:F2} м/с, через 5 с {w.Get(b).Velocity.Length():F2} м/с, наибольший счётчик покоя {rest:F3} с");
        Assert.True(second < 0.5f && w.Get(b).Velocity.Length() > 0.1f, "тело не сползало медленно — тест ничего не проверил");
    }

    [Fact]
    public void Corpse_OnAwakeBody_DoesNotFallAsleep_OnGroundOrSleepers_Does()
    {
        // опора: на теле, которое держит кинематика (живой), и в воздухе на неспящем — не засыпает; на земле — засыпает
        var w = World(Sleepy with { SleepTime = 0.2f }, new FlatGround());
        var table = w.CreateArticulation(1);
        Setup(w, w.Body(table, 0), 50, 0.2f, 0.6f, new Vector3(0, 0.2f, 0), AxisAlong(Vector3.UnitX), 0);
        w.SetMode(w.Body(table, 0), BodyMode.Kinematic);   // «стол» — кинематическое тело, неподвижное
        var onTable = w.ArticulationOf(Simulated(w, 5, 0.1f, 0.2f, new Vector3(0, 0.52f, 0), AxisAlong(Vector3.UnitZ)));
        var onGround = w.ArticulationOf(Simulated(w, 5, 0.1f, 0.2f, new Vector3(2, 0.11f, 0), AxisAlong(Vector3.UnitZ)));
        for (int t = 0; t < 240; t++) w.Step(Dt);
        Assert.True(w.IsAsleep(onGround), "на земле — должно уснуть");
        Assert.False(w.IsAsleep(onTable), "на живом (кинематическом) — не засыпает: живой уйдёт — повисло бы");
        Assert.True(w.RestTime(onTable) > 0.2f, "лежит неподвижно — счётчик покоя идёт, держит опора");
    }

    // ——— Пробуждение ———

    [Fact]
    public void Push_KinematicMovingIntoSleeper_Wakes_SlowOnlyDeeperThanSlop()
    {
        var (w, trace, sleeper) = SleepingCapsule();
        var pusher = w.CreateArticulation(1);
        var kb = w.Body(pusher, 0);
        w.SetShape(kb, 5, 0.1f, 0.2f);
        var start = new Vector3(0.505f, 0.1f, 0);   // в 5 мм сбоку от торца (капсула спящего — вдоль X, полудлина 0,3, радиусы по 0,1)
        w.Place(kb, start, AxisAlong(Vector3.UnitZ));

        // медленно (1 см/с): не будит, пока не вошёл глубже допуска; вошёл — будит толчком
        var at = start;
        int woke = -1;
        for (int t = 0; t < 360 && woke < 0; t++)
        {
            at -= new Vector3(0.01f * Dt, 0, 0);
            w.SetKinematicTarget(kb, at, AxisAlong(Vector3.UnitZ), Dt);
            w.Step(Dt);
            if (!w.IsAsleep(sleeper)) woke = t;
        }
        float depth = 0.1f + 0.1f - (at.X - 0.3f);   // сумма радиусов − расстояние от торца спящего до оси толкающего
        output.WriteLine($"медленный толчок разбудил на тике {woke}: вошёл на {depth * 1000:F1} мм (допуск {PhysicsWorld.SleepSlop * 1000} мм)");
        Assert.True(woke > 0 && depth > PhysicsWorld.SleepSlop - 2e-3f && depth <= PhysicsWorld.SleepSlop + 2e-3f, $"разбудил на {depth} м");
        Assert.True(trace.Run.Wakes(WakeReason.Push) == 1, "причина — толчок");

        // быстрый (1 м/с): будит, едва оказался у спящего
        (w, trace, sleeper) = SleepingCapsule();
        pusher = w.CreateArticulation(1);
        kb = w.Body(pusher, 0);
        w.SetShape(kb, 5, 0.1f, 0.2f);
        w.Place(kb, start + new Vector3(0.1f, 0, 0), AxisAlong(Vector3.UnitZ));
        at = start + new Vector3(0.1f, 0, 0);
        woke = -1;
        for (int t = 0; t < 30 && woke < 0; t++)
        {
            at -= new Vector3(1f * Dt, 0, 0);
            w.SetKinematicTarget(kb, at, AxisAlong(Vector3.UnitZ), Dt);
            w.Step(Dt);
            if (!w.IsAsleep(sleeper)) woke = t;
        }
        float gap = at.X - 0.3f - 0.2f;
        output.WriteLine($"быстрый толчок разбудил на тике {woke}, зазор {gap * 1000:F1} мм");
        Assert.True(woke >= 0 && gap > -2e-3f, $"быстрый толчок: разбудил на тике {woke}, зазор {gap} м");
    }

    [Fact]
    public void Hit_PassHitAndApplyHit_Wake()
    {
        var (w, trace, sleeper) = SleepingCapsule();
        var pusher = w.CreateArticulation(1);
        w.SetShape(w.Body(pusher, 0), 5, 0.1f, 0.2f);
        w.Place(w.Body(pusher, 0), new Vector3(3, 1, 0), Quaternion.Identity);
        w.PassHit(pusher, sleeper);
        Assert.False(w.IsAsleep(sleeper));
        Assert.Equal(1, trace.Run.Wakes(WakeReason.Hit));

        (w, trace, sleeper) = SleepingCapsule();
        var b = w.Body(sleeper, 0);
        w.ApplyHit(b, w.Get(b).Position, new Vector3(0, 0, 2), 0, ArticulationHandle.None);
        Assert.False(w.IsAsleep(sleeper));
        Assert.True(w.Get(b).Velocity.Z > 0, "импульс удара — в скорость разбуженного");
        Assert.Equal(1, trace.Run.Wakes(WakeReason.Hit));
        w.Step(Dt);
        Assert.True(w.Get(b).Position.Z > 0, "разбуженный ударом двигается уже в следующем шаге");
    }

    [Fact]
    public void Touch_FallingBody_WakesSleeper_SlowRestingDoesNot_SleeperIsSupport()
    {
        // падающее тело (быстрее порога части) будит спящее до подшагов шага, где коснулось бы
        var (w, trace, sleeper) = SleepingCapsule();
        var falling = Simulated(w, 5, 0.1f, 0.2f, new Vector3(0, 0.6f, 0), AxisAlong(Vector3.UnitZ));
        int woke = -1;
        for (int t = 0; t < 120 && woke < 0; t++)
        {
            w.Step(Dt);
            if (!w.IsAsleep(sleeper)) woke = t;
        }
        Assert.True(woke >= 0, "падающее не разбудило");
        Assert.Equal(1, trace.Run.Wakes(WakeReason.Touch));
        output.WriteLine($"падающее разбудило на тике {woke}, скорость {w.Get(falling).Velocity.Length():F2} м/с");

        // тело, положенное на спящее почти без зазора (1 мм), медленнее порога: спящее — неподвижная опора, не будится, тело не тонет
        (w, trace, sleeper) = SleepingCapsule();
        var resting = Simulated(w, 5, 0.1f, 0.2f, new Vector3(0, 0.301f, 0), AxisAlong(Vector3.UnitZ));
        float depth = 0;
        for (int t = 0; t < 240; t++)
        {
            w.Step(Dt);
            depth = MathF.Max(depth, Overlap(w.Get(resting), w.Get(w.Body(sleeper, 0))));
            Assert.True(w.IsAsleep(sleeper), $"лёгшее медленно разбудило спящее на тике {t}");
        }
        output.WriteLine($"лёгшее на спящее: глубина ≤ {depth * 1000:F2} мм, касаний спящего {trace.Run.SleepContacts}");
        Assert.True(depth <= 2e-3f, $"лёгшее на спящее утонуло на {depth} м");
        Assert.True(trace.Run.SleepContacts > 0, "касания спящего как опоры не было");
        Assert.True(w.IsAsleep(w.ArticulationOf(resting)), "лёгшее на спящее само засыпает (опора — спящий)");
    }

    [Fact]
    public void Chain_WokenWakesTouchingSleepers_SameTick_OneTouchDeep()
    {
        // стопка из трёх капсул на земле: A — на земле, B — на A, C — на B; все уснули. Удар по A: в шаге A и B (касается A) проснулись
        // (цепочка), C (касается только B) — нет: цепочка — на одно касание, дальше — движением
        var w = World(Sleepy, new FlatGround());
        var trace = w.EnableTrace();
        var a = w.ArticulationOf(Simulated(w, 10, 0.1f, 0.3f, new Vector3(0, 0.1f, 0), AxisAlong(Vector3.UnitX)));
        var b = w.ArticulationOf(Simulated(w, 5, 0.1f, 0.3f, new Vector3(0, 0.3f, 0), AxisAlong(Vector3.UnitZ)));
        var c = w.ArticulationOf(Simulated(w, 3, 0.1f, 0.3f, new Vector3(0, 0.5f, 0), AxisAlong(Vector3.UnitX)));
        for (int t = 0; t < 360 && w.AsleepCount < 3; t++) w.Step(Dt);
        Assert.Equal(3, w.AsleepCount);
        w.ApplyHit(w.Body(a, 0), w.Get(w.Body(a, 0)).Position, new Vector3(0, 0, 0.5f), 0, ArticulationHandle.None);
        w.Step(Dt);
        Assert.False(w.IsAsleep(a));
        Assert.False(w.IsAsleep(b));
        Assert.True(w.IsAsleep(c), "цепочка — на одно касание: лежащий на разбуженном цепочкой спит, пока его не сдвинут");
        Assert.Equal((1, 1), (trace.Run.Wakes(WakeReason.Hit), trace.Run.Wakes(WakeReason.Chain)));
    }

    public enum SupportLoss { Revive, Shift, Destroy }

    [Theory]
    [InlineData(SupportLoss.Revive)]
    [InlineData(SupportLoss.Shift)]
    [InlineData(SupportLoss.Destroy)]
    public void SupportRemoved_BetweenSteps_SleepersOnTopFall_NothingHangs(SupportLoss loss)
    {
        // найдено проверкой 7з-3: стопка A (на земле) — B (на A) — C (на B), все спят. Нижний уходит между шагами: возрождение (кинематика и
        // постройка в другом месте), команда сцены «сдвинуть тела», удаление. Цепочка в начале шага шла от новой позы нижнего — B и C висели
        // в воздухе (0,2 и 0,4 м); теперь B будит запись по позе до неё (цепочка), C — падающий из покоя B (касание сверху): зазор ½·g·t²
        // обгонял досягаемость раньше, чем точка B набирала порог части
        var w = World(Sleepy, new FlatGround());
        var trace = w.EnableTrace();
        var a = w.ArticulationOf(Simulated(w, 10, 0.1f, 0.3f, new Vector3(0, 0.1f, 0), AxisAlong(Vector3.UnitX)));
        var b = w.ArticulationOf(Simulated(w, 5, 0.1f, 0.3f, new Vector3(0, 0.3f, 0), AxisAlong(Vector3.UnitZ)));
        var c = w.ArticulationOf(Simulated(w, 3, 0.1f, 0.3f, new Vector3(0, 0.5f, 0), AxisAlong(Vector3.UnitX)));
        for (int t = 0; t < 360 && w.AsleepCount < 3; t++) w.Step(Dt);
        Assert.Equal(3, w.AsleepCount);
        trace.Clear();
        var ba = w.Body(a, 0);
        var far = new Vector3(5, 1, 0);
        switch (loss)
        {
            case SupportLoss.Revive:
                var s = w.Get(ba);
                s.Mode = BodyMode.Kinematic;
                s.Grounded = false;
                w.Set(ba, s);
                w.Place(ba, far, Quaternion.Identity);
                break;
            case SupportLoss.Shift:
                w.Place(ba, new Vector3(5, 0.1f, 0), w.Get(ba).Rotation);
                break;
            case SupportLoss.Destroy:
                w.DestroyArticulation(a);
                break;
        }
        Assert.False(w.IsAsleep(b), "лежавший на нижнем не проснулся при записи нижнего");
        for (int t = 0; t < 240; t++)
        {
            if (loss == SupportLoss.Revive) w.SetKinematicTarget(ba, far, Quaternion.Identity, Dt);
            w.Step(Dt);
        }
        float bottomB = Bottom(w.Get(w.Body(b, 0))), bottomC = Bottom(w.Get(w.Body(c, 0)));
        output.WriteLine($"{loss}: низ B {bottomB * 1000:F1} мм, низ C {bottomC * 1000:F1} мм; пробуждений цепочкой {trace.Run.Wakes(WakeReason.Chain)}, " +
                         $"касанием {trace.Run.Wakes(WakeReason.Touch)}");
        Assert.True(MathF.Abs(bottomB) < 3e-3f, $"B висит: низ {bottomB} м (на земле — 0)");
        Assert.True(MathF.Abs(bottomC - 0.2f) < 3e-3f, $"C висит: низ {bottomC} м (на B — 0,2)");
        Assert.Equal((1, 1), (trace.Run.Wakes(WakeReason.Chain), trace.Run.Wakes(WakeReason.Touch)));   // B — по позе до записи, C — падающий B
    }

    [Fact]
    public void KinematicWrite_DoesNotWakeNeighbours_OnlyItsOwn()
    {
        // кинематическое тело спящих не держит (шаг их не сталкивает): его постановка в другое место соседей не будит (новое тело — в начале
        // координат, у спящего)
        var (w, trace, sleeper) = SleepingCapsule();
        var kb = w.Body(w.CreateArticulation(1), 0);
        w.SetShape(kb, 5, 0.1f, 0.2f);
        w.Place(kb, new Vector3(3, 1, 0), Quaternion.Identity);
        Assert.True(w.IsAsleep(sleeper));
        Assert.Equal(0, trace.Run.WakesTotal);
    }

    private static float Bottom(in BodyState s) => MathF.Min(s.EndA.Y, s.EndB.Y) - s.Radius;

    [Fact]
    public void Commands_Wake_WritesModeProfileShapeJointsWake_SameValuesDoNot()
    {
        void Expect(string what, Action<PhysicsWorld, ArticulationHandle, BodyHandle> act, bool wakes)
        {
            var (w, trace, art, body) = SleepingCorpse();
            act(w, art, body);
            Assert.True(w.IsAsleep(art) != wakes, $"{what}: {(wakes ? "не разбудило" : "разбудило")}");
            Assert.Equal(wakes ? 1 : 0, trace.Run.Wakes(WakeReason.Command));
        }
        Expect("Place", (w, _, b) => w.Place(b, w.Get(b).Position, w.Get(b).Rotation), true);
        Expect("Set", (w, _, b) => w.Set(b, w.Get(b)), true);
        Expect("SetMode", (w, _, b) => w.SetMode(b, BodyMode.Simulated), true);
        Expect("SetShape", (w, _, b) => w.SetShape(b, w.Get(b).Mass, w.Get(b).Radius, w.Get(b).HalfLength), true);
        Expect("SetDynamics — другой профиль", (w, _, b) => w.SetDynamics(b, ProfileGravityOnly), true);
        Expect("SetDynamics — тот же", (w, _, b) => w.SetDynamics(b, 0), false);
        Expect("SetJoints", (w, a, _) => w.SetJoints(a, JointsRig.FighterJoints(w, a)), true);
        Expect("RebindJoints", (w, a, _) => w.RebindJoints(a), true);
        Expect("SetJointEnabled — выключить", (w, a, _) => w.SetJointEnabled(a, 0, false), true);
        Expect("SetJointEnabled — то же", (w, a, _) => w.SetJointEnabled(a, 0, true), false);
        Expect("Wake", (w, a, _) => w.Wake(a), true);
        Expect("SetKinematicSource", (w, _, b) => w.SetKinematicSource(b, KinematicSource.Body), false);
    }

    [Fact]
    public void Settings_Changed_WakeAll_Equal_DoNot_ProfileGroundWallPillars()
    {
        void Expect(string what, Action<PhysicsWorld> act, bool wakes)
        {
            var (w, trace, art, _) = SleepingCorpse();
            act(w);
            Assert.True(w.IsAsleep(art) != wakes, $"{what}: {(wakes ? "не разбудило" : "разбудило")}");
            Assert.Equal(wakes ? 1 : 0, trace.Run.Wakes(WakeReason.Settings));
        }
        Expect("настройки — другие", w => w.Settings = w.Settings with { StaticFriction = 0.5f }, true);
        Expect("настройки — равные (новый объект)", w => w.Settings = w.Settings with { }, false);
        Expect("порог сна", w => w.Settings = w.Settings with { SleepSpeed = 0.04f }, true);
        Expect("сон выключен", w => w.Settings = w.Settings with { SleepTime = float.PositiveInfinity }, true);
        Expect("профиль тел трупа — другой", w => w.ReplaceDynamics(0, w.GetDynamics(0) with { MaxSpeed = 20 }), true);
        Expect("профиль тел трупа — тот же", w => w.ReplaceDynamics(0, w.GetDynamics(0)), false);
        Expect("чужой профиль", w => w.ReplaceDynamics(ProfileVacuum, w.GetDynamics(ProfileVacuum) with { MaxSpeed = 20 }), false);
        Expect("земля — другая", w => w.SetGround(new FlatGround(-0.01f)), true);
        Expect("стена", w => w.SetWall(30), true);
        Expect("колонны", w => w.SetPillars([new Pillar(10, 10, 1)]), true);
        Expect("колонны — те же (пусто)", w => w.SetPillars([]), false);
    }

    [Fact]
    public void Destroyed_Created_InSlot_NotAsleep_CountsConsistent()
    {
        var (w, _, art, _) = SleepingCorpse();
        Assert.Equal(1, w.AsleepCount);
        w.DestroyArticulation(art);
        Assert.Equal(0, w.AsleepCount);
        var again = Corpse(w, new Vector3(0, 0.4f, 0), Lying(0));
        Assert.Equal(art.Index, again.Index);
        Assert.False(w.IsAsleep(again));
        Assert.Equal(0f, w.RestTime(again));
    }

    // ——— Детерминизм, память, трасса ———

    [Fact]
    public void Deterministic_WithSleepAndWakes()
    {
        static ulong Run()
        {
            var w = World(Sleepy, new FlatGround());
            var arts = new[]
            {
                Corpse(w, new Vector3(0, 0.4f, 0), Lying(0.3f)), Corpse(w, new Vector3(0.3f, 0.9f, 0.2f), Lying(1.9f)),
                Corpse(w, new Vector3(-0.2f, 1.4f, -0.1f), Lying(-0.8f)),
            };
            var pusher = w.CreateArticulation(1);
            var kb = w.Body(pusher, 0);
            w.SetShape(kb, 5, 0.1f, 0.3f);
            var at = new Vector3(4, 0.3f, 0);
            w.Place(kb, at, Quaternion.Identity);
            ulong hash = 14695981039346656037;
            for (int t = 0; t < 900; t++)
            {
                if (t == 500) w.ApplyHit(w.Body(arts[1], 0), w.Get(w.Body(arts[1], 0)).Position, new Vector3(3, 0, 0), 0, ArticulationHandle.None);
                if (t > 600) at -= new Vector3(1.5f * Dt, 0, 0);   // живой проходит сквозь кучу
                w.SetKinematicTarget(kb, at, Quaternion.Identity, Dt);
                w.Step(Dt);
                foreach (var a in arts)
                {
                    hash = (hash ^ (w.IsAsleep(a) ? 1UL : 0)) * 1099511628211;
                    for (int k = 0; k < 14; k++)
                    {
                        var s = w.Get(w.Body(a, k));
                        hash = (hash ^ BitConverter.SingleToUInt32Bits(s.Position.X) ^ ((ulong)BitConverter.SingleToUInt32Bits(s.Position.Y) << 32)) * 1099511628211;
                        hash = (hash ^ BitConverter.SingleToUInt32Bits(s.Rotation.W)) * 1099511628211;
                    }
                }
            }
            return hash;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void SteadyState_SleepWakeAndChain_ZeroBytesPerStep()
    {
        var w = World(Sleepy, new FlatGround());
        var trace = w.EnableTrace();
        var arts = new[] { Corpse(w, new Vector3(0, 0.4f, 0), Lying(0.3f)), Corpse(w, new Vector3(0.2f, 0.8f, 0.1f), Lying(1.4f)) };
        var pusher = w.CreateArticulation(1);
        var kb = w.Body(pusher, 0);
        w.SetShape(kb, 5, 0.1f, 0.3f);
        w.Place(kb, new Vector3(3, 0.3f, 0), Quaternion.Identity);
        PhysicsSettings slippery = Sleepy with { StaticFriction = 0.6f }, sticky = Sleepy with { StaticFriction = 0.7f };   // заранее: запись настроек — без выделений
        void Cycle(int t)
        {
            if (t % 240 == 120) w.ApplyHit(w.Body(arts[0], 0), w.Get(w.Body(arts[0], 0)).Position, new Vector3(0, 1, 0), 0, ArticulationHandle.None);
            if (t % 240 == 200) w.Settings = t % 480 < 240 ? slippery : sticky;
            w.Step(Dt);
        }
        for (int t = 0; t < 960; t++) Cycle(t);   // прогрев: сон, пробуждения, цепочка, настройки
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 960; t < 1920; t++) Cycle(t);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"960 тиков сна и пробуждений: {bytes} Б; засыпаний {trace.Run.Sleeps}, пробуждений {trace.Run.WakesTotal}");
        Assert.True(trace.Run.Sleeps >= 4 && trace.Run.WakesTotal >= 4, "сон и пробуждения не случились — тест ничего не проверил");
        Assert.True(bytes == 0, $"сон выделил {bytes} Б");
    }

    [Fact]
    public void Trace_WakesByReason_WindowAndRun_Summary()
    {
        var (w, trace, art, body) = SleepingCorpse();
        trace.ClearWindow();
        w.Wake(art);
        w.Step(Dt);
        var window = trace.Window;
        Assert.Equal((1, 1), (window.Wakes(WakeReason.Command), window.WakesTotal));
        Assert.Contains("сон: засыпаний", trace.Run.Summary("прогон"));
        trace.ClearWindow();
        Assert.Equal(0, trace.Window.WakesTotal);
        Assert.True(trace.Run.WakesTotal >= 1, "прогон окно не чистит");
        _ = body;
    }

    // ——— Замер: куча ———

    [Fact]
    public void Pile32_FallsAsleep_SleepingPileStepIsCheap()
    {
        // куча 32 трупов теста сведения (8 слоёв по 4 над кромкой ступени) со сном игры: засыпает; шаг спящей кучи — в выводе
        // (Release — docs/history/engine-physics.md, «7з-3»)
        var ground = new StepGround(new Vector3(0, -1, -3), new Vector3(3, 0.2f, 3));
        var w = new PhysicsWorld(settings: Sleepy);
        w.SetGround(ground);
        var trace = w.EnableTrace();
        var rng = new Random(32);
        for (int k = 0; k < 32; k++)
        {
            int layer = k / 4, column = k % 4;
            var at = new Vector3(-1.4f + (column % 2) * 0.9f + TestBodies.RandomVector(rng, 0.2f).X, 0.6f + layer * 0.45f,
                -0.6f + (column / 2) * 1.2f + TestBodies.RandomVector(rng, 0.2f).Z);
            Corpse(w, at, Lying(rng.NextSingle() * MathF.Tau));
        }
        var sw = new Stopwatch();
        double fallMs = 0;
        int all = -1;
        for (int t = 0; t < 30 * 120 && all < 0; t++)
        {
            sw.Restart();
            w.Step(Dt);
            sw.Stop();
            if (t < 240) fallMs += sw.Elapsed.TotalMilliseconds;
            if (w.AsleepCount == 32) all = t;
        }
        string wakes = $"пробуждений {trace.Run.WakesTotal} (касанием {trace.Run.Wakes(WakeReason.Touch)}, цепочкой {trace.Run.Wakes(WakeReason.Chain)}), " +
                       $"засыпаний {trace.Run.Sleeps}";
        w.DisableTrace();   // замер шага — без трассы (с ней шаг обходит тела ради сводок)
        long before = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (int t = 0; t < 600; t++) w.Step(Dt);
        sw.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"куча 32 трупов: падение (2 с, с трассой) — {fallMs / 240:F3} мс на шаг; все уснули через {all / 120f:F2} с; спящая куча — " +
                         $"{sw.Elapsed.TotalMilliseconds / 600:F4} мс на шаг, {bytes} Б за 600 шагов; {wakes}");
        Assert.True(all > 0, "куча не уснула за 30 с");
        Assert.Equal(32, w.AsleepCount);
        Assert.True(bytes == 0, $"спящая куча выделила {bytes} Б");
    }

    // ——— Помощники ———

    /// <summary>Шаги до сна сочленения (не больше <paramref name="max"/>); номер тика, на котором уснуло (−1 — не уснуло).</summary>
    private static int StepUntilAsleep(PhysicsWorld w, ArticulationHandle art, int max)
    {
        for (int t = 1; t <= max; t++)
        {
            w.Step(Dt);
            if (w.IsAsleep(art)) return t;
        }
        return -1;
    }

    private static BodyState[] States(PhysicsWorld w, ArticulationHandle art)
    {
        var s = new BodyState[w.BodyCount(art)];
        for (int k = 0; k < s.Length; k++) s[k] = w.Get(w.Body(art, k));
        return s;
    }

    /// <summary>Спящая капсула (5 кг, r 0,1, полудлина 0,3) вдоль X на ровной земле; трасса включена.</summary>
    private static (PhysicsWorld World, PhysicsTrace Trace, ArticulationHandle Sleeper) SleepingCapsule()
    {
        var w = World(Sleepy, new FlatGround());
        var trace = w.EnableTrace();
        var art = w.ArticulationOf(Simulated(w, 5, 0.1f, 0.3f, new Vector3(0, 0.1f, 0), AxisAlong(Vector3.UnitX)));
        Assert.True(StepUntilAsleep(w, art, 240) > 0, "капсула на земле не уснула");
        trace.Clear();
        return (w, trace, art);
    }

    /// <summary>Спящий труп на ровной земле, тело — таз; трасса включена, накопители чистые.</summary>
    private static (PhysicsWorld World, PhysicsTrace Trace, ArticulationHandle Art, BodyHandle Pelvis) SleepingCorpse()
    {
        var w = World(Sleepy, new FlatGround());
        var trace = w.EnableTrace();
        var art = Corpse(w, new Vector3(0, 0.3f, 0), Lying(0.5f));
        Assert.True(StepUntilAsleep(w, art, 720) > 0, "труп на земле не уснул");
        trace.Clear();
        return (w, trace, art, w.Body(art, 0));
    }

    /// <summary>Труп теста сведения: боец стойки лёжа, суставы по этой позе, запястье выключено (как у трупа игры).</summary>
    private static ArticulationHandle Corpse(PhysicsWorld w, Vector3 origin, Quaternion turn)
    {
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
        for (int i = 0; i < 14; i++)
        {
            var b = w.Body(art, i);
            var s = w.Get(b);
            w.Place(b, origin + Vector3.Transform(s.Position, turn), Quaternion.Normalize(turn * s.Rotation));
        }
        w.SetJoints(art, JointsRig.FighterJoints(w, art));
        w.SetJointEnabled(art, 10, false);
        return art;
    }

    private static Quaternion Lying(float yaw) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
}
