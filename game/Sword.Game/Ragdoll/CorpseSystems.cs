using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Труп на физике: переход тела бойца в труп и обратно. Живой — 14 тел кинематические, профиль по умолчанию; труп — 14 тел на
/// физике, профиль трупа, суставы по позе смерти, запястье выключено (меч выпал), самоколлизия — <see cref="CorpseJoints.SelfPairs"/>.
/// </summary>
public static class Corpse
{
    /// <summary>Тело — труп: таз на физике (переход делает все 14 тел сразу; уже перешедших система пропускает по нему).</summary>
    public static bool IsCorpse(IPhysicsWorld physics, ArticulationHandle body) =>
        physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis))).Mode == BodyMode.Simulated;

    /// <summary>
    /// Переход в труп по позе смерти <paramref name="model"/>: скорости тел урезаны до <see cref="RagdollRules.DeathMaxSpeed"/> и
    /// <see cref="RagdollRules.DeathMaxAngularSpeed"/> (гасит ложную закрутку капсул и скачок постановки), суставы привязаны по позе
    /// смерти, запястье выключено, тела — на физике. Нечисловое тело не урезается. Памяти не выделяет.
    /// </summary>
    public static void Convert(IPhysicsWorld physics, ArticulationHandle body, CorpseJoints joints, Span<JointDesc> descs,
        ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld, int corpseProfile, in RagdollRules rules)
    {
        for (int k = 0; k < BodySegs.Bodies; k++) Tame(physics, physics.Body(body, k), rules.DeathMaxSpeed, rules.DeathMaxAngularSpeed);
        joints.Describe(physics, body, model, fighterWorld, descs);
        physics.SetJoints(body, descs[..CorpseJoints.Count]);
        physics.SetJointEnabled(body, CorpseJoints.Wrist, false);
        physics.SetSelfContacts(body, CorpseJoints.SelfPairs);
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var b = physics.Body(body, k);
            physics.SetMode(b, BodyMode.Simulated);
            physics.SetDynamics(b, corpseProfile);
        }
    }

    /// <summary>
    /// Переход в труп бойца с отделёнными частями (<paramref name="severance"/>): как <see cref="Convert"/>, но суставы — по дереву
    /// после отделений; суставы отделённого при жизни сохраняют привязку (их позу покоя задал разрез, а не поза смерти); отделённые тела
    /// не урезаются — они уже на физике. Места под суставы и пары (<paramref name="all"/>, <paramref name="pairs"/>) — у вызывающего.
    /// </summary>
    public static void Convert(IPhysicsWorld physics, ArticulationHandle body, CorpseJoints joints, Span<JointDesc> descs,
        ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld, int corpseProfile, in RagdollRules rules, in Severance severance,
        Span<JointDesc> all, Span<BodyPair> pairs)
    {
        for (int k = 0; k < BodySegs.Bodies; k++)
            if (!severance.IsDetached(k)) Tame(physics, physics.Body(body, k), rules.DeathMaxSpeed, rules.DeathMaxAngularSpeed);
        int count = Severing.Joints(physics, body, joints, model, fighterWorld, severance, descs, all);
        Severing.SetJointsKeeping(physics, body, all[..count], severance.Detached);
        for (int j = 0; j < count; j++)
            if (all[j].Child == BodySegs.Weapon) physics.SetJointEnabled(body, j, false);   // меч выпадает
        physics.SetSelfContacts(body, pairs[..Severing.SelfPairs(all[..count], physics.BodyCount(body), severance, pairs)]);
        for (int k = 0, n = physics.BodyCount(body); k < n; k++)
        {
            var b = physics.Body(body, k);
            if (physics.Get(b).Mode == BodyMode.Simulated) continue;
            physics.SetMode(b, BodyMode.Simulated);
            physics.SetDynamics(b, corpseProfile);
        }
    }

    /// <summary>Меч выпадает: сустав клинка (ребёнок — тело клинка) выключен.</summary>
    public static void DropWeapon(IPhysicsWorld physics, ArticulationHandle body)
    {
        for (int j = 0, n = physics.JointCount(body); j < n; j++)
            if (physics.Joint(body, j).Child == BodySegs.Body(BodySegs.Weapon)) physics.SetJointEnabled(body, j, false);
    }

    /// <summary>Скорость и угловая скорость тела — не больше пределов (направление сохраняется).</summary>
    private static void Tame(IPhysicsWorld physics, BodyHandle b, float maxSpeed, float maxAngular)
    {
        var s = physics.Get(b);
        if (!s.IsFinite) return;
        s.Velocity = Clamp(s.Velocity, maxSpeed);
        s.AngularVelocity = Clamp(s.AngularVelocity, maxAngular);
        physics.Set(b, s);
    }

    private static Vector3 Clamp(Vector3 v, float max)
    {
        float l = v.Length();
        return l > max ? v * (max / l) : v;
    }

    /// <summary>
    /// Живой снова (постройка заново): 14 тел кинематические, профиль по умолчанию, суставы включены; тела поставит постройка.
    /// Нечисловое тело сначала ставится в начало координат без скорости.
    /// </summary>
    public static void Revive(IPhysicsWorld physics, ArticulationHandle body)
    {
        int n = physics.BodyCount(body);
        for (int k = 0; k < n; k++) Kinematic(physics, physics.Body(body, k));
        for (int j = 0, joints = physics.JointCount(body); j < joints; j++) physics.SetJointEnabled(body, j, true);
    }

    private static void Kinematic(IPhysicsWorld physics, BodyHandle b)
    {
        var s = physics.Get(b);
        if (s.Mode == BodyMode.Kinematic && s.Profile == 0 && !s.Grounded) return;
        if (!s.IsFinite)
        {
            // нечисловое тело (пересборка) — сначала в конечную позу без скорости: запись целиком его бы не приняла; постройка поставит заново
            physics.Place(b, Vector3.Zero, Quaternion.Identity);
            s = physics.Get(b);
        }
        s.Mode = BodyMode.Kinematic;
        s.Profile = 0;
        s.Grounded = false;
        physics.Set(b, s);
    }

    /// <summary>Все поля всех тел сочленения конечны (<see cref="BodyState.IsFinite"/>: положения, повороты, скорости).</summary>
    public static bool Finite(IPhysicsWorld physics, ArticulationHandle body)
    {
        for (int k = 0, n = physics.BodyCount(body); k < n; k++)
            if (!physics.Get(physics.Body(body, k)).IsFinite) return false;
        return true;
    }

}

