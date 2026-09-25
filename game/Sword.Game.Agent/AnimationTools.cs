using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Инструменты анимации: поза бойца по суставам и библиотека клипов — через вид только для чтения
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
                "последнего тика, пропущенные); у бойца со слоем действий — action: действие (None, Attack — удар, Dash — вид рывка, " +
                "Turn — поворот на месте), клип с именем, время и длительность, вес слоя и его доля в позе, растяжение yaw клипа, " +
                "поворот курса и сдвиг корня клипом за тик; удар — слот (slash1–4, stab1–3), номер удара бойца, слой верха и доля клипа " +
                "в ногах, обрезан ли хвост; снимок позы — переход и сколько снимков; что прервано последним действием. joints — имена " +
                "суставов (по умолчанию таз, позвоночник, голова, кисти, стопы). У бойца на Motion Matching (игрок при " +
                "locomotion.player=mm) — motionMatching: кадр базы, клип с именем, кадр в клипе, доля, темп, идёт ли, ищет ли, тиков с поиска, " +
                "поисков, смен, переходов; стопы на земле по разметке кадра; последний поиск (тик, повод, текущий и найденный кадр, клип, цена, " +
                "цена продолжения, 12 групп по именам, осмотрено блоков и кадров, сменил ли, время мкс и наибольшее); замер тика (скольжение " +
                "опорной стопы м/с и чей сустав, скачок сустава за тик м и чей); траектории в мире — желаемая (запрос поиска), найденного кадра, " +
                "история корня ([t, x, z, курс]); отпечаток базы.",
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
        if (world.Knows<AnimatorAction>() && world.TryGet<AnimatorAction>(e, out var action))
            data["action"] = Action(view, world.TryView<ICombatView>(out var combat) ? combat : null, action);
        if (world.TryView<IMotionMatchView>(out var mm) && mm.TryStatus(e, out var status)) data["motionMatching"] = MotionMatching(view, mm, e, status);
        return (data, ctx.Point);
    }

    /// <summary>
    /// Motion Matching бойца (вид <see cref="IMotionMatchView"/>): состояние, стопы на земле, последний поиск с группами, замер тика,
    /// траектории в мире (<c>[t, x, z, курс]</c>), отпечаток базы.
    /// </summary>
    private static JsonObject MotionMatching(IAnimationView anim, IMotionMatchView mm, Entity e, in MotionMatchStatus s)
    {
        string? Joint(int j) => j >= 0 && j < anim.JointCount ? anim.JointName(j) : null;
        string? Clip(int c) => (uint)c < (uint)anim.ClipCount ? anim.ClipName(c) : null;
        var feet = new JsonArray();
        for (int k = 0; k < mm.ContactJointCount; k++)
            if ((s.Contacts >> k & 1) != 0) feet.Add(Joint(mm.ContactJoint(k)));
        var o = new JsonObject
        {
            ["frame"] = s.Frame, ["clip"] = s.Clip, ["clipName"] = Clip(s.Clip), ["clipFrame"] = s.ClipFrame, ["alpha"] = Round(s.Alpha),
            ["rate"] = Round(s.Rate), ["stepping"] = s.Stepping, ["searching"] = s.Searching, ["sinceSearch"] = s.SinceSearch,
            ["searches"] = s.Searches, ["switches"] = s.Switches, ["transitions"] = s.Transitions, ["feetOnGround"] = feet,
            ["turnInPlace"] = new JsonObject
            {
                ["twistDegrees"] = Math.Round(s.Twist * 180 / MathF.PI, 2), ["chestDegrees"] = Math.Round(s.Chest * 180 / MathF.PI, 2),
                ["turnClip"] = s.TurnClip, ["turnClipName"] = Clip(s.TurnClip), ["turns"] = s.Turns,
            },
        };
        if (mm.TrySearch(e, out var q))
        {
            var groups = new JsonObject();
            for (int g = 0; g < mm.GroupCount; g++) groups[mm.GroupName(g)] = Round(mm.GroupCost(e, g));
            o["lastSearch"] = new JsonObject
            {
                ["tick"] = q.Tick, ["reason"] = q.Reason.ToString(), ["current"] = q.Current, ["frame"] = q.Frame, ["clip"] = q.Clip,
                ["clipName"] = Clip(q.Clip), ["clipFrame"] = q.ClipFrame, ["cost"] = Round(q.Cost), ["continuationCost"] = Round(q.ContinuationCost),
                ["switched"] = q.Switched, ["blocks"] = q.Blocks, ["frames"] = q.Frames, ["microseconds"] = Math.Round(q.Microseconds, 1),
                ["maxMicroseconds"] = Math.Round(q.MaxMicroseconds, 1), ["groups"] = groups,
            };
        }
        var m = mm.Measure(e);
        o["measure"] = new JsonObject
        {
            ["footSlide"] = float.IsFinite(m.FootSlide) ? Round(m.FootSlide) : null, ["slideJoint"] = Joint(m.SlideJoint),
            ["feetOnGround"] = m.FeetOnGround, ["jointJump"] = float.IsFinite(m.JointJump) ? Math.Round(m.JointJump, 5) : null,
            ["jumpJoint"] = Joint(m.JumpJoint),
        };
        o["desired"] = Points(mm.Desired(e));
        o["matched"] = Points(mm.Matched(e));
        o["history"] = Points(mm.History(e));
        var b = mm.Base;
        o["base"] = new JsonObject
        {
            ["fingerprint"] = b.Fingerprint.ToString("X16", System.Globalization.CultureInfo.InvariantCulture), ["clips"] = b.Clips,
            ["frames"] = b.Frames, ["pattern"] = mm.Pattern,
        };
        return o;
    }

    private static JsonArray Points(ReadOnlySpan<MotionTrajectoryPoint> points)
    {
        var list = new JsonArray();
        foreach (var p in points) list.Add(new JsonArray(Round(p.Time), Round(p.Position.X), Round(p.Position.Z), Round(p.Yaw)));
        return list;
    }

    /// <summary>
    /// Слой действий бойца (<see cref="AnimatorAction"/>): действие, клип с именем, время и длительность, вес слоя и его
    /// сглаженная доля в позе, растяжение yaw клипа поворота и выход тика — поворот курса и сдвиг корня клипом.
    /// </summary>
    private static JsonObject Action(IAnimationView view, ICombatView? combat, in AnimatorAction a)
    {
        bool acting = a.Kind != ActionKind.None && (uint)a.Clip < (uint)view.ClipCount;
        string? Slot(int attack) => combat is not null && (uint)attack < (uint)combat.Attacks.Length ? combat.Attacks[attack].Slot : null;
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
            ["attack"] = Slot(a.Attack),
            ["attackSerial"] = a.AttackSerial,
            ["upper"] = acting ? a.Upper : null,
            ["stand"] = acting ? Round(a.Stand) : null,
            ["cutRecovery"] = acting ? a.CutRecovery : null,
            ["rate"] = acting ? Round(a.Rate) : null,
            ["snapFade"] = Round(a.SnapFade),
            ["snapshots"] = a.Snapshots,
            ["interrupted"] = new JsonObject { ["kind"] = a.From.ToString(), ["time"] = Round(a.FromTime), ["attack"] = Slot(a.FromAttack) },
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
