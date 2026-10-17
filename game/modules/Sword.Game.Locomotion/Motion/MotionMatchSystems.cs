using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Системы Motion Matching игрока (<c>locomotion.player = mm</c>): корень и курс ведёт контроллер ядра, MM выбирает
/// и проигрывает позу. Порядок: mm-setup (Gameplay) → mm (Animation) → mm-history (Movement) → mm-release (End).
/// </summary>
public static class MotionMatchSystems
{
    public static readonly SystemLabel Setup = new("locomotion.mm-setup"), Step = new("locomotion.mm"),
        History = new("locomotion.mm-history"), Release = new("locomotion.mm-release");
}

/// <summary>
/// Gameplay, по <c>Added&lt;Fighter&gt;</c> игрока: построенный (заново) игрок начинает MM с нуля — флаг
/// <see cref="MotionMatchState.Restart"/>; хэндлы берёт <c>locomotion.mm</c> этого же тика, не реакция (иначе снятый на барьере
/// появления боец терял бы их). Игроку, созданному командой, ставит и модель контроллера, и позу постройки.
/// </summary>
public sealed class MotionMatchSetupSystem(MotionMatchRuntime runtime) : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) => s.Label(MotionMatchSystems.Setup).Adds<MotionMatchState>().Sets<MotionMatchState>().Adds<SteerModel>()
        .Adds<FighterStartPose>().NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        for (int i = 0; i < added.Length; i++)
        {
            var e = added[i].Entity;
            // Боец построен дважды за срабатывание (сборка мира и сброс в тике 0) — одна команда, по последней записи.
            if (!world.IsAlive(e) || !world.Has<PlayerTag>(e) || Later(added, i)) continue;
            if (world.TryGet<MotionMatchState>(e, out var old))
            {
                old.Restart = true;
                ctx.Commands.Set(e, old);
                continue;
            }
            ctx.Commands.Add(e, MotionMatchState.Fresh);
            if (!world.Has<SteerModel>(e)) ctx.Commands.Add(e, new SteerModel());
            if (!world.Has<FighterStartPose>(e)) ctx.Commands.Add(e, runtime.StartPose);
        }
    }

    private static bool Later(ReadOnlySpan<Added<Fighter>> added, int i)
    {
        for (int j = i + 1; j < added.Length; j++)
            if (added[j].Entity == added[i].Entity) return true;
        return false;
    }

    /// <summary>
    /// Сборка мира (до <c>OnWorldReady</c>, где ядро ставит тела по позе постройки): игроку — состояние MM, модель контроллера
    /// и поза постройки — кадр <see cref="MotionMatchRuntime.StartFrame"/>, чтобы капсулы в первом тике не прыгали.
    /// </summary>
    public static void PlaceWorldBuilt(World world, MotionMatchRuntime runtime)
    {
        var players = new List<Entity>();
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().With<PlayerTag>()))
            players.AddRange(chunk.Entities);
        foreach (var e in players)
        {
            if (!world.Has<MotionMatchState>(e)) world.AddImmediate(e, MotionMatchState.Fresh);
            if (!world.Has<SteerModel>(e)) world.AddImmediate(e, new SteerModel());
            if (world.Has<FighterStartPose>(e)) world.SetImmediate(e, runtime.StartPose);
            else world.AddImmediate(e, runtime.StartPose);
        }
    }
}

