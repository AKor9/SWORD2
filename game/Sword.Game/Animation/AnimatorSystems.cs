using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Animation;

/// <summary>Константы аниматора бойца — <c>FighterAnimator</c> SWORD 2eab13b.</summary>
public static class AnimatorRules
{
    /// <summary>Гистерезис «идёт»: встал ниже 0,25 м/с, пошёл выше 0,5 м/с (<c>IdleEnter</c>, <c>IdleExit</c>).</summary>
    public const float IdleEnter = 0.25f, IdleExit = 0.5f;

    /// <summary>Полупериод сглаживания скорости для локомоции, с (<c>VelHalflife</c>).</summary>
    public const float VelHalflife = 0.12f;

    /// <summary>Мировое направление → система бойца (x — вправо, z — вперёд): <c>Fighter.DirToLocal</c> SWORD.</summary>
    public static Vector3 DirToLocal(Vector3 dir, float facing)
    {
        var v = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -facing));
        return new Vector3(-v.X, v.Y, v.Z);
    }

    /// <summary>
    /// Слой движения аниматора на тик (начало <c>FighterAnimator.Tick</c>): сглаженная скорость и «идёт» с гистерезисом;
    /// поиск кадра локомоции разрешён, пока действие не ведёт всё тело (действий ещё нет — всегда).
    /// </summary>
    public static void Step(ref AnimatorMotion m, Vector3 localVel, float dt)
    {
        m.Velocity += (new Vector3(localVel.X, 0, localVel.Z) - m.Velocity) * (1 - MathF.Exp(-dt * 0.693f / VelHalflife));
        float speed = new Vector2(m.Velocity.X, m.Velocity.Z).Length();
        m.Moving = m.Moving ? speed > IdleEnter : speed > IdleExit;
        m.Search = true;
    }
}

/// <summary>
/// Gameplay, после курса и разгона: вход слоя локомоции — скорость корня в системе бойца (по новому курсу), сглаженная,
/// и «идёт» (<see cref="AnimatorMotion"/>). В SWORD — первая часть <c>FighterAnimator.Tick(dt, f.DirToLocal(f.RootVel))</c>.
/// </summary>
public sealed class AnimatorInputSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.AnimatorInput).After(GameLabels.RootSteer).Reads<Transform>().Reads<RootVelocity>().Writes<AnimatorMotion>();
        _query = s.Query().With<Fighter>().Read<Transform>().Read<RootVelocity>().Write<AnimatorMotion>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var m = chunk.Write<AnimatorMotion>();
            var t = chunk.Read<Transform>()[..m.Length];
            var v = chunk.Read<RootVelocity>()[..m.Length];
            for (int i = 0; i < m.Length; i++) AnimatorRules.Step(ref m[i], AnimatorRules.DirToLocal(v[i].Value, t[i].Facing), dt);
        }
    }
}

/// <summary>
/// Animation: итоговая поза бойца — задание прямой кинематики его позы (<c>clips.ToComponent</c> в конце
/// <c>FighterAnimator.TickActions</c>). Слои позы (локомоция модуля Locomotion, позже действия) ставят свои задания раньше
/// (<see cref="GameLabels.AnimatorPose"/>); без них поза остаётся позой привязки.
/// </summary>
public sealed class AnimatorPoseSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.AnimatorPose).Reads<PoseRef>();
        _query = s.Query().With<Fighter>().Read<PoseRef>();
    }

    public void Run(in SystemContext ctx)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var chunk in ctx.Iterate(_query))
            foreach (var p in chunk.Read<PoseRef>())
                pool.ToModel(new PoseHandle(p.Index, p.Generation));
    }
}

/// <summary>Animation, последней: пул поз исполняет задания тика пачкой (<see cref="PosePool.Tick"/>).</summary>
public sealed class AnimationTickSystem : IContinuousSystem
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.AnimationTick).After(GameLabels.AnimatorPose);

    public void Run(in SystemContext ctx) => ctx.World.Resource<PosePool>().Tick();
}

/// <summary>End: снятая поза (боец удалён или компонент снят) возвращается в пул.</summary>
public sealed class PoseReleaseSystem : IReactiveSystem<Removed<PoseRef>>
{
    public void Setup(SystemSetup s) => s.Label(new SystemLabel("core.pose-release"));

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<PoseRef>> removed)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var r in removed)
        {
            var h = new PoseHandle(r.Value.Index, r.Value.Generation);
            if (pool.IsAlive(h)) pool.Free(h);
        }
    }
}

/// <summary>Анимация для наблюдателей (<see cref="IAnimationView"/>): чтение пула и библиотеки без права их менять.</summary>
public sealed class AnimationView(PosePool pool) : IAnimationView
{
    public int JointCount => pool.JointCount;

    public string JointName(int joint) => pool.Skeleton.JointNames[joint];

    public int FindJoint(string name) => pool.Skeleton.Find(name);

    public ReadOnlySpan<Matrix4x4> Model(PoseRef pose)
    {
        var h = new PoseHandle(pose.Index, pose.Generation);
        return pool.IsAlive(h) ? pool.Model(h) : default;
    }

    public ReadOnlySpan<Matrix4x4> ReferenceModel() => pool.ReferenceModel;

    public int ClipCount => pool.Clips.Clips.Length;

    public string ClipName(int clip) => pool.Clips.Clips[clip].Name;

    public float ClipDuration(int clip) => pool.Clips.Duration(clip);

    public bool ClipLoops(int clip) => pool.Clips.Clips[clip].Loop;

    public float ClipRootSpeed(int clip) => pool.Clips.RootSpeed(clip);

    public int FindClip(string name) => pool.Clips.FindClip(name);

    public int PoseCount => pool.Count;

    public int LastTickJobs => pool.LastTick.Total;

    public int LastTickStale => pool.LastTick.Stale;
}
