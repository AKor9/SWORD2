using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Клиент игры без окна и GPU (точка 1г): камера SWORD, команда игрока из ввода, мир представления с интерполяцией.
/// Ожидания — <c>RtsCamera</c>, <c>PlayerController</c>, <c>SkinCutPose.DrawnJoints</c> SWORD 2eab13b.
/// </summary>
public class CameraTests
{
    private const float Dt = 1f / 60;

    [Fact]
    public void Wheel_ZoomsBy088PerNotch_Smoothly_WithinLimits()
    {
        var cam = new RtsCamera();
        cam.Update(new CameraControls { Wheel = 1 }, Dt, Vector3.Zero, 1600, 900);
        Assert.Equal(24f * 0.88f, cam.TargetDistance, 4);
        Assert.True(cam.Distance < 24f && cam.Distance > cam.TargetDistance, "дистанция идёт к цели сглаженно");
        for (int i = 0; i < 120; i++) cam.Update(default, Dt, Vector3.Zero, 1600, 900);
        Assert.Equal(cam.TargetDistance, cam.Distance, 3);

        cam.Update(new CameraControls { Wheel = -100 }, Dt, Vector3.Zero, 1600, 900);
        Assert.Equal(RtsCamera.MaxDistance, cam.TargetDistance);
        cam.Update(new CameraControls { Wheel = 100 }, Dt, Vector3.Zero, 1600, 900);
        Assert.Equal(RtsCamera.MinDistance, cam.TargetDistance);
    }

    [Fact]
    public void Rotation_KeysAndOrbit()
    {
        var cam = new RtsCamera();
        cam.Update(new CameraControls { RotateRight = true }, 0.5f, Vector3.Zero, 1600, 900);
        Assert.Equal(0.6f + 1.8f * 0.5f, cam.Yaw, 5);
        cam.Update(new CameraControls { Orbit = true, MouseDeltaX = 100, MouseDeltaY = 1000 }, Dt, Vector3.Zero, 1600, 900);
        Assert.Equal(0.6f + 0.9f - 0.6f, cam.Yaw, 4);
        Assert.Equal(RtsCamera.MaxPitch, cam.Pitch);   // наклон в пределах SWORD
    }

    [Fact]
    public void Follow_ApproachesPlayer_FreeModePansAndStaysInArena()
    {
        var cam = new RtsCamera();
        var player = new Vector3(10, 0, 0);
        cam.Update(default, 0.1f, player, 1600, 900);
        Assert.Equal(10 * (1 - MathF.Exp(-1)), cam.Target.X, 4);

        cam.Update(new CameraControls { ToggleFollow = true }, Dt, player, 1600, 900);
        Assert.False(cam.Follow);
        var before = cam.Target;
        cam.Update(new CameraControls { PanUp = true }, 0.5f, player, 1600, 900);
        var moved = cam.Target - before;
        Assert.True(Vector3.Dot(Vector3.Normalize(moved), cam.GroundForward) > 0.999f, "стрелка вверх — вперёд по камере");
        for (int i = 0; i < 200; i++) cam.Update(new CameraControls { PanUp = true }, 0.5f, player, 1600, 900);
        Assert.True(new Vector2(cam.Target.X, cam.Target.Z).Length() <= 80.001f, "свободная камера не уходит дальше 80 м");
    }

    [Fact]
    public void ScreenCentre_HitsGroundAhead_AlongGroundForward()
    {
        var cam = new RtsCamera { Target = new Vector3(5, 0, 5) };
        cam.Rebuild(1600, 900);
        Assert.True(cam.ScreenToGround(800, 450, 1600, 900, out var hit));
        Assert.Equal(0f, hit.Y, 4);
        var ahead = hit - cam.Target;
        Assert.True(ahead.Length() < 3, $"центр экрана — у точки взгляда: {ahead}");
        Assert.True(Vector3.Dot(ahead, cam.GroundForward) >= 0, "точка — впереди по камере (взгляд на 0,8 м над землёй)");
    }
}

public class PlayerIntentTests
{
    private static FrameInput Held(bool first, params InputAction[] actions) =>
        new(new Sword.Engine.Platform.InputState(), InputBindings.Resolve(ArenaInput.All, null),
            actions.Select(a => a.Name).ToHashSet(), first, true, 1600, 900, 1f / 60);

    [Fact]
    public void Actions_ReachPlayerKeysAndCamera()
    {
        // Путь действий (привязки → FrameInput → ArenaInput): то, что делают клавиши в игре.
        var keys = ArenaInput.Player(Held(false, ArenaInput.MoveForward, ArenaInput.Walk));
        Assert.Equal(new PlayerKeys(true, false, false, false, true), keys);
        var cam = ArenaInput.Camera(Held(true, ArenaInput.CameraRotateRight, ArenaInput.CameraFollow, ArenaInput.CameraZoomIn));
        Assert.True(cam.RotateRight && cam.ZoomIn && cam.ToggleFollow);   // F — нажатие: в первом кадре удержания
        Assert.False(ArenaInput.Camera(Held(false, ArenaInput.CameraFollow)).ToggleFollow);
    }

