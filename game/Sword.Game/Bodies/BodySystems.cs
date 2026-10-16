using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Модельные матрицы позы постройки — клип в момент, −1 — поза привязки. Считаются своим маленьким пулом теми же заданиями,
/// что поза тика (общий пул, который хеширует <c>animation</c>, не трогается); хранятся по значению — до <see cref="Cached"/>
/// значений, каждое считается один раз. Слой поверх своей позы — те же задания, что ставит модуль в тике.
/// </summary>
public sealed class StartPoseModel(ClipLibrary clips)
{
    /// <summary>Значений позы постройки с матрицами наготове.</summary>
    public const int Cached = 4;

    private PosePool? _pool;
    private PoseHandle _layer;
    private readonly PoseHandle[] _poses = new PoseHandle[Cached];
    private readonly FighterStartPose[] _values = new FighterStartPose[Cached];
    private readonly List<(int Joint, PoseMask Mask)> _masks = [];
    private int _count, _next;

    /// <summary>
    /// Поза постройки бойца <paramref name="e"/>: своя (<see cref="FighterStartPose"/>), нет — общая (<see cref="StartPose"/>, без слоя).
    /// </summary>
    public static FighterStartPose For(WorldReader world, Entity e) =>
        world.TryGet<FighterStartPose>(e, out var own) ? own : Common(world.Resource<StartPose>());

    /// <inheritdoc cref="For(WorldReader, Entity)"/>
    public static FighterStartPose For(World world, Entity e) =>
        world.TryGet<FighterStartPose>(e, out var own) ? own : Common(world.Resource<StartPose>());

    private static FighterStartPose Common(StartPose pose) => new() { Clip = pose.Clip, Time = pose.Time };

    /// <summary>Значения совпадают: клип и момент, а при слое (вес больше 0) — и слой целиком.</summary>
    private static bool Same(in FighterStartPose a, in FighterStartPose b) =>
        a.Clip == b.Clip && a.Time.Equals(b.Time) && a.LayerWeight.Equals(b.LayerWeight)
        && (!(a.LayerWeight > 0) || a.LayerClip == b.LayerClip && a.LayerTime.Equals(b.LayerTime) && a.LayerJoint == b.LayerJoint);

    /// <summary>Матрицы позы постройки <paramref name="pose"/>; каждое значение считается один раз (до <see cref="Cached"/> значений).</summary>
    /// <exception cref="InvalidDataException">Клипа позы постройки или её слоя нет в библиотеке, сустава слоя нет в скелете.</exception>
    public ReadOnlySpan<Matrix4x4> Of(in FighterStartPose pose)
    {
        _pool ??= new PosePool(clips, capacity: Cached + 1);   // + поза слоя поверх
        for (int i = 0; i < _count; i++)
            if (Same(_values[i], pose)) return _pool.Model(_poses[i]);
        bool layered = pose.LayerWeight > 0;
        if (layered)
        {
            if ((uint)pose.LayerClip >= (uint)clips.ClipCount)
                throw new InvalidDataException($"поза постройки: клипа слоя {pose.LayerClip} нет (клипов {clips.ClipCount})");
            if ((uint)pose.LayerJoint >= (uint)clips.JointCount)
                throw new InvalidDataException($"поза постройки: сустава слоя {pose.LayerJoint} нет (суставов {clips.JointCount})");
        }
        int slot;
        if (_count < Cached) _poses[slot = _count++] = _pool.Alloc();
        else
        {
            slot = _next;
            _next = (_next + 1) % Cached;
        }
        if (pose.Clip >= 0)
        {
            if (pose.Clip >= clips.ClipCount) throw new InvalidDataException($"поза постройки: клипа {pose.Clip} нет (клипов {clips.ClipCount})");
            _pool.Sample(_poses[slot], pose.Clip, pose.Time);
        }
        else
        {
            _pool.Free(_poses[slot]);
            _poses[slot] = _pool.Alloc();   // новая поза — поза привязки
        }
        if (layered)
        {
            if (_layer.IsNone) _layer = _pool.Alloc();
            _pool.Sample(_layer, pose.LayerClip, pose.LayerTime);
            _pool.BlendMasked(_poses[slot], _layer, pose.LayerWeight, MaskOf(pose.LayerJoint), 0);
        }
        _pool.ToModel(_poses[slot]);
        _pool.Tick();
        _values[slot] = pose;
        return _pool.Model(_poses[slot]);
    }

