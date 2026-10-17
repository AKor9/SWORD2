using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Data;
using Sword.Game.Movement;

namespace Sword.Game.Ragdoll;

/// <summary>Сбитый с ног: сколько лежит, когда встаёт.</summary>
public static class KnockdownRules
{
    /// <summary>Лежит не меньше, с; встаёт, пролежав спокойно столько, с; встаёт в любом случае через, с.</summary>
    public const float MinDown = 1.2f, StillTime = 0.35f, MaxDown = 5f;

    /// <summary>«Спокойно»: скорость таза, м/с, и его угловая скорость, рад/с, — не больше.</summary>
    public const float StillSpeed = 0.35f, StillSpin = 2f;
}

/// <summary>
/// Settle, реактивная по <c>Added&lt;Downed&gt;</c>: живой сбит с ног — привязка скина по позе тика (рисуется по телам), тела — на физике
/// как у трупа (<see cref="Corpse.Convert"/>), но меч остаётся в руке; удар, которым сбит, — телу части. Мёртвых и уже лежащих пропускает.
/// Памяти не выделяет.
/// </summary>
public sealed class KnockdownSystem(IPhysicsWorld physics, CorpseJoints joints, SkinBindings skins, int corpseProfile) : IReactiveSystem<Added<Downed>>
{
    private readonly JointDesc[] _descs = new JointDesc[CorpseJoints.Count];
    private readonly JointDesc[] _all = new JointDesc[Severing.MaxBodies];
    private readonly BodyPair[] _pairs = new BodyPair[Severing.MaxPairs];

    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.Knockdown).After(GameLabels.DeathToRagdoll).Reads<Downed>().Reads<Dead>().Reads<Transform>().Reads<PoseRef>()
            .Reads<BodyRef>().Reads<Severance>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Downed>> added)
    {
        var world = ctx.World;
        var pool = world.Resource<PosePool>();
        var rules = world.Resource<RagdollRules>();
        foreach (var a in added)
        {
            var e = a.Entity;
            if (!world.IsAlive(e) || world.Has<Dead>(e) || !world.TryGet<Downed>(e, out var downed) || !world.TryGet<BodyRef>(e, out var bodyRef)
                || !world.TryGet<PoseRef>(e, out var poseRef) || !world.TryGet<Transform>(e, out var root))
                continue;
            var body = FighterBodies.Handle(bodyRef);
            var pose = new PoseHandle(poseRef.Index, poseRef.Generation);
            if (!pool.IsAlive(pose) || !BodyPlacement.Placeable(physics, body) || Corpse.IsCorpse(physics, body)) continue;
            var fighterWorld = BodyPlacement.World(root.Position, root.Facing);
            skins.Capture(physics, e, body, pool.Model(pose), fighterWorld, downed.Time);
            bool severed = world.TryGet<Severance>(e, out var severance);
            if (severed)
                Corpse.Convert(physics, body, joints, _descs, pool.Model(pose), fighterWorld, corpseProfile, rules, severance, _all, _pairs);
            else Corpse.Convert(physics, body, joints, _descs, pool.Model(pose), fighterWorld, corpseProfile, rules);
            // меч — в руке: падая, боец его не бросает
            if (!severed || !severance.IsDetached(BodySegs.Weapon))
                for (int j = 0, n = physics.JointCount(body); j < n; j++)
                    if (physics.Joint(body, j).Child == BodySegs.Body(BodySegs.Weapon)) physics.SetJointEnabled(body, j, true);
            if (BodySegs.IsFlesh(downed.Part) && downed.Impulse != Vector3.Zero)
                Push(physics.Body(body, BodySegs.Body(downed.Part)), downed.Point, downed.Impulse);
        }
    }

    /// <summary>Удар телу части; скорость и вращение — не выше потолков профиля трупа.</summary>
    private void Push(BodyHandle b, Vector3 point, Vector3 impulse)
    {
        physics.ApplyHit(b, point, impulse, 0, ArticulationHandle.None);
        var d = physics.GetDynamics(corpseProfile);
        var s = physics.Get(b);
        float v = s.Velocity.Length(), w = s.AngularVelocity.Length();
        if (!(v > d.MaxSpeed) && !(w > d.MaxAngularSpeed)) return;
        if (v > d.MaxSpeed) s.Velocity *= d.MaxSpeed / v;
        if (w > d.MaxAngularSpeed) s.AngularVelocity *= d.MaxAngularSpeed / w;
        physics.Set(b, s);
    }
}

