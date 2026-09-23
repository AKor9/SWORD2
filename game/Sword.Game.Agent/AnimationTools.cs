using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Инструменты анимации (этап 2): поза бойца по суставам и библиотека клипов — через вид только для чтения
/// <see cref="IAnimationView"/>. Номер клипа в состояниях слоёв (<c>entity.get</c> → <c>LegacyLocomotionState.Clip</c>)
/// переводится в имя через <c>anim.clips</c>.
/// </summary>
public static class AnimationTools
{
    /// <summary>Суставы по умолчанию в <c>anim.pose</c> — то, по чему видно походку.</summary>
    public static readonly string[] DefaultJoints = ["pelvis", "spine_03", "head", "hand_l", "hand_r", "foot_l", "foot_r"];

    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "anim.pose", Title = "Поза бойца",
            Description = "Поза бойца последнего тика: суставы в пространстве персонажа (+Z вперёд, +X влево, Y вверх, м) и в мире " +
                "(поворот на курс и перенос в корень, без высоты рельефа), корень и курс; счётчики пула поз (живые позы, задания " +
                "последнего тика, пропущенные); у бойца со слоем действий (враг) — action: действие (None, Turn — поворот на месте), " +
                "клип с именем, время и длительность, вес слоя и его доля в позе, растяжение yaw клипа, поворот курса и сдвиг корня " +
                "клипом за тик. joints — имена суставов (по умолчанию таз, позвоночник, голова, кисти, стопы).",
            Arguments = new JsonObject
            {
                ["entity"] = new JsonObject { ["type"] = "string", ["description"] = "номер «12v3» или имя (по умолчанию player)" },
                ["joints"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "имена суставов",
                },
            },
            Run = async call =>
            {
                string which = call.String("entity") ?? "player";
                var joints = call.Strings("joints");
                var result = await host.Serve(ctx => Pose(ctx, host.Names, which, joints.Count > 0 ? joints : DefaultJoints), call.Cancel);
                return new ToolOutput(result.Data) { Point = result.Point };
            },
        });

        host.Add(new AgentTool
        {
            Name = "anim.clips", Title = "Клипы",
            Description = "Библиотека клипов: номер, ID «источник/имя», длительность, цикл, скорость корневого движения. index — один " +
                "клип по номеру; filter — подстрока ID (без учёта регистра); страницей: skip — пропустить подходящих, take — сколько " +
                "(по умолчанию 50).",
            Arguments = new JsonObject
            {
                ["index"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "номер клипа" },
                ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "подстрока ID клипа" },
                ["skip"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "пропустить подходящих (по умолчанию 0)" },
                ["take"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 2000, ["description"] = "сколько клипов" },
            },
            Run = async call =>
            {
                long? index = call.Node("index") is null ? null : call.Integer("index", 0, 0, int.MaxValue);
                string? filter = call.String("filter");
                int skip = (int)call.Integer("skip", 0, 0, int.MaxValue);
                int take = (int)call.Integer("take", 50, 1, 2000);
                var result = await host.Serve(ctx => (Data: Clips(ctx.World, index, filter, skip, take), ctx.Point), call.Cancel);
                int matched = (int)result.Data["matched"]!, count = ((JsonArray)result.Data["clips"]!).Count;
                bool truncated = skip + count < matched;
                return new ToolOutput(result.Data)
                {
                    Point = result.Point, Truncated = truncated, Continuation = truncated ? $"skip={skip + count}" : null,
                };
            },
        });
    }

    private static (JsonObject Data, ServicePoint Point) Pose(in ServiceContext ctx, IEntityNames? names, string which, IReadOnlyList<string> joints)
    {
        var world = ctx.World;
        var view = world.View<IAnimationView>();
        var e = which.Length > 0 && char.IsDigit(which[0]) ? FieldCodec.ParseEntity(which)
            : names?.Resolve(ctx, which) ?? throw new ArgumentException($"нет сущности с именем «{which}» (или номер «12v3»)");
        if (!world.TryGet<PoseRef>(e, out var poseRef)) throw new ArgumentException($"у {e} нет позы (PoseRef)");
        var model = view.Model(poseRef);
        if (model.IsEmpty) throw new ArgumentException($"поза {e} освобождена");
        var t = world.TryGet<Transform>(e, out var tr) ? tr : default;
        var toWorld = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, t.Facing));
        toWorld.Translation = t.Position;
        var list = new JsonArray();
        foreach (string name in joints)
        {
            int j = view.FindJoint(name);
            if (j < 0) throw new ArgumentException($"нет сустава «{name}»");
            var local = model[j].Translation;
            var w = Vector3.Transform(local, toWorld);
            list.Add(new JsonObject { ["joint"] = name, ["model"] = Vec(local), ["world"] = Vec(w) });
        }
        var data = new JsonObject
        {
            ["entity"] = e.ToString(),
            ["root"] = Vec(t.Position),
            ["facing"] = Round(t.Facing),
            ["joints"] = list,
            ["pool"] = new JsonObject { ["poses"] = view.PoseCount, ["jobsLastTick"] = view.LastTickJobs, ["staleLastTick"] = view.LastTickStale },
        };
        if (world.Knows<AnimatorAction>() && world.TryGet<AnimatorAction>(e, out var action)) data["action"] = Action(view, action);
        return (data, ctx.Point);
    }

    /// <summary>
    /// Слой действий бойца (4г, <see cref="AnimatorAction"/>): действие, клип с именем, время и длительность, вес слоя и его
    /// сглаженная доля в позе, растяжение yaw клипа поворота и выход тика — поворот курса и сдвиг корня клипом.
    /// </summary>
    private static JsonObject Action(IAnimationView view, in AnimatorAction a)
    {
        bool acting = a.Kind != ActionKind.None && (uint)a.Clip < (uint)view.ClipCount;
        return new JsonObject
        {
            ["kind"] = a.Kind.ToString(),
            ["clip"] = a.Clip,
            ["clipName"] = acting ? view.ClipName(a.Clip) : null,
            ["time"] = acting ? Round(a.Time) : null,
            ["duration"] = acting ? Round(a.Duration) : null,
            ["weight"] = Round(a.Weight),
            ["blend"] = Round(ActionLayer.Smooth(a.Weight)),
            ["turnScale"] = acting ? Round(a.TurnScale) : null,
            ["yawDelta"] = Math.Round(a.YawDelta, 6),
            ["rootDelta"] = new JsonArray(Math.Round(a.RootDelta.X, 6), Math.Round(a.RootDelta.Y, 6), Math.Round(a.RootDelta.Z, 6)),
        };
    }

    private static JsonObject Clips(WorldReader world, long? index, string? filter, int skip, int take)
    {
        var view = world.View<IAnimationView>();
        var list = new JsonArray();
        int matched = 0;
        for (int c = 0; c < view.ClipCount; c++)
        {
            if (index is { } i && c != i) continue;
            string name = view.ClipName(c);
            if (filter is { Length: > 0 } && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            int at = matched++;
            if (at < skip || at >= skip + take) continue;
            list.Add(new JsonObject
            {
                ["index"] = c, ["name"] = name, ["duration"] = Round(view.ClipDuration(c)), ["loop"] = view.ClipLoops(c),
                ["rootSpeed"] = Round(view.ClipRootSpeed(c)),
            });
        }
        if (index is { } k && (k >= view.ClipCount)) throw new ArgumentException($"клипа {k} нет: клипов {view.ClipCount}");
        return new JsonObject { ["total"] = view.ClipCount, ["matched"] = matched, ["skip"] = skip, ["clips"] = list };
    }

    private static JsonArray Vec(Vector3 v) => [Round(v.X), Round(v.Y), Round(v.Z)];

    private static double Round(float x) => Math.Round(x, 4);
}
