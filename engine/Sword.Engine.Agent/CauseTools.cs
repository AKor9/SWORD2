using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Причинность (раздел 8 плана, 6д): <c>state.why</c> — почему поле сущности такое: последние изменения, система, тик, источник и
/// цепочка причин до корня (команда, решение ИИ) из журнала причинности; для непрерывных полей — честно грубо: какая регистрация
/// писала столбец в каком тике. Режим включают <c>session.start {causality:true}</c> и ключ <c>--causality</c> (Headless, App).
/// </summary>
internal static class CauseTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "state.why", Title = "Почему поле такое",
            Description = "Почему у сущности такое значение поля: последние изменения (changes) — тик, вид (Set/Add/Remove), система " +
                "(регистрация метка@стадия), источник (файл:строка команды), значение до и после, и цепочка причин до корня: команда " +
                "(игрока, арены, врага, пробы, агента) или решение системы (ИИ бьёт по таймеру) → начало удара → StrikeContact → Hit → " +
                "Set<Health>/Add<Dead>. field — компонент или поле: Health.Hp, Health, Dead. Точно — для команд Add/Set/Remove (реактивные " +
                "компоненты, метки); непрерывные поля (запись колонки на месте, Transform) — грубо: writers — какая регистрация писала " +
                "столбец в каком тике (глубже — entity.history, time.break, experiment.fork). Нужен диагностический режим причинности: " +
                "session.start {causality:true} или ключ --causality (хеш и реплей он не меняет); без него — enabled:false и объявленные " +
                "производители. Кольцо журнала ограничено (65 536 записей): старое вытесняется — complete:false со словами почему. " +
                "cause — вместо entity и field: цепочка записи журнала по номеру (cause.id или cause.parent строки events.history: " +
                "почему случилось событие — переход Motion Matching, попадание) до корня.",
            Arguments = CoreTools.Props(
                ("entity", CoreTools.Str("номер «12v3» или имя (player, enemy N; session.status → namedEntities); с field")),
                ("field", CoreTools.Str("компонент или поле компонента: Health.Hp, Health, Dead, Transform.Position.X; с entity")),
                ("cause", CoreTools.Int("номер записи журнала причинности (events.history → cause.id, cause.parent) — вместо entity и field", 1, int.MaxValue)),
                ("changes", CoreTools.Int($"сколько последних изменений поля (по умолчанию 1, не больше {CausalityQuery.MaxChanges})", 1, CausalityQuery.MaxChanges)),
                ("depth", CoreTools.Int($"глубина цепочки (по умолчанию 16, не больше {CausalityQuery.MaxDepth})", 1, CausalityQuery.MaxDepth))),
            Run = async call =>
            {
                if (call.Node("cause") is not null) return await Cause(host, call);
                if (call.String("entity") is null || call.String("field") is null)
                    throw new ArgumentException("нужно entity и field (почему поле такое) или cause (почему случилась запись журнала)");
                string which = call.RequiredString("entity");
                string text = call.RequiredString("field");
                int changes = (int)call.Integer("changes", 1, 1, CausalityQuery.MaxChanges);
                int depth = (int)call.Integer("depth", 16, 1, CausalityQuery.MaxDepth);
                int dot = text.IndexOf('.');
                var component = host.Catalog.Component(dot < 0 ? text : text[..dot]);
                string? field = dot < 0 ? null : FieldCodec.CheckPath(component.Type, text[(dot + 1)..], Catalog.ShortName(component.Type));
                var r = await host.Serve(ctx =>
                {
                    var e = ObservationTools.ResolveEntity(ctx, which, host.Names);
                    var answer = CausalityQuery.Why(ctx, e, component, field, changes, depth, host.Names);
                    return (Answer: answer, Names: EntityNames(ctx, answer, host.Names), ctx.Point);
                }, call.Cancel);
                return new ToolOutput(Json(r.Answer, r.Names, host.Catalog)) { Point = r.Point };
            },
        });
    }

    /// <summary>
    /// <c>state.why {cause}</c> (8з): цепочка записи журнала причинности по номеру до корня — событие (переход Motion Matching, попадание) ←
    /// решение или команда. Режим выключен — <c>enabled: false</c>; запись вытеснена — ошибка вызова со словами.
    /// </summary>
    private static async Task<ToolOutput> Cause(AgentHost host, ToolCall call)
    {
        int id = (int)call.Integer("cause", 0, 1, int.MaxValue);
        int depth = (int)call.Integer("depth", 16, 1, CausalityQuery.MaxDepth);
        var r = await host.Serve(ctx =>
        {
            bool enabled = ctx.World.Causality is not null;
            var chain = CausalityQuery.WhyCause(ctx, new CauseId(id), depth, host.Names);
            var names = new Dictionary<Entity, string>();
            if (chain is not null && host.Names is { } known)
                foreach (var s in chain.Chain.Prepend(chain.Change))
                    foreach (var e in Entities(s))
                        if (!names.ContainsKey(e) && known.NameOf(ctx, e) is { } name) names[e] = name;
            return (Enabled: enabled, Chain: chain, Names: names, ctx.Point);
        }, call.Cancel);
        if (!r.Enabled)
            return new ToolOutput(new JsonObject
            {
                ["enabled"] = false,
                ["note"] = "диагностический режим причинности выключен: session.start {causality:true} (Headless) или ключ --causality",
            }) { Point = r.Point };
        if (r.Chain is not { } c)
            throw new ArgumentException($"записи {id} в журнале причинности нет: вытеснена (кольцо 65 536 записей) или номер не тот — cause.id из events.history");
        var o = Step(c.Change, r.Names, values: true);
        var steps = new JsonArray();
        foreach (var step in c.Chain) steps.Add(Step(step, r.Names, values: true));
        o["chain"] = steps;
        o["complete"] = c.Complete;
        if (c.Stop is not null) o["stop"] = c.Stop;
        o["root"] = c.Complete ? (c.Chain.Count > 0 ? c.Chain[^1] : c.Change).Record.Kind.ToString() : null;
        var summary = new StringBuilder(Label(c.Change, r.Names)).Append(" · тик ").Append(c.Change.Record.Tick.ToString(CultureInfo.InvariantCulture));
        foreach (var step in c.Chain) summary.Append(" ← ").Append(Label(step, r.Names));
        if (!c.Complete) summary.Append(" ← … (").Append(c.Stop).Append(')');
        o["summary"] = summary.ToString();
        var result = new JsonObject { ["enabled"] = true, ["cause"] = o };
        if (r.Names.Count > 0)
        {
            var map = new JsonObject();
            foreach (var (e, name) in r.Names) map[e.ToString()] = name;
            result["names"] = map;
        }
        return new ToolOutput(result) { Point = r.Point };
    }

    /// <summary>Сущности шага: запись и поля-сущности значений до и после.</summary>
    private static IEnumerable<Entity> Entities(CauseStep s)
    {
        if (!s.Record.Entity.IsNull && !s.Record.Entity.IsDeferred) yield return s.Record.Entity;
        if (s.ValueType is null) yield break;
        var layout = FieldLayout.Of(s.ValueType);
        foreach (var bytes in (byte[]?[])[s.Before, s.After])
        {
            if (bytes is null || bytes.Length < layout.Size) continue;
            foreach (var leaf in layout.Leaves)
                if (leaf.Kind == FieldLayout.Kind.Entity && MemoryMarshal.Read<Entity>(bytes.AsSpan(leaf.Offset)) is { IsNull: false, IsDeferred: false } e)
                    yield return e;
        }
    }

    /// <summary>Имена игры сущностей из значений шагов (атакующий и жертва попадания…) — в точке обслуживания.</summary>
    private static Dictionary<Entity, string> EntityNames(in ServiceContext ctx, WhyAnswer answer, IEntityNames? names)
    {
        var map = new Dictionary<Entity, string>();
        if (names is null) return map;
        void Add(in ServiceContext ctx, Entity e)
        {
            if (e.IsNull || e.IsDeferred || map.ContainsKey(e)) return;
            if (names.NameOf(ctx, e) is { } name) map[e] = name;
        }
        void Scan(in ServiceContext ctx, CauseStep s)
        {
            Add(ctx, s.Record.Entity);
            if (s.ValueType is null) return;
            var layout = FieldLayout.Of(s.ValueType);
            foreach (var bytes in (byte[]?[])[s.Before, s.After])
            {
                if (bytes is null || bytes.Length < layout.Size) continue;
                foreach (var leaf in layout.Leaves)
                    if (leaf.Kind == FieldLayout.Kind.Entity) Add(ctx, MemoryMarshal.Read<Entity>(bytes.AsSpan(leaf.Offset)));
            }
        }
        Add(ctx, answer.Entity);
        foreach (var change in answer.Changes)
        {
            Scan(ctx, change.Change);
            foreach (var s in change.Chain) Scan(ctx, s);
        }
        return map;
    }

    private static JsonObject Json(WhyAnswer a, Dictionary<Entity, string> names, Catalog catalog)
    {
        string component = catalog.DisplayName(a.Component);
        string what = a.Field is null ? component : $"{component}.{a.Field}";
        var o = new JsonObject
        {
            ["enabled"] = a.Enabled,
            ["entity"] = a.Entity.ToString(),
            ["name"] = a.EntityName,
            ["field"] = what,
            ["value"] = a.Current is null ? null : Pick(FieldCodec.Decode(a.Component.Type, a.Current), a.Field),
        };
        if (!a.Enabled)
        {
            o["note"] = "диагностический режим причинности выключен: session.start {causality:true} (Headless) или ключ --causality " +
                "(Sword.Headless --agent, Sword.exe); хеш и реплей он не меняет";
            o["producers"] = Strings(a.Producers);
            return o;
        }
        var changes = new JsonArray();
        foreach (var c in a.Changes) changes.Add(Change(c, a, what, names));
        o["changes"] = changes;
        if (a.Changes.Count == 0)
            o["note"] = a.Component.Reactive || a.Component.IsTag
                ? "изменений в кольце нет: поле не менялось с включения режима или запись вытеснена"
                : "команд Add/Set/Remove этого компонента у сущности в кольце нет — поле пишут на месте (writers)";
        var writers = new JsonArray();
        foreach (var w in a.Writers) writers.Add(new JsonObject { ["system"] = w.System, ["tick"] = w.Tick });
        o["writers"] = writers;
        ulong? lastChange = a.Changes.Count > 0 ? a.Changes[0].Change.Record.Tick : null;
        // Точно — когда последнее изменение поля сделано командой и после него колонку на месте никто не писал.
        o["exact"] = a.Changes.Count > 0 && (a.Writers.Count == 0 || a.Writers[0].Tick < lastChange);
        if (a.Writers.Count > 0)
            o["writersNote"] = "грубо: регистрация писала столбец компонента (весь чанк, не только эту сущность) в последний раз в этом тике";
        o["producers"] = Strings(a.Producers);
        o["log"] = new JsonObject { ["written"] = a.Written, ["capacity"] = a.Capacity, ["scanned"] = a.Scanned };
        if (names.Count > 0)
        {
            var map = new JsonObject();
            foreach (var (e, name) in names) map[e.ToString()] = name;
            o["names"] = map;
        }
        return o;
    }

    private static JsonObject Change(WhyChange c, WhyAnswer a, string what, Dictionary<Entity, string> names)
    {
        var s = c.Change;
        var o = Step(s, names);
        if (s.ValueType is not null)
        {
            if (s.Before is not null) o["before"] = Pick(FieldCodec.Decode(s.ValueType, s.Before), a.Field);
            if (s.After is not null) o["after"] = Pick(FieldCodec.Decode(s.ValueType, s.After), a.Field);
        }
        var chain = new JsonArray();
        foreach (var step in c.Chain) chain.Add(Step(step, names, values: true));
        o["chain"] = chain;
        o["complete"] = c.Complete;
        if (c.Stop is not null) o["stop"] = c.Stop;
        o["root"] = c.Complete ? (c.Chain.Count > 0 ? c.Chain[^1] : s).Record.Kind.ToString() : null;
        o["summary"] = Summary(c, what, names, o);
        return o;
    }

    private static JsonObject Step(CauseStep s, Dictionary<Entity, string> names, bool values = false)
    {
        var r = s.Record;
        var o = new JsonObject
        {
            ["id"] = r.Id.Value,
            ["parent"] = r.Parent.Value == 0 ? null : r.Parent.Value,
            ["kind"] = r.Kind.ToString(),
            ["tick"] = r.Tick,
            ["stage"] = s.Stage,
            ["system"] = s.System,
            ["source"] = CausalityQuery.Source(r),
        };
        if (s.ValueType is not null) o["type"] = Catalog.ShortName(s.ValueType);
        else if (s.TypeName is not null) o["type"] = s.TypeName;
        if (!r.Entity.IsNull && !r.Entity.IsDeferred)
        {
            o["entity"] = r.Entity.ToString();
            if (names.TryGetValue(r.Entity, out var name)) o["name"] = name;
        }
        if (r.Note is not null) o["note"] = r.Note;
        if (r.Arg != 0) o["arg"] = r.Arg;
        if (values && s.ValueType is not null && (s.After ?? s.Before) is { } bytes) o["fields"] = FieldCodec.Decode(s.ValueType, bytes);
        return o;
    }

    /// <summary>Строка для людей: «Health.Hp: 100 → 64 · тик 1842 · core.hit-apply@Resolve ← Hit (enemy 1 → player) ← …».</summary>
    private static string Summary(WhyChange c, string what, Dictionary<Entity, string> names, JsonObject change)
    {
        var s = c.Change;
        var b = new StringBuilder(what).Append(": ");
        b.Append(s.Record.Kind switch
        {
            CauseKind.Add => "добавлен" + (change["after"] is { } after ? $" {Short(after)}" : ""),
            CauseKind.Remove => "снят",
            _ => $"{(change["before"] is { } before ? Short(before) : "?")} → {(change["after"] is { } now ? Short(now) : "?")}",
        });
        b.Append(" · тик ").Append(s.Record.Tick.ToString(CultureInfo.InvariantCulture)).Append(" · ").Append(s.System ?? "вне системы");
        foreach (var step in c.Chain) b.Append(" ← ").Append(Label(step, names));
        if (!c.Complete) b.Append(" ← … (").Append(c.Stop).Append(')');
        return b.ToString();
    }

    private static string Label(CauseStep s, Dictionary<Entity, string> names)
    {
        var r = s.Record;
        string type = s.ValueType is not null ? Catalog.ShortName(s.ValueType) : s.TypeName ?? "";
        string who = r.Entity.IsNull ? "" : Name(r.Entity, names);
        return r.Kind switch
        {
            CauseKind.Command => $"команда {type}{(r.Note is null ? "" : $" ({r.Note})")} · тик {r.Tick}",
            CauseKind.Decision => $"решение {s.System ?? "?"}: {r.Note}{(who.Length > 0 ? $" ({who})" : "")} · тик {r.Tick}",
            CauseKind.Step => $"{r.Note} ({who}{(r.Arg != 0 ? $", №{r.Arg}" : "")}) · тик {r.Tick}",
            CauseKind.Event => $"{type}{Parties(s, names)} · {s.System ?? "?"}",
            _ => $"{r.Kind}<{type}>{(who.Length > 0 ? $" {who}" : "")} · {s.System ?? "?"}",
        };
    }

    /// <summary>У события с атакующим и жертвой (StrikeContact, Hit) — «(enemy 1 → player)».</summary>
    private static string Parties(CauseStep s, Dictionary<Entity, string> names)
    {
        if (s.ValueType is null || s.After is null) return "";
        var layout = FieldLayout.Of(s.ValueType);
        Entity? attacker = null, victim = null;
        foreach (var leaf in layout.Leaves)
        {
            if (leaf.Kind != FieldLayout.Kind.Entity || s.After.Length < leaf.Offset + 8) continue;
            var e = MemoryMarshal.Read<Entity>(s.After.AsSpan(leaf.Offset));
            if (leaf.Path == "Attacker") attacker = e;
            else if (leaf.Path == "Victim") victim = e;
        }
        return attacker is { } a && victim is { } v ? $" ({Name(a, names)} → {Name(v, names)})" : "";
    }

    private static string Name(Entity e, Dictionary<Entity, string> names) => names.TryGetValue(e, out var n) ? n : e.ToString();

    private static string Short(JsonNode node) => node is JsonValue ? node.ToJsonString() : "{…}";

    /// <summary>Поле по пути в разобранном значении; путь null — значение целиком.</summary>
    private static JsonNode? Pick(JsonNode? value, string? path)
    {
        if (path is null || value is null) return value?.DeepClone();
        var at = value;
        foreach (string part in path.Split('.'))
        {
            if (at is not JsonObject o || !o.TryGetPropertyValue(part, out var next)) return null;
            at = next;
        }
        return at?.DeepClone();
    }

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
}
