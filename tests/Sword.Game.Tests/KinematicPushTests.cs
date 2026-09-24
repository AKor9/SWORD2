using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 7к этапа 7 — толчки живых и удары по трупам, свои тесты свойств (со SWORD не сравниваем): метки толчков (клинок в ударе — проходом),
/// отдача в корень (доля, масса, предел — игрок не теряет управления), живой проходит сквозь труп, теряя немного скорости, удар по трупу — импульс
/// в часть с потолком Δv и из бюджета прохода, отдача атакующему (игроку слабее), серия ударов не разгоняет труп выше потолков, смертельный удар
/// толкает падающий труп, клинки врагов толкают трупы врагов без урона, причинность импульса и отдачи — от попадания, повтор реплея, инварианты
/// на неисправностях. Контент — синтетический (<see cref="TestAssets"/>: его удары клинком никого не задевают — касание клинка по трупу здесь
/// подаётся системой теста <see cref="InjectContact"/> тем же путём Resolve); настоящие удары по трупу, s1, s3, s6 на паке —
/// <see cref="KinematicPushContentTests"/>.
/// </summary>
public sealed class KinematicPushTests(ITestOutputHelper output)
{
    private static RagdollCorpseTests.Game Create(string sets, params (string Stage, ISystem System)[] systems) =>
        RagdollCorpseTests.Create(TestAssets.Assets, sets, systems);

    private static Entity Fighter(Simulation sim, int slot) => RagdollCorpseTests.Fighter(sim, slot);

    private static ArticulationHandle Body(Simulation sim, Entity e) => RagdollCorpseTests.Body(sim, e);

    private static void Clean(Simulation sim) => RagdollCorpseTests.Clean(sim);