/// <summary>
/// Animation, после <c>core.action-snapshot</c> и до <c>core.animator-pose</c>: шаг MM — одно задание пулу поз
/// (<see cref="PosePool.Motion"/>) прямо в <see cref="PoseRef"/> бойца; мёртвого не трогает. Шаг: темп, поиск (по расписанию,
/// сразу — при скачке цели и после действия всем телом), переход по порогу (<see cref="Switches"/>), продвижение на dt × темп.
/// Запрос: «сейчас» — корень в конце тика по модели контроллера, будущее — <see cref="SteerModel"/>, прошлое — история корня.
/// Память не выделяет.
/// </summary>
public sealed class MotionMatchSystem(MotionMatchRuntime runtime) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(MotionMatchSystems.Step).After(GameLabels.ActionSnapshot).Before(GameLabels.AnimatorPose).Reads<Transform>().Reads<RootVelocity>()
            .Reads<SteerModel>().Reads<AnimatorMotion>().Reads<PoseRef>().Writes<MotionMatchState>();
        _query = s.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<Transform>().Read<RootVelocity>().Read<SteerModel>().Read<AnimatorMotion>()
            .Read<PoseRef>().Write<MotionMatchState>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        var pool = world.Resource<PosePool>();
        var rules = world.Resource<LocomotionRules>();
        rules.Weights(runtime.Weights);
        runtime.Diagnostics.Attach(world.Profiler);   // зона locomotion.search
        int period = rules.SearchTicks(time.Dt);
        foreach (var chunk in ctx.Iterate(_query))
        {
            var state = chunk.Write<MotionMatchState>();
            var t = chunk.Read<Transform>()[..state.Length];
            var v = chunk.Read<RootVelocity>()[..state.Length];
            var steer = chunk.Read<SteerModel>()[..state.Length];
            var motion = chunk.Read<AnimatorMotion>()[..state.Length];
            var pose = chunk.Read<PoseRef>()[..state.Length];
            for (int i = 0; i < state.Length; i++)
            {
                var handle = new PoseHandle(pose[i].Index, pose[i].Generation);
                if (!pool.IsAlive(handle)) continue;   // позу ещё не завели (её ловит animation.owners)
                Tick(ref state[i], t[i], v[i].Value, steer[i], motion[i], handle, pool, rules, period, time);
            }
        }
    }

    /// <summary>Шаг бойца (тело цикла — свой метод: правило OSR).</summary>
    private void Tick(ref MotionMatchState s, in Transform t, Vector3 velocity, in SteerModel steer, in AnimatorMotion motion, PoseHandle pose,
        PosePool pool, in LocomotionRules rules, int period, in SimTime time)
    {
        var players = runtime.Players;
        float dt = time.Dt;
        if (s.Restart || !players.IsAlive(s.Player))
        {
            Restart(ref s, t.Facing, motion, pose, pool, rules, period, dt);
            return;
        }
        var db = runtime.Database;
        var turn = runtime.Turn;
        int current = runtime.CurrentFrame(s.Player);
        s.Rate = Rate(s.Rate, velocity, current >= 0 ? db.RootVelocity[current] : Vector3.Zero, rules.RateRange, dt);
        float goal = new Vector2(steer.GoalVelocity.X, steer.GoalVelocity.Z).Length();
        if (goal > GaspMotion.StandSpeed) s.ControlSpeed = goal;
        bool standing = goal < GaspMotion.StandSpeed && new Vector2(velocity.X, velocity.Z).Length() < GaspMotion.StandSpeed && motion.Search;
        var history = runtime.History(s.Player);
        float lastFacing = history.Count > 0 ? history[history.Count - 1].Root.Yaw : t.Facing;
        var output = players.State(s.Player);
        float chest = standing && output.HasOutput ? turn.ChestFix(players.OutputRotations(s.Player), players.OutputPositions(s.Player)) : 0;
        int transition = turn.Step(ref s, t.Facing, lastFacing, standing, current, output.RootStep.Yaw, chest, rules, dt);
        if (transition >= 0) runtime.Diagnostics.Turned(s.Player, (long)time.Tick, current, transition);
        if (s.SinceSearch < int.MaxValue) s.SinceSearch++;
        // Клип поворота на месте, пока его корень поворачивается, поиск не перебивает.
        if (transition < 0 && !turn.Turning(s, current) && motion.Search
            && (s.SinceSearch >= period || !s.SearchWas || GoalJumped(s.SearchGoal, steer.GoalVelocity)))
            transition = Search(ref s, t, velocity, steer, current, rules, time, standing, pool, pose);
        s.SearchWas = motion.Search;
        pool.Motion(pose, players, s.Player, new MotionStep(dt * s.Rate, dt, transition, rules.InertialHalflife));
        // Скручивание — после шага проигрывателя (он пишет позу целиком) и до руки с мечом (она — в системе груди).
        if (s.Twist != 0 || s.Chest != 0) pool.Twist(pose, turn.Twist, -s.Twist, s.Chest);
    }

    /// <summary>
    /// Постройка (заново): проигрыватель заново (тот же слот, поколение +1), история пуста; кадр позы постройки без
    /// инерциализации. Поиск — в следующем тике.
    /// </summary>
    private void Restart(ref MotionMatchState s, float facing, in AnimatorMotion motion, PoseHandle pose, PosePool pool, in LocomotionRules rules,
        int period, float dt)
    {
        var players = runtime.Players;
        if (players.IsAlive(s.Player)) players.Free(s.Player);
        s = MotionMatchState.Fresh;
        s.Restart = false;
        s.FeetYaw = facing;
        s.Player = players.Alloc();
        s.SinceSearch = period;
        s.SearchWas = motion.Search;
        runtime.History(s.Player).Clear();
        pool.Motion(pose, players, s.Player, new MotionStep(0, dt, runtime.StartFrame, rules.InertialHalflife));
    }

    /// <summary>
    /// Поиск: лучший кадр по запросу; переход (кадр), если сменяет текущий по <see cref="Switches"/>, иначе −1. Стоя клип
    /// поворота на месте поиск не выбирает — его ставит поворот на месте. Первый поиск после действия всем телом (передача) —
    /// по суставам видимой позы, а не кадра проигрывателя: под действием его кадр устарел и проигрывает видимой позе по цене.
    /// </summary>
    private int Search(ref MotionMatchState s, in Transform t, Vector3 velocity, in SteerModel steer, int current, in LocomotionRules rules,
        in SimTime time, bool standing, PosePool pool, PoseHandle pose)
    {
        bool handoff = !s.SearchWas;
        float dt = time.Dt;
        velocity.Y = 0;
        // «Сейчас» — конец тика по модели: курс тика уже повёрнут (core.root-steer), корень переносит core.root-control после Animation.
        var root = new Pose2(t.Position.X + velocity.X * dt, t.Position.Z + velocity.Z * dt, t.Facing);
        var model = new TrajectoryModel(steer.Rate, steer.TurnRate, steer.GoalVelocity, steer.GoalYaw);
        float kv = runtime.DataScale(velocity, t.Facing, s.ControlSpeed);
        float kg = runtime.DataScale(steer.GoalVelocity, steer.GoalYaw, s.ControlSpeed);
        var query = runtime.Query;
        var diagnostics = runtime.Diagnostics;   // повод, зона профайлера, последний поиск для вида и событий
        var reason = MotionMatchDiagnostics.Reason(s, steer.GoalVelocity);
        diagnostics.Begin();
        var history = runtime.History(s.Player);
        var visible = handoff ? runtime.VisibleJoints(pool, pose, history, velocity) : default;
        query.Fill(new TrajectoryState(root, velocity), model, history, (long)time.Tick, current, kv, kg, query.DataYawRate, visible);
        var match = runtime.Matcher.Search(query.Values, runtime.Weights, current, 0, runtime.SkipFrames);
        double microseconds = diagnostics.End();
        s.Searches++;
        s.SinceSearch = 0;
        s.SearchGoal = steer.GoalVelocity;
        s.Match = Finite(match);
        bool switches = Switches(match, current, current >= 0 && runtime.Database.Searchable[current], rules.SwitchGain)
            && !(standing && runtime.Turn.IsTurnFrame(match.Frame));
        diagnostics.Searched(s.Player, (long)time.Tick, reason, root, current, s.Match, switches, microseconds, query.Raw);
        if (!switches) return -1;
        s.Switches++;
        return match.Frame;
    }

    /// <summary>
    /// Темп: скорость корня / скорость корня клипа в 1 ± <paramref name="range"/> (хоть одна медленнее
    /// <see cref="GaspMotion.StandSpeed"/> — 1), к нему — экспонентой с полупериодом <see cref="GaspMotion.RateHalflife"/>.
    /// </summary>
    public static float Rate(float rate, Vector3 velocity, Vector3 clipVelocity, float range, float dt)
    {
        float speed = new Vector2(velocity.X, velocity.Z).Length(), clip = new Vector2(clipVelocity.X, clipVelocity.Z).Length();
        float target = speed > GaspMotion.StandSpeed && clip > GaspMotion.StandSpeed ? Math.Clamp(speed / clip, 1 - range, 1 + range) : 1;
        if (GaspMotion.RateHalflife > 0 && float.IsFinite(rate))
            target = rate + (target - rate) * (1 - MathF.Exp(-dt * 0.6931472f / GaspMotion.RateHalflife));
        return Math.Clamp(target, 1 - range, 1 + range);
    }

    /// <summary>
    /// Смена кадра: найден не текущий и текущий не искомый (нет полного будущего) или найденный дешевле продолжения больше
    /// чем на <paramref name="gain"/>.
    /// </summary>
    public static bool Switches(in MotionMatch match, int current, bool currentSearchable, float gain) =>
        match.Frame >= 0 && match.Frame != current && (!currentSearchable || match.Cost < match.ContinuationCost - gain);

    /// <summary>Скачок цели: <c>|цель − цель прошлого поиска| &gt; <see cref="GaspMotion.GoalJump"/>·max(1, |цель|)</c>.</summary>
    public static bool GoalJumped(Vector3 before, Vector3 goal)
    {
        var d = new Vector2(goal.X - before.X, goal.Z - before.Z);
        float limit = GaspMotion.GoalJump * MathF.Max(1, new Vector2(goal.X, goal.Z).Length());
        return d.LengthSquared() > limit * limit;
    }

    /// <summary>Итог поиска для компонента: бесконечные цены (нет продолжения, не нашлось) — −1 (<c>ecs.finite</c>).</summary>
    private static MotionMatch Finite(in MotionMatch m) => m with
    {
        Cost = float.IsFinite(m.Cost) ? m.Cost : -1,
        ContinuationCost = float.IsFinite(m.ContinuationCost) ? m.ContinuationCost : -1,
    };
}

