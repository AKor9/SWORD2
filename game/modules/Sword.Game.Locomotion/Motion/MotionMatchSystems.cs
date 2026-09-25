using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Системы Motion Matching игрока (<c>locomotion.player = mm</c>): корень и курс ведёт контроллер ядра, MM выбирает и проигрывает позу.
/// <list type="table">
/// <item><term>сборка мира</term><description>игроку — состояние MM, модель контроллера (<see cref="SteerModel"/>) и своя поза постройки
/// (<see cref="FighterStartPose"/> — кадр стойки базы): ядро ставит его капсулы и клинок по ней (<see cref="MotionMatchSetupSystem.PlaceWorldBuilt"/>).</description></item>
/// <item><term>Gameplay</term><description><c>locomotion.mm-setup</c> — по <c>Added&lt;Fighter&gt;</c> игрока: состояние с нуля.</description></item>
/// <item><term>Animation</term><description><c>locomotion.mm</c> — после <c>core.action-snapshot</c>, до <c>core.animator-pose</c>: темп,
/// поиск по расписанию или сразу, задание пулу — шаг проигрывателя прямо в позу бойца (<see cref="PoseRef"/>).</description></item>
/// <item><term>Movement</term><description><c>locomotion.mm-history</c> — после <c>core.root-collision</c>: корень тика в историю, вид
/// состояния после шага.</description></item>
/// <item><term>End</term><description><c>locomotion.mm-release</c> — снятое состояние возвращает проигрыватель.</description></item>
/// </list>
/// </summary>
public static class MotionMatchSystems
{
    public static readonly SystemLabel Setup = new("locomotion.mm-setup"), Step = new("locomotion.mm"),
        History = new("locomotion.mm-history"), Release = new("locomotion.mm-release");
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c> игрока (<c>locomotion.mm-setup</c>): построенный (заново) игрок начинает Motion Matching
/// с нуля — флаг <see cref="MotionMatchState.Restart"/> (проигрыватель и историю заводит заново <c>locomotion.mm</c> этого же тика: хэндлы
/// берёт не реакция — иначе снятый на барьере появления боец терял бы их). Сброс — по событию постройки (R, команда сцены, пересборка трупа,
/// сборка мира), а не по «телепорту». Мёртвого реакция не касается: он оживает только постройкой. Игроку, созданному командой (сборка мира
/// ставит всё сразу; игрока никто не удаляет, поэтому командой он не создаётся), — состояние, модель контроллера и поза постройки командой:
/// его тела в тике создания встали бы ещё по общей позе постройки.
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
            ctx.Cause = added[i].Cause;   // причина постройки бойца — родитель состояния MM (state.why)
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
    /// Сборка мира (<c>OnWorldBuilt</c> модуля — после бойцов ядра, до <c>OnWorldReady</c>, где ядро ставит тела по позе постройки): игроку —
    /// состояние MM с нуля, модель контроллера (её пишет <c>core.root-steer</c> с первого тика) и своя поза постройки — кадр
    /// <see cref="MotionMatchRuntime.StartFrame"/>: первый шаг проигрывателя выводит ровно его, капсулы в первом тике не прыгают.
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
/// Animation, после <c>core.action-snapshot</c> (снимок позы прошлого тика — первым в очереди) и до <c>core.animator-pose</c> (слой действий
/// поверх): шаг Motion Matching бойца с <see cref="MotionMatchState"/> — одно задание пулу поз (<see cref="PosePool.Motion"/>) прямо в его
/// позу <see cref="PoseRef"/> (своих поз у MM нет: выход и скорости выхода держит проигрыватель). Мёртвого не трогает (<c>Without&lt;Dead&gt;</c>).
///
/// Шаг: построен (заново) — проигрыватель заново, история пуста, кадр позы постройки без инерциализации (выход — поза постройки); иначе —
/// темп (скорость корня / скорость корня клипа текущего кадра, 1 ± размах, сглаживание <see cref="GaspMotion.RateHalflife"/>), поиск (раз в
/// <c>round(период × частота)</c> тиков, сразу — при скачке цели и в первом тике после действия всем телом; под действием всем телом —
/// без поиска), переход — если нашёлся не текущий кадр дешевле продолжения больше чем на порог смены (<see cref="Switches"/>); задание:
/// продвижение на dt × темп, запрошенный переход.
///
/// Запрос: «сейчас» — корень в конце тика по модели контроллера (корень после прошлого тика + скорость тика·dt, курс тика: прибавка рывка,
/// сдвиг клипом действия и столкновения — вне модели), будущее — модель контроллера тика (<see cref="SteerModel"/> → <see cref="TrajectoryModel"/>),
/// прошлое — история корня после столкновений, масштаб данных — <see cref="MotionMatchRuntime.DataScale"/>. Текущий кадр — ближний к
/// проигрывателю, кадр выхода — его вход. Память не выделяет.
/// </summary>
public sealed class MotionMatchSystem(MotionMatchRuntime runtime) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(MotionMatchSystems.Step).After(GameLabels.ActionSnapshot).Before(GameLabels.AnimatorPose).Reads<Transform>().Reads<RootVelocity>()
            .Reads<SteerModel>().Reads<AnimatorMotion>().Reads<PoseRef>().Writes<MotionMatchState>();
        _query = s.Query().With<Fighter>().Without<Dead>().Read<Transform>().Read<RootVelocity>().Read<SteerModel>().Read<AnimatorMotion>()
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
            Restart(ref s, motion, pose, pool, rules, period, dt);
            return;
        }
        var db = runtime.Database;
        int current = runtime.CurrentFrame(s.Player);
        s.Rate = Rate(s.Rate, velocity, current >= 0 ? db.RootVelocity[current] : Vector3.Zero, rules.RateRange, dt);
        float goal = new Vector2(steer.GoalVelocity.X, steer.GoalVelocity.Z).Length();
        if (goal > GaspMotion.StandSpeed) s.ControlSpeed = goal;
        int transition = -1;
        if (s.SinceSearch < int.MaxValue) s.SinceSearch++;
        if (motion.Search && (s.SinceSearch >= period || !s.SearchWas || GoalJumped(s.SearchGoal, steer.GoalVelocity)))
            transition = Search(ref s, t, velocity, steer, current, rules, time);
        s.SearchWas = motion.Search;
        pool.Motion(pose, players, s.Player, new MotionStep(dt * s.Rate, dt, transition, rules.InertialHalflife));
    }

    /// <summary>
    /// Постройка (заново): проигрыватель заново (тот же слот — поколение +1: свободный выдаётся первым), история пуста, счётчики с нуля; кадр
    /// позы постройки без инерциализации — выход тика ровно поза постройки. Поиск — в следующем тике (счётчик «просрочен»).
    /// </summary>
    private void Restart(ref MotionMatchState s, in AnimatorMotion motion, PoseHandle pose, PosePool pool, in LocomotionRules rules, int period, float dt)
    {
        var players = runtime.Players;
        if (players.IsAlive(s.Player)) players.Free(s.Player);
        s = MotionMatchState.Fresh;
        s.Restart = false;
        s.Player = players.Alloc();
        s.SinceSearch = period;
        s.SearchWas = motion.Search;
        runtime.History(s.Player).Clear();
        pool.Motion(pose, players, s.Player, new MotionStep(0, dt, runtime.StartFrame, rules.InertialHalflife));
    }

    /// <summary>
    /// Поиск: запрос по модели контроллера и истории, лучший кадр (без скидки продолжению); сменяет текущий по порогу смены
    /// (<see cref="Switches"/>) — переход (кадр), иначе −1.
    /// </summary>
    private int Search(ref MotionMatchState s, in Transform t, Vector3 velocity, in SteerModel steer, int current, in LocomotionRules rules,
        in SimTime time)
    {
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
        query.Fill(new TrajectoryState(root, velocity), model, runtime.History(s.Player), (long)time.Tick, current, kv, kg, query.DataYawRate);
        var match = runtime.Matcher.Search(query.Values, runtime.Weights, current, 0, runtime.SkipFrames);
        double microseconds = diagnostics.End();
        s.Searches++;
        s.SinceSearch = 0;
        s.SearchGoal = steer.GoalVelocity;
        s.Match = Finite(match);
        bool switches = Switches(match, current, current >= 0 && runtime.Database.Searchable[current], rules.SwitchGain);
        diagnostics.Searched(s.Player, (long)time.Tick, reason, root, current, s.Match, switches, microseconds, query.Raw);
        if (!switches) return -1;
        s.Switches++;
        return match.Frame;
    }

    /// <summary>
    /// Темп: скорость корня / скорость корня клипа в текущем кадре в 1 ± <paramref name="range"/> (хоть одна медленнее
    /// <see cref="GaspMotion.StandSpeed"/> — 1), к нему — экспонентой с полупериодом <see cref="GaspMotion.RateHalflife"/>, итог — в тех же
    /// пределах (ручка размаха могла сузиться).
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
    /// Смена кадра (один механизм — порог смены): нашёлся кадр, не текущий, и текущий не искомый (у него нет полного будущего — поиск
    /// уводит из него) или найденный дешевле продолжения больше чем на <paramref name="gain"/>. Ничья и выигрыш не больше порога — остаться.
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
/// Movement, после <c>core.root-collision</c> (<c>locomotion.mm-history</c>): корень живого бойца на MM после столкновений — окончательный за
/// тик (отдача меняет только скорость следующего тика) — в историю корня под номером тика; прибавка рывка в нём есть (положение), отдачи —
/// нет (она в <see cref="RootVelocity"/> следующего тика). Затем вид состояния после шага (кадр, клип, доля, переходы — пул исполнил задания
/// в <c>core.animation-tick</c>).
/// </summary>
public sealed class MotionMatchHistorySystem(MotionMatchRuntime runtime) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(MotionMatchSystems.History).After(GameLabels.RootCollision).Reads<Transform>().Writes<MotionMatchState>();
        _query = s.Query().With<Fighter>().Without<Dead>().Read<Transform>().Write<MotionMatchState>();
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
