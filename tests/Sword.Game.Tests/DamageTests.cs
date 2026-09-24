using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Bootstrap;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 6г «Урон, смерть, реакции, возрождение»: правила урона против чисел SWORD (<see cref="DamageRules"/>), проба удара командой
/// тем же путём Resolve (урон, раны, кровотечение, заявка на отруб, смертельный укол только остриём, реакция, статистика),
/// неуязвимость (настройка и рывок), трупы — не цели и замирают (капсулы хранят скорости), кровопотеря, возрождение ровно через 8 с,
/// мёртвый игрок (команды не действуют, враги ×0,2 и не бьют, R — заново), вздрагивание игрока (только верх, управление не отнимается),
/// инварианты урона на неисправностях. Контент — синтетический (<see cref="TestAssets"/>); касания настоящих ударов пака — сценарии
/// манифеста <c>combat</c> (дуэль, смерть игрока, рывок сквозь удар) и инвариант <c>combat.hits</c>.
/// </summary>
public sealed class DamageTests
{
    private static Simulation Create(params string[] sets) => TestAssets.Simulation(ConfigSet.Empty(sets));

    private static void Step(Simulation sim, int ticks, PlayerCommand command = default)
    {
        for (int t = 0; t < ticks; t++)
        {
            sim.Enqueue(command);
            sim.Step();
        }
    }

    private static void Probe(Simulation sim, int attacker, int victim, int part, float damage, HitKind kind = HitKind.Slash) =>
        sim.Enqueue(new StrikeProbe { Attacker = attacker, Victim = victim, Part = part, Damage = damage, Kind = kind });

