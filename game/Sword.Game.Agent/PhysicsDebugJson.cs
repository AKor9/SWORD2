using System.Globalization;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Часть <c>physics.bodies</c> 7и — суставы тела и трасса физики агенту через вид <see cref="IPhysicsDebugView"/>: суставы бойца (описание,
/// включён ли, замер — ошибка точки, углы, выход за предел) и трасса (диагностический режим <see cref="IPhysicsDebugView.TraceMode"/>:
/// включить, выключить, начать окно заново; сводки последнего тика, окна и прогона, по источникам, самые быстрые и последние события
/// быстрее порога, контакты последнего подшага по источникам). Нового инструмента нет — расширен существующий (раздел 8 плана).
/// </summary>
public static class PhysicsDebugJson
{
    /// <summary>Действия с трассой (аргумент <c>trace</c>).</summary>
    public static readonly IReadOnlyList<string> TraceActions = ["on", "off", "reset", "show"];

    /// <summary>Последних событий в ответе не больше (кольцо трассы — 64).</summary>
    public const int MaxEvents = 64;

    /// <summary>Имена источников в ответе (<see cref="PhysicsContactSource"/> по номеру).</summary>
    public static readonly string[] SourceNames = ["joints", "ground", "wall", "pair", "pushBody", "pushPass", "pushNoReaction"];

    /// <summary>
    /// Суставы тела бойца (7и): у живого суставов обычно нет (тела ставит поза), у трупа на физике — таблица суставов игры. Пусто — null.
    /// </summary>
    public static JsonObject? Joints(WorldReader world, BodyRef body)
    {
        if (!world.TryView<IPhysicsDebugView>(out var debug)) return null;
        int count = debug.JointCount(body);
        if (count == 0) return null;
        var list = new JsonArray();
        float worst = 0, gap = 0;
        int disabled = 0;
        for (int i = 0; i < count; i++)
        {
            var j = debug.Joint(body, i);
            worst = MathF.Max(worst, j.Excess);
            gap = MathF.Max(gap, j.PointError);
            if (!j.Enabled) disabled++;
            list.Add(new JsonObject
            {
                ["joint"] = i, ["parent"] = Segment(j.Parent), ["child"] = Segment(j.Child), ["kind"] = j.Hinge ? "hinge" : "swingTwist",
                ["enabled"] = j.Enabled, ["pointError"] = Round(j.PointError), ["bend"] = Round(j.Bend), ["swing"] = Round(j.Swing),
                ["twist"] = Round(j.Twist), ["excess"] = Round(j.Excess),
            });
        }
        return new JsonObject
        {
            ["count"] = count, ["disabled"] = disabled, ["excessMax"] = Round(worst), ["pointErrorMax"] = Round(gap),
            ["note"] = "замер в позе тика: pointError — расстояние точек сустава на ребёнке и родителе, м; bend (шарнир), swing, twist — рад; " +
                "excess — наибольший выход за предел, рад (0 — в пределах); выключенный сустав — запястье трупа (меч выпал)",
            ["list"] = list,
        };
    }

    /// <summary>
    /// Трасса физики (7и): действие <paramref name="action"/> (<see cref="TraceActions"/>; null — только флаг) в точке обслуживания
    /// (между тиками: режим не меняет ни мир, ни хеш, ни реплей), затем сводка: последний тик, окно, прогон (с включения), последние
    /// <paramref name="events"/> событий быстрее порога и контакты последнего подшага по источникам.
    /// </summary>
    public static JsonObject Trace(in ServiceContext ctx, IEntityNames? names, string? action, int events)
    {
        var world = ctx.World;
        if (!world.TryView<IPhysicsDebugView>(out var debug) || !ctx.HasDiagnostic(IPhysicsDebugView.TraceMode))
            throw new ArgumentException("трассы физики нет в этой симуляции (вид IPhysicsDebugView и режим physics.trace регистрирует игра)");
        string? done = null;
        switch (action)
        {
            case null or "show":
                break;
            case "on":
                done = debug.TraceOn ? "уже включена" : "включена";
                ctx.SetDiagnostic(IPhysicsDebugView.TraceMode, true);
                break;
            case "off":
                done = debug.TraceOn ? "выключена" : "уже выключена";
                ctx.SetDiagnostic(IPhysicsDebugView.TraceMode, false);
                break;
            case "reset":
                if (!debug.TraceOn) throw new ArgumentException("trace: reset — трасса выключена (сначала on)");
                ctx.ResetDiagnosticWindow(IPhysicsDebugView.TraceMode);
                done = "окно начато заново";
                break;
            default:
                throw new ArgumentException($"trace: «{action}» — есть {string.Join(", ", TraceActions)}");
        }
        var o = new JsonObject
        {
            ["on"] = debug.TraceOn,
            ["action"] = done,
            ["mode"] = IPhysicsDebugView.TraceMode,
            ["note"] = "трасса физики — диагностический режим: мир, хеш и реплей от неё не зависят; включение и выключение — между тиками, " +
                "данные — с ближайшего тика. last — последний тик, window — с начала окна (reset), run — с включения. Скорости — тел на " +
                "физике (труп) по подшагам, м/с; sources — поправки по источникам: сдвиг / h (м/с), угол / h (рад/с), скрытый сдвиг прошлой " +
                "позы / h (должен быть 0), псевдоимпульс за тик (Н·с), контакты и глубины (м); energy — рост энергии за подшаг, Дж (без толчков " +
                "живых — должен быть около 0)",
        };
        if (!debug.TraceOn) return o;
        var owners = Owners(ctx, names);
        o["eventSpeed"] = Round(debug.TraceEventSpeed);
        o["last"] = Period(debug, PhysicsTracePeriod.Last, owners);
        o["window"] = Period(debug, PhysicsTracePeriod.Window, owners);
        o["run"] = Period(debug, PhysicsTracePeriod.Run, owners);
        var recent = new JsonArray();
        for (int k = 0; k < Math.Min(events, debug.RecentEventCount); k++) recent.Add(Event(debug.RecentEvent(k), owners));
        o["recent"] = recent;
        o["recentTotal"] = debug.RecentEventCount;
        var contacts = debug.Contacts;
        var bySource = new int[SourceNames.Length];
        foreach (var c in contacts)
            if ((int)c.Source < bySource.Length) bySource[(int)c.Source]++;
        var counts = new JsonObject();
        for (int s = 0; s < bySource.Length; s++)
            if (bySource[s] > 0) counts[SourceNames[s]] = bySource[s];
        o["contacts"] = new JsonObject { ["count"] = contacts.Length, ["lost"] = debug.LostContacts, ["bySource"] = counts };
        return o;
    }

    /// <summary>Хозяева сочленений: хэндл тела бойца (<see cref="BodyRef"/>) → сущность и имя.</summary>
    private static Dictionary<(int, int), (Entity Entity, string? Name)> Owners(in ServiceContext ctx, IEntityNames? names)
    {
        var owners = new Dictionary<(int, int), (Entity, string?)>();
        var world = ctx.World;
        if (!world.Knows<Fighter>() || !world.Knows<BodyRef>()) return owners;
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<BodyRef>()))
        {
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < bodies.Length; i++) owners[(bodies[i].Index, bodies[i].Generation)] = (chunk.Entities[i], names?.NameOf(ctx, chunk.Entities[i]));
        }
        return owners;
    }

    private static JsonObject Period(IPhysicsDebugView debug, PhysicsTracePeriod period, Dictionary<(int, int), (Entity Entity, string? Name)> owners)
    {
        var s = debug.TraceSummary(period);
        var sources = new JsonArray();
        for (int i = 0; i < SourceNames.Length; i++)
        {
            var x = debug.TraceSource(period, (PhysicsContactSource)i);
            if (x.Count == 0 && x.Contacts == 0 && x.VelocitySolved == 0 && x.Applied == 0) continue;
            sources.Add(new JsonObject
            {
                ["source"] = SourceNames[i], ["corrections"] = x.Count, ["linear"] = Round(x.Linear), ["angular"] = Round(x.Angular),
                ["hidden"] = Round(x.Hidden), ["impulseTickMax"] = Round(x.ImpulseTickMax), ["impulseSum"] = Round(x.ImpulseSum),
                ["contacts"] = x.Contacts, ["depth"] = Round(x.Depth), ["deferred"] = Round(x.Deferred), ["velocitySolved"] = Round(x.VelocitySolved),
                ["applied"] = Round(x.Applied),
            });
        }
        var top = new JsonArray();
        for (int k = 0; k < debug.TraceTopCount(period); k++) top.Add(Event(debug.TraceTop(period, k), owners));
        return new JsonObject
        {
            ["steps"] = s.Steps, ["substeps"] = s.Substeps, ["seconds"] = Math.Round(s.Seconds, 4), ["events"] = s.Events,
            ["speed"] = new JsonObject
            {
                ["max"] = Round(s.SpeedMax), ["body"] = Body(s.SpeedBody, s.SpeedBodyIndex, owners), ["p99"] = Round(s.SpeedP99), ["p999"] = Round(s.SpeedP999),
            },
            ["angularSpeed"] = new JsonObject { ["max"] = Round(s.AngularSpeedMax), ["p99"] = Round(s.AngularP99) },
            ["capped"] = s.Capped, ["samples"] = s.SpeedSamples, ["nonFinite"] = s.NonFinite,
            ["push"] = new JsonObject
            {
                ["pairShare"] = Round(s.PairShare), ["pairSubsteps"] = s.PairSubsteps, ["passShare"] = Round(s.PassShare), ["popSpeed"] = Round(s.PushPopSpeed),
            },
            ["reaction"] = new JsonObject { ["max"] = Round(s.ReactionMax), ["body"] = Body(s.ReactionBody, -1, owners), ["sum"] = Round(s.ReactionSum) },
            ["hits"] = new JsonObject
            {
                ["count"] = s.Hits, ["capped"] = s.HitsCapped, ["budgeted"] = s.HitsBudgeted, ["dvShare"] = Round(s.HitDvShare), ["dv"] = Round(s.HitDv),
                ["dw"] = Round(s.HitDw),
            },
            ["friction"] = new JsonObject { ["contacts"] = s.FrictionContacts, ["held"] = s.FrictionHeld },
            ["energy"] = new JsonObject
            {
                ["growth"] = Round(s.EnergyGrowth), ["body"] = Body(s.EnergyBody, -1, owners), ["total"] = Round(s.EnergyGrowthTotal),
                ["pushed"] = Round(s.EnergyGrowthPushed),
            },
            ["sources"] = sources,
            ["top"] = top,
        };
    }

    private static JsonObject Event(in PhysicsTraceEvent e, Dictionary<(int, int), (Entity Entity, string? Name)> owners) => new()
    {
        ["step"] = e.Step, ["body"] = Body(e.Body, e.BodyIndex, owners), ["mass"] = Round(e.Mass), ["speed"] = Round(e.Speed),
        ["angularSpeed"] = Round(e.AngularSpeed), ["hitDv"] = Round(e.HitDv), ["main"] = SourceNames[(int)e.Main], ["mainLinear"] = Round(e.MainLinear),
        ["mainAngular"] = Round(e.MainAngular),
    };

    /// <summary>Тело словами: сочленение, сущность и имя бойца (если сочленение — тело бойца), сегмент; нет — null.</summary>
    private static JsonObject? Body(BodyRef body, int index, Dictionary<(int, int), (Entity Entity, string? Name)> owners)
    {
        if (body.Generation == 0) return null;
        var o = new JsonObject { ["articulation"] = string.Create(CultureInfo.InvariantCulture, $"{body.Index}v{body.Generation}") };
        if (owners.TryGetValue((body.Index, body.Generation), out var owner))
        {
            o["entity"] = owner.Entity.ToString();
            o["name"] = owner.Name;
        }
        if (index >= 0) o["segment"] = Segment(index);
        return o;
    }

    private static string Segment(int body) => (uint)body < BodySegs.Bodies ? BodySegs.Name(BodySegs.Segment(body)) : body.ToString(CultureInfo.InvariantCulture);

    /// <summary>Число для ответа: 4 знака; нечисло — null (JSON не несёт NaN).</summary>
    private static double? Round(float x) => float.IsFinite(x) ? Math.Round(x, 4) : null;
}