    [Fact]
    public void Wasd_IsRelativeToCamera()
    {
        // PlayerController.Think SWORD: W — GroundForward, D — GroundRight; симуляция нормирует направление.
        var cam = new RtsCamera { Yaw = 0.6f };
        var w = PlayerIntent.Command(new PlayerKeys(true, false, false, false, false), cam, false, default);
        Assert.Equal(new Vector2(cam.GroundForward.X, cam.GroundForward.Z), w.Move);
        var d = PlayerIntent.Command(new PlayerKeys(false, false, false, true, false), cam, false, default);
        Assert.Equal(new Vector2(cam.GroundRight.X, cam.GroundRight.Z), d.Move);
        var still = PlayerIntent.Command(new PlayerKeys(true, true, true, true, false), cam, false, default);
        Assert.Equal(0f, still.Move.Length(), 5);   // противоположные гасят друг друга
    }

    [Fact]
    public void Shift_Walks_CursorGivesGaze()
    {
        var cam = new RtsCamera();
        var c = PlayerIntent.Command(new PlayerKeys(false, false, false, false, true), cam, true, new Vector3(3, 0.5f, 4));
        Assert.True(c.Walk);
        Assert.True(c.HasGaze);
        Assert.Equal(new Vector3(3, 0, 4), c.Gaze);
        Assert.False(PlayerIntent.Command(default, cam, false, new Vector3(3, 0, 4)).HasGaze);   // курсора на земле нет
    }

    [Fact]
    public void Run_DirectionOnScreen_MatchesSimulation()
    {
        // W при повёрнутой камере: игрок бежит «от камеры» — по GroundForward этой камеры.
        var cam = new RtsCamera { Yaw = 2.1f };
        var sim = SwordSimulation.Create(ConfigSet.Empty());
        var p = GameCore.Player(sim.World);
        var cmd = PlayerIntent.Command(new PlayerKeys(true, false, false, false, false), cam, false, default);
        for (int i = 0; i < 240; i++)
        {
            sim.Enqueue(cmd);
            sim.Step();
        }
        var v = sim.World.Get<RootVelocity>(p).Value;
        Assert.True(Vector3.Dot(Vector3.Normalize(v), cam.GroundForward) > 0.9999f);
        Assert.Equal(4.2f, v.Length(), 3);
    }
}

public class ArenaViewTests
{
    // Функция рельефа, как Terrain.Height в KinWorld SWORD (не сетка рисования).
    private static readonly Func<float, float, float> Field = ArenaTerrain.Height;

    [Fact]
    public void Interpolation_RootLinear_FacingShortArc_HeightUnderRoot()
    {
        var prev = new PrevTransform { Position = new Vector3(0, 0, 0), Facing = MathF.PI - 0.1f };
        var cur = new Transform { Position = new Vector3(2, 0, 4), Facing = -MathF.PI + 0.1f };
        var half = ArenaView.Interpolate(default, true, prev, cur, 0.5f, Field);
        Assert.Equal(new Vector3(1, 0, 2), half.Root);
        // Курс идёт через ±π коротким путём (0,2 рад), а не через ноль (6,08 рад).
        Assert.Equal(MathF.PI, half.Facing, 4);
        Assert.Equal(ArenaTerrain.Height(1, 2), half.Height);
        Assert.Equal(prev.Position, ArenaView.Interpolate(default, true, prev, cur, 0, Field).Root);
        Assert.Equal(cur.Position, ArenaView.Interpolate(default, true, prev, cur, 1, Field).Root);
    }

