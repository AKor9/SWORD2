using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Модельные матрицы позы постройки <see cref="StartPose"/> (<c>FighterAnimator.Reset</c> → <c>Tick(0)</c> SWORD 2eab13b):
/// клип в момент, −1 — поза привязки. Считаются своим маленьким пулом поз теми же заданиями, что поза тика (сэмпл клипа и
/// прямая кинематика), — один раз на значение ресурса; общий пул поз не трогается (его хеширует участник <c>animation</c>).
/// Один на симуляцию: его берут постройка сборки мира (<see cref="BodySetupSystem.PlaceWorldBuilt"/>), постройка командой
/// (<see cref="BodySetupSystem"/>) и инвариант <c>physics.kinematicPose</c> до первого тика.
/// </summary>
public sealed class StartPoseModel(ClipLibrary clips)
{
    private PosePool? _pool;
    private PoseHandle _pose;
    private StartPose _computed = new() { Clip = int.MinValue };

    /// <summary>Матрицы позы постройки <paramref name="pose"/>; пересчёт — только при смене значения.</summary>
    /// <exception cref="InvalidDataException">Клипа позы постройки нет в библиотеке.</exception>
    public ReadOnlySpan<Matrix4x4> Of(StartPose pose)
    {
        if (_pool is null)
        {
            _pool = new PosePool(clips, capacity: 1);
            _pose = _pool.Alloc();
        }
        if (_computed.Clip != pose.Clip || !_computed.Time.Equals(pose.Time))
        {
            if (pose.Clip >= 0)
            {
                if (pose.Clip >= clips.ClipCount) throw new InvalidDataException($"поза постройки: клипа {pose.Clip} нет (клипов {clips.ClipCount})");
                _pool.Sample(_pose, pose.Clip, pose.Time);
            }
            else
            {
                _pool.Free(_pose);
                _pose = _pool.Alloc();   // новая поза — поза привязки
            }
            _pool.ToModel(_pose);
            _pool.Tick();
            _computed = pose;
        }
        return _pool.Model(_pose);
    }
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, до курса и разгона (<see cref="GameLabels.BodySetup"/>): построенному
/// бойцу — форма тела и капсулы без скорости по позе постройки (<c>Fighter.Build</c> → <c>PlaceBuilt</c> SWORD 2eab13b: тела
/// переставляются, а не создаются — у бойца, построенного заново, те же тела). Корень и курс — постройки: <see cref="Transform"/>
/// до <c>core.root-steer</c> этого тика. Поза постройки — <see cref="StartPose"/> (<see cref="StartPoseModel"/>). С 7з построенный заново
/// труп сначала снова живой (<see cref="Ragdoll.Corpse.Revive"/>: кинематика, профиль по умолчанию, суставы включены).
///
/// Бойцов сборки мира ставит сборка мира (<see cref="PlaceWorldBuilt"/>, после наполнения мира модулями — 5в: как SWORD
/// после <c>AttachRig</c>). Их записи <c>Added</c> система видит в тике 1 (записи сборки мира доставляются первому
/// срабатыванию) и ставит их ещё раз теми же значениями — корень, курс и поза постройки до <c>core.root-steer</c> тика 1 те
/// же, а <c>Place</c> переписывает все поля постановки: итог тот же, что без повтора.
/// </summary>
public sealed class BodySetupSystem(IPhysicsWorld physics, BodyJoints joints, StartPoseModel start) : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.BodySetup).After(GameLabels.FighterSetup).Before(GameLabels.RootSteer).Reads<Transform>().Reads<BodyRef>()
            .NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        var model = start.Of(world.Resource<StartPose>());
        foreach (var a in added)
        {
            // Удалён на том же барьере или без тела (испорченный хэндл ловит physics.bodyRefs) — ставить нечего.
            if (!world.IsAlive(a.Entity) || !world.TryGet<BodyRef>(a.Entity, out var body) || !world.TryGet<Transform>(a.Entity, out var root))
                continue;
            var handle = FighterBodies.Handle(body);
            if (!BodyPlacement.Placeable(physics, handle)) continue;
            // 7з: построенный заново мёртвый (возрождение, сброс R, команда сцены, пересборка) — снова живой: тела кинематические, профиль
            // по умолчанию, суставы включены; тела плоти и клинка ставятся без скорости по позе постройки.
            Ragdoll.Corpse.Revive(physics, handle);
            BodyPlacement.Build(physics, handle, model, joints, root.Position, root.Facing);
        }
    }

    /// <summary>
    /// Постройка бойцов сборки мира (<c>OnWorldReady</c> ядра — после <c>OnWorldBuilt</c> ядра и модулей: позу постройки
    /// задаёт модуль Locomotion при сборке мира): форма и капсулы без скорости по позе постройки в корне и курсом постройки,
    /// как <c>PlaceBuilt</c> SWORD сразу после <c>Reset</c>/<c>AttachRig</c>. До первого тика тело бойца уже стоит.
    /// </summary>
    public static void PlaceWorldBuilt(World world, IPhysicsWorld physics, BodyJoints joints, StartPoseModel start)
    {
        var model = start.Of(world.Resource<StartPose>());
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<Transform>().Read<BodyRef>()))
        {
            var roots = chunk.Read<Transform>();
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < chunk.Count; i++)
            {
                var handle = FighterBodies.Handle(bodies[i]);
                if (BodyPlacement.Placeable(physics, handle)) BodyPlacement.Build(physics, handle, model, joints, roots[i].Position, roots[i].Facing);
            }
        }
    }
}