/// <summary>
/// Gameplay и Settle, реактивная по <c>Added&lt;Dead&gt;</c>: переход в труп (<see cref="Corpse.Convert"/>). В Gameplay — смерти
/// командой сцены (тела прошлого тика), в Settle — от ударов и кровопотери этого тика. Идемпотентна; ожившие, без позы или годного
/// тела пропускает. Памяти не выделяет.
/// </summary>
public sealed class DeathToRagdollSystem(IPhysicsWorld physics, CorpseJoints joints, int corpseProfile, bool gameplay) : IReactiveSystem<Added<Dead>>
{
    private readonly JointDesc[] _descs = new JointDesc[CorpseJoints.Count];
    private readonly JointDesc[] _all = new JointDesc[Severing.MaxBodies];
    private readonly BodyPair[] _pairs = new BodyPair[Severing.MaxPairs];

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.DeathToRagdoll).After(GameLabels.SkinCapture).Reads<Dead>().Reads<Downed>().Reads<Transform>().Reads<PoseRef>().Reads<BodyRef>()
            .Reads<Severance>();
        if (gameplay) s.Before(GameLabels.RootSteer).NextTickOk<Added<Dead>>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Dead>> added)
    {
        var world = ctx.World;
        var pool = world.Resource<PosePool>();
        var rules = world.Resource<RagdollRules>();
        foreach (var a in added)
        {
            var e = a.Entity;
            if (!world.IsAlive(e) || !world.Has<Dead>(e) || !world.TryGet<BodyRef>(e, out var bodyRef) || !world.TryGet<PoseRef>(e, out var poseRef)
                || !world.TryGet<Transform>(e, out var root))
                continue;
            var body = FighterBodies.Handle(bodyRef);
            var pose = new PoseHandle(poseRef.Index, poseRef.Generation);
            if (!pool.IsAlive(pose) || !BodyPlacement.Placeable(physics, body)) continue;
            if (Corpse.IsCorpse(physics, body))
            {
                // убит лёжа (сбит с ног): тела уже на физике, меч выпадает из руки
                if (world.Has<Downed>(e)) Corpse.DropWeapon(physics, body);
                continue;
            }
            if (world.TryGet<Severance>(e, out var severance))
                Corpse.Convert(physics, body, joints, _descs, pool.Model(pose), BodyPlacement.World(root.Position, root.Facing), corpseProfile, rules,
                    severance, _all, _pairs);
            else Corpse.Convert(physics, body, joints, _descs, pool.Model(pose), BodyPlacement.World(root.Position, root.Facing), corpseProfile, rules);
        }
    }
}

