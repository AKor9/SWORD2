using System.Numerics;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;

namespace Sword.Game.Bodies;

/// <summary>
/// Участник хеша состояния <c>physics</c> (раздел 7 плана): авторитетное состояние подсистемы Physics симуляции. По слотам
/// сочленений по возрастанию — поколение (у свободного — со знаком минус: от него зависит поколение следующего), у живого —
/// число тел и их слоты; порядок свободных слотов сочленений; по слотам тел — поколение, у живого — сочленение-владелец,
/// режим, форма (радиус, полудлина), положение, поворот, прошлые положение и поворот, скорость и угловая скорость; порядок
/// свободных слотов тел (от него зависят хэндлы будущих тел). Кто держит сочленение (<c>BodyRef</c>), хеширует участник
/// <c>ecs</c>. <see cref="Describe"/> — ровно эти поля.
/// </summary>
public sealed class PhysicsState(IPhysicsWorld physics) : IStateContributor
{
    public string Id => "physics";

    public void AppendHash(ref StateHasher h)
    {
        int arts = physics.ArticulationCapacity;
        h.Add(arts);
        for (int i = 0; i < arts; i++)
        {
            h.Add(physics.ArticulationGenerationAt(i));
            var a = physics.ArticulationAt(i);
            if (a.IsNone) continue;
            int n = physics.BodyCount(a);
            h.Add(n);
            for (int k = 0; k < n; k++) h.Add(physics.Body(a, k).Index);
        }
        var freeArts = physics.FreeArticulations;
        h.Add(freeArts.Length);
        foreach (int slot in freeArts) h.Add(slot);

        int bodies = physics.BodyCapacity;
        h.Add(bodies);
        for (int i = 0; i < bodies; i++)
        {
            h.Add(physics.BodyGenerationAt(i));
            var b = physics.BodyAt(i);
            if (b.IsNone) continue;
            h.Add(physics.ArticulationOf(b).Index);
            var s = physics.Get(b);
            h.Add((int)s.Mode);
            h.Add(s.Radius);
            h.Add(s.HalfLength);
            Add(ref h, s.Position);
            Add(ref h, s.Rotation);
            Add(ref h, s.PrevPosition);
            Add(ref h, s.PrevRotation);
            Add(ref h, s.Velocity);
            Add(ref h, s.AngularVelocity);
        }
        var freeBodies = physics.FreeBodies;
        h.Add(freeBodies.Length);
        foreach (int slot in freeBodies) h.Add(slot);
    }

    /// <summary>
    /// Сводка (сочленений, тел, слотов, свободных) и сочленения по слотам (часть — <c>2v1</c>: тела по номеру в сочленении,
    /// у тела — хэндл, режим, форма, постановка, прошлая постановка, скорости); последняя часть <c>slots</c> — свободные слоты
    /// в порядке выдачи с конца и поколения свободных. Отбор: сочленение <c>2v1</c> (из <c>BodyRef</c>: <c>Index</c>v<c>Generation</c>)
    /// или <c>slots</c>. Тело <c>i</c> сочленения бойца — сегмент <c>BodySegs.Segment(i)</c>: 0…10 — pelvis…shin.r, 11 — foot.l,
    /// 12 — foot.r.
    /// </summary>
    public void Describe(StateWriter w, in DescribeQuery q)
    {
        string? filter = q.Filter is { Length: > 0 } f ? f : null;
        w.Summary("articulations", physics.ArticulationCount);
        w.Summary("bodies", physics.LiveBodyCount);
        w.Summary("articulationCapacity", physics.ArticulationCapacity);
        w.Summary("bodyCapacity", physics.BodyCapacity);
        w.Summary("freeArticulations", physics.FreeArticulations.Length);
        w.Summary("freeBodies", physics.FreeBodies.Length);
        int matched = 0;
        for (int i = 0; i < physics.ArticulationCapacity; i++)
        {
            var a = physics.ArticulationAt(i);
            if (a.IsNone || filter is not null && a.ToString() != filter) continue;
            if (Take(ref matched, q)) WriteArticulation(w, a);
        }
        if (filter is null or "slots" && Take(ref matched, q))
        {
            w.BeginItem("slots");
            w.Values("freeArticulations", physics.FreeArticulations);
            w.Values("freeArticulationGenerations", Generations(physics.FreeArticulations, physics.ArticulationGenerationAt));
            w.Values("freeBodies", physics.FreeBodies);
            w.Values("freeBodyGenerations", Generations(physics.FreeBodies, physics.BodyGenerationAt));
            w.EndItem();
        }
        w.Matched = matched;
    }

    private static bool Take(ref int matched, in DescribeQuery q)
    {
        int at = matched++;
        return at >= q.Skip && at < q.Skip + q.Take;
    }

    private static int[] Generations(ReadOnlySpan<int> slots, Func<int, int> generation)
    {
        var g = new int[slots.Length];
        for (int i = 0; i < g.Length; i++) g[i] = generation(slots[i]);
        return g;
    }

    private void WriteArticulation(StateWriter w, ArticulationHandle a)
    {
        w.BeginItem(a.ToString());
        int n = physics.BodyCount(a);
        w.Value("bodyCount", n);
        for (int k = 0; k < n; k++)
        {
            var b = physics.Body(a, k);
            var s = physics.Get(b);
            w.BeginObject(k.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.Value("body", b.ToString());
            w.Value("mode", s.Mode.ToString());
            w.Value("radius", s.Radius);
            w.Value("halfLength", s.HalfLength);
            Write(w, "position", s.Position);
            Write(w, "rotation", s.Rotation);
            Write(w, "prevPosition", s.PrevPosition);
            Write(w, "prevRotation", s.PrevRotation);
            Write(w, "velocity", s.Velocity);
            Write(w, "angularVelocity", s.AngularVelocity);
            w.EndObject();
        }
        w.EndItem();
    }

    private static void Add(ref StateHasher h, Vector3 v)
    {
        h.Add(v.X);
        h.Add(v.Y);
        h.Add(v.Z);
    }

    private static void Add(ref StateHasher h, Quaternion q)
    {
        h.Add(q.X);
        h.Add(q.Y);
        h.Add(q.Z);
        h.Add(q.W);
    }

    private static void Write(StateWriter w, string name, Vector3 v)
    {
        w.BeginObject(name);
        w.Value("x", v.X);
        w.Value("y", v.Y);
        w.Value("z", v.Z);
        w.EndObject();
    }

    private static void Write(StateWriter w, string name, Quaternion q)
    {
        w.BeginObject(name);
        w.Value("x", q.X);
        w.Value("y", q.Y);
        w.Value("z", q.Z);
        w.Value("w", q.W);
        w.EndObject();
    }
}
