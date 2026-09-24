using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;

namespace Sword.Game.Tests;

/// <summary>
/// Аниматор бойца и модуль Locomotion на тестовом контенте (<see cref="TestAssets"/>): выбор клипа по движению, поза —
/// итог клипа, модуль выключен, позы возвращаются в пул, инструменты агента. На настоящем контенте — сценарии манифеста
/// <c>animation</c> и эталон SWORD2 <c>animation.swreplay</c> (сравнение со SWORD снято на 7а).
/// </summary>
public sealed class AnimationTests
{
    private static string ClipOf(Simulation sim, Entity e) =>
        TestAssets.Assets.Clips.Clips[sim.World.Get<LegacyLocomotionState>(e).Clip].ShortName;

    private static void Run(Simulation sim, int ticks, Vector2 move, bool walk = false, Vector3? gaze = null)
    {
        for (int i = 0; i < ticks; i++)
        {
            sim.Enqueue(new PlayerCommand { Move = move, Walk = walk, HasGaze = gaze is not null, Gaze = gaze ?? default });
            sim.Step();
        }
    }

    [Fact]
    public void Clip_FollowsMovement()
    {
        var sim = TestAssets.Simulation();
        var player = GameCore.Player(sim.World);
        Run(sim, 30, Vector2.Zero);
        Assert.Equal("1HM_Idle_Standing", ClipOf(sim, player));

        // бег вперёд, лицом вперёд: сначала шаг (скорость растёт), потом бег
        Run(sim, 240, new Vector2(0, 1), gaze: new Vector3(0, 0, 100));
        Assert.Equal("1HM_RunForward", ClipOf(sim, player));

        // бег по +X лицом по +Z: +X мира — левая сторона бойца (+X персонажа — влево)
        Run(sim, 240, new Vector2(1, 0), gaze: new Vector3(0, 0, 100));
        Assert.Equal("1HM_RunLeft", ClipOf(sim, player));

        // стоп — стойка; шаг назад с места: 1,6 м/с ниже порога бега 1,9 — ходьба
        Run(sim, 240, Vector2.Zero);
        Assert.Equal("1HM_Idle_Standing", ClipOf(sim, player));
        Run(sim, 240, new Vector2(0, -1), walk: true, gaze: new Vector3(0, 0, 100));
        Assert.Equal("1HM_WalkBack", ClipOf(sim, player));
    }

    [Fact]
    public void Pose_IsSampledClip_WhenFadeIsOver()
    {
        var sim = TestAssets.Simulation();
        var player = GameCore.Player(sim.World);
        Run(sim, 200, new Vector2(0, 1), gaze: new Vector3(0, 0, 100));
        var state = sim.World.Get<LegacyLocomotionState>(player);
        Assert.Equal(1f, state.Fade);

        var clips = TestAssets.Assets.Clips;
        int jc = clips.JointCount;
        var rot = new Quaternion[jc];
        var pos = new Vector3[jc];
        var model = new Matrix4x4[jc];
        clips.Sample(state.Clip, state.Time, rot, pos);
        clips.Skeleton.ToModel(rot, pos, model);
        var view = sim.World.View<IAnimationView>();
        Assert.Equal(model, view.Model(sim.World.Get<PoseRef>(player)).ToArray());
    }

