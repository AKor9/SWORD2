using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;

namespace Sword.Game.Tests;

/// <summary>
/// Аниматор бойца и модуль Locomotion на тестовом контенте: выбор клипа по движению, поза — итог клипа, позы возвращаются в
/// пул. На настоящем контенте — эталон <c>animation.swreplay</c>.
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

    /// <summary><see cref="Bootstrap.SwordSimulation.ModuleIds"/> знает каждый модуль сборки: иначе сборка без модуля упала бы на общем <c>sword.json</c>.</summary>
    [Fact]
    public void ModuleIds_ListEveryModuleOfTheBuild()
    {
        foreach (var module in Bootstrap.SwordSimulation.Modules()) Assert.Contains(module.Id, Bootstrap.SwordSimulation.ModuleIds);
        Assert.Equal(Bootstrap.SwordSimulation.ModuleIds.Count, Bootstrap.SwordSimulation.ModuleIds.Distinct().Count());
    }

    /// <summary>Общие ассеты анимации (<see cref="Animation.CharacterAssets"/>) не дают путей записи: ни массивов наружу, ни полей, ни сеттеров.</summary>
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

}
