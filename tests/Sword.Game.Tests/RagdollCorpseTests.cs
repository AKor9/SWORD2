using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Труп на физике. Контент — синтетический (<see cref="TestAssets"/>): стопы скелета тестов — в земле на 4,5 см, поэтому труп на нём
/// «выходит» из земли.
/// </summary>
public sealed class RagdollCorpseTests(ITestOutputHelper output)
{
    /// <summary>Модуль теста: берёт физику после сборки мира и (по желанию) ставит свою систему в стадию.</summary>
    internal sealed class Probe(Action<IPhysicsWorld> take, params (string Stage, ISystem System)[] systems) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b)
        {
            b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
            foreach (var (stage, system) in systems) b.System(stage, system);
        }
    }

    internal sealed record Game(Simulation Sim, IPhysicsWorld Physics);

    internal static Game Create(CharacterAssets assets, string sets, params (string Stage, ISystem System)[] systems)
    {
        IPhysicsWorld? physics = null;
        var sim = Simulation.Build(ConfigSet.Empty(sets.Split(';', StringSplitOptions.RemoveEmptyEntries)), GameStages.All,
            b => GameCore.Configure(b, assets), [.. Bootstrap.SwordSimulation.Modules(), new Probe(p => physics = p, systems)]);
        return new Game(sim, physics!);
    }

    private static Game Create(string sets, params (string Stage, ISystem System)[] systems) => Create(TestAssets.Assets, sets, systems);

    internal static void Step(Simulation sim, params SceneCommand[] commands)
    {
        foreach (var c in commands) sim.Enqueue(c);
        sim.Step();
    }

    internal static Entity Fighter(Simulation sim, int slot)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<Fighter>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет бойца на месте {slot}");
    }

    internal static ArticulationHandle Body(Simulation sim, Entity e) => FighterBodies.Handle(sim.World.Get<BodyRef>(e));

    internal static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    internal static SceneCommand Kill(int slot) => new() { Action = SceneAction.Kill, Slot = slot };

    internal static SceneCommand Place(int slot, float x, float z, float facing = 0) =>
        new() { Action = SceneAction.Place, Slot = slot, Position = new Vector2(x, z), Facing = facing };

    internal static SceneCommand Shift(int slot, Vector3 offset) => new() { Action = SceneAction.ShiftBodies, Slot = slot, Offset = offset };

    /// <summary>Итоги тела бойца (центр масс, скорость, энергии) — по виду тел.</summary>
    internal static CorpseMetrics.Totals Totals(Simulation sim, Entity e) => CorpseMetrics.Of(sim.World.View<IBodyView>(), sim.World.Get<BodyRef>(e));

    /// <summary>Наибольшая скорость тел сочленения, м/с.</summary>
    internal static float MaxSpeed(IPhysicsWorld physics, ArticulationHandle a)
    {
        float v = 0;
        for (int k = 0; k < physics.BodyCount(a); k++) v = MathF.Max(v, physics.Get(physics.Body(a, k)).Velocity.Length());
        return v;
    }

    /// <summary>Наибольшая глубина торцов тел сочленения в земле арены, м.</summary>
    internal static float Depth(IPhysicsWorld physics, ArticulationHandle a)
    {
        Span<GroundTouch> touches = stackalloc GroundTouch[IGround.MaxTouches];
        float depth = 0;
        for (int k = 0; k < physics.BodyCount(a); k++)
        {
            var s = physics.Get(physics.Body(a, k));
            foreach (var end in (ReadOnlySpan<Vector3>)[s.EndA, s.EndB])
            {
                int n = ArenaGround.Instance.Touches(end, s.Radius, touches);
                for (int t = 0; t < n; t++) depth = MathF.Max(depth, touches[t].Depth);
            }
        }
        return depth;
    }

    // ── переход в труп ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Смерть командой (Gameplay) и пробой (Settle): все 14 тел — на физике с профилем трупа, 13 суставов (сустав j — ребёнок тела j + 1),
    /// запястье выключено — меч выпадает, прочие включены; оси шарниров — единичные, знак сгиба +1; привязка — по позе смерти (сразу после
    /// перехода ошибка точки 0); скорости тел урезаны до 6 м/с и 20 рад/с; привязка скина снята. Повторная регистрация (Gameplay видит смерть
    /// прошлого тика) труп не трогает: привязка суставов та же.
    /// </summary>
    [Fact]
    public void Death_BecomesCorpse_AllBodiesOnPhysics_WristOff_JointsBoundAtDeathPose()
    {
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false");
        for (int t = 0; t < 60; t++) sim.Step();
        var rules = sim.World.Resource<RagdollRules>();
        var byCommand = Fighter(sim, 1);
        var byProbe = Fighter(sim, 2);
        sim.Enqueue(Kill(1));
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.Torso, Damage = 1000 });
        sim.Step();
        foreach (var e in new[] { byCommand, byProbe })
        {
            Assert.True(sim.World.Has<Dead>(e));
            var a = Body(sim, e);
            Assert.True(sim.World.View<ISkinView>().TryGet(sim.World.Get<BodyRef>(e), out _, out _), "привязка скина снята");
            Assert.Equal(CorpseJoints.Count, physics.JointCount(a));
            for (int k = 0; k < BodySegs.Bodies; k++)
            {
                var s = physics.Get(physics.Body(a, k));
                Assert.Equal(BodyMode.Simulated, s.Mode);
                Assert.Equal(1, (int)s.Profile);
            }
            for (int j = 0; j < CorpseJoints.Count; j++)
            {
                var d = physics.Joint(a, j);
                Assert.Equal((CorpseJoints.Parent(j + 1), j + 1), (d.Parent, d.Child));
                Assert.Equal(j != CorpseJoints.Wrist, physics.IsJointEnabled(a, j));
                if (d.Kind == JointKind.Hinge) Assert.True(MathF.Abs(d.HingeAxis.Length() - 1) < 1e-4f && d.BendSign == 1, $"шарнир {j}: ось {d.HingeAxis}, знак {d.BendSign}");
                Assert.Equal(new Vector3(0, -physics.Get(physics.Body(a, j + 1)).HalfLength, 0), d.ChildAnchor);
            }
            Assert.Equal(JointKind.SwingTwist, physics.Joint(a, CorpseJoints.Wrist).Kind);
        }
        // Смерть пробой — в Settle: шага ещё не было, привязка — ошибка точки 0 и скорости урезаны.
        var probe = Body(sim, byProbe);
        for (int j = 0; j < CorpseJoints.Count; j++) Assert.True(physics.MeasureJoint(probe, j).PointError < 1e-4f, $"сустав {j}");
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var s = physics.Get(physics.Body(probe, k));
            Assert.True(s.Velocity.Length() <= rules.DeathMaxSpeed + 1e-4f && s.AngularVelocity.Length() <= rules.DeathMaxAngularSpeed + 1e-3f);
        }
        var bindings = Enumerable.Range(0, CorpseJoints.Count).Select(j => physics.Binding(probe, j)).ToArray();
        sim.Step();   // Gameplay видит Added<Dead> прошлого тика — уже труп, привязка та же
        Assert.Equal(bindings, Enumerable.Range(0, CorpseJoints.Count).Select(j => physics.Binding(probe, j)));
        for (int t = 0; t < 120; t++) sim.Step();
        Clean(sim);
    }

    /// <summary>
    /// Анатомия шарниров: у синтетического скелета клипы колени и локти почти не сгибают — ось запасная («вправо» бойца со знаком сгиба);
    /// у Manny пака — найдена по клипам (−Y кости, <see cref="RagdollCorpseContentTests"/>). Ось в системе капсулы — поперёк оси капсулы.
    /// </summary>
    [Fact]
    public void Hinges_FallbackAxis_OnSyntheticRig_AcrossTheBone()
    {
        var anatomy = TestAssets.Assets.Hinges;
        foreach (int seg in new[] { BodySegs.FArmL, BodySegs.FArmR, BodySegs.ShinL, BodySegs.ShinR }) Assert.False(anatomy.Found(seg));
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false");
        Step(sim, Place(1, 3, 3, 0.7f));
        Step(sim, Kill(1));
        var a = Body(sim, Fighter(sim, 1));
        foreach (int j in new[] { CorpseJoints.JointOf(BodySegs.FArmL), CorpseJoints.JointOf(BodySegs.ShinR) })
        {
            var d = physics.Joint(a, j);
            Assert.Equal(JointKind.Hinge, d.Kind);
            Assert.True(MathF.Abs(d.HingeAxis.Y) < 0.2f, $"сустав {j}: ось шарнира {d.HingeAxis} вдоль кости");
            var m = physics.MeasureJoint(a, j);
            Assert.True(m.Excess < 0.05f, $"сустав {j}: поза смерти вне пределов шарнира на {m.Excess} рад (сгиб {m.Bend})");
        }
    }

    // ── покой и энергия ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Покой трупа (окно 5 с после 2 с падения; допуски ~2–4× к худшему замеру): скорость центра масс, м/с; кинетическая энергия в конце
    /// окна, Дж; подъём полной энергии над началом окна, Дж.
    /// </summary>
    internal const float RestComSpeed = 0.2f, RestKineticEnd = 0.03f, RestEnergyRise = 0.05f;

    /// <summary>
    /// Труп на ровной земле: за 3 с ложится (синтетический риг перекатывается до 2,25 с: разрыв суставов сводится не быстрее
    /// <see cref="PhysicsSettings.JointPopSpeed"/>), дальше 5 с покоя; тела не глубже 1 см, суставы держат, инварианты чисты.
    /// </summary>
    [Fact]
    public void Corpse_OnFlatGround_Settles_EnergyAtRestDoesNotGrow()
    {
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false");
        Step(sim, Place(1, 4, 4, 1.1f));
        Step(sim, Kill(1));
        var e = Fighter(sim, 1);
        var rest = RestWindow(sim, physics, e, settle: 360, window: 600);
        output.WriteLine($"ровная земля: {rest}");
        Assert.True(rest.ComSpeed <= RestComSpeed && rest.KineticEnd <= RestKineticEnd, rest.ToString());
        Assert.True(rest.EnergyRise <= RestEnergyRise, rest.ToString());
        Assert.True(rest.Depth <= 0.01f && rest.Point <= 0.01f, rest.ToString());
        Clean(sim);
    }

    internal readonly record struct Rest(float ComSpeed, float ComDrift, double EnergyRise, float KineticMax, float KineticEnd, float Depth, float Point, float Excess)
    {
        public override string ToString() =>
            FormattableString.Invariant($"ЦМ ≤ {ComSpeed:F4} м/с, сдвиг {ComDrift:F4} м; энергия над началом окна ≤ {EnergyRise:G3} Дж; кинетическая ≤ {KineticMax:F4}, в конце {KineticEnd:F5} Дж; глубина ≤ {Depth:F4} м; суставы — точка {Point * 1000:F2} мм, выход {Excess:F4} рад");
    }

    /// <summary>Окно покоя: <paramref name="settle"/> тиков — лечь, затем <paramref name="window"/> тиков — замер центра масс, энергии, глубины, суставов.</summary>
    internal static Rest RestWindow(Simulation sim, IPhysicsWorld physics, Entity e, int settle, int window)
    {
        for (int t = 0; t < settle; t++) sim.Step();
        var a = Body(sim, e);
        var start = Totals(sim, e);
        float comSpeed = 0, depth = 0, point = 0, excess = 0, keMax = 0;
        double rise = double.NegativeInfinity;
        var last = start;
        for (int t = 0; t < window; t++)
        {
            sim.Step();
            last = Totals(sim, e);
            comSpeed = MathF.Max(comSpeed, last.ComVelocity.Length());
            rise = Math.Max(rise, last.Energy - start.Energy);
            keMax = MathF.Max(keMax, last.KineticEnergy);
            depth = MathF.Max(depth, Depth(physics, a));
            for (int j = 0; j < physics.JointCount(a); j++)
            {
                if (!physics.IsJointEnabled(a, j)) continue;
                var m = physics.MeasureJoint(a, j);
                (point, excess) = (MathF.Max(point, m.PointError), MathF.Max(excess, m.Excess));
            }
        }
        return new Rest(comSpeed, Vector3.Distance(start.Com, last.Com), rise, keMax, last.KineticEnergy, depth, point, excess);
    }

    // ── конечность в земле, утопленный труп, трупы внахлёст ─────────────────────────────────────────────────────────

    /// <summary>Система теста: в тике <paramref name="tick"/> после шага физики — действие над физикой (порча, сдвиг тела).</summary>
    internal sealed class AfterStep(ulong tick, Action<IPhysicsWorld, World> act, IPhysicsWorld[] physics) : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("test.after-step")).After(GameLabels.CorpseRoot).Writes<Transform>();

        public void Run(in SystemContext ctx)
        {
            if (ctx.World.Resource<SimTime>().Tick == tick) act(physics[0], ctx.World);
        }
    }

    /// <summary>
    /// Труп вывернут в землю (лежащий труп повёрнут как целое вокруг таза на 0,35 рад — суставы целы, ноги или голова с руками уходят в землю
    /// до ~0,3 м; без скорости, после шага): землю покидает не быстрее скорости выталкивания, через суставы — весь труп, но не разгоняется:
    /// скорость центра масс не выше 1,5 · PopSpeed, кинетическая энергия — не выше <see cref="LimbKinetic"/>, через 2 с — покой. Конец —
    /// до возрождения (8 с).
    /// </summary>
    [Fact]
    public void LimbTwistedIntoGround_DoesNotLaunchCorpse()
    {
        var holder = new IPhysicsWorld[1];
        const ulong twist = 400;   // труп уже лёг (3,3 с)
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Physics, new AfterStep(twist, (p, w) =>
        {
            var a = FighterBodies.Handle(w.Get<BodyRef>(FighterIn(w, 1)));
            var pivot = p.Get(p.Body(a, BodySegs.Pelvis)).Position;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.35f);
            for (int k = 0; k < BodySegs.Bodies; k++)
            {
                var b = p.Body(a, k);
                var s = p.Get(b);
                p.Place(b, pivot + Vector3.Transform(s.Position - pivot, turn), Quaternion.Normalize(turn * s.Rotation));
            }
        }, holder)));
        holder[0] = physics;
        Step(sim, Place(1, 6, 2));
        Step(sim, Kill(1));
        var e = Fighter(sim, 1);
        while (sim.Tick < twist) sim.Step();
        float pop = sim.World.Resource<RagdollRules>().PopSpeed;
        float keBefore = Totals(sim, e).KineticEnergy;
        float comSpeed = 0, ke = 0, depth = 0;
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            var totals = Totals(sim, e);
            comSpeed = MathF.Max(comSpeed, totals.ComVelocity.Length());
            ke = MathF.Max(ke, totals.KineticEnergy);
            if (t == 0) depth = Depth(physics, Body(sim, e));
        }
        var rest = RestWindow(sim, physics, e, settle: 240, window: 120);
        output.WriteLine(FormattableString.Invariant($"вывернут в землю на {depth:F3} м: до — кинетическая {keBefore:F3} Дж; после — ЦМ ≤ {comSpeed:F3} м/с, кинетическая ≤ {ke:F2} Дж; через 2 с — {rest}"));
        Assert.True(depth > 0.1f, $"в землю ушло только {depth} м");
        Assert.True(comSpeed <= 1.5f * pop, $"ЦМ разогнался до {comSpeed} м/с");
        Assert.True(ke <= LimbKinetic, $"кинетическая энергия {ke} Дж");
        Assert.True(rest.ComSpeed <= RestComSpeed && rest.KineticEnd <= RestKineticEnd && rest.Depth <= 0.01f, rest.ToString());
        Clean(sim);
    }

    /// <summary>
    /// Предел кинетической энергии трупа при выходе вывернутой конечности из земли, Дж: с самоколлизией выходит весь труп, а весь труп
    /// со скоростью выталкивания 1 м/с — ½·74,8·1² ≈ 37 Дж; больше — разгон, не выход.
    /// </summary>
    private const float LimbKinetic = 40;

    private static Entity FighterIn(World world, int slot)
    {
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет бойца на месте {slot}");
    }

    /// <summary>
    /// Утопленный на 0,3 м труп выходит не быстрее <c>1,5 · PopSpeed · dt</c> за тик (запас 1,5: сустав, тянущий соседа вниз, даёт новую
    /// глубину, которую земля решает целиком), выходит весь и не подпрыгивает; ручка «выталкивание» замедляет выход.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public void SunkCorpse_ExitsNoFasterThanPopSpeed(float popSpeed)
    {
        var (sim, physics) = Create(FormattableString.Invariant($"arena.enemies=1;combat.enemiesAttack=false;ragdoll.popSpeed={popSpeed}"));
        Step(sim, Place(1, -4, 5));
        Step(sim, Kill(1));
        var e = Fighter(sim, 1);
        for (int t = 0; t < 360; t++) sim.Step();
        var a = Body(sim, e);
        float y0 = Totals(sim, e).Com.Y;
        Step(sim, Shift(1, new Vector3(0, -0.3f, 0)));
        float limit = popSpeed * sim.World.Resource<SimTime>().Dt, rise = 0, prev = Totals(sim, e).Com.Y;
        int ticks = 0;
        while (Depth(physics, a) > 0.01f && ticks < 600)
        {
            sim.Step();
            float y = Totals(sim, e).Com.Y;
            rise = MathF.Max(rise, y - prev);
            prev = y;
            ticks++;
        }
        float top = 0;
        for (int t = 0; t < 240; t++)
        {
            sim.Step();
            top = MathF.Max(top, Totals(sim, e).Com.Y - y0);
        }
        output.WriteLine(FormattableString.Invariant($"PopSpeed {popSpeed}: вышел за {ticks} тиков, подъём ЦМ за тик ≤ {rise * 1000:F2} мм (предел {limit * 1000:F2}), над прежним ≤ {top * 1000:F1} мм"));
        Assert.True(ticks < 600, "не вышел из земли");
        Assert.True(rise <= limit * 1.5f, $"подъём за тик {rise} при пределе {limit}");
        Assert.True(top <= 0.02f, $"подпрыгнул на {top} м");
        Clean(sim);
    }

    /// <summary>
    /// Два трупа внахлёст (тазы в 0,2 м): расходятся не быстрее 1,5 · PopSpeed, без разлёта (тазы в конце не дальше 2,5 м — трупы в рост
    /// 1,8 м падают в разные стороны), оба успокоились.
    /// </summary>
    [Fact]
    public void TwoCorpsesOverlapping_SeparateCalmly_S4()
    {
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false");
        Step(sim, Place(1, -0.36f, 1.5f), Place(2, 0.36f, 1.5f));
        Step(sim, Kill(1), Kill(2));
        var (e1, e2) = (Fighter(sim, 1), Fighter(sim, 2));
        var (a1, a2) = (Body(sim, e1), Body(sim, e2));
        var p1 = physics.Get(physics.Body(a1, 0)).Position;
        var p2 = physics.Get(physics.Body(a2, 0)).Position;
        Step(sim, Shift(2, new Vector3(p1.X - p2.X + 0.2f, 0, p1.Z - p2.Z)));
        Assert.True(Vector3.Distance(physics.Get(physics.Body(a1, 0)).Position, physics.Get(physics.Body(a2, 0)).Position) < 0.25f);
        float pop = sim.World.Resource<RagdollRules>().PopSpeed, rel = 0, relAll = 0;
        for (int t = 0; t < 600; t++)
        {
            sim.Step();
            var (c1, c2) = (Totals(sim, e1), Totals(sim, e2));
            float r = new Vector2(c1.ComVelocity.X - c2.ComVelocity.X, c1.ComVelocity.Z - c2.ComVelocity.Z).Length();
            relAll = MathF.Max(relAll, r);
            if (t < 30) rel = MathF.Max(rel, r);   // расходятся из внахлёста — первые 0,25 с (дальше — падение каждого в свою сторону)
        }
        float dist = Vector3.Distance(physics.Get(physics.Body(a1, 0)).Position, physics.Get(physics.Body(a2, 0)).Position);
        var (k1, k2) = (Totals(sim, e1).KineticEnergy, Totals(sim, e2).KineticEnergy);
        output.WriteLine(FormattableString.Invariant($"s4: относительная скорость ЦМ за 0,25 с ≤ {rel:F3} м/с, за 5 с ≤ {relAll:F3}; тазы в конце в {dist:F3} м, кинетическая {k1:F4} и {k2:F4} Дж"));
        Assert.True(rel <= 1.5f * pop, $"расходятся со скоростью {rel} м/с");
        Assert.True(dist <= 2.5f, $"разлетелись: тазы в {dist} м");
        Assert.True(k1 <= 0.1f && k2 <= 0.1f, $"не успокоились: {k1}, {k2} Дж");
        Clean(sim);
    }

    // ── меч, возрождение, корень ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Меч выпадает при каждой смерти: через 3 с лежит нижним концом у земли, в тела трупа не вошёл глубже допуска <c>physics.selfContacts</c>,
    /// хват ушёл от кисти; клинок-отрезок мёртвого стоит, где был в тик смерти.
    /// </summary>
    [Fact]
    public void Sword_DropsFromHand_LiesOnGround()
    {
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false");
        Step(sim, Place(1, 2, 6, 2.2f));
        for (int t = 0; t < 30; t++) sim.Step();
        Step(sim, Kill(1));
        var e = Fighter(sim, 1);
        var a = Body(sim, e);
        var blade = sim.World.Get<Blade>(e);
        for (int t = 0; t < 360; t++) sim.Step();
        var sword = physics.Get(physics.Body(a, BodySegs.Weapon));
        var forearm = physics.Get(physics.Body(a, BodySegs.FArmR));
        float groundA = ArenaTerrain.Height(sword.EndA.X, sword.EndA.Z), groundB = ArenaTerrain.Height(sword.EndB.X, sword.EndB.Z);
        output.WriteLine(FormattableString.Invariant($"меч: концы на {sword.EndA.Y - groundA:F3} и {sword.EndB.Y - groundB:F3} м над землёй, скорость {sword.Velocity.Length():F4}, хват от кисти {Vector3.Distance(sword.EndA, forearm.EndB):F3} м"));
        Assert.False(physics.IsJointEnabled(a, CorpseJoints.Wrist));
        Assert.True(MathF.Min(sword.EndA.Y - groundA, sword.EndB.Y - groundB) <= sword.Radius + 0.02f, "меч не лежит на земле");
        Assert.True(Vector3.Distance(sword.EndA, forearm.EndB) > 0.2f, "меч в руке");
        for (int k = 0; k < BodySegs.Bodies; k++)
            if (CorpseJoints.Collides(k, BodySegs.Weapon))
                Assert.True(SelfOverlap.Depth(sword, physics.Get(physics.Body(a, k)), 1) <= CorpseInvariants.SelfTolerance, $"меч в теле {BodySegs.Name(k)}");
        Assert.True(sword.Velocity.Length() <= 0.02f);
        Assert.Equal(blade, sim.World.Get<Blade>(e));
        Clean(sim);
    }

    /// <summary>
    /// Возрождение (команда «поставить», сброс R, враг через 8 с) возвращает кинематику: тела по позе, профиль по умолчанию, без скачка
    /// скорости; следующая смерть — снова труп.
    /// </summary>
    [Fact]
    public void Revival_ReturnsKinematics_DefaultProfile_JointsEnabled()
    {
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false");
        for (int t = 0; t < 30; t++) sim.Step();
        Step(sim, Kill(1), Kill(2));
        for (int t = 0; t < 120; t++) sim.Step();
        var (e1, e2) = (Fighter(sim, 1), Fighter(sim, 2));
        Assert.True(Corpse.IsCorpse(physics, Body(sim, e1)));
        Step(sim, Place(1, 3, 3));   // команда сцены — Gameplay того же тика строит тело
        AssertAlive(physics, Body(sim, e1));
        for (int t = 0; t < 30; t++) sim.Step();
        AssertAlive(physics, Body(sim, e1));
        while (sim.World.Has<Dead>(e2)) sim.Step();   // возрождение врага через 8 с (Aftermath) — тело ставит Gameplay следующего тика
        sim.Step();
        AssertAlive(physics, Body(sim, e2));
        Step(sim, Kill(1));
        Assert.True(Corpse.IsCorpse(physics, Body(sim, e1)));
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });   // сброс R — все построены заново в Begin
        sim.Step();
        AssertAlive(physics, Body(sim, e1));
        for (int t = 0; t < 30; t++) sim.Step();
        Clean(sim);
    }

    private static void AssertAlive(IPhysicsWorld physics, ArticulationHandle a)
    {
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var s = physics.Get(physics.Body(a, k));
            Assert.Equal((BodyMode.Kinematic, (byte)0, false), (s.Mode, s.Profile, s.Grounded));
            Assert.True(s.Velocity.Length() < 30, $"тело {k}: скорость {s.Velocity.Length()}");
        }
        for (int j = 0; j < physics.JointCount(a); j++) Assert.True(physics.IsJointEnabled(a, j), $"сустав {j} выключен у живого");
    }

    /// <summary>
    /// Корень мёртвого идёт за тазом: после смерти игрока (x, z) корня — ровно (x, z) таза каждый тик, курс прежний; враги идут к трупу
    /// игрока (×0,2 — не бьют) и встают у его таза, а не у места смерти.
    /// </summary>
    [Fact]
    public void DeadRoot_FollowsPelvis_EnemiesWalkToPlayersCorpse()
    {
        var (sim, physics) = Create("arena.enemies=3;combat.enemiesAttack=false");
        for (int t = 0; t < 10; t++) sim.Step();
        var player = Fighter(sim, 0);
        Step(sim, Kill(0));
        float facing = sim.World.Get<Transform>(player).Facing;
        var a = Body(sim, player);
        for (int t = 0; t < 1200; t++)
        {
            sim.Step();
            var root = sim.World.Get<Transform>(player);
            var pelvis = physics.Get(physics.Body(a, BodySegs.Pelvis)).Position;
            Assert.Equal((pelvis.X, pelvis.Z, facing), (root.Position.X, root.Position.Z, root.Facing));
        }
        var at = sim.World.Get<Transform>(player).Position;
        foreach (int slot in new[] { 1, 2, 3 })
        {
            float d = Vector3.Distance(sim.World.Get<Transform>(Fighter(sim, slot)).Position with { Y = 0 }, at with { Y = 0 });
            Assert.True(d < 3.5f, $"враг {slot} в {d} м от трупа игрока");
        }
        Clean(sim);
    }

    // ── ручки, реплей, пересборка ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Команда ручки «Рэгдолл» посреди прогона с трупами меняет правила, настройки физики и профиль трупа до шага того же тика
    /// и воспроизводится реплеем — те же хеши всех участников.
    /// </summary>
    [Fact]
    public void RagdollKnob_MidRun_ChangesPhysics_ReplaysWithSameHashes()
    {
        var (sim, physics) = Create("arena.enemies=2;combat.enemiesAttack=false");
        var knobs = sim.Tuning.Where(k => k.Section == RagdollKnobs.Section).ToList();
        Assert.Equal(["ragdoll.friction", "ragdoll.pushForce", "ragdoll.popSpeed", "ragdoll.jointCompliance", "ragdoll.jointFriction",
            "ragdoll.selfCollision", "ragdoll.corpseMaxSpeed", "ragdoll.corpseMaxAngularSpeed", "ragdoll.deathMaxSpeed", "ragdoll.deathMaxAngularSpeed",
            "ragdoll.enemyRecoil", "ragdoll.playerRecoil", "ragdoll.recoilMaxSpeed", "ragdoll.jointPopSpeed", "ragdoll.pairFriction", "ragdoll.sleepSpeed",
            "ragdoll.sleepBodySpeed", "ragdoll.sleepTime"],
            knobs.Select(k => k.Name));   // отдача — доли врагу и игроку, предел; разрыв суставов, трение пар и сон; трение суставов, самоколлизия
        var d = new RagdollSettings();
        Assert.Equal((d.JointPopSpeed, d.PairFriction, d.SleepSpeed, d.SleepBodySpeed, d.SleepTime),
            (physics.Settings.JointPopSpeed, physics.Settings.PairFriction, physics.Settings.SleepSpeed, physics.Settings.SleepBodySpeed, physics.Settings.SleepTime));
        Assert.Equal((2e-6f, 0f, 1f), (d.JointCompliance, d.JointFriction, d.SelfCollision));   // жёсткость пользователя, трения нет, капсулы
        Assert.Equal((d.JointFriction, d.SelfCollision), (physics.Settings.JointFriction, physics.Settings.SelfContactScale));
        Assert.Equal(d.PopSpeed, sim.Knob(RagdollKnobs.PopSpeed)!.Read(sim.World));
        Assert.Equal((d.PopSpeed, d.Friction, d.JointCompliance), (physics.Settings.PopSpeed, physics.Settings.StaticFriction, physics.Settings.JointLimitCompliance));
        Assert.Contains(sim.World.Systems, s => s.Label == "tuning.ragdoll" && s.Stage == GameStages.Begin);
        Assert.Contains(sim.World.Systems, s => s.Label == GameLabels.RagdollRules.Name && s.Stage == GameStages.Intent);
        var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
        for (int t = 0; t < 20; t++) sim.Step();
        Step(sim, Kill(1));
        for (int t = 0; t < 40; t++) sim.Step();
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of(RagdollKnobs.PopSpeed), Value = 2.5f });
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of(RagdollKnobs.CorpseMaxSpeed), Value = 9 });
        sim.Step();
        Assert.Equal(2.5f, sim.World.Resource<RagdollRules>().PopSpeed);
        Assert.Equal(2.5f, physics.Settings.PopSpeed);
        Assert.Equal(9f, physics.GetDynamics(1).MaxSpeed);
        Step(sim, Kill(2));
        for (int t = 0; t < 120; t++) sim.Step();
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        var again = Simulation.Build(replay.CreateConfig(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe(_ => { })]);
        var player = again.Play(replay);
        while (again.Tick < replay.Ticks) again.Step();
        Assert.Null(player.Divergence);
        Assert.Equal((int)replay.Ticks + 1, player.HashesChecked);   // и тика 0
        Assert.Equal(sim.HashState().Total, again.HashState().Total);
        Clean(sim);
    }

    /// <summary>
    /// Нечисловой труп: <c>physics.finite</c> называет его в том же тике, в Aftermath он строится заново на месте корня (живым).
    /// Живого с нечисловым телом (ошибка выше физики) пересборка не чинит.
    /// </summary>
    [Fact]
    public void NonFiniteCorpse_RebuiltAtRoot_AliveAndFinite()
    {
        var holder = new IPhysicsWorld[1];
        const ulong fault = 150;
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Physics, new AfterStep(fault, (p, w) =>
        {
            var b = p.Body(FighterBodies.Handle(w.Get<BodyRef>(FighterIn(w, 1))), BodySegs.ShinL);
            p.Place(b, new Vector3(float.NaN), Quaternion.Identity);
        }, holder)));
        holder[0] = physics;
        Step(sim, Place(1, 5, -5, 1));
        Step(sim, Kill(1));
        var e = Fighter(sim, 1);
        while (sim.Tick < fault) sim.Step();
        var root = sim.World.Get<Transform>(e).Position;
        sim.Step();   // тик порчи: после шага — нечисловая голень, в Aftermath — постройка заново
        Assert.Contains(sim.Invariants.Recent(), v => v.Id == "physics.finite" && v.Tick == fault);
        Assert.False(sim.World.Has<Dead>(e));   // построен заново в Aftermath — на месте корня (корень до шага тика порчи — рядом: труп лежит)
        Assert.True(Vector2.Distance(new Vector2(root.X, root.Z), new Vector2(sim.World.Get<Transform>(e).Position.X, sim.World.Get<Transform>(e).Position.Z)) < 0.05f);
        sim.Step();
        var a = Body(sim, e);
        Assert.True(Corpse.Finite(physics, a));
        AssertAlive(physics, a);
    }

    // ── инварианты-неисправности ────────────────────────────────────────────────────────────────────────────────────

    public enum CorpseFault { Modes, Speed, Joints, Ground, InArena, DeadRoot, SelfTable }

    /// <summary>Порча трупа в тике <paramref name="tick"/>: после шага (Physics).</summary>
    private sealed class Fault(CorpseFault fault, ulong tick, IPhysicsWorld[] physics) : IContinuousSystem
    {
        private Query _dead = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("test.fault")).Writes<Transform>();
            _dead = s.Query().With<Dead>().Write<Transform>();
            s.After(GameLabels.CorpseRoot);
        }

        public void Run(in SystemContext ctx)
        {
            if (ctx.World.Resource<SimTime>().Tick != tick) return;
            var p = physics[0];
            var world = ctx.World;
            var e = FighterIn(world, 1);
            var a = FighterBodies.Handle(world.Get<BodyRef>(e));
            var head = p.Body(a, BodySegs.Head);
            var s = p.Get(head);
            switch (fault)
            {
                case CorpseFault.Modes: p.SetMode(head, BodyMode.Kinematic); break;
                case CorpseFault.Speed: s.Velocity = new Vector3(100, 0, 0); p.Set(head, s); break;
                case CorpseFault.Joints: p.Place(head, s.Position + new Vector3(0, 0, 1), s.Rotation); break;
                case CorpseFault.Ground: p.Place(head, s.Position with { Y = -1 }, s.Rotation); break;
                case CorpseFault.InArena: p.Place(head, new Vector3(0, 1, 70), s.Rotation); break;
                case CorpseFault.SelfTable: p.SetSelfContacts(a, []); break;   // труп без самоколлизии
                case CorpseFault.DeadRoot:
                    foreach (var chunk in ctx.Iterate(_dead))
                    {
                        var roots = chunk.Write<Transform>();
                        for (int i = 0; i < roots.Length; i++) roots[i].Position += new Vector3(0.5f, 0, 0);
                    }
                    break;
            }
        }
    }

    /// <summary>Инварианты трупа ловят каждую порчу на её тике; до порчи — чисто.</summary>
    [Theory]
    [InlineData(CorpseFault.Modes, "physics.modes", GameStages.Physics)]
    [InlineData(CorpseFault.Speed, "physics.simulatedSpeed", GameStages.Physics)]
    [InlineData(CorpseFault.Joints, "physics.joints", GameStages.Physics)]
    [InlineData(CorpseFault.Ground, "physics.ground", GameStages.Physics)]
    [InlineData(CorpseFault.InArena, "physics.inArena", GameStages.Physics)]
    [InlineData(CorpseFault.DeadRoot, "combat.dead", GameStages.End)]
    [InlineData(CorpseFault.SelfTable, "physics.modes", GameStages.Physics)]
    public void CorpseInvariants_CatchFault_OnItsTick(CorpseFault fault, string id, string stage)
    {
        var holder = new IPhysicsWorld[1];
        const ulong tick = 120;
        IContinuousSystem system = new Fault(fault, tick, holder);
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Physics, system));
        holder[0] = physics;
        Step(sim, Place(1, -6, -6, 0.4f));
        Step(sim, Kill(1));
        while (sim.Tick < tick) sim.Step();   // номер идущего тика (SimTime.Tick) — сыграно до него: следующий шаг — тик порчи
        Clean(sim);
        sim.Step();
        var hit = sim.Invariants.Recent().FirstOrDefault(v => v.Id == id);
        Assert.True(hit.Id == id, $"{id} не поймал {fault}; нарушения: {string.Join("; ", sim.Invariants.Recent().Take(3))}");
        Assert.Equal((tick, stage), (hit.Tick, hit.Stage));
        output.WriteLine($"{fault}: {hit}");
    }

    /// <summary>
    /// <c>physics.ground</c>: глубже допуска — только выходя. Голова трупа, лежащего на ровной земле, поставлена на 4 см в землю (глубже
    /// допуска 3 см, мельче 0,3 м): отпущенная выходит (застарелая глубина — <c>PopSpeed·dt</c> за тик) — нарушений нет; удержанная на 4 см —
    /// нарушение, когда эпизод длится дольше <c>(4 − 3 см) / (¼ PopSpeed) + </c><see cref="CorpseInvariants.GroundExitGrace"/> ≈ 0,29 с (тик
    /// 395).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroundInvariant_DeepBody_OnlyExiting(bool hold)
    {
        var holder = new IPhysicsWorld[1];
        const ulong tick = 360;
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false", (GameStages.Physics, new SinkHead(tick, hold ? 60 : 1, holder)));
        holder[0] = physics;
        Step(sim, Place(1, 4, 4, 1.1f));
        Step(sim, Kill(1));
        while (sim.Tick < tick) sim.Step();
        Clean(sim);
        for (int t = 0; t < 60; t++) sim.Step();
        var hits = sim.Invariants.Recent().Where(v => v.Id == "physics.ground").OrderBy(v => v.Tick).ToArray();
        output.WriteLine(hits.Length > 0 ? $"{hits[0].Tick}: {hits[0].Details}" : "нарушений нет");
        if (hold) Assert.True(hits.Length > 0 && hits[0].Tick is >= tick + 33 and <= tick + 38, $"удержанная в земле голова: первое нарушение {(hits.Length > 0 ? hits[0].Tick : 0)}, ждали ~{tick + 35}");
        else Assert.Empty(hits);
    }

    /// <summary>Голова трупа 1 — на 4 см в земле после шага (Physics) тиков <paramref name="from"/> … <c>from + ticks − 1</c>.</summary>
    private sealed class SinkHead(ulong from, int ticks, IPhysicsWorld[] physics) : IContinuousSystem
    {
        private readonly GroundTouch[] _touches = new GroundTouch[IGround.MaxTouches];

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("test.sink-head")).After(GameLabels.CorpseRoot);

        public void Run(in SystemContext ctx)
        {
            ulong now = ctx.World.Resource<SimTime>().Tick;
            if (now < from || now >= from + (ulong)ticks) return;
            var p = physics[0];
            var head = p.Body(FighterBodies.Handle(ctx.World.Get<BodyRef>(FighterIn(ctx.World, 1))), BodySegs.Head);
            for (int k = 0; k < 50; k++)   // касание видно только в земле: голова над землёй — опускать по 1 см (может лежать на руке), в земле — до 4 см
            {
                var s = p.Get(head);
                float depth = MathF.Max(Depth(s.EndA, s.Radius), Depth(s.EndB, s.Radius));
                if (MathF.Abs(depth - 0.04f) < 1e-3f) return;
                p.Place(head, s.Position - new Vector3(0, depth > 0 ? 0.04f - depth : 0.01f, 0), s.Rotation);
            }
            throw new InvalidOperationException("голова трупа не легла на землю");
        }

        private float Depth(Vector3 end, float radius)
        {
            int n = Math.Min(ArenaGround.Instance.Touches(end, radius, _touches), _touches.Length);
            float d = 0;
            for (int i = 0; i < n; i++) d = MathF.Max(d, _touches[i].Depth);
            return d;
        }
    }

    /// <summary>
    /// Прогрев шага физики (<see cref="PhysicsWarmup"/>) на этом потоке — шаги без исключений (в игре он идёт в фоне, и ошибка только пишется
    /// в журнал). 0,1 с — тела трупов прогрева сложены плотно, в первые шаги почти все пары самоколлизии внахлёст.
    /// </summary>
    [Fact]
    public void PhysicsWarmup_Runs_WithoutErrors()
    {
        var result = PhysicsWarmup.Run(PhysicsSettings.Default with { PopSpeed = 1 }, 0.1);
        output.WriteLine(result.ToString());
        Assert.True(result.Steps > 30, result.ToString());
    }

    // ── хеш, память ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Хеш <c>physics</c> видит суставы, их привязку и включённость и отдачу: переключение сустава трупа меняет хеш участника; суставы
    /// трупа заведены (запястье выключено, колено — шарнир).
    /// </summary>
    [Fact]
    public void PhysicsPart_HashesJoints()
    {
        var (sim, physics) = Create("arena.enemies=1;combat.enemiesAttack=false");
        Step(sim, Place(1, 3, 3));
        Step(sim, Kill(1));
        var a = Body(sim, Fighter(sim, 1));
        ulong before = sim.HashState().Parts["physics"];
        physics.SetJointEnabled(a, 0, false);
        Assert.NotEqual(before, sim.HashState().Parts["physics"]);
        physics.SetJointEnabled(a, 0, true);
        Assert.Equal(before, sim.HashState().Parts["physics"]);
        Assert.Equal(CorpseJoints.Count, physics.JointCount(a));
        Assert.False(physics.IsJointEnabled(a, 10));                     // запястье — меч выпал
        Assert.Equal(JointKind.Hinge, physics.Joint(a, 7).Kind);         // колено левое
        Assert.NotEqual(Vector3.Zero, physics.Joint(a, 7).HingeAxis);
        Assert.True(float.IsFinite(physics.MeasureJoint(a, 0).PointError));
    }

    /// <summary>
    /// Толпа 32 трупов, убитых около игрока: не разлетается (центры масс — в 8 м от игрока), через 5 с — покой; 0 Б на тик после прогрева.
    /// </summary>
    [Fact]
    public void Crowd32Corpses_NoScatter_Settle_ZeroBytesPerTick()
    {
        var (sim, physics) = Create("arena.enemies=32;combat.enemiesAttack=false");
        for (int t = 0; t < 600; t++) sim.Step();
        for (int s = 1; s <= 32; s++) sim.Enqueue(Kill(s));
        sim.Step();
        var enemies = Enumerable.Range(1, 32).Select(s => Fighter(sim, s)).ToArray();
        for (int t = 0; t < 600; t++) sim.Step();
        var center = sim.World.Get<Transform>(Fighter(sim, 0)).Position;
        float far = enemies.Max(e => Vector2.Distance(new Vector2(Totals(sim, e).Com.X, Totals(sim, e).Com.Z), new Vector2(center.X, center.Z)));
        float ke = enemies.Max(e => Totals(sim, e).KineticEnergy);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 240; t++)
            sim.Step();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / 240;
        foreach (var i in sim.Invariants.List().Where(i => i.Violations > 0)) output.WriteLine($"  нарушено {i.Id}: {i.Violations}");
        foreach (var v in sim.Invariants.Recent().Take(3)) output.WriteLine($"  {v}");
        output.WriteLine(FormattableString.Invariant($"толпа 32 трупов: ЦМ до {far:F2} м от игрока, кинетическая ≤ {ke:F3} Дж через 5 с; тик {ms:F2} мс (Debug, с инвариантами после барьера); {bytes} Б за 240 тиков"));
        Assert.True(far <= 8, $"разлетелись: {far} м");
        // 2,5 Дж — с самоколлизией трупы кучи лежат друг на друге, а не сложившись в себя: оседание дольше
        Assert.True(ke <= 2.5f, $"не успокоились: {ke} Дж");
        Assert.True(bytes == 0, $"240 тиков толпы трупов выделили {bytes} Б");
        Clean(sim);
    }
}

/// <summary>
/// На паке игры (категория Content): оси шарниров Manny найдены по клипам (−Y кости).
/// </summary>
[Trait("Category", "Content")]
public sealed class RagdollCorpseContentTests
{
    private static CharacterAssets Pak => TestContent.Reference.Value.Assets;

    [Fact]
    public void Hinges_FoundFromClips_OnManny()
    {
        var anatomy = Pak.Hinges;
        foreach (int seg in new[] { BodySegs.FArmL, BodySegs.FArmR, BodySegs.ShinL, BodySegs.ShinR })
        {
            Assert.True(anatomy.Found(seg), BodySegs.Name(seg));
            Assert.True(Vector3.Dot(anatomy.Local(seg), -Vector3.UnitY) > 0.99f, $"{BodySegs.Name(seg)}: {anatomy.Local(seg)}");
        }
    }
}
