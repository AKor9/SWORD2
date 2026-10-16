using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Animation;

/// <summary>Константы аниматора бойца.</summary>
public static class AnimatorRules
{
    /// <summary>Гистерезис «идёт»: встал ниже 0,25 м/с, пошёл выше 0,5 м/с (<c>IdleEnter</c>, <c>IdleExit</c>).</summary>
    public const float IdleEnter = 0.25f, IdleExit = 0.5f;

    /// <summary>Полупериод сглаживания скорости для локомоции, с (<c>VelHalflife</c>).</summary>
    public const float VelHalflife = 0.12f;

    /// <summary>Мировое направление → система бойца (x — вправо, z — вперёд).</summary>
    public static Vector3 DirToLocal(Vector3 dir, float facing)
    {
        var v = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -facing));
        return new Vector3(-v.X, v.Y, v.Z);
    }

    /// <summary>
    /// Слой движения аниматора на тик: сглаженная скорость и «идёт» с гистерезисом; поиск кадра локомоции разрешён
    /// (<paramref name="search"/>), пока действие не ведёт всё тело.
    /// </summary>
    public static void Step(ref AnimatorMotion m, Vector3 localVel, float dt, bool search = true)
    {
        m.Velocity += (new Vector3(localVel.X, 0, localVel.Z) - m.Velocity) * (1 - MathF.Exp(-dt * 0.693f / VelHalflife));
        float speed = new Vector2(m.Velocity.X, m.Velocity.Z).Length();
        m.Moving = m.Moving ? speed > IdleEnter : speed > IdleExit;
        m.Search = search;
    }
}

/// <summary>
/// Gameplay, после курса и разгона: аниматор бойца на тик без поз — вход слоя локомоции (<see cref="AnimatorMotion"/> по новому
/// курсу), затем шаг действия (<see cref="ActionLayer.Tick"/>) и время слотов вздрагивания игрока. Мёртвый замирает.
/// </summary>
public sealed class AnimatorInputSystem(StrikeTable strikes) : IContinuousSystem
{
    private Query _plain = null!, _agile = null!, _steady = null!, _flinch = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.AnimatorInput).After(GameLabels.RootSteer).Reads<Transform>().Reads<RootVelocity>().Writes<AnimatorMotion>()
            .Writes<AnimatorAction>().Writes<Flinch>();
        _plain = s.Query().With<Fighter>().Without<Dead>().Without<AnimatorAction>().Read<Transform>().Read<RootVelocity>().Write<AnimatorMotion>();
        var acting = s.Query().With<Fighter>().Without<Dead>().Read<Transform>().Read<RootVelocity>().Write<AnimatorMotion>().Write<AnimatorAction>();
        _agile = acting.With<PlayerTag>();
        _steady = acting.Without<PlayerTag>();
        _flinch = s.Query().With<Fighter>().Without<Dead>().Write<Flinch>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_plain))
        {
            var m = chunk.Write<AnimatorMotion>();
            var t = chunk.Read<Transform>()[..m.Length];
            var v = chunk.Read<RootVelocity>()[..m.Length];
            for (int i = 0; i < m.Length; i++) AnimatorRules.Step(ref m[i], AnimatorRules.DirToLocal(v[i].Value, t[i].Facing), dt);
        }
        var clips = ctx.World.Resource<PosePool>().Clips;
        foreach (var chunk in ctx.Iterate(_agile)) Act(chunk, dt, clips, true);
        foreach (var chunk in ctx.Iterate(_steady)) Act(chunk, dt, clips, false);
        foreach (var chunk in ctx.Iterate(_flinch))
            foreach (ref var f in chunk.Write<Flinch>()) FlinchLayer.Tick(ref f, dt);
    }

    private void Act(ChunkView chunk, float dt, ClipLibrary clips, bool agile)
    {
        var m = chunk.Write<AnimatorMotion>();
        var t = chunk.Read<Transform>()[..m.Length];
        var v = chunk.Read<RootVelocity>()[..m.Length];
        var a = chunk.Write<AnimatorAction>()[..m.Length];
        var attacks = strikes.Attacks;
        for (int i = 0; i < m.Length; i++)
        {
            // локомоция ищет кадр, пока действие не ведёт всё тело (FullBodyAction до шага действия)
            AnimatorRules.Step(ref m[i], AnimatorRules.DirToLocal(v[i].Value, t[i].Facing), dt, !a[i].FullBody);
            float speed = new Vector2(m[i].Velocity.X, m[i].Velocity.Z).Length();   // сглаженная скорость для слоя верха
            ActionLayer.Tick(ref a[i], m[i].Moving, speed, dt, clips, attacks, agile);
        }
    }
}

