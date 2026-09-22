using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Locomotion;

/// <summary>
/// Состояние локомоции клипами у бойца (<c>LegacyLocomotion</c> SWORD 2eab13b): играющий клип, сторона, бег, время
/// клипа, доля кроссфейда, сколько клип держится; слой локомоции и снимок, от которого идёт кроссфейд, — позы в пуле.
/// </summary>
public struct LegacyLocomotionState
{
    /// <summary>Играющий клип (номер в библиотеке); −1 — ещё не выбран.</summary>
    public int Clip;

    /// <summary>Сторона ходьбы и бега: 0 — вперёд, по часовой стрелке через 45° (<see cref="ActionSlots.Dirs"/>).</summary>
    public int Dir;
    public bool Run;
    public float Time, Fade, Hold;

    /// <summary>
    /// Поза слоя локомоции (итог прошлого тика — снимок при смене клипа) и снимок кроссфейда. Берутся из пула на первом
    /// тике (<see cref="LegacyLocomotion.Tick"/>), когда состояние уже в мире: снятое состояние всегда их вернёт.
    /// </summary>
    public PoseHandle Layer, From;
}

/// <summary>
/// Локомоция клипами — <c>LegacyLocomotion</c> SWORD 2eab13b без изменений логики: стойка, ходьба и бег в 8 сторон по
/// сглаженной скорости с гистерезисом походки (бег выше 1,9 м/с, шаг ниже 1,4 м/с) и направления (±0,2 сектора); клип
/// меняется не чаще <see cref="MinHold"/>, кроссфейдом за <see cref="LocoFade"/> с сохранением фазы цикла; темп — по
/// скорости корневого движения клипа (0,5…2,2), чтобы стопы не скользили. Позу не считает сам — ставит задания пулу поз.
/// </summary>
public static class LegacyLocomotion
{
    public const float LocoFade = 0.25f, MinHold = 0.35f, RunEnter = 1.9f, RunExit = 1.4f, DirHysteresis = 0.2f;

    /// <summary>
    /// Новое состояние — как после <c>FighterAnimator.Reset</c> SWORD (<c>Tick(0)</c> стоя): стойка с нулевого момента,
    /// без кроссфейда. Поз ещё нет — их возьмёт первый <see cref="Tick"/>.
    /// </summary>
    public static LegacyLocomotionState Start(ActionTable actions) => new()
    {
        Clip = actions.Idle, Dir = 0, Run = false, Time = 0, Fade = 1, Hold = 0, Layer = PoseHandle.None, From = PoseHandle.None,
    };

    /// <summary>
    /// Выбор клипа на тик (<c>LegacyLocomotion.Tick</c>). Возвращает, нужен ли снимок кроссфейда: смена клипа — снимок
    /// берётся с итога прошлого тика (слой ещё его держит), это и есть поза, которую SWORD пересэмплирует при смене.
    /// </summary>
    public static bool Choose(ref LegacyLocomotionState s, in AnimatorMotion input, float dt, ClipLibrary clips, ActionTable actions)
    {
        var vel = input.Velocity;
        float speed = new Vector2(vel.X, vel.Z).Length();
        s.Run = s.Run ? speed > RunExit : speed > RunEnter;
        if (input.Moving)
        {
            float angle = MathF.Atan2(vel.X, vel.Z) / (MathF.PI / 4);
            float rel = angle - s.Dir;
            rel -= 8 * MathF.Round(rel / 8);
            if (MathF.Abs(rel) > 0.5f + DirHysteresis) s.Dir = ((int)MathF.Round(angle) % 8 + 8) % 8;
        }
        int clip = !input.Moving ? actions.Idle : s.Run ? actions.Run(s.Dir) : actions.Walk(s.Dir);
        // темп — по реальной скорости клипа (корневое движение), иначе стопы скользят
        float clipSpeed = clips.RootSpeed(clip);
        float rate = !input.Moving || clipSpeed < 0.2f ? 1 : Math.Clamp(speed / clipSpeed, 0.5f, 2.2f);
        s.Hold += dt;
        bool snapshot = false;
        if (clip != s.Clip && (s.Clip < 0 || s.Hold >= MinHold))
        {
            if (s.Clip >= 0)
            {
                snapshot = true;
                s.Fade = 0;
            }
            float phase = s.Clip >= 0 && clips.Clips[s.Clip].Loop ? s.Time / MathF.Max(clips.Duration(s.Clip), 1e-3f) : 0;
            s.Clip = clip;
            s.Time = clips.Clips[clip].Loop ? (phase % 1) * clips.Duration(clip) : 0;
            s.Hold = 0;
        }
        s.Time += dt * rate;
        s.Fade = MathF.Min(1, s.Fade + dt / LocoFade);
        return snapshot;
    }

