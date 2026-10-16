using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Movement;

namespace Sword.Game.Tests;

/// <summary>Движение игрока в композиции игры по формулам <see cref="MovementRules"/>: разгон, поворот, столкновения, курс на взгляд.</summary>
public class MovementTests
{
    private static (Simulation Sim, Entity Player) Create(params string[] sets)
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty(sets));
        return (sim, GameCore.Player(sim.World));
    }

    [Fact]
    public void Core_IsNotAModule_AndCannotBeSwitchedOff()
    {
        // Ядро — не модуль и не выключается; его регистрации без токена модуля.
        var sim = TestAssets.Simulation(ConfigSet.Empty());
        Assert.DoesNotContain(sim.ActiveModules, m => m == "core");
        Assert.All(sim.World.Systems.Where(s => s.Label.StartsWith("core.", StringComparison.Ordinal)), s => Assert.Null(s.Module));
        Assert.Contains("modules.core", Assert.Throws<InvalidDataException>(() =>
            TestAssets.Simulation(ConfigSet.Empty("modules.core.enabled=false"))).Message);
    }

    private static void Run(Simulation sim, int ticks, PlayerCommand command)
    {
        for (int i = 0; i < ticks; i++)
        {
            sim.Enqueue(command);
            sim.Step();
        }
    }

    [Fact]
    public void PlayerSpawnsAtCentre_Standing()
    {
        var (sim, p) = Create();
        Run(sim, 10, default);
        var t = sim.World.Get<Transform>(p);
        Assert.Equal(Vector3.Zero, t.Position);
        Assert.Equal(0f, t.Facing);
        Assert.Equal(Vector3.Zero, sim.World.Get<RootVelocity>(p).Value);
    }

    [Fact]
    public void FirstTick_AcceleratesByKinAccel()
    {
        var (sim, p) = Create();
        Run(sim, 1, new PlayerCommand { Move = new Vector2(1, 0) });
        float dt = 1f / 120;
        float v = MovementRules.RunSpeed * MathF.Min(1, MovementRules.Accel * dt);
        Assert.Equal(v, sim.World.Get<RootVelocity>(p).Value.X, 6);
        Assert.Equal(v * dt, sim.World.Get<Transform>(p).Position.X, 6);
        Assert.Equal(Vector3.Zero, sim.World.Get<PrevTransform>(p).Position);   // прошлый тик — до шага
    }

    [Theory]
    [InlineData(false, MovementRules.RunSpeed)]
    [InlineData(true, MovementRules.WalkSpeed)]
    public void Speed_ConvergesToRunOrWalk_DirectionIsNormalized(bool walk, float speed)
    {
        var (sim, p) = Create();
        Run(sim, 240, new PlayerCommand { Move = new Vector2(3, 4), Walk = walk });   // длина 5 — нормируется
        var v = sim.World.Get<RootVelocity>(p).Value;
        Assert.Equal(speed, v.Length(), 4);
        Assert.Equal(0.6f, v.X / v.Length(), 5);
        Assert.Equal(0.8f, v.Z / v.Length(), 5);
    }

    [Fact]
    public void Facing_TurnsAtKinTurnRate_TowardGaze()
    {
        var (sim, p) = Create();
        var gaze = new PlayerCommand { HasGaze = true, Gaze = new Vector3(5, 0, 0) };   // курс-цель π/2
        Run(sim, 1, gaze);
        Assert.Equal(MovementRules.TurnRate / 120, sim.World.Get<Transform>(p).Facing, 6);
        Run(sim, 20, gaze);
        Assert.Equal(MathF.PI / 2, sim.World.Get<Transform>(p).Facing, 5);

        // Точка взгляда ближе 0,3 м к корню — курс-цель прежний.
        Run(sim, 20, new PlayerCommand { HasGaze = true, Gaze = new Vector3(0, 0, 0.2f) });
        Assert.Equal(MathF.PI / 2, sim.World.Get<Transform>(p).Facing, 5);
    }

    [Fact]
    public void Gaze_IsRemembered_AndTrackedFromCurrentRoot()
    {
        // Курсор запоминается, и каждый тик курс-цель — на него от текущего корня, даже когда курсора на земле нет.
        // Бежим по +X мимо точки (10, 0, 3): взгляд поворачивает за ней.
        var (sim, p) = Create();
        Run(sim, 1, new PlayerCommand { HasGaze = true, Gaze = new Vector3(10, 0, 3) });
        Run(sim, 120 * 5, new PlayerCommand { Move = new Vector2(1, 0) });   // без взгляда в командах
        var t = sim.World.Get<Transform>(p);
        Assert.True(t.Position.X > 15, $"x = {t.Position.X}");
        float expected = MathF.Atan2(10 - t.Position.X, 3 - t.Position.Z);   // точка уже позади: курс назад
        Assert.Equal(expected, t.Facing, 2);
    }

    [Fact]
    public void NonFiniteOrHugeCommand_DoesNotBreakMovement()
    {
        var (sim, p) = Create();
        Run(sim, 60, new PlayerCommand { Move = new Vector2(float.NaN, 1), HasGaze = true, Gaze = new Vector3(float.PositiveInfinity, 0, 0) });
        var t = sim.World.Get<Transform>(p);
        Assert.Equal(Vector3.Zero, t.Position);   // нечисловое направление — стоять
        Assert.Equal(0f, t.Facing);               // нечисловая точка — не взгляд

        // Огромное, но конечное направление — то же, что единичное (длина не важна).
        Run(sim, 240, new PlayerCommand { Move = new Vector2(1e20f, 0) });
        Assert.Equal(MovementRules.RunSpeed, sim.World.Get<RootVelocity>(p).Value.X, 4);
    }

    [Fact]
    public void ArenaEdge_HoldsRoot_AndKeepsTangentialSpeed()
    {
        var (sim, p) = Create();
        Run(sim, 120 * 20, new PlayerCommand { Move = new Vector2(1, 0) });   // 20 с бега наружу: 84 м > радиуса
        var t = sim.World.Get<Transform>(p);
        float lim = ArenaLayout.ArenaRadius - MovementRules.EdgeMargin;
        Assert.Equal(lim, t.Position.Length(), 3);
        Assert.True(sim.World.Get<RootVelocity>(p).Value.X <= 1e-4f);   // наружу не едет

        // Бег по касательной у края: скользит вдоль, радиус не растёт.
        Run(sim, 120, new PlayerCommand { Move = new Vector2(0, 1) });
        var slid = sim.World.Get<Transform>(p).Position;
        Assert.True(slid.Z > 1, $"вдоль края: z = {slid.Z}");
        Assert.True(slid.Length() <= lim + 1e-3f);
    }

    [Fact]
    public void Pillar_RootNeverEntersIt_AndIsBlockedThenSlides()
    {
        // Корень — не ближе радиуса колонны плюс 0,35 м, скорость внутрь колонны гасится; остаток вдоль колонны уводит бойца
        // в обход («упираются и скользят»), насквозь он не проходит.
        var (sim, p) = Create();
        var pillar = ArenaLayout.Pillars[0];
        var centre = new Vector2(pillar.X, pillar.Y);
        var toPillar = Vector2.Normalize(centre);
        float rr = pillar.Z + MovementRules.RootRadius, slowest = float.MaxValue;
        bool touched = false;
        for (int i = 0; i < 120 * 12; i++)   // 12 с прямо в центр колонны (до неё 30 м)
        {
            Run(sim, 1, new PlayerCommand { Move = toPillar });
            var r = sim.World.Get<Transform>(p).Position;
            float d = Vector2.Distance(new Vector2(r.X, r.Z), centre);
            Assert.True(d >= rr - 1e-3f, $"тик {i}: корень внутри колонны, {d} < {rr}");
            if (d < rr + 1e-3f)
            {
                touched = true;
                slowest = MathF.Min(slowest, sim.World.Get<RootVelocity>(p).Value.Length());
            }
        }
        Assert.True(touched, "до колонны не добежал");
        Assert.True(slowest < 1f, $"у колонны скорость {slowest} м/с — не упёрся");
    }

    [Fact]
    public void Pillar_GlancingRun_SlidesAroundAndGoesOn()
    {
        // Бег мимо центра колонны на 0,5 м вбок: корень касается колонны, скользит по ней и бежит дальше.
        var (sim, p) = Create();
        var pillar = ArenaLayout.Pillars[0];
        var centre = new Vector2(pillar.X, pillar.Y);
        var dir = Vector2.Normalize(centre);
        var side = new Vector2(-dir.Y, dir.X);
        float rr = pillar.Z + MovementRules.RootRadius, closest = float.MaxValue;
        // Цель — точка за колонной со смещением 0,5 м: прямая к ней проходит сквозь колонну.
        var goal = centre + dir * 10 + side * 0.5f;
        for (int i = 0; i < 120 * 12; i++)
        {
            var r = sim.World.Get<Transform>(p).Position;
            var to = goal - new Vector2(r.X, r.Z);
            Run(sim, 1, new PlayerCommand { Move = to });
            var after = sim.World.Get<Transform>(p).Position;
            float d = Vector2.Distance(new Vector2(after.X, after.Z), centre);
            Assert.True(d >= rr - 1e-3f, $"тик {i}: внутри колонны");
            closest = MathF.Min(closest, d);
        }
        var end = sim.World.Get<Transform>(p).Position;
        Assert.True(closest < rr + 1e-3f, "колонны не коснулся");
        Assert.True(Vector2.Dot(new Vector2(end.X, end.Z) - centre, dir) > 5, "не обошёл колонну");
    }

    [Fact]
    public void SameCommands_SameState_BitForBit()
    {
        Vector3 Run1()
        {
            var (sim, p) = Create();
            var rng = new Random(5);
            for (int i = 0; i < 1000; i++)
            {
                sim.Enqueue(new PlayerCommand
                {
                    Move = new Vector2(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1),
                    Walk = rng.Next(4) == 0,
                    HasGaze = true,
                    Gaze = new Vector3(rng.NextSingle() * 40 - 20, 0, rng.NextSingle() * 40 - 20),
                });
                sim.Step();
            }
            var t = sim.World.Get<Transform>(p);
            return new Vector3(t.Position.X, t.Position.Z, t.Facing);
        }
        Assert.Equal(Run1(), Run1());
    }

    [Fact]
    public void TickWithoutCommand_Stops()
    {
        var (sim, p) = Create();
        Run(sim, 120, new PlayerCommand { Move = new Vector2(1, 0) });
        for (int i = 0; i < 120; i++) sim.Step();   // команд нет — стоять
        Assert.True(sim.World.Get<RootVelocity>(p).Value.Length() < 1e-3f);
    }

    [Fact]
    public void SteadyTicks_DoNotAllocate()
    {
        // 0 выделенных байтов на тик после прогрева — вся композиция игры, с командами тика.
        var (sim, _) = Create();
        var command = new PlayerCommand { Move = new Vector2(1, 1), HasGaze = true, Gaze = new Vector3(30, 0, 0) };
        Run(sim, 200, command);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(sim, 1000, command);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"1000 тиков выделили {allocated} Б");
    }
}
