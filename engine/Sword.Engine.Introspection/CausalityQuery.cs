using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// Шаг цепочки причин (копия записи журнала причинности из точки обслуживания): запись, имена стадии, регистрации системы и типа,
/// тип CLR значения (компонент, событие; null — без значения или ресурс), значения до и после байтами (null — нет или вытеснены),
/// имя сущности игры.
/// </summary>
public sealed record CauseStep(CauseRecord Record, string? Stage, string? System, string? TypeName, Type? ValueType, byte[]? Before,
    byte[]? After, string? EntityName);

/// <summary>
/// Изменение поля и его цепочка до корня: <see cref="Complete"/> — дошли до записи без родителя (команда, решение); иначе
/// <see cref="Stop"/> — почему оборвалась (родитель вытеснен из кольца, глубже предела, родителя не передали).
/// </summary>
public sealed record WhyChange(CauseStep Change, IReadOnlyList<CauseStep> Chain, bool Complete, string? Stop);

/// <summary>Регистрация, писавшая колонку компонента на месте, и последний тик (грубо: столбец, не сущность).</summary>
public sealed record ColumnWriter(string System, ulong Tick);

/// <summary>
/// Ответ <c>state.why</c> (копии; разбор в JSON — на потоке агента): включён ли режим, сущность, компонент и путь поля, текущее
/// значение компонента, последние изменения поля с цепочками (точно — команды <c>Add/Set/Remove</c>), регистрации, писавшие колонку
/// на месте (грубо — непрерывные поля), объявленные производители (из плана мира), состояние кольца.
/// </summary>
public sealed record WhyAnswer(bool Enabled, Entity Entity, string? EntityName, DataTypeInfo Component, string? Field, byte[]? Current,
    IReadOnlyList<WhyChange> Changes, IReadOnlyList<ColumnWriter> Writers, IReadOnlyList<string> Producers, long Written, int Capacity,
    long Oldest, int Scanned);

/// <summary>
/// Причинность для агента (раздел 8 плана, <c>state.why</c>; 6д): поиск в журнале причинности мира (<see cref="WorldReader.Causality"/>)
/// последних изменений поля сущности и цепочек до корня. Исполняется в точке обслуживания — только копии; журнал и мир не меняются.
/// Точность — честно: дискретные факты (команды <c>Add/Set/Remove</c>, события) — точная цепочка; непрерывные поля (запись колонки на
/// месте) — только какая регистрация писала столбец в каком тике.
/// </summary>
public static class CausalityQuery
{
    /// <summary>Самая длинная цепочка и больше всего изменений в одном ответе.</summary>
    public const int MaxDepth = 64, MaxChanges = 20;

    /// <summary>
    /// Последние <paramref name="changes"/> изменений <paramref name="field"/> (точный путь внутри компонента; null — компонент целиком)
    /// у <paramref name="entity"/> и цепочка каждого не глубже <paramref name="depth"/>.
    /// </summary>
    public static WhyAnswer Why(in ServiceContext ctx, Entity entity, DataTypeInfo component, string? field, int changes, int depth,
        IEntityNames? names)
    {
        var world = ctx.World;
        var current = world.Copy(entity, component.Id);
        var infos = world.Systems;   // описание мира строится при каждом обращении — один раз на запрос
        var producers = Producers(infos, component);
        string? name = names?.NameOf(ctx, entity);
        if (world.Causality is not { } log)
            return new WhyAnswer(false, entity, name, component, field, current, [], [], producers, 0, 0, 0, 0);

        changes = Math.Clamp(changes, 1, MaxChanges);
        depth = Math.Clamp(depth, 1, MaxDepth);
        var systems = infos.ToDictionary(s => s.RegistrationId, s => s.Name);
        var leaves = field is null || component.IsTag ? null : Leaves(component.Type, field);
        var layout = leaves is null ? null : FieldLayout.Of(component.Type);
        var found = new List<WhyChange>();
        int scanned = 0;
        for (long seq = log.NextSequence - 1; seq >= log.FirstSequence && found.Count < changes; seq--)
        {
            if (!log.TryAt(seq, out var r)) continue;
            scanned++;
            if (r.Entity != entity || r.Type != component.Id || r.Kind is not (CauseKind.Add or CauseKind.Set or CauseKind.Remove)) continue;
            if (r.Kind == CauseKind.Set && layout is not null && log.ValuesKept(r) && Same(layout, leaves!, log.Before(r), log.After(r))) continue;
            found.Add(Chain(ctx, log, r, depth, systems, names));
        }
        return new WhyAnswer(true, entity, name, component, field, current, found, Writers(infos, log, component), producers,
            log.Written, log.Capacity, log.FirstSequence, scanned);
    }

    /// <summary>
    /// Цепочка записи журнала причинности по её номеру (8з; <c>state.why {cause}</c>: номер — <c>cause.id</c> или <c>cause.parent</c> записи
    /// <c>events.history</c>) до корня, не глубже <paramref name="depth"/>; режим выключен или запись вытеснена — null.
    /// </summary>
    public static WhyChange? WhyCause(in ServiceContext ctx, CauseId id, int depth, IEntityNames? names)
    {
        if (ctx.World.Causality is not { } log || !log.TryGet(id, out var r)) return null;
        return Chain(ctx, log, r, Math.Clamp(depth, 1, MaxDepth), SystemNames(ctx.World), names);
    }

    /// <summary>Имена регистраций систем по номеру — один раз на запрос (описание мира строится при каждом обращении).</summary>
    public static Dictionary<int, string> SystemNames(WorldReader world) => world.Systems.ToDictionary(s => s.RegistrationId, s => s.Name);

    /// <summary>
    /// Шаг по причине (<c>events.history</c>: причина и родитель записи журнала; <paramref name="systems"/> — <see cref="SystemNames"/>);
    /// вытеснена или режим выключен — null.
    /// </summary>
    public static CauseStep? Step(in ServiceContext ctx, CauseId id, Dictionary<int, string> systems, IEntityNames? names)
    {
        if (ctx.World.Causality is not { } log || !log.TryGet(id, out var r)) return null;
        return Step(ctx, log, r, systems, names, values: false);
    }

    private static WhyChange Chain(in ServiceContext ctx, CausalityLog log, in CauseRecord change, int depth, Dictionary<int, string> systems,
        IEntityNames? names)
    {
        var chain = new List<CauseStep>();
        var at = change;
        var seen = new HashSet<int> { change.Id.Value };
        while (true)
        {
            if (at.Parent.Value == 0) return new WhyChange(Step(ctx, log, change, systems, names, true), chain, true, null);
            if (chain.Count >= depth) return new WhyChange(Step(ctx, log, change, systems, names, true), chain, false, $"цепочка глубже {depth}");
            if (!seen.Add(at.Parent.Value) || !log.TryGet(at.Parent, out var parent))
                return new WhyChange(Step(ctx, log, change, systems, names, true), chain, false,
                    $"родитель {at.Parent.Value} вытеснен из кольца (записей {log.Capacity}) — ближе к изменению: time.seek на его тик (режим сохраняется)");
            chain.Add(Step(ctx, log, parent, systems, names, true));
            at = parent;
        }
    }

