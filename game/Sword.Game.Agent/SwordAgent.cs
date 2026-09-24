using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Агентский доступ игры (раздел 8 плана, Game.Agent): то, что знает только игра, — имена сущностей («player» —
/// игрок, «enemy N» — враг на месте N, «fighter N» — боец с номером сущности N). Инструменты движка берут их для ответов
/// (<c>world.query</c>, <c>entity.get</c>, условия) и для пакета наблюдения. Ядро игры об агенте не знает.
/// </summary>
public static class SwordAgent
{
    public static void Register(AgentHost host)
    {
        host.Names = new ArenaNames();
        host.Fields = new BodyFields();
        AnimationTools.Register(host);
        PhysicsTools.Register(host);
        CombatTools.Register(host);
    }

    /// <summary>
    /// Имена бойцов арены: игрок — «player», враг — «enemy N» по месту (<see cref="FighterSlot"/>: 1…32 — номер бойца
    /// SWORD, не номер сущности: убавленное и снова прибавленное место получает новую сущность, а место остаётся; сброс
    /// строит врага заново на той же сущности), остальные бойцы — «fighter N» по номеру сущности.
    /// </summary>
    public sealed class ArenaNames : IEntityNames
    {
        public IReadOnlyList<string> Known { get; } = ["player", "enemy <место 1…32>", "fighter <номер сущности>"];

        public string? NameOf(in ServiceContext context, Entity entity)
        {
            var world = context.World;
            if (!world.Knows<Fighter>() || !world.Has<Fighter>(entity)) return null;
            if (world.Knows<PlayerTag>() && world.Has<PlayerTag>(entity)) return "player";
            if (world.Knows<EnemyTag>() && world.Has<EnemyTag>(entity) && world.TryGet<FighterSlot>(entity, out var slot))
                return $"enemy {slot.Index}";
            return $"fighter {entity.Index}";
        }

        public Entity? Resolve(in ServiceContext context, string name)
        {
            var world = context.World;
            if (string.Equals(name, "player", StringComparison.OrdinalIgnoreCase))
            {
                if (!world.Knows<PlayerTag>()) return null;
                foreach (var chunk in world.Iterate(world.Query().With<PlayerTag>()))
                    if (chunk.Count > 0) return chunk.Entities[0];
                return null;
            }
            const string enemy = "enemy ";
            if (name.StartsWith(enemy, StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(enemy.Length), out int place)
                && world.Knows<EnemyTag>() && world.Knows<FighterSlot>())
            {
                foreach (var chunk in world.Iterate(world.Query().With<EnemyTag>().Read<FighterSlot>()))
                {
                    var slots = chunk.Read<FighterSlot>();
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i].Index == place) return chunk.Entities[i];
                }
                return null;
            }
            const string prefix = "fighter ";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(prefix.Length), out int index)
                && world.Knows<Fighter>())
                foreach (var chunk in world.Iterate(world.Query().With<Fighter>()))
                    foreach (var e in chunk.Entities)
                        if (e.Index == index) return e;
            return null;
        }
    }
}
