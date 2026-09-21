using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

public class StorageTests
{
    [Fact]
    public void SameStableName_FromTwoAssemblies_IsAnError()
    {
        // Тип с тем же полным именем, что Position, но из другой (динамической) сборки.
        var module = System.Reflection.Emit.AssemblyBuilder
            .DefineDynamicAssembly(new System.Reflection.AssemblyName("Twin"), System.Reflection.Emit.AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Twin");
        var tb = module.DefineType(typeof(Position).FullName!,
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed | System.Reflection.TypeAttributes.SequentialLayout,
            typeof(ValueType));
        tb.DefineField("Value", typeof(int), System.Reflection.FieldAttributes.Public);
        var twin = tb.CreateType();

        var builder = Worlds.Builder();
        typeof(WorldBuilder).GetMethod(nameof(WorldBuilder.Component))!.MakeGenericMethod(twin).Invoke(builder, null);
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("стабильным именем " + typeof(Position).FullName, ex.Message);
    }

    [Fact]
    public void Reader_SeesWorld_KnowsRegisteredComponents()
    {
        var w = Worlds.Builder().Resource(7).Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Position { Value = new Vector3(1, 2, 3) });
        w.AddImmediate(e, new Frozen());
        var r = w.Reader;

        Assert.True(r.Knows<Position>());
        Assert.False(r.Knows<Guid>());
        Assert.Equal(1, r.EntityCount);
        Assert.True(r.Has<Frozen>(e));
        Assert.Equal(new Vector3(1, 2, 3), r.Get<Position>(e).Value);
        Assert.Equal(7, r.Resource<int>());

        ReadQuery q = r.Query().With<Frozen>().Read<Position>().Without<Velocity>();
        int rows = 0;
        foreach (var chunk in r.Iterate(q))
        {
            Assert.Equal(e, chunk.Entities[0]);
            Assert.Equal(3f, chunk.Read<Position>()[0].Value.Z);
            rows += chunk.Count;
        }
        Assert.Equal(1, rows);
        Assert.Equal(1, q.Count());
        Assert.Throws<InvalidOperationException>(() => r.Query().With<Guid>());
    }

    [Fact]
    public void CreateGetDestroy_StaleHandleIsDead()
    {
        var w = Worlds.Builder().Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Position { Value = new Vector3(1, 2, 3) });
        Assert.True(w.IsAlive(e));
        Assert.Equal(new Vector3(1, 2, 3), w.Get<Position>(e).Value);
        Assert.False(w.Has<Velocity>(e));