    private static CauseStep Step(in ServiceContext ctx, CausalityLog log, in CauseRecord r, Dictionary<int, string> systems, IEntityNames? names,
        bool values)
    {
        var world = ctx.World;
        string? stage = r.Stage >= 0 && r.Stage < world.Stages.Count ? world.Stages[r.Stage] : null;
        string? system = r.Registration > 0 ? systems.GetValueOrDefault(r.Registration) : null;
        (string? typeName, Type? valueType) = r.Kind switch
        {
            CauseKind.Add or CauseKind.Set or CauseKind.Remove when r.Type >= 0 && r.Type < world.Components.Count =>
                (world.Components[r.Type].Name, world.Components[r.Type].Type),
            CauseKind.Event or CauseKind.Command when r.Type >= 0 && r.Type < world.Events.Count =>
                (world.Events[r.Type].Name, world.Events[r.Type].Type),
            CauseKind.SetResource when r.Type >= 0 && r.Type < world.Resources.Count => (world.Resources[r.Type].Name, null),
            _ => ((string?)null, (Type?)null),
        };
        byte[]? before = null, after = null;
        if (values && valueType is not null && log.ValuesKept(r))
        {
            if (r.HasBefore) before = log.Before(r).ToArray();
            if (r.HasAfter) after = log.After(r).ToArray();
        }
        string? entityName = r.Entity.IsNull || r.Entity.IsDeferred ? null : names?.NameOf(ctx, r.Entity);
        return new CauseStep(r, stage, system, typeName, valueType, before, after, entityName);
    }

    /// <summary>Листья пути поля (сам лист или все листья вложенной структуры).</summary>
    private static int[] Leaves(Type type, string field)
    {
        var layout = FieldLayout.Of(type);
        var leaves = new List<int>();
        for (int i = 0; i < layout.Leaves.Count; i++)
        {
            string path = layout.Leaves[i].Path;
            if (path == field || path.StartsWith(field + ".", StringComparison.Ordinal) || path.StartsWith(field + "[", StringComparison.Ordinal))
                leaves.Add(i);
        }
        return [.. leaves];
    }

    private static bool Same(FieldLayout layout, int[] leaves, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (before.Length < layout.Size || after.Length < layout.Size) return false;
        foreach (int leaf in leaves)
            if (layout.Bits(before, leaf) != layout.Bits(after, leaf)) return false;
        return true;
    }

    /// <summary>Кто писал колонку компонента на месте (с включения журнала): регистрации по убыванию тика.</summary>
    private static IReadOnlyList<ColumnWriter> Writers(IReadOnlyList<SystemInfo> systems, CausalityLog log, DataTypeInfo component)
    {
        var writers = new List<(ulong Tick, int Order, string Name)>();
        int order = 0;
        foreach (var s in systems)
        {
            order++;
            if (log.LastWrite(component.Id, s.RegistrationId) is { } tick) writers.Add((tick, order, s.Name));
        }
        return [.. writers.OrderByDescending(w => w.Tick).ThenByDescending(w => w.Order).Select(w => new ColumnWriter(w.Name, w.Tick))];
    }

    /// <summary>Объявленные производители компонента (план мира): кто его пишет на месте, добавляет, заменяет, снимает.</summary>
    private static IReadOnlyList<string> Producers(IReadOnlyList<SystemInfo> systems, DataTypeInfo component)
    {
        var list = new List<string>();
        foreach (var s in systems)
        {
            var how = new List<string>();
            if (s.Writes.Contains(component.Name)) how.Add("Write");
            if (s.Sets.Contains(component.Name)) how.Add("Set");
            if (s.Adds.Contains(component.Name)) how.Add("Add");
            if (s.Removes.Contains(component.Name)) how.Add("Remove");
            if (s.DestroysEntities) how.Add("Destroy");
            if (how.Count > 0 && !(how.Count == 1 && how[0] == "Destroy")) list.Add($"{s.Name}: {string.Join(", ", how)}");
        }
        return list;
    }

    /// <summary>Источник для людей: путь от папки репозитория (<c>engine/…</c>, <c>game/…</c>, <c>tests/…</c>) и строка.</summary>
    public static string? Source(in CauseRecord r)
    {
        if (r.File is not { } file) return null;
        string path = file.Replace('\\', '/');
        foreach (string root in (string[])["/engine/", "/game/", "/tests/", "/tools/"])
        {
            int at = path.LastIndexOf(root, StringComparison.Ordinal);
            if (at >= 0)
            {
                path = path[(at + 1)..];
                break;
            }
        }
        return $"{path}:{r.Line}";
    }
}
