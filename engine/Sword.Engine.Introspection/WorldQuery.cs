using System.Text.Json.Nodes;
using Sword.Engine.Ecs;

namespace Sword.Engine.Introspection;

/// <summary>
/// Имена сущностей для людей и агента («игрок», «боец 3»): их знает игра (Game.Agent), движок — нет. Исполняется в точке
/// обслуживания — видит мир. Имя — подсказка, не идентичность: сущность в ответах всегда и номером (<c>12v3</c>).
/// </summary>
public interface IEntityNames
{
    /// <summary>Имя сущности; null — у неё нет имени.</summary>
    string? NameOf(in ServiceContext context, Entity entity);

    /// <summary>Сущность по имени (например, <c>player</c>); null — такого имени нет.</summary>
    Entity? Resolve(in ServiceContext context, string name);

    /// <summary>Какие имена понимает <see cref="Resolve"/> (для подсказок).</summary>
    IReadOnlyList<string> Known { get; }
}

/// <summary>Сущность с копиями байтов её компонентов — ответ точки обслуживания (разбирается уже на потоке агента).</summary>
public sealed record EntityCopy(Entity Entity, string? Name, IReadOnlyList<(int Component, byte[] Bytes)> Values);

/// <summary>Страница запроса к миру: всего подходит, тик, сущности страницы.</summary>
public sealed record QueryCopy(int Total, ulong Tick, IReadOnlyList<EntityCopy> Rows);

/// <summary>
/// Запросы к миру для агента (<c>world.query</c>, <c>entity.get</c>): в точке обслуживания — только выбор сущностей и
/// копирование байтов (<see cref="Copy"/>), разбор в JSON — на потоке агента (<see cref="ToJson"/>).
/// </summary>
public static class WorldQuery
{
    /// <summary>
    /// Сущности со всеми <paramref name="with"/> и без <paramref name="without"/> (номера компонентов), страница
    /// [<paramref name="skip"/>, +<paramref name="take"/>) по возрастанию индекса; у каждой — байты компонентов
    /// <paramref name="project"/> (null — всех её компонентов).
    /// </summary>
    public static QueryCopy Copy(in ServiceContext ctx, IReadOnlyList<int> with, IReadOnlyList<int> without,
        IReadOnlyList<int>? project, int skip, int take, IEntityNames? names)
    {
        var page = new List<Entity>();
        int total = ctx.World.Select([.. with], [.. without], skip, take, page);
        var rows = new List<EntityCopy>(page.Count);
        foreach (var e in page) rows.Add(CopyEntity(ctx, e, project, names));
        return new QueryCopy(total, ctx.Tick, rows);
    }

    /// <summary>Одна сущность (null — мертва) с байтами её компонентов (или только <paramref name="project"/>).</summary>
    public static EntityCopy? CopyOne(in ServiceContext ctx, Entity e, IReadOnlyList<int>? project, IEntityNames? names) =>
        ctx.World.IsAlive(e) ? CopyEntity(ctx, e, project, names) : null;

    private static EntityCopy CopyEntity(in ServiceContext ctx, Entity e, IReadOnlyList<int>? project, IEntityNames? names)
    {
        var world = ctx.World;
        var components = new List<int>();
        world.ComponentsOf(e, components);
        var values = new List<(int, byte[])>();
        foreach (int c in components)
        {
            if (project is not null && !project.Contains(c)) continue;
            values.Add((c, world.Copy(e, c)!));
        }
        return new EntityCopy(e, names?.NameOf(ctx, e), values);
    }

    /// <summary>
    /// Сущность в JSON: <c>{entity, name?, components: {Имя: поля | true для тега}}</c>. <paramref name="fields"/> —
    /// оставить только эти поля (<c>Transform.Position</c>); null — все.
    /// </summary>
    public static JsonObject ToJson(EntityCopy row, Catalog catalog, IReadOnlyList<string>? fields = null)
    {
        var components = new JsonObject();
        foreach (var (c, bytes) in row.Values)
        {
            var info = catalog.Components[c];
            string name = catalog.DisplayName(info);
            JsonNode? value = info.IsTag ? JsonValue.Create(true) : FieldCodec.Decode(info.Type, bytes);
            if (fields is not null)
            {
                value = Project(name, value, fields);
                if (value is null) continue;
            }
            components[name] = value;
        }
        var o = new JsonObject { ["entity"] = row.Entity.ToString() };
        if (row.Name is not null) o["name"] = row.Name;
        o["components"] = components;
        return o;
    }

    /// <summary>Проекция по путям <c>Компонент</c> или <c>Компонент.Поле.Поле</c> (без учёта регистра).</summary>
    private static JsonNode? Project(string component, JsonNode? value, IReadOnlyList<string> fields)
    {
        JsonObject? kept = null;
        foreach (string path in fields)
        {
            // Имя компонента само может быть с точками (полное имя) — путь начинается с него целиком.
            if (string.Equals(path, component, StringComparison.OrdinalIgnoreCase)) return value?.DeepClone();
            if (!path.StartsWith(component + ".", StringComparison.OrdinalIgnoreCase)) continue;
            string rest = path[(component.Length + 1)..];
            JsonNode? node = value;
            foreach (string part in rest.Split('.'))
                node = node is JsonObject o ? o.FirstOrDefault(p => string.Equals(p.Key, part, StringComparison.OrdinalIgnoreCase)).Value : null;
            if (node is null) continue;
            (kept ??= [])[rest] = node.DeepClone();
        }
        return kept;
    }
}
