using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;
using Xunit.Abstractions;
using static Sword.Game.Tests.RagdollCorpseTests;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 7з-4 — самоколлизия трупа в игре (свои тесты свойств): таблица пар трупа (<see cref="CorpseJoints.SelfPairs"/> — все пары 14 тел, кроме
/// связанных суставом и торса с бёдрами) ставится переходом в труп, входит в хеш <c>physics</c> и его описание; агенту — поле
/// <c>Body.SelfDepth</c> и <c>physics.bodies</c> → <c>self</c>; инвариант <c>physics.selfContacts</c> ловит часть, удержанную в своём торсе, и
/// не ловит выходящую; лежащий труп не сложен сам в себя, а без самоколлизии (ручка 0) — сложен. Контент — синтетический
/// (<see cref="TestAssets"/>); на паке — сценарии <c>self-fold</c>, <c>self-fold-off</c> манифеста «Рэгдолл».
/// </summary>
public sealed class CorpseSelfContactsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Таблица трупа: 76 пар — ни одной связанной суставом (родитель ↔ ребёнок, и клинок с предплечьем), ни торса с бёдрами; остальные пары 14 тел —
    /// все, по номерам тел, без повторов. Переход в труп ставит её сочленению; хеш <c>physics</c> её видит (другая таблица — другой хеш),
    /// описание показывает пары.
    /// </summary>
    [Fact]
    public void SelfPairs_Table_SetOnDeath_HashedDescribed()
    {
        var pairs = CorpseJoints.SelfPairs.ToArray();
        Assert.Equal(76, pairs.Length);
        for (int a = 0; a < BodySegs.Bodies; a++)
            for (int b = a + 1; b < BodySegs.Bodies; b++)
            {
                bool joint = b != BodySegs.Pelvis && CorpseJoints.Parent(b) == a || a != BodySegs.Pelvis && CorpseJoints.Parent(a) == b;
                bool torsoThigh = a == BodySegs.Torso && b is BodySegs.ThighL or BodySegs.ThighR;
                Assert.Equal(!joint && !torsoThigh, pairs.Contains(new BodyPair(a, b)));
                Assert.Equal(!joint && !torsoThigh, CorpseJoints.Collides(b, a));
            }
        Assert.DoesNotContain(new BodyPair(BodySegs.FArmR, BodySegs.Weapon), pairs);   // запястье выключено, но клинок у кисти — внахлёст всегда
        Assert.Contains(new BodyPair(BodySegs.ThighL, BodySegs.ThighR), pairs);          // ноги друг сквозь друга не проходят

        var (sim, physics) = Create(TestAssets.Assets, "arena.enemies=1;combat.enemiesAttack=false");
        Step(sim, Place(1, 3, 3));
        var a1 = Body(sim, Fighter(sim, 1));
        Assert.Equal(0, physics.SelfContacts(a1).Length);   // живой (никогда не был трупом) — пар нет
        Step(sim, Kill(1));
        Assert.True(physics.SelfContacts(a1).SequenceEqual(CorpseJoints.SelfPairs));
        ulong before = sim.HashState().Parts["physics"];
        physics.SetSelfContacts(a1, CorpseJoints.SelfPairs[..10]);
        Assert.NotEqual(before, sim.HashState().Parts["physics"]);
        physics.SetSelfContacts(a1, CorpseJoints.SelfPairs);
        Assert.Equal(before, sim.HashState().Parts["physics"]);
        var corpse = JsonNode.Parse(sim.DescribeState("physics", new DescribeQuery(a1.ToString())))!["items"]![0]!;
        Assert.Equal(76, corpse["selfContactCount"]!.GetValue<int>());
        Assert.StartsWith("0-2,0-3,", corpse["selfContacts"]!.GetValue<string>());   // таз ↔ голова, таз ↔ плечо (таз ↔ торс — сустав)
    }

    /// <summary>
    /// Лежащий труп (синтетический риг, 4 с после смерти) не сложен сам в себя: самая глубокая пара — миллиметры; поле агента <c>Body.SelfDepth</c> и
    /// вид <c>physics.bodies</c> → <c>self</c> показывают то же; без самоколлизии (ручка «самоколлизия» 0 — как до 7з-4) тот же труп сложен —
    /// часть в своём теле на сантиметры (замер — в выводе). Инварианты чисты.
    /// </summary>
    [Fact]
    public void LyingCorpse_NotFoldedIntoItself_FieldAndView_WithoutSelfCollisionFolded()
    {
        float Run(string sets, bool check)
        {
            var (sim, physics) = Create(TestAssets.Assets, "arena.enemies=1;combat.enemiesAttack=false" + sets);
            Step(sim, Place(1, 0, 4));
            Step(sim, Kill(1));
            for (int t = 0; t < 480; t++) sim.Step();
            var e = Fighter(sim, 1);
            var a = Body(sim, e);
            var r = SelfOverlap.Deepest(physics, a, 1);
            output.WriteLine($"[{sets}] самая глубокая пара {(r.A >= 0 ? $"{BodySegs.Name(r.A)}–{BodySegs.Name(r.B)}" : "—")} {r.Depth * 1000:F1} мм, пересеклось пар {r.Touching}");
            if (check)
            {
                var ref_ = sim.World.Get<BodyRef>(e);
                double field = new Agent.BodyFields().Compile("Body.SelfDepth", "t")!(new ServiceContext(sim), e);
                Assert.Equal(r.Depth, field, 5);
                var self = Agent.PhysicsDebugJson.Self(sim.World, ref_)!;
                Assert.Equal(76, self["pairs"]!.GetValue<int>());
                Assert.True(self["active"]!.GetValue<bool>());
                Assert.Equal(1, self["scale"]!.GetValue<double>());
                Assert.Equal(r.Touching, self["touching"]!.GetValue<int>());
                Assert.Equal(0, new Agent.BodyFields().Compile("Body.SelfDepth", "t")!(new ServiceContext(sim), Fighter(sim, 0)));   // у живого пар нет
                // проверка 7з-4: построенный заново (живой, тела кинематические) — таблица осталась, но не действует: «части в теле» нет, хотя
                // капсулы стойки могут стоять внахлёст (замер — в выводе)
                Step(sim, Place(1, 0, 4));
                var revived = Body(sim, Fighter(sim, 1));
                Assert.Equal(76, physics.SelfContacts(revived).Length);
                var stance = SelfOverlap.Deepest(physics, revived, 1);
                output.WriteLine($"построенный заново: пары таблицы в стойке — пересеклось {stance.Touching}, самая глубокая {stance.Depth * 1000:F1} мм; действующих — нет");
                Assert.Equal(0, new Agent.BodyFields().Compile("Body.SelfDepth", "t")!(new ServiceContext(sim), Fighter(sim, 1)));
                var selfRevived = Agent.PhysicsDebugJson.Self(sim.World, sim.World.Get<BodyRef>(Fighter(sim, 1)))!;
                Assert.False(selfRevived["active"]!.GetValue<bool>());
                Assert.Equal(0, selfRevived["touching"]!.GetValue<int>());
                Clean(sim);
            }
            return r.Depth;
        }

        float with = Run("", check: true), without = Run(";ragdoll.selfCollision=0", check: false);
        Assert.True(with <= 0.01f, $"труп сложен сам в себя на {with} м");
        Assert.True(without >= 0.05f, $"без самоколлизии труп не сложился ({without} м) — тест не проверяет то, что должен");
    }

    /// <summary>
    /// <c>physics.selfContacts</c>: левое предплечье трупа поставлено в торс (вдоль его оси, центр в центр — в торсе на 0,225 м, в тазу — на
    /// ~0,1). Отпущенное выходит (самоколлизия выталкивает не быстрее <c>PopSpeed</c>) — нарушений нет; удержанное — нарушение по предплечью,
    /// когда эпизод пары длится дольше <c>(глубина начала − допуск) / (¼ PopSpeed) + </c><see cref="CorpseInvariants.SelfExitGrace"/> — первым
    /// у пары «таз — предплечье»: (0,1 − 0,06) / 0,25 + 0,25 ≈ 0,41 с (замер — тик 432 при порче с 360).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelfContactsInvariant_HeldInsideCaught_ReleasedExits(bool hold)
    {
        var holder = new IPhysicsWorld[1];
        const ulong tick = 360;
        var (sim, physics) = Create(TestAssets.Assets, "arena.enemies=1;combat.enemiesAttack=false",
            (GameStages.Physics, new ArmIntoTorso(tick, hold ? 200 : 1, holder)));
        holder[0] = physics;
        Step(sim, Place(1, 4, 4, 1.1f));
        Step(sim, Kill(1));
        while (sim.Tick < tick) sim.Step();
        Clean(sim);
        for (int t = 0; t < 200; t++) sim.Step();
        var hits = sim.Invariants.Recent().Where(v => v.Id == "physics.selfContacts").OrderBy(v => v.Tick).ToArray();
        output.WriteLine(hits.Length > 0 ? $"{hits[0].Tick}: {hits[0].Details}" : "нарушений нет");
        // не раньше выхода из допуска пары с самой мелкой глубиной начала (0,06 м — только запас), не позже, чем у самой глубокой (0,225 м)
        float pop = sim.World.Resource<RagdollRules>().PopSpeed;
        ulong earliest = tick + (ulong)(CorpseInvariants.SelfExitGrace * 120);
        ulong latest = tick + (ulong)MathF.Ceiling(((0.225f - CorpseInvariants.SelfTolerance) / (0.25f * pop) + CorpseInvariants.SelfExitGrace) * 120) + 1;
        if (hold)
            Assert.True(hits.Length > 0 && hits[0].Tick >= earliest && hits[0].Tick <= latest && hits[0].Details.Contains("forearm.l"),
                $"удержанное в торсе предплечье: первое нарушение {(hits.Length > 0 ? $"{hits[0].Tick}: {hits[0].Details}" : "—")}, ждали {earliest}…{latest}");
        else
        {
            Assert.Empty(hits);
            var a = Body(sim, Fighter(sim, 1));
            Assert.True(SelfOverlap.Deepest(physics, a, 1).Depth <= CorpseInvariants.SelfTolerance, "предплечье не вышло из торса");
        }
    }

    /// <summary>Левое предплечье трупа 1 — в торсе (ось вдоль оси торса, центр в его центре) после шага (Physics) тиков <paramref name="from"/> … <c>from + ticks − 1</c>.</summary>
    private sealed class ArmIntoTorso(ulong from, int ticks, IPhysicsWorld[] physics) : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("test.arm-into-torso")).After(GameLabels.CorpseRoot);

        public void Run(in SystemContext ctx)
        {
            ulong now = ctx.World.Resource<SimTime>().Tick;
            if (now < from || now >= from + (ulong)ticks) return;
            var p = physics[0];
            var a = FighterBodies.Handle(ctx.World.Get<BodyRef>(FighterIn(ctx.World, 1)));
            var torso = p.Get(p.Body(a, BodySegs.Torso));
            p.Place(p.Body(a, BodySegs.FArmL), torso.Position, torso.Rotation);
        }
    }

    private static Entity FighterIn(World world, int slot)
    {
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет бойца на месте {slot}");
    }
}
