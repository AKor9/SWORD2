using System.Runtime.CompilerServices;
using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

public struct Signal;   // событие без полей

[InlineArray(4)]
public struct EntityQuad { private Entity _first; }

public struct Squad { public EntityQuad Members; }

/// <summary>Краевые случаи ECS, найденные ревью: каждый тест воспроизводит найденный дефект.</summary>
public class EdgeCaseTests
{
    [Fact]
    public void FieldlessEvent_ReachesSubscribers()
    {
        var seen = new Reactor<Event<Signal>>("seen");
        var w = Worlds.Builder().Event<Signal>()
            .System("Early", new Sys("emitter", s => s.Emits<Signal>(), (in SystemContext ctx) => ctx.Commands.Emit(new Signal())))
            .System("Late", seen)
            .Build();
        w.RunTick(0);
        w.EmitImmediate(new Signal());
        w.RunTick(1);
        Assert.Equal(3, seen.Seen.Count);
    }

    [Fact]
    public void InlineArrayOfEntity_IsRegistrationError()
    {
        // Встроенный массив виден отражению одним полем — разрешитель подменил бы только первый элемент.
        Assert.Contains("встроенный массив", Assert.Throws<InvalidOperationException>(() =>
            Worlds.Builder().Component<Squad>().Build()).Message);
    }

