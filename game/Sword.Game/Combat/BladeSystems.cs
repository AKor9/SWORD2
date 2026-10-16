using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Клинок бойца в мире: основание и кончик клинка меча — в системе кисти позы в мире бойца (<see cref="BodyPlacement.World"/>);
/// тело клинка (<see cref="BladeBody"/>) — капсула от хвата до кончика в той же системе (<see cref="BodyOf"/>).
/// </summary>
public static class BladePlacement
{
    /// <summary>Мир кисти <paramref name="hand"/> позы <paramref name="model"/>: модельная матрица кисти в мире бойца.</summary>
    public static Matrix4x4 Hand(ReadOnlySpan<Matrix4x4> model, int hand, Vector3 root, float facing) =>
        model[hand] * BodyPlacement.World(root, facing);

    /// <summary>Основание и кончик клинка <paramref name="weapon"/> в кисти <paramref name="hand"/> позы <paramref name="model"/>.</summary>
    public static (Vector3 Base, Vector3 Tip) Of(ReadOnlySpan<Matrix4x4> model, int hand, in WeaponInfo weapon, Vector3 root, float facing)
    {
        var world = Hand(model, hand, root, facing);
        return (Vector3.Transform(weapon.BladeBase, world), Vector3.Transform(weapon.BladeTip, world));
    }

    /// <summary>
    /// Тело клинка в мире кисти <paramref name="hand"/>: ось — от хвата до кончика меча <paramref name="weapon"/>, центр — середина,
    /// поворот — Y вдоль клинка, X — опорная ось <see cref="BladeBody.XHint"/>: относительно кисти всегда одинаково.
    /// </summary>
    public static (Vector3 Position, Quaternion Rotation) BodyOf(in Matrix4x4 hand, in WeaponInfo weapon, in BladeBody body)
    {
        var grip = hand.Translation;   // Vector3.Transform(BladeBody.Grip, hand): хват — начало системы кисти
        var tip = Vector3.Transform(weapon.BladeTip, hand);
        return ((grip + tip) * 0.5f, AnimMath.FromBasis(tip - grip, Vector3.TransformNormal(body.XHint, hand)));
    }

    /// <summary>
    /// Постановка клинка тика: прошлый — клинок прошлого тика; клинок не поставлен или посчитан другим мечом — прошлое = нынешнему
    /// (фантомного прохода от старого клинка к новому нет).
    /// </summary>
    public static void Place(ref Blade blade, Vector3 bladeBase, Vector3 tip, int weapon)
    {
        bool fresh = blade.Weapon != weapon;
        blade.PrevBase = fresh ? bladeBase : blade.Base;
        blade.PrevTip = fresh ? tip : blade.Tip;
        blade.Base = bladeBase;
        blade.Tip = tip;
        blade.Weapon = weapon;
    }

    /// <summary>Клинок только что построенного бойца — без прошлого.</summary>
    public static Blade Built(ReadOnlySpan<Matrix4x4> startModel, int hand, WeaponTable weapons, int weapon, Vector3 root, float facing)
    {
        var (b, t) = Of(startModel, hand, weapons.Weapons[weapon], root, facing);
        return new Blade { Base = b, Tip = t, PrevBase = b, PrevTip = t, Weapon = weapon };
    }