    private static Entity Enemy(Simulation sim, int slot = 1)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет врага на месте {slot}");
    }

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    // ── правила ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Числа SWORD 2eab13b: урон касания (<c>KinematicStrikes</c>), множители частей и пороги отруба (<c>Fighter.HpMul</c>,
    /// <c>SeverAt</c>, <c>SeverThreshold</c> отзывчивого режима), попадание (<c>ApplyHit</c>), кровопотеря (<c>AfterTick</c>);
    /// смертельный укол SWORD2 — только остриём вперёд (доля движения кончика вдоль клинка больше 0,6).
    /// </summary>
    [Fact]
    public void Rules_MatchSword()
    {
        Assert.Equal([0.9f, 1f, 1.6f, .4f, .4f, .4f, .4f, .5f, .5f, .5f, .5f, 0, .3f, .3f], DamageRules.HpMul.ToArray());
        Assert.Equal([1e9f, 1e9f, 45, 42, 30, 42, 30, 62, 44, 62, 44, 1e9f, 30, 30], DamageRules.SeverAt.ToArray());
        // ((укол ? 12 : 8) + min(скорость, 16)·(укол ? 1,2 : 1,4))·(игрок ? урон игрока : 1)
        Assert.Equal((8f + 10f * 1.4f) * 1, DamageRules.StrikeDamage(false, 10, false, 2));
        Assert.Equal((8f + 16f * 1.4f) * 2, DamageRules.StrikeDamage(false, 40, true, 2));
        Assert.Equal((12f + 5f * 1.2f) * 2, DamageRules.StrikeDamage(true, 5, true, 2));
        Assert.Equal(60.8f, DamageRules.StrikeDamage(false, 30, true, 2), 4);
        // порог отруба: торс 75, прочее — SeverAt·0,6; удар игрока — ÷ max(острота, 0,05)
        Assert.Equal(75f, DamageRules.SeverThreshold(BodySegs.Torso));
        Assert.Equal(45f * 0.6f, DamageRules.SeverThreshold(BodySegs.Head));
        Assert.Equal(42f * 0.6f / 0.5f, DamageRules.SeverThreshold(BodySegs.UArmL, true, 0.5f));
        Assert.Equal(42f * 0.6f / 0.05f, DamageRules.SeverThreshold(BodySegs.UArmL, true, 0));
        Assert.Equal(42f * 0.6f, DamageRules.SeverThreshold(BodySegs.UArmL, false, 0.5f));
        // попадание: очки — урон × множитель, рана — + урон/45 (не больше 1,5), кровотечение — + урон·0,02
        var h = Health.Fresh;
        DamageRules.Apply(ref h, BodySegs.Head, 30);
        Assert.Equal((100 - 30 * 1.6f, 30f / 45, 30 * 0.02f), (h.Hp, h.Wounds[BodySegs.Head], h.Bleed));
        DamageRules.Apply(ref h, BodySegs.Head, 100);
        Assert.Equal(1.5f, h.Wounds[BodySegs.Head]);
        Assert.True(DamageRules.Killed(h, false));   // очков не осталось
        var torso = Health.Fresh;
        DamageRules.Apply(ref torso, BodySegs.Torso, 63);   // рана 1,4 — смерть при 37 очках
        Assert.True(torso.Hp > 0 && DamageRules.Killed(torso, false));
        // смертельный укол: укол, голова или торс, урон больше 20, остриём вперёд
        var fwd = Vector3.UnitZ;
        Assert.True(DamageRules.LethalThrust(HitKind.Thrust, BodySegs.Torso, 21, fwd, fwd));
        Assert.True(DamageRules.LethalThrust(HitKind.Thrust, BodySegs.Head, 21, Vector3.Normalize(new Vector3(0.5f, 0, 1)), fwd));
        Assert.False(DamageRules.LethalThrust(HitKind.Thrust, BodySegs.Torso, 21, Vector3.UnitX, fwd));   // вбок — не острием
        Assert.False(DamageRules.LethalThrust(HitKind.Thrust, BodySegs.Torso, 21, -fwd, fwd));           // отвод клинка
        Assert.False(DamageRules.LethalThrust(HitKind.Thrust, BodySegs.Torso, 20, fwd, fwd));
        Assert.False(DamageRules.LethalThrust(HitKind.Slash, BodySegs.Torso, 50, fwd, fwd));
        Assert.False(DamageRules.LethalThrust(HitKind.Thrust, BodySegs.ThighL, 50, fwd, fwd));
        // кровопотеря: Hp −= Bleed·dt, Bleed −= 0,2·dt (не ниже 0)
        var b = new Health { Hp = 10, Bleed = 0.001f };
        DamageRules.Bleed(ref b, 0.5f);
        Assert.Equal((10 - 0.001f * 0.5f, 0f), (b.Hp, b.Bleed));
    }

    // ── проба и Resolve ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Проба удара командой: попадание в плечо врага — очки жизни, рана, кровотечение (кровопотеря — в том же тике), заявка на отруб
    /// (урон больше порога), событие попадания; враг отшатывается (реакция действием клипом реакции); статистика — попадание и последний
    /// удар игрока. Инварианты чисты.
    /// </summary>
    [Fact]
    public void Probe_DamageWoundBleedSeverReactionStats()
    {
        var sim = Create("arena.enemies=1", "combat.enemiesAttack=false");
        var enemy = Enemy(sim);
        Step(sim, 5);
        sim.Export.BeginFrame();
        Probe(sim, 0, 1, BodySegs.UArmL, 50);
        Step(sim, 1);
        var h = sim.World.Get<Health>(enemy);
        float hp = 100 - 50 * 0.4f, bleed = 50 * 0.02f;
        hp -= bleed * sim.Dt;
        Assert.Equal(hp, h.Hp);
        Assert.Equal(bleed - DamageRules.BleedDecay * sim.Dt, h.Bleed);
        Assert.Equal(50f / 45, h.Wounds[BodySegs.UArmL]);
        var hit = Assert.Single(sim.Export.Frame<Event<Hit>>().ToArray()).Record.Value;
        Assert.Equal((GameCore.Player(sim.World), enemy, BodySegs.UArmL, 50f, false, true), (hit.Attacker, hit.Victim, hit.Part, hit.Damage, hit.Killed, hit.Probe));
        Assert.Equal(42f * 0.6f, hit.SeverAt);
        var sever = Assert.Single(sim.Export.Frame<Event<SeverRequested>>().ToArray()).Record.Value;
        Assert.Equal((enemy, BodySegs.UArmL, hit.Contact), (sever.Victim, sever.Part, sever.Contact));
        var contact = Assert.Single(sim.Export.Frame<Event<StrikeContact>>().ToArray()).Record.Value;
        Assert.Equal(hit.Contact, contact.Id);
        Assert.True(contact.Probe && contact.AttackSerial == -1);
        var a = sim.World.Get<AnimatorAction>(enemy);
        Assert.Equal(ActionKind.HitReact, a.Kind);
        var actions = TestAssets.Assets.Actions;
        Assert.Contains(a.Clip, new[] { actions.Clip[ActionSlots.HitFront1], actions.Clip[ActionSlots.HitFront2], actions.Clip[ActionSlots.HitBack] });
        var stats = sim.World.Resource<ArenaStats>();
        Assert.Equal((1, 0, BodySegs.UArmL, 50f, 42f * 0.6f), (stats.Hits, stats.Kills, stats.LastPart, stats.LastDamage, stats.LastSeverAt));
        // реакция идёт: враг не бьёт (CanAct — нет), ход — 0,15
        Assert.False(ActionLayer.CanAct(a, false, TestAssets.Assets.Strikes.Attacks));
        Assert.Equal(ActionRules.HitReactMoveScale, ActionLayer.MoveScale(a, false));
        Step(sim, 60);
        Clean(sim);
        // негодные пробы — ничего: место без бойца, одно место за обоих, клинок, отрицательный урон
        sim.Export.BeginFrame();
        Probe(sim, 0, 5, BodySegs.Head, 10);
        Probe(sim, 1, 1, BodySegs.Head, 10);
        Probe(sim, 0, 1, BodySegs.Weapon, 10);
        Probe(sim, 0, 1, BodySegs.Head, -1);
        Step(sim, 1);
        Assert.True(sim.Export.Frame<Event<StrikeContact>>().IsEmpty);
    }

    /// <summary>
    /// Смертельный укол — только остриём вперёд (проба: направление = ось клинка): укол в голову урона 25 убивает при 60 очках жизни;
    /// рубящий тем же уроном — нет. Смерть — метка <see cref="Dead"/> с временем; убито — 1.
    /// </summary>
    [Fact]
    public void LethalThrust_KillsOutright_SlashDoesNot()
    {
        var sim = Create("arena.enemies=2", "combat.enemiesAttack=false");
        Step(sim, 3);
        Probe(sim, 0, 1, BodySegs.Head, 25, HitKind.Slash);
        Probe(sim, 0, 2, BodySegs.Head, 25, HitKind.Thrust);
        sim.Export.BeginFrame();
        Step(sim, 1);
        Assert.False(sim.World.Has<Dead>(Enemy(sim, 1)));
        Assert.Equal(sim.World.Resource<SimTime>().Time, sim.World.Get<Dead>(Enemy(sim, 2)).Time);
        var hits = sim.Export.Frame<Event<Hit>>();
        Assert.Equal(2, hits.Length);
        Assert.Equal((false, false), (hits[0].Record.Value.Killed, hits[0].Record.Value.LethalThrust));
        Assert.Equal((true, true), (hits[1].Record.Value.Killed, hits[1].Record.Value.LethalThrust));
        Assert.Equal(1, sim.World.Resource<ArenaStats>().Kills);
        Step(sim, 10);
        Clean(sim);
    }

    /// <summary>
    /// Неуязвимость: <c>combat.playerInvulnerable</c> — касание есть, попадания нет; неуязвимость рывка — так же, пока идёт таймер
    /// рывка, после — попадание засчитывается.
    /// </summary>
    [Fact]
    public void Invulnerable_ContactWithoutHit_SettingAndDash()
    {
        var sim = Create("arena.enemies=1", "combat.enemiesAttack=false", "combat.playerInvulnerable=true");
        var player = GameCore.Player(sim.World);
        Step(sim, 3);
        sim.Export.BeginFrame();
        Probe(sim, 1, 0, BodySegs.Torso, 30);
        Step(sim, 1);
        Assert.Single(sim.Export.Frame<Event<StrikeContact>>().ToArray());
        Assert.True(sim.Export.Frame<Event<Hit>>().IsEmpty);
        Assert.Equal(Health.Full, sim.World.Get<Health>(player).Hp);

        var dash = Create("arena.enemies=1", "combat.enemiesAttack=false");
        player = GameCore.Player(dash.World);
        Step(dash, 3);
        Step(dash, 1, new PlayerCommand { Dash = true, Move = new Vector2(1, 0) });
        Assert.True(dash.World.Get<PlayerControl>(player).Invulnerable > 0);
        Probe(dash, 1, 0, BodySegs.Torso, 30);
        Step(dash, 1);
        Assert.Equal(Health.Full, dash.World.Get<Health>(player).Hp);
        while (dash.World.Get<PlayerControl>(player).Invulnerable > 0) Step(dash, 1);
        Probe(dash, 1, 0, BodySegs.Torso, 30);
        Step(dash, 1);
        Assert.True(dash.World.Get<Health>(player).Hp < Health.Full - 29);
        Clean(sim);
        Clean(dash);
    }

    /// <summary>
    /// Труп — не цель и замирает (до рэгдолла): после смерти проба не даёт попадания; корень, курс, действие, поза и клинок стоят, хотя
    /// ИИ живого вёл бы его к игроку; капсулы стоят и хранят скорости последнего тика жизни (их возьмёт рэгдолл, этап 7).
    /// </summary>
    [Fact]
    public void Corpse_NotTarget_Frozen_CapsuleVelocitiesKept()
    {
        var sim = Create("arena.enemies=1", "combat.enemiesAttack=false");
        var enemy = Enemy(sim);
        Step(sim, 40);   // враг идёт к игроку
        Probe(sim, 0, 1, BodySegs.Head, 100, HitKind.Thrust);
        Step(sim, 1);
        Assert.True(sim.World.Has<Dead>(enemy));
        var bodies = sim.World.View<IBodyView>();
        var body = sim.World.Get<BodyRef>(enemy);
        BodySnapshot Capsule() => bodies.Body(body, BodySegs.Body(BodySegs.Pelvis));
        var capsule = Capsule();
        Assert.True(capsule.Velocity.Length() > 0.1f, $"враг шёл — скорость капсулы таза {capsule.Velocity}");
        var root = sim.World.Get<Transform>(enemy);
        var action = sim.World.Get<AnimatorAction>(enemy);
        var tip = sim.World.Get<Blade>(enemy).Tip;
        var anim = sim.World.View<IAnimationView>();
        var poseRef = sim.World.Get<PoseRef>(enemy);
        var pose = anim.Model(poseRef).ToArray();
        sim.Export.BeginFrame();
        Probe(sim, 0, 1, BodySegs.Torso, 50);
        Step(sim, 120);
        Assert.True(sim.Export.Frame<Event<Hit>>().IsEmpty);
        Assert.Single(sim.Export.Frame<Event<StrikeContact>>().ToArray());   // касание есть, попадания нет — труп
        Assert.Equal(root, sim.World.Get<Transform>(enemy));
        Assert.Equal((action.Kind, action.Time, action.Weight), (sim.World.Get<AnimatorAction>(enemy).Kind, sim.World.Get<AnimatorAction>(enemy).Time, sim.World.Get<AnimatorAction>(enemy).Weight));
        Assert.Equal(tip, sim.World.Get<Blade>(enemy).Tip);
        Assert.Equal(pose, anim.Model(poseRef).ToArray());
        Assert.Equal((capsule.Position, capsule.Velocity, capsule.AngularVelocity), (Capsule().Position, Capsule().Velocity, Capsule().AngularVelocity));
        Clean(sim);
    }

    /// <summary>
    /// Кровопотеря: после попадания живой теряет очки жизни (кровотечение·dt, кровотечение гаснет на 0,2/с) и умирает, когда они кончатся;
    /// «убито» считает и смерть от кровопотери (в SWORD — только от удара игрока).
    /// </summary>
    [Fact]
    public void Bleed_KillsLater_CountsKill()
    {
        var sim = Create("arena.enemies=1", "combat.enemiesAttack=false");
        var enemy = Enemy(sim);
        Step(sim, 3);
        Probe(sim, 0, 1, BodySegs.UArmL, 200);   // 20 очков, кровотечение 4/с — смерть через ~5,9 с
        Step(sim, 1);
        var h = sim.World.Get<Health>(enemy);
        Assert.False(sim.World.Has<Dead>(enemy));
        // модель SWORD шаг за шагом
        int ticks = 0;
        while (h.Hp > 0)
        {
            DamageRules.Bleed(ref h, sim.Dt);
            ticks++;
        }
        Step(sim, ticks - 1);
        Assert.False(sim.World.Has<Dead>(enemy));
        Assert.Equal(0, sim.World.Resource<ArenaStats>().Kills);
        Step(sim, 1);
        Assert.True(sim.World.Has<Dead>(enemy));
        Assert.Equal(h.Hp, sim.World.Get<Health>(enemy).Hp);
        Assert.InRange(ticks / 120f, 5.8f, 5.9f);
        Assert.Equal((1, 1), (sim.World.Resource<ArenaStats>().Kills, sim.World.Resource<ArenaStats>().Hits));
        Clean(sim);
    }

    /// <summary>
    /// Возрождение врага — в первом тике, где с его смерти прошло больше 8 с (<c>Time − DeadTime &gt; 8</c>): та же сущность, место,
    /// полное здоровье, пары удара с нуля; поза, тело и клинок следующего тика — постройки; инварианты чисты.
    /// </summary>
    [Fact]
    public void Respawn_After8Seconds_SameEntityFreshState()
    {
        var sim = Create("arena.enemies=1", "combat.enemiesAttack=false");
        var enemy = Enemy(sim);
        Step(sim, 3);
        Probe(sim, 0, 1, BodySegs.Head, 100, HitKind.Thrust);
        Step(sim, 1);
        float died = sim.World.Get<Dead>(enemy).Time;
        var where = sim.World.Get<Transform>(enemy).Position;
        while (sim.World.Has<Dead>(enemy))
        {
            Assert.False(sim.World.Resource<SimTime>().Time - died > DamageRules.RespawnDelay);
            Step(sim, 1);
        }
        float time = sim.World.Resource<SimTime>().Time;
        Assert.True(time - died > DamageRules.RespawnDelay && time - sim.Dt - died <= DamageRules.RespawnDelay, $"{time - died}");
        Assert.Equal(enemy, Enemy(sim));
        Assert.Equal(Health.Full, sim.World.Get<Health>(enemy).Hp);
        Assert.Equal(StrikePairs.None, sim.World.Get<StrikePairs>(enemy));
        Assert.NotEqual(where, sim.World.Get<Transform>(enemy).Position);
        Step(sim, 60);
        Assert.False(sim.World.Has<Dead>(enemy));
        Clean(sim);
    }

    /// <summary>
    /// Мёртвый игрок: команды не действуют (ход не разгоняет), враги подходят ×0,2 (намерение — 0,2 от составляющих ИИ) и не бьют; сброс
    /// арены (R) строит игрока заново — жив, здоровье полное, статистика с нуля.
    /// </summary>
    [Fact]
    public void DeadPlayer_Frozen_EnemiesCalm_ResetRevives()
    {
        var sim = Create("arena.enemies=1");
        var player = GameCore.Player(sim.World);
        var enemy = Enemy(sim);
        Step(sim, 3);
        Probe(sim, 1, 0, BodySegs.Torso, 200);
        Step(sim, 1);
        Assert.True(sim.World.Has<Dead>(player));
        var root = sim.World.Get<Transform>(player);
        Step(sim, 30, new PlayerCommand { Move = new Vector2(0, 1), Attack = true });
        Assert.Equal(root, sim.World.Get<Transform>(player));
        Assert.Equal(0, sim.World.Get<AnimatorAction>(player).AttackSerial);
        // враг: намерение — 0,2 от ИИ, ударов нет, хотя подошёл бы и таймер вышел
        var terms = sim.World.Get<EnemyAiTerms>(enemy);
        var intent = sim.World.Get<FighterCommand>(enemy);
        var t = sim.World.Get<PrevTransform>(enemy).Position;   // ИИ думал по корню до движения тика
        var to = root.Position - t;
        to.Y = 0;
        var dir = Vector3.Normalize(to);
        var expected = (dir * terms.ApproachSpeed + new Vector3(dir.Z, 0, -dir.X) * terms.SwaySpeed + terms.Separation) * EnemyCrowd.DeadPlayerCalm;
        Assert.True(Vector3.Distance(expected, intent.DesiredVelocity) < 1e-4f, $"{expected} ≠ {intent.DesiredVelocity}");
        Step(sim, 1500);
        Assert.True(sim.World.Get<EnemyAiTerms>(enemy).Distance < EnemyAiRules.Reach, $"враг не подошёл: {sim.World.Get<EnemyAiTerms>(enemy).Distance} м");
        Assert.Equal(0, sim.World.Get<AnimatorAction>(enemy).AttackSerial);
        Assert.True(sim.World.Get<EnemyAttack>(enemy).Timer <= 0);   // таймер вышел — а удара нет
        // R — игрок заново
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim, 1);
        Assert.False(sim.World.Has<Dead>(player));
        Assert.Equal(Health.Full, sim.World.Get<Health>(player).Hp);
        Assert.Equal(ArenaStats.Start, sim.World.Resource<ArenaStats>());
        Step(sim, 5, new PlayerCommand { Move = new Vector2(0, 1) });
        Assert.True(sim.World.Get<RootVelocity>(player).Value.Z > 1);
        Clean(sim);
    }

    /// <summary>
    /// Вздрагивание игрока: попадание не отнимает управление — корень и скорость бега побитово как без попадания; поза меняется только в
    /// суставах маски верха (ноги и таз — как без попадания), в двух слотах; через длину вздрагивания поза снова как без попадания.
    /// </summary>
    [Fact]
    public void Flinch_UpperBodyOnly_ControlKept()
    {
        Simulation Run(bool hit, out Entity player)
        {
            var sim = Create("arena.enemies=1", "combat.enemiesAttack=false");
            player = GameCore.Player(sim.World);
            Step(sim, 10, new PlayerCommand { Move = new Vector2(0, 1) });
            if (hit) Probe(sim, 1, 0, BodySegs.FArmL, 10);
            return sim;
        }
        var plain = Run(false, out var p0);
        var flinched = Run(true, out var p1);
        var anim0 = plain.World.View<IAnimationView>();
        var anim1 = flinched.World.View<IAnimationView>();
        var mask = ActionRules.UpperMask(TestAssets.Assets.Clips.Skeleton);
        bool differed = false;
        for (int t = 0; t < 120; t++)
        {
            Step(plain, 1, new PlayerCommand { Move = new Vector2(0, 1) });
            Step(flinched, 1, new PlayerCommand { Move = new Vector2(0, 1) });
            Assert.Equal(plain.World.Get<Transform>(p0), flinched.World.Get<Transform>(p1));
            Assert.Equal(plain.World.Get<RootVelocity>(p0), flinched.World.Get<RootVelocity>(p1));
            // модельные матрицы: у суставов вне маски верха (таз, ноги) все предки тоже вне маски — поза та же
            var rot0 = anim0.Model(plain.World.Get<PoseRef>(p0));
            var rot1 = anim1.Model(flinched.World.Get<PoseRef>(p1));
            for (int j = 0; j < mask.Length; j++)
            {
                if (mask[j] == 0) Assert.Equal(rot0[j], rot1[j]);
                else differed |= rot0[j] != rot1[j];
            }
            if (t >= 70) Assert.Equal(rot0.ToArray(), rot1.ToArray());   // 0,55 с вздрагивания прошли
        }
        Assert.True(differed, "вздрагивание не изменило верх тела");
        Assert.Equal(1, flinched.World.Get<Flinch>(p1).Count);
        Assert.Equal(ActionKind.None, flinched.World.Get<AnimatorAction>(p1).Kind);   // игрок не играет реакцию действием
        Clean(flinched);
    }

    /// <summary>
    /// Два попадания подряд — второе вздрагивание в другом слоте, прошлое гаснет с текущего веса (<c>StartFlinch</c> SWORD); вес прошлого в
    /// момент нового попадания не скачет и на входе (исправление SWORD: там на первых 0,06 с вес падал до квадрата входа); третье попадание,
    /// пока оба слота видны, нового вздрагивания не даёт (исправление SWORD: там старший слот обрывался — скачок позы).
    /// </summary>
    [Fact]
    public void Flinch_SecondHitFades_ThirdWhileVisibleSkipped()
    {
        const float dt = 1f / 120;
        static float Weight(in FlinchSlot s) => s.Clip < 0 ? 0 : FlinchLayer.Envelope(s.Time, s.Length);
        var f = Flinch.None;
        Assert.True(FlinchLayer.Start(ref f, 7, 1.0f));
        Assert.Equal((1, 7, 0f, FlinchLayer.Length), (f.Last, f.Second.Clip, f.Second.Time, f.Second.Length));
        for (int i = 0; i < 12; i++) FlinchLayer.Tick(ref f, dt);   // 0,1 с — огибающая 1
        Assert.True(FlinchLayer.Start(ref f, 8, 0.3f));
        Assert.Equal((0, 8, 0.3f), (f.Last, f.First.Clip, f.First.Length));   // короткий клип — длина клипа
        Assert.Equal(f.Second.Time + FlinchLayer.Out, f.Second.Length, 5);   // вес 1 — гаснет за 0,2 с
        // третье попадание, пока прошлый (второй слот) ещё гаснет, — не начато, слоты те же
        for (int i = 0; i < 3; i++) FlinchLayer.Tick(ref f, dt);
        var before = f;
        Assert.False(FlinchLayer.Start(ref f, 9, 1.0f));
        Assert.Equal(before, f);
        for (int i = 0; i < 60; i++) FlinchLayer.Tick(ref f, dt);
        Assert.Equal((-1, -1), (f.First.Clip, f.Second.Clip));
        Assert.Equal(2, f.Count);
        // попадание на входе прошлого (через 3 тика): вес прошлого не меняется
        var g = Flinch.None;
        FlinchLayer.Start(ref g, 7, 1.0f);
        for (int i = 0; i < 3; i++) FlinchLayer.Tick(ref g, dt);
        float w = Weight(g.Second);
        Assert.True(w is > 0 and < 1);
        Assert.True(FlinchLayer.Start(ref g, 8, 1.0f));
        Assert.Equal(w, Weight(g.Second), 6);
    }

    // ── агент ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Агенту: проба удара — <c>command.send StrikeProbe</c>; «почему боец умер» — <c>events.history</c> по <c>Hit</c> и <c>Added&lt;Dead&gt;</c>
    /// (экспорт даёт событиям журнал — без подписчика они не видны); здоровье и пары — <c>entity.get</c> (рецепт — docs/agent-playbook.md).
    /// </summary>
    [Fact]
    public async Task Agent_ProbeByCommand_WhyDiedByEventsHistory()
    {
        var sim = Create("arena.enemies=1", "combat.enemiesAttack=false");
        var host = new Sword.Engine.Agent.AgentHost(new Sword.Engine.Agent.AgentHostOptions
        {
            Mode = "headless", Catalog = new Sword.Engine.Introspection.Catalog(sim.World, sim.CommandTypes),
            Time = new Sword.Engine.Introspection.TimeControl(true, sim.Tick), Service = new Sword.Engine.Introspection.ServiceQueue(),
            Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"), Stop = () => { },
            ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        Sword.Game.Agent.SwordAgent.Register(host);
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => Sword.Engine.Agent.SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<System.Text.Json.Nodes.JsonObject> Call(string tool, System.Text.Json.Nodes.JsonObject? args = null)
            {
                var request = new System.Text.Json.Nodes.JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = args ?? [] },
                };
                var r = System.Text.Json.Nodes.JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
                Assert.False(r["isError"]?.GetValue<bool>() ?? false, r.ToJsonString());
                return r["structuredContent"]!.AsObject();
            }

            await Call("time.step", new() { ["ticks"] = 3 });
            await Call("command.send", new()
            {
                ["command"] = "StrikeProbe",
                ["fields"] = new System.Text.Json.Nodes.JsonObject { ["Attacker"] = 0, ["Victim"] = 1, ["Part"] = BodySegs.Head, ["Damage"] = 30, ["Kind"] = "Thrust" },
            });
            await Call("time.step", new() { ["ticks"] = 2 });
            var history = await Call("events.history", new() { ["types"] = new System.Text.Json.Nodes.JsonArray("Hit", "Added<Dead>"), ["entity"] = "enemy 1" });
            var events = history["events"]!.AsArray();
            Assert.Equal(["Added<Dead>", "Event<Hit>"], events.Select(e => e!["type"]!.GetValue<string>()).Order());
            var hit = events.Single(e => e!["type"]!.GetValue<string>() == "Event<Hit>")!["fields"]!["Value"]!;
            Assert.True(hit["Killed"]!.GetValue<bool>() && hit["LethalThrust"]!.GetValue<bool>() && hit["Probe"]!.GetValue<bool>(), hit.ToJsonString());
            Assert.Equal(("0v1", "Thrust", BodySegs.Head), (hit["Attacker"]!.GetValue<string>(), hit["Kind"]!.GetValue<string>(), hit["Part"]!.GetValue<int>()));
            var enemy = (await Call("entity.get", new() { ["entity"] = "enemy 1", ["components"] = new System.Text.Json.Nodes.JsonArray("Health", "Dead", "StrikePairs") }))
                .ToJsonString();
            Assert.Contains("Wounds", enemy);
            Assert.Contains("FromPlayer", enemy);
            Assert.Contains("\"Dead\"", enemy);
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }

    // ── неисправности ────────────────────────────────────────────────────────────────────────────────────────────────

    public enum DamageFault { LiveWithoutHp, DeadMoves, HitWithoutPass, StuckCorpse }

    /// <summary>Неисправность на тике <see cref="Tick"/> — её ловит свой инвариант урона в том же тике.</summary>
    public sealed class DamageFaultModule(DamageFault fault) : IModule
    {
        public const ulong Tick = 30;

        public string Id => "fault";

        public void Configure(SimulationBuilder b)
        {
            switch (fault)
            {
                case DamageFault.LiveWithoutHp: b.System(GameStages.End, new ZeroHp()); break;
                case DamageFault.DeadMoves: b.System(GameStages.End, new MoveDead()); break;
                case DamageFault.HitWithoutPass: b.System(GameStages.Combat, new FakeContact()); break;
                case DamageFault.StuckCorpse: b.System(GameStages.Aftermath, new BackdateDeath()); break;
            }
        }

        private static bool Now(in SystemContext ctx) => ctx.World.Resource<SimTime>().Tick == Tick;

        private static Entity Enemy(in SystemContext ctx, Query q)
        {
            foreach (var chunk in ctx.Iterate(q))
                if (chunk.Count > 0) return chunk.Entities[0];
            return Entity.Null;
        }

        /// <summary>Живому — 0 очков жизни без метки смерти.</summary>
        private sealed class ZeroHp : IContinuousSystem
        {
            private Query _q = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("fault.zero-hp")).Sets<Health>();
                _q = s.Query().With<EnemyTag>().Without<Dead>();
            }

            public void Run(in SystemContext ctx)
            {
                if (Now(ctx) && Enemy(ctx, _q) is { IsNull: false } e) ctx.Commands.Set(e, new Health { Hp = 0 });
            }
        }

        /// <summary>Мёртвого сдвинуть.</summary>
        private sealed class MoveDead : IContinuousSystem
        {
            private Query _q = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("fault.move-dead")).Writes<Transform>();
                _q = s.Query().With<EnemyTag>().With<Dead>().Write<Transform>();
            }

            public void Run(in SystemContext ctx)
            {
                if (!Now(ctx)) return;
                foreach (var chunk in ctx.Iterate(_q))
                    foreach (ref var t in chunk.Write<Transform>()) t.Position.X += 0.5f;
            }
        }

        /// <summary>Касание клинком (не проба) от врага не в ударе.</summary>
        private sealed class FakeContact : IContinuousSystem
        {
            private Query _q = null!, _p = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("fault.fake-contact")).After(GameLabels.StrikeDetection).Emits<StrikeContact>();
                _q = s.Query().With<EnemyTag>().Without<Dead>();
                _p = s.Query().With<PlayerTag>();
            }

            public void Run(in SystemContext ctx)
            {
                if (!Now(ctx)) return;
                ctx.Commands.Emit(new StrikeContact
                {
                    Id = new ContactId(Tick, 99), Attacker = Enemy(ctx, _q), Victim = Enemy(ctx, _p), Part = BodySegs.Torso, Point = Vector3.UnitY,
                    Dir = Vector3.UnitZ, Blade = Vector3.UnitZ, Speed = 10, Kind = HitKind.Slash, Damage = 10, AttackSerial = 1,
                });
            }
        }

        /// <summary>Труп умер «давно» — 9 с назад, а не возрождён.</summary>
        private sealed class BackdateDeath : IContinuousSystem
        {
            private Query _q = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("fault.backdate")).Adds<Dead>();
                _q = s.Query().With<EnemyTag>().Without<Dead>();
            }

            public void Run(in SystemContext ctx)
            {
                if (Now(ctx) && Enemy(ctx, _q) is { IsNull: false } e)
                    ctx.Commands.Add(e, new Dead { Time = ctx.World.Resource<SimTime>().Time - 9 });
            }
        }
    }

    [Theory]
    [InlineData(DamageFault.LiveWithoutHp, "combat.health", "жив, а очков жизни")]
    [InlineData(DamageFault.HitWithoutPass, "combat.hits", "не в ударе")]
    [InlineData(DamageFault.StuckCorpse, "combat.respawn", "возрождение через 8 с не случилось")]
    public void Fault_CaughtOnItsTick(DamageFault fault, string id, string details)
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=1", "combat.enemiesAttack=false"), GameStages.All,
            b => GameCore.Configure(b, TestAssets.Assets), [.. SwordSimulation.Modules(), new DamageFaultModule(fault)]);
        Step(sim, (int)DamageFaultModule.Tick);
        Clean(sim);
        Step(sim, 1);
        var first = sim.Invariants.Recent().First(v => v.Id == id);
        Assert.Equal(DamageFaultModule.Tick, first.Tick);
        Assert.Contains(details, first.Details);
    }

    /// <summary><c>combat.dead</c>: мёртвый сдвинут — нарушение в тике сдвига.</summary>
    [Fact]
    public void Fault_DeadMoved_Caught()
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=1", "combat.enemiesAttack=false"), GameStages.All,
            b => GameCore.Configure(b, TestAssets.Assets), [.. SwordSimulation.Modules(), new DamageFaultModule(DamageFault.DeadMoves)]);
        Step(sim, 3);
        Probe(sim, 0, 1, BodySegs.Head, 100, HitKind.Thrust);
        Step(sim, (int)DamageFaultModule.Tick - 3);
        Clean(sim);
        Step(sim, 1);
        var first = sim.Invariants.Recent().First(v => v.Id == "combat.dead");
        Assert.Equal(DamageFaultModule.Tick, first.Tick);
        Assert.Contains("мёртвый", first.Details);
    }
}