/// <summary>
/// Physics: шаг физики на тик (<see cref="IPhysicsWorld.Step"/>); кинематические тела шаг не двигает, мир ECS не меняет.
/// Профайлер мира передаётся физике перед шагом, когда сменился (заведение зон выделяет память один раз). Прогрев кода шага —
/// <see cref="PhysicsWarmup"/> в конструкторе.
/// </summary>
public sealed class PhysicsStepSystem : IContinuousSystem
{
    private readonly IPhysicsWorld _physics;
    private CpuProfiler _attached = CpuProfiler.Off;

    public PhysicsStepSystem(IPhysicsWorld physics)
    {
        _physics = physics;
        PhysicsWarmup.Once(physics.Settings);
    }

    public void Setup(SystemSetup s) => s.Label(GameLabels.PhysicsStep);

    public void Run(in SystemContext ctx)
    {
        var profiler = ctx.World.Profiler;
        var want = profiler.Enabled ? profiler : CpuProfiler.Off;
        if (!ReferenceEquals(want, _attached)) Attach(want);
        _physics.Step(ctx.World.Resource<SimTime>().Dt);
    }

    private void Attach(CpuProfiler profiler)
    {
        _physics.AttachProfiler(profiler);
        _attached = profiler;
    }
}

/// <summary>
/// Physics, после шага: корень мёртвого идёт за тазом трупа по земле — (x, z) корня ← (x, z) таза, чтобы целящиеся в корень видели
/// труп. Таз не на физике или нечисловой — корень стоит. Памяти не выделяет.
/// </summary>
public sealed class CorpseRootSystem(IPhysicsWorld physics) : IContinuousSystem
{
    private Query _dead = null!, _downed = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.CorpseRoot).After(GameLabels.PhysicsStep).Reads<BodyRef>().Writes<Transform>();
        _dead = s.Query().With<Fighter>().With<Dead>().Read<BodyRef>().Write<Transform>();
        _downed = s.Query().With<Fighter>().With<Downed>().Without<Dead>().Read<BodyRef>().Write<Transform>();   // сбитый с ног лежит так же
    }

    public void Run(in SystemContext ctx)
    {
        Follow(ctx, _dead);
        Follow(ctx, _downed);
    }

    private void Follow(in SystemContext ctx, Query query)
    {
        foreach (var chunk in ctx.Iterate(query))
        {
            var roots = chunk.Write<Transform>();
            var bodies = chunk.Read<BodyRef>()[..roots.Length];
            for (int i = 0; i < roots.Length; i++) Follow(ref roots[i], FighterBodies.Handle(bodies[i]));
        }
    }

    private void Follow(ref Transform root, ArticulationHandle body)
    {
        if (!BodyPlacement.Placeable(physics, body)) return;
        var pelvis = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis)));
        if (pelvis.Mode != BodyMode.Simulated || !float.IsFinite(pelvis.Position.X) || !float.IsFinite(pelvis.Position.Z)) return;
        root.Position = new Vector3(pelvis.Position.X, root.Position.Y, pelvis.Position.Z);
    }
}

