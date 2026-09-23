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

    /// <summary>Радиус круга корня, м: столкновения корней с колоннами и между бойцами (пара — ближе 2·0,35 м).</summary>
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
/// Gameplay: курс и разгон корня (<c>KinematicSteer</c>, начало <c>KinematicBegin</c>): курс догоняет цель не быстрее
/// <see cref="MovementRules.TurnRate"/>, скорость — желаемую с разгоном <see cref="MovementRules.Accel"/>. Стоит до
/// аниматора: в SWORD <c>anim.Tick</c> идёт между разгоном и переносом корня и видит новую скорость. Действий ещё нет
/// (этап 6): масштаб хода и поворота — 1. Враги пока поворачивают тем же контроллером, что игрок (<c>Agile</c>): у
/// не-<c>Agile</c> SWORD стоя — 2,5 рад/с и поворот на месте клипом, если отстал больше 45° (точка 4г).
/// </summary>
public sealed class RootSteerSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootSteer).Reads<FighterCommand>().Writes<Transform>().Writes<RootVelocity>();
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
            for (int i = 0; i < t.Length; i++) Steer(intent[i], ref t[i], ref v[i].Value, dt);
        }
    }

    /// <summary>Курс, затем скорость — порядок SWORD.</summary>
    public static void Steer(in FighterCommand intent, ref Transform t, ref Vector3 velocity, float dt)
    {
        float df = MathF.IEEERemainder(intent.FacingGoal - t.Facing, MathF.Tau);
        t.Facing += Math.Clamp(df, -MovementRules.TurnRate * dt, MovementRules.TurnRate * dt);

        var desired = new Vector3(intent.DesiredVelocity.X, 0, intent.DesiredVelocity.Z);
        velocity += (desired - velocity) * MathF.Min(1, MovementRules.Accel * dt);
        velocity.Y = 0;
    }
}

/// <summary>
/// Movement: перенос корня на скорость·dt по земле (<c>KinematicBegin</c> после <c>anim.Tick</c>). Корневого движения
/// действий (выпад, уклонение) и поворота клипом ещё нет — они придут с действиями (этап 6).
/// </summary>
public sealed class RootControlSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootControl).Reads<RootVelocity>().Writes<Transform>();
        _query = s.Query().With<Fighter>().Read<RootVelocity>().Write<Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var t = chunk.Write<Transform>();
            var v = chunk.Read<RootVelocity>()[..t.Length];
            for (int i = 0; i < t.Length; i++) Move(ref t[i], v[i].Value, dt);
        }
    }

    public static void Move(ref Transform t, Vector3 velocity, float dt)
    {
        t.Position += velocity * dt;
        t.Position.Y = 0;
    }
}

/// <summary>
/// Толчки корня врага столкновениями бойцов на последнем тике (<c>ResolveRoots</c>, оба прохода) — агентский вид
/// столкновений: кто кого толкнул и насколько. Пишет <see cref="RootCollisionSystem"/> каждый тик; читают агент
/// (<c>entity.get</c>, <c>watch.add</c>, условия и агрегаты) и манифест фичи. Хешируется (участник <c>ecs</c>): это
/// производное состояние, детерминированное, как и сами корни. Только у врагов (как <c>EnemyAiTerms</c>): толчок игрока
/// парами — <c>−Σ FromPlayer</c> по врагам (каждая пара сдвигает обоих на одну и ту же величину в разные стороны).
/// Колонны и край арены сюда не входят.
/// </summary>
public struct RootContacts
{
    /// <summary>Сколько раз за тик разрешалась пара с этим бойцом (оба прохода; пара в обоих проходах — 2).</summary>
    public int Contacts;

    /// <summary>Сдвиг корня парами с бойцами за тик, м (сумма толчков обоих проходов).</summary>
    public Vector3 Push;

    /// <summary>Часть <see cref="Push"/> от пары с игроком (место 0), м: игрок толкнул этого врага.</summary>
    public Vector3 FromPlayer;

    /// <summary>Наибольшее перекрытие пары до разрешения за тик, м (0,7 − расстояние корней; 0 — касаний не было).</summary>
    public float Deepest;

    /// <summary>Место бойца этой пары (0 — игрок); −1 — касаний не было.</summary>
    public int DeepestWith;

    /// <summary>Касаний не было.</summary>
    public static readonly RootContacts None = new() { DeepestWith = -1 };
}

/// <summary>
/// <c>ResolveRoots</c> SWORD 2eab13b (<c>DuelArena.cs</c>) по местам бойцов: два прохода; в каждом бойцы по порядку мест,
/// у бойца — сначала пары со всеми следующими (круги корней по 0,35 м: каждому половина перекрытия, скорость внутрь
/// соседа гасится), затем колонны и край арены — до следующего бойца. Порядок вычислений float тот же (сверка —
/// <c>RootCollisionTests</c>, побитово против копии SWORD).
/// </summary>
public static class RootCollision
{
    /// <summary>
    /// Корни и скорости по местам (0 — игрок, 1…32 — враги); <paramref name="present"/> — место занято. Пустое место
    /// пропускается, как неактивное в SWORD (<c>i &lt; ActiveCount</c>); этап 6 добавит сюда пропуск мёртвых
    /// (<c>a.Dead</c>, <c>b.Dead</c>). <paramref name="contacts"/> — толчки пар за вызов по местам (перезаписываются).
    /// </summary>
    public static void Resolve(Span<Vector3> roots, Span<Vector3> velocities, ReadOnlySpan<bool> present, Span<RootContacts> contacts)
    {
        int n = Math.Min(Math.Min(roots.Length, velocities.Length), Math.Min(present.Length, contacts.Length));
        for (int i = 0; i < n; i++) contacts[i] = RootContacts.None;
        for (int iter = 0; iter < MovementRules.CollisionIterations; iter++)
            for (int i = 0; i < n; i++)
            {
                if (!present[i]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (!present[j]) continue;
                    Pair(ref roots[i], ref velocities[i], ref roots[j], ref velocities[j], out var push, out float overlap);
                    if (overlap <= 0) continue;
                    Record(ref contacts[i], push, overlap, j);
                    Record(ref contacts[j], -push, overlap, i);
                }
                foreach (var pillar in ArenaLayout.Pillars) RootCollisionSystem.PushOutOfPillar(ref roots[i], ref velocities[i], pillar);
                RootCollisionSystem.KeepInsideArena(ref roots[i], ref velocities[i]);
            }
    }

