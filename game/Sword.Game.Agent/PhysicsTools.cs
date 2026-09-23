using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Инструменты тел (5б): <c>physics.bodies</c> — капсулы тела бойца по сегментам (имена «левое предплечье» и т. п.,
/// центр, концы, поворот, скорость, прошлая постановка, форма) через вид только для чтения <see cref="IBodyView"/>, и
/// вычисляемые поля тела для метрик манифестов (<see cref="BodyFields"/>: <c>Body.pelvis.Position.Y</c>,
/// <c>Body.MaxSpeed</c>…). Слоты и поколения тел — участник хеша <c>physics</c> (<c>state.describe physics</c>).
/// </summary>
public static class PhysicsTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "physics.bodies", Title = "Тело бойца",
            Description = "Капсулы тела бойца после последнего тика по сегментам: номер тела в сочленении, сегмент SWORD (0…13, 11 — " +
                "клинок, у бойца его нет), имя (forearm.l) и название («левое предплечье»), центр, концы оси A и B, поворот, " +
                "скорость и её модуль, угловая скорость и её модуль, прошлая постановка, радиус, полудлина, м; сводка — самая быстрая " +
                "капсула. segments — имена сегментов (по умолчанию все 13). Слоты и поколения тел — state.describe physics.",
            Arguments = new JsonObject
            {
                ["entity"] = new JsonObject { ["type"] = "string", ["description"] = "номер «12v3» или имя (по умолчанию player)" },
                ["segments"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "имена сегментов: pelvis, torso, head, upperarm.l, forearm.l, upperarm.r, forearm.r, thigh.l, shin.l, thigh.r, shin.r, foot.l, foot.r",
                },
            },
            Run = async call =>
            {
                string which = call.String("entity") ?? "player";
                var segments = call.Strings("segments");
                var result = await host.Serve(ctx => (Data: Bodies(ctx, host.Names, which, segments), ctx.Point), call.Cancel);
                return new ToolOutput(result.Data) { Point = result.Point };
            },
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
                ["prevPosition"] = Vec(b.PrevPosition), ["radius"] = Round(b.Radius), ["halfLength"] = Round(b.HalfLength),
            });
        }
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
            ["capsules"] = list,
        };
    }

    private static JsonArray Vec(Vector3 v) => [Round(v.X), Round(v.Y), Round(v.Z)];

    private static JsonArray Quat(Quaternion q) => [Round(q.X), Round(q.Y), Round(q.Z), Round(q.W)];

    private static double Round(float x) => Math.Round(x, 4);
}

/// <summary>
/// Вычисляемые поля тела бойца (<see cref="IEntityFields"/>, 5б) — по <see cref="IBodyView"/>: <c>Body.&lt;сегмент&gt;.&lt;поле&gt;[.X|.Y|.Z|.W]</c>,
/// сегмент — имя <see cref="BodySegs.Name"/> (<c>pelvis</c>, <c>forearm.l</c>…), поле — <c>Position</c>, <c>Rotation</c>,
/// <c>PrevPosition</c>, <c>PrevRotation</c>, <c>Velocity</c>, <c>AngularVelocity</c>, <c>EndA</c>, <c>EndB</c> (без компоненты —
/// длина вектора), <c>Radius</c>, <c>HalfLength</c>; и по всему телу — <c>Body.MaxSpeed</c>, <c>Body.MaxAngularSpeed</c> (самая
/// быстрая капсула, м/с и рад/с). Нет тела — NaN.
/// </summary>
public sealed class BodyFields : IEntityFields
{
    private const string Prefix = "Body.";
    private static readonly string[] Vectors = ["Position", "PrevPosition", "Velocity", "AngularVelocity", "EndA", "EndB"];
    private static readonly string[] Quaternions = ["Rotation", "PrevRotation"];

    public IReadOnlyList<string> Known { get; } =
        ["Body.<сегмент>.Position|Rotation|PrevPosition|PrevRotation|Velocity|AngularVelocity|EndA|EndB[.X|.Y|.Z|.W]", "Body.<сегмент>.Radius|HalfLength", "Body.MaxSpeed", "Body.MaxAngularSpeed"];

    public EntityFieldReader? Compile(string path, string at)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        string rest = path[Prefix.Length..];
        if (rest == "MaxSpeed") return (in ServiceContext ctx, Entity e) => Max(ctx, e, angular: false);
        if (rest == "MaxAngularSpeed") return (in ServiceContext ctx, Entity e) => Max(ctx, e, angular: true);
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
        bool vector = Vectors.Contains(name0), quaternion = Quaternions.Contains(name0), scalar = name0 is "Radius" or "HalfLength";
        if (!vector && !quaternion && !scalar || component < -1 || scalar && component != -1 || vector && component == 3)
            throw new ArgumentException($"{at}: {path} — поле тела: {string.Join(" | ", Known)}");
        return (in ServiceContext ctx, Entity e) =>
        {
            if (!Snapshot(ctx, e, body, out var b)) return double.NaN;
            if (scalar) return name0 == "Radius" ? b.Radius : b.HalfLength;
            if (quaternion)
            {
                var q = name0 == "Rotation" ? b.Rotation : b.PrevRotation;
                return component switch { 0 => q.X, 1 => q.Y, 2 => q.Z, 3 => q.W, _ => q.Length() };
            }
            var v = name0 switch
            {
                "Position" => b.Position, "PrevPosition" => b.PrevPosition, "Velocity" => b.Velocity,
                "AngularVelocity" => b.AngularVelocity, "EndA" => b.EndA, _ => b.EndB,
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