/// <summary>
/// Aftermath, после возрождения: труп с нечисловым полем хоть одного тела (решатель «взорвался») строится заново живым на месте
/// корня (<see cref="Fighters.Rebuild"/>, партия <see cref="ArenaRules.RebuildBatch"/>). Живого с нечисловым телом не пересобирает —
/// это ошибка выше физики, её ловят <c>ecs.finite</c> и <c>physics.finite</c>. Без нечисловых тел (счётчик физики 0) — ничего не обходит.
/// </summary>
public sealed class BodyRebuildSystem(IPhysicsWorld physics) : IContinuousSystem
{
    private Query _fighters = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.BodyRebuild).After(GameLabels.Respawn).Reads<BodyRef>().Reads<FighterSlot>().Reads<Transform>().Reads<FighterSeed>()
            .Reads<WeaponSlot>().Reads<GazeTarget>().Reads<AnimatorAction>().Reads<Dead>().DeclaresRebuild()
            .ReserveCommands(ArenaRules.MaxFighters * CombatReserve.RebuildCommands, 0, ArenaRules.MaxFighters * CombatReserve.RebuildBytes);
        _fighters = s.Query().With<Fighter>().Read<BodyRef>().Read<FighterSlot>().Read<Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        if (physics.NonFiniteArticulations == 0) return;   // нечисловых тел на физике нет — обходить нечего
        foreach (var chunk in ctx.Iterate(_fighters))
        {
            var bodies = chunk.Read<BodyRef>();
            var slots = chunk.Read<FighterSlot>()[..bodies.Length];
            var roots = chunk.Read<Transform>()[..bodies.Length];
            var entities = chunk.Entities[..bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                var body = FighterBodies.Handle(bodies[i]);
                if (!BodyPlacement.Placeable(physics, body) || !physics.HasNonFinite(body) || !Corpse.IsCorpse(physics, body)) continue;
                Rebuild(ctx, entities[i], slots[i].Index, roots[i]);
            }
        }
    }

    private static void Rebuild(in SystemContext ctx, Entity e, int slot, in Transform root)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        bool dead = world.TryGet<Dead>(e, out var d);
        // Возрождение того же тика (core.respawn) уже строит этого врага заново — второй постройки не нужно.
        if (dead && world.Has<EnemyTag>(e) && time.Time - d.Time > DamageRules.RespawnDelay) return;
        if (slot is < 0 or > ArenaState.MaxEnemies) return;   // место вне арены — нарушение arena.slots
        var at = float.IsFinite(root.Position.X) && float.IsFinite(root.Position.Z) ? new Vector2(root.Position.X, root.Position.Z) : Vector2.Zero;
        float facing = float.IsFinite(root.Facing) ? root.Facing : 0;
        float seed = world.TryGet<FighterSeed>(e, out var s) ? s.Value : 0;
        int weapon = slot == 0 ? world.Resource<PlayerInventory>().Equipped : world.TryGet<WeaponSlot>(e, out var w) ? w.Index : 0;
        var spawn = ArenaRules.Placed(world.Resource<SimRandom>(), time.Tick, slot, at, facing, seed, weapon, ArenaRules.RebuildBatch);
        var gaze = slot == 0 && world.TryGet<GazeTarget>(e, out var g) ? g : default;
        var action = world.TryGet<AnimatorAction>(e, out var a) ? a : AnimatorAction.None;
        Fighters.Rebuild(ctx.Commands, e, spawn, Fighters.Standing(spawn), gaze, action, dead: dead);
    }
}
