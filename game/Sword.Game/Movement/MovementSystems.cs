using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Data;

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

    /// <summary>
    /// Боец не-<c>Agile</c> (враг) стоя: курс догоняет цель со скоростью 2,5 рад/с (<c>KinIdleTurnRate</c>), а отстал больше
    /// 45° — поворот на месте клипом (<c>KinTurnAnimMin</c>).
    /// </summary>
    public const float IdleTurnRate = 2.5f, TurnAnimMin = 45 * MathF.PI / 180;

    /// <summary>
    /// «Стоит» для поворота на месте (<c>KinematicSteer</c>): желаемая скорость по земле меньше 0,3 м/с (квадрат 0,09) и
    /// скорость корня меньше 0,5 м/с (квадрат 0,25).
    /// </summary>
    public const float StandingDesiredSq = 0.09f, StandingSpeedSq = 0.25f;

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
/// неё от текущего корня, если она дальше 0,3 м. Тик без команды — стоять, взгляд — на прежнюю точку. С 6в — бой игрока
/// (<see cref="PlayerActions"/>): фронты удара, укола и рывка — в буферы <see cref="PlayerControl"/>, рывок раньше удара,
/// удар — когда слой действий позволяет, выбор удара — <see cref="AttackPicker"/> (укол — счётчиковый ГСЧ, свой поток).
/// Несколько команд на тик — движение и жест последней, фронты — любой из них (нажатие не теряется).
/// </summary>
public sealed class PlayerCommandSystem(StrikeTable strikes) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.PlayerCommand).ReadsJournal<Event<PlayerCommand>>().Reads<Transform>().Writes<FighterCommand>()
            .Writes<GazeTarget>().Writes<AnimatorAction>().Writes<PlayerControl>().Writes<AttackCombo>();
        // мёртвый игрок (6г) замирает: команды игрока на него не действуют, пока сброс арены (R) не построит его заново
        _query = s.Query().With<PlayerTag>().Without<Dead>().Read<Transform>().Write<FighterCommand>().Write<GazeTarget>().Write<AnimatorAction>()
            .Write<PlayerControl>().Write<AttackCombo>();
    }

    public void Run(in SystemContext ctx)
    {
        var commands = ctx.Journal<Event<PlayerCommand>>();
        bool has = !commands.IsEmpty;
        var command = has ? commands[^1].Value : default;   // несколько на тик — действует последняя
        var log = ctx.Causality;
        var press = log is null ? CauseId.None : Press(commands);
        for (int c = commands.Length - 2; c >= 0; c--)
        {
            // фронты прежних команд тика не теряются; жест — последнего удара (обход с конца: первый найденный — последний)
            var earlier = commands[c].Value;
            if (earlier.Attack && !command.Attack) command.Gesture = earlier.Gesture;
            command.Attack |= earlier.Attack;
            command.Stab |= earlier.Stab;
            command.Dash |= earlier.Dash;
        }
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        var rng = world.Resource<SimRandom>();
        var rules = world.Resource<CombatRules>();
        var clips = world.Resource<PosePool>().Clips;
        var actions = world.Resource<ActionTable>();
        var attacks = strikes.Attacks;
        int slashes = strikes.Slashes.Length, stabs = Math.Max(1, attacks.Length - slashes);
        foreach (var chunk in ctx.Iterate(_query))
        {
            var intent = chunk.Write<FighterCommand>();
            var entities = chunk.Entities;
            var t = chunk.Read<Transform>()[..intent.Length];
            var gaze = chunk.Write<GazeTarget>()[..intent.Length];
            var action = chunk.Write<AnimatorAction>()[..intent.Length];
            var control = chunk.Write<PlayerControl>()[..intent.Length];
            var combo = chunk.Write<AttackCombo>()[..intent.Length];
            for (int i = 0; i < intent.Length; i++)
            {
                Apply(command, t[i], ref intent[i], ref gaze[i]);
                PlayerActions.Press(command, ref control[i]);
                int stabRoll = rng.Index(PlayerControlRules.PickStream, time.Tick, entities[i], stabs), serial = action[i].AttackSerial;
                PlayerActions.Think(ref control[i], ref combo[i], ref action[i], t[i], intent[i].DesiredVelocity, time.Time, time.Dt, stabRoll,
                    rules, attacks, slashes, clips, actions);
                if (log is not null) Caused(ctx, log, entities[i], press, serial, action[i].AttackSerial);
            }
        }
    }

    /// <summary>Причина нажатия удара тика — последняя команда с <c>Attack</c> или <c>Stab</c> (диагностический режим, 6д); нет — пусто.</summary>
    private static CauseId Press(ReadOnlySpan<Event<PlayerCommand>> commands)
    {
        for (int c = commands.Length - 1; c >= 0; c--)
            if (commands[c].Value.Attack || commands[c].Value.Stab) return commands[c].Cause;
        return CauseId.None;
    }

    /// <summary>
    /// Диагностический режим (6д): нажатие удара — в таблицу связей (удар выходит из буфера, может быть и тиками позже); удар начался —
    /// шаг «начало удара» с родителем «последнее нажатие».
    /// </summary>
    private static void Caused(in SystemContext ctx, CausalityLog log, Entity e, CauseId press, int before, int serial)
    {
        if (press.Value != 0) log.Link(e, CombatCauses.Swing, press);
        CombatCauses.Started(ctx, log, e, before, serial, log.Linked(e, CombatCauses.Swing));
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
/// Gameplay: курс и разгон корня (<c>KinematicSteer</c>, начало <c>KinematicBegin</c>): курс догоняет цель, скорость —
/// желаемую с разгоном <see cref="MovementRules.Accel"/>. Стоит до аниматора: в SWORD <c>anim.Tick</c> идёт между разгоном
/// и переносом корня и видит новую скорость. <c>Agile</c> SWORD (<c>Agile = f.IsPlayer</c>) — боец с
/// <see cref="PlayerTag"/>: курс — сразу контроллером, 12 рад/с. Боец не-<c>Agile</c> (враг, со слоем действий
/// <see cref="AnimatorAction"/>; 4г): стоя курс догоняет цель со скоростью 2,5 рад/с, а отстал больше 45° — поворот на
/// месте клипом (<see cref="ActionLayer.StartTurn"/>), и курс, пока он стоит, ведёт yaw клипа (<c>RootControlSystem</c>);
/// на ходу — 12 рад/с. Масштаб поворота и хода действия (<c>TurnScale</c>, <c>MoveScale</c>; 6в — <see cref="ActionLayer.TurnScale"/>,
/// <see cref="ActionLayer.MoveScale"/>): у игрока ход не замедляется никогда, поворот в окне урона — 0,6; у врага в ударе — 0,35 и
/// 0,35. У игрока здесь же — прибавка скорости рывка тика (<see cref="PlayerActions.Boost"/>; корень сдвигает Movement).
/// </summary>
public sealed class RootSteerSystem(StrikeTable strikes) : IContinuousSystem
{
    private Query _agile = null!, _steady = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootSteer).Reads<FighterCommand>().Reads<AnimatorMotion>().Writes<Transform>().Writes<RootVelocity>()
            .Writes<AnimatorAction>().Writes<PlayerControl>();
        _agile = s.Query().With<Fighter>().Without<Dead>().With<PlayerTag>().Read<FighterCommand>().Read<AnimatorAction>().Write<Transform>()
            .Write<RootVelocity>().Write<PlayerControl>();
        _steady = s.Query().With<Fighter>().Without<Dead>().Without<PlayerTag>().Read<FighterCommand>().Read<AnimatorMotion>().Write<Transform>()
            .Write<RootVelocity>().Write<AnimatorAction>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        var rules = ctx.World.Resource<CombatRules>();
        var attacks = strikes.Attacks;
        foreach (var chunk in ctx.Iterate(_agile))
        {
            var t = chunk.Write<Transform>();
            var intent = chunk.Read<FighterCommand>()[..t.Length];
            var a = chunk.Read<AnimatorAction>()[..t.Length];
            var v = chunk.Write<RootVelocity>()[..t.Length];
            var control = chunk.Write<PlayerControl>()[..t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                Steer(intent[i], ref t[i], ref v[i].Value, dt, ActionLayer.TurnScale(a[i], true, attacks), ActionLayer.MoveScale(a[i], true));
                PlayerActions.Boost(ref control[i], rules, dt);
            }
        }
        var clips = ctx.World.Resource<PosePool>().Clips;
        var actions = ctx.World.Resource<ActionTable>();
        foreach (var chunk in ctx.Iterate(_steady))
        {
            var t = chunk.Write<Transform>();
            var intent = chunk.Read<FighterCommand>()[..t.Length];
            var motion = chunk.Read<AnimatorMotion>()[..t.Length];
            var v = chunk.Write<RootVelocity>()[..t.Length];
            var a = chunk.Write<AnimatorAction>()[..t.Length];
            for (int i = 0; i < t.Length; i++)
                SteerSteady(intent[i], motion[i].Moving, ref t[i], ref v[i].Value, ref a[i], dt, clips, actions, attacks);
        }
    }

    /// <summary>
    /// <c>Agile</c>: курс не быстрее <see cref="MovementRules.TurnRate"/>·<paramref name="turnScale"/>, затем скорость к желаемой
    /// ·<paramref name="moveScale"/> — порядок и выражения SWORD (при масштабах 1 — побитово как без действий).
    /// </summary>
    public static void Steer(in FighterCommand intent, ref Transform t, ref Vector3 velocity, float dt, float turnScale = 1, float moveScale = 1)
    {
        float df = MathF.IEEERemainder(intent.FacingGoal - t.Facing, MathF.Tau);
        float turn = MovementRules.TurnRate * turnScale;
        t.Facing += Math.Clamp(df, -turn * dt, turn * dt);
        Accelerate(intent, ref velocity, dt, moveScale);
    }

    /// <summary>
    /// Не-<c>Agile</c> (<c>KinematicSteer</c> SWORD): «стоит» — желаемая скорость и скорость корня малы; стоит без
    /// действия и отстал больше 45° — поворот клипом; курс, если его не ведёт клип (<paramref name="moving"/> — «идёт»
    /// прошлого тика), — 2,5 рад/с стоя без действия, иначе 12 рад/с × <c>TurnScale</c>; затем скорость к желаемой ×
    /// <c>MoveScale</c>.
    /// </summary>
    public static void SteerSteady(in FighterCommand intent, bool moving, ref Transform t, ref Vector3 velocity, ref AnimatorAction action,
        float dt, ClipLibrary clips, ActionTable actions, ReadOnlySpan<AttackInfo> attacks)
    {
        float df = MathF.IEEERemainder(intent.FacingGoal - t.Facing, MathF.Tau);
        bool standing = new Vector2(intent.DesiredVelocity.X, intent.DesiredVelocity.Z).LengthSquared() < MovementRules.StandingDesiredSq
                        && new Vector2(velocity.X, velocity.Z).LengthSquared() < MovementRules.StandingSpeedSq;
        bool idleTurn = standing && action.Kind == ActionKind.None;
        if (idleTurn && MathF.Abs(df) > MovementRules.TurnAnimMin) ActionLayer.StartTurn(ref action, df, clips, actions);
        // пошёл во время поворота на месте — клип гаснет уже без yaw, курс снова доворачивает контроллер
        if (!action.TurnDrivesYaw(moving))
        {
            float turn = idleTurn ? MovementRules.IdleTurnRate : MovementRules.TurnRate * ActionLayer.TurnScale(action, false, attacks);
            t.Facing += Math.Clamp(df, -turn * dt, turn * dt);
        }
        Accelerate(intent, ref velocity, dt, ActionLayer.MoveScale(action, false));
    }

    private static void Accelerate(in FighterCommand intent, ref Vector3 velocity, float dt, float moveScale)
    {
        var desired = new Vector3(intent.DesiredVelocity.X, 0, intent.DesiredVelocity.Z) * moveScale;
        velocity += (desired - velocity) * MathF.Min(1, MovementRules.Accel * dt);
        velocity.Y = 0;
    }
}