/// <summary>
/// Movement, после <c>core.root-collision</c>: окончательный корень тика живого бойца (с рывком, без отдачи) — в историю
/// корня под номером тика; затем вид состояния после шага.
/// </summary>
public sealed class MotionMatchHistorySystem(MotionMatchRuntime runtime) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(MotionMatchSystems.History).After(GameLabels.RootCollision).Reads<Transform>().Writes<MotionMatchState>();
        _query = s.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<Transform>().Write<MotionMatchState>();
    }

    public void Run(in SystemContext ctx)
    {
        long tick = (long)ctx.World.Resource<SimTime>().Tick;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var state = chunk.Write<MotionMatchState>();
            var t = chunk.Read<Transform>()[..state.Length];
            for (int i = 0; i < state.Length; i++) Record(ref state[i], t[i], tick);
        }
    }

    private void Record(ref MotionMatchState s, in Transform t, long tick)
    {
        var players = runtime.Players;
        if (s.Restart || !players.IsAlive(s.Player)) return;
        runtime.History(s.Player).Add(tick, new Pose2(t.Position.X, t.Position.Z, t.Facing));
        var db = runtime.Database;
        var st = players.State(s.Player);
        s.Frame = st.Frame;
        s.Alpha = st.Alpha;
        s.Transitions = st.Transitions;
        s.Clip = st.Frame >= 0 ? db.Clips[db.ClipOf[st.Frame]].LibraryClip : -1;
        s.ClipFrame = st.Frame >= 0 ? db.FrameInClip(st.Frame) : -1;
    }
}

/// <summary>End (<c>locomotion.mm-release</c>): снятое состояние MM (боец удалён или компонент снят) возвращает проигрыватель.</summary>
public sealed class MotionMatchReleaseSystem(MotionMatchRuntime runtime) : IReactiveSystem<Removed<MotionMatchState>>
{
    public void Setup(SystemSetup s) => s.Label(MotionMatchSystems.Release);

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<MotionMatchState>> removed)
    {
        foreach (var r in removed)
            if (runtime.Players.IsAlive(r.Value.Player)) runtime.Players.Free(r.Value.Player);
    }
}