    private static string F(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Касание клинка бьющего места <paramref name="attacker"/> по части <paramref name="part"/> бойца места <paramref name="victim"/> — в
    /// Combat после проходов клинка, в тиках, где <paramref name="when"/> и бьющий в ударе с кончиком не медленнее 3,5 м/с (синтетические
    /// удары клинком никого не задевают). Точка — на оси части, в четверти полудлины от центра (удар с плечом), направление — от корня бьющего к
    /// точке по земле, скорость — кончика тика, урон <paramref name="damage"/>, номер удара — бьющего: дальше — тот же путь Resolve, что у
    /// касания 6г; касаний на удар — не больше <paramref name="perStrike"/>. Проходы пар (<see cref="StrikePairs"/>) касание не трогает —
    /// <c>combat.hits</c> в таких тестах выключен.
    /// </summary>
    internal sealed class InjectContact(int attacker, int victim, int part, Func<ulong, bool> when, IPhysicsWorld[] physics, float damage = 60.8f,
        int perStrike = 2) : IContinuousSystem
    {
        public readonly List<(ulong Tick, float Speed, Vector3 Dir, Vector3 Point, Vector3 PointVelocity)> Sent = [];
        private int _serial = -1, _count;

        public SystemLabel Label { get; } = new($"test.inject-contact.{attacker}.{victim}.{part}");

        public void Setup(SystemSetup s) =>
            s.Label(Label).After(GameLabels.StrikeDetection).Reads<AnimatorAction>().Reads<Blade>().Emits<StrikeContact>();

        public void Run(in SystemContext ctx)
        {
            var world = ctx.World;
            var time = world.Resource<SimTime>();
            if (!when(time.Tick)) return;
            var a = FighterIn(world, attacker);
            var v = FighterIn(world, victim);
            var action = world.Get<AnimatorAction>(a);
            var blade = world.Get<Blade>(a);
            float speed = Vector3.Distance(blade.Tip, blade.PrevTip) / time.Dt;
            if (action.Kind != ActionKind.Attack || speed < DamageRules.MinStrikeSpeed) return;
            if (action.AttackSerial != _serial) (_serial, _count) = (action.AttackSerial, 0);
            if (_count++ >= perStrike) return;   // касаний на удар — не больше perStrike (первые тики с быстрым кончиком)
            var p = physics[0];
            var s = p.Get(p.Body(FighterBodies.Handle(world.Get<BodyRef>(v)), BodySegs.Body(part)));
            var point = s.Position + s.Axis * (0.25f * s.HalfLength);
            var dir = point - world.Get<Transform>(a).Position;
            dir.Y = 0;
            dir = Vector3.Normalize(dir);
            Sent.Add((time.Tick, speed, dir, point, s.PointVelocity(point)));
            ctx.Commands.Emit(new StrikeContact
            {
                Id = new ContactId(time.Tick, 100 + Sent.Count), Attacker = a, Victim = v, Part = part, Point = point, Dir = dir, Blade = dir,
                Speed = speed, Kind = HitKind.Slash, Damage = damage, AttackSerial = action.AttackSerial,
            });
        }

        internal static Entity FighterIn(World world, int slot)
        {
            foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<FighterSlot>()))
                for (int i = 0; i < chunk.Count; i++)
                    if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
            return Entity.Null;
        }
    }

    // ── отдача: доля, масса, предел ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Отдача в корень (<see cref="KinematicPush.RootDelta"/>): горизонталь импульса × доля / масса бойца (74,8 кг; вертикаль не идёт), игроку —
    /// четверть; вдоль себя отдача уводит скорость корня от заданной не дальше предела — уже увела — ничего, против отклонения — целиком.
    /// Масса удара — 4,65 кг (клинок, предплечье, половина плеча).
    /// </summary>
    [Fact]
    public void RootDelta_HorizontalShareOverFighterMass_LimitedDeviationFromCommand()
    {
        Assert.Equal(74.8f, KinematicPush.FighterMass, 3);
        Assert.Equal(4.65f, KinematicPush.StrikeMass, 4);
        var j = new Vector3(-7.48f, 50, 3.74f);
        var enemy = KinematicPush.RootDelta(j, 1, Vector3.Zero, Vector3.Zero, 10);
        var player = KinematicPush.RootDelta(j, 0.25f, Vector3.Zero, Vector3.Zero, 10);
        Assert.Equal(-0.1f, enemy.X, 5);
        Assert.Equal(0.05f, enemy.Z, 5);
        Assert.Equal(0, enemy.Y);
        Assert.Equal(enemy * 0.25f, player);
        // предел 1 м/с: бег 4,2 вперёд, отдача назад на 3 м/с — уводит до 3,2 (не дальше 1 от заданной)
        var run = new Vector3(0, 0, 4.2f);
        Assert.Equal(-1f, KinematicPush.RootDelta(new Vector3(0, 0, -3 * 74.8f), 1, run, run, 1).Z, 5);
        // уже отстал на 0,95 — ещё 0,05; отстал на 1,2 — ничего
        Assert.Equal(-0.05f, KinematicPush.RootDelta(new Vector3(0, 0, -74.8f), 1, run - new Vector3(0, 0, 0.95f), run, 1).Z, 4);
        Assert.Equal(Vector3.Zero, KinematicPush.RootDelta(new Vector3(0, 0, -74.8f), 1, run - new Vector3(0, 0, 1.2f), run, 1));
        // отдача против отклонения (боец отстал, отдача вперёд) — целиком
        Assert.Equal(0.5f, KinematicPush.RootDelta(new Vector3(0, 0, 0.5f * 74.8f), 1, run - new Vector3(0, 0, 1.2f), run, 1).Z, 5);
        Assert.Equal(Vector3.Zero, KinematicPush.RootDelta(new Vector3(0, 9, 0), 1, Vector3.Zero, Vector3.Zero, 1));
        Assert.Equal(Vector3.Zero, KinematicPush.RootDelta(new Vector3(float.NaN, 0, 1), 1, Vector3.Zero, Vector3.Zero, 1));
        Assert.Equal(Vector3.Zero, KinematicPush.RootDelta(new Vector3(1, 0, 1), 0, Vector3.Zero, Vector3.Zero, 1));
    }

    // ── метки толчков ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Метки толчков (PrePhysics): тело клинка игрока толкает проходом ровно в тиках удара, вне удара и остальные 13 тел — телом; удар бьющего —
    /// в физике каждый тик удара: проходы бьющего ко всем сочленениям несут номер удара (<see cref="AnimatorAction.AttackSerial"/>). Инвариант
    /// <c>physics.sources</c> чист.
    /// </summary>
    [Fact]
    public void Sources_BladePassOnlyInStrike_StrikePreparedForAllPairs()
    {
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false");
        var player = Fighter(sim, 0);
        var a = Body(sim, player);
        int striking = 0, passTicks = 0;
        for (int t = 0; t < 200; t++)
        {
            sim.Enqueue(new PlayerCommand { Attack = t == 30 });
            sim.Step();
            var action = sim.World.Get<AnimatorAction>(player);
            bool strike = KinematicPush.Striking(action, sim.World.Get<Blade>(player));
            var blade = physics.Get(physics.Body(a, BodySegs.Body(BodySegs.Weapon)));
            Assert.Equal(strike ? KinematicSource.Pass : KinematicSource.Body, blade.Source);
            for (int k = 0; k < BodySegs.Bodies; k++)
                if (k != BodySegs.Body(BodySegs.Weapon)) Assert.Equal(KinematicSource.Body, physics.Get(physics.Body(a, k)).Source);
            if (!strike) continue;
            striking++;
            foreach (int slot in (ReadOnlySpan<int>)[1, 2])
                Assert.Equal(action.AttackSerial, physics.Pass(a, Body(sim, Fighter(sim, slot))).Strike);
            if (blade.Source == KinematicSource.Pass) passTicks++;
        }
        Assert.True(striking > 20, $"удар — {striking} тиков");
        Assert.Equal(striking, passTicks);
        Clean(sim);
    }

    // ── живой сквозь труп ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// s3 (синтетический риг): враг в (0; 4) убит, через 2 с игрок бежит вперёд (намерение сцены 4,2 м/с) сквозь труп — скорость корня у трупа
    /// падает немного (≥ 4,0; замер 4,09), но падает; курс прежний, игрок пробегает труп; труп сдвигается (центр масс ≥ 5 см; замер 15 см);
    /// импульс толчка за проход ≤ бюджета пары × время контакта (сила ≤ <c>ragdoll.pushForce</c>); доля бюджета пары за подшаг ≤ 1; отдача
    /// игрока не уводит скорость дальше предела. Инварианты чисты (кроме диагностических — они в тесте на паке).
    /// </summary>
    [Fact]
    public void LivingWalksThroughCorpse_LosesLittleSpeed_KeepsControl_PushWithinBudget()
    {
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false");
        var trace = physics.EnableTrace();
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 4, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        while (sim.Tick < 241) sim.Step();
        var player = Fighter(sim, 0);
        var enemy = Fighter(sim, 1);
        var rules = sim.World.Resource<RagdollRules>();
        var com0 = RagdollCorpseTests.Totals(sim, enemy).Com;
        trace.ClearWindow();
        int contact = 0;
        float push = 0, before = 0, near = 99;
        for (int t = 0; t < 360; t++)
        {
            sim.Enqueue(new SceneCommand { Action = SceneAction.PlayerIntent, Velocity = new Vector2(0, 4.2f), Facing = 0 });
            sim.Step();
            float tick = trace.Last.Source(PhysicsSource.PushBody).Applied + trace.Last.Source(PhysicsSource.PushPass).Applied;
            if (tick > 0) contact++;
            push += tick;
            Assert.True(tick <= rules.PushForce * sim.Dt * 1.001f, F($"толчок за тик {tick} Н·с больше F·dt"));
            var v = sim.World.Get<RootVelocity>(player).Value;
            float dz = sim.World.Get<Transform>(player).Position.Z - RagdollCorpseTests.Totals(sim, enemy).Com.Z;
            if (dz < -1.5f) before = MathF.Max(before, v.Z);
            else if (dz < 1) near = MathF.Min(near, v.Z);
            Assert.True(MathF.Abs(sim.World.Get<Transform>(player).Facing) < 1e-3f);
        }
        float moved = Vector3.Distance(com0, RagdollCorpseTests.Totals(sim, enemy).Com);
        output.WriteLine(F($"s3: скорость до трупа {before:F3}, у трупа не ниже {near:F3} м/с; труп сдвинут на {moved:F3} м; толчок {push:F2} Н·с за {contact} тиков контакта (бюджет × время {rules.PushForce * contact * sim.Dt:F2}); доля пары {trace.Window.PairShare:F3}, выталкивание {trace.Window.PushPopSpeed:F4} м/с; отдача игроку — до {trace.Window.ReactionMax:F2} Н·с за тик"));
        Assert.Equal(4.2f, before, 3);
        Assert.InRange(near, 4.0f, 4.19f);
        Assert.True(4.2f - near <= rules.RecoilMaxSpeed, $"скорость ушла от заданной на {4.2f - near}");
        Assert.True(sim.World.Get<Transform>(player).Position.Z > 11, "не пробежал труп");
        Assert.True(moved >= 0.05f, $"труп сдвинут на {moved} м");
        Assert.True(contact > 5 && push <= rules.PushForce * contact * sim.Dt, F($"импульс {push} за {contact} тиков"));
        Assert.True(trace.Window.PairShare <= 1 + 1e-3f);
        Assert.True(sim.World.Get<Recoil>(player).Tick > 241, "отдачи не было");
        Clean(sim);
    }

    // ── удар по трупу ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Удар по трупу (касание клинка в ударе — через <see cref="InjectContact"/>): попадание по трупу, импульс в часть — Δv точки вдоль удара
    /// ≤ 0,5 × скорость удара и ровно на столько изменилась скорость точки; импульс ≤ того, что хотел удар, и ≤ бюджета прохода; второй удар того
    /// же прохода получает только остаток (сумма за проход ≤ бюджета); отдача — атакующему, против приложенного, в его корень: игроку — четверть
    /// от врагу (на тот же импульс). Серия ударов по трупу не разгоняет тела выше потолков профиля. Проба в труп импульса не даёт.
    /// </summary>
    [Fact]
    public void HitOnCorpse_ImpulseCappedByDvAndPass_RecoilPushesAttacker_PlayerWeaker()
    {
        var holder = new IPhysicsWorld[1];
        // игрок бьёт ударом на тике 241 и ещё раз в следующем ударе; касания — в первых двух тиках каждого удара с быстрым кончиком
        // первый удар — в торс (упор в бюджет прохода), второй — в предплечье (упор в потолок Δv)
        var hits = new InjectContact(0, 1, BodySegs.Torso, t => t is > 241 and < 350, holder);
        var arm = new InjectContact(0, 1, BodySegs.FArmL, t => t >= 350, holder, perStrike: 1);
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Combat, hits), (GameStages.Combat, arm));
        holder[0] = physics;
        sim.Invariants.SetEnabled("combat.hits", false);   // касание теста мимо проходов 6г
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 3, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        var player = Fighter(sim, 0);
        var enemy = Fighter(sim, 1);
        while (sim.Tick < 241)
        {
            sim.Enqueue(new PlayerCommand { HasGaze = true, Gaze = new Vector3(0, 0, 3) });
            sim.Step();
        }
        var a = Body(sim, player);
        var corpse = Body(sim, enemy);
        var share = physics.Settings.HitDvShare;
        var rules = sim.World.Resource<RagdollRules>();
        float spentFirst = 0, bodyMax = 0;
        int impulses = 0, recoils = 0;
        var stats = sim.World.Resource<ArenaStats>();
        for (int t = 241; t < 700; t++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(new PlayerCommand { HasGaze = true, Gaze = new Vector3(0, 0, 3), Attack = t is 241 or 400 });
            int sent = hits.Sent.Count, sentArm = arm.Sent.Count;
            sim.Step();
            bodyMax = MathF.Max(bodyMax, RagdollCorpseTests.MaxSpeed(physics, corpse));
            if (hits.Sent.Count == sent && arm.Sent.Count == sentArm) continue;
            var from = hits.Sent.Count > sent ? hits : arm;
            int part = from == hits ? BodySegs.Torso : BodySegs.FArmL;
            var (tick, speed, dir, point, pointBefore) = from.Sent[^1];
            var hit = Assert.Single(sim.Export.Frame<Event<Hit>>().ToArray()).Record.Value;
            Assert.True(hit.Corpse && !hit.Killed);
            var h = sim.World.Get<HitImpulse>(enemy);
            Assert.Equal((new ContactId(tick, 100 + from.Sent.Count), player, part, true), (h.Contact, h.Attacker, h.Part, h.Corpse));
            float j = h.Impulse.Length();
            var pass = physics.Pass(a, corpse);
            output.WriteLine(F($"тик {tick}: скорость удара {speed:F2}, хотел {h.Wanted:F2}, приложено {j:F3} Н·с, Δv {h.Dv:F3} м/с; проход {pass.Number}: бюджет {pass.Potential:F2}, остаток {pass.Remaining:F2}"));
            Assert.True(j <= h.Wanted + 1e-4f && j <= pass.Potential + 1e-4f);
            Assert.True(h.Dv <= share * speed + 1e-3f, F($"Δv {h.Dv} > {share} × {speed}"));
            Assert.True(j > 0 && Vector3.Dot(h.Impulse, dir) > 0.999f * j);
            // скорость точки части изменилась ровно на Δv вдоль удара (после удара до конца тика шага нет)
            float dv = Vector3.Dot(physics.Get(physics.Body(corpse, BodySegs.Body(part))).PointVelocity(point) - pointBefore, dir);
            Assert.Equal(h.Dv, dv, 3);
            impulses++;
            if (tick < 300) spentFirst += j;
            if (tick < 300) Assert.True(spentFirst <= pass.Potential + 1e-3f, F($"за проход {spentFirst} > бюджета {pass.Potential}"));
            else Assert.Equal(share * speed, h.Dv, 3);   // второй удар — в лёгкое предплечье: упор в потолок Δv, а не в бюджет прохода
            // отдача атакующему: −приложенный (толчков тела нет — труп в 3 м), в корень — четверть горизонтали / масса
            var recoil = sim.World.Get<Recoil>(player);
            Assert.Equal(tick, recoil.Tick);
            Assert.True(Vector3.Distance(-h.Impulse, recoil.Impulse) < 1e-4f, $"{recoil.Impulse} против {-h.Impulse}");
            var flat = new Vector3(-h.Impulse.X, 0, -h.Impulse.Z);
            if (recoil.Velocity.Length() < rules.RecoilMaxSpeed - 1e-3f)
                Assert.True(Vector3.Distance(flat * (rules.PlayerRecoil / KinematicPush.FighterMass), recoil.Velocity) < 1e-5f);
            recoils++;
        }
        Assert.True(impulses >= 3 && recoils == impulses && arm.Sent.Count >= 1, $"ударов {impulses}");
        var dyn = physics.GetDynamics(physics.Get(physics.Body(corpse, 0)).Profile);
        Assert.True(bodyMax <= dyn.MaxSpeed, $"тело трупа {bodyMax} м/с (потолок {dyn.MaxSpeed})");
        var after = sim.World.Resource<ArenaStats>();
        Assert.Equal((stats.Kills, stats.Hits, stats.CorpseHits + impulses), (after.Kills, after.Hits, after.CorpseHits));
        // проба в труп — попадание без импульса (скорости удара нет)
        var last = sim.World.Get<HitImpulse>(enemy);
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.Head, Damage = 30, Kind = HitKind.Slash });
        sim.Step();
        Assert.Equal(last, sim.World.Get<HitImpulse>(enemy));
        Assert.Equal(after.CorpseHits + 1, sim.World.Resource<ArenaStats>().CorpseHits);
        Clean(sim);

        // враг бьёт труп игрока: на тот же импульс отдача врагу вчетверо больше, чем игроку
        var enemyHolder = new IPhysicsWorld[1];
        var enemyHits = new InjectContact(1, 0, BodySegs.Torso, t => t > 241, enemyHolder);
        var (esim, ephysics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Combat, enemyHits));
        enemyHolder[0] = ephysics;
        esim.Invariants.SetEnabled("combat.hits", false);
        RagdollCorpseTests.Step(esim, RagdollCorpseTests.Place(1, 0, 3, MathF.PI));
        RagdollCorpseTests.Step(esim, RagdollCorpseTests.Kill(0));
        var attacker = Fighter(esim, 1);
        for (int t = 2; t < 400 && enemyHits.Sent.Count == 0; t++)
        {
            esim.Enqueue(new EnemyCommand { Slot = 1, Velocity = Vector2.Zero, FacingGoal = MathF.PI, Attack = t == 241 ? 1 : 0 });
            esim.Step();
        }
        Assert.NotEmpty(enemyHits.Sent);
        var er = esim.World.Get<Recoil>(attacker);
        float enemyPerImpulse = er.Velocity.Length() / new Vector2(er.Impulse.X, er.Impulse.Z).Length();
        Assert.Equal(rules.EnemyRecoil / KinematicPush.FighterMass, enemyPerImpulse, 5);
        Assert.Equal(0.25f, rules.PlayerRecoil / rules.EnemyRecoil);
        Clean(esim);
    }

    /// <summary>
    /// Смертельный удар толкает падающий труп: касание убивает живого — импульс в его ещё кинематическую часть (до перехода в труп в Settle) с
    /// потолком Δv, без прохода и отдачи; после перехода скорость части — от удара (урезана переходом до 6 м/с); смерть пробой (без скорости
    /// удара) импульса не даёт.
    /// </summary>
    [Fact]
    public void LethalHit_PushesFallingCorpse_WithoutPassAndRecoil()
    {
        var holder = new IPhysicsWorld[1];
        var lethal = new InjectContact(0, 1, BodySegs.Head, t => t > 241, holder, damage: 200);
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false", (GameStages.Combat, lethal));
        holder[0] = physics;
        sim.Invariants.SetEnabled("combat.hits", false);
        sim.Enqueue(RagdollCorpseTests.Place(2, 2, 3, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, -2, 3, MathF.PI));
        var player = Fighter(sim, 0);
        var one = Fighter(sim, 1);
        var two = Fighter(sim, 2);
        void Hold(int t)
        {
            sim.Enqueue(new EnemyCommand { Slot = 1, Velocity = Vector2.Zero, FacingGoal = MathF.PI });
            sim.Enqueue(new EnemyCommand { Slot = 2, Velocity = Vector2.Zero, FacingGoal = MathF.PI });
            sim.Enqueue(new PlayerCommand { Attack = t == 241 });
        }
        while (sim.Tick < 241) { Hold((int)sim.Tick); sim.Step(); }
        while (lethal.Sent.Count == 0 && sim.Tick < 400) { Hold((int)sim.Tick); sim.Step(); }
        Assert.Single(lethal.Sent);
        var (tick, speed, dir, point, before) = lethal.Sent[0];
        Assert.True(sim.World.Has<Dead>(one));
        var h = sim.World.Get<HitImpulse>(one);
        Assert.Equal((false, BodySegs.Head), (h.Corpse, h.Part));
        Assert.True(h.Dv > 0.3f && h.Dv <= physics.Settings.HitDvShare * speed + 1e-3f, F($"Δv {h.Dv}, скорость удара {speed}"));
        var head = physics.Get(physics.Body(Body(sim, one), BodySegs.Body(BodySegs.Head)));
        Assert.Equal(BodyMode.Simulated, head.Mode);
        float along = Vector3.Dot(head.PointVelocity(point) - before, dir);
        output.WriteLine(F($"смертельный удар: скорость удара {speed:F2}, Δv точки {h.Dv:F3}, после перехода вдоль удара {along:F3} м/с, скорость головы {head.Velocity.Length():F3}"));
        Assert.True(along > 0.2f, $"вдоль удара {along}");
        Assert.True(head.Velocity.Length() <= sim.World.Resource<RagdollRules>().DeathMaxSpeed + 1e-4f);
        Assert.NotEqual(tick, sim.World.Get<Recoil>(player).Tick);   // без отдачи: часть была кинематической
        Assert.Equal(default, physics.Pass(Body(sim, player), Body(sim, one)).Number);   // и без прохода
        // смерть пробой — импульса нет
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.Head, Damage = 200, Kind = HitKind.Slash });
        sim.Step();
        Assert.True(sim.World.Has<Dead>(two));
        Assert.Equal(-1, sim.World.Get<HitImpulse>(two).Part);
        Clean(sim);
    }

    // ── причинность, реплей ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Причинность (6д, 7к): импульс в тело трупа и отдача атакующему — от попадания: <c>state.why(труп, HitImpulse)</c> — Set ← Hit ←
    /// касание; <c>state.why(игрок, Recoil)</c> — Set ← Hit того же удара.
    /// </summary>
    [Fact]
    public void Causality_ImpulseAndRecoil_FromHit()
    {
        var holder = new IPhysicsWorld[1];
        var hits = new InjectContact(0, 1, BodySegs.Torso, t => t > 241, holder);
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Combat, hits));
        holder[0] = physics;
        sim.Invariants.SetEnabled("combat.hits", false);
        sim.EnableCausality();
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 3, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        for (int t = 2; t < 400 && hits.Sent.Count == 0; t++)
        {
            sim.Enqueue(new PlayerCommand { Attack = t == 241 });
            sim.Step();
        }
        Assert.NotEmpty(hits.Sent);
        var enemy = Fighter(sim, 1);
        var impulse = Assert.Single(CausalityTests.Why(sim, enemy, nameof(HitImpulse), null).Changes);
        Assert.Equal(CauseKind.Set, impulse.Change.Record.Kind);
        Assert.Equal("core.hit-apply@Resolve", impulse.Change.System);
        var hit = impulse.Chain.First(s => s.ValueType == typeof(Hit));
        Assert.True(MemoryMarshal.Read<Hit>(hit.After).Corpse);
        Assert.Contains(impulse.Chain, s => s.ValueType == typeof(StrikeContact));
        var recoil = Assert.Single(CausalityTests.Why(sim, Fighter(sim, 0), nameof(Recoil), null).Changes);
        Assert.Equal("core.root-recoil@React", recoil.Change.System);
        Assert.Equal(hit.Record.Id, recoil.Chain[0].Record.Id);
        output.WriteLine(CausalityTests.Describe(impulse));
        output.WriteLine(CausalityTests.Describe(recoil));
    }

    /// <summary>
    /// Повтор реплея — те же хеши всех участников (и <c>physics</c>: проходы, отдача): труп, живой бежит сквозь него (намерение сцены), боец
    /// поставлен на трупе, враг бьёт труп клинком, удары игрока по трупу (касание теста — в обоих прогонах).
    /// </summary>
    [Fact]
    public void Replay_PushesHitsAndRecoil_RepeatWithSameHashes()
    {
        IContinuousSystem Hits(IPhysicsWorld[] holder) => new InjectContact(0, 1, BodySegs.Torso, t => t > 300, holder);
        var holder = new IPhysicsWorld[1];
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false", (GameStages.Combat, Hits(holder)));
        holder[0] = physics;
        sim.Invariants.SetEnabled("combat.hits", false);
        var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
        sim.Enqueue(RagdollCorpseTests.Place(2, 1, 5, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 3, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        for (int t = 2; t < 700; t++)
        {
            if (t is 300 or 500) sim.Enqueue(new PlayerCommand { Attack = true });
            if (t is > 150 and < 280) sim.Enqueue(new SceneCommand { Action = SceneAction.PlayerIntent, Velocity = new Vector2(0, 2), Facing = 0 });
            if (t == 400) sim.Enqueue(RagdollCorpseTests.Place(2, 0, 3.3f, 0));
            sim.Enqueue(new EnemyCommand { Slot = 2, Velocity = Vector2.Zero, Attack = t == 420 ? 2 : 0 });
            sim.Step();
        }
        Assert.True(sim.World.Resource<ArenaStats>().CorpseHits >= 2);
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        var again = Simulation.Build(replay.CreateConfig(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new RagdollCorpseTests.Probe(p => holder[0] = p, (GameStages.Combat, Hits(holder)))]);
        again.Invariants.SetEnabled("combat.hits", false);
        var player = again.Play(replay);
        while (again.Tick < replay.Ticks) again.Step();
        Assert.Null(player.Divergence);
        Assert.Equal((int)replay.Ticks + 1, player.HashesChecked);
        Assert.Equal(sim.HashState().Total, again.HashState().Total);
        Clean(sim);
    }

    // ── инварианты-неисправности ─────────────────────────────────────────────────────────────────────────────────────

    public enum PushFault { Sources, HitSource, HitImpulse }

    /// <summary>
    /// Порча: на тике <paramref name="tick"/> — метка клинка игрока вне удара — проход (PrePhysics, после меток) или импульс удара с Δv выше доли
    /// скорости удара (Resolve, после попаданий); в тике касания теста <paramref name="hits"/> — клинок бьющего толкает не проходом (Combat, до
    /// Resolve: попадание дал бы «физический клинок»).
    /// </summary>
    private sealed class Fault(PushFault fault, ulong tick, IPhysicsWorld[] physics, InjectContact hits) : IContinuousSystem
    {
        public ulong Faulted { get; private set; } = ulong.MaxValue;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("test.push-fault"));
            if (fault == PushFault.Sources) s.After(GameLabels.KinematicSource);
            if (fault == PushFault.HitSource) s.After(hits.Label);
            if (fault == PushFault.HitImpulse) s.After(GameLabels.HitApply).Sets<HitImpulse>();
        }

        public void Run(in SystemContext ctx)
        {
            var world = ctx.World;
            ulong now = world.Resource<SimTime>().Tick;
            bool due = fault == PushFault.HitSource ? Faulted == ulong.MaxValue && hits.Sent.Count > 0 && hits.Sent[^1].Tick == now : now == tick;
            if (!due) return;
            Faulted = now;
            var p = physics[0];
            var player = InjectContact.FighterIn(world, 0);
            var blade = p.Body(FighterBodies.Handle(world.Get<BodyRef>(player)), BodySegs.Body(BodySegs.Weapon));
            switch (fault)
            {
                case PushFault.Sources: p.SetKinematicSource(blade, KinematicSource.Pass); break;
                case PushFault.HitSource: p.SetKinematicSource(blade, KinematicSource.Body); break;
                case PushFault.HitImpulse:
                    ctx.Commands.Set(InjectContact.FighterIn(world, 1), new HitImpulse
                    {
                        Contact = new ContactId(now, 0), Attacker = player, Part = BodySegs.Torso, Impulse = Vector3.UnitX, Wanted = 12, Speed = 1, Dv = 3,
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// Инварианты толчков ловят порчу на своём тике: клинок толкает проходом вне удара (<c>physics.sources</c>, после PrePhysics); попадание
    /// клинком, который толкает не проходом, — «физический клинок попаданий не даёт» (<c>ragdoll.hitSource</c>, после Resolve); импульс удара с
    /// Δv выше доли скорости удара (<c>ragdoll.hitImpulse</c>, после Resolve). До порчи — чисто.
    /// </summary>
    [Theory]
    [InlineData(PushFault.Sources, "physics.sources", GameStages.PrePhysics)]
    [InlineData(PushFault.HitSource, "ragdoll.hitSource", GameStages.Resolve)]
    [InlineData(PushFault.HitImpulse, "ragdoll.hitImpulse", GameStages.Resolve)]
    public void PushInvariants_CatchFault_OnItsTick(PushFault fault, string id, string stage)
    {
        var holder = new IPhysicsWorld[1];
        var hits = new InjectContact(0, 1, BodySegs.Torso, t => t > 241, holder);
        var system = new Fault(fault, 120, holder, hits);
        string faultStage = fault switch { PushFault.Sources => GameStages.PrePhysics, PushFault.HitSource => GameStages.Combat, _ => GameStages.Resolve };
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Combat, hits), (faultStage, system));
        holder[0] = physics;
        sim.Invariants.SetEnabled("combat.hits", false);   // касание теста мимо проходов 6г
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 3, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        while (system.Faulted == ulong.MaxValue && sim.Tick < 400)
        {
            if (sim.Invariants.Total == 0) Assert.Equal(0, sim.Invariants.Total);
            sim.Enqueue(new PlayerCommand { Attack = sim.Tick == 241 });
            sim.Step();
        }
        Assert.NotEqual(ulong.MaxValue, system.Faulted);
        var hit = sim.Invariants.Recent().FirstOrDefault(v => v.Id == id);
        Assert.True(hit.Id == id, $"{id} не поймал {fault}; нарушения: {string.Join("; ", sim.Invariants.Recent().Take(3))}");
        Assert.Equal((system.Faulted, stage), (hit.Tick, hit.Stage));
        Assert.DoesNotContain(sim.Invariants.Recent(), v => v.Tick < system.Faulted);
        output.WriteLine($"{fault}: {hit}");
    }

    /// <summary>
    /// Порча попадания (Resolve, после <c>core.hit-apply</c>) на тике <paramref name="tick"/>: попадание по трупу врага 1 без метки «по трупу»
    /// (проба — проходы 6г не сверяются) или попадание клинком от трупа врага 1 в игрока.
    /// </summary>
    private sealed class FakeHit(bool fromCorpse, ulong tick) : IContinuousSystem
    {
        public ulong Fired { get; private set; } = ulong.MaxValue;

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("test.fake-hit")).After(GameLabels.HitApply).Emits<Hit>();

        public void Run(in SystemContext ctx)
        {
            var world = ctx.World;
            ulong now = world.Resource<SimTime>().Tick;
            if (now < tick || Fired != ulong.MaxValue) return;
            Fired = now;
            var player = InjectContact.FighterIn(world, 0);
            var corpse = InjectContact.FighterIn(world, 1);
            ctx.Commands.Emit(new Hit
            {
                Contact = new ContactId(now, 0), Attacker = fromCorpse ? corpse : player, Victim = fromCorpse ? player : corpse, Part = BodySegs.Torso,
                Damage = 10, Dir = Vector3.UnitZ, Kind = HitKind.Slash, SeverAt = 99, Probe = !fromCorpse, Corpse = false,
            });
        }
    }

    /// <summary>
    /// Пересмотренный <c>combat.hits</c> и <c>ragdoll.hitSource</c> (7к) ловят на своём тике (после Resolve): попадание по трупу без метки
    /// «по трупу» (<see cref="Hit.Corpse"/> расходится с «мертва до попаданий тика») и попадание клинком от трупа. До порчи — чисто.
    /// </summary>
    [Theory]
    [InlineData(false, "«по трупу» False")]
    [InlineData(true, "от трупа")]
    public void CorpseHitInvariants_CatchWrongCorpseFlag_AndHitFromCorpse(bool fromCorpse, string text)
    {
        var fake = new FakeHit(fromCorpse, 60);
        var (sim, _) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Resolve, fake));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 3, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        while (fake.Fired == ulong.MaxValue && sim.Tick < 200)
        {
            Assert.Equal(0, sim.Invariants.Total);
            sim.Step();
        }
        Assert.NotEqual(ulong.MaxValue, fake.Fired);
        foreach (string id in (ReadOnlySpan<string>)["combat.hits", "ragdoll.hitSource"])
        {
            var v = sim.Invariants.Recent().FirstOrDefault(r => r.Id == id);
            Assert.True(v.Id == id, $"{id} не поймал; нарушения: {string.Join("; ", sim.Invariants.Recent().Take(3))}");
            Assert.Equal((fake.Fired, GameStages.Resolve), (v.Tick, v.Stage));
            Assert.Contains(text, v.Details);
            output.WriteLine($"{id}: {v}");
        }
    }
}

