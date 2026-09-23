using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Movement;

namespace Sword.Game.Arena;

/// <summary>
/// Intent, реактивная по <c>Event&lt;ArenaCommand&gt;</c>: команды арены тика по порядку — число врагов (<c>SetEnemyCount</c>
/// SWORD: больше — новые места old+1…n спаунятся заново; меньше — последние места уходят) и сброс (<c>Reset(EnemyCount)</c>:
/// игрок заново в центре курсом 0, все враги заново). Порядок SWORD в кадре — PageUp, PageDown, R — задаёт клиент.
///
/// Команды тика сначала сводятся в итог (сколько врагов, какие места строятся заново и от какого центра, сброшен ли
/// игрок), затем мир меняется один раз: боец, построенный и снятый в одном тике, не создаётся вовсе (черновик снять
/// нельзя). Место, у которого уже есть боец, строится заново на той же сущности (<see cref="Fighters.Rebuild"/>: как
/// <c>Fighter.Build</c> SWORD над тем же бойцом места — имя и номер у агента прежние, модули заводят состояние бойца с
/// нуля по <c>Added&lt;Fighter&gt;</c>); новое место — новая сущность, ушедшее — сущность снимается. Изменения видны с
/// барьера Intent. Игрок после сброса получает команду игрока этого тика сразу (в SWORD <c>Reset</c> — до
/// <c>PlayerController.Think</c> того же тика) и прежнюю точку взгляда (<c>_cursor</c> контроллера SWORD сброс не трогает).
/// </summary>
public sealed class ArenaCommandSystem : IReactiveSystem<Event<ArenaCommand>>
{
    private Query _fighters = null!;
    private readonly Entity[] _bySlot = new Entity[ArenaRules.MaxFighters];
    /// <summary>Партия постройки места в этом тике (номер команды с 1); 0 — место не строится заново.</summary>
    private readonly uint[] _batch = new uint[ArenaRules.MaxFighters];
    private readonly Vector3[] _center = new Vector3[ArenaRules.MaxFighters];

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.ArenaCommand).Before(GameLabels.PlayerCommand).ReadsJournal<Event<PlayerCommand>>()
            .Reads<FighterSlot>().Reads<Transform>().Reads<GazeTarget>().SetsResource<ArenaState>().DeclaresFighters();
        _fighters = s.Query().With<Fighter>().Read<FighterSlot>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<Event<ArenaCommand>> commands)
    {
        var world = ctx.World;
        var state = world.Resource<ArenaState>();
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
            }
        }
        if (enemies == state.Enemies && resetBatch == 0 && !AnyMarked()) return;

        var rng = world.Resource<SimRandom>();
        ulong tick = world.Resource<SimTime>().Tick;
        var cmd = ctx.Commands;
        if (resetBatch != 0)
        {
            var gaze = world.TryGet<GazeTarget>(player, out var g) ? g : default;
            var spawn = ArenaRules.Player(rng, tick, resetBatch);
            var intent = Fighters.Standing(spawn);
            var journal = ctx.Journal<Event<PlayerCommand>>();
            var playerCommand = journal.IsEmpty ? default : journal[^1].Value;   // несколько на тик — действует последняя
            PlayerCommandSystem.Apply(playerCommand, new Transform { Position = spawn.Position, Facing = spawn.Facing }, ref intent, ref gaze);
            if (player.IsNull) Fighters.Create(cmd, spawn, intent, gaze);
            else Fighters.Rebuild(cmd, player, spawn, intent, gaze);
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
            var spawn = ArenaRules.Enemy(rng, tick, _batch[slot], slot, _center[slot]);
            if (e.IsNull) Fighters.Create(cmd, spawn, Fighters.Standing(spawn), default);
            else Fighters.Rebuild(cmd, e, spawn, Fighters.Standing(spawn), default);
        }
        if (enemies != state.Enemies) cmd.SetResource(new ArenaState { Enemies = enemies });
    }

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
