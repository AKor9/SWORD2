using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

/// <summary>Ресурс подсистемы за хэндлом — то, что хуки заводят и освобождают (как тело физики или поза).</summary>
public struct Handle { public int Slot; }

/// <summary>
/// Граничные тесты хуков времени жизни (раздел 5 плана, v1): хуки на барьере после журналов и до событий, в порядке
/// команд; не нормализуются («добавлен и снят на одном барьере» — журналы пусты, хуки отработали оба и ресурс вернулся);
/// повторный вход и изменение структуры из хука — исключение; 0 байт в установившемся режиме.
/// </summary>
public class HookTests
{
    /// <summary>Подсистема-заглушка: выдаёт слоты по порядку, помнит живые и журнал вызовов хуков.</summary>
    private sealed class Pool
    {
        public readonly HashSet<int> Alive = [];
        public readonly List<string> Log = [];
        private int _next = 1;

        public void OnAdd(Entity e, ref Handle h)
        {
            h.Slot = _next++;
            Alive.Add(h.Slot);
            Log.Add($"+{e.Index}:{h.Slot}");
        }

        public void OnRemove(Entity e, in Handle h)
        {
            Assert.True(Alive.Remove(h.Slot), $"слот {h.Slot} не жив — хук снятия не видит, что записал хук добавления");
            Log.Add($"-{e.Index}:{h.Slot}");
        }
    }

    private static WorldBuilder Builder(Pool pool) => Worlds.Builder().Component<Handle>()
        .OnAdd<Handle>(pool.OnAdd).OnRemove<Handle>(pool.OnRemove);

    private static Sys Actor(RunFn run) =>
        new("actor", s => s.Creates().Destroys().Adds<Handle>().Removes<Handle>().Sets<Handle>().Adds<Position>().Emits<Pinged>(), run);