/// <summary>
/// 7к на эталонном паке (категория Content): настоящие удары клинком по трупу — s1 (удары по трупу серией: попадания по трупу, импульс ≤ бюджета
/// прохода, Δv ≤ доли скорости удара, труп сдвигается и переворачивается, тела — в потолках, причинность импульса до команды игрока), s3 (проход
/// сквозь труп), s6 (постройка бойца на трупе — место наибольшего пересечения), клинки врагов толкают трупы врагов без урона. Числа — в выводе;
/// пороги — ~2× к замеру (README «Рэгдолл», «7к»). Толчки проверяются по трассе физики; выталкивание толчком сверх толкающей точки — с запасом
/// 0,05 м/с (замер — до 1,026 м/с при <c>PopSpeed</c> 1: второй порядок поворота в проекции; <c>physics.pushes</c> 7з с запасом 1e-3 здесь не
/// включается — «Известные ошибки» README «Рэгдолл», «7к»).
/// </summary>
[Trait("Category", "Content")]
public sealed class KinematicPushContentTests(ITestOutputHelper output)
{
    private static CharacterAssets Pak => FeatureManifestsTests.Reference.Value.Assets;

    private static string F(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Выталкивание толчком сверх толкающей точки, м/с, — не больше <c>PopSpeed</c> с этим запасом (второй порядок поворота в проекции, замер до 0,026).</summary>
    private const float PopSlack = 0.05f;

    /// <summary>
    /// s1 — удары по трупу: враг в (0; 1,4) курсом к игроку убит командой — падает к игроку; с 2 с игрок рубит его ударом ЛКМ вправо-вниз (жест
    /// (1; −1)) раз в 0,5 с 6 с. Попадания по трупу (≥ 3; замер 7), у каждого — импульс в часть: Δv ≤ 0,5 × скорость удара (есть упёршиеся в потолок),
    /// импульс ≤ бюджета прохода; отдача — игроку; труп сдвигается (центр масс ≥ 0,5 м; замер 1,0) и переворачивается (таз ≥ 1,2 рад; замер 2,3);
    /// тела — не быстрее потолка профиля; «убито» — 1. Причинность: <c>state.why(труп, HitImpulse)</c> — Hit ← касание ← начало удара ← команда игрока.
    /// </summary>
    [Fact]
    public void S1_StrikesOnCorpse_ImpulseWithinPass_CorpseMovesAndTurns_CausedByPlayerCommand()
    {
        var (sim, physics) = RagdollCorpseTests.Create(Pak, "arena.enemies=1;combat.enemiesAttack=false");
        var trace = physics.EnableTrace();
        sim.EnableCausality();
        var gaze = new Vector3(0, 0, 1.4f);
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 1.4f, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        var player = RagdollCorpseTests.Fighter(sim, 0);
        var enemy = RagdollCorpseTests.Fighter(sim, 1);
        while (sim.Tick < 241)
        {
            sim.Enqueue(new PlayerCommand { HasGaze = true, Gaze = gaze });
            sim.Step();
        }
        var corpse = RagdollCorpseTests.Body(sim, enemy);
        var pelvis0 = physics.Get(physics.Body(corpse, 0)).Rotation;
        var com0 = RagdollCorpseTests.Totals(sim, enemy).Com;
        trace.ClearWindow();
        int hits = 0;
        float turned = 0, bodyMax = 0;
        HitImpulse lastImpulse = default;
        for (int t = 0; t < 720; t++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(new PlayerCommand { HasGaze = true, Gaze = gaze, Attack = t % 60 == 0, Gesture = new Vector2(1, -1) });
            sim.Step();
            bodyMax = MathF.Max(bodyMax, RagdollCorpseTests.MaxSpeed(physics, corpse));
            var q = physics.Get(physics.Body(corpse, 0)).Rotation;
            turned = MathF.Max(turned, 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(q, pelvis0)), 0, 1)));
            foreach (var h in sim.Export.Frame<Event<Hit>>())
            {
                Assert.True(h.Record.Value.Corpse && !h.Record.Value.Killed && !h.Record.Value.Probe);
                hits++;
                var imp = sim.World.Get<HitImpulse>(enemy);
                var pass = physics.Pass(RagdollCorpseTests.Body(sim, player), corpse);
                float j = imp.Impulse.Length();
                output.WriteLine(F($"тик {sim.Tick}: попадание по трупу в {BodySegs.Name(h.Record.Value.Part)}, скорость удара {imp.Speed:F1} м/с, хотел {imp.Wanted:F2}, приложено {j:F2} Н·с, Δv {imp.Dv:F2} м/с; проход {pass.Number}: бюджет {pass.Potential:F2}, остаток {pass.Remaining:F2}; отдача игроку {sim.World.Get<Recoil>(player).Velocity.Length():F4} м/с"));
                Assert.Equal(h.Record.Value.Contact, imp.Contact);
                Assert.True(imp.Corpse && imp.Dv <= physics.Settings.HitDvShare * imp.Speed + 1e-3f);
                Assert.True(j <= imp.Wanted + 1e-4f && j <= pass.Potential + 1e-3f && pass.Potential - pass.Remaining >= j - 1e-3f);
                Assert.Equal(imp.Contact.Tick, sim.World.Get<Recoil>(player).Tick);
                lastImpulse = imp;
            }
        }
        float moved = Vector3.Distance(com0, RagdollCorpseTests.Totals(sim, enemy).Com);
        var stats = sim.World.Resource<ArenaStats>();
        output.WriteLine(F($"s1: попаданий по трупу {hits} (статистика {stats.CorpseHits}, убито {stats.Kills}); труп сдвинут на {moved:F3} м, таз повернулся до {turned:F2} рад; тела до {bodyMax:F2} м/с; доля прохода {trace.Window.PassShare:F3}, доля Δv {trace.Window.HitDvShare:F3}, ударов урезано проходом {trace.Window.HitsBudgeted}; выталкивание {trace.Window.PushPopSpeed:F4} м/с"));
        Assert.True(hits >= 3, $"попаданий по трупу {hits}");
        Assert.Equal((hits, 1, 0), (stats.CorpseHits, stats.Kills, stats.Hits));
        Assert.True(moved >= 0.5f, $"труп сдвинут на {moved} м");
        Assert.True(turned >= 1.2f, $"таз повернулся на {turned} рад");
        Assert.True(trace.Window.HitsCapped > 0, "ни один удар не упёрся в потолок Δv");
        Assert.True(bodyMax <= sim.World.Resource<RagdollRules>().CorpseMaxSpeed, $"тело трупа {bodyMax} м/с");
        Assert.True(trace.Window.PassShare <= 1 + 1e-3f && trace.Window.HitDvShare <= physics.Settings.HitDvShare + 1e-3f);
        Assert.True(trace.Window.PushPopSpeed <= physics.Settings.PopSpeed + PopSlack);
        // причинность: импульс последнего удара — от попадания, до нажатия игрока
        var why = CausalityTests.Why(sim, enemy, nameof(HitImpulse), null);
        var change = why.Changes[0];
        Assert.True(change.Complete, CausalityTests.Describe(change));
        Assert.Equal([typeof(Hit), typeof(StrikeContact), null, typeof(PlayerCommand)], change.Chain.Select(s => s.ValueType));
        Assert.Equal(CombatCauses.AttackStart, change.Chain[2].Record.Note);
        Assert.Equal(lastImpulse.Contact, MemoryMarshal.Read<Hit>(change.Chain[0].After).Contact);
        output.WriteLine(CausalityTests.Describe(change));
        RagdollCorpseTests.Clean(sim);
    }

    /// <summary>
    /// s3 — проход сквозь труп на паке: враг в (0; 4) убит, через 2 с игрок бежит вперёд 4,2 м/с — у трупа скорость корня падает немного
    /// (≥ 4,1; замер 4,15), но падает; труп сдвигается (центр масс ≥ 2 см; замер 4 см); импульс толчка ≤ бюджета × время контакта.
    /// </summary>
    [Fact]
    public void S3_WalkThroughCorpse_LosesLittleSpeed_PushWithinBudget()
    {
        var (sim, physics) = RagdollCorpseTests.Create(Pak, "arena.enemies=1;combat.enemiesAttack=false");
        var trace = physics.EnableTrace();
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 4, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        while (sim.Tick < 241) sim.Step();
        var player = RagdollCorpseTests.Fighter(sim, 0);
        var enemy = RagdollCorpseTests.Fighter(sim, 1);
        var rules = sim.World.Resource<RagdollRules>();
        var com0 = RagdollCorpseTests.Totals(sim, enemy).Com;
        trace.ClearWindow();
        int contact = 0;
        float push = 0, near = 99, comSpeed = 0;
        for (int t = 0; t < 360; t++)
        {
            sim.Enqueue(new SceneCommand { Action = SceneAction.PlayerIntent, Velocity = new Vector2(0, 4.2f), Facing = 0 });
            sim.Step();
            float tick = trace.Last.Source(PhysicsSource.PushBody).Applied + trace.Last.Source(PhysicsSource.PushPass).Applied;
            if (tick > 0) contact++;
            push += tick;
            var tot = RagdollCorpseTests.Totals(sim, enemy);
            comSpeed = MathF.Max(comSpeed, tot.ComVelocity.Length());
            float dz = sim.World.Get<Transform>(player).Position.Z - tot.Com.Z;
            if (dz is >= -1.5f and < 1) near = MathF.Min(near, sim.World.Get<RootVelocity>(player).Value.Z);
        }
        float moved = Vector3.Distance(com0, RagdollCorpseTests.Totals(sim, enemy).Com);
        output.WriteLine(F($"s3 (пак): у трупа скорость корня не ниже {near:F3} м/с; труп сдвинут на {moved:F3} м, центр масс до {comSpeed:F2} м/с; толчок {push:F2} Н·с за {contact} тиков (бюджет × время {rules.PushForce * contact * sim.Dt:F2}); выталкивание {trace.Window.PushPopSpeed:F4}"));
        Assert.InRange(near, 4.1f, 4.195f);
        Assert.True(moved >= 0.02f && comSpeed >= 0.1f, $"труп: сдвиг {moved} м, скорость центра масс {comSpeed}");
        Assert.True(contact > 5 && push <= rules.PushForce * contact * sim.Dt);
        Assert.True(trace.Window.PairShare <= 1 + 1e-3f && trace.Window.PushPopSpeed <= physics.Settings.PopSpeed + PopSlack);
        RagdollCorpseTests.Clean(sim);
    }

    /// <summary>
    /// Место наибольшего пересечения стоящего бойца с телами трупа: вертикальная капсула (0,1…1,7 м, r 0,25) против 14 капсул трупа — сетка ±0,4 м
    /// шагом 0,2 вокруг центра масс (сцена s6 SWORD — перебор постановок); сумма глубин — мера пересечения.
    /// </summary>
    internal static (Vector2 At, float Overlap) BestOverlap(IPhysicsWorld physics, ArticulationHandle corpse, Vector3 com)
    {
        float best = -1;
        Vector2 at = default;
        for (int i = -2; i <= 2; i++)
            for (int j = -2; j <= 2; j++)
            {
                var p = new Vector2(com.X + 0.2f * i, com.Z + 0.2f * j);
                float overlap = 0;
                for (int k = 0; k < BodySegs.Bodies; k++)
                {
                    var s = physics.Get(physics.Body(corpse, k));
                    float d2 = Sword.Engine.Geometry.Segments.SegSeg(new Vector3(p.X, 0.1f, p.Y), new Vector3(p.X, 1.7f, p.Y), s.EndA, s.EndB, out _, out _);
                    overlap += MathF.Max(0, 0.25f + s.Radius - MathF.Sqrt(d2));
                }
                if (overlap > best) (best, at) = (overlap, p);
            }
        return (at, best);
    }

    /// <summary>
    /// s6 — постройка бойца на трупе: враг 1 в (0; 1,5) убит, через 2 с враг 2 поставлен командой в место наибольшего пересечения с телами трупа и
    /// стоит (команда врагу): его тела расталкивают труп не быстрее своей точки + <c>PopSpeed</c> (стоит — 1 м/с) — без разлёта: центр масс трупа
    /// ≤ 1 м/с (замер 0,48), сдвиг ≤ 0,5 м (0,04), тела ≤ 3 м/с (1,6); через 4 с покой (≤ 0,1 Дж; 0,04); враг 2 не отброшен (отдача в пределе).
    /// </summary>
    [Fact]
    public void S6_FighterBuiltOnCorpse_PushesItApartWithoutScatter()
    {
        var (sim, physics) = RagdollCorpseTests.Create(Pak, "arena.enemies=2;combat.enemiesAttack=false");
        var trace = physics.EnableTrace();
        sim.Enqueue(RagdollCorpseTests.Place(2, 6, 6, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 1.5f, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        var enemy = RagdollCorpseTests.Fighter(sim, 1);
        var second = RagdollCorpseTests.Fighter(sim, 2);
        while (sim.Tick < 241)
        {
            sim.Enqueue(new EnemyCommand { Slot = 2, Velocity = Vector2.Zero, FacingGoal = 0 });
            sim.Step();
        }
        var corpse = RagdollCorpseTests.Body(sim, enemy);
        var com0 = RagdollCorpseTests.Totals(sim, enemy).Com;
        var (at, overlap) = BestOverlap(physics, corpse, com0);
        trace.ClearWindow();
        sim.Enqueue(RagdollCorpseTests.Place(2, MathF.Round(at.X, 2), MathF.Round(at.Y, 2), 0));
        float comSpeed = 0, bodyMax = 0, secondMoved = 0;
        for (int t = 0; t < 480; t++)
        {
            sim.Enqueue(new EnemyCommand { Slot = 2, Velocity = Vector2.Zero, FacingGoal = 0 });
            sim.Step();
            comSpeed = MathF.Max(comSpeed, RagdollCorpseTests.Totals(sim, enemy).ComVelocity.Length());
            bodyMax = MathF.Max(bodyMax, RagdollCorpseTests.MaxSpeed(physics, corpse));
            var root = sim.World.Get<Transform>(second).Position;
            secondMoved = MathF.Max(secondMoved, Vector2.Distance(new Vector2(root.X, root.Z), new Vector2(MathF.Round(at.X, 2), MathF.Round(at.Y, 2))));
        }
        var end = RagdollCorpseTests.Totals(sim, enemy);
        float moved = Vector3.Distance(com0, end.Com);
        output.WriteLine(F($"s6 (пак): труп лёг центром масс в ({com0.X:F3}; {com0.Z:F3}); место наибольшего пересечения ({at.X:F2}; {at.Y:F2}), мера {overlap:F3}; центр масс трупа до {comSpeed:F3} м/с, сдвиг {moved:F3} м, тела до {bodyMax:F2} м/с, в конце {end.KineticEnergy:F4} Дж; враг 2 сдвинулся до {secondMoved:F3} м; толчок {trace.Window.Source(PhysicsSource.PushBody).Applied:F2} Н·с, выталкивание {trace.Window.PushPopSpeed:F4}"));
        Assert.True(overlap > 0.5f, $"пересечения нет: {overlap}");
        Assert.True(comSpeed <= 1 && moved <= 0.5f && bodyMax <= 3, "труп разлетелся");
        Assert.True(end.KineticEnergy <= 0.1f, $"труп не успокоился: {end.KineticEnergy} Дж");
        Assert.True(secondMoved <= 0.5f, $"враг 2 отброшен на {secondMoved} м");
        Assert.True(trace.Window.Source(PhysicsSource.PushBody).Applied > 0 && trace.Window.PushPopSpeed <= physics.Settings.PopSpeed + PopSlack);
        RagdollCorpseTests.Clean(sim);
    }

    /// <summary>
    /// Клинки врагов толкают трупы врагов без урона (толчок физики — для всех пар, урон — только пары игрок ↔ враг): враг 2 стоит у трупа врага 1 и
    /// рубит (команда врагу, slash1–4 раз в 0,5 с) — толчки клинка проходом (трасса: <c>push.pass</c>) и проход пары враг 2 → труп открывался;
    /// попаданий нет, здоровье трупа то же; отдача — врагу 2.
    /// </summary>
    [Fact]
    public void EnemyBlades_PushEnemyCorpses_WithoutDamage()
    {
        var (sim, physics) = RagdollCorpseTests.Create(Pak, "arena.enemies=2;combat.enemiesAttack=false");
        var trace = physics.EnableTrace();
        sim.Enqueue(RagdollCorpseTests.Place(2, 6, 6, 0));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Place(1, 0, 3, MathF.PI));
        RagdollCorpseTests.Step(sim, RagdollCorpseTests.Kill(1));
        var one = RagdollCorpseTests.Fighter(sim, 1);
        var two = RagdollCorpseTests.Fighter(sim, 2);
        while (sim.Tick < 241)
        {
            sim.Enqueue(new EnemyCommand { Slot = 2, Velocity = Vector2.Zero });
            sim.Step();
        }
        var com = RagdollCorpseTests.Totals(sim, one).Com;
        sim.Enqueue(RagdollCorpseTests.Place(2, com.X, com.Z - 1.1f, 0));
        sim.Step();
        var health = sim.World.Get<Health>(one);
        trace.ClearWindow();
        int hits = 0;
        for (int t = 0; t < 480; t++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(new EnemyCommand { Slot = 2, Velocity = Vector2.Zero, FacingGoal = 0, Attack = t % 60 == 0 ? 1 + t / 60 % 4 : 0 });
            sim.Step();
            hits += sim.Export.Frame<Event<Hit>>().Length;
        }
        var pass = physics.Pass(RagdollCorpseTests.Body(sim, two), RagdollCorpseTests.Body(sim, one));
        float moved = Vector3.Distance(com, RagdollCorpseTests.Totals(sim, one).Com);
        var after = sim.World.Get<Health>(one);
        output.WriteLine(F($"клинок врага по трупу: толчок проходом {trace.Window.Source(PhysicsSource.PushPass).Applied:F2} Н·с, телом {trace.Window.Source(PhysicsSource.PushBody).Applied:F2}; проходов {pass.Number}, доля прохода {trace.Window.PassShare:F3}; труп сдвинут на {moved:F3} м"));
        Assert.Equal(0, hits);
        Assert.Equal((health.Hp, health.Bleed, health.Wounds[BodySegs.Torso]), (after.Hp, after.Bleed, after.Wounds[BodySegs.Torso]));
        Assert.True(trace.Window.Source(PhysicsSource.PushPass).Applied > 0 && pass.Number > 0, "клинок врага не толкнул труп проходом");
        Assert.True(sim.World.Get<Recoil>(two).Tick > 241);
        RagdollCorpseTests.Clean(sim);
    }
}