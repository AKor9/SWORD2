using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Инструменты тел (5б): <c>physics.bodies</c> — капсулы тела бойца по сегментам (имена «левое предплечье» и т. п.,
/// центр, концы, поворот, скорость, прошлая постановка, форма; с 7ж — тело клинка, масса, режим, профиль, касание земли, поза
/// начала тика и привязка скина мёртвого) через виды только для чтения <see cref="IBodyView"/>, <see cref="ISkinView"/>, и
/// вычисляемые поля тела для метрик манифестов (<see cref="BodyFields"/>: <c>Body.pelvis.Position.Y</c>,
/// <c>Body.MaxSpeed</c>…). Слоты и поколения тел — участник хеша <c>physics</c> (<c>state.describe physics</c>), привязки — <c>skin</c>.
/// </summary>
public static class PhysicsTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "physics.bodies", Title = "Тело бойца",
            Description = "Капсулы тела бойца после последнего тика по сегментам: номер тела в сочленении (= сегмент SWORD 0…13; 11 — " +
                "клинок: капсула от хвата до кончика меча в руке, хват — конец A), имя (forearm.l) и название («левое предплечье»), центр, " +
                "концы оси A и B, поворот, скорость и её модуль, угловая скорость и её модуль, прошлая постановка, поза начала тика, радиус, " +
                "полудлина, м, масса, кг, режим (kinematic — ставит поза, simulated — на физике), профиль динамики, касание земли; сводка — " +
                "самая быстрая капсула, масса тела, привязка скина (skin: есть ли, время смерти, суставов, drift — насколько скин, " +
                "привязка × тело, ушёл от позы бойца, м). segments — имена сегментов (по умолчанию все 14, blade — клинок). Слоты и " +
                "поколения тел — state.describe physics, привязки скина — state.describe skin. 7и: joints — суставы тела (у трупа на физике: " +
                "родитель, ребёнок, включён ли, ошибка точки, углы, выход за предел); trace — трасса физики (диагностический режим physics.trace, " +
                "мир, хеш и реплей не меняет): on — включить, off — выключить, reset — начать окно заново, show — сводка; в ответе trace — " +
                "last (последний тик), window, run (с включения): скорости тел на физике и перцентили, поправки по источникам, толчки, удары, " +
                "рост энергии за подшаг, самые быстрые события; recent — последние события быстрее порога (events штук); контакты последнего " +
                "подшага по источникам (их точки — оверлей contacts в capture.bundle).",
            Arguments = new JsonObject
            {
                ["entity"] = new JsonObject { ["type"] = "string", ["description"] = "номер «12v3» или имя (по умолчанию player)" },
                ["segments"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "имена сегментов: pelvis, torso, head, upperarm.l, forearm.l, upperarm.r, forearm.r, thigh.l, shin.l, thigh.r, shin.r, blade, foot.l, foot.r",
                },
                ["trace"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray([.. PhysicsDebugJson.TraceActions.Select(a => (JsonNode?)a)]),
                    ["description"] = "трасса физики (7и): on — включить (данные — с ближайшего тика), off — выключить, reset — начать окно " +
                        "заново, show — только сводка; без аргумента — в ответе только traceOn",
                },
                ["events"] = new JsonObject
                {
                    ["type"] = "integer", ["minimum"] = 0, ["maximum"] = PhysicsDebugJson.MaxEvents,
                    ["description"] = $"сколько последних событий трассы быстрее порога вернуть (по умолчанию 8, до {PhysicsDebugJson.MaxEvents})",
                },
            },
            Run = async call =>
            {
                string which = call.String("entity") ?? "player";
                var segments = call.Strings("segments");
                string? trace = call.String("trace");
                if (trace is not null && !PhysicsDebugJson.TraceActions.Contains(trace))
                    throw new ArgumentException($"trace: «{trace}» — есть {string.Join(", ", PhysicsDebugJson.TraceActions)}");
                int events = (int)call.Integer("events", 8, 0, PhysicsDebugJson.MaxEvents);   // все аргументы — до точки обслуживания
                var result = await host.Serve(ctx =>
                {
                    // Сначала тело (нет такого бойца — ошибка до переключения трассы), затем трасса (включение — между тиками; мир
                    // она не меняет — ответ одного момента), traceOn — после действия.
                    var data = Bodies(ctx, host.Names, which, segments);
                    if (trace is not null)
                    {
                        var traceJson = PhysicsDebugJson.Trace(ctx, host.Names, trace, events);
                        data["traceOn"] = traceJson["on"]!.GetValue<bool>();
                        data["trace"] = traceJson;
                    }
                    return (Data: data, ctx.Point);
                }, call.Cancel);
                return new ToolOutput(result.Data) { Point = result.Point };
            },
            // 7и: trace on/off/reset меняет диагностический режим сеанса (не мир) — как profile.stages {enable}.
            ReadOnly = false,
        });
    }

    private static JsonObject Bodies(in ServiceContext ctx, IEntityNames? names, string which, IReadOnlyList<string> segments)
    {
        var world = ctx.World;
        var e = which.Length > 0 && char.IsDigit(which[0]) ? FieldCodec.ParseEntity(which)
            : names?.Resolve(ctx, which) ?? throw new ArgumentException($"нет сущности с именем «{which}» (или номер «12v3»)");
        if (!world.IsAlive(e)) throw new ArgumentException($"сущности {e} нет");
        if (!world.TryGet<BodyRef>(e, out var body)) throw new ArgumentException($"у {e} нет тела (BodyRef)");
        var view = world.View<IBodyView>();
        int count = view.BodyCount(body);
        if (count == 0) throw new ArgumentException($"тело {body.Index}v{body.Generation} у {e} не живо");
        var wanted = new List<int>();
        foreach (string name in segments)
        {
            int seg = BodySegs.Find(name);
            if (seg < 0) throw new ArgumentException($"нет сегмента «{name}»; есть: {string.Join(", ", Enumerable.Range(0, BodySegs.Bodies).Select(i => BodySegs.Name(BodySegs.Segment(i))))}");
            wanted.Add(BodySegs.Body(seg));
        }
        if (wanted.Count == 0) wanted.AddRange(Enumerable.Range(0, Math.Min(count, BodySegs.Bodies)));

        var list = new JsonArray();
        foreach (int k in wanted)
        {
            var b = view.Body(body, k);
            int seg = BodySegs.Segment(k);
            list.Add(new JsonObject
            {
                ["body"] = k, ["segment"] = seg, ["name"] = BodySegs.Name(seg), ["title"] = BodySegs.Title(seg),
                ["position"] = Vec(b.Position), ["endA"] = Vec(b.EndA), ["endB"] = Vec(b.EndB), ["rotation"] = Quat(b.Rotation),
                ["velocity"] = Vec(b.Velocity), ["speed"] = Round(b.Velocity.Length()),
                ["angularVelocity"] = Vec(b.AngularVelocity), ["angularSpeed"] = Round(b.AngularVelocity.Length()),
                ["prevPosition"] = Vec(b.PrevPosition), ["startPosition"] = Vec(b.StartPosition), ["radius"] = Round(b.Radius),
                ["halfLength"] = Round(b.HalfLength), ["mass"] = Round(b.Mass), ["mode"] = b.Simulated ? "simulated" : "kinematic",
                ["profile"] = b.Profile, ["grounded"] = b.Grounded,
            });
        }
        float mass = 0;
        for (int k = 0; k < count; k++) mass += view.Body(body, k).Mass;
        int fastest = 0;
        float max = -1;
        for (int k = 0; k < count; k++)
        {
            float v = view.Body(body, k).Velocity.Length();
            if (v > max) (max, fastest) = (v, k);
        }
        return new JsonObject
        {
            ["entity"] = e.ToString(),
            ["name"] = names?.NameOf(ctx, e),
            ["articulation"] = string.Create(CultureInfo.InvariantCulture, $"{body.Index}v{body.Generation}"),
            ["bodyCount"] = count,
            ["fastest"] = new JsonObject { ["name"] = BodySegs.Name(BodySegs.Segment(fastest)), ["title"] = BodySegs.Title(BodySegs.Segment(fastest)), ["speed"] = Round(max) },
            ["mass"] = Round(mass),
            ["skin"] = Skin(world, e, body, view),
            // 7и: суставы тела (у трупа на физике) и включена ли трасса физики (сводка — аргументом trace).
            ["joints"] = PhysicsDebugJson.Joints(world, body),
            ["traceOn"] = world.TryView<IPhysicsDebugView>(out var debug) && debug.TraceOn,
            ["capsules"] = list,
        };
    }

    /// <summary>
    /// Привязка скина тела (7ж): есть ли, время смерти, суставов и насколько скин (привязка × тело сегмента) ушёл от позы бойца
    /// (модельная матрица × мир бойца): пока тела мёртвого стоят — 0; тела двинулись (сдвиг командой сцены, труп на физике) — больше.
    /// </summary>
    private static JsonObject Skin(WorldReader world, Entity e, BodyRef body, IBodyView bodies)
    {
        if (!world.TryView<ISkinView>(out var skins) || !skins.TryGet(body, out var binding, out float time))
            return new JsonObject { ["captured"] = false };
        float drift = float.NaN;
        if (world.TryView<IAnimationView>(out var poses) && world.TryGet<PoseRef>(e, out var pose) && poses.Model(pose) is { Length: > 0 } model
            && world.TryGet<Transform>(e, out var root))
        {
            var fighter = BodyPlacement.World(root.Position, root.Facing);
            drift = 0;
            for (int j = 0; j < Math.Min(binding.Length, model.Length); j++)
            {
                var skin = binding[j] * bodies.Body(body, BodySegs.Body(bodies.JointSegment(j))).World;
                drift = MathF.Max(drift, Vector3.Distance(skin.Translation, (model[j] * fighter).Translation));
            }
        }
        return new JsonObject { ["captured"] = true, ["time"] = Round(time), ["joints"] = binding.Length, ["drift"] = float.IsNaN(drift) ? null : Round(drift) };
    }

    private static JsonArray Vec(Vector3 v) => [Round(v.X), Round(v.Y), Round(v.Z)];

    private static JsonArray Quat(Quaternion q) => [Round(q.X), Round(q.Y), Round(q.Z), Round(q.W)];

    private static double Round(float x) => Math.Round(x, 4);
}

/// <summary>
/// Вычисляемые поля тела бойца (<see cref="IEntityFields"/>, 5б) — по <see cref="IBodyView"/>: <c>Body.&lt;сегмент&gt;.&lt;поле&gt;[.X|.Y|.Z|.W]</c>,
/// сегмент — имя <see cref="BodySegs.Name"/> (<c>pelvis</c>, <c>forearm.l</c>…), поле — <c>Position</c>, <c>Rotation</c>,
/// <c>PrevPosition</c>, <c>PrevRotation</c>, <c>Velocity</c>, <c>AngularVelocity</c>, <c>EndA</c>, <c>EndB</c>, <c>StartPosition</c> (7ж)
/// (без компоненты — длина вектора), <c>Radius</c>, <c>HalfLength</c>, <c>Mass</c> (7ж); сегмент клинка — <c>blade</c> (7ж); и по всему телу — <c>Body.MaxSpeed</c>, <c>Body.MaxAngularSpeed</c> (самая
/// быстрая капсула, м/с и рад/с); с 7з — тело целиком (<see cref="CorpseFields"/>): <c>Body.Com</c>, <c>Body.ComVelocity</c>, <c>Body.KineticEnergy</c>,
/// <c>Body.Energy</c>. Нет тела — NaN.
/// </summary>
public sealed class BodyFields : IEntityFields
{
    private const string Prefix = "Body.";
    private static readonly string[] Vectors = ["Position", "PrevPosition", "Velocity", "AngularVelocity", "EndA", "EndB", "StartPosition"];
    private static readonly string[] Quaternions = ["Rotation", "PrevRotation"];

