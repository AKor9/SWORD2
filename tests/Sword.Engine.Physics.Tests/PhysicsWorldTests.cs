using System.Numerics;
using Sword.Engine.Physics;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Физика этапа 5а: хэндлы тел и сочленений (индекс + поколение, устаревший — исключение, выдача слотов по договору),
/// кинематическая капсула — форма, постановка, цель тика (скорости — разностью постановок) и концы по оси,
/// 0 байт в установившемся режиме, две реализации (экземпляра) в одном процессе независимы.
/// </summary>
public class PhysicsWorldTests
{
    [Fact]
    public void Articulation_BodiesFromFreeSlotsInOrder_NewBodyIsEmpty()
    {
        var w = new PhysicsWorld(articulations: 2, bodies: 4);
        var a = w.CreateArticulation(3);
        Assert.Equal(new ArticulationHandle(0, 1), a);
        Assert.Equal(3, w.BodyCount(a));
        Assert.Equal([new BodyHandle(0, 1), new BodyHandle(1, 1), new BodyHandle(2, 1)], Enumerable.Range(0, 3).Select(i => w.Body(a, i)));
        var b = w.CreateArticulation(3);   // тел не хватает — пул растёт удвоением, младшие номера первыми
        Assert.Equal([3, 4, 5], Enumerable.Range(0, 3).Select(i => w.Body(b, i).Index));
        Assert.Equal((2, 6, 8), (w.ArticulationCount, w.LiveBodyCount, w.BodyCapacity));
        Assert.Equal(b, w.ArticulationOf(w.Body(b, 2)));

        // Новое тело до SetShape и Place: начало координат, без поворота, скоростей и формы.
        var s = w.Get(w.Body(a, 1));
        Assert.Equal((Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero), (s.Position, s.PrevPosition, s.Velocity, s.AngularVelocity));
        Assert.Equal((Quaternion.Identity, Quaternion.Identity), (s.Rotation, s.PrevRotation));
        Assert.Equal((0f, 0f, BodyMode.Kinematic), (s.Radius, s.HalfLength, s.Mode));
    }

