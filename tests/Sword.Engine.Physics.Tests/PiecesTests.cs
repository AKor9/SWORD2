using System.Numerics;
using Sword.Engine.Physics;

namespace Sword.Engine.Physics.Tests;

/// <summary>Куски тела: тело в конец сочленения, сустав с готовой привязкой, удаление тел с конца — для отделения частей.</summary>
public sealed class PiecesTests
{
    private static PhysicsWorld World()
    {
        var w = new PhysicsWorld(4, 16);
        w.ReplaceDynamics(0, TestBodies.Vacuum);
        return w;
    }

    [Fact]
    public void AddBody_AppendsKinematicBody_KeepsJointsAndPairs()
    {
        var w = World();
        var a = w.CreateArticulation(3);
        for (int k = 0; k < 3; k++) w.Set(w.Body(a, k), TestBodies.Capsule(1, 0.05f, 0.1f, new Vector3(0, k * 0.3f, 0)));
        w.SetJoints(a, [JointDesc.SwingTwist(0, 1, new(0, -0.1f, 0), -1, 1, 1, 1), JointDesc.SwingTwist(1, 2, new(0, -0.1f, 0), -1, 1, 1, 1)]);
        w.SetSelfContacts(a, [new BodyPair(0, 2)]);
        var before = w.Binding(a, 1);

        int added = w.AddBody(a);

        Assert.Equal(3, added);
        Assert.Equal(4, w.BodyCount(a));
        Assert.Equal(BodyMode.Kinematic, w.Get(w.Body(a, 3)).Mode);
        Assert.Equal(a, w.ArticulationOf(w.Body(a, 3)));
        Assert.Equal(2, w.JointCount(a));
        Assert.Equal(before, w.Binding(a, 1));
        Assert.Equal([new BodyPair(0, 2)], w.SelfContacts(a).ToArray());
        w.SetSelfContacts(a, [new BodyPair(0, 2), new BodyPair(0, 3), new BodyPair(1, 3), new BodyPair(2, 3)]);   // место под новые пары
    }

    [Fact]
    public void SetJoint_KeepsGivenBinding_AndRetargetsParent()
    {
        var w = World();
        var a = w.CreateArticulation(3);
        for (int k = 0; k < 3; k++) w.Set(w.Body(a, k), TestBodies.Capsule(1, 0.05f, 0.1f, new Vector3(0, k * 0.3f, 0)));
        w.SetJoints(a, [JointDesc.SwingTwist(0, 1, new(0, -0.1f, 0), -1, 1, 1, 1), JointDesc.SwingTwist(1, 2, new(0, -0.1f, 0), -1, 1, 1, 1)]);
        var binding = new JointBinding(new Vector3(0, 0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f));

        w.SetJoint(a, 1, JointDesc.SwingTwist(0, 2, new(0, -0.1f, 0), -1, 1, 1, 1), binding);

        Assert.Equal(0, w.Joint(a, 1).Parent);
        Assert.Equal(binding.ParentAnchor, w.Binding(a, 1).ParentAnchor);
        Assert.True(Quaternion.Dot(binding.Rest, w.Binding(a, 1).Rest) > 0.9999f);
        // Добавление в конец и запрет второго сустава у того же ребёнка.
        w.AddBody(a);
        w.SetJoint(a, 2, JointDesc.SwingTwist(2, 3, new(0, -0.1f, 0), -1, 1, 1, 1), binding);
        Assert.Equal(3, w.JointCount(a));
        Assert.Throws<ArgumentException>(() => w.SetJoint(a, 3, JointDesc.SwingTwist(0, 3, new(0, -0.1f, 0), -1, 1, 1, 1), binding));
    }

    [Fact]
    public void RemoveBodies_DropsTailBodiesJointsAndPairs_SlotsReturnFromEnd()
    {
        var w = World();
        var a = w.CreateArticulation(2);
        int b2 = w.AddBody(a), b3 = w.AddBody(a);
        var h3 = w.Body(a, b3);
        for (int k = 0; k < 4; k++) w.Set(w.Body(a, k), TestBodies.Capsule(1, 0.05f, 0.1f, new Vector3(0, k * 0.3f, 0)));
        w.SetJoints(a, [JointDesc.SwingTwist(0, 1, new(0, -0.1f, 0), -1, 1, 1, 1), JointDesc.SwingTwist(1, b2, new(0, -0.1f, 0), -1, 1, 1, 1),
            JointDesc.SwingTwist(b2, b3, new(0, -0.1f, 0), -1, 1, 1, 1)]);
        w.SetSelfContacts(a, [new BodyPair(0, b3), new BodyPair(0, 1)]);

        w.RemoveBodies(a, 2);

        Assert.Equal(2, w.BodyCount(a));
        Assert.False(w.IsAlive(h3));
        Assert.Equal(1, w.JointCount(a));
        Assert.Equal([new BodyPair(0, 1)], w.SelfContacts(a).ToArray());
        // Следующее тело — тот же слот, что ушёл последним (возврат с конца, как у удаления сочленения).
        int again = w.AddBody(a);
        Assert.Equal(2, again);
        w.Step(1f / 120);
    }

    /// <summary>
    /// Кусок и культя, разделённые с сохранением скоростей материи, двигаются дальше как одно тело, пока не разошлись: суммарный
    /// импульс шага без внешних сил сохраняется.
    /// </summary>
    [Fact]
    public void SplitBodies_ConserveMomentum_WithoutForces()
    {
        var w = World();
        var a = w.CreateArticulation(1);
        var s = TestBodies.Capsule(2, 0.05f, 0.2f);
        s.Velocity = new Vector3(1, 0, 0);
        w.Set(w.Body(a, 0), s);
        // Деление: культя — нижняя половина, кусок — верхняя; угловой скорости нет — скорость та же.
        int piece = w.AddBody(a);
        var stump = TestBodies.Capsule(1, 0.05f, 0.08f, new Vector3(0, -0.1f, 0));
        stump.Velocity = s.Velocity;
        var top = TestBodies.Capsule(1, 0.05f, 0.08f, new Vector3(0, 0.1f, 0));
        top.Velocity = s.Velocity;
        w.Set(w.Body(a, 0), stump);
        w.Set(w.Body(a, piece), top);
        var p0 = Momentum(w, a);
        for (int i = 0; i < 60; i++) w.Step(1f / 120);
        Assert.True(Vector3.Distance(p0, Momentum(w, a)) < 1e-3f, $"{p0} → {Momentum(w, a)}");
    }

    private static Vector3 Momentum(PhysicsWorld w, ArticulationHandle a)
    {
        var p = Vector3.Zero;
        for (int k = 0; k < w.BodyCount(a); k++)
        {
            var b = w.Get(w.Body(a, k));
            p += b.Velocity * b.Mass;
        }
        return p;
    }
}