    /// <summary>
    /// Тик бойца: выбор клипа и задания пулу — снимок (при смене клипа), сэмпл клипа в слой, кроссфейд от снимка
    /// (вес — сглаженная ступень 1 − доля), слой — в позу бойца. Первый тик берёт из пула позы слоя и снимка (в позе
    /// привязки — как раньше при заведении бойца: снимок до смены клипа не читается, смена — не раньше MinHold).
    /// </summary>
    public static void Tick(ref LegacyLocomotionState s, in AnimatorMotion input, PoseHandle pose, float dt, PosePool pool, ActionTable actions)
    {
        if (s.Layer.IsNone)
        {
            s.Layer = pool.Alloc();
            s.From = pool.Alloc();
        }
        if (Choose(ref s, input, dt, pool.Clips, actions)) pool.Copy(s.From, s.Layer);
        pool.Sample(s.Layer, s.Clip, s.Time);
        if (s.Fade < 1)
        {
            float k = 1 - s.Fade;
            pool.Blend(s.Layer, s.From, k * k * (3 - 2 * k));
        }
        pool.Copy(pose, s.Layer);
    }
}

/// <summary>
/// Gameplay: новому бойцу — состояние локомоции. Позы слоя и снимка здесь не берутся: компонент добавляет команда, и если
/// она не сыграет (боец снят на том же барьере, тик прерван), позы некому было бы вернуть.
/// </summary>
public sealed class LegacyLocomotionSetupSystem : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) => s.Label(new SystemLabel("locomotion.setup")).Adds<LegacyLocomotionState>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var actions = ctx.World.Resource<ActionTable>();
        foreach (var a in added)
        {
            if (!ctx.World.IsAlive(a.Entity) || ctx.World.Has<LegacyLocomotionState>(a.Entity)) continue;
            ctx.Commands.Add(a.Entity, LegacyLocomotion.Start(actions));
        }
    }
}

/// <summary>
/// Animation, до итоговой позы бойца: локомоция клипами всех бойцов (<see cref="LegacyLocomotion.Tick"/>) — задания
/// пулу поз; пул исполнит их в <c>core.animation-tick</c>.
/// </summary>
public sealed class LegacyLocomotionSystem : IContinuousSystem
{
    public static readonly SystemLabel Label = new("locomotion.legacy");

    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(Label).Before(GameLabels.AnimatorPose).Reads<AnimatorMotion>().Reads<PoseRef>().Writes<LegacyLocomotionState>();
        _query = s.Query().With<Fighter>().Read<AnimatorMotion>().Read<PoseRef>().Write<LegacyLocomotionState>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        var pool = ctx.World.Resource<PosePool>();
        var actions = ctx.World.Resource<ActionTable>();
        foreach (var chunk in ctx.Iterate(_query))
        {
            var state = chunk.Write<LegacyLocomotionState>();
            var motion = chunk.Read<AnimatorMotion>()[..state.Length];
            var pose = chunk.Read<PoseRef>()[..state.Length];
            for (int i = 0; i < state.Length; i++)
                LegacyLocomotion.Tick(ref state[i], motion[i], new PoseHandle(pose[i].Index, pose[i].Generation), dt, pool, actions);
        }
    }
}

/// <summary>End: снятое состояние локомоции возвращает свои позы в пул.</summary>
public sealed class LegacyLocomotionReleaseSystem : IReactiveSystem<Removed<LegacyLocomotionState>>
{
    public void Setup(SystemSetup s) => s.Label(new SystemLabel("locomotion.release"));

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<LegacyLocomotionState>> removed)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var r in removed)
        {
            if (pool.IsAlive(r.Value.Layer)) pool.Free(r.Value.Layer);
            if (pool.IsAlive(r.Value.From)) pool.Free(r.Value.From);
        }
    }
}

/// <summary>
/// Модуль Locomotion (раздел 10 плана), часть 1: локомоция клипами у всех бойцов. Без модуля ходьбы нет ни у кого:
/// корень движется, поза бойца — поза привязки. Часть 2 — Motion Matching игрока (этап 8).
/// </summary>
public sealed class LocomotionModule : IModule
{
    public string Id => "locomotion";

    public void Configure(SimulationBuilder b)
    {
        b.Component<LegacyLocomotionState>()
            .System(GameStages.Gameplay, new LegacyLocomotionSetupSystem())
            .System(GameStages.Animation, new LegacyLocomotionSystem())
            .System(GameStages.End, new LegacyLocomotionReleaseSystem());
    }
}