    /// <summary>Маска поддерева сустава <paramref name="joint"/> в своём пуле — заводится один раз на сустав.</summary>
    private PoseMask MaskOf(int joint)
    {
        foreach (var (j, mask) in _masks)
            if (j == joint) return mask;
        var weights = new float[clips.JointCount];
        clips.Skeleton.SubtreeMask(joint, weights);
        var added = _pool!.AddMask(weights);
        _masks.Add((joint, added));
        return added;
    }
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, до курса и разгона: построенному бойцу — форма тела и капсулы без скорости
/// по позе постройки (тела переставляются, а не создаются); построенный заново труп сначала снова живой. Бойцов сборки мира
/// ставит <see cref="PlaceWorldBuilt"/>; их записи <c>Added</c> в тике 1 ставят их ещё раз теми же значениями — итог тот же.
/// </summary>
public sealed class BodySetupSystem(IPhysicsWorld physics, BodyJoints joints, StartPoseModel start) : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.BodySetup).After(GameLabels.FighterSetup).Before(GameLabels.RootSteer).Reads<Transform>().Reads<BodyRef>()
            .Reads<FighterStartPose>().NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        foreach (var a in added)
        {
            // Удалён на том же барьере или без тела (испорченный хэндл ловит physics.bodyRefs) — ставить нечего.
            if (!world.IsAlive(a.Entity) || !world.TryGet<BodyRef>(a.Entity, out var body) || !world.TryGet<Transform>(a.Entity, out var root))
                continue;
            var handle = FighterBodies.Handle(body);
            if (!BodyPlacement.Placeable(physics, handle)) continue;
            // Построенный заново мёртвый (возрождение, сброс R, команда сцены, пересборка) — снова живой: тела кинематические, профиль
            // по умолчанию, суставы включены; тела плоти и клинка ставятся без скорости по позе постройки (своей у бойца или общей).
            Ragdoll.Corpse.Revive(physics, handle);
            BodyPlacement.Build(physics, handle, start.Of(StartPoseModel.For(world, a.Entity)), joints, root.Position, root.Facing);
        }
    }

    /// <summary>
    /// Постройка бойцов сборки мира (<c>OnWorldReady</c> — после того, как модуль задал позу постройки): форма и капсулы без скорости.
    /// До первого тика тело бойца уже стоит.
    /// </summary>
    public static void PlaceWorldBuilt(World world, IPhysicsWorld physics, BodyJoints joints, StartPoseModel start)
    {
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<Transform>().Read<BodyRef>()))
        {
            var entities = chunk.Entities;
            var roots = chunk.Read<Transform>();
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < chunk.Count; i++)
            {
                var handle = FighterBodies.Handle(bodies[i]);
                if (BodyPlacement.Placeable(physics, handle))
                    BodyPlacement.Build(physics, handle, start.Of(StartPoseModel.For(world, entities[i])), joints, roots[i].Position, roots[i].Facing);
            }
        }
    }
}

/// <summary>
/// PrePhysics: капсулы тела каждого бойца — на суставы позы этого тика в корне после Movement; кинематическая цель
/// (<see cref="IPhysicsWorld.SetKinematicTarget"/>): скорость — разность с прошлой постановкой за тик, угловая —
/// <c>RotationVector</c>/dt. Боец без позы или с негодным телом пропускается. Памяти не выделяет.
/// </summary>
public sealed class KinematicPlacementSystem(IPhysicsWorld physics, BodyJoints joints) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.KinematicPlacement).Reads<Transform>().Reads<PoseRef>().Reads<BodyRef>();
        // Мёртвый — труп на физике: его тела двигает шаг физики, а не поза; скорости последнего тика жизни берёт переход в труп.
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
