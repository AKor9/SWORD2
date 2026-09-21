using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

/// <summary>Граничные тесты журналов — обязательный список раздела 5 плана.</summary>
public class JournalTests
{
    // ── порядок стадий производителя и потребителя ─────────────────────────

    private static Sys Pinger(string label, Func<ulong, bool> when) =>
        new(label, s => s.Emits<Pinged>(), (in SystemContext ctx) =>
        {
            if (when(ctx.Tick)) ctx.Commands.Emit(new Pinged { Code = (int)ctx.Tick });
        });

    [Fact]
    public void ProducerInEarlierStage_SeenInSameTick()
    {
        var consumer = new Reactor<Event<Pinged>>("consumer");
        var w = Worlds.Builder()
            .System("Early", Pinger("producer", t => t == 5))
            .System("Late", consumer)
            .Build();
        for (ulong t = 0; t < 10; t++) w.RunTick(t);
        Assert.Equal([(5ul, 5)], consumer.Seen.Select(x => (x.Tick, x.Trigger.Value.Code)));
    }

    [Theory]
    [InlineData("Late")]   // та же стадия
    [InlineData("End")]    // более поздняя
    public void ProducerInSameOrLaterStage_WithoutNextTickOk_IsBuildError(string producerStage)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Worlds.Builder()
            .System(producerStage, Pinger("producer", _ => true))
            .System("Late", new Reactor<Event<Pinged>>("consumer"))
            .Build());
        Assert.Contains("consumer@Late", ex.Message);
        Assert.Contains("NextTickOk", ex.Message);
    }

    [Theory]
    [InlineData("Late")]
    [InlineData("End")]
    public void ProducerInSameOrLaterStage_WithNextTickOk_SeenNextTick(string producerStage)
    {
        var consumer = new Reactor<Event<Pinged>>("consumer", s => s.NextTickOk<Event<Pinged>>());
        var w = Worlds.Builder()
            .System(producerStage, Pinger("producer", t => t == 5))
            .System("Late", consumer)
            .Build();
        for (ulong t = 0; t < 10; t++) w.RunTick(t);
        Assert.Equal([(6ul, 5)], consumer.Seen.Select(x => (x.Tick, x.Trigger.Value.Code)));
    }

    [Fact]
    public void EveryConsumerSeesEveryRecordExactlyOnce_OverManyTicks()
    {
        var early = new Reactor<Event<Pinged>>("early", s => s.NextTickOk<Event<Pinged>>());
        var late = new Reactor<Event<Pinged>>("late");
        var w = Worlds.Builder()
            .System("Begin", early)
            .System("Early", Pinger("producer", _ => true))
            .System("End", late)
            .Build();
        for (ulong t = 0; t < 50; t++) w.RunTick(t);
        Assert.Equal(Enumerable.Range(0, 49), early.Seen.Select(x => x.Trigger.Value.Code));   // тик 49 — в тике 50
        Assert.Equal(Enumerable.Range(0, 50), late.Seen.Select(x => x.Trigger.Value.Code));
        Assert.All(early.Seen, x => Assert.Equal(x.Trigger.Value.Code + 1, (int)x.Tick));
        Assert.All(late.Seen, x => Assert.Equal(x.Trigger.Value.Code, (int)x.Tick));
        // Записи живут один оборот тика — журнал не растёт.
        Assert.True(w.JournalOf<Event<Pinged>>()!.Count <= 1);
    }

    // ── черновики ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("create")]
    [InlineData("resource")]
    [InlineData("tick")]
    [InlineData("module")]
    public void ImmediateOpsInsideTick_AreErrors_EvenWithoutChecks(string op)
    {
        var w = new WorldBuilder { Options = new WorldOptions { Checks = false } }.Stages("S").Resource(1)
            .System("S", new Sys("sneaky", null, (in SystemContext ctx) =>
            {
                switch (op)
                {
                    case "create": ctx.World.CreateImmediate(); break;
                    case "resource": ctx.World.SetResourceImmediate(2); break;
                    case "tick": ctx.World.RunTick(ctx.Tick + 1); break;
                    default: ctx.World.SetModuleActive("x", false); break;
                }
            }))
            .Build();
        Assert.Contains("во время тика", Assert.Throws<InvalidOperationException>(() => w.RunTick(0)).Message);
        // После ошибки мир снова принимает немедленные операции вне тика.
        w.SetResourceImmediate(3);
        Assert.Equal(3, w.Resource<int>());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 32_768)]    // прежний 15-битный оборот повторялся здесь
    [InlineData(true, 32_768)]
    [InlineData(false, 65_536)]    // и 16-битный повторился бы здесь
    [InlineData(true, 65_536)]
    public void DraftOfPastBarrier_IsAnError(bool asField, int ticksLater)
    {
        EntityDraft kept = default;
        var w = Worlds.Builder()
            .System("Early", new Sys("keeper", s => s.Creates().Adds<Position>().Adds<Link>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick == 0)
                {
                    kept = ctx.Commands.Create();
                    ctx.Commands.Add(kept, new Position());
                    return;
                }
                // Черновик каждый тик — оборот буфера растёт каждый тик.
                var fresh = ctx.Commands.Create();
                if ((int)ctx.Tick < ticksLater) return;
                // Черновик прошлого барьера уже живая сущность — ссылаться на него нельзя.
                if (asField) ctx.Commands.Add(fresh, new Link { Target = kept.Ref });
                else ctx.Commands.Add(kept, new Link());
            }))
            .Build();
        for (int t = 0; t < ticksLater; t++) w.RunTick((ulong)t);
        Assert.Contains("прошлого барьера", Assert.Throws<InvalidOperationException>(() => w.RunTick((ulong)ticksLater)).Message);
    }

    [Fact]
    public void DraftEncoding_KeepsBufferIndexAndFullTurn()
    {
        var d = new EntityDraft(EntityDraft.MaxDrafts - 1, EntityDraft.MaxBuffers, uint.MaxValue);
        var e = d.Ref;
        Assert.True(e.IsDeferred);
        Assert.Equal(EntityDraft.MaxDrafts - 1, e.DraftIndex);
        Assert.Equal(EntityDraft.MaxBuffers, e.DraftBuffer);
        Assert.Equal(uint.MaxValue, e.DraftTurn);
        Assert.True(new EntityDraft(0, 0, 0).Ref.IsDeferred);   // немедленный буфер, первый оборот
    }

    [Fact]
    public void Drafts_ResolvedByManagedFieldOffsets_NotMarshalOnes()
    {
        Entity parent = default;
        var w = Worlds.Builder().Component<Mixed>().Component<Owned>()
            .System("Early", new Sys("spawner", s => s.Creates().Adds<Mixed>().Adds<Owned>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick != 0) return;
                var a = ctx.Commands.Create();
                ctx.Commands.Add(a, new Mixed { A = true, B = true, Target = a.Ref, C = 'Я', Second = parent, D = true });
                // Неизменяемая структура: ссылка во вложенном поле только для чтения.
                ctx.Commands.Add(a, new Owned(true, new Link { Target = a.Ref }, 'Ж'));
            }))
            .Build();
        parent = w.CreateImmediate();
        w.RunTick(0);
        foreach (var chunk in w.Iterate(w.Query().Read<Mixed>().Read<Owned>()))
        {
            var owned = chunk.Read<Owned>()[0];
            Assert.Equal(new Owned(true, new Link { Target = chunk.Entities[0] }, 'Ж'), owned);
            var m = chunk.Read<Mixed>()[0];
            Assert.Equal(chunk.Entities[0], m.Target);
            Assert.Equal(parent, m.Second);
            Assert.True(m.A && m.B && m.D);
            Assert.Equal('Я', m.C);
            return;
        }
        Assert.Fail("сущность не создана");
    }

    [Fact]
    public void Drafts_ResolvedInAddSetAndEmit_IncludingNestedStructs()
    {
        var target = Entity.Null;
        var pinged = new Reactor<Event<Pinged>>("pinged");
        var w = Worlds.Builder()
            .System("Early", new Sys("spawner", s => s.Creates().Adds<Link>().Adds<Nested>().Adds<Position>().Sets<Link>().Emits<Pinged>(),
                (in SystemContext ctx) =>
                {
                    if (ctx.Tick != 0) return;
                    var a = ctx.Commands.Create();
                    var b = ctx.Commands.Create();
                    ctx.Commands.Add(a, new Position());
                    ctx.Commands.Add(b, new Link { Target = a.Ref });
                    ctx.Commands.Add(b, new Nested { Kind = 7, Inner = new Link { Target = a.Ref } });
                    ctx.Commands.Set(b.Ref, new Link { Target = b.Ref });   // Set по черновику, ссылка на себя
                    ctx.Commands.Emit(new Pinged { Who = a.Ref, Code = 1 });
                }))
            .System("Late", pinged)
            .Build();
        w.RunTick(0);

        Query q = w.Query().Read<Link>().Read<Nested>();
        foreach (var chunk in w.Iterate(q))
        {
            var e = chunk.Entities[0];
            var link = chunk.Read<Link>()[0];
            var nested = chunk.Read<Nested>()[0];
            Assert.Equal(e, link.Target);                    // Set разрешён в саму сущность b
            Assert.True(w.Has<Position>(nested.Inner.Target)); // вложенное поле — сущность a
            target = nested.Inner.Target;
        }
        Assert.False(target.IsNull);
        Assert.Equal(target, Assert.Single(pinged.Seen).Trigger.Value.Who);
    }

    [Fact]
    public void ForeignDraft_IsAnError()
    {
        EntityDraft stolen = default;
        var w = Worlds.Builder()
            .System("Early", new Sys("a", s => s.Creates(), (in SystemContext ctx) => stolen = ctx.Commands.Create()))
            .System("Early", new Sys("b", s => s.Adds<Position>().After(new SystemLabel("a")),
                (in SystemContext ctx) => ctx.Commands.Add(stolen, new Position())))
            .System("Late", new Sys("c", s => s.Adds<Link>().Creates(),
                (in SystemContext ctx) => ctx.Commands.Add(ctx.Commands.Create(), new Link { Target = stolen.Ref })))
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => w.RunTick(0));
        Assert.Contains("чужого буфера", ex.Message);
    }

    [Fact]
    public void DeferredValueWrittenPastCommands_IsAnError()
    {
        var w = Worlds.Builder()
            .System("Early", new Sys("cheater", s => s.Creates().Writes<Link>(), (in SystemContext ctx) =>
            {
                var d = ctx.Commands.Create();
                foreach (var chunk in ctx.Iterate(ctx.World.Query().Write<Link>()))
                    chunk.Write<Link>()[0].Target = d.Ref;
            }))
            .Build();
        w.AddImmediate(w.CreateImmediate(), new Link());
        Assert.Contains("мимо команд", Assert.Throws<InvalidOperationException>(() => w.RunTick(0)).Message);
    }

    [Fact]
    public void EntityIndices_AreDeterministic()
    {
        List<Entity> Run()
        {
            var created = new List<Entity>();
            var w = Worlds.Builder()
                .System("Early", new Sys("spawn", s => s.Creates().Destroys().Adds<Position>(), (in SystemContext ctx) =>
                {
                    for (int i = 0; i < 3; i++) ctx.Commands.Add(ctx.Commands.Create(), new Position { Value = new Vector3(ctx.Tick, i, 0) });
                    foreach (var chunk in ctx.Iterate(ctx.World.Query().Read<Position>()))
                        foreach (var e in chunk.Entities)
                            if (e.Index % 2 == 0) ctx.Commands.Destroy(e);
                }))
                .System("Late", new Reactor<Added<Position>>("seen", react: (in SystemContext ctx, ReadOnlySpan<Added<Position>> t) =>
                {
                    foreach (var a in t) created.Add(a.Entity);
                }))
                .Build();
            for (ulong t = 0; t < 20; t++) w.RunTick(t);
            return created;
        }
        Assert.Equal(Run(), Run());
    }

    // ── нормализация на барьере ────────────────────────────────────────────

    private sealed class Probe
    {
        public readonly Reactor<Added<Health>> Added = new("added");
        public readonly Reactor<Removed<Health>> Removed = new("removed");
        public readonly Reactor<Changed<Health>> Changed = new("changed");

        public World Build(RunFn early) => Worlds.Builder()
            .System("Early", new Sys("actor", s => s.Creates().Destroys().Adds<Health>().Removes<Health>().Sets<Health>(), early))
            .System("Late", Added).System("Late", Removed).System("Late", Changed)
            .Build();
    }

    [Fact]
    public void AddedAndRemovedOnOneBarrier_ProduceNothing_ButCreateAndDestroyWorks()
    {
        var p = new Probe();
        var w = p.Build((in SystemContext ctx) =>
        {
            var d = ctx.Commands.Create();
            ctx.Commands.Add(d, new Health { Value = 1 });
            ctx.Commands.Remove<Health>(d.Ref);
            var gone = ctx.Commands.Create();
            ctx.Commands.Add(gone, new Health { Value = 2 });
            ctx.Commands.Destroy(gone.Ref);
        });
        w.RunTick(0);
        Assert.Empty(p.Added.Seen);
        Assert.Empty(p.Removed.Seen);
        Assert.Empty(p.Changed.Seen);
        Assert.Equal(1, w.EntityCount);
    }

    [Fact]
    public void RemovedAndReAdded_GivesRemovedOldThenAdded_EachWithItsCause()
    {
        var p = new Probe();
        Entity e = default;
        var w = p.Build((in SystemContext ctx) =>
        {
            if (ctx.Tick != 1) return;
            ctx.Commands.Cause = new CauseId(1);
            ctx.Commands.Remove<Health>(e);
            ctx.Commands.Cause = new CauseId(2);
            ctx.Commands.Add(e, new Health { Value = 9 });
            ctx.Commands.Cause = CauseId.None;
        });
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 3 });
        w.RunTick(0);
        p.Added.Seen.Clear();
        w.RunTick(1);
        var removed = Assert.Single(p.Removed.Seen).Trigger;
        Assert.Equal(3, removed.Value.Value);
        Assert.Equal(new CauseId(1), removed.Cause);
        var added = Assert.Single(p.Added.Seen).Trigger;
        Assert.Equal(e, added.Entity);
        Assert.Equal(new CauseId(2), added.Cause);
        Assert.Empty(p.Changed.Seen);
        Assert.Equal(9, w.Get<Health>(e).Value);
    }

    [Fact]
    public void RemovedReAddedRemoved_RemovedCarriesLastRemovalAndItsCause()
    {
        var p = new Probe();
        Entity e = default;
        var w = p.Build((in SystemContext ctx) =>
        {
            if (ctx.Tick != 1) return;
            ctx.Commands.Cause = new CauseId(1);
            ctx.Commands.Remove<Health>(e);
            ctx.Commands.Cause = new CauseId(2);
            ctx.Commands.Add(e, new Health { Value = 9 });
            ctx.Commands.Cause = new CauseId(3);
            ctx.Commands.Remove<Health>(e);
            ctx.Commands.Cause = CauseId.None;
        });
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 3 });
        w.RunTick(0);
        p.Added.Seen.Clear();
        w.RunTick(1);
        var removed = Assert.Single(p.Removed.Seen).Trigger;
        Assert.Equal(9, removed.Value.Value);
        Assert.Equal(new CauseId(3), removed.Cause);
        Assert.Empty(p.Added.Seen);
    }

    [Fact]
    public void TwoSets_OneChangedWithFinalValue()
    {
        var p = new Probe();
        Entity e = default;
        var w = p.Build((in SystemContext ctx) =>
        {
            if (ctx.Tick != 1) return;
            ctx.Commands.Set(e, new Health { Value = 4 });
            ctx.Commands.Set(e, new Health { Value = 5 });
        });
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 1 });
        w.RunTick(0);
        w.RunTick(1);
        var changed = Assert.Single(p.Changed.Seen);
        Assert.Equal(5, changed.Trigger.Value.Value);
        Assert.Equal(1ul, changed.Tick);
    }

    [Fact]
    public void Destroy_RemovedCarriesLastValue()
    {
        var p = new Probe();
        Entity e = default;
        var w = p.Build((in SystemContext ctx) =>
        {
            if (ctx.Tick != 1) return;
            ctx.Commands.Set(e, new Health { Value = 42 });
            ctx.Commands.Destroy(e);
        });
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 1 });
        w.RunTick(0);
        w.RunTick(1);
        var removed = Assert.Single(p.Removed.Seen);
        Assert.Equal(e, removed.Trigger.Entity);
        Assert.Equal(42, removed.Trigger.Value.Value);
        Assert.Empty(p.Changed.Seen);
    }

    [Fact]
    public void ImmediateChanges_AreSeenByConsumersOfEveryStage()
    {
        var begin = new Reactor<Added<Health>>("begin");
        var end = new Reactor<Added<Health>>("end");
        var w = Worlds.Builder().System("Begin", begin).System("End", end).Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Health());
        for (ulong t = 0; t < 5; t++) w.RunTick(t);
        Assert.Equal(e, Assert.Single(begin.Seen).Trigger.Entity);
        Assert.Equal(e, Assert.Single(end.Seen).Trigger.Entity);
    }

    // ── активация, две регистрации, объявления ─────────────────────────────

    [Fact]
    public void DeactivateThenActivate_DoesNotReplayOldRecords()
    {
        var consumer = new Reactor<Event<Pinged>>("consumer");
        var w = Worlds.Builder()
            .System("Early", Pinger("producer", _ => true))
            .System("Late", consumer)
            .Build();
        w.RunTick(0);
        w.SetActive("consumer@Late", false);
        for (ulong t = 1; t < 5; t++) w.RunTick(t);
        w.SetActive("consumer@Late", true);
        w.RunTick(5);
        Assert.Equal([0, 5], consumer.Seen.Select(x => x.Trigger.Value.Code));
    }

    [Fact]
    public void OneSystemInTwoStages_EachRegistrationSeesRecordOnce_ActionOnce()
    {
        int actions = 0;
        var once = new HashSet<Entity>();
        var dual = new Reactor<Added<Health>>("dual", s => s.NextTickOk<Added<Health>>(), (in SystemContext ctx, ReadOnlySpan<Added<Health>> t) =>
        {
            foreach (var a in t)
                if (once.Add(a.Entity)) actions++;   // идемпотентна: вторая регистрация то же не повторяет
        });
        var w = Worlds.Builder()
            .System("Begin", dual)
            .System("Early", new Sys("spawn", s => s.Creates().Adds<Health>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick == 2) ctx.Commands.Add(ctx.Commands.Create(), new Health());
            }))
            .System("End", dual)
            .Build();
        for (ulong t = 0; t < 6; t++) w.RunTick(t);
        Assert.Equal(1, actions);
        Assert.Equal([2ul, 3ul], dual.Seen.Select(x => x.Tick).Order());   // End — тик 2, Begin — тик 3
        Assert.Contains(w.Systems, s => s.Name == "dual@Begin");
        Assert.Contains(w.Systems, s => s.Name == "dual@End");
    }

    [Fact]
    public void UndeclaredProduction_IsAnError()
    {
        var w = Worlds.Builder()
            .System("Early", new Sys("sneaky", null, (in SystemContext ctx) => ctx.Commands.Emit(new Pinged())))
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => w.RunTick(0));
        Assert.Contains("Emits<Pinged>", ex.Message);
    }

    [Fact]
    public void ChangedOfNonReactive_IsBuildError() =>
        Assert.Contains("не реактивный", Assert.Throws<InvalidOperationException>(() =>
            Worlds.Builder().System("Late", new Reactor<Changed<Position>>("bad")).Build()).Message);

    [Fact]
    public void AfterBefore_OrderSystems_AndCycleIsBuildError()
    {
        var order = new List<string>();
        RunFn Log(string name) => (in SystemContext _) => order.Add(name);
        var w = Worlds.Builder()
            .System("Early", new Sys("c", s => s.After(new SystemLabel("b")), Log("c")))
            .System("Early", new Sys("a", s => s.Before(new SystemLabel("b")), Log("a")))
            .System("Early", new Sys("b", null, Log("b")))
            .System("Early", new Sys("free", s => s.After(new SystemLabel("absent-module")), Log("free")))
            .Build();
        w.RunTick(0);
        // a → b → c по After/Before; «free» ссылается на метку отсутствующего модуля — связи нет, идёт по регистрации.
        Assert.Equal(["a", "b", "c", "free"], order);

        Assert.Contains("цикл", Assert.Throws<InvalidOperationException>(() => Worlds.Builder()
            .System("Early", new Sys("x", s => s.After(new SystemLabel("y")), Log("x")))
            .System("Early", new Sys("y", s => s.After(new SystemLabel("x")), Log("y")))
            .Build()).Message);
    }

    [Fact]
    public void ResourceChanged_ReachesSubscribers()
    {
        var seen = new Reactor<ResourceChanged<int>>("seen");
        var w = Worlds.Builder().Resource(0)
            .System("Early", new Sys("setter", s => s.SetsResource<int>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick == 3) ctx.Commands.SetResource(33);
            }))
            .System("Late", seen)
            .Build();
        for (ulong t = 0; t < 5; t++) w.RunTick(t);
        Assert.Equal(33, w.Resource<int>());
        Assert.Equal([(3ul, 33)], seen.Seen.Select(x => (x.Tick, x.Trigger.Value)));
    }

    [Fact]
    public void SteadyState_CommandsAndJournals_DoNotAllocate()
    {
        int added = 0, pings = 0, changed = 0, removed = 0, resources = 0;
        Entity keeper = default;
        var w = Worlds.Builder().Resource(0)
            .System("Early", new Sys("churn",
                s => s.Creates().Destroys().Adds<Position>().Adds<Link>().Emits<Pinged>().Sets<Health>().Removes<Velocity>()
                    .Adds<Velocity>().SetsResource<int>(),
                (in SystemContext ctx) =>
                {
                    for (int i = 0; i < 8; i++)
                    {
                        var d = ctx.Commands.Create();
                        ctx.Commands.Add(d, new Position());
                        ctx.Commands.Add(d, new Link { Target = d.Ref });   // разрешитель черновиков
                    }
                    ctx.Commands.Emit(new Pinged { Code = 1 });
                    ctx.Commands.Set(keeper, new Health { Value = ctx.Tick });           // Changed
                    ctx.Commands.Remove<Velocity>(keeper);                               // Removed и Added заново
                    ctx.Commands.Add(keeper, new Velocity());
                    ctx.Commands.SetResource((int)ctx.Tick);                             // ResourceChanged
                    foreach (var chunk in ctx.Iterate(Churned!))
                        foreach (var e in chunk.Entities) ctx.Commands.Destroy(e);
                }))
            .System("Late", new Counting<Added<Position>>("added", n => added += n))
            .System("Late", new Counting<Event<Pinged>>("pings", n => pings += n))
            .System("Late", new Counting<Changed<Health>>("changed", n => changed += n))
            .System("Late", new Counting<Removed<Velocity>>("removed", n => removed += n))
            .System("Late", new Counting<ResourceChanged<int>>("resources", n => resources += n))
            .Build();
        keeper = w.CreateImmediate();
        w.AddImmediate(keeper, new Health());
        w.AddImmediate(keeper, new Velocity());
        Churned = w.Query().Read<Position>().Without<Health>();
        for (ulong t = 0; t < 200; t++) w.RunTick(t);   // прогрев: ёмкости выросли
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 200; t < 1200; t++) w.RunTick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1200 * 8, added);
        Assert.Equal(1200, pings);
        Assert.Equal(1200, changed);
        Assert.Equal(1200, removed);
        Assert.Equal(1200, resources);
        Assert.Equal(1199, w.Resource<int>());
        Assert.True(allocated == 0, $"устойчивый режим выделил {allocated} Б за 1000 тиков");
    }

    private sealed class Counting<T>(string label, Action<int> count) : IReactiveSystem<T> where T : struct
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel(label));
        public void React(in SystemContext ctx, ReadOnlySpan<T> triggers) => count(triggers.Length);
    }

    private static Query? Churned;
}
