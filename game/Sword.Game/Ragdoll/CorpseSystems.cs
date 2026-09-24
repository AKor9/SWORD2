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
/// Труп на физике (7з): переход тела бойца в труп и обратно — функции над телом в подсистеме Physics (идеи — <c>CaptureAndSettle</c>,
/// <c>TameDetached</c>, <c>ToRagdoll</c>, <c>Build</c> SWORD 2eab13b; research-arena-tick §3). Живой — все 14 тел кинематические, профиль
/// по умолчанию, суставы (если были) включены; труп — все 14 тел на физике, профиль трупа, суставы привязаны по позе смерти, запястье
/// выключено (меч выпал).
/// </summary>
public static class Corpse
{
    /// <summary>Тело — труп: таз на физике (переход делает все 14 тел сразу; уже перешедших система пропускает по нему).</summary>
    public static bool IsCorpse(IPhysicsWorld physics, ArticulationHandle body) =>
        physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis))).Mode == BodyMode.Simulated;

    /// <summary>
    /// Переход в труп по позе смерти <paramref name="model"/> в мире бойца <paramref name="fighterWorld"/>: (1) скорости тел (последней
    /// постановки живого) урезаны до <see cref="RagdollRules.DeathMaxSpeed"/> и <see cref="RagdollRules.DeathMaxAngularSpeed"/> — гасит и
    /// ложную закрутку кинематических капсул вокруг оси, и скачок постановки; (2) суставы — описания по позе смерти (оси шарниров — анатомические)
    /// и привязка по ней: ошибка точки 0, пределы наклона — от этой позы; (3) сустав запястья выключен — меч выпадает при каждой смерти;
    /// (4) все 14 тел — на физике с профилем трупа. Привязку скина снимает <c>core.skin-capture</c> раньше в той же стадии — ей нужна только
    /// поза тел, не скорости. Нечисловое тело не урезается (его пересоберёт <see cref="BodyRebuildSystem"/>). Памяти не выделяет.
    /// </summary>
    public static void Convert(IPhysicsWorld physics, ArticulationHandle body, CorpseJoints joints, Span<JointDesc> descs,
        ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld, int corpseProfile, in RagdollRules rules)
    {
        for (int k = 0; k < BodySegs.Bodies; k++) Tame(physics, physics.Body(body, k), rules.DeathMaxSpeed, rules.DeathMaxAngularSpeed);
        joints.Describe(physics, body, model, fighterWorld, descs);
        physics.SetJoints(body, descs[..CorpseJoints.Count]);
        physics.SetJointEnabled(body, CorpseJoints.Wrist, false);
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var b = physics.Body(body, k);
            physics.SetMode(b, BodyMode.Simulated);
            physics.SetDynamics(b, corpseProfile);
        }
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
    /// Живой снова (постройка заново — возрождение, сброс R, команда сцены «поставить», пересборка): все 14 тел кинематические, профиль по
    /// умолчанию, касания земли нет, суставы (если есть) включены. Поставит тела без скорости постройка (<see cref="BodyPlacement.Build"/>,
    /// тело клинка — <see cref="BladePlacement.BuildBody"/>). Нечисловое тело (пересборка) сначала ставится в начало координат без скорости.
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

    /// <summary>Центр масс тел сочленения, м (масса 0 — нет центра: NaN).</summary>
    public static Vector3 CenterOfMass(IPhysicsWorld physics, ArticulationHandle body)
    {
        var sum = Vector3.Zero;
        float mass = 0;
        for (int k = 0, n = physics.BodyCount(body); k < n; k++)
        {
            var s = physics.Get(physics.Body(body, k));
            sum += s.Position * s.Mass;
            mass += s.Mass;
        }
        return mass > 0 ? sum / mass : new Vector3(float.NaN);
    }
}

