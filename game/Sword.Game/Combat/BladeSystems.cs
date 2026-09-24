using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Клинок бойца в мире (<c>FighterAnimator.Blade</c> и клинок в <c>DuelArena.PlaceBodies</c> SWORD 2eab13b; этап 6б): модельная
/// матрица кисти позы в мире бойца (<see cref="BodyPlacement.World"/> — корень, курс, высота рельефа: тот же мир, что у капсул
/// тела, «как показан боец»), основание и кончик клинка меча — в её системе. Масштаба клинка игрока (<c>SWORD_PLAYER_BLADE</c>,
/// тест разреза SWORD) нет.
/// </summary>
public static class BladePlacement
{
    /// <summary>Основание и кончик клинка <paramref name="weapon"/> в кисти <paramref name="hand"/> позы <paramref name="model"/>.</summary>
    public static (Vector3 Base, Vector3 Tip) Of(ReadOnlySpan<Matrix4x4> model, int hand, in WeaponInfo weapon, Vector3 root, float facing)
    {
        var world = model[hand] * BodyPlacement.World(root, facing);
        return (Vector3.Transform(weapon.BladeBase, world), Vector3.Transform(weapon.BladeTip, world));
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
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c>, после постройки тела и до курса (<see cref="GameLabels.BladeSetup"/>):
/// построенному бойцу — клинок по позе постройки (<see cref="StartPose"/>) в корне и курсе постройки, без прошлого
/// (<c>PlaceBuilt</c> → <c>PlaceBodies(f, 0)</c> SWORD: <c>first</c> — <c>KinPrevBase = bladeBase</c>): первая постановка тика
/// после постройки заметает клинок от позы постройки к позе тика, как в SWORD. Ставится командой (на барьере Gameplay).
/// Бойцов сборки мира ставит сборка мира (<see cref="PlaceWorldBuilt"/>); тик 1 ставит их ещё раз теми же значениями.
/// </summary>
public sealed class BladeSetupSystem(WeaponTable weapons, int hand, StartPoseModel start) : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.BladeSetup).After(GameLabels.BodySetup).Before(GameLabels.RootSteer).Reads<Transform>().Reads<WeaponSlot>()
            .Sets<Blade>().NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        var model = start.Of(world.Resource<StartPose>());
        foreach (var a in added)
        {
            // Удалён на том же барьере, без клинка или с чужим номером меча (его ловит combat.weapons) — ставить нечего.
            if (!world.IsAlive(a.Entity) || !world.Has<Blade>(a.Entity) || !world.TryGet<Transform>(a.Entity, out var root)
                || !world.TryGet<WeaponSlot>(a.Entity, out var slot) || (uint)slot.Index >= (uint)weapons.Count)
                continue;
            ctx.Cause = a.Cause;   // причина постройки бойца — родитель клинка (state.why, 6д)
            ctx.Commands.Set(a.Entity, BladePlacement.Built(model, hand, weapons, slot.Index, root.Position, root.Facing));
        }
    }

    /// <summary>Клинки бойцов сборки мира — по позе постройки (<c>OnWorldReady</c>, как тела: <c>PlaceBuilt</c> после <c>AttachRig</c>).</summary>
    public static void PlaceWorldBuilt(World world, WeaponTable weapons, int hand, StartPoseModel start)
    {
        var model = start.Of(world.Resource<StartPose>());
        var built = new List<(Entity Entity, Blade Blade)>();
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().With<Blade>().Read<Transform>().Read<WeaponSlot>()))
        {
            var roots = chunk.Read<Transform>();
            var slots = chunk.Read<WeaponSlot>();
            for (int i = 0; i < chunk.Count; i++)
                if ((uint)slots[i].Index < (uint)weapons.Count)
                    built.Add((chunk.Entities[i], BladePlacement.Built(model, hand, weapons, slots[i].Index, roots[i].Position, roots[i].Facing)));
        }
        foreach (var (e, blade) in built) world.SetImmediate(e, blade);
    }
}

/// <summary>
/// PrePhysics, после капсул тела (<see cref="GameLabels.BladePlacement"/>): клинок каждого бойца — по позе этого тика в корне
/// после Movement (<c>anim.Blade(world, …)</c> в <c>PlaceBodies</c> SWORD 2eab13b), прошлый — клинок прошлого тика; сменился меч —
/// прошлое = нынешнему (<see cref="BladePlacement.Place"/>). Физического тела клинка нет (этап 7). Боец без позы или с
/// номером меча вне таблицы пропускается — его ловят <c>animation.owners</c> и <c>combat.weapons</c>. Памяти не выделяет.
/// </summary>
public sealed class BladePlacementSystem(WeaponTable weapons, int hand) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.BladePlacement).After(GameLabels.KinematicPlacement).Reads<Transform>().Reads<PoseRef>().Reads<WeaponSlot>()
            .Writes<Blade>();
        _query = s.Query().With<Fighter>().Read<Transform>().Read<PoseRef>().Read<WeaponSlot>().Write<Blade>();
    }

    public void Run(in SystemContext ctx)
    {
        var pool = ctx.World.Resource<PosePool>();
        var table = weapons.Weapons;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var blades = chunk.Write<Blade>();
            var roots = chunk.Read<Transform>()[..blades.Length];
            var poses = chunk.Read<PoseRef>()[..blades.Length];
            var slots = chunk.Read<WeaponSlot>()[..blades.Length];
            for (int i = 0; i < blades.Length; i++)
            {
                var pose = new PoseHandle(poses[i].Index, poses[i].Generation);
                int weapon = slots[i].Index;
                if (!pool.IsAlive(pose) || (uint)weapon >= (uint)table.Length) continue;
                var (b, t) = BladePlacement.Of(pool.Model(pose), hand, table[weapon], roots[i].Position, roots[i].Facing);
                BladePlacement.Place(ref blades[i], b, t, weapon);
            }
        }
    }
}