    [Fact]
    public void Destroy_StaleHandlesThrowEverywhere_SameSizeReusesSameSlotsInSameOrder()
    {
        var w = new PhysicsWorld();
        var a = w.CreateArticulation(13);
        var keep = w.CreateArticulation(2);
        var bodies = Enumerable.Range(0, 13).Select(i => w.Body(a, i)).ToArray();
        w.DestroyArticulation(a);
        Assert.False(w.IsAlive(a));
        Assert.All(bodies, b => Assert.False(w.IsAlive(b)));
        Assert.Equal((1, 2), (w.ArticulationCount, w.LiveBodyCount));
        Assert.Equal(-1, w.ArticulationGenerationAt(0));
        Assert.Equal(ArticulationHandle.None, w.ArticulationAt(0));
        Assert.Equal(BodyHandle.None, w.BodyAt(bodies[0].Index));

        var body = bodies[4];
        var stale = new Action[]
        {
            () => w.DestroyArticulation(a), () => w.BodyCount(a), () => w.Body(a, 0), () => w.Get(body), () => w.ArticulationOf(body),
            () => w.SetShape(body, 0.1f, 0.2f), () => w.Place(body, Vector3.One, Quaternion.Identity),
            () => w.SetKinematicTarget(body, Vector3.One, Quaternion.Identity, 1f / 120),
            () => w.Get(new BodyHandle(9999, 1)), () => w.BodyCount(new ArticulationHandle(0, 0)),
        };
        Assert.All(stale, call => Assert.Contains("не жив", Assert.Throws<InvalidOperationException>(call).Message));

        var again = w.CreateArticulation(13);
        Assert.Equal(new ArticulationHandle(0, 2), again);   // тот же слот, новое поколение
        Assert.Equal(bodies.Select(b => (b.Index, b.Generation + 1)), Enumerable.Range(0, 13).Select(i => (w.Body(again, i).Index, w.Body(again, i).Generation)));
        Assert.True(w.IsAlive(keep));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Body(again, 13));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.CreateArticulation(0));
    }

    [Fact]
    public void Slots_FreeListsAndGenerations_AreTheStateOfAllocation()
    {
        var w = new PhysicsWorld(articulations: 4, bodies: 8);
        Assert.Equal([3, 2, 1, 0], w.FreeArticulations.ToArray());   // выдача с конца: следующий — 0
        var a = w.CreateArticulation(2);
        var b = w.CreateArticulation(1);
        w.DestroyArticulation(a);
        Assert.Equal([3, 2, 0], w.FreeArticulations.ToArray());
        Assert.Equal([7, 6, 5, 4, 3, 1, 0], w.FreeBodies.ToArray());
        Assert.Equal((-1, 1, 0), (w.ArticulationGenerationAt(0), w.ArticulationGenerationAt(1), w.ArticulationGenerationAt(2)));
        Assert.Equal((-1, -1, 1, 0), (w.BodyGenerationAt(0), w.BodyGenerationAt(1), w.BodyGenerationAt(2), w.BodyGenerationAt(3)));
        Assert.Equal(b, w.ArticulationAt(1));
    }

    [Fact]
    public void ShapePlaceAndEnds_AxisAndEndsFromRotation()
    {
        var w = new PhysicsWorld();
        var body = w.Body(w.CreateArticulation(1), 0);
        w.SetShape(body, 0.065f, 0.14f);
        var rot = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f)), 1.1f);
        var pos = new Vector3(1.5f, 1.2f, -0.4f);
        w.Place(body, pos, rot);
        var s = w.Get(body);
        Assert.Equal((pos, pos, rot, rot), (s.Position, s.PrevPosition, s.Rotation, s.PrevRotation));
        Assert.Equal((Vector3.Zero, Vector3.Zero), (s.Velocity, s.AngularVelocity));
        Assert.Equal((0.065f, 0.14f), (s.Radius, s.HalfLength));
        // Ось — +Y тела в мире, концы — центр ∓ ось · полудлина.
        var axis = Vector3.Transform(Vector3.UnitY, rot);
        Assert.Equal(axis, s.Axis);
        Assert.Equal(pos - axis * 0.14f, s.EndA);
        Assert.Equal(pos + axis * 0.14f, s.EndB);
        Assert.Equal(0.28f, Vector3.Distance(s.EndA, s.EndB), 5);

        foreach (float bad in new[] { -0.1f, float.NaN, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetShape(body, bad, 0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetShape(body, 0.1f, bad));
        }
    }

    [Fact]
    public void KinematicTarget_VelocitiesByDifference()
    {
        const float dt = 1f / 120;
        var w = new PhysicsWorld();
        var body = w.Body(w.CreateArticulation(1), 0);
        var p0 = new Vector3(0.1f, 0.9f, 0.2f);
        var r0 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        w.Place(body, p0, r0);
        var p1 = p0 + new Vector3(0.02f, -0.01f, 0.035f);
        var r1 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.05f) * r0;
        w.SetKinematicTarget(body, p1, r1, dt);
        var s = w.Get(body);
        // Кинематическая цель: прошлая постановка — нынешняя до вызова, скорости — разность за шаг (угловая — вектор поворота / dt), побитово.
        Assert.Equal((p0, r0, p1, r1), (s.PrevPosition, s.PrevRotation, s.Position, s.Rotation));
        Assert.Equal((p1 - p0) / dt, s.Velocity);
        Assert.Equal(PhysicsMath.RotationVector(r0, r1) / dt, s.AngularVelocity);
        // Смысл: поворот на 0,05 рад вокруг Y за тик — 6 рад/с вокруг Y.
        Assert.Equal(0, s.AngularVelocity.X, 3);
        Assert.Equal(0.05f / dt, s.AngularVelocity.Y, 2);
        Assert.Equal(0, s.AngularVelocity.Z, 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetKinematicTarget(body, p1, r1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.SetKinematicTarget(body, p1, r1, float.NaN));
    }

    [Fact]
    public void RotationVector_ShortestPath_SmallAngleBranch()
    {
        Assert.Equal(Vector3.Zero, PhysicsMath.RotationVector(Quaternion.Identity, Quaternion.Identity));
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        // −q — тот же поворот: вектор тот же (кратчайший путь), а не 2π − 0,7.
        Assert.Equal(PhysicsMath.RotationVector(Quaternion.Identity, q), PhysicsMath.RotationVector(Quaternion.Identity, Quaternion.Negate(q)));
        Assert.Equal(0.7f, PhysicsMath.RotationVector(Quaternion.Identity, q).Z, 5);
        // Меньше 1e-7 по длине мнимой части — линейная ветка 2·v.
        var tiny = Quaternion.Normalize(new Quaternion(1e-8f, 0, 0, 1));
        Assert.Equal(new Vector3(tiny.X, tiny.Y, tiny.Z) * 2, PhysicsMath.RotationVector(Quaternion.Identity, tiny));
    }

    [Fact]
    public void TwoInstances_InOneProcess_SameCallsSameState_Independent()
    {
        IPhysicsWorld a = new PhysicsWorld(), b = new PhysicsWorld();
        void Script(IPhysicsWorld w)
        {
            var x = w.CreateArticulation(13);
            var y = w.CreateArticulation(13);
            w.DestroyArticulation(x);
            var z = w.CreateArticulation(5);
            for (int i = 0; i < 5; i++) w.SetKinematicTarget(w.Body(z, i), new Vector3(i, 1, 0), Quaternion.Identity, 0.01f);
            w.SetShape(w.Body(y, 3), 0.1f, 0.2f);
        }
        Script(a);
        Script(b);
        Assert.Equal(a.FreeBodies.ToArray(), b.FreeBodies.ToArray());
        for (int i = 0; i < a.BodyCapacity; i++)
        {
            Assert.Equal(a.BodyAt(i), b.BodyAt(i));
            if (!a.BodyAt(i).IsNone) Assert.Equal(a.Get(a.BodyAt(i)), b.Get(b.BodyAt(i)));
        }
        a.DestroyArticulation(a.ArticulationAt(1));
        Assert.Equal(2, b.ArticulationCount);   // у каждой симуляции свой экземпляр
    }

    [Fact]
    public void SteadyState_33FightersPlacedEveryTick_AndRespawns_DoNotAllocate()
    {
        const int fighters = 33, bodies = 13;
        var w = new PhysicsWorld();
        var arts = new ArticulationHandle[fighters];
        for (int f = 0; f < fighters; f++) arts[f] = w.CreateArticulation(bodies);
        void Tick(int t)
        {
            for (int f = 0; f < fighters; f++)
                for (int i = 0; i < bodies; i++)
                    w.SetKinematicTarget(w.Body(arts[f], i), new Vector3(f, i, t * 0.01f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, t * 0.01f), 1f / 120);
            // Как PageDown/PageUp: одно сочленение уходит и приходит заново того же размера.
            int k = t % fighters;
            w.DestroyArticulation(arts[k]);
            arts[k] = w.CreateArticulation(bodies);
        }
        for (int t = 0; t < 100; t++) Tick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 100; t < 1100; t++) Tick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"1000 тиков 33 × 13 тел выделили {allocated} Б");
        Assert.Equal((fighters, fighters * bodies), (w.ArticulationCount, w.LiveBodyCount));
    }
}