    public IReadOnlyList<string> Known { get; } =
        ["Body.<сегмент>.Position|Rotation|PrevPosition|PrevRotation|Velocity|AngularVelocity|EndA|EndB|StartPosition[.X|.Y|.Z|.W]", "Body.<сегмент>.Radius|HalfLength|Mass", "Body.MaxSpeed", "Body.MaxAngularSpeed",
            .. CorpseFields.Known];

    public EntityFieldReader? Compile(string path, string at)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        string rest = path[Prefix.Length..];
        if (rest == "MaxSpeed") return (in ServiceContext ctx, Entity e) => Max(ctx, e, angular: false);
        if (rest == "MaxAngularSpeed") return (in ServiceContext ctx, Entity e) => Max(ctx, e, angular: true);
        if (CorpseFields.Compile(rest, path, at) is { } whole) return whole;   // 7з: тело целиком — центр масс, энергии
        int segment = -1;
        string field = "";
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            string name = BodySegs.Name(BodySegs.Segment(k));
            if (rest.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && name.Length > (segment < 0 ? -1 : BodySegs.Name(segment).Length))
            {
                segment = BodySegs.Segment(k);
                field = rest[(name.Length + 1)..];
            }
        }
        if (segment < 0)
            throw new ArgumentException($"{at}: {path} — нет сегмента; сегменты: {string.Join(", ", Enumerable.Range(0, BodySegs.Bodies).Select(i => BodySegs.Name(BodySegs.Segment(i))))}");
        int body = BodySegs.Body(segment);
        string[] parts = field.Split('.');
        string name0 = parts[0];
        // Компонента — ровно одна из X, Y, Z, W; иное (x, XY, пусто, лишняя часть) — ошибка, а не длина вектора молча.
        int component = parts.Length == 1 ? -1 : parts.Length == 2 ? parts[1] switch { "X" => 0, "Y" => 1, "Z" => 2, "W" => 3, _ => -2 } : -2;
        bool vector = Vectors.Contains(name0), quaternion = Quaternions.Contains(name0), scalar = name0 is "Radius" or "HalfLength" or "Mass";
        if (!vector && !quaternion && !scalar || component < -1 || scalar && component != -1 || vector && component == 3)
            throw new ArgumentException($"{at}: {path} — поле тела: {string.Join(" | ", Known)}");
        return (in ServiceContext ctx, Entity e) =>
        {
            if (!Snapshot(ctx, e, body, out var b)) return double.NaN;
            if (scalar) return name0 switch { "Radius" => b.Radius, "HalfLength" => b.HalfLength, _ => b.Mass };
            if (quaternion)
            {
                var q = name0 == "Rotation" ? b.Rotation : b.PrevRotation;
                return component switch { 0 => q.X, 1 => q.Y, 2 => q.Z, 3 => q.W, _ => q.Length() };
            }
            var v = name0 switch
            {
                "Position" => b.Position, "PrevPosition" => b.PrevPosition, "Velocity" => b.Velocity,
                "AngularVelocity" => b.AngularVelocity, "EndA" => b.EndA, "StartPosition" => b.StartPosition, _ => b.EndB,
            };
            return component switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.Length() };
        };
    }

    private static bool Snapshot(in ServiceContext ctx, Entity e, int body, out BodySnapshot b)
    {
        b = default;
        var world = ctx.World;
        if (!world.IsAlive(e) || !world.Knows<BodyRef>() || !world.TryGet<BodyRef>(e, out var r)) return false;
        var view = world.View<IBodyView>();
        if (view.BodyCount(r) <= body) return false;
        b = view.Body(r, body);
        return true;
    }

    private static double Max(in ServiceContext ctx, Entity e, bool angular)
    {
        var world = ctx.World;
        if (!world.IsAlive(e) || !world.Knows<BodyRef>() || !world.TryGet<BodyRef>(e, out var r)) return double.NaN;
        var view = world.View<IBodyView>();
        int n = view.BodyCount(r);
        if (n == 0) return double.NaN;
        float max = 0;
        for (int k = 0; k < n; k++)
        {
            var b = view.Body(r, k);
            max = MathF.Max(max, (angular ? b.AngularVelocity : b.Velocity).Length());
        }
        return max;
    }
}