    /// <summary>
    /// Тело клинка без скорости (постройка, смена меча): масса и радиус <see cref="BladeBody"/>, полудлина — половина отрезка хват —
    /// кончик, постановка по кисти <paramref name="hand"/>. Негодное сочленение — ничего (ловит <c>physics.bodyRefs</c>).
    /// </summary>
    public static void BuildBody(IPhysicsWorld physics, ArticulationHandle body, WeaponTable weapons, int weapon, in Matrix4x4 hand)
    {
        if (!BodyPlacement.Placeable(physics, body)) return;
        var shape = weapons.Bodies[weapon];
        var (position, rotation) = BodyOf(hand, weapons.Weapons[weapon], shape);
        var b = physics.Body(body, BodySegs.Body(BodySegs.Weapon));
        physics.SetShape(b, BladeBody.Mass, BladeBody.Radius, shape.HalfLength);
        physics.Place(b, position, rotation);
    }
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, после постройки тела: клинок по позе постройки без прошлого (первая
/// постановка тика заметает клинок от позы постройки к позе тика) — командой; тело клинка — сразу
/// (<see cref="BladePlacement.BuildBody"/>). Бойцов сборки мира ставит <see cref="PlaceWorldBuilt"/>.
/// </summary>
public sealed class BladeSetupSystem(IPhysicsWorld physics, WeaponTable weapons, int hand, StartPoseModel start) : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.BladeSetup).After(GameLabels.BodySetup).Before(GameLabels.RootSteer).Reads<Transform>().Reads<WeaponSlot>()
            .Reads<BodyRef>().Reads<FighterStartPose>().Sets<Blade>().NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        foreach (var a in added)
        {
            // Удалён на том же барьере, без клинка или с чужим номером меча (его ловит combat.weapons) — ставить нечего.
            if (!world.IsAlive(a.Entity) || !world.Has<Blade>(a.Entity) || !world.TryGet<Transform>(a.Entity, out var root)
                || !world.TryGet<WeaponSlot>(a.Entity, out var slot) || (uint)slot.Index >= (uint)weapons.Count)
                continue;
            var model = start.Of(StartPoseModel.For(world, a.Entity));   // своя поза постройки бойца или общая
            ctx.Commands.Set(a.Entity, BladePlacement.Built(model, hand, weapons, slot.Index, root.Position, root.Facing));
            if (world.TryGet<BodyRef>(a.Entity, out var body))
                BladePlacement.BuildBody(physics, FighterBodies.Handle(body), weapons, slot.Index, BladePlacement.Hand(model, hand, root.Position, root.Facing));
        }
    }

    /// <summary>Клинки бойцов сборки мира — по позе постройки (<c>OnWorldReady</c>, как тела).</summary>
    public static void PlaceWorldBuilt(World world, IPhysicsWorld physics, WeaponTable weapons, int hand, StartPoseModel start)
    {
        var built = new List<(Entity Entity, Blade Blade)>();
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().With<Blade>().Read<Transform>().Read<WeaponSlot>().Read<BodyRef>()))
        {
            var roots = chunk.Read<Transform>();
            var slots = chunk.Read<WeaponSlot>();
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if ((uint)slots[i].Index >= (uint)weapons.Count) continue;
                var model = start.Of(StartPoseModel.For(world, chunk.Entities[i]));
                built.Add((chunk.Entities[i], BladePlacement.Built(model, hand, weapons, slots[i].Index, roots[i].Position, roots[i].Facing)));
                BladePlacement.BuildBody(physics, FighterBodies.Handle(bodies[i]), weapons, slots[i].Index,
                    BladePlacement.Hand(model, hand, roots[i].Position, roots[i].Facing));
            }
        }
        foreach (var (e, blade) in built) world.SetImmediate(e, blade);
    }
}

/// <summary>
/// PrePhysics, после капсул тела: клинок каждого бойца — по позе тика (<see cref="BladePlacement.Place"/>); у живого — и тело
/// клинка кинематической целью; сменился меч — новая форма без скорости. Мёртвый пропускается (тело клинка на физике, отрезок
/// стоит, где был). Боец без позы или с мечом вне таблицы пропускается. Памяти не выделяет.
/// </summary>
public sealed class BladePlacementSystem(IPhysicsWorld physics, WeaponTable weapons, int hand) : IContinuousSystem
{
    private Query _alive = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.BladePlacement).After(GameLabels.KinematicPlacement).Reads<Transform>().Reads<PoseRef>().Reads<WeaponSlot>()
            .Reads<BodyRef>().Writes<Blade>();
        _alive = s.Query().With<Fighter>().Without<Dead>().Read<Transform>().Read<PoseRef>().Read<WeaponSlot>().Write<Blade>().Read<BodyRef>();
    }

    public void Run(in SystemContext ctx)
    {
        var pool = ctx.World.Resource<PosePool>();
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_alive))
        {
            var blades = chunk.Write<Blade>();
            var roots = chunk.Read<Transform>()[..blades.Length];
            var poses = chunk.Read<PoseRef>()[..blades.Length];
            var slots = chunk.Read<WeaponSlot>()[..blades.Length];
            var bodies = chunk.Read<BodyRef>()[..blades.Length];
            for (int i = 0; i < blades.Length; i++)
                Place(pool, ref blades[i], roots[i], poses[i], slots[i].Index, FighterBodies.Handle(bodies[i]), dt);
        }
    }

    /// <summary>Клинок живого бойца и тело клинка.</summary>
    private void Place(PosePool pool, ref Blade blade, in Transform root, PoseRef poseRef, int weapon, ArticulationHandle body, float dt)
    {
        var pose = new PoseHandle(poseRef.Index, poseRef.Generation);
        var table = weapons.Weapons;
        if (!pool.IsAlive(pose) || (uint)weapon >= (uint)table.Length) return;
        var handWorld = BladePlacement.Hand(pool.Model(pose), hand, root.Position, root.Facing);
        bool fresh = blade.Weapon != weapon;
        BladePlacement.Place(ref blade, Vector3.Transform(table[weapon].BladeBase, handWorld), Vector3.Transform(table[weapon].BladeTip, handWorld), weapon);
        if (!BodyPlacement.Placeable(physics, body)) return;
        if (fresh)
        {
            BladePlacement.BuildBody(physics, body, weapons, weapon, handWorld);
            return;
        }
        var (position, rotation) = BladePlacement.BodyOf(handWorld, table[weapon], weapons.Bodies[weapon]);
        physics.SetKinematicTarget(physics.Body(body, BodySegs.Body(BodySegs.Weapon)), position, rotation, dt);
    }
}
