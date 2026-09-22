using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Агентский доступ игры (раздел 8 плана, Game.Agent): то, что знает только игра, — имена сущностей («player» —
/// игрок, «fighter N» — боец с номером N). Инструменты движка берут их для ответов (<c>world.query</c>,
/// <c>entity.get</c>) и для пакета наблюдения. Ядро игры об агенте не знает.
/// </summary>
public static class SwordAgent
{
    public static void Register(AgentHost host)
    {
        host.Names = new ArenaNames();
        AnimationTools.Register(host);
    }

    /// <summary>Имена бойцов арены.</summary>
    public sealed class ArenaNames : IEntityNames
    {
        public IReadOnlyList<string> Known { get; } = ["player", "fighter <номер>"];

        public string? NameOf(in ServiceContext context, Entity entity)
        {
            var world = context.World;
            if (!world.Knows<Fighter>() || !world.Has<Fighter>(entity)) return null;
            return world.Knows<PlayerTag>() && world.Has<PlayerTag>(entity) ? "player" : $"fighter {entity.Index}";
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
