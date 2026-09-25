using System.Numerics;
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
/// Intent, первой (<see cref="GameLabels.SceneCommand"/>, до <c>core.player-command</c>): команды сцены тика по порядку
/// (<see cref="SceneCommand"/>; постановки сцен s1–s8 <c>RagdollScene</c> SWORD 2eab13b — идеи, не образец). Стадия Intent — после
/// барьера Begin: арена уже после своих команд (снятое место не найдётся, прибавленное — найдётся), и мир меняется один раз, на
/// барьере Intent. Команды одного места в тике складываются по порядку: «убить, затем поставить» — боец жив и стоит в точке,
/// «поставить, затем убить» — стоит мёртвым; меч, заданный раньше постановки, — в руке поставленного.
/// <list type="bullet">
/// <item><see cref="SceneAction.Place"/> — постройка места заново на той же сущности (<see cref="Fighters.Rebuild"/>, как возрождение и
/// сброс): корень в точке (x, z) курсом, прошлый корень = новому, намерение — стоять, зерно и меч — прежние, таймер удара врага — новый
/// (<see cref="ArenaRules.Placed"/>); мёртвый оживает. Тело и клинок без скорости по позе постройки ставит Gameplay этого тика
/// (<c>core.body-setup</c>, <c>core.blade-setup</c> по <c>Added&lt;Fighter&gt;</c>).</item>
/// <item><see cref="SceneAction.Kill"/> — <see cref="Dead"/> с причиной <see cref="DeathCause.Command"/> живому (здоровье не трогается,
/// как <c>Die(t, null)</c> SWORD); уже мёртвому — ничего. Привязку скина снимет Gameplay этого тика (<c>core.skin-capture</c>) по позе и
/// телам прошлого тика — тела ещё не двигались.</item>
/// <item><see cref="SceneAction.ShiftBodies"/> — тела мёртвого (метка смерти уже стояла до тика) сдвигаются на вектор без скорости
/// (<see cref="IPhysicsWorld.Place"/> — в любом режиме, и тела трупа на физике; поворот прежний) — сразу, в Intent; живому и убитому
/// этим же тиком — ничего.</item>
/// <item><see cref="SceneAction.Weapon"/> — меч живого: врагу — <see cref="WeaponSlot"/>, игроку — выбор в инвентаре (<see cref="PlayerInventory"/>;
/// нет меча в инвентаре — ничего) и меч в руке; номер вне таблицы — ничего. Клинок и тело клинка сменятся в PrePhysics (новая форма, без
/// скорости).</item>
/// </list>
/// Намерение игрока (<see cref="SceneAction.PlayerIntent"/>) — не здесь, а в Gameplay (<see cref="ScenePlayerIntentSystem"/>).
/// Причина (<c>state.why</c>) — каждая команда своих изменений.
/// </summary>
public sealed class SceneCommandSystem(IPhysicsWorld physics, int weapons) : IContinuousSystem
{
    private const int Slots = ArenaRules.MaxFighters;
    private Query _fighters = null!;
    private readonly Entity[] _bySlot = new Entity[Slots];
    /// <summary>Мёртв ли боец места с учётом команд тика до этой (итог — на барьере).</summary>
    private readonly bool[] _dead = new bool[Slots];
    /// <summary>Меч места, заданный командой тика до этой; −1 — не задавался.</summary>
    private readonly int[] _weapon = new int[Slots];

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.SceneCommand).Before(GameLabels.PlayerCommand).ReadsJournal<Event<SceneCommand>>().Reads<FighterSlot>()
            .Reads<FighterSeed>().Reads<WeaponSlot>().Reads<GazeTarget>().Reads<AnimatorAction>().Reads<Dead>().Reads<BodyRef>()
            .Adds<Dead>().SetsResource<PlayerInventory>().DeclaresRebuild();
        _fighters = s.Query().With<Fighter>().Read<FighterSlot>();
    }

    public void Run(in SystemContext ctx)
    {
        var commands = ctx.Journal<Event<SceneCommand>>();
        if (commands.IsEmpty) return;
        var world = ctx.World;
        Array.Clear(_bySlot);
        foreach (var chunk in ctx.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++)
                if ((uint)slots[i].Index < Slots && _bySlot[slots[i].Index].IsNull) _bySlot[slots[i].Index] = entities[i];
        }
        for (int slot = 0; slot < Slots; slot++)
        {
            _dead[slot] = !_bySlot[slot].IsNull && world.Has<Dead>(_bySlot[slot]);
            _weapon[slot] = -1;
        }
        var before = world.Resource<PlayerInventory>();
        var inventory = before;
        foreach (var c in commands)
        {
            ctx.Cause = c.Cause;
            var command = c.Value;
            if ((uint)command.Slot >= Slots || _bySlot[command.Slot].IsNull) continue;   // места без бойца — ничего
            var e = _bySlot[command.Slot];
            switch (command.Action)
            {
                case SceneAction.Place:
                    Place(ctx, e, command, inventory);
                    break;
                case SceneAction.Kill:
                    Kill(ctx, e, command.Slot);
                    break;
                case SceneAction.ShiftBodies:
                    Shift(world, e, command);
                    break;
                case SceneAction.Weapon:
                    Weapon(ctx, e, command, ref inventory);
                    break;
            }
        }
        if (inventory.Equipped != before.Equipped) ctx.Commands.SetResource(inventory);
    }

    /// <summary>Постройка места заново в точке (<see cref="SceneAction.Place"/>).</summary>
    private void Place(in SystemContext ctx, Entity e, in SceneCommand c, in PlayerInventory inventory)
    {
        if (!float.IsFinite(c.Position.X) || !float.IsFinite(c.Position.Y) || !float.IsFinite(c.Facing)) return;
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        float seed = world.TryGet<FighterSeed>(e, out var s) ? s.Value : 0;
        int weapon = c.Slot == 0 ? inventory.Equipped
            : _weapon[c.Slot] >= 0 ? _weapon[c.Slot]
            : world.TryGet<WeaponSlot>(e, out var w) ? w.Index : 0;
        var spawn = ArenaRules.Placed(world.Resource<SimRandom>(), time.Tick, c.Slot, c.Position, c.Facing, seed, weapon);
        var gaze = c.Slot == 0 && world.TryGet<GazeTarget>(e, out var g) ? g : default;
        var action = world.TryGet<AnimatorAction>(e, out var a) ? a : AnimatorAction.None;
        Fighters.Rebuild(ctx.Commands, e, spawn, Fighters.Standing(spawn), gaze, action, dead: _dead[c.Slot]);
        _dead[c.Slot] = false;
        _weapon[c.Slot] = weapon;
    }

    /// <summary>Смерть без удара (<see cref="SceneAction.Kill"/>): причина — команда; уже мёртвому — ничего.</summary>
    private void Kill(in SystemContext ctx, Entity e, int slot)
    {
        if (_dead[slot]) return;
        _dead[slot] = true;
        ctx.Commands.Add(e, new Dead { Time = ctx.World.Resource<SimTime>().Time, Cause = DeathCause.Command });
        ctx.Causality?.Link(e, CombatCauses.Death, ctx.Commands.LastCause);   // родитель возрождения
    }

    /// <summary>Сдвиг тел трупа без скорости (<see cref="SceneAction.ShiftBodies"/>) — сразу, в подсистеме.</summary>
    private void Shift(World world, Entity e, in SceneCommand c)
    {
        var offset = c.Offset;
        if (!world.Has<Dead>(e) || !_dead[c.Slot] || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z)) return;
        if (!world.TryGet<BodyRef>(e, out var body) || !BodyPlacement.Placeable(physics, FighterBodies.Handle(body))) return;
        var a = FighterBodies.Handle(body);
        for (int k = 0; k < physics.BodyCount(a); k++)
        {
            var b = physics.Body(a, k);
            var state = physics.Get(b);
            physics.Place(b, state.Position + offset, state.Rotation);
        }
    }

    /// <summary>Меч места (<see cref="SceneAction.Weapon"/>): живому; игроку — через инвентарь.</summary>
    private void Weapon(in SystemContext ctx, Entity e, in SceneCommand c, ref PlayerInventory inventory)
    {
        if (_dead[c.Slot] || (uint)c.Weapon >= (uint)weapons) return;
        if (c.Slot == 0 && !inventory.Equip(c.Weapon)) return;   // нет такого меча у игрока — ничего (Inventory.Equip SWORD)
        _weapon[c.Slot] = c.Weapon;
        ctx.Commands.Set(e, new WeaponSlot { Index = c.Weapon });
    }
}