/// <summary>
/// Gameplay, до движения корня: сбитый лежит — копит время покоя таза; отлежался (<see cref="KnockdownRules"/>) — встаёт. Курс и корень —
/// так, чтобы начало клипа вставания легло на тела (направление таз → голова и место таза); поза тел (привязка × тело) — в позу бойца
/// заданием <see cref="PosePool.FromModel"/> до снимка тика; тела, кроме отделённых, — снова кинематические; привязка скина снимается
/// (у бойца с отделёнными частями остаётся); действие — вставание от снимка. Памяти в установившемся тике не выделяет.
/// </summary>
public sealed class RiseSystem(IPhysicsWorld physics, SkinBindings skins) : IContinuousSystem
{
    private Query _downed = null!;
    private Matrix4x4[] _joints = [], _clip = [];
    private int _clipFor = -1;
    private Vector3 _clipPelvis, _clipHead;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.Rise).After(GameLabels.BodySetup).Before(GameLabels.RootSteer).Reads<Downed>().Reads<BodyRef>().Reads<PoseRef>()
            .Reads<Severance>().Reads<AnimatorAction>().Sets<Downed>().Removes<Downed>().Sets<Transform>().Sets<PrevTransform>()
            .Sets<RootVelocity>().Sets<Shove>().Sets<AnimatorAction>().Sets<Blade>().ReserveCommands(64 * 7, 0, 64 * 7 * 128);
        _downed = s.Query().With<Fighter>().With<Downed>().Without<Dead>().Read<BodyRef>().Read<PoseRef>().Read<Downed>().Read<AnimatorAction>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        foreach (var chunk in ctx.Iterate(_downed))
        {
            var entities = chunk.Entities;
            var bodies = chunk.Read<BodyRef>();
            var poses = chunk.Read<PoseRef>();
            var downed = chunk.Read<Downed>();
            var actions = chunk.Read<AnimatorAction>();
            for (int i = 0; i < entities.Length; i++) Lie(ctx, world, time, entities[i], bodies[i], poses[i], downed[i], actions[i]);
        }
    }

    private void Lie(in SystemContext ctx, World world, in SimTime time, Entity e, BodyRef bodyRef, PoseRef poseRef, Downed downed, AnimatorAction action)
    {
        var body = FighterBodies.Handle(bodyRef);
        if (!BodyPlacement.Placeable(physics, body)) return;
        var pelvis = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis)));
        bool still = pelvis.Velocity.Length() <= KnockdownRules.StillSpeed && pelvis.AngularVelocity.Length() <= KnockdownRules.StillSpin;
        downed.Still = still ? downed.Still + time.Dt : 0;
        float lying = time.Time - downed.Time;
        if (downed.Crippled || !(lying >= KnockdownRules.MinDown && downed.Still >= KnockdownRules.StillTime || lying >= KnockdownRules.MaxDown))
        {
            ctx.Commands.Set(e, downed);
            return;
        }
        Rise(ctx, world, e, body, poseRef, action);
    }

    private void Rise(in SystemContext ctx, World world, Entity e, ArticulationHandle body, PoseRef poseRef, AnimatorAction action)
    {
        var pool = world.Resource<PosePool>();
        var actions = world.Resource<ActionTable>();
        var pose = new PoseHandle(poseRef.Index, poseRef.Generation);
        if (!pool.IsAlive(pose) || !skins.TryGet(body, out var binding, out _) || binding.Length != pool.Skeleton.JointCount) return;
        int clip = actions.Clip[ActionSlots.StandUp];
        ClipStart(pool, clip);
        // суставы по телам (как рисуется лежащий)
        if (_joints.Length < binding.Length) _joints = new Matrix4x4[binding.Length];
        var joints = _joints.AsSpan(0, binding.Length);
        for (int j = 0; j < joints.Length; j++) joints[j] = binding[j] * System(physics.Get(physics.Body(body, BodySegs.Body(skins.Segment(j)))));
        var skeleton = pool.Skeleton;
        int pelvisJoint = skeleton.Find(PelvisJoint), headJoint = skeleton.Find(HeadJoint);
        // скелет без таза или головы (синтетический) — курс по телу торса, клип — без сдвига
        var pelvis = pelvisJoint >= 0 ? joints[pelvisJoint].Translation : physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis))).Position;
        var along = headJoint >= 0 ? joints[headJoint].Translation - pelvis
            : Vector3.Transform(Vector3.UnitY, physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Torso))).Rotation);
        float facing = MathF.Atan2(along.X, along.Z) - MathF.Atan2(_clipHead.X - _clipPelvis.X, _clipHead.Z - _clipPelvis.Z);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing);
        var offset = Vector3.Transform(_clipPelvis with { Y = 0 }, turn);
        var root = new Vector3(pelvis.X - offset.X, 0, pelvis.Z - offset.Z);
        Matrix4x4.Invert(BodyPlacement.World(root, facing), out var toModel);
        for (int j = 0; j < joints.Length; j++) joints[j] *= toModel;
        pool.FromModel(pose, joints);
        bool severed = world.TryGet<Severance>(e, out var severance);
        Stand(body, severed ? severance.Detached : (ushort)0);
        if (!severed) skins.Release(e);
        var cmd = ctx.Commands;
        cmd.Remove<Downed>(e);
        cmd.Set(e, new Transform { Position = root, Facing = facing });
        cmd.Set(e, new PrevTransform { Position = root, Facing = facing });
        cmd.Set(e, new RootVelocity());
        cmd.Set(e, new Shove());
        // клинок висел на запястье и мог повернуться в нём: тело клинка — заново по кисти позы, без скорости (как смена меча)
        cmd.Set(e, Blade.None);
        ActionLayer.StartStandUp(ref action, clip, pool.Clips);
        cmd.Set(e, action);
    }

    /// <summary>Тела сегментов, кроме отделённых, — снова кинематические с профилем по умолчанию; куски и отделённое — на физике.</summary>
    private void Stand(ArticulationHandle body, ushort detached)
    {
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            if ((detached & (1 << BodySegs.Segment(k))) != 0) continue;
            var b = physics.Body(body, k);
            var s = physics.Get(b);
            s.Mode = BodyMode.Kinematic;
            s.Profile = 0;
            s.Grounded = false;
            physics.Set(b, s);
            // цель постановки — с места, где тело лежит: прошлая цель осталась от момента падения, скорость от неё была бы скачком
            physics.Place(b, s.Position, s.Rotation);
        }
    }

    /// <summary>Таз и голова в начале клипа вставания (модель) — раз на клип.</summary>
    private void ClipStart(PosePool pool, int clip)
    {
        if (_clipFor == clip) return;
        var skeleton = pool.Skeleton;
        int n = skeleton.JointCount;
        var rot = new Quaternion[n];
        var pos = new Vector3[n];
        _clip = new Matrix4x4[n];
        pool.Clips.Sample(clip, 0, rot, pos);
        skeleton.ToModel(rot, pos, _clip);
        int pelvis = skeleton.Find(PelvisJoint), head = skeleton.Find(HeadJoint);
        _clipPelvis = pelvis >= 0 ? _clip[pelvis].Translation : Vector3.Zero;
        _clipHead = head >= 0 ? _clip[head].Translation : _clipPelvis + Vector3.UnitZ;
        _clipFor = clip;
    }

    private const string PelvisJoint = "pelvis", HeadJoint = "head";

    private static Matrix4x4 System(in BodyState s)
    {
        var m = Matrix4x4.CreateFromQuaternion(s.Rotation);
        m.Translation = s.Position;
        return m;
    }
}