/// <summary>
/// Movement: перенос корня (<c>KinematicBegin</c> после <c>anim.Tick</c>): у бойца со слоем действий сначала курс —
/// поворот клипом (<see cref="AnimatorAction.YawDelta"/>), затем у всех корень на скорость·dt по земле, затем сдвиг клипом
/// действия (<see cref="AnimatorAction.RootDelta"/>) по новому курсу. У игрока в рывке (6в) корень после скорости сдвигается ещё
/// на прибавку рывка·dt (<see cref="PlayerControl.Boost"/>): рывок ведёт контроллер, а не клип уклонения.
/// </summary>
public sealed class RootControlSystem : IContinuousSystem
{
    private Query _plain = null!, _acting = null!, _controlled = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.RootControl).Reads<RootVelocity>().Reads<AnimatorAction>().Reads<PlayerControl>().Writes<Transform>();
        _plain = s.Query().With<Fighter>().Without<Dead>().Without<AnimatorAction>().Read<RootVelocity>().Write<Transform>();
        _acting = s.Query().With<Fighter>().Without<Dead>().Without<PlayerControl>().Read<RootVelocity>().Read<AnimatorAction>().Write<Transform>();
        _controlled = s.Query().With<Fighter>().Without<Dead>().Read<RootVelocity>().Read<AnimatorAction>().Read<PlayerControl>().Write<Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_plain))
        {
            var t = chunk.Write<Transform>();
            var v = chunk.Read<RootVelocity>()[..t.Length];
            for (int i = 0; i < t.Length; i++) Move(ref t[i], v[i].Value, dt);
        }
        foreach (var chunk in ctx.Iterate(_acting))
        {
            var t = chunk.Write<Transform>();
            var v = chunk.Read<RootVelocity>()[..t.Length];
            var a = chunk.Read<AnimatorAction>()[..t.Length];
            for (int i = 0; i < t.Length; i++) Move(ref t[i], v[i].Value, a[i], dt);
        }
        foreach (var chunk in ctx.Iterate(_controlled))
        {
            var t = chunk.Write<Transform>();
            var v = chunk.Read<RootVelocity>()[..t.Length];
            var a = chunk.Read<AnimatorAction>()[..t.Length];
            var c = chunk.Read<PlayerControl>()[..t.Length];
            for (int i = 0; i < t.Length; i++) Move(ref t[i], v[i].Value, a[i], dt, c[i].Boost);
        }
    }

    public static void Move(ref Transform t, Vector3 velocity, float dt)
    {
        t.Position += velocity * dt;
        t.Position.Y = 0;
    }

    /// <summary>
    /// Со слоем действий: <c>Facing += YawDelta</c>, корень на скорость (и прибавку рывка <paramref name="boost"/>, 6в), сдвиг
    /// клипом по курсу (порядок — идея SWORD).
    /// </summary>
    public static void Move(ref Transform t, Vector3 velocity, in AnimatorAction action, float dt, Vector3 boost = default)
    {
        t.Facing += action.YawDelta;
        t.Position += velocity * dt;
        if (boost != Vector3.Zero) t.Position += boost * dt;
        if (action.RootDelta != Vector3.Zero)
            t.Position += Vector3.Transform(action.RootDelta, Quaternion.CreateFromAxisAngle(Vector3.UnitY, t.Facing));
        t.Position.Y = 0;
    }
}