    [Fact]
    public void EntityItselfAsComponent_IsResolved()
    {
        var w = Worlds.Builder().Component<Entity>()
            .System("Early", new Sys("self", s => s.Creates().Adds<Entity>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick != 0) return;
                var d = ctx.Commands.Create();
                ctx.Commands.Add(d, d.Ref);
            }))
            .Build();
        w.RunTick(0);
        foreach (var chunk in w.Iterate(w.Query().Read<Entity>()))
            Assert.Equal(chunk.Entities[0], chunk.Read<Entity>()[0]);
    }

    [Fact]
    public void QueryBuilder_BranchesDoNotAffectEachOther()
    {
        var w = Worlds.Builder().Build();
        var moving = w.CreateImmediate();
        w.AddImmediate(moving, new Position());
        w.AddImmediate(moving, new Velocity());
        w.AddImmediate(w.CreateImmediate(), new Position());

        var b = w.Query().Read<Position>();
        Query withVelocity = b.Read<Velocity>();
        Query without = b.Without<Velocity>();
        Query all = b;
        Assert.Equal(1, withVelocity.Count());
        Assert.Equal(1, without.Count());
        Assert.Equal(2, all.Count());
    }

    [Fact]
    public void TickAbortedByException_DoesNotLeakIntoNextTick()
    {
        // Первая система записала событие, вторая упала: событие прерванного тика не доставляется в следующем.
        var seen = new Reactor<Event<Pinged>>("seen");
        var w = Worlds.Builder()
            .System("Early", new Sys("emitter", s => s.Emits<Pinged>(), (in SystemContext ctx) =>
                ctx.Commands.Emit(new Pinged { Code = (int)ctx.Tick })))
            .System("Early", new Sys("faulty", s => s.After(new SystemLabel("emitter")), (in SystemContext ctx) =>
            {
                if (ctx.Tick == 0) throw new InvalidOperationException("сбой системы");
            }))
            .System("Late", seen)
            .Build();
        Assert.Throws<InvalidOperationException>(() => w.RunTick(0));
        w.RunTick(1);
        Assert.Equal([1], seen.Seen.Select(x => x.Trigger.Value.Code));
    }

    [Fact]
    public void BarrierError_IsNotReplayedEveryTick()
    {
        // Ошибка на барьере (Add уже имеющегося компонента) не повторяется в следующих тиках и не плодит сущностей.
        Entity victim = default;
        var w = Worlds.Builder()
            .System("Early", new Sys("adder", s => s.Creates().Adds<Health>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick != 0) return;
                ctx.Commands.Create();
                ctx.Commands.Add(victim, new Health { Value = 1 });
            }))
            .Build();
        victim = w.CreateImmediate();
        w.AddImmediate(victim, new Health());
        Assert.Throws<InvalidOperationException>(() => w.RunTick(0));
        int entities = w.EntityCount;
        for (ulong t = 1; t < 6; t++) w.RunTick(t);
        Assert.Equal(entities, w.EntityCount);
    }

    [Fact]
    public void ChurnLargerThanAChunk_DoesNotAllocate()
    {
        // Удалить все и создать 5000 сущностей за тик — несколько чанков опустевают и заполняются заново каждый тик.
        Query? all = null;
        var w = Worlds.Builder()
            .System("Early", new Sys("churn", s => s.Creates().Destroys().Adds<Position>(), (in SystemContext ctx) =>
            {
                foreach (var chunk in ctx.Iterate(all!))
                    foreach (var e in chunk.Entities) ctx.Commands.Destroy(e);
                for (int i = 0; i < 5000; i++) ctx.Commands.Add(ctx.Commands.Create(), new Position());
            }))
            .Build();
        all = w.Query().Read<Position>();
        for (ulong t = 0; t < 50; t++) w.RunTick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 50; t < 150; t++) w.RunTick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"100 тиков выделили {allocated} Б");
    }

    /// <summary>Реактивная на ресурс, читает ещё и журнал событий.</summary>
    private sealed class ContextReader : IReactiveSystem<ResourceChanged<int>>
    {
        public readonly List<(ulong Tick, int Code)> Pings = [];

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("context")).ReadsJournal<Event<Pinged>>();

        public void React(in SystemContext ctx, ReadOnlySpan<ResourceChanged<int>> triggers)
        {
            foreach (var p in ctx.Journal<Event<Pinged>>()) Pings.Add((ctx.Tick, p.Value.Code));
        }
    }

    [Fact]
    public void ReactiveSystem_ExtraJournal_IsContextOfItsFiringTickOnly()
    {
        // Ресурс меняется каждый третий тик, события — каждый тик. В тике срабатывания видны события этого тика —
        // и только они: записи тиков без срабатывания пропущены целиком, а не «частично, что не успело вычиститься».
        var reader = new ContextReader();
        var w = Worlds.Builder().Resource(0)
            .System("Early", new Sys("source", s => s.Emits<Pinged>().SetsResource<int>(), (in SystemContext ctx) =>
            {
                ctx.Commands.Emit(new Pinged { Code = (int)ctx.Tick });
                if (ctx.Tick % 3 == 0) ctx.Commands.SetResource((int)ctx.Tick);
            }))
            .System("Late", reader)
            .Build();
        for (ulong t = 0; t < 7; t++) w.RunTick(t);
        Assert.Equal([(0ul, 0), (3ul, 3), (6ul, 6)], reader.Pings);
    }

    [Fact]
    public void AddOfExisting_CountsAsChangedProducer_ForStageCheck()
    {
        // Без проверок мира Add уже имеющегося компонента заменяет значение — Changed; планировщик это учитывает.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new WorldBuilder { Options = new WorldOptions { Checks = false } }.Stages(Worlds.Stages).Component<Health>()
                .System("Early", new Reactor<Changed<Health>>("consumer"))
                .System("Late", new Sys("adder", s => s.Adds<Health>(), (in SystemContext _) => { }))
                .Build());
        Assert.Contains("Changed", ex.Message);
    }

    [Fact]
    public void StaleDraftInField_IsErrorEvenWithoutChecks()
    {
        EntityDraft kept = default;
        var w = new WorldBuilder { Options = new WorldOptions { Checks = false } }.Stages(Worlds.Stages).Component<Link>()
            .System("Early", new Sys("keeper", s => s.Creates().Adds<Link>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick == 0) kept = ctx.Commands.Create();
                else ctx.Commands.Add(ctx.Commands.Create(), new Link { Target = kept.Ref });
            }))
            .Build();
        w.RunTick(0);
        Assert.Contains("прошлого барьера", Assert.Throws<InvalidOperationException>(() => w.RunTick(1)).Message);
    }

    [Fact]
    public void QueryOfAnotherWorld_IsRejected()
    {
        var a = Worlds.Builder().Build();
        var b = Worlds.Builder().Build();
        Query q = a.Query().Read<Position>();
        Assert.Throws<ArgumentException>(() => b.Iterate(q));
        Assert.Throws<ArgumentException>(() => b.Reader.Iterate(a.Reader.Query().Read<Position>()));
    }

}
