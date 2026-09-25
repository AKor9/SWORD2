using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;

namespace Sword.Game.Agent;

/// <summary>
/// Вычисляемые поля тела бойца целиком (7з; часть <see cref="BodyFields"/>): <c>Body.Com[.X|.Y|.Z]</c> — центр масс тел, м;
/// <c>Body.ComVelocity[.X|.Y|.Z]</c> — его скорость, м/с (без компоненты — длина); <c>Body.KineticEnergy</c> — кинетическая энергия тел, Дж
/// (поступательная и вращательная); <c>Body.Energy</c> — полная (плюс <c>m·g·y</c>), Дж. Для трупа — «успокоился ли» и «не растёт ли энергия в
/// покое» (<c>watch.add</c>, <c>entity.history</c>, метрики и проверки манифеста фичи «Рэгдолл»); у живого — те же величины кинематических тел.
/// 7з-3 — сон: <c>Body.Asleep</c> — 1, если труп спит (физика его не считает), иначе 0; <c>Body.RestTime</c> — счётчик покоя, с. 7з-4 —
/// самоколлизия: <c>Body.SelfDepth</c> — насколько глубже всего часть тела вошла в свою другую часть (действующие пары самоколлизии — оба тела на
/// физике, капсулы как F7), м; пар нет или живой (тела кинематические, в том числе возрождённый) — 0. Считает <see cref="CorpseMetrics"/> по виду тел (самоколлизию — вид диагностики физики). Нет тела — NaN.
/// </summary>
public static class CorpseFields
{
    public static readonly string[] Known = ["Body.Com|ComVelocity[.X|.Y|.Z]", "Body.KineticEnergy|Energy", "Body.Asleep|RestTime", "Body.SelfDepth"];

    /// <summary>Читатель поля <paramref name="rest"/> (путь после <c>Body.</c>); не поле тела целиком — null; ошибка в пути — исключение.</summary>
    public static EntityFieldReader? Compile(string rest, string path, string at)
    {
        switch (rest)
        {
            case "KineticEnergy": return (in ServiceContext ctx, Entity e) => Totals(ctx, e) is { } t ? t.KineticEnergy : double.NaN;
            case "Energy": return (in ServiceContext ctx, Entity e) => Totals(ctx, e) is { } t ? t.Energy : double.NaN;
            case "Asleep": return (in ServiceContext ctx, Entity e) => Body(ctx, e, out var view, out var r) ? view.IsAsleep(r) ? 1 : 0 : double.NaN;
            case "RestTime": return (in ServiceContext ctx, Entity e) => Body(ctx, e, out var view, out var r) ? view.RestTime(r) : double.NaN;
            case "SelfDepth":
                return (in ServiceContext ctx, Entity e) => Body(ctx, e, out _, out var r) && ctx.World.TryView<IPhysicsDebugView>(out var debug)
                    ? debug.Self(r).Depth : double.NaN;
        }
        string[] parts = rest.Split('.');
        if (parts[0] is not ("Com" or "ComVelocity")) return null;
        int component = parts.Length == 1 ? -1 : parts.Length == 2 ? parts[1] switch { "X" => 0, "Y" => 1, "Z" => 2, _ => -2 } : -2;
        if (component < -1) throw new ArgumentException($"{at}: {path} — поле тела целиком: {string.Join(" | ", Known)}");
        bool velocity = parts[0] == "ComVelocity";
        return (in ServiceContext ctx, Entity e) =>
        {
            if (Totals(ctx, e) is not { } t) return double.NaN;
            var v = velocity ? t.ComVelocity : t.Com;
            return component switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.Length() };
        };
    }

    private static CorpseMetrics.Totals? Totals(in ServiceContext ctx, Entity e)
    {
        var world = ctx.World;
        if (!world.IsAlive(e) || !world.Knows<BodyRef>() || !world.TryGet<BodyRef>(e, out var r)) return null;
        var t = CorpseMetrics.Of(world.View<IBodyView>(), r);
        return t.Mass > 0 ? t : null;
    }

    /// <summary>Тело бойца живо (7з-3): вид тел и ссылка на тело.</summary>
    private static bool Body(in ServiceContext ctx, Entity e, out IBodyView view, out BodyRef body)
    {
        var world = ctx.World;
        view = null!;
        body = default;
        if (!world.IsAlive(e) || !world.Knows<BodyRef>() || !world.TryGet<BodyRef>(e, out body)) return false;
        view = world.View<IBodyView>();
        return view.BodyCount(body) > 0;
    }
}
