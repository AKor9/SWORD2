using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Тела бойцов, точка 5а: сочленение из 13 кинематических капсул за <see cref="BodyRef"/>, время жизни — хуками ECS. Каждый
/// боец получает тело на барьере своего появления; команды арены (PageUp, PageDown, число, сброс R) не оставляют ни лишних,
/// ни чужих тел; сброс строит бойца заново с тем же телом; боец, созданный и снятый на одном барьере, тела не оставляет;
/// участник хеша <c>physics</c> и его описание.
/// </summary>
public sealed class BodiesTests
{
    /// <summary>
    /// Подсистема физики симуляции снаружи не видна (мир владельцу — только для чтения, вид тел — 5в): тест берёт её модулем,
    /// как система взяла бы ресурс, — после сборки мира.
    /// </summary>
    private sealed class Probe(Action<IPhysicsWorld> take, ISystem? system = null) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b)
        {
            b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
            if (system is not null) b.System(GameStages.Gameplay, system);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, IPhysicsWorld> Worlds = [];

    private static Simulation Create(string sets, ISystem? system = null)
    {
        IPhysicsWorld? physics = null;
        var sim = Simulation.Build(ConfigSet.Empty(sets), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe(p => physics = p, system)]);
        Worlds.Add(sim, physics!);
        return sim;
    }

    private static void Step(Simulation sim, params ArenaCommand[] commands)
    {
        foreach (var c in commands) sim.Enqueue(c);
        sim.Export.BeginFrame();
        sim.Step();
    }

    private static ArenaCommand Change(int n) => new() { Action = ArenaAction.ChangeEnemies, Enemies = n };
    private static ArenaCommand Set(int n) => new() { Action = ArenaAction.SetEnemies, Enemies = n };
    private static readonly ArenaCommand Reset = new() { Action = ArenaAction.Reset };

    private static IPhysicsWorld Physics(Simulation sim) => Worlds.TryGetValue(sim, out var p) ? p : throw new InvalidOperationException("симуляция не из Create");

    /// <summary>Бойцы по местам: сущность и её тело.</summary>
    private static SortedDictionary<int, (Entity Entity, ArticulationHandle Body)> Bodies(Simulation sim)
    {
        var w = sim.World;
        var rows = new SortedDictionary<int, (Entity, ArticulationHandle)>();
        foreach (var chunk in w.Iterate(w.Query().With<Fighter>().Read<FighterSlot>().Read<BodyRef>()))
        {
            var slots = chunk.Read<FighterSlot>();
            var refs = chunk.Read<BodyRef>();
            for (int i = 0; i < chunk.Count; i++) rows.Add(slots[i].Index, (chunk.Entities[i], FighterBodies.Handle(refs[i])));
        }
        return rows;
    }

    /// <summary>У каждого бойца живое сочленение из 13 тел, у каждого сочленения — ровно один боец, других тел нет.</summary>
    private static void AssertConsistent(Simulation sim)
    {
        var physics = Physics(sim);
        var bodies = Bodies(sim);
        Assert.Equal(sim.World.Resource<ArenaState>().Enemies + 1, bodies.Count);
        Assert.All(bodies.Values, b => Assert.True(physics.IsAlive(b.Body) && physics.BodyCount(b.Body) == BodySegs.Bodies, $"{b.Entity}: {b.Body}"));
        Assert.Equal(bodies.Count, bodies.Values.Select(b => b.Body).Distinct().Count());
        Assert.Equal((bodies.Count, bodies.Count * BodySegs.Bodies), (physics.ArticulationCount, physics.LiveBodyCount));
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent()));
    }

    [Fact]
    public void EveryFighter_GetsThirteenKinematicCapsules_ShapeComesWithPlacement5b()
    {
        var sim = Create("arena.enemies=3");
        AssertConsistent(sim);   // сборка мира: немедленные операции — тоже барьеры, хуки сработали
        var physics = Physics(sim);
        var (_, player) = Bodies(sim)[0];
        Assert.Equal(new ArticulationHandle(0, 1), player);   // игрок строится первым
        for (int i = 0; i < BodySegs.Bodies; i++)
        {
            var b = physics.Body(player, i);
            Assert.Equal(player, physics.ArticulationOf(b));
            var s = physics.Get(b);
            // До 5б — тело SWORD между конструктором бойца и Build: без формы, в начале координат, кинематическое.
            Assert.Equal(BodyState.New(BodyMode.Kinematic), s);
        }
        for (int t = 0; t < 60; t++) Step(sim);
        AssertConsistent(sim);
    }

    [Fact]
    public void ArenaCommands_LeaveNoExtraOrForeignBodies()
    {
        var sim = Create("arena.enemies=5");
        ArenaCommand[][] script =
        [
            [Change(1)], [Change(1)], [Change(-1)], [Change(-3)], [Set(32)], [Set(0)], [Change(4)], [Reset],
            [Change(2), Change(-2)],          // прибавили и убавили в одном тике — места не строятся
            [Change(-2), Change(2)],          // убавили и прибавили — места строятся заново на новых сущностях
            [Change(3), Reset, Change(-1)],
            [Set(32), Reset], [Set(0), Change(1), Reset],
        ];
        foreach (var commands in script)
        {
            Step(sim, commands);
            AssertConsistent(sim);
            Step(sim);
            AssertConsistent(sim);
        }
    }

    [Fact]
    public void Reset_RebuildsOnTheSameEntity_WithTheSameBody_PageDownUp_NewEntityNewBody()
    {
        var sim = Create("arena.enemies=4");
        for (int t = 0; t < 30; t++) Step(sim);
        var before = Bodies(sim);
        Step(sim, Reset);
        var after = Bodies(sim);
        // Как Fighter.Build SWORD над Fighter.Bodies: тот же боец места, те же тела (постановку при постройке даёт 5б).
        Assert.Equal(before, after);

        Step(sim, Change(-1));
        Assert.False(Physics(sim).IsAlive(before[4].Body));   // ушедшее место — тело освобождено хуком снятия
        Step(sim, Change(1));
        var back = Bodies(sim)[4];
        Assert.NotEqual(before[4].Entity, back.Entity);
        Assert.Equal((before[4].Body.Index, before[4].Body.Generation + 1), (back.Body.Index, back.Body.Generation));   // тот же слот, новое поколение
        AssertConsistent(sim);
    }

    /// <summary>На тике 5 создаёт сущность с <see cref="BodyRef"/> и удаляет её командами того же барьера.</summary>
    private sealed class Blink : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("blink")).Creates().Destroys().Adds<BodyRef>();

        public void Run(in SystemContext ctx)
        {
            if (ctx.World.Resource<SimTime>().Tick != 5) return;
            var d = ctx.Commands.Create();
            ctx.Commands.Add(d, new BodyRef());
            ctx.Commands.Destroy(d.Ref);
        }
    }

    [Fact]
    public void CreatedAndDestroyedOnOneBarrier_BothHooksRan_NoBodyLeft()
    {
        var sim = Create("arena.enemies=2", new Blink());
        var physics = Physics(sim);
        int created = 0;
        for (int t = 0; t < 10; t++)
        {
            int generationBefore = physics.ArticulationGenerationAt(3);
            Step(sim);
            if (physics.ArticulationGenerationAt(3) != generationBefore) created++;
            AssertConsistent(sim);
        }
        // Слот 3 (после игрока и двух врагов) выдан хуком добавления и возвращён хуком снятия на барьере Gameplay тика 5.
        Assert.Equal(1, created);
        Assert.Equal(-1, physics.ArticulationGenerationAt(3));
    }

    [Fact]
    public void PhysicsPart_HashesBodies_DescribeShowsWhatIsHashed()
    {
        var a = Create("arena.enemies=1");
        var b = Create("arena.enemies=1");
        Assert.Equal(a.HashState().Parts["physics"], b.HashState().Parts["physics"]);
        Step(a, Change(1));
        Step(b);
        Assert.NotEqual(a.HashState().Parts["physics"], b.HashState().Parts["physics"]);   // новое тело — другой хеш

        var d = JsonNode.Parse(a.DescribeState("physics", new DescribeQuery("0v1")))!;
        Assert.Equal(3, d["articulations"]!.GetValue<int>());
        Assert.Equal(39, d["bodies"]!.GetValue<int>());
        var player = d["items"]![0]!;
        Assert.Equal("0v1", player["id"]!.GetValue<string>());
        Assert.Equal(13, player["bodyCount"]!.GetValue<int>());
        Assert.Equal("Kinematic", player["0"]!["mode"]!.GetValue<string>());
        Assert.Equal("0v1", player["0"]!["body"]!.GetValue<string>());
        Assert.Equal(1, player["12"]!["rotation"]!["w"]!.GetValue<float>());
        var slots = JsonNode.Parse(a.DescribeState("physics", new DescribeQuery("slots")))!["items"]![0]!;
        Assert.Equal(ArenaRules.MaxFighters - 3, slots["freeArticulations"]!.AsArray().Count);

        // Карта архитектуры агента (ecs.componentUsage): кто заводит и освобождает тело за BodyRef — хуки ядра.
        var usage = ArchitectureMap.ComponentUsage(a.World.Systems, typeof(BodyRef).FullName!, a.World.Hooks);
        Assert.Equal(["OnAdd", "OnRemove"], usage["hooks"]!.AsArray().Select(h => h!["hook"]!.GetValue<string>()));
        Assert.All(usage["hooks"]!.AsArray(), h => Assert.Null(h!["module"]));
        Assert.Contains(usage["systems"]!.AsArray(), u => u!["system"]!.GetValue<string>() == "core.arena-command@Intent");
    }

    [Fact]
    public void SegmentNumbering_IsSwordWithBladeNumberReserved()
    {
        // Номера SWORD (BodySegs 2eab13b): 0…10, клинок 11 (этап 6), стопы 12 и 13; тело i сочленения бойца — по возрастанию.
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13], Enumerable.Range(0, BodySegs.Bodies).Select(BodySegs.Segment));
        Assert.All(Enumerable.Range(0, BodySegs.Bodies), i => Assert.Equal(i, BodySegs.Body(BodySegs.Segment(i))));
        Assert.Equal(-1, BodySegs.Body(11));
        Assert.Equal((12, 13, 14), (BodySegs.FootL, BodySegs.FootR, BodySegs.Count));
        Assert.Equal("forearm.l", BodySegs.Name(BodySegs.FArmL));
    }
}
