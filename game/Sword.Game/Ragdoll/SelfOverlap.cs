using Sword.Engine.Geometry;
using Sword.Engine.Physics;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Замер самоколлизии трупа: насколько части одного тела вошли друг в друга — по парам самоколлизии сочленения
/// (<see cref="IPhysicsWorld.SelfContacts"/>, у трупа — <see cref="CorpseJoints.SelfPairs"/>), капсулы с радиусами × толщина. Читают инвариант
/// <c>physics.selfContacts</c>, вид диагностики физики (агент — <c>physics.bodies</c> → <c>self</c>) и тесты. Памяти не выделяет.
/// </summary>
public static class SelfOverlap
{
    /// <summary>Итог замера: пар в таблице, пересекшихся пар, самая глубокая пара (номера тел; нет — −1) и её глубина, м (нет — 0).</summary>
    public readonly record struct Result(int Pairs, int Touching, int A, int B, float Depth);

    /// <summary>Глубина пары капсул тел <paramref name="a"/> и <paramref name="b"/> с радиусами × <paramref name="scale"/>, м (не касаются — ≤ 0).</summary>
    public static float Depth(in BodyState a, in BodyState b, float scale)
    {
        float d2 = Segments.SegSeg(a.EndA, a.EndB, b.EndA, b.EndB, out _, out _);
        return (a.Radius + b.Radius) * scale - MathF.Sqrt(d2);
    }

    /// <summary>
    /// Самая глубокая пара самоколлизии сочленения <paramref name="articulation"/> в нынешней позе (радиусы × <paramref name="scale"/>; 1 —
    /// капсулы как есть). <paramref name="simulatedOnly"/> — только пары, где оба тела на физике (действующие).
    /// </summary>
    public static Result Deepest(IPhysicsWorld physics, ArticulationHandle articulation, float scale, bool simulatedOnly = false)
    {
        var pairs = physics.SelfContacts(articulation);
        int touching = 0, a = -1, b = -1;
        float deepest = 0;
        foreach (var p in pairs)
        {
            var sa = physics.Get(physics.Body(articulation, p.A));
            var sb = physics.Get(physics.Body(articulation, p.B));
            if (simulatedOnly && (sa.Mode != BodyMode.Simulated || sb.Mode != BodyMode.Simulated)) continue;
            float depth = Depth(sa, sb, scale);
            if (!(depth > 0)) continue;
            touching++;
            if (depth > deepest) (deepest, a, b) = (depth, p.A, p.B);
        }
        return new Result(pairs.Length, touching, a, b, deepest);
    }
}