/// <summary>
/// Gameplay (после <c>core.skin-capture</c>, до <c>core.root-steer</c>) и Settle (после <c>core.skin-capture</c>) (<see cref="GameLabels.DeathToRagdoll"/>;
/// 7з), реактивная по <c>Added&lt;Dead&gt;</c>: переход в труп (<see cref="Corpse.Convert"/>). В Gameplay — смерти командой сцены этого тика
/// (Intent): тела и поза — прошлого тика, шаг физики этого тика уже двигает труп; в Settle — смерти от ударов (Resolve) и кровопотери
/// (Aftermath) этого тика: тела и поза этого тика, двигать их начнёт шаг следующего. Идемпотентна: уже перешедших (таз на физике —
/// регистрация в Gameplay видит и смерти прошлого тика, снятые в Settle) пропускает; ожившие, без позы или годного тела — тоже (их ловят
/// <c>physics.modes</c>, <c>animation.*</c>, <c>physics.bodyRefs</c>). Памяти не выделяет.
/// </summary>
public sealed class DeathToRagdollSystem(IPhysicsWorld physics, CorpseJoints joints, int corpseProfile, bool gameplay) : IReactiveSystem<Added<Dead>>
{
    private readonly JointDesc[] _descs = new JointDesc[CorpseJoints.Count];

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.DeathToRagdoll).After(GameLabels.SkinCapture).Reads<Dead>().Reads<Transform>().Reads<PoseRef>().Reads<BodyRef>();
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
            if (!pool.IsAlive(pose) || !BodyPlacement.Placeable(physics, body) || Corpse.IsCorpse(physics, body)) continue;
            Corpse.Convert(physics, body, joints, _descs, pool.Model(pose), BodyPlacement.World(root.Position, root.Facing), corpseProfile, rules);
        }
    }
}

/// <summary>
/// Physics (<see cref="GameLabels.PhysicsStep"/>; 7з): шаг физики на тик (<see cref="IPhysicsWorld.Step"/>, подшаги — из настроек): тела на
/// физике (трупы) — интеграция, суставы, земля, стена и колонны арены, трупы между собой и толчки кинематических тел живых, мир после пар
/// (7з-2). Кинематические тела шаг не двигает; без трупов — только границы сочленений. Мир ECS не меняет.
/// <para>
/// 7з-2: зоны профайлера внутри шага (<c>physics.*</c>, <see cref="IPhysicsWorld.AttachProfiler"/>) — профайлер мира, пока он включён: сменился
/// (подключили, включили, выключили) — физике передаётся перед шагом (заведение зон выделяет память — один раз на профайлер, в первом тике
/// после включения); выключенный или <see cref="CpuProfiler.Off"/> — физике <see cref="CpuProfiler.Off"/>. Прогрев кода шага —
/// <see cref="PhysicsWarmup"/> при сборке мира (конструктор системы).
/// </para>
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
/// Physics, после шага (<see cref="GameLabels.CorpseRoot"/>; 7з): корень мёртвого идёт за тазом его трупа по земле — (x, z) корня ← (x, z)
/// центра тела таза (высота и курс корня прежние): кто целится в корень (ИИ врагов идёт к трупу игрока ×0,2 — 6г), видит, где лежит труп.
/// Таз не на физике (труп ещё не перешёл — смерть этого тика после шага) или нечисловой (пересборка — <see cref="BodyRebuildSystem"/>) — корень
/// стоит. Памяти не выделяет.
/// </summary>
public sealed class CorpseRootSystem(IPhysicsWorld physics) : IContinuousSystem
{
    private Query _dead = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.CorpseRoot).After(GameLabels.PhysicsStep).Reads<BodyRef>().Writes<Transform>();
        _dead = s.Query().With<Fighter>().With<Dead>().Read<BodyRef>().Write<Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var chunk in ctx.Iterate(_dead))
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
/// Aftermath, после возрождения (<see cref="GameLabels.BodyRebuild"/>; 7з; идея — пересборка по NaN у таза SWORD 2eab13b, у нас — любое
/// поле любого тела): труп, у которого нечисловое хоть одно поле хоть одного из 14 тел (решатель «взорвался»), строится заново на месте
/// корня тем же курсом (<see cref="Fighters.Rebuild"/>: зерно и меч прежние, таймер удара врага — своей партией
/// <see cref="ArenaRules.RebuildBatch"/>), живым; тела без скорости ставит постройка следующего тика (Gameplay). Враг, которого в этом
/// же тике строит возрождение, пропускается. Нарушение до пересборки видно — <c>physics.finite</c> после шага. Живого с нечисловым телом
/// (кинематика — по позе и корню: ошибка выше физики) не пересобирает — его ловят <c>ecs.finite</c>, <c>physics.finite</c>, и чинить молча
/// нельзя.
/// <para>
/// 7з-2: нечисловые тела считает физика (<see cref="IPhysicsWorld.NonFiniteArticulations"/> — по концу шага и записям тел после него): 0 —
/// система ничего не обходит (тик боя — одно чтение счётчика вместо 14 копий тел на каждого бойца); иначе — бойцы по порядку, у трупа с
/// флагом <see cref="IPhysicsWorld.HasNonFinite"/> — постройка заново. Гарантия та же: флаг — любое нечисловое поле любого тела на физике.
/// </para>
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
        if (physics.NonFiniteArticulations == 0) return;   // 7з-2: нечисловых тел на физике нет — обходить нечего
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