/// <summary>
/// Gameplay, после команды врагу и до курса и разгона (<see cref="GameLabels.SceneIntent"/>): намерение игрока командой сцены
/// (<see cref="SceneAction.PlayerIntent"/>) — желаемая скорость и курс-цель поверх того, что записала команда игрока (Intent), как
/// <c>core.enemy-command</c> врагу; несколько в тике — последняя; нечисловая скорость — стоять, нечисловой курс — курс-цель команды
/// игрока. Мёртвому — ничего. Сцена s3 SWORD — идти сквозь труп с заданной скоростью (<c>DesiredVel = (0; 0; 4,2)</c>).
/// </summary>
public sealed class ScenePlayerIntentSystem : IContinuousSystem
{
    private Query _player = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.SceneIntent).After(GameLabels.EnemyCommand).Before(GameLabels.RootSteer).ReadsJournal<Event<SceneCommand>>()
            .Writes<FighterCommand>();
        _player = s.Query().With<Fighter>().With<PlayerTag>().Without<Dead>().Write<FighterCommand>();
    }

    public void Run(in SystemContext ctx)
    {
        var commands = ctx.Journal<Event<SceneCommand>>();
        if (commands.IsEmpty) return;
        for (int c = commands.Length - 1; c >= 0; c--)
        {
            var command = commands[c].Value;
            if (command.Action != SceneAction.PlayerIntent || command.Slot != 0) continue;
            foreach (var chunk in ctx.Iterate(_player))
            {
                var intent = chunk.Write<FighterCommand>();
                for (int i = 0; i < intent.Length; i++) Apply(command, ref intent[i]);
            }
            return;
        }
    }

    /// <summary>Намерение из команды: скорость (x, z) — нечисловая стоять; курс-цель — нечисловой прежний.</summary>
    public static void Apply(in SceneCommand c, ref FighterCommand intent)
    {
        intent.DesiredVelocity = float.IsFinite(c.Velocity.X) && float.IsFinite(c.Velocity.Y) ? new Vector3(c.Velocity.X, 0, c.Velocity.Y) : Vector3.Zero;
        if (float.IsFinite(c.Facing)) intent.FacingGoal = c.Facing;
    }
}