/// <summary>
/// Animation: итоговая поза бойца — задания слоя действий поверх локомоции (<see cref="ActionLayer.Pose"/>), у игрока затем
/// вздрагивание (<see cref="FlinchLayer.Pose"/>), у всех — прямая кинематика. Слой локомоции и снимок ставят задания раньше.
/// Мёртвому заданий нет. <paramref name="full"/> — маска всего тела для перехода от снимка.
/// </summary>
public sealed class AnimatorPoseSystem(PoseMask upper, PoseMask full) : IContinuousSystem
{
    private Query _plain = null!, _acting = null!, _flinching = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.AnimatorPose).Reads<PoseRef>().Reads<AnimatorAction>().Reads<Flinch>().Writes<ActionPoses>();
        _plain = s.Query().With<Fighter>().Without<Dead>().Without<AnimatorAction>().Read<PoseRef>();
        var acting = s.Query().With<Fighter>().Without<Dead>().Read<PoseRef>().Read<AnimatorAction>().Write<ActionPoses>();
        _acting = acting.Without<Flinch>();
        _flinching = acting.Read<Flinch>();
    }

    public void Run(in SystemContext ctx)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var chunk in ctx.Iterate(_plain))
            foreach (var p in chunk.Read<PoseRef>())
                if (p.Generation != 0) pool.ToModel(new PoseHandle(p.Index, p.Generation));   // пустой — позу ещё не завели
        foreach (var chunk in ctx.Iterate(_acting))
        {
            var poses = chunk.Write<ActionPoses>();
            var refs = chunk.Read<PoseRef>()[..poses.Length];
            var a = chunk.Read<AnimatorAction>()[..poses.Length];
            for (int i = 0; i < poses.Length; i++)
            {
                if (refs[i].Generation == 0) continue;
                var pose = new PoseHandle(refs[i].Index, refs[i].Generation);
                ActionLayer.Pose(a[i], ref poses[i], pose, pool, upper, full);
                pool.ToModel(pose);
            }
        }
        foreach (var chunk in ctx.Iterate(_flinching))
        {
            var poses = chunk.Write<ActionPoses>();
            var refs = chunk.Read<PoseRef>()[..poses.Length];
            var a = chunk.Read<AnimatorAction>()[..poses.Length];
            var f = chunk.Read<Flinch>()[..poses.Length];
            for (int i = 0; i < poses.Length; i++)
            {
                if (refs[i].Generation == 0) continue;
                var pose = new PoseHandle(refs[i].Index, refs[i].Generation);
                ActionLayer.Pose(a[i], ref poses[i], pose, pool, upper, full);
                FlinchLayer.Pose(f[i], ref poses[i], pose, pool, upper);
                pool.ToModel(pose);
            }
        }
    }
}

/// <summary>Animation, последней: пул поз исполняет задания тика пачкой (<see cref="PosePool.Tick"/>).</summary>
public sealed class AnimationTickSystem : IContinuousSystem
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.AnimationTick).After(GameLabels.AnimatorPose);

    public void Run(in SystemContext ctx) => ctx.World.Resource<PosePool>().Tick();
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>: бойцу, построенному командами тика, — поза в пуле, взятая здесь, когда он
/// уже в мире (поза, взятая командой, не вернулась бы, если бы команда не сыграла). У бойца со своей живой позой — ничего.
/// </summary>
public sealed class FighterSetupSystem : IReactiveSystem<Added<Fighter>>
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        // Боец, добавленный после Gameplay (сейчас так никто не делает: команды арены — в Intent), получит позу в
        // следующем тике; до того у него пустой хэндл, и итоговую позу ему не считают (AnimatorPoseSystem).
        s.Label(GameLabels.FighterSetup).Writes<PoseRef>().NextTickOk<Added<Fighter>>();
        _query = s.Query().With<Fighter>().Write<PoseRef>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var chunk in ctx.Iterate(_query))
            foreach (ref var p in chunk.Write<PoseRef>())
            {
                // Только пустой хэндл: испорченный (не живой) не чинится молча — его ловит animation.owners.
                if (!new PoseHandle(p.Index, p.Generation).IsNone) continue;
                var pose = pool.Alloc();
                p = new PoseRef { Index = pose.Index, Generation = pose.Generation };
            }
    }
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