/// <summary>
/// Толчки корня врага столкновениями бойцов на последнем тике (<c>ResolveRoots</c>, оба прохода) — агентский вид
/// столкновений: кто кого толкнул и насколько. Пишет <see cref="RootCollisionSystem"/> каждый тик; читают агент
/// (<c>entity.get</c>, <c>watch.add</c>, условия и агрегаты) и манифест фичи. Хешируется (участник <c>ecs</c>): это
/// производное состояние, детерминированное, как и сами корни. Только у врагов (как <c>EnemyAiTerms</c>): толчок игрока
/// парами — <c>−Σ FromPlayer</c> по врагам (каждая пара сдвигает обоих на одну и ту же величину в разные стороны).
/// Колонны и край арены — отдельно, <see cref="Obstacle"/> (4д): <see cref="Push"/> + <see cref="Obstacle"/> — весь сдвиг корня
/// столкновениями за тик (с точностью до округления float).
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

    /// <summary>Сдвиг корня колоннами и краем арены за тик, м (оба прохода; точка 4д): то, чего пары не объясняют.</summary>
    public Vector3 Obstacle;

    /// <summary>Касаний не было.</summary>
    public static readonly RootContacts None = new() { DeepestWith = -1 };
}

/// <summary>
/// <c>ResolveRoots</c> SWORD 2eab13b (<c>DuelArena.cs</c>) по местам бойцов: два прохода; в каждом бойцы по порядку мест,
/// у бойца — сначала пары со всеми следующими (круги корней по 0,35 м: каждому половина перекрытия, скорость внутрь
/// соседа гасится), затем колонны и край арены — до следующего бойца. Свойства — <c>RootCollisionTests</c> (сдвиг объясняют
/// пары и препятствия, корни вне колонн и внутри арены).
/// </summary>
public static class RootCollision
{
    /// <summary>
    /// Корни и скорости по местам (0 — игрок, 1…32 — враги); <paramref name="present"/> — место занято. Пустое место
    /// пропускается, как неактивное в SWORD (<c>i &lt; ActiveCount</c>); мёртвый (<c>a.Dead</c>, <c>b.Dead</c> SWORD, 6г) — тоже:
    /// <see cref="RootCollisionSystem"/> не отмечает его место занятым. <paramref name="contacts"/> — толчки пар, колонн и края за вызов по местам
    /// (перезаписываются; только учёт — корни и скорости он не трогает).
    /// </summary>
    /// <remarks>
    /// Тело прохода по бойцу — свой метод (<see cref="Fighter"/>): за вызов ~1000 пар, и долгий цикл в методе, который зовётся
    /// раз в тик, многоуровневый JIT заменял на ходу (OSR) — компиляция 1,5 мс на главном потоке в первые секунды толпы
    /// («Разбор Пд», вопрос 3; исправлено по решению пользователя). Порядок вычислений тот же.
    /// </remarks>
    public static void Resolve(Span<Vector3> roots, Span<Vector3> velocities, ReadOnlySpan<bool> present, Span<RootContacts> contacts)
    {
        int n = Math.Min(Math.Min(roots.Length, velocities.Length), Math.Min(present.Length, contacts.Length));
        for (int i = 0; i < n; i++) contacts[i] = RootContacts.None;
        for (int iter = 0; iter < MovementRules.CollisionIterations; iter++)
            for (int i = 0; i < n; i++)
                if (present[i]) Fighter(roots[..n], velocities[..n], present[..n], contacts[..n], i);
    }