    /// <summary>Прогон «клиент при заданной частоте кадров»: скорость показанного игрока по кадрам.</summary>
    private static List<float> ShownSpeeds(double fps, bool interpolate)
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty());
        var run = new PlayerCommand { Move = new Vector2(1, 0) };
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(run));
        var view = new ArenaView(Field);
        var speeds = new List<float>();
        float? last = null;
        double frame = 1 / fps;
        for (int i = 0; i < (int)(fps * 3); i++)
        {
            clock.RunFrame(frame);
            view.Extract(clock.Link, interpolate ? clock.Alpha : 1);
            if (view.Player is not { } p) continue;
            // Тень и кольцо — там же, где показан Manny: иначе они дрожат относительно плавно идущего бойца.
            Assert.Equal(p.Root with { Y = 0 }, view.PlayerMarker);
            if (last is { } x && i > fps * 1.5) speeds.Add((float)((p.Root.X - x) / frame));   // после разгона
            last = p.Root.X;
        }
        return speeds;
    }

    [Theory]
    [InlineData(45)]
    [InlineData(144)]
    [InlineData(37.3)]
    public void Shown_Movement_IsSmooth_AtAnyFrameRate(double fps)
    {
        // Точка 1г: «плавное движение при любой частоте кадров» — показанный игрок проходит за кадр ровно
        // скорость × шаг кадра, хотя тиков в кадрах разное число (120 Гц не делится на частоту кадров).
        var speeds = ShownSpeeds(fps, interpolate: true);
        Assert.NotEmpty(speeds);
        Assert.All(speeds, s => Assert.Equal(4.2f, s, 1));
        Assert.True(speeds.Max() - speeds.Min() < 0.01f, $"разброс скорости {speeds.Min()}…{speeds.Max()}");

        // Контроль чувствительности: без интерполяции (показ последнего тика) скорость по кадрам скачет.
        var raw = ShownSpeeds(fps, interpolate: false);
        Assert.True(raw.Max() - raw.Min() > 0.5f, $"без интерполяции разброс {raw.Min()}…{raw.Max()} — тест ничего не проверяет");
    }

    /// <summary>Снимает и возвращает игроку <see cref="Fighter"/> по расписанию: (тик, стадия, снять ли).</summary>
    private sealed class FighterToggle((ulong Tick, string Stage, bool Remove)[] plan) : IModule
    {
        public string Id => "toggle";

        public void Configure(SimulationBuilder b)
        {
            foreach (var stage in plan.Select(p => p.Stage).Distinct())
                b.System(stage, new Toggler(stage, plan.Where(p => p.Stage == stage).ToArray()));
        }

        private sealed class Toggler(string stage, (ulong Tick, string Stage, bool Remove)[] steps) : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("toggle." + stage)).Adds<Fighter>().Removes<Fighter>();

            public void Run(in SystemContext ctx)
            {
                var player = GameCore.Player(ctx.World.Reader);
                foreach (var step in steps)
                    if (step.Tick == ctx.Tick)
                    {
                        if (step.Remove) ctx.Commands.Remove<Fighter>(player);
                        else ctx.Commands.Add(player, new Fighter());
                    }
            }
        }
    }

    /// <summary>Кадр из одного тика (появление игрока), затем кадр из двух тиков (1 и 2) по расписанию.</summary>
    private static (ArenaView View, Simulation Sim) TwoTickFrame(params (ulong Tick, string Stage, bool Remove)[] plan)
    {
        var sim = Simulation.Build(ConfigSet.Empty(), GameStages.All, GameCore.Configure, [new FighterToggle(plan)]);
        var view = new ArenaView(Field);
        var first = new SimulationClock(sim, (_, _) => { }, ticksPerFrame: 1);
        first.RunFrame(0);
        view.Extract(first.Link, first.Alpha);
        Assert.NotNull(view.Player);
        var second = new SimulationClock(sim, (_, _) => { }, ticksPerFrame: 2);
        second.RunFrame(0);
        view.Extract(second.Link, second.Alpha);
        return (view, sim);
    }

    [Fact]
    public void Fighters_RemovedThenAddedInLaterTickOfFrame_StayShown()
    {
        // Тик 1 — снят, тик 2 — добавлен: в мире боец есть, в кадре — тоже (записи по тикам, не «все Added, потом Removed»).
        var (view, sim) = TwoTickFrame((1, GameStages.Gameplay, true), (2, GameStages.Gameplay, false));
        Assert.True(sim.World.Has<Fighter>(GameCore.Player(sim.World)));
        Assert.NotNull(view.Player);
    }

    [Fact]
    public void Fighters_RemovedAndReAddedOnOneBarrier_StayShown()
    {
        // Одна стадия: снят и добавлен заново — нормализация даёт Removed, затем Added того же тика.
        var (view, _) = TwoTickFrame((1, GameStages.Gameplay, true), (1, GameStages.Gameplay, false));
        Assert.NotNull(view.Player);
    }

    [Fact]
    public void Fighters_AddedThenRemovedLaterInSameTick_AreNotShown()
    {
        // Снят, добавлен, снова снят в стадиях одного тика: порядок двух очередей внутри тика не восстановить — итог
        // сверяется с миром, бойца нет.
        var (view, sim) = TwoTickFrame((1, GameStages.Gameplay, true), (1, GameStages.Movement, false), (1, GameStages.Settle, true));
        Assert.False(sim.World.Has<Fighter>(GameCore.Player(sim.World)));
        Assert.Null(view.Player);
        Assert.Empty(view.Fighters);
    }

    [Fact]
    public void Fighters_ComeFromFrameQueue_AfterFirstTick()
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty());
        var clock = new SimulationClock(sim, (_, _) => { });
        var view = new ArenaView(Field);
        clock.RunFrame(0);   // кадр без тиков: экспорта ещё не было — бойцов в представлении нет
        view.Extract(clock.Link, clock.Alpha);
        Assert.Empty(view.Fighters);
        clock.RunFrame(1.0 / 60);
        view.Extract(clock.Link, clock.Alpha);
        Assert.Equal(GameCore.Player(sim.World), Assert.Single(view.Fighters).Entity);
        Assert.NotNull(view.Player);
    }
}
