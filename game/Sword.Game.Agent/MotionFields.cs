using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Вычисляемые поля Motion Matching бойца (8з; часть <see cref="BodyFields"/> — поля игры для <c>watch.add</c>, <c>entity.history</c>,
/// <c>replay.observe</c>, условий и метрик манифестов) по виду <see cref="IMotionMatchView"/>: <c>MotionMatch.FootSlide</c> — скольжение
/// опорной стопы, м/с (наибольшая горизонтальная скорость в мире суставов контакта на земле по разметке кадра базы; ни одной стопы на
/// земле — NaN: метрика окна — с <c>skipUndefined</c>), <c>MotionMatch.JointJump</c> — наибольший скачок сустава выхода проигрывателя за тик,
/// м, <c>MotionMatch.FeetOnGround</c> — суставов контакта на земле. Боец не на Motion Matching (враг, <c>locomotion.player = clips</c>),
/// мёртвый, не начатый — NaN. Считается по запросу из состояния тика (<see cref="MotionMatchMeasure"/>): в тике ничего не стоит.
/// </summary>
public static class MotionFields
{
    public const string Prefix = "MotionMatch.";

    public static readonly string[] Known = ["MotionMatch.FootSlide|JointJump|FeetOnGround"];

    /// <summary>Читатель поля <paramref name="path"/>; не поле Motion Matching (другой префикс) — null; ошибка в пути — исключение.</summary>
    public static EntityFieldReader? Compile(string path, string at)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return path[Prefix.Length..] switch
        {
            "FootSlide" => (in ServiceContext ctx, Entity e) => Measure(ctx, e).FootSlide,
            "JointJump" => (in ServiceContext ctx, Entity e) => Measure(ctx, e).JointJump,
            "FeetOnGround" => (in ServiceContext ctx, Entity e) => Measure(ctx, e) is { JointJump: var jump } m && float.IsFinite(jump) ? m.FeetOnGround : double.NaN,
            _ => throw new ArgumentException($"{at}: {path} — поле Motion Matching: {string.Join(" | ", Known)}"),
        };
    }

    private static MotionMatchMeasure Measure(in ServiceContext ctx, Entity e) =>
        ctx.World.IsAlive(e) && ctx.World.TryView<IMotionMatchView>(out var view) ? view.Measure(e) : MotionMatchMeasure.None;
}
