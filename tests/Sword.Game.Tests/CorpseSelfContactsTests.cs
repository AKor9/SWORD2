using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;
using Xunit.Abstractions;
using static Sword.Game.Tests.RagdollCorpseTests;

namespace Sword.Game.Tests;

/// <summary>
/// Самоколлизия трупа в игре: таблица <see cref="CorpseJoints.SelfPairs"/> ставится переходом в труп и входит в хеш
/// <c>physics</c>; инвариант <c>physics.selfContacts</c> ловит часть в своём торсе; лежащий труп не сложен, без самоколлизии —
/// сложен. Контент — синтетический.
/// </summary>
public sealed class CorpseSelfContactsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Таблица трупа: 76 пар — все пары 14 тел, кроме связанных суставом (и клинка с предплечьем) и торса с бёдрами, без повторов.
    /// Переход в труп ставит её сочленению; хеш <c>physics</c> её видит, описание показывает.
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
        var pairs76 = physics.SelfContacts(a1);
        Assert.Equal(76, pairs76.Length);
        Assert.Equal([new BodyPair(0, 2), new BodyPair(0, 3)], pairs76[..2].ToArray());   // таз ↔ голова, таз ↔ плечо (таз ↔ торс — сустав)
    }

    /// <summary>
    /// Лежащий труп (4 с после смерти) не сложен сам в себя — самая глубокая пара в миллиметрах, вид физики показывает то же; без
    /// самоколлизии тот же труп сложен на сантиметры. Инварианты чисты.
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
                Assert.Equal(76, physics.SelfContacts(a).Length);
                // Построенный заново (живой, тела кинематические) — таблица осталась, но не действует: «части в теле» нет, хотя
                // капсулы стойки могут стоять внахлёст (замер — в выводе)
                Step(sim, Place(1, 0, 4));
                var revived = Body(sim, Fighter(sim, 1));
                Assert.Equal(76, physics.SelfContacts(revived).Length);
                var stance = SelfOverlap.Deepest(physics, revived, 1);
                output.WriteLine($"построенный заново: пары таблицы в стойке — пересеклось {stance.Touching}, самая глубокая {stance.Depth * 1000:F1} мм; действующих — нет");
                Clean(sim);
            }
            return r.Depth;
        }

        float with = Run("", check: true), without = Run(";ragdoll.selfCollision=0", check: false);
        Assert.True(with <= 0.01f, $"труп сложен сам в себя на {with} м");
        Assert.True(without >= 0.05f, $"без самоколлизии труп не сложился ({without} м) — тест не проверяет то, что должен");
    }

    /// <summary>
    /// <c>physics.selfContacts</c>: предплечье поставлено в торс. Отпущенное выходит не быстрее <c>PopSpeed</c> — нарушений нет;
    /// удержанное — нарушение, когда эпизод пары длится дольше <c>(глубина − допуск) / (¼ PopSpeed) + </c><see cref="CorpseInvariants.SelfExitGrace"/>:
    /// у пары «таз — предплечье» (0,1 − 0,06) / 0,25 + 0,25 ≈ 0,41 с.
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