    [Fact]
    public void AddAndRemove_HookOwnsResource_HandleWrittenInPlace()
    {
        var pool = new Pool();
        Entity e = default;
        var w = Builder(pool)
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick == 0)
                {
                    var d = ctx.Commands.Create();
                    ctx.Commands.Add(d, new Handle());
                }
                if (ctx.Tick == 2) ctx.Commands.Remove<Handle>(e);
            }))
            .Build();
        w.RunTick(0);
        foreach (var chunk in w.Reader.Iterate(w.Reader.Query().Read<Handle>())) e = chunk.Entities[0];
        Assert.Equal(1, w.Get<Handle>(e).Slot);   // хук записал хэндл в ячейку компонента
        Assert.Equal([1], pool.Alive);
        w.RunTick(1);
        Assert.Single(pool.Log);                   // без изменений хуков нет
        w.RunTick(2);
        Assert.Empty(pool.Alive);
        Assert.Equal([$"+{e.Index}:1", $"-{e.Index}:1"], pool.Log);
    }

    [Fact]
    public void Destroy_RemoveHookSeesLastValue()
    {
        var pool = new Pool();
        Entity e = default;
        var w = Builder(pool)
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick == 1) ctx.Commands.Destroy(e);
            }))
            .Build();
        e = w.CreateImmediate();
        w.AddImmediate(e, new Handle());           // немедленная операция — тоже барьер: хук сработал
        Assert.Equal([1], pool.Alive);
        w.RunTick(0);
        w.RunTick(1);
        Assert.Empty(pool.Alive);
        Assert.Equal([$"+{e.Index}:1", $"-{e.Index}:1"], pool.Log);
    }

    [Fact]
    public void AddedAndRemovedOnOneBarrier_JournalsEmpty_BothHooksRan_ResourceReturned()
    {
        var pool = new Pool();
        var added = new Reactor<Added<Handle>>("added");
        var removed = new Reactor<Removed<Handle>>("removed");
        Entity keep = default;
        var w = Builder(pool)
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick != 1) return;
                var d = ctx.Commands.Create();
                ctx.Commands.Add(d, new Handle());
                ctx.Commands.Remove<Handle>(d.Ref);      // добавлен и снят
                var gone = ctx.Commands.Create();
                ctx.Commands.Add(gone, new Handle());
                ctx.Commands.Destroy(gone.Ref);          // создан с компонентом и удалён
                ctx.Commands.Remove<Handle>(keep);       // снят, добавлен заново и снят
                ctx.Commands.Add(keep, new Handle());
                ctx.Commands.Remove<Handle>(keep);
            }))
            .System("Late", added).System("Late", removed)
            .Build();
        keep = w.CreateImmediate();
        w.AddImmediate(keep, new Handle());               // слот 1
        w.RunTick(0);
        added.Seen.Clear();                               // Added<Handle> keep от немедленной операции
        w.RunTick(1);
        Assert.Empty(added.Seen);
        // Для журналов keep только потерял компонент — одна запись (значение — последнего снятия по командам: хуки идут
        // после журналов, и записанное хуком добавления того же барьера в неё не попадает).
        Assert.Equal(keep, Assert.Single(removed.Seen).Trigger.Entity);
        Assert.Empty(pool.Alive);
        Assert.Equal(2, w.EntityCount);
        // Порядок — порядок команд: хук добавления над копией, хук снятия с тем, что тот записал.
        Assert.Equal([$"+{keep.Index}:1", "+1:2", "-1:2", "+2:3", "-2:3", $"-{keep.Index}:1", $"+{keep.Index}:4", $"-{keep.Index}:4"],
            pool.Log);
    }

    [Fact]
    public void RemovedAndReAdded_OldReleasedThenNewAcquired_JournalsRemovedThenAdded()
    {
        var pool = new Pool();
        var added = new Reactor<Added<Handle>>("added");
        var removed = new Reactor<Removed<Handle>>("removed");
        Entity e = default;
        var w = Builder(pool)
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick != 1) return;
                ctx.Commands.Remove<Handle>(e);
                ctx.Commands.Add(e, new Handle());
            }))
            .System("Late", added).System("Late", removed)
            .Build();
        e = w.CreateImmediate();
        w.AddImmediate(e, new Handle());
        w.RunTick(0);
        added.Seen.Clear();
        w.RunTick(1);
        Assert.Equal(1, Assert.Single(removed.Seen).Trigger.Value.Slot);   // старое значение — со старым хэндлом
        Assert.Equal(e, Assert.Single(added.Seen).Trigger.Entity);
        Assert.Equal(2, w.Get<Handle>(e).Slot);                             // новый ресурс
        Assert.Equal([2], pool.Alive);
        Assert.Equal([$"+{e.Index}:1", $"-{e.Index}:1", $"+{e.Index}:2"], pool.Log);
    }

    [Fact]
    public void AddedRemovedAndAddedAgain_OnOneBarrier_FirstResourceReturned_SecondInCell()
    {
        var pool = new Pool();
        var added = new Reactor<Added<Handle>>("added");
        var removed = new Reactor<Removed<Handle>>("removed");
        Entity e = default;
        var w = Builder(pool)
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick != 1) return;
                ctx.Commands.Add(e, new Handle());       // добавлен, снят и добавлен снова
                ctx.Commands.Remove<Handle>(e);
                ctx.Commands.Add(e, new Handle());
            }))
            .System("Late", added).System("Late", removed)
            .Build();
        e = w.CreateImmediate();
        w.RunTick(0);
        w.RunTick(1);
        // Для журналов — одно добавление; хуки — все три по порядку команд: первый ресурс заведён над копией и возвращён,
        // второй записан в ячейку живого компонента.
        Assert.Equal(e, Assert.Single(added.Seen).Trigger.Entity);
        Assert.Empty(removed.Seen);
        Assert.Equal(2, w.Get<Handle>(e).Slot);
        Assert.Equal([2], pool.Alive);
        Assert.Equal([$"+{e.Index}:1", $"-{e.Index}:1", $"+{e.Index}:2"], pool.Log);
    }

    [Fact]
    public void Hooks_RunAfterJournals_BeforeEvents_SeeWorldAfterAllCommands()
    {
        World w = null!;
        var seen = new List<(int Added, int Events, bool HasPosition)>();
        var w0 = Worlds.Builder().Component<Handle>()
            .OnAdd<Handle>((Entity e, ref Handle h) =>
                seen.Add((w.JournalOf<Added<Handle>>()!.Count, w.JournalOf<Event<Pinged>>()!.Count, w.Has<Position>(e))))
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick != 0) return;
                var d = ctx.Commands.Create();
                ctx.Commands.Add(d, new Handle());
                ctx.Commands.Add(d, new Position());     // команда после Add<Handle> — хук видит и её
                ctx.Commands.Emit(new Pinged());
            }))
            .System("Late", new Reactor<Added<Handle>>("added")).System("Late", new Reactor<Event<Pinged>>("pinged"));
        w = w0.Build();
        w.RunTick(0);
        Assert.Equal([(1, 0, true)], seen);   // запись Added уже в журнале, события барьера ещё нет
        Assert.Equal(1, w.JournalOf<Event<Pinged>>()!.Count);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("resource", true)]
    [InlineData("tick", true)]
    [InlineData("module", true)]
    public void MutatingWorldFromHook_IsAnError_WorldStaysUsable(string op, bool inTick)
    {
        World w = null!;
        bool armed = true;
        w = new WorldBuilder { Options = new WorldOptions { Checks = false } }.Stages("S").Resource(1).Component<Handle>()
            .OnAdd<Handle>((Entity e, ref Handle h) =>
            {
                if (!armed) return;
                switch (op)
                {
                    case "create": w.CreateImmediate(); break;
                    case "resource": w.SetResourceImmediate(2); break;
                    case "tick": w.RunTick(99); break;
                    default: w.SetModuleActive("x", false); break;
                }
            })
            .System("S", new Sys("adder", s => s.Creates().Adds<Handle>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick == 0) ctx.Commands.Add(ctx.Commands.Create(), new Handle());
            }))
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (inTick) w.RunTick(0);
            else w.AddImmediate(w.CreateImmediate(), new Handle());
        });
        Assert.Contains("из хука", ex.Message);
        armed = false;
        w.SetResourceImmediate(3);   // после ошибки мир снова принимает немедленные операции и тики
        w.RunTick(1);
        Assert.Equal(3, w.Resource<int>());
    }

    [Fact]
    public void ExceptionInHook_AbortsBarrier_NextTickClean()
    {
        int calls = 0;
        var pinged = new Reactor<Event<Pinged>>("pinged");
        var w = Worlds.Builder().Component<Handle>()
            .OnAdd<Handle>((Entity e, ref Handle h) =>
            {
                if (calls++ == 0) throw new InvalidOperationException("ресурса нет");
            })
            .System("Early", Actor((in SystemContext ctx) =>
            {
                if (ctx.Tick > 1) return;
                ctx.Commands.Add(ctx.Commands.Create(), new Handle());
                ctx.Commands.Emit(new Pinged { Code = (int)ctx.Tick });
            }))
            .System("Late", pinged)
            .Build();
        Assert.Throws<InvalidOperationException>(() => w.RunTick(0));
        w.RunTick(1);
        Assert.Equal(2, calls);                                           // хук тика 0 не повторился
        Assert.Equal([1], pinged.Seen.Select(x => x.Trigger.Value.Code));  // событие прерванного барьера не доставлено
    }

    [Fact]
    public void AddOfExisting_WithoutChecks_IsReplacement_NoHook()
    {
        var pool = new Pool();
        var w = new WorldBuilder { Options = new WorldOptions { Checks = false } }.Stages("S").Component<Handle>()
            .OnAdd<Handle>(pool.OnAdd).OnRemove<Handle>(pool.OnRemove).Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Handle());
        w.AddImmediate(e, new Handle { Slot = 1 });
        Assert.Single(pool.Log);
    }

    [Fact]
    public void TagHook_GetsEmptyValue()
    {
        var log = new List<string>();
        var w = Worlds.Builder()
            .OnAdd<Frozen>((Entity e, ref Frozen _) => log.Add("+"))
            .OnRemove<Frozen>((Entity e, in Frozen _) => log.Add("-"))
            .Build();
        var e = w.CreateImmediate();
        w.AddImmediate(e, new Frozen());
        w.DestroyImmediate(e);
        Assert.Equal(["+", "-"], log);
    }

    [Fact]
    public void Registration_OneHookPerKind_OnlyRegisteredComponents_VisibleToObservers()
    {
        var pool = new Pool();
        Assert.Contains("дважды", Assert.Throws<InvalidOperationException>(() =>
            Builder(pool).OnAdd<Handle>(pool.OnAdd).Build()).Message);
        Assert.Contains("не зарегистрирован", Assert.Throws<InvalidOperationException>(() =>
            Worlds.Builder().OnAdd<Handle>(pool.OnAdd).Build()).Message);
        var w = Worlds.Builder().Component<Handle>().OnAdd<Handle>(pool.OnAdd, "physics").OnRemove<Handle>(pool.OnRemove).Build();
        Assert.Equal([new HookInfo(typeof(Handle).FullName!, "OnAdd", "physics"), new HookInfo(typeof(Handle).FullName!, "OnRemove", null)],
            w.Reader.Hooks);
    }

    [Fact]
    public void SteadyState_WithHooks_DoesNotAllocate()
    {
        var pool = new int[1];
        Entity keeper = default;
        var w = Worlds.Builder().Component<Handle>()
            .OnAdd<Handle>((Entity e, ref Handle h) => h.Slot = ++pool[0])
            .OnRemove<Handle>((Entity e, in Handle h) => pool[0]--)
            .System("Early", Actor((in SystemContext ctx) =>
            {
                for (int i = 0; i < 8; i++)
                {
                    var d = ctx.Commands.Create();
                    ctx.Commands.Add(d, new Handle());
                    ctx.Commands.Destroy(d.Ref);                 // создан и удалён: оба хука
                }
                ctx.Commands.Remove<Handle>(keeper);             // снят и добавлен заново
                ctx.Commands.Add(keeper, new Handle());
            }))
            .Build();
        keeper = w.CreateImmediate();
        w.AddImmediate(keeper, new Handle());
        for (ulong t = 0; t < 200; t++) w.RunTick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 200; t < 1200; t++) w.RunTick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, pool[0]);
        Assert.True(allocated == 0, $"устойчивый режим с хуками выделил {allocated} Б за 1000 тиков");
    }
}
