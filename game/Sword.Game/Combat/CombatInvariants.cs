using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Инварианты боя (раздел 8 плана; этап 6б), их регистрирует ядро: <c>combat.blade</c> — после PrePhysics (клинок на кисти),
/// <c>combat.weapons</c> — в конце End (меч и клинок у каждого бойца, инвентарь игрока согласован).
/// </summary>
public static class CombatInvariants
{
    /// <summary><c>combat.blade</c>: основание и кончик — не дальше этого от кисти × клинок меча, м (постановка — те же выражения, в исправной игре 0).</summary>
    public const float Tolerance = 1e-4f;

    public static IEnumerable<IInvariant> For(PosePool poses, WeaponTable weapons, int hand, StartPoseModel start) =>
        [new BladeOnHand(poses, weapons, hand, start), new Weapons(weapons)];

    /// <summary>
    /// <c>combat.blade</c> — «клинок на кисти» (после PrePhysics): у бойца с живой позой клинок (<see cref="Blade"/>) посчитан мечом
    /// в руке (<see cref="Blade.Weapon"/> = <see cref="WeaponSlot"/>), все четыре точки конечны, а основание и кончик — это клинок
    /// меча в системе кисти hand_r позы тика в мире бойца (<see cref="BladePlacement.Of"/>). Ловит постановку, которая не прошла
    /// (клинок остался на прошлой позе, корне или мече), клинок чужого бойца, сдвинутый после постановки, нечисловой. Посреди
    /// тика до PrePhysics (проверка «сейчас») — не проверяется: клинок ещё прошлого тика; до первого тика — сверка с позой постройки
    /// (клинки бойцов сборки мира поставила сборка мира, как тела).
    /// </summary>
    private sealed class BladeOnHand(PosePool poses, WeaponTable weapons, int hand, StartPoseModel start) : IInvariant
    {
        private ReadQuery? _query;

        public string Id => "combat.blade";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.PrePhysics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (Stages.MidTickBefore(ctx, GameStages.PrePhysics)) return;
            var world = ctx.World;
            bool built = Stages.BeforeFirstTick(ctx);
            var buildModel = built ? start.Of(world.Resource<StartPose>()) : default;
            _query ??= world.Query().With<Fighter>().Read<Transform>().Read<PoseRef>().Read<WeaponSlot>().Read<Blade>();
            var table = weapons.Weapons;
            foreach (var chunk in world.Iterate(_query))
            {
                var entities = chunk.Entities;
                var roots = chunk.Read<Transform>();
                var refs = chunk.Read<PoseRef>();
                var slots = chunk.Read<WeaponSlot>();
                var blades = chunk.Read<Blade>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var pose = new PoseHandle(refs[i].Index, refs[i].Generation);
                    int weapon = slots[i].Index;
                    if (!poses.IsAlive(pose) || (uint)weapon >= (uint)table.Length) continue;   // их ловят animation.owners, combat.weapons
                    if (Problem(built ? buildModel : poses.Model(pose), table[weapon], weapon, roots[i], blades[i]) is not { } problem) continue;
                    violations.Report(entities[i], problem);
                    if (violations.Full) return;
                }
            }
        }

        private string? Problem(ReadOnlySpan<Matrix4x4> model, in WeaponInfo info, int weapon, in Transform root, in Blade blade)
        {
            if (!Finite(blade.Base) || !Finite(blade.Tip) || !Finite(blade.PrevBase) || !Finite(blade.PrevTip))
                return Text($"клинок нечисловой: основание {blade.Base}, кончик {blade.Tip}, прошлые {blade.PrevBase}, {blade.PrevTip}");
            if (blade.Weapon != weapon)
                return blade.Weapon < 0 ? $"клинок не поставлен, а в руке {info.Name}"
                    : $"клинок посчитан мечом {blade.Weapon}, а в руке {weapon} ({info.Name})";
            var (b, t) = BladePlacement.Of(model, hand, info, root.Position, root.Facing);
            float offBase = Vector3.Distance(b, blade.Base), offTip = Vector3.Distance(t, blade.Tip);
            if (offBase <= Tolerance && offTip <= Tolerance) return null;
            return Text($"клинок {info.Name} не на кисти: основание в {offBase:G4} м, кончик в {offTip:G4} м от hand_r × клинок меча");
        }

        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>combat.weapons</c> — оружие согласовано (в конце End): у каждого бойца есть меч (<see cref="WeaponSlot"/>) из таблицы и
    /// клинок (<see cref="Blade"/>); инвентарь игрока (<see cref="PlayerInventory"/>) — только мечи таблицы, не пуст, выбранный —
    /// из доступных; меч в руке игрока — выбранный в инвентаре.
    /// </summary>
    private sealed class Weapons(WeaponTable weapons) : IInvariant
    {
        private ReadQuery? _fighters, _bare;

        public string Id => "combat.weapons";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            var inventory = world.Resource<PlayerInventory>();
            ulong table = weapons.Count == PlayerInventory.MaxWeapons ? ~0UL : (1UL << weapons.Count) - 1;
            if (inventory.Owned == 0 || (inventory.Owned & ~table) != 0 || !inventory.Has(inventory.Equipped))
            {
                violations.Report(string.Create(CultureInfo.InvariantCulture,
                    $"инвентарь игрока: доступно 0x{inventory.Owned:X}, выбран {inventory.Equipped} — мечей в таблице {weapons.Count}"));
                if (violations.Full) return;
            }
            _bare ??= world.Query().With<Fighter>().Without<WeaponSlot>();
            foreach (var chunk in world.Iterate(_bare))
                foreach (var e in chunk.Entities)
                {
                    violations.Report(e, "боец без меча (WeaponSlot)");
                    if (violations.Full) return;
                }
            _fighters ??= world.Query().With<Fighter>().Read<WeaponSlot>();
            foreach (var chunk in world.Iterate(_fighters))
            {
                var entities = chunk.Entities;
                var slots = chunk.Read<WeaponSlot>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    int weapon = slots[i].Index;
                    string? problem = (uint)weapon >= (uint)weapons.Count ? $"меч {weapon} вне таблицы оружия ({weapons.Count})"
                        : !world.Has<Blade>(e) ? "боец без клинка (Blade)"
                        : world.Has<PlayerTag>(e) && weapon != inventory.Equipped
                            ? $"в руке игрока {weapons.Weapons[weapon].Name}, а в инвентаре выбран {inventory.Equipped}"
                            : null;
                    if (problem is null) continue;
                    violations.Report(e, problem);
                    if (violations.Full) return;
                }
            }
        }
    }
}

/// <summary>Где в тике проверка «сейчас» (<c>invariant.run</c>): посреди тика — стадия игры, между тиками — нет.</summary>
internal static class Stages
{
    /// <summary>Посреди тика до стадии <paramref name="stage"/> (после <c>time.break</c>): её постановка ещё прошлого тика.</summary>
    public static bool MidTickBefore(in InvariantContext ctx, string stage)
    {
        var stages = GameStages.All;
        int target = -1, current = -1;
        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i] == stage) target = i;
            if (stages[i] == ctx.Stage) current = i;
        }
        return current >= 0 && current < target;
    }

    /// <summary>Между тиками (не стадия игры) до первого тика: бойцы сборки мира стоят по позе постройки.</summary>
    public static bool BeforeFirstTick(in InvariantContext ctx)
    {
        var stages = GameStages.All;
        for (int i = 0; i < stages.Count; i++)   // индексом: foreach по IReadOnlyList выделил бы перечислитель в каждом тике
            if (stages[i] == ctx.Stage) return false;
        return ctx.Tick == 0;
    }
}