    [Fact]
    public void WithoutLocomotion_PoseIsBindPose_RootStillMoves()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("modules.locomotion.enabled=false"));
        var player = GameCore.Player(sim.World);
        Assert.False(sim.World.Knows<LegacyLocomotionState>());
        Run(sim, 120, new Vector2(0, 1));
        Assert.True(sim.World.Get<Transform>(player).Position.Z > 3);   // корень идёт и без ходьбы
        var view = sim.World.View<IAnimationView>();
        var pose = view.Model(sim.World.Get<PoseRef>(player));
        // поза привязки меша тестов: суставы — сдвиги Offsets по иерархии
        Assert.Equal(new Vector3(0, 0.95f, 0), pose[view.FindJoint("pelvis")].Translation);
        Assert.True(Vector3.Distance(new Vector3(0.1f, 0, 0.12f), pose[view.FindJoint("ball_l")].Translation) < 1e-5f);
        Assert.Equal(1, view.PoseCount);
    }

    /// <summary>
    /// Ассеты анимации общие для всех симуляций (<see cref="Animation.CharacterAssets"/>): у их типов нет открытых путей
    /// записи — ни изменяемых массивов наружу, ни открытых полей, ни сеттеров.
    /// </summary>
    [Theory]
    [InlineData(typeof(Skeleton))]
    [InlineData(typeof(ClipLibrary))]
    [InlineData(typeof(Data.ActionTable))]
    [InlineData(typeof(Data.ActionSlots))]
    [InlineData(typeof(Animation.CharacterAssets))]
    public void SharedAnimationAssets_ExposeNoWritableData(Type type)
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        var problems = new List<string>();
        foreach (var f in type.GetFields(all))
            if (!f.IsLiteral && (!f.IsInitOnly || f.FieldType.IsArray)) problems.Add($"поле {f.Name}");
        foreach (var p in type.GetProperties(all))
        {
            if (p.SetMethod is { IsPublic: true }) problems.Add($"сеттер {p.Name}");
            if (p.PropertyType.IsArray || p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Span<>))
                problems.Add($"свойство {p.Name}: {p.PropertyType.Name}");
        }
        foreach (var m in type.GetMethods(all).Where(m => !m.IsSpecialName))
            if (m.ReturnType.IsArray || m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Span<>))
                problems.Add($"метод {m.Name}: {m.ReturnType.Name}");
        Assert.True(problems.Count == 0, $"{type.Name}: " + string.Join(", ", problems));
    }

    [Fact]
    public void DestroyedFighter_ReturnsPosesToPool()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Destroyer(10)]);
        var view = sim.World.View<IAnimationView>();
        for (int i = 0; i < 5; i++) sim.Step();
        Assert.Equal(3, view.PoseCount);   // поза бойца, слой локомоции и снимок кроссфейда
        for (int i = 0; i < 10; i++) sim.Step();
        Assert.Equal(0, view.PoseCount);
        Assert.Equal(0, view.LastTickStale);
    }

    [Fact]
    public void FighterRemovedOnSpawnBarrier_LeaksNoPoses()
    {
        // Боец снят на том же барьере, где модуль добавляет ему состояние локомоции: состояние в мир не попадает,
        // Removed не придёт — позы слоя и снимка, взятые заранее, вернуть было бы некому.
        var sim = Simulation.Build(ConfigSet.Empty(), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new SpawnBarrierDestroyer()]);
        var view = sim.World.View<IAnimationView>();
        for (int i = 0; i < 5; i++) sim.Step();
        Assert.Equal(0, view.PoseCount);
        Assert.Equal(0, view.LastTickStale);
    }

    /// <summary>Удаляет игрока в Gameplay первого тика — сразу после <c>locomotion.setup</c>, на том же барьере.</summary>
    private sealed class SpawnBarrierDestroyer : IModule
    {
        public string Id => "spawn_destroyer";

        public void Configure(SimulationBuilder b) => b.System(GameStages.Gameplay, new System());

        private sealed class System : IContinuousSystem
        {
            private bool _done;

            public void Setup(SystemSetup s) => s.Label(new SystemLabel("spawn-barrier-destroyer")).After(new SystemLabel("locomotion.setup")).Destroys();

            public void Run(in SystemContext ctx)
            {
                if (_done) return;
                _done = true;
                ctx.Commands.Destroy(GameCore.Player(ctx.World.Reader));
            }
        }
    }

    /// <summary>Удаляет игрока на тике <paramref name="tick"/> (Intent).</summary>
    private sealed class Destroyer(ulong tick) : IModule
    {
        public string Id => "destroyer";

        public void Configure(SimulationBuilder b) => b.System(GameStages.Intent, new System(tick));

        private sealed class System(ulong tick) : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("destroyer")).Destroys();

            public void Run(in SystemContext ctx)
            {
                if (ctx.Tick == tick) ctx.Commands.Destroy(GameCore.Player(ctx.World.Reader));
            }
        }
    }

    [Fact]
    public async Task AgentTools_ShowPoseAndClips()
    {
        var sim = TestAssets.Simulation();
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true, sim.Tick),
            Service = new ServiceQueue(), Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"),
            Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        SwordAgent.Register(host);
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<JsonObject> Call(string tool, JsonObject args)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
                };
                var r = JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
                Assert.False(r["isError"]?.GetValue<bool>() ?? false, r.ToJsonString());
                return r["structuredContent"]!.AsObject();
            }

            await Call("time.step", new JsonObject { ["ticks"] = 3 });
            var pose = await Call("anim.pose", new JsonObject { ["joints"] = new JsonArray("pelvis", "foot_l") });
            var pelvis = pose["joints"]![0]!;
            Assert.Equal("pelvis", pelvis["joint"]!.GetValue<string>());
            Assert.Equal(0.95, pelvis["model"]![1]!.GetValue<double>(), 3);
            Assert.Equal(3, pose["pool"]!["poses"]!.GetValue<int>());

            var clips = await Call("anim.clips", new JsonObject { ["filter"] = "runforward" });
            var run = Assert.Single(clips["clips"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "sword_1h/1HM_RunForward")!;
            Assert.Equal(TestAssets.RunSpeed, run["rootSpeed"]!.GetValue<double>(), 3);
            Assert.True(run["loop"]!.GetValue<bool>());
            Assert.Equal(34, clips["total"]!.GetValue<int>());

            // Страницы: 34 клипа (локомоция, поворот на месте; с 6в — удары, уколы, уклонения; с 6г — реакции) по 10 — продолжение у всех, кроме последней.
            var first = await Call("anim.clips", new JsonObject { ["take"] = 10 });
            Assert.Equal(10, first["clips"]!.AsArray().Count);
            Assert.True(first["receipt"]!["truncated"]!.GetValue<bool>());
            Assert.Equal("skip=10", first["receipt"]!["continuationToken"]!.GetValue<string>());
            var second = await Call("anim.clips", new JsonObject { ["skip"] = 10, ["take"] = 10 });
            Assert.Equal(10, second["clips"]!.AsArray().Count);
            Assert.Equal(10, second["clips"]![0]!["index"]!.GetValue<int>());
            Assert.Equal("skip=20", second["receipt"]!["continuationToken"]!.GetValue<string>());
            var last = await Call("anim.clips", new JsonObject { ["skip"] = 30, ["take"] = 10 });
            Assert.Equal(4, last["clips"]!.AsArray().Count);
            Assert.False(last["receipt"]!["truncated"]!.GetValue<bool>());
            Assert.Null(last["receipt"]!["continuationToken"]);
        }
        finally
        {
            stop.Cancel();
            loop.Join();
        }
    }
}