        w.DestroyImmediate(e);
        Assert.False(w.IsAlive(e));
        var again = w.CreateImmediate();
        Assert.Equal(e.Index, again.Index);             // номер переиспользован
        Assert.NotEqual(e.Generation, again.Generation); // а старый хэндл распознаётся
        Assert.False(w.IsAlive(e));
        Assert.True(w.IsAlive(again));
        Assert.False(Entity.Null == again);
    }

    [Fact]
    public void MovesBetweenArchetypes_KeepValues_AndRowsStayDense()
    {
        var w = Worlds.Builder().Build();
        var list = new List<Entity>();
        for (int i = 0; i < 3000; i++)
        {
            var e = w.CreateImmediate();
            w.AddImmediate(e, new Position { Value = new Vector3(i, 0, 0) });
            if (i % 3 == 0) w.AddImmediate(e, new Velocity { Value = new Vector3(0, i, 0) });
            list.Add(e);
        }
        for (int i = 0; i < 3000; i += 2) w.DestroyImmediate(list[i]);
        for (int i = 1; i < 3000; i += 4) w.RemoveImmediate<Position>(list[i]);

        for (int i = 0; i < 3000; i++)
        {
            var e = list[i];
            if (i % 2 == 0) { Assert.False(w.IsAlive(e)); continue; }
            Assert.Equal(i % 4 != 1, w.Has<Position>(e));
            if (i % 4 != 1) Assert.Equal(i, w.Get<Position>(e).Value.X);
            Assert.Equal(i % 3 == 0, w.Has<Velocity>(e));
            if (i % 3 == 0) Assert.Equal(i, w.Get<Velocity>(e).Value.Y);
        }
        Assert.Equal(1500, w.EntityCount);
    }

    [Fact]
    public void Query_ReadWriteWithWithout()
    {
        var w = Worlds.Builder().Build();
        for (int i = 0; i < 100; i++)
        {
            var e = w.CreateImmediate();
            w.AddImmediate(e, new Position());
            w.AddImmediate(e, new Velocity { Value = Vector3.UnitX });
            if (i % 10 == 0) w.AddImmediate(e, new Frozen());
        }
        Query moving = w.Query().Write<Position>().Read<Velocity>().Without<Frozen>();
        foreach (var chunk in w.Iterate(moving))
        {
            var p = chunk.Write<Position>();
            var v = chunk.Read<Velocity>();
            for (int i = 0; i < chunk.Count; i++) p[i].Value += v[i].Value;
        }
        Assert.Equal(90, moving.Count());
        Query frozen = w.Query().With<Frozen>().Read<Position>();
        float sum = 0;
        foreach (var chunk in w.Iterate(frozen))
            foreach (var p in chunk.Read<Position>()) sum += p.Value.X;
        Assert.Equal(0, sum);
        Assert.Equal(10, frozen.Count());
    }

    [Fact]
    public void TypeIds_DoNotDependOnRegistrationOrder()
    {
        var a = new WorldBuilder().Stages("S").Component<Velocity>().Component<Position>().Component<Health>().Build();
        var b = new WorldBuilder().Stages("S").Component<Health>().Component<Position>().Component<Velocity>().Build();
        // Одинаковые действия — одинаковые номера сущностей и одинаковое содержимое.
        foreach (var w in new[] { a, b })
        {
            var e = w.CreateImmediate();
            w.AddImmediate(e, new Position { Value = Vector3.One });
            w.AddImmediate(e, new Health { Value = 5 });
        }
        Assert.Equal(a.Systems.Count, b.Systems.Count);
        var ea = new Entity(0, 1);
        Assert.Equal(a.Get<Position>(ea), b.Get<Position>(ea));
        Assert.Equal(a.Get<Health>(ea), b.Get<Health>(ea));
    }

    [Fact]
    public void Tag_HasNoColumn_AndUnregisteredComponentIsAnError()
    {
        var w = Worlds.Builder().Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Frozen());
        Assert.True(w.Has<Frozen>(e));
        Query q = w.Query().With<Frozen>();
        foreach (var chunk in w.Iterate(q))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => { chunk.Read<Frozen>(); });
            Assert.Contains("тег", ex.Message);
        }
        Assert.Throws<InvalidOperationException>(() => w.Has<Unregistered>(e));
    }

    [Fact]
    public void ReactiveComponent_CannotBeWrittenInPlace()
    {
        var w = Worlds.Builder().Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 1 });
        Query q = w.Query().Write<Health>();
        foreach (var chunk in w.Iterate(q))
            Assert.Contains("реактивный", Assert.Throws<InvalidOperationException>(() => { chunk.Write<Health>(); }).Message);
    }

    [Fact]
    public void AddExistingComponent_IsAnError_UseSet()
    {
        var w = Worlds.Builder().Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Position());
        Assert.Contains("Set", Assert.Throws<InvalidOperationException>(() => w.AddImmediate(e, new Position())).Message);
    }

    [Fact]
    public void DeferredValueInWorldAccess_IsAnError()
    {
        var w = Worlds.Builder().Build();
        Assert.Contains("отложенное", Assert.Throws<InvalidOperationException>(() => w.IsAlive(new EntityDraft(0, 1, 0).Ref)).Message);
    }

    private struct Unregistered { public int X = 1; public Unregistered() { } }
}
