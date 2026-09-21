using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Movement;

/// <summary>
/// Константы движения корня — из SWORD 2eab13b: скорости — <c>PlayerController</c>, поворот и разгон —
/// <c>DuelArena.Kinematic.cs</c>, край арены — <c>DuelArena.ResolveRoots</c>.
/// </summary>
public static class MovementRules
{
    /// <summary>Бег и шаг, м/с (<c>PlayerController.RunSpeed/WalkSpeed</c>).</summary>
    public const float RunSpeed = 4.2f, WalkSpeed = 1.6f;

    /// <summary>Поворот курса к цели, рад/с, и разгон к желаемой скорости, 1/с (<c>KinTurnRate</c>, <c>KinAccel</c>).</summary>
    public const float TurnRate = 12f, Accel = 14f;

    /// <summary>Курс меняется, только если точка взгляда дальше 0,3 м от корня (0,09 м² в SWORD).</summary>
    public const float GazeMinDistanceSq = 0.09f;

    /// <summary>Корень держится на 0,6 м внутри края арены.</summary>
    public const float EdgeMargin = 0.6f;

    /// <summary>Радиус круга корня, м: столкновения корней с колоннами (и с бойцами — этап 4).</summary>
    public const float RootRadius = 0.35f;

    /// <summary>Проходов разрешения столкновений корней за тик.</summary>
    public const int CollisionIterations = 2;
}

/// <summary>Begin: корень прошлого тика — для интерполяции клиента (начало <c>KinematicBegin</c>).</summary>
public sealed class PrevTransformSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.PrevTransform).Reads<Transform>().Writes<PrevTransform>();
        _query = s.Query().Read<Transform>().Write<PrevTransform>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var chunk in ctx.Iterate(_query))
        {
            var prev = chunk.Write<PrevTransform>();
            var t = chunk.Read<Transform>()[..prev.Length];   // цикл по длине колонки: JIT снимает проверки границ
            for (int i = 0; i < prev.Length; i++) prev[i] = new PrevTransform { Position = t[i].Position, Facing = t[i].Facing };
        }
    }
}

/// <summary>
/// Intent: команда игрока тика → намерение бойца игрока (<c>PlayerController.Think</c>): желаемая скорость — направление
/// команды на скорость бега или шага; точка взгляда запоминается (<see cref="GazeTarget"/>), курс-цель каждый тик — на
/// неё от текущего корня, если она дальше 0,3 м. Тик без команды — стоять, взгляд — на прежнюю точку.
/// </summary>
public sealed class PlayerCommandSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.PlayerCommand).ReadsJournal<Event<PlayerCommand>>().Reads<Transform>().Writes<FighterCommand>()
            .Writes<GazeTarget>();
        _query = s.Query().With<PlayerTag>().Read<Transform>().Write<FighterCommand>().Write<GazeTarget>();
    }

    public void Run(in SystemContext ctx)
    {
        var commands = ctx.Journal<Event<PlayerCommand>>();
        bool has = !commands.IsEmpty;
        var command = has ? commands[^1].Value : default;   // несколько на тик — действует последняя
        foreach (var chunk in ctx.Iterate(_query))
        {
            var intent = chunk.Write<FighterCommand>();
            var t = chunk.Read<Transform>()[..intent.Length];
            var gaze = chunk.Write<GazeTarget>()[..intent.Length];
            for (int i = 0; i < intent.Length; i++) Apply(command, t[i], ref intent[i], ref gaze[i]);
        }
    }

    public static void Apply(in PlayerCommand c, in Transform t, ref FighterCommand intent, ref GazeTarget gaze)
    {
        // Команда приходит и из реплея, и от агента: нечисловое направление — стоять, нечисловая точка — не взгляд.
        var wish = float.IsFinite(c.Move.X) && float.IsFinite(c.Move.Y) ? new Vector3(c.Move.X, 0, c.Move.Y) : Vector3.Zero;
        float lengthSq = wish.LengthSquared();
        if (lengthSq > 0)
        {
            // В SWORD направление WASD ограничено; огромное по модулю переполнило бы квадрат длины — сначала масштаб.
            if (!float.IsFinite(lengthSq)) wish /= MathF.Max(MathF.Abs(wish.X), MathF.Abs(wish.Z));
            wish = Vector3.Normalize(wish);
        }
        intent.DesiredVelocity = wish * (c.Walk ? MovementRules.WalkSpeed : MovementRules.RunSpeed);
        if (c.HasGaze && float.IsFinite(c.Gaze.X) && float.IsFinite(c.Gaze.Z))
        {
            gaze.Point = c.Gaze;
            gaze.Has = true;
        }
        if (!gaze.Has) return;
        var to = gaze.Point - t.Position;
        to.Y = 0;
        if (to.LengthSquared() > MovementRules.GazeMinDistanceSq) intent.FacingGoal = MathF.Atan2(to.X, to.Z);
    }
}