/// <summary>
/// PrePhysics, непрерывная (<see cref="GameLabels.KinematicPlacement"/>): капсулы тела каждого бойца — на суставы позы этого
/// тика в корне после Movement (<c>DuelArena.PlaceBodies</c> SWORD 2eab13b после <c>ResolveRoots</c>; без клинка — этап 6,
/// <c>BodyPlacer</c>/<c>PinFeet</c> — этап 9, визуального смещения — этап 8, пропуска отрубленных нет — Cutting отложен, решение 17): кинематическая
/// цель тика (<see cref="IPhysicsWorld.SetKinematicTarget"/>) — скорость — разность с прошлой постановкой за тик, угловая —
/// <c>RotationVector</c>/dt. Первая постановка без скорости — при постройке (<see cref="BodySetupSystem"/>). Боец без позы
/// (не заведена) или с негодным телом пропускается — его ловят инварианты <c>animation.*</c>, <c>physics.bodyRefs</c>.
/// Памяти не выделяет.
/// </summary>
public sealed class KinematicPlacementSystem(IPhysicsWorld physics, BodyJoints joints) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.KinematicPlacement).Reads<Transform>().Reads<PoseRef>().Reads<BodyRef>();
        // Мёртвый — труп на физике (7з): его тела двигает шаг физики, а не поза; скорости последнего тика жизни берёт переход в труп.
        _query = s.Query().With<Fighter>().Without<Dead>().Read<Transform>().Read<PoseRef>().Read<BodyRef>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        var pool = ctx.World.Resource<PosePool>();
        foreach (var chunk in ctx.Iterate(_query))
        {
            var bodies = chunk.Read<BodyRef>();
            var roots = chunk.Read<Transform>()[..bodies.Length];
            var poses = chunk.Read<PoseRef>()[..bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                var pose = new PoseHandle(poses[i].Index, poses[i].Generation);
                var body = FighterBodies.Handle(bodies[i]);
                if (!pool.IsAlive(pose) || !BodyPlacement.Placeable(physics, body)) continue;
                BodyPlacement.Tick(physics, body, pool.Model(pose), joints, roots[i].Position, roots[i].Facing, dt);
            }
        }
    }
}
