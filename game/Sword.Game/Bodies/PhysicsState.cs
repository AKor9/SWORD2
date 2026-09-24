using System.Numerics;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;

namespace Sword.Game.Bodies;

/// <summary>
/// Участник хеша состояния <c>physics</c> (раздел 7 плана): авторитетное состояние подсистемы Physics симуляции. По слотам
/// сочленений по возрастанию — поколение (у свободного — со знаком минус: от него зависит поколение следующего), у живого —
/// число тел и их слоты; порядок свободных слотов сочленений; по слотам тел — поколение, у живого — сочленение-владелец,
/// режим, форма (радиус, полудлина), положение, поворот, прошлые положение и поворот, скорость и угловая скорость; с 7ж — поза
/// начала тика, масса, обратная масса, обратная инерция, касание земли, профиль динамики, вид толчка (все поля тела 7б); порядок
/// свободных слотов тел (от него зависят хэндлы будущих тел); с 7ж — профили динамики мира (таблица по номерам). Суставы, проходы
/// и отдача (7в, 7д) войдут в 7з — до слияния их члены — заготовки. Кто держит сочленение (<c>BodyRef</c>), хеширует участник
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
        for (int i = 0; i < bodies; i++) Body(ref h, i);
        var freeBodies = physics.FreeBodies;
        h.Add(freeBodies.Length);
        foreach (int slot in freeBodies) h.Add(slot);

        int profiles = physics.DynamicsCount;
        h.Add(profiles);
        for (int p = 0; p < profiles; p++)
        {
            var d = physics.GetDynamics(p);
            h.Add(d.Gravity);
            h.Add(d.LinearDamping);
            h.Add(d.AngularDamping);
            h.Add(d.GroundFriction);
            h.Add(d.GroundAngularFriction);
            h.Add(d.MaxSpeed);
            h.Add(d.MaxAngularSpeed);
        }
    }

    /// <summary>Слот тела <paramref name="i"/> (тело цикла по слотам — свой метод: слотов тел 462 и больше).</summary>
    private void Body(ref StateHasher h, int i)
    {
        h.Add(physics.BodyGenerationAt(i));
        var b = physics.BodyAt(i);
        if (b.IsNone) return;
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
        // 7ж: поля тела 7б — динамика
        Add(ref h, s.StartPosition);
        Add(ref h, s.StartRotation);
        h.Add(s.Mass);
        h.Add(s.InvMass);
        Add(ref h, s.InvInertia);
        h.Add(s.Grounded);
        h.Add((int)s.Profile);
        h.Add((int)s.Source);
    }

    /// <summary>
    /// Сводка (сочленений, тел, слотов, свободных, профилей) и сочленения по слотам (часть — <c>2v1</c>: тела по номеру в сочленении,
    /// у тела — хэндл, режим, форма, постановка, прошлая постановка, скорости; с 7ж — поза начала тика, масса, обратные масса и
    /// инерция, касание земли, профиль, толчок); часть <c>slots</c> — свободные слоты в порядке выдачи с конца и поколения
    /// свободных; часть <c>dynamics</c> (7ж) — профили динамики по номерам. Отбор: сочленение <c>2v1</c> (из <c>BodyRef</c>:
    /// <c>Index</c>v<c>Generation</c>), <c>slots</c> или <c>dynamics</c>. Тело <c>i</c> сочленения бойца — сегмент <c>i</c> (с 7ж): 0…10 —
    /// pelvis…shin.r, 11 — клинок (blade), 12 — foot.l, 13 — foot.r.
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
        w.Summary("dynamicsProfiles", physics.DynamicsCount);
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
        if (filter is null or "dynamics" && Take(ref matched, q))
        {
            w.BeginItem("dynamics");
            for (int p = 0; p < physics.DynamicsCount; p++)
            {
                var d = physics.GetDynamics(p);
                w.BeginObject(p.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.Value("gravity", d.Gravity);
                w.Value("linearDamping", d.LinearDamping);
                w.Value("angularDamping", d.AngularDamping);
                w.Value("groundFriction", d.GroundFriction);
                w.Value("groundAngularFriction", d.GroundAngularFriction);
                w.Value("maxSpeed", d.MaxSpeed);
                w.Value("maxAngularSpeed", d.MaxAngularSpeed);
                w.EndObject();
            }
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
            Write(w, "startPosition", s.StartPosition);
            Write(w, "startRotation", s.StartRotation);
            w.Value("mass", s.Mass);
            w.Value("invMass", s.InvMass);
            Write(w, "invInertia", s.InvInertia);
            w.Value("grounded", s.Grounded);
            w.Value("profile", (int)s.Profile);
            w.Value("source", s.Source.ToString());
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
