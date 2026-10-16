using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Arena;

/// <summary>
/// Begin, реактивная по <c>Event&lt;ArenaCommand&gt;</c>: команды арены тика по порядку — число врагов, сброс, меч игрока.
/// Команды сначала сводятся в итог, затем мир меняется один раз: боец, построенный и снятый в одном тике, не создаётся вовсе.
/// Занятое место строится заново на той же сущности (<see cref="Fighters.Rebuild"/>), новое — новая сущность. Intent этого тика
/// уже видит арену после команд. Сброс сохраняет точку взгляда игрока и обнуляет <see cref="ArenaStats"/>.
/// </summary>
public sealed class ArenaCommandSystem(int[] weapons) : IReactiveSystem<Event<ArenaCommand>>
{
    private Query _fighters = null!;
    private readonly Entity[] _bySlot = new Entity[ArenaRules.MaxFighters];
    /// <summary>Партия постройки места в этом тике (номер команды с 1); 0 — место не строится заново.</summary>
    private readonly uint[] _batch = new uint[ArenaRules.MaxFighters];
    private readonly Vector3[] _center = new Vector3[ArenaRules.MaxFighters];

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.ArenaCommand).After(GameLabels.PrevTransform).Reads<FighterSlot>().Reads<Transform>().Reads<GazeTarget>()
            .Reads<AnimatorAction>()
            .SetsResource<ArenaState>().SetsResource<PlayerInventory>().SetsResource<ArenaStats>().DeclaresFighters();
        _fighters = s.Query().With<Fighter>().Read<FighterSlot>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<Event<ArenaCommand>> commands)
    {
        var world = ctx.World;
        var state = world.Resource<ArenaState>();
        var before = world.Resource<PlayerInventory>();
        Array.Clear(_bySlot);
        Array.Clear(_batch);
        foreach (var chunk in ctx.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++)
                if ((uint)slots[i].Index < ArenaRules.MaxFighters) _bySlot[slots[i].Index] = entities[i];
        }
        var player = _bySlot[0];
        var center = world.TryGet<Transform>(player, out var root) ? root.Position with { Y = 0 } : Vector3.Zero;

        int enemies = state.Enemies;
        uint resetBatch = 0;
        var inventory = before;
        for (int c = 0; c < commands.Length; c++)
        {
            var command = commands[c].Value;
            uint batch = (uint)c + 1;
            switch (command.Action)
            {
                case ArenaAction.ChangeEnemies:
                    enemies = Resize(enemies, ArenaRules.ClampEnemies((long)enemies + command.Enemies), batch, center);
                    break;
                case ArenaAction.SetEnemies:
                    enemies = Resize(enemies, ArenaRules.ClampEnemies(command.Enemies), batch, center);
                    break;
                case ArenaAction.Reset:
                    resetBatch = batch;
                    center = Vector3.Zero;   // игрок заново в центре — враги вокруг него
                    for (int slot = 1; slot <= enemies; slot++) Mark(slot, batch, center);
                    break;
                case ArenaAction.NextWeapon:
                    inventory.EquipNext();
                    break;
                case ArenaAction.EquipWeapon:
                    inventory.Equip(command.Weapon);   // такого меча у игрока нет — ничего
                    break;
            }
        }
        var cmd = ctx.Commands;
        if (inventory.Equipped != before.Equipped || inventory.Owned != before.Owned) cmd.SetResource(inventory);
        // Меч в руке игрока — меч инвентаря (сброс строит игрока сразу с ним).
        if (resetBatch == 0 && !player.IsNull && inventory.Equipped != before.Equipped)
            cmd.Set(player, new WeaponSlot { Index = inventory.Equipped });
        if (enemies == state.Enemies && resetBatch == 0 && !AnyMarked()) return;
        // Врагов прибавилось — запас пула поз добирается здесь, до постройки новых бойцов (их позы — в Gameplay этого тика,
        // слои боя — позже): рост пула посреди боя не выделяет памяти. Разовое выделение — в тике команды арены.
        if (enemies > state.Enemies) GameCore.ReservePoses(world.Resource<PosePool>(), enemies);

        var rng = world.Resource<SimRandom>();
        ulong tick = world.Resource<SimTime>().Tick;
        if (resetBatch != 0)
        {
            var playerSpawn = ArenaRules.Player(rng, tick, resetBatch, inventory.Equipped);
            var gaze = world.TryGet<GazeTarget>(player, out var g) ? g : default;
            if (player.IsNull) Fighters.Create(cmd, playerSpawn, Fighters.Standing(playerSpawn), gaze);
            else Fighters.Rebuild(cmd, player, playerSpawn, Fighters.Standing(playerSpawn), gaze, Action(world, player), dead: world.Has<Dead>(player));
            cmd.SetResource(ArenaStats.Start);   // сброс — статистика и последний удар с нуля
        }
        for (int slot = 1; slot < ArenaRules.MaxFighters; slot++)
        {
            var e = _bySlot[slot];
            if (slot > enemies)
            {
                if (!e.IsNull) cmd.Destroy(e);   // убавили — последние места уходят
                continue;
            }
            if (_batch[slot] == 0) continue;
            var spawn = ArenaRules.Enemy(rng, tick, _batch[slot], slot, _center[slot], weapons);
            if (e.IsNull) Fighters.Create(cmd, spawn, Fighters.Standing(spawn), default);
            else Fighters.Rebuild(cmd, e, spawn, Fighters.Standing(spawn), default, Action(world, e), dead: world.Has<Dead>(e));
        }
        if (enemies != state.Enemies) cmd.SetResource(new ArenaState { Enemies = enemies });
    }

    /// <summary>Слой действий бойца до постройки заново (номер удара и счёт снимков переживают её); нет — без действия.</summary>
    private static AnimatorAction Action(World world, Entity e) => world.TryGet<AnimatorAction>(e, out var a) ? a : AnimatorAction.None;

    /// <summary>Новое число врагов: прибавленные места строятся (от нынешнего центра), убавленные — забываются.</summary>
    private int Resize(int old, int n, uint batch, Vector3 center)
    {
        for (int slot = old + 1; slot <= n; slot++) Mark(slot, batch, center);
        for (int slot = n + 1; slot <= old; slot++) _batch[slot] = 0;
        return n;
    }

    private void Mark(int slot, uint batch, Vector3 center)
    {
        _batch[slot] = batch;
        _center[slot] = center;
    }

    private bool AnyMarked()
    {
        foreach (uint b in _batch)
            if (b != 0) return true;
        return false;
    }
}
