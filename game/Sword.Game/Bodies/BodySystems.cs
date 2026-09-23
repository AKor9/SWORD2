using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, до курса и разгона (<see cref="GameLabels.BodySetup"/>): построенному
/// бойцу — форма тела и капсулы без скорости по позе постройки (<c>Fighter.Build</c> → <c>PlaceBuilt</c> SWORD 2eab13b: тела
/// переставляются, а не создаются — у бойца, построенного заново, те же тела). Корень и курс — постройки: <see cref="Transform"/>
/// до <c>core.root-steer</c> этого тика. Поза постройки — <see cref="StartPose"/> (<c>FighterAnimator.Reset</c> → <c>Tick(0)</c>):
/// её модельные матрицы считаются своим маленьким пулом поз теми же заданиями, что поза тика (сэмпл клипа и прямая
/// кинематика), — один раз на значение ресурса; общий пул поз не трогается (его хеширует участник <c>animation</c>).
///
/// Боец сборки мира строится немедленными операциями до первого тика: его постройку система видит в тике 1 (записи
/// <c>Added</c> сборки мира доставляются первому срабатыванию) — до того тело без формы в начале координат, как поза до
/// первого тика — поза привязки. Итог после тика 1 — тот же, что у SWORD после <c>PlaceBuilt</c> и тика.
/// </summary>
public sealed class BodySetupSystem(IPhysicsWorld physics, BodyJoints joints) : IReactiveSystem<Added<Fighter>>
{
    private PosePool? _startPool;
    private PoseHandle _start;
    private StartPose _computed = new() { Clip = int.MinValue };

    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.BodySetup).After(GameLabels.FighterSetup).Before(GameLabels.RootSteer).Reads<Transform>().Reads<BodyRef>()
            .NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        var start = StartModel(world.Resource<PosePool>().Clips, world.Resource<StartPose>());
        foreach (var a in added)
        {
            // Удалён на том же барьере или без тела (испорченный хэндл ловит physics.bodyRefs) — ставить нечего.
            if (!world.IsAlive(a.Entity) || !world.TryGet<BodyRef>(a.Entity, out var body) || !world.TryGet<Transform>(a.Entity, out var root))
                continue;
            var handle = FighterBodies.Handle(body);
            if (!BodyPlacement.Placeable(physics, handle)) continue;
            BodyPlacement.Build(physics, handle, start, joints, root.Position, root.Facing);
        }
    }

    /// <summary>Модельные матрицы позы постройки: клип <see cref="StartPose.Clip"/> в момент <see cref="StartPose.Time"/> (−1 — поза привязки).</summary>
    private ReadOnlySpan<Matrix4x4> StartModel(ClipLibrary clips, StartPose pose)
    {
        if (_startPool is null)
        {
            _startPool = new PosePool(clips, capacity: 1);
            _start = _startPool.Alloc();
        }
        if (_computed.Clip != pose.Clip || !_computed.Time.Equals(pose.Time))
        {
            if (pose.Clip >= 0)
            {
                if (pose.Clip >= clips.ClipCount) throw new InvalidDataException($"поза постройки: клипа {pose.Clip} нет (клипов {clips.ClipCount})");
                _startPool.Sample(_start, pose.Clip, pose.Time);
            }
            else
            {
                _startPool.Free(_start);
                _start = _startPool.Alloc();   // новая поза — поза привязки
            }
            _startPool.ToModel(_start);
            _startPool.Tick();
            _computed = pose;
        }
        return _startPool.Model(_start);
    }
}

/// <summary>
/// PrePhysics, непрерывная (<see cref="GameLabels.KinematicPlacement"/>): капсулы тела каждого бойца — на суставы позы этого
/// тика в корне после Movement (<c>DuelArena.PlaceBodies</c> SWORD 2eab13b после <c>ResolveRoots</c>; без клинка — этап 6,
/// <c>BodyPlacer</c>/<c>PinFeet</c> — этап 9, визуального смещения — этап 8, пропуска отрубленных — этап 11): кинематическая
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
        _query = s.Query().With<Fighter>().Read<Transform>().Read<PoseRef>().Read<BodyRef>();
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
