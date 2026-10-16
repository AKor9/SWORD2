using System.Numerics;
using System.Reflection;
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
/// Сон трупа в игре: трупы засыпают; сон и счётчик покоя — в хеше <c>physics</c>; ручка физики будит спящих в том же тике,
/// ручка отдачи — нет; реплей — те же хеши; <c>physics.sleep</c> и <c>physics.sleepContacts</c> ловят неисправность.
/// </summary>
public sealed class RagdollSleepTests(ITestOutputHelper output)
{
    [Fact]
    public void Corpses_FallAsleep_HashDescribeFields_PhysicsKnobWakes_RecoilKnobDoesNot_ReplaySameHashes()
    {
        var (sim, physics) = Create(TestAssets.Assets, "arena.enemies=3;combat.enemiesAttack=false");
        var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
        Step(sim, Place(1, 4, -4), Place(2, 4.5f, -4.2f), Place(3, 4.2f, -3.6f));
        Step(sim, Kill(1), Kill(2), Kill(3));
        var enemies = new[] { Fighter(sim, 1), Fighter(sim, 2), Fighter(sim, 3) };
        var bodies = enemies.Select(e => Body(sim, e)).ToArray();
        int asleepAt = -1;
        for (int t = 1; t <= 1200 && asleepAt < 0; t++)
        {
            sim.Step();
            if (bodies.All(physics.IsAsleep)) asleepAt = t;
        }
        output.WriteLine($"три трупа уснули через {asleepAt / 120f:F2} с");
        Assert.True(asleepAt > 0, "три трупа не уснули за 10 с");

        // хеш: сон и счётчик покоя
        ulong before = sim.HashState().Parts["physics"];
        Assert.True(physics.IsAsleep(bodies[0]));
        Assert.True(physics.RestTime(bodies[0]) >= physics.Settings.SleepTime);
        Assert.Equal(3, physics.AsleepCount);
        sim.Step();
        Assert.NotEqual(before, sim.HashState().Parts["physics"]);   // спящий в хеше меняет только счётчик покоя

        // ручка отдачи живому физику не меняет — не будит; трение о землю — будит в том же тике
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of(RagdollKnobs.EnemyRecoil), Value = 2 });
        sim.Step();
        Assert.All(bodies, b => Assert.True(physics.IsAsleep(b), "ручка отдачи разбудила"));
        physics.EnableTrace();
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of(RagdollKnobs.Friction), Value = 0.6f });
        sim.Step();
        Assert.All(bodies, b => Assert.False(physics.IsAsleep(b), "ручка трения не разбудила"));
        Assert.Equal(3, physics.Trace!.Run.Wakes(WakeReason.Settings));
        physics.DisableTrace();
        for (int t = 0; t < 180; t++) sim.Step();   // до возрождения (8 с после смерти) — засыпают снова: разбуженные лежат, как лежали
        Assert.All(bodies, b => Assert.True(physics.IsAsleep(b), "после ручки трупы не уснули снова"));

        // реплей: засыпания и пробуждения — те же хеши каждого тика
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        var again = Simulation.Build(replay.CreateConfig(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe(_ => { })]);
        var player = again.Play(replay);
        while (again.Tick < replay.Ticks) again.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(sim.HashState().Total, again.HashState().Total);
        Clean(sim);
    }

    /// <summary>
    /// Нижний труп построен заново в другом месте — запись тела будит лежавших на нём по позе до записи: верхний просыпается в тот
    /// же тик и ложится на землю, а не висит в воздухе.
    /// </summary>
    [Fact]
    public void CorpseAsleepOnCorpse_BottomRebuiltElsewhere_TopWakesAndFalls()
    {
        var (sim, physics) = Create(TestAssets.Assets, "arena.enemies=2;combat.enemiesAttack=false");
        Step(sim, Place(1, 4, -4), Place(2, 7, -4, 1.2f));
        Step(sim, Kill(1), Kill(2));
        var bottom = Body(sim, Fighter(sim, 1));
        var top = Body(sim, Fighter(sim, 2));
        bool Both() => physics.IsAsleep(bottom) && physics.IsAsleep(top);
        for (int t = 0; t < 900 && !Both(); t++) sim.Step();
        // верхний — поверх нижнего: сдвиг тел командой сцены, падает и засыпает на нём. Таз верхнего — на 10 см по −X от таза нижнего: при
        // сдвиге по +X с самоколлизией голова верхнего свисала с нижнего и качалась маятником до возрождения (8 с), оба
        // не засыпали; так — засыпают на тике ~800.
        var shift = physics.Get(physics.Body(bottom, 0)).Position - physics.Get(physics.Body(top, 0)).Position + new Vector3(-0.1f, 0.35f, 0.05f);
        Step(sim, Shift(2, shift));
        for (int t = 0; t < 900 && !Both(); t++) sim.Step();
        Assert.True(Both(), "трупы не уснули друг на друге");
        float lying = physics.Get(physics.Body(bottom, 0)).Position.Y, before = physics.Get(physics.Body(top, 0)).Position.Y;
        Assert.True(before > lying + 0.1f, $"верхний не на нижнем: таз {before} м, у нижнего {lying} м");

        Step(sim, Place(1, -6, 6));   // нижний построен заново в другом месте — как возрождение
        Assert.False(physics.IsAsleep(top), "верхний не проснулся, когда из-под него ушёл нижний");
        for (int t = 0; t < 120; t++) sim.Step();
        float after = physics.Get(physics.Body(top, 0)).Position.Y;
        output.WriteLine($"таз верхнего: на нижнем {before:F3} м → через 1 с {after:F3} м (лежащий на земле — {lying:F3})");
        Assert.True(after < lying + 0.05f, $"верхний висит: таз {after} м (лежащий на земле — {lying} м)");
        Clean(sim);
    }

    public enum SleepFault { Moved, Velocity, KinematicInside }

    /// <summary>
    /// Неисправности сна ловятся на своём тике: спящее тело сдвинуто мимо физики (как если бы шаг двигал спящего) и спящему дана скорость
    /// (сон без обнуления) — <c>physics.sleep</c>; тело живого поставлено в спящий труп после шага (пробуждение пропущено) —
    /// <c>physics.sleepContacts</c>. Порча спящего — отражением в массив тел мира (любая запись через API будит).
    /// </summary>
    [Theory]
    [InlineData(SleepFault.Moved, "physics.sleep")]
    [InlineData(SleepFault.Velocity, "physics.sleep")]
    [InlineData(SleepFault.KinematicInside, "physics.sleepContacts")]
    public void SleepInvariants_CatchFault_OnItsTick(SleepFault fault, string id)
    {
        var holder = new IPhysicsWorld[1];
        const ulong tick = 720;
        var (sim, physics) = Create(TestAssets.Assets, "arena.enemies=1;combat.enemiesAttack=false", (GameStages.Physics, new AfterStep(tick, (p, w) =>
        {
            var corpse = FighterBodies.Handle(w.Get<BodyRef>(EnemyIn(w)));
            var torso = p.Body(corpse, BodySegs.Torso);
            switch (fault)
            {
                case SleepFault.Moved: Poke(p, torso, (ref BodyState s) => s.Position += new Vector3(0, 0, 0.05f)); break;
                case SleepFault.Velocity: Poke(p, torso, (ref BodyState s) => s.Velocity = new Vector3(0.3f, 0, 0)); break;
                case SleepFault.KinematicInside:
                    var living = p.Body(FighterBodies.Handle(w.Get<BodyRef>(PlayerIn(w))), BodySegs.Torso);
                    p.Place(living, p.Get(torso).Position, p.Get(living).Rotation);   // кинематическое тело — не будит спящий
                    break;
            }
        }, holder)));
        holder[0] = physics;
        Step(sim, Place(1, -6, -6, 0.4f));
        Step(sim, Kill(1));
        while (sim.Tick < tick) sim.Step();
        Assert.True(physics.IsAsleep(Body(sim, Fighter(sim, 1))), "труп не уснул до порчи");
        Clean(sim);
        sim.Step();
        var hit = sim.Invariants.Recent().FirstOrDefault(v => v.Id == id);
        Assert.True(hit.Id == id, $"{id} не поймал {fault}; нарушения: {string.Join("; ", sim.Invariants.Recent().Take(3))}");
        Assert.Equal((tick, GameStages.Physics), (hit.Tick, hit.Stage));
        output.WriteLine($"{fault}: {hit}");
    }

    private delegate void BodyEdit(ref BodyState s);

    /// <summary>Порча тела мимо API мира (массив тел <see cref="PhysicsWorld"/> — отражением): сон так не снимается.</summary>
    private static void Poke(IPhysicsWorld physics, BodyHandle body, BodyEdit edit)
    {
        var bodies = (BodyState[])typeof(PhysicsWorld).GetField("_bodies", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(physics)!;
        edit(ref bodies[body.Index]);
    }

    private static Entity EnemyIn(World world) => Slot(world, 1);

    private static Entity PlayerIn(World world) => Slot(world, 0);

    private static Entity Slot(World world, int slot)
    {
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет бойца на месте {slot}");
    }
}
