using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Arena;

/// <summary>
/// <c>arena.slots</c> — бойцов на арене 1 + заданное число врагов (<see cref="ArenaState.Enemies"/>), места
/// (<see cref="FighterSlot"/>) — 0…n без дыр и повторов: место 0 — игрок (<see cref="PlayerTag"/>), 1…n — враги
/// (<see cref="EnemyTag"/>). В конце тика (команды арены меняют мир в Intent). Без нарушений не выделяет памяти.
/// </summary>
public sealed class ArenaSlotsInvariant : IInvariant
{
    private ReadQuery? _fighters, _bare;
    private readonly Entity[] _owner = new Entity[ArenaRules.MaxFighters];

    public string Id => "arena.slots";
    public InvariantCost Cost => InvariantCost.EveryTick;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        var world = ctx.World;
        _fighters ??= world.Query().With<Fighter>().Read<FighterSlot>();
        _bare ??= world.Query().With<Fighter>().Without<FighterSlot>();
        Array.Clear(_owner);
        int enemies = world.Resource<ArenaState>().Enemies;
        if (enemies is < 0 or > ArenaState.MaxEnemies)
        {
            violations.Report($"ArenaState.Enemies = {enemies}: вне 0…{ArenaState.MaxEnemies}");
            return;
        }
        int fighters = 0;
        foreach (var chunk in world.Iterate(_bare))
            foreach (var e in chunk.Entities)
            {
                fighters++;
                violations.Report(e, "боец без места (FighterSlot)");
                if (violations.Full) return;
            }
        foreach (var chunk in world.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>();
            for (int i = 0; i < entities.Length; i++)
            {
                fighters++;
                var e = entities[i];
                int slot = slots[i].Index;
                bool player = world.Has<PlayerTag>(e), enemy = world.Has<EnemyTag>(e);
                if (slot < 0 || slot > enemies) violations.Report(e, $"место {slot} вне 0…{enemies} (врагов задано {enemies})");
                else if (!_owner[slot].IsNull) violations.Report(e, $"место {slot} — ещё и у {_owner[slot]}");
                else if (slot == 0 ? !player || enemy : !enemy || player)
                    violations.Report(e, slot == 0 ? "место 0 — не игрок (PlayerTag без EnemyTag)" : $"место {slot} — не враг (EnemyTag без PlayerTag)");
                else
                {
                    _owner[slot] = e;
                    continue;
                }
                if (violations.Full) return;
            }
        }
        for (int slot = 0; slot <= enemies; slot++)
            if (_owner[slot].IsNull)
            {
                violations.Report(slot == 0 ? "нет игрока на месте 0" : $"место {slot} пусто (врагов задано {enemies})");
                if (violations.Full) return;
            }
        if (fighters != enemies + 1) violations.Report($"бойцов {fighters}, а должно быть 1 + {enemies}");
    }
}
