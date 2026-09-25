using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Клинок бойца в мире (<c>FighterAnimator.Blade</c> и клинок в <c>DuelArena.PlaceBodies</c> SWORD 2eab13b; этап 6б): модельная
/// матрица кисти позы в мире бойца (<see cref="BodyPlacement.World"/> — корень, курс, высота рельефа: тот же мир, что у капсул
/// тела, «как показан боец»), основание и кончик клинка меча — в её системе. Масштаба клинка игрока (<c>SWORD_PLAYER_BLADE</c>,
/// тест разреза SWORD) нет. С 7ж — и тело клинка (тело 11 сочленения, <see cref="BladeBody"/>): капсула от хвата (начало кисти) до
/// кончика в той же системе кисти (<see cref="BodyOf"/>).
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
    /// Тело клинка (7ж) в мире кисти <paramref name="hand"/>: хват — начало кисти, кончик — кончик клинка меча
    /// <paramref name="weapon"/> (тот же, что у <see cref="Of"/>: ось капсулы лежит на отрезке хват — кончик), центр — середина,
    /// поворот — ось Y вдоль клинка, X — опорная ось кисти <see cref="BladeBody.XHint"/> (<see cref="AnimMath.FromBasis"/>): тело
    /// клинка повёрнуто относительно кисти всегда одинаково.
    /// </summary>
    public static (Vector3 Position, Quaternion Rotation) BodyOf(in Matrix4x4 hand, in WeaponInfo weapon, in BladeBody body)
    {
        var grip = hand.Translation;   // Vector3.Transform(BladeBody.Grip, hand): хват — начало системы кисти
        var tip = Vector3.Transform(weapon.BladeTip, hand);
        return ((grip + tip) * 0.5f, AnimMath.FromBasis(tip - grip, Vector3.TransformNormal(body.XHint, hand)));
    }

    /// <summary>
    /// Постановка клинка тика: прошлый — клинок прошлого тика (<c>KinPrevBase = KinBase</c>); клинок не поставлен или посчитан
    /// другим мечом (постройка, смена меча) — прошлое = нынешнему: прохода между разными клинками нет (в SWORD смена меча давала
    /// один фантомный проход от старого клинка к новому).
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

    /// <summary>Клинок только что построенного бойца — без прошлого (<c>PlaceBuilt</c> SWORD: первая постановка).</summary>
    public static Blade Built(ReadOnlySpan<Matrix4x4> startModel, int hand, WeaponTable weapons, int weapon, Vector3 root, float facing)
    {
        var (b, t) = Of(startModel, hand, weapons.Weapons[weapon], root, facing);
        return new Blade { Base = b, Tip = t, PrevBase = b, PrevTip = t, Weapon = weapon };
    }

    /// <summary>
    /// Тело клинка без скорости (7ж; постройка бойца, смена меча): форма меча <paramref name="weapon"/> — масса и радиус
    /// <see cref="BladeBody"/>, полудлина — половина отрезка хват — кончик — и постановка по кисти <paramref name="hand"/>
    /// (<see cref="IPhysicsWorld.Place"/>). Сочленение негодно для бойца — ничего (его ловит <c>physics.bodyRefs</c>).
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
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, после постройки тела и до курса (<see cref="GameLabels.BladeSetup"/>):
/// построенному бойцу — клинок по позе постройки (своей — <see cref="FighterStartPose"/>, 8е — или общей <see cref="StartPose"/>) в корне и
/// курсе постройки, без прошлого
/// (<c>PlaceBuilt</c> → <c>PlaceBodies(f, 0)</c> SWORD: <c>first</c> — <c>KinPrevBase = bladeBase</c>): первая постановка тика
/// после постройки заметает клинок от позы постройки к позе тика, как в SWORD. Ставится командой (на барьере Gameplay).
/// С 7ж — и тело клинка: форма меча в руке и постановка без скорости по той же кисти (<see cref="BladePlacement.BuildBody"/>) —
/// сразу, как тела плоти в <c>core.body-setup</c>. Бойцов сборки мира ставит сборка мира (<see cref="PlaceWorldBuilt"/>); тик 1
/// ставит их ещё раз теми же значениями.
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
            ctx.Cause = a.Cause;   // причина постройки бойца — родитель клинка (state.why, 6д)
            var model = start.Of(StartPoseModel.For(world, a.Entity));   // 8е: своя поза постройки бойца или общая
            ctx.Commands.Set(a.Entity, BladePlacement.Built(model, hand, weapons, slot.Index, root.Position, root.Facing));
            if (world.TryGet<BodyRef>(a.Entity, out var body))
                BladePlacement.BuildBody(physics, FighterBodies.Handle(body), weapons, slot.Index, BladePlacement.Hand(model, hand, root.Position, root.Facing));
        }
    }

    /// <summary>Клинки бойцов сборки мира — по позе постройки (<c>OnWorldReady</c>, как тела: <c>PlaceBuilt</c> после <c>AttachRig</c>).</summary>
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
/// PrePhysics, после капсул тела (<see cref="GameLabels.BladePlacement"/>): клинок каждого бойца — по позе этого тика в корне
/// после Movement (<c>anim.Blade(world, …)</c> в <c>PlaceBodies</c> SWORD 2eab13b), прошлый — клинок прошлого тика; сменился меч —
/// прошлое = нынешнему (<see cref="BladePlacement.Place"/>). С 7ж у живого — и тело клинка: кинематическая цель тика по той же кисти
/// (<see cref="IPhysicsWorld.SetKinematicTarget"/>: скорости — разностью за тик, как у тел плоти); сменился меч — новая форма и
/// постановка без скорости (<see cref="BladePlacement.BuildBody"/>: фантомной скорости от старого меча к новому нет, как у отрезка).
/// Мёртвый пропускается (7з): его тело клинка — на физике (меч выпал, сустав запястья выключен), клинок-отрезок стоит, где был в тик смерти
/// (удара у мёртвого нет; корень мёртвого идёт за тазом — отрезок по застывшей позе ехал бы с ним). Боец без позы или с номером меча вне
/// таблицы пропускается — его ловят <c>animation.owners</c> и <c>combat.weapons</c>. Памяти не выделяет.
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
