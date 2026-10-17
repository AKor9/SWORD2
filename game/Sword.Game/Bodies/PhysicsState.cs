using System.Numerics;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;

namespace Sword.Game.Bodies;

/// <summary>
/// Участник хеша <c>physics</c>: всё, от чего зависит следующий шаг физики — слоты и поколения сочленений и тел, все поля тел,
/// порядок свободных слотов, профили динамики, суставы, отдача, сон, пары самоколлизии и проходы. Выводимое (границы, настройки,
/// журнал подшага) не хешируется; владельцев (<c>BodyRef</c>) хеширует <c>ecs</c>.
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
            Joints(ref h, a);
            SelfContacts(ref h, a);
            Add(ref h, physics.Reaction(a));
            h.Add(physics.IsAsleep(a));   // сон и счётчик покоя — от них зависит следующий шаг
            h.Add(physics.RestTime(a));
        }
        Passes(ref h, arts);
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

    /// <summary>Суставы сочленения: описание, привязка, включён ли — по порядку решения.</summary>
    private void Joints(ref StateHasher h, ArticulationHandle a)
    {
        int n = physics.JointCount(a);
        h.Add(n);
        for (int j = 0; j < n; j++)
        {
            var d = physics.Joint(a, j);
            h.Add(d.Parent);
            h.Add(d.Child);
            Add(ref h, d.ChildAnchor);
            h.Add((int)d.Kind);
            h.Add(d.BendMin);
            h.Add(d.BendMax);
            h.Add(d.AxisSlack);
            Add(ref h, d.HingeAxis);
            h.Add((int)d.BendSign);
            h.Add(d.SwingXMin);
            h.Add(d.SwingXMax);
            h.Add(d.SwingZ);
            h.Add(d.Twist);
            h.Add(d.LimitCompliance.HasValue);
            h.Add(d.LimitCompliance ?? 0);
            var binding = physics.Binding(a, j);
            Add(ref h, binding.ParentAnchor);
            Add(ref h, binding.Rest);
            h.Add(physics.IsJointEnabled(a, j));
        }
    }

    /// <summary>Пары самоколлизии сочленения — по порядку решения.</summary>
    private void SelfContacts(ref StateHasher h, ArticulationHandle a)
    {
        var pairs = physics.SelfContacts(a);
        h.Add(pairs.Length);
        foreach (var p in pairs)
        {
            h.Add(p.A);
            h.Add(p.B);
        }
    }

    /// <summary>Проходы пар живых сочленений «толкающий → цель» — по слотам, толкающий ≠ цель (тело цикла по толкающим — свой метод).</summary>
    private void Passes(ref StateHasher h, int arts)
    {
        for (int i = 0; i < arts; i++)
        {
            var pusher = physics.ArticulationAt(i);
            if (!pusher.IsNone) PassesOf(ref h, pusher, arts);
        }
    }

    private void PassesOf(ref StateHasher h, ArticulationHandle pusher, int arts)
    {
        for (int j = 0; j < arts; j++)
        {
            var target = physics.ArticulationAt(j);
            if (target.IsNone || j == pusher.Index) continue;
            var p = physics.Pass(pusher, target);
            h.Add(p.Strike);
            h.Add(p.Number);
            h.Add(p.Inside);
            h.Add(p.Potential);
            h.Add(p.Remaining);
            h.Add(p.EmptyTicks);
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
        // поля динамики тела
        Add(ref h, s.StartPosition);
        Add(ref h, s.StartRotation);
        h.Add(s.Mass);
        h.Add(s.InvMass);
        Add(ref h, s.InvInertia);
        h.Add(s.Grounded);
        h.Add((int)s.Profile);
        h.Add((int)s.Source);
        var probes = physics.GroundProbes(b);
        h.Add(probes.Length);
        foreach (var p in probes)
        {
            h.Add(p.X);
            h.Add(p.Y);
            h.Add(p.Z);
            h.Add(p.W);
        }
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
}