    /// <summary>
    /// Боец <paramref name="i"/> одного прохода: пары со всеми следующими, затем колонны и край арены (<c>ResolveRoots</c>
    /// SWORD, тело цикла по бойцам).
    /// </summary>
    private static void Fighter(Span<Vector3> roots, Span<Vector3> velocities, ReadOnlySpan<bool> present, Span<RootContacts> contacts, int i)
    {
        for (int j = i + 1; j < roots.Length; j++)
        {
            if (!present[j]) continue;
            Pair(ref roots[i], ref velocities[i], ref roots[j], ref velocities[j], out var push, out float overlap);
            if (overlap <= 0) continue;
            Record(ref contacts[i], push, overlap, j);
            Record(ref contacts[j], -push, overlap, i);
        }
        var before = roots[i];
        foreach (var pillar in ArenaLayout.Pillars) RootCollisionSystem.PushOutOfPillar(ref roots[i], ref velocities[i], pillar);
        RootCollisionSystem.KeepInsideArena(ref roots[i], ref velocities[i]);
        contacts[i].Obstacle += roots[i] - before;
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
        // мёртвые (6г) из столкновений выпадают целиком, как в ResolveRoots SWORD (a.Dead, b.Dead): их корень стоит, живые проходят сквозь
        _fighters = s.Query().With<Fighter>().Without<Dead>().Read<FighterSlot>().Write<Transform>().Write<RootVelocity>();
        _viewers = s.Query().With<Fighter>().Without<Dead>().Read<FighterSlot>().Write<RootContacts>();
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
