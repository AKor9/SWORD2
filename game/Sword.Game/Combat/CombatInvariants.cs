using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Инварианты боя: <c>combat.blade</c> — после PrePhysics; <c>combat.weapons</c> и <c>combat.control</c> — в конце End.
/// </summary>
public static class CombatInvariants
{
    /// <summary><c>combat.blade</c>: основание и кончик — не дальше этого от кисти × клинок меча, м (постановка — те же выражения, в исправной игре 0).</summary>
    public const float Tolerance = 1e-4f;

    public static IEnumerable<IInvariant> For(PosePool poses, WeaponTable weapons, int hand, StartPoseModel start, StrikeTable strikes) =>
        [new BladeOnHand(poses, weapons, hand, start), new Weapons(weapons), new Control(strikes)];

    /// <summary>
    /// <c>combat.blade</c> — у бойца с живой позой клинок посчитан мечом в руке, точки конечны, основание и кончик — клинок меча в
    /// системе кисти позы тика (<see cref="BladePlacement.Of"/>). Посреди тика до PrePhysics не проверяется; до первого тика —
    /// сверка с позой постройки; мёртвые — нет (их держит <c>combat.dead</c>).
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
            _query ??= world.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<Transform>().Read<PoseRef>().Read<WeaponSlot>().Read<Blade>();
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
                    // меч из отрубленной руки выпал — клинок не на кисти законно
                    if (world.TryGet<Severance>(entities[i], out var severance) && severance.IsDetached(BodySegs.Weapon)) continue;
                    var model = built ? start.Of(StartPoseModel.For(world, entities[i])) : poses.Model(pose);   // своя поза постройки или общая
                    if (Problem(model, table[weapon], weapon, roots[i], blades[i]) is not { } problem) continue;
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

    /// <summary>
    /// <c>combat.control</c> — у игрока есть управление и связка, у врага — таймер удара; буферы, перезарядка, неуязвимость, время и
    /// прибавка рывка — конечны и в пределах правил <c>combat</c>; следующий удар связки — рубящий; таймер врага не меньше 0.
    /// </summary>
    private sealed class Control(StrikeTable strikes) : IInvariant
    {
        private ReadQuery? _players, _enemies, _barePlayers, _bareEnemies;

        public string Id => "combat.control";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            var rules = world.Resource<CombatRules>();
            _barePlayers ??= world.Query().With<Fighter>().With<PlayerTag>();
            foreach (var chunk in world.Iterate(_barePlayers))
                foreach (var e in chunk.Entities)
                {
                    if (world.Has<PlayerControl>(e) && world.Has<AttackCombo>(e)) continue;
                    violations.Report(e, "игрок без управления боем (PlayerControl и AttackCombo)");
                    if (violations.Full) return;
                }
            _bareEnemies ??= world.Query().With<Fighter>().With<EnemyTag>().Without<EnemyAttack>();
            foreach (var chunk in world.Iterate(_bareEnemies))
                foreach (var e in chunk.Entities)
                {
                    violations.Report(e, "враг без таймера удара (EnemyAttack)");
                    if (violations.Full) return;
                }
            _players ??= world.Query().Read<PlayerControl>().Read<AttackCombo>();
            foreach (var chunk in world.Iterate(_players))
            {
                var entities = chunk.Entities;
                var control = chunk.Read<PlayerControl>();
                var combo = chunk.Read<AttackCombo>();
                for (int i = 0; i < entities.Length; i++)
                {
                    if (Problem(control[i], combo[i], rules) is not { } problem) continue;
                    violations.Report(entities[i], problem);
                    if (violations.Full) return;
                }
            }
            _enemies ??= world.Query().Read<EnemyAttack>();
            foreach (var chunk in world.Iterate(_enemies))
            {
                var entities = chunk.Entities;
                var timers = chunk.Read<EnemyAttack>();
                for (int i = 0; i < entities.Length; i++)
                {
                    if (float.IsFinite(timers[i].Timer) && timers[i].Timer >= 0) continue;
                    violations.Report(entities[i], Text($"таймер удара {timers[i].Timer}"));
                    if (violations.Full) return;
                }
            }
        }

        private string? Problem(in PlayerControl c, in AttackCombo combo, in CombatRules rules)
        {
            if (!In(c.SwingBuffer, PlayerControlRules.SwingBufferTime) || !In(c.DashBuffer, PlayerControlRules.DashBufferTime))
                return Text($"буфер удара {c.SwingBuffer} с или рывка {c.DashBuffer} с вне 0…0,3 / 0…0,2");
            if (!In(c.DashCooldown, rules.DashCooldown) || !In(c.Invulnerable, rules.DashInvulnerable))
                return Text($"перезарядка рывка {c.DashCooldown} с или неуязвимость {c.Invulnerable} с вне 0…{rules.DashCooldown} / 0…{rules.DashInvulnerable}");
            if (!float.IsFinite(c.SwingGesture.X) || !float.IsFinite(c.SwingGesture.Y)) return Text($"жест {c.SwingGesture} нечисловой");
            if (!Finite(c.Boost) || c.Boost.Length() > rules.DashSpeed * (1 + 1e-5f))
                return Text($"прибавка рывка {c.Boost} (|{c.Boost.Length()}| м/с) — нечисловая или больше {rules.DashSpeed} м/с");
            if (c.Dashing)
            {
                if (!In(c.DashElapsed, rules.DashTime)) return Text($"рывок идёт {c.DashElapsed} с — вне 0…{rules.DashTime}");
                if (!Finite(c.DashDirection) || c.DashDirection.Y != 0 || MathF.Abs(c.DashDirection.LengthSquared() - 1) > 1e-3f)
                    return Text($"направление рывка {c.DashDirection} — не единичное по земле");
            }
            else if (c.Boost != Vector3.Zero && c.DashElapsed < rules.DashTime) return Text($"рывка нет, а прибавка {c.Boost}");
            if (combo.Next < 0 || combo.Next > strikes.Slashes.Length || !float.IsFinite(combo.LastTime))
                return Text($"связка: следующий {combo.Next} из {strikes.Slashes.Length}, последний удар в {combo.LastTime} с");
            return null;
        }

        private static bool In(float value, float max) => float.IsFinite(value) && value >= 0 && value <= max;

        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Где в тике проверка «сейчас»: посреди тика — стадия игры, между тиками — нет.</summary>
internal static class Stages
{
    /// <summary>Посреди тика до стадии <paramref name="stage"/> (останов после барьера): её постановка ещё прошлого тика.</summary>
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
