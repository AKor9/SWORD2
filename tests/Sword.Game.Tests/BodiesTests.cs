using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
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
/// участник хеша <c>physics</c> и его описание. Точка 5б: форма при постройке, постановка при постройке без скорости по позе
/// постройки, капсулы по позе тика с разностными скоростями, вид тел <see cref="IBodyView"/>.
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
        var sim = Simulation.Build(ConfigSet.Empty(sets.Split(';')), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
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
    public void EveryFighter_GetsThirteenKinematicCapsules_ShapeAndPlacementAtWorldBuild()
    {
        var sim = Create("arena.enemies=3");
        AssertConsistent(sim);   // сборка мира: немедленные операции — тоже барьеры, хуки сработали
        var physics = Physics(sim);
        var (_, player) = Bodies(sim)[0];
        Assert.Equal(new ArticulationHandle(0, 1), player);   // игрок строится первым
        for (int i = 0; i < BodySegs.Bodies; i++) Assert.Equal(player, physics.ArticulationOf(physics.Body(player, i)));

        // 5в: тела бойцов сборки мира стоят до первого тика (как PlaceBuilt SWORD после AttachRig): форма — радиусы
        // Fighter.Radii и полудлины процедурной позы постройки (в корне и курсом постройки), капсулы — по позе постройки без
        // скорости. Тик 1 ставит их при постройке ещё раз теми же значениями — состояние тел то же.
        var built = Snapshot(sim);
        Span<float> half = stackalloc float[BodySegs.Count];
        var model = StartModel(sim.World.Resource<StartPose>().Clip);
        var joints = new BodyJoints(TestAssets.Assets.Clips.Skeleton);
        var pos = new Vector3[BodySegs.Bodies];
        var rot = new Quaternion[BodySegs.Bodies];
        foreach (var (e, body) in Bodies(sim).Values)
        {
            var root = sim.World.Get<Transform>(e);   // корень постройки
            FighterShape.HalfLengths(root.Position, root.Facing, half);
            BodyPlacement.Capsules(model, joints, root.Position, root.Facing, pos, rot);
            for (int i = 0; i < BodySegs.Bodies; i++)
            {
                var s = physics.Get(physics.Body(body, i));
                int seg = BodySegs.Segment(i);
                Assert.Equal((FighterShape.Radii[seg], half[seg]), (s.Radius, s.HalfLength));
                Assert.True(s.HalfLength is > 0.02f and < 0.4f, $"{BodySegs.Name(seg)}: полудлина {s.HalfLength}");
                Assert.Equal((pos[i], rot[i], pos[i], rot[i]), (s.Position, s.Rotation, s.PrevPosition, s.PrevRotation));
                Assert.Equal((Vector3.Zero, Vector3.Zero), (s.Velocity, s.AngularVelocity));
            }
        }
        // Проверка «сейчас» до первого тика — капсулы сверяются с позой постройки, нарушений нет.
        Assert.All(sim.Invariants.Run(["physics.kinematicPose", "physics.finite"], 0, "—", sim.Dt), r => Assert.Equal(0, r.Violations));

        Step(sim);
        // Тик 1: постройка сборки мира поставлена ещё раз теми же значениями, затем постановка тика — прошлая постановка та же.
        foreach (var (e, body) in Bodies(sim).Values)
            for (int i = 0; i < BodySegs.Bodies; i++)
            {
                var s = physics.Get(physics.Body(body, i));
                var b = built[(e, i)];
                Assert.Equal((b.Position, b.Rotation, b.Radius, b.HalfLength), (s.PrevPosition, s.PrevRotation, s.Radius, s.HalfLength));
            }
        for (int t = 0; t < 60; t++) Step(sim);
        AssertConsistent(sim);   // и physics.kinematicPose, physics.kinematicSpeed — чисто
    }

    /// <summary>Состояние всех тел бойцов: (сущность, тело) → состояние.</summary>
    private static Dictionary<(Entity, int), BodyState> Snapshot(Simulation sim)
    {
        var physics = Physics(sim);
        var result = new Dictionary<(Entity, int), BodyState>();
        foreach (var (e, body) in Bodies(sim).Values)
            for (int i = 0; i < BodySegs.Bodies; i++) result[(e, i)] = physics.Get(physics.Body(body, i));
        return result;
    }

    /// <summary>Модельные матрицы позы постройки: клип <paramref name="clip"/> в момент 0 (−1 — поза привязки) — своим пулом.</summary>
    private static Matrix4x4[] StartModel(int clip)
    {
        var pool = new PosePool(TestAssets.Assets.Clips, 1);
        var h = pool.Alloc();
        if (clip >= 0) pool.Sample(h, clip, 0);
        pool.ToModel(h);
        pool.Tick();
        return pool.Model(h).ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_PlacesFromStartPose_WithoutVelocity_ThenTickVelocityIsTheDifference(bool locomotion)
    {
        // PlaceBuilt SWORD: при постройке капсулы стоят по позе FighterAnimator.Reset → Tick(0) (с модулем Locomotion — стойка с
        // нулевого момента, без него — поза привязки) без скорости; в тике — цель по позе тика, скорость — разность за тик.
        var sim = Create(locomotion ? "arena.enemies=1" : "arena.enemies=1;modules.locomotion.enabled=false");
        var start = sim.World.Resource<StartPose>();
        Assert.Equal(locomotion ? TestAssets.Assets.Actions.Idle : -1, start.Clip);
        Assert.Equal(0f, start.Time);
        var model = StartModel(start.Clip);
        var joints = new BodyJoints(TestAssets.Assets.Clips.Skeleton);
        var physics = Physics(sim);
        var pos = new Vector3[BodySegs.Bodies];
        var rot = new Quaternion[BodySegs.Bodies];

        void Check(string when)
        {
            foreach (var (e, body) in Bodies(sim).Values)
            {
                var built = sim.World.Get<PrevTransform>(e);   // корень постройки в тике постройки
                BodyPlacement.Capsules(model, joints, built.Position, built.Facing, pos, rot);
                for (int i = 0; i < BodySegs.Bodies; i++)
                {
                    var s = physics.Get(physics.Body(body, i));
                    Assert.True(s.PrevPosition == pos[i] && s.PrevRotation == rot[i], $"{when}, {e}, {BodySegs.Name(BodySegs.Segment(i))}: прошлая постановка — не постройка");
                    Assert.Equal((s.Position - s.PrevPosition) / sim.Dt, s.Velocity);
                    Assert.Equal(PhysicsMath.RotationVector(s.PrevRotation, s.Rotation) / sim.Dt, s.AngularVelocity);
                }
            }
        }

        Step(sim);
        Check("сборка мира, тик 1");
        for (int t = 0; t < 40; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0) });
            Step(sim);
        }
        sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0) });
        Step(sim, Reset);   // игрок убежал — сброс строит его в центре: без скачка скорости капсул
        Check("сброс R");
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Capsules_FollowThePose_AndTheTerrain()
    {
        // Игрок бежит на склон (0, 18): капсулы идут за корнем и поднимаются с рельефом; таз — над землёй под корнем.
        var sim = Create("arena.enemies=0");
        var view = sim.World.View<IBodyView>();
        var (player, body) = Bodies(sim)[0];
        var bodyRef = sim.World.Get<BodyRef>(player);
        for (int t = 0; t < 600; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
            Step(sim);
        }
        var root = sim.World.Get<Transform>(player).Position;
        float ground = Arena.ArenaTerrain.Height(root.X, root.Z);
        Assert.True(ground > 0.5f, $"игрок не на склоне: корень {root}, земля {ground}");
        var pelvis = view.Body(bodyRef, BodySegs.Body(BodySegs.Pelvis));
        Assert.InRange(pelvis.Position.Y - ground, 0.5f, 1.3f);
        Assert.InRange(new Vector2(pelvis.Position.X - root.X, pelvis.Position.Z - root.Z).Length(), 0, 0.3f);
        Assert.InRange(pelvis.Velocity.Z, 2, 6);   // бежит вперёд по +Z
        Assert.Equal(BodySegs.Bodies, view.BodyCount(bodyRef));
        Assert.Equal(0, view.BodyCount(default));
        Assert.Equal(0, sim.Invariants.Total);
        _ = body;
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
        // Как Fighter.Build SWORD над Fighter.Bodies: тот же боец места, те же тела (форма и постановка — заново, 5б).
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
        Assert.Equal(FighterShape.Radii[BodySegs.FootR], player["12"]!["radius"]!.GetValue<float>());   // 5б: форма и постановка с тика 1
        Assert.True(player["12"]!["halfLength"]!.GetValue<float>() > 0);
        var slots = JsonNode.Parse(a.DescribeState("physics", new DescribeQuery("slots")))!["items"]![0]!;
        Assert.Equal(ArenaRules.MaxFighters - 3, slots["freeArticulations"]!.AsArray().Count);

        // Карта архитектуры агента (ecs.componentUsage): кто заводит и освобождает тело за BodyRef — хуки ядра.
        var usage = ArchitectureMap.ComponentUsage(a.World.Systems, typeof(BodyRef).FullName!, a.World.Hooks);
        Assert.Equal(["OnAdd", "OnRemove"], usage["hooks"]!.AsArray().Select(h => h!["hook"]!.GetValue<string>()));
        Assert.All(usage["hooks"]!.AsArray(), h => Assert.Null(h!["module"]));
        Assert.Contains(usage["systems"]!.AsArray(), u => u!["system"]!.GetValue<string>() == "core.arena-command@Begin");   // 6б: команды арены — в Begin
    }

    [Fact]
    public void JointSegments_AsCharacterRig_ByNameThenParents()
    {
        // CharacterRig.JointSegment SWORD: ключевые кости, затем по имени кости UE Manny, затем вверх по родителям, иначе таз.
        // Имена — UE Manny (пальцы и ball_r в экспорте UE без родителя).
        string[] names = ["root", "pelvis", "spine_01", "spine_03", "clavicle_l", "neck_01", "head", "upperarm_l", "hand_r",
            "index_01_l", "thigh_r", "calf_l", "ball_r", "ik_foot_root"];
        int[] parents = [-1, 0, 1, 2, 3, 3, 5, 4, -1, -1, 1, 1, -1, 0];
        Assert.Equal([BodySegs.Pelvis, BodySegs.Pelvis, BodySegs.Torso, BodySegs.Torso, BodySegs.Torso, BodySegs.Head, BodySegs.Head,
            BodySegs.UArmL, BodySegs.FArmR, BodySegs.FArmL, BodySegs.ThighR, BodySegs.ShinL, BodySegs.FootR, BodySegs.Pelvis],
            JointSegments.Build(names, parents));
        var skeleton = TestAssets.Assets.Clips.Skeleton;
        var segments = JointSegments.Build(skeleton);
        Assert.All(segments, s => Assert.True(BodySegs.Body(s) >= 0, $"сегмент {s} без тела"));
        // Синтетический скелет: неизвестная кость — по родителю, без ключевого родителя — таз.
        Assert.Equal([BodySegs.Pelvis, BodySegs.ThighL, BodySegs.ThighL, BodySegs.Pelvis],
            JointSegments.Build(["pelvis", "thigh_l", "strap", "prop"], [-1, 0, 1, -1]));
        // Вид тел отдаёт ту же таблицу клиенту (ID-буфер снимка).
        var view = Create("arena.enemies=0").World.View<IBodyView>();
        Assert.Equal(segments.Length, view.JointCount);
        Assert.All(Enumerable.Range(0, segments.Length), j => Assert.Equal(segments[j], view.JointSegment(j)));
    }

    [Fact]
    public async Task AgentTools_BodiesBySegment_AndBodyFieldsForMetrics()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=3"));
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true, sim.Tick),
            Service = new ServiceQueue(), Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"),
            Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        Agent.SwordAgent.Register(host);
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<JsonNode> Call(string tool, JsonObject args)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
                };
                return JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
            }

            await Call("time.step", new JsonObject { ["ticks"] = 5 });
            var all = (await Call("physics.bodies", new JsonObject { ["entity"] = "enemy 3" }))["structuredContent"]!;
            Assert.Equal("enemy 3", all["name"]!.GetValue<string>());
            Assert.Equal(13, all["bodyCount"]!.GetValue<int>());
            var capsules = all["capsules"]!.AsArray();
            Assert.Equal(13, capsules.Count);
            var forearm = capsules[BodySegs.Body(BodySegs.FArmL)]!;
            Assert.Equal(("forearm.l", "левое предплечье", 4), (forearm["name"]!.GetValue<string>(), forearm["title"]!.GetValue<string>(), forearm["segment"]!.GetValue<int>()));
            Assert.Equal(FighterShape.Radii[BodySegs.FArmL], forearm["radius"]!.GetValue<double>(), 4);
            // Концы — центр ± ось · полудлина: половина расстояния между ними — полудлина.
            var a = forearm["endA"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            var b = forearm["endB"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            double half = Math.Sqrt(a.Zip(b, (p, q) => (p - q) * (p - q)).Sum()) / 2;
            Assert.Equal(forearm["halfLength"]!.GetValue<double>(), half, 3);

            var feet = (await Call("physics.bodies", new JsonObject { ["segments"] = new JsonArray("foot.l", "FOOT.R") }))["structuredContent"]!;
            Assert.Equal(["foot.l", "foot.r"], feet["capsules"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
            Assert.Equal("player", feet["name"]!.GetValue<string>());
            var bad = await Call("physics.bodies", new JsonObject { ["segments"] = new JsonArray("tail") });
            Assert.True(bad["isError"]!.GetValue<bool>());
            Assert.Contains("нет сегмента «tail»", bad.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            // 5г: история капсул по тикам — watch.add с вычисляемыми полями тела, entity.history; последняя строка — те же числа,
            // что physics.bodies на этом тике.
            var watch = await Call("watch.add", new JsonObject { ["entities"] = new JsonArray("player"), ["fields"] = new JsonArray("Body.MaxSpeed", "Body.pelvis.Position.Y", "Transform.Position.X") });
            Assert.False(watch["isError"]?.GetValue<bool>() ?? false, watch.ToJsonString());
            await Call("command.send", new JsonObject { ["command"] = "PlayerCommand", ["fields"] = new JsonObject { ["Move"] = new JsonObject { ["X"] = 1, ["Y"] = 0 } }, ["repeat"] = 3 });
            await Call("time.step", new JsonObject { ["ticks"] = 3 });
            var history = (await Call("entity.history", new JsonObject { ["entity"] = "player" }))["structuredContent"]!;
            var rows = history["rows"]!.AsArray();
            Assert.Equal(3, rows.Count);
            var last = rows[^1]!["values"]!;
            var now = (await Call("physics.bodies", new JsonObject { ["entity"] = "player", ["segments"] = new JsonArray("pelvis") }))["structuredContent"]!;
            Assert.Equal(now["capsules"]![0]!["position"]![1]!.GetValue<double>(), last["Body.pelvis.Position.Y"]!.GetValue<double>(), 4);
            Assert.Equal(now["fastest"]!["speed"]!.GetValue<double>(), last["Body.MaxSpeed"]!.GetValue<double>(), 4);
            Assert.True(last["Transform.Position.X"]!.GetValue<double>() > 0);
        }
        finally
        {
            stop.Cancel();
            loop.Join();
        }

        // Вычисляемые поля для метрик манифеста — те же числа, что вид тел.
        var ctx = new ServiceContext(sim);
        var names = new Agent.SwordAgent.ArenaNames();
        var catalog = new Catalog(sim.World, sim.CommandTypes);
        var fields = new Agent.BodyFields();
        var y = new FieldProbe(catalog, names, "player", "Body.pelvis.Position.Y", "тест", fields);
        var view = sim.World.View<IBodyView>();
        var player = sim.World.Get<BodyRef>(names.Resolve(ctx, "player")!.Value);
        Assert.Equal(view.Body(player, 0).Position.Y, (float)y.Read(ctx));
        var speed = new FieldProbe(catalog, names, "enemy 1", "Body.MaxSpeed", "тест", fields).Read(ctx);
        Assert.True(speed > 0 && double.IsFinite(speed));
        Assert.Equal(view.Body(player, BodySegs.Body(BodySegs.FootR)).HalfLength,
            (float)new FieldProbe(catalog, names, "player", "Body.foot.r.HalfLength", "тест", fields).Read(ctx));
        Assert.Throws<ArgumentException>(() => new FieldProbe(catalog, names, "player", "Body.tail.Position", "тест", fields));
        Assert.Throws<ArgumentException>(() => new FieldProbe(catalog, names, "player", "Body.pelvis.Position.W", "тест", fields));
        // Строгий разбор компоненты: только X, Y, Z, W — опечатка не читается молча как длина или как X.
        foreach (string bad in new[] { "Body.pelvis.Position.x", "Body.pelvis.Position.XY", "Body.pelvis.Position.", "Body.pelvis.Rotation.Q",
                     "Body.pelvis.Position.X.Y", "Body.pelvis.Radius.X", "Body.pelvis", "Body.MaxSpeed.X" })
            Assert.Throws<ArgumentException>(() => new FieldProbe(catalog, names, "player", bad, "тест", fields));
        Assert.Equal(view.Body(player, 0).Rotation.W, (float)new FieldProbe(catalog, names, "player", "Body.pelvis.Rotation.W", "тест", fields).Read(ctx));
        Assert.True(double.IsNaN(new FieldProbe(catalog, names, "enemy 9", "Body.MaxSpeed", "тест", fields).Read(ctx)));   // нет такого врага
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
        Assert.Equal(("левое предплечье", BodySegs.FArmL, -1), (BodySegs.Title(BodySegs.FArmL), BodySegs.Find("Forearm.L"), BodySegs.Find("blade")));
    }
}