    /// <summary>
    /// Пара бойцов (a — место раньше): ближе 0,7 м — каждому половина перекрытия по линии корней, скорость a внутрь b и
    /// b внутрь a гасится. Совпавшие корни (ближе 1e-4 м) не разводятся — как в SWORD. <paramref name="overlap"/> —
    /// перекрытие до разрешения (0 — пары не было), <paramref name="push"/> — сдвиг a (b сдвинут на −push).
    /// </summary>
    public static void Pair(ref Vector3 a, ref Vector3 aVelocity, ref Vector3 b, ref Vector3 bVelocity, out Vector3 push, out float overlap)
    {
        push = default;
        overlap = 0;
        var d = a - b;
        float l = d.Length();
        if (l >= 2 * MovementRules.RootRadius || l < 1e-4f) return;
        var n = d / l;
        overlap = 2 * MovementRules.RootRadius - l;
        push = n * (overlap * 0.5f);
        a += push;
        b -= push;
        float va = Vector3.Dot(aVelocity, n), vb = Vector3.Dot(bVelocity, n);
        if (va < 0) aVelocity -= n * va;
        if (vb > 0) bVelocity -= n * vb;
    }

    private static void Record(ref RootContacts c, Vector3 push, float overlap, int other)
    {
        c.Contacts++;
        c.Push += push;
        if (other == 0) c.FromPlayer += push;
        if (overlap > c.Deepest)
        {
            c.Deepest = overlap;
            c.DeepestWith = other;
        }
    }
}

/// <summary>
/// Movement, после <see cref="RootControlSystem"/>: столкновения корней (<c>ResolveRoots</c> SWORD целиком,
/// <see cref="RootCollision.Resolve"/>) — бойцы собираются по местам (<see cref="FighterSlot"/>: порядок обхода SWORD —
/// игрок 0, враги 1…32), разрешаются два прохода (пары бойцов, колонны, край арены) и записываются обратно. Корень
/// выталкивается на край препятствия, скорость внутрь него гасится: боец упирается и скользит вдоль, бойцы
/// расталкивают друг друга. Врагам — вид толчков тика (<see cref="RootContacts"/>). Массивы мест заводятся один раз —
/// тик памяти не выделяет. Боец без места или второй на том же месте (нарушение <c>arena.slots</c>) не сдвигается.
/// </summary>
public sealed class RootCollisionSystem : IContinuousSystem
{
    private Query _fighters = null!, _viewers = null!;
    private readonly Vector3[] _roots = new Vector3[ArenaRules.MaxFighters], _velocities = new Vector3[ArenaRules.MaxFighters];
    private readonly bool[] _present = new bool[ArenaRules.MaxFighters];
    private readonly Entity[] _owners = new Entity[ArenaRules.MaxFighters];
    private readonly RootContacts[] _contacts = new RootContacts[ArenaRules.MaxFighters];

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootCollision).After(GameLabels.RootControl).Reads<FighterSlot>().Writes<Transform>()
            .Writes<RootVelocity>().Writes<RootContacts>();
        _fighters = s.Query().With<Fighter>().Read<FighterSlot>().Write<Transform>().Write<RootVelocity>();
        _viewers = s.Query().With<Fighter>().Read<FighterSlot>().Write<RootContacts>();
    }

    public void Run(in SystemContext ctx)
    {
        Array.Clear(_present);
        foreach (var chunk in ctx.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            var t = chunk.Write<Transform>()[..entities.Length];
            var v = chunk.Write<RootVelocity>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++)
            {
                int slot = slots[i].Index;
                if ((uint)slot >= ArenaRules.MaxFighters || _present[slot]) continue;
                (_roots[slot], _velocities[slot], _owners[slot], _present[slot]) = (t[i].Position, v[i].Value, entities[i], true);
            }
        }

        RootCollision.Resolve(_roots, _velocities, _present, _contacts);

        foreach (var chunk in ctx.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            var t = chunk.Write<Transform>()[..entities.Length];
            var v = chunk.Write<RootVelocity>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++)
                if (Owns(slots[i].Index, entities[i], out int slot))
                {
                    t[i].Position = _roots[slot];
                    v[i].Value = _velocities[slot];
                }
        }
        foreach (var chunk in ctx.Iterate(_viewers))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            var c = chunk.Write<RootContacts>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++) c[i] = Owns(slots[i].Index, entities[i], out int slot) ? _contacts[slot] : RootContacts.None;
        }
    }

    private bool Owns(int slot, Entity e, out int index)
    {
        index = slot;
        return (uint)slot < ArenaRules.MaxFighters && _present[slot] && _owners[slot] == e;
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
