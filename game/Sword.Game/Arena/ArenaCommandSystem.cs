using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
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
/// Враги по той же причине думают уже по новой арене (в SWORD <c>SetEnemyCount</c> и <c>Reset</c> — до <c>EnemyAi.Think</c>
/// того же тика): в тике, где мир меняется, намерение всех врагов 1…n — ИИ (<see cref="EnemyCrowd"/>) по корням после
/// команд (построенные — точкой спауна, прежние — своим корнем, ушедших нет); прежним оно ставится <c>Set</c> и на
/// барьере ложится поверх записи <see cref="EnemyAiSystem"/>, которая видела арену до команд.
/// </summary>
public sealed class ArenaCommandSystem : IReactiveSystem<Event<ArenaCommand>>
{
    private Query _fighters = null!;
    private readonly Entity[] _bySlot = new Entity[ArenaRules.MaxFighters];
    /// <summary>Партия постройки места в этом тике (номер команды с 1); 0 — место не строится заново.</summary>
    private readonly uint[] _batch = new uint[ArenaRules.MaxFighters];
    private readonly Vector3[] _center = new Vector3[ArenaRules.MaxFighters];
    private readonly FighterSpawn[] _spawn = new FighterSpawn[ArenaRules.MaxFighters];
    private readonly EnemyCrowd _crowd = new();

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.ArenaCommand).Before(GameLabels.PlayerCommand).ReadsJournal<Event<PlayerCommand>>()
            .Reads<FighterSlot>().Reads<Transform>().Reads<GazeTarget>().Reads<FighterSeed>().SetsResource<ArenaState>()
            .DeclaresFighters();
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
        var time = world.Resource<SimTime>();
        ulong tick = time.Tick;
        var cmd = ctx.Commands;

        // Арена после команд — для ИИ врагов этого тика: игрок (сброшен — в точке спауна), враги 1…n.
        _crowd.Clear();
        var playerSpawn = ArenaRules.Player(rng, tick, resetBatch);
        if (resetBatch != 0) _crowd.Put(0, playerSpawn.Position, playerSpawn.Seed);
        else if (!player.IsNull) _crowd.Put(0, root.Position, world.Get<FighterSeed>(player).Value);
        for (int slot = 1; slot <= enemies; slot++)
        {
            var e = _bySlot[slot];
            if (_batch[slot] != 0)
            {
                _spawn[slot] = ArenaRules.Enemy(rng, tick, _batch[slot], slot, _center[slot]);
                _crowd.Put(slot, _spawn[slot].Position, _spawn[slot].Seed);
            }
            else if (!e.IsNull) _crowd.Put(slot, world.Get<Transform>(e).Position, world.Get<FighterSeed>(e).Value);
        }

        if (resetBatch != 0)
        {
            var gaze = world.TryGet<GazeTarget>(player, out var g) ? g : default;
            var intent = Fighters.Standing(playerSpawn);
            var journal = ctx.Journal<Event<PlayerCommand>>();
            var playerCommand = journal.IsEmpty ? default : journal[^1].Value;   // несколько на тик — действует последняя
            PlayerCommandSystem.Apply(playerCommand, new Transform { Position = playerSpawn.Position, Facing = playerSpawn.Facing }, ref intent, ref gaze);
            if (player.IsNull) Fighters.Create(cmd, playerSpawn, intent, gaze);
            else Fighters.Rebuild(cmd, player, playerSpawn, intent, gaze);
        }
        for (int slot = 1; slot < ArenaRules.MaxFighters; slot++)
        {
            var e = _bySlot[slot];
            if (slot > enemies)
            {
                if (!e.IsNull) cmd.Destroy(e);   // убавили — последние места уходят
                continue;
            }
            if (_batch[slot] == 0)
            {
                if (e.IsNull) continue;
                // Прежний враг: намерение по новой арене поверх записи EnemyAiSystem; игрока нет — намерение прежнее.
                FighterCommand thought = default;
                GazeTarget look = default;
                EnemyAiTerms why = default;
                if (!_crowd.Think(slot, time.Time, ref thought, ref look, ref why)) continue;
                cmd.Set(e, thought);
                cmd.Set(e, look);
                cmd.Set(e, why);
                continue;
            }
            var spawn = _spawn[slot];
            var intent = Fighters.Standing(spawn);
            GazeTarget gaze = default;
            EnemyAiTerms terms = default;
            _crowd.Think(slot, time.Time, ref intent, ref gaze, ref terms);
            if (e.IsNull) Fighters.Create(cmd, spawn, intent, gaze, terms);
            else Fighters.Rebuild(cmd, e, spawn, intent, gaze, terms);
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