/// <summary>
/// Movement: корень-контроллер (<c>KinematicSteer</c> и шаг корня <c>KinematicBegin</c>): курс догоняет цель не быстрее
/// <see cref="MovementRules.TurnRate"/>, скорость — желаемую с разгоном <see cref="MovementRules.Accel"/>, корень едет
/// на скорость·dt по земле. Аниматора ещё нет (этап 2): масштаб хода и поворота — 1, корневого движения клипа нет,
/// поворот на месте клипом (у врагов) не делается.
/// </summary>
public sealed class RootControlSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootControl).Reads<FighterCommand>().Writes<Transform>().Writes<RootVelocity>();
        _query = s.Query().With<Fighter>().Read<FighterCommand>().Write<Transform>().Write<RootVelocity>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var t = chunk.Write<Transform>();
            var intent = chunk.Read<FighterCommand>()[..t.Length];
            var v = chunk.Write<RootVelocity>()[..t.Length];
            for (int i = 0; i < t.Length; i++) Step(intent[i], ref t[i], ref v[i].Value, dt);
        }
    }

    /// <summary>Один тик корня — порядок вычислений как в SWORD: курс, скорость, перенос.</summary>
    public static void Step(in FighterCommand intent, ref Transform t, ref Vector3 velocity, float dt)
    {
        float df = MathF.IEEERemainder(intent.FacingGoal - t.Facing, MathF.Tau);
        t.Facing += Math.Clamp(df, -MovementRules.TurnRate * dt, MovementRules.TurnRate * dt);

        var desired = new Vector3(intent.DesiredVelocity.X, 0, intent.DesiredVelocity.Z);
        velocity += (desired - velocity) * MathF.Min(1, MovementRules.Accel * dt);
        velocity.Y = 0;

        t.Position += velocity * dt;
        t.Position.Y = 0;
    }
}

/// <summary>
/// Movement, после <see cref="RootControlSystem"/>: столкновения корней (<c>ResolveRoots</c>) — два прохода, в каждом
/// колонны, затем край арены. Корень выталкивается на край препятствия, скорость внутрь него гасится: боец упирается
/// и скользит вдоль. Столкновения корней бойцов между собой — с врагами (этап 4).
/// </summary>
public sealed class RootCollisionSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootCollision).After(GameLabels.RootControl).Writes<Transform>().Writes<RootVelocity>();
        _query = s.Query().With<Fighter>().Write<Transform>().Write<RootVelocity>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var chunk in ctx.Iterate(_query))
        {
            var t = chunk.Write<Transform>();
            var v = chunk.Write<RootVelocity>()[..t.Length];
            for (int i = 0; i < t.Length; i++) Resolve(ref t[i].Position, ref v[i].Value);
        }
    }

    /// <summary>Один боец: порядок <c>ResolveRoots</c> — проходы, в каждом колонны, затем край.</summary>
    public static void Resolve(ref Vector3 root, ref Vector3 velocity)
    {
        for (int iter = 0; iter < MovementRules.CollisionIterations; iter++)
        {
            foreach (var pillar in ArenaLayout.Pillars) PushOutOfPillar(ref root, ref velocity, pillar);
            KeepInsideArena(ref root, ref velocity);
        }
    }

    /// <summary>Колонна: xy — центр (x, z), z — радиус; корень — на радиус колонны плюс свой.</summary>
    public static void PushOutOfPillar(ref Vector3 root, ref Vector3 velocity, Vector4 pillar)
    {
        var d = new Vector3(root.X - pillar.X, 0, root.Z - pillar.Y);
        float l = d.Length(), rr = pillar.Z + MovementRules.RootRadius;
        if (l >= rr || l < 1e-4f) return;
        var n = d / l;
        root = new Vector3(pillar.X, 0, pillar.Y) + n * rr;
        float v = Vector3.Dot(velocity, n);
        if (v < 0) velocity -= n * v;
    }

    public static void KeepInsideArena(ref Vector3 root, ref Vector3 velocity)
    {
        float lim = ArenaLayout.ArenaRadius - MovementRules.EdgeMargin, dist = root.Length();
        if (dist <= lim) return;
        var n = root / dist;
        root = n * lim;
        float out_ = Vector3.Dot(velocity, n);
        if (out_ > 0) velocity -= n * out_;
    }
}
