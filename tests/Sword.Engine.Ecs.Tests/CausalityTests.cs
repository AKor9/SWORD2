using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

/// <summary>
/// Журнал причинности (раздел 8 плана, <c>state.why</c>): вне режима запись несёт причину системы как есть и журнала нет;
/// в режиме — у каждой команды и события своя запись (вид, родитель, тик, стадия, регистрация, источник, сущность, тип,
/// значения до и после), причина записи идёт в журналы срабатываний, реактивная система с одним срабатыванием получает его
/// причину родителем, шаги и решения — явно; кольца ограничены; побочная таблица связей; грубый учёт записи столбцов; 0 байт.
/// </summary>
public sealed class CausalityTests
{
    private static CauseRecord Record(World w, CauseId id)
    {
        Assert.True(w.Causality!.TryGet(id, out var r), $"записи {id.Value} нет");
        return r;
    }

    [Fact]
    public void Off_NoLog_CausePassesThrough()
    {
        var seen = new Reactor<Changed<Health>>("seen");
        Entity e = default;
        var w = Worlds.Builder()
            .System("Early", new Sys("set", s => s.Sets<Health>(), (in SystemContext ctx) =>
            {
                ctx.Cause = new CauseId(7);
                ctx.Commands.Set(e, new Health { Value = 2 });
            }))
            .System("Late", seen)
            .Build();
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 1 });
        w.RunTick(0);
        Assert.Null(w.Causality);
        Assert.Equal(new CauseId(7), Assert.Single(seen.Seen).Trigger.Cause);
    }

    /// <summary>
    /// Команда и событие: своя запись с родителем <see cref="CommandBuffer.Cause"/>, тиком, стадией, регистрацией, источником (этот
    /// файл), сущностью и типом; <c>Set</c> — значения до и после; её причина — в <c>Changed</c>; реактивная система с одним
    /// срабатыванием — его причина родителем; черновик — сущность разрешена на барьере.
    /// </summary>
    [Fact]
    public void On_RecordsParentStageSourceValues_AndReactiveParent()
    {
        CauseId root = default, set = default, ping = default, created = default, reacted = default;
        Entity e = default;
        var react = new Reactor<Changed<Health>>("react", s => s.Emits<Pinged>(), (in SystemContext ctx, ReadOnlySpan<Changed<Health>> t) =>
        {
            ctx.Commands.Emit(new Pinged { Code = 2 });
            reacted = ctx.Commands.LastCause;
        });
        var w = Worlds.Builder()
            .System("Early", new Sys("act", s => s.Sets<Health>().Emits<Pinged>().Creates().Adds<Position>(), (in SystemContext ctx) =>
            {
                if (ctx.Tick != 3) return;
                root = ctx.Commands.Note("решение", e, CauseId.None, 42);
                ctx.Cause = root;
                ctx.Commands.Set(e, new Health { Value = 5 });
                set = ctx.Commands.LastCause;
                ctx.Commands.Emit(new Pinged { Who = e, Code = 1 });
                ping = ctx.Commands.LastCause;
                var d = ctx.Commands.Create();
                created = ctx.Commands.LastCause;
                ctx.Commands.Add(d, new Position());
            }))
            .System("Late", react)
            .Build();
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health { Value = 1 });
        w.SetCausality(new CausalityLog(64, 1 << 16));
        for (ulong t = 0; t < 4; t++) w.RunTick(t);

        var decision = Record(w, root);
        Assert.Equal((CauseKind.Decision, CauseId.None, 3UL, "решение", 42L, e), (decision.Kind, decision.Parent, decision.Tick, decision.Note, decision.Arg, decision.Entity));
        var s = Record(w, set);
        Assert.Equal((CauseKind.Set, root, 3UL, 1, e), (s.Kind, s.Parent, s.Tick, s.Stage, s.Entity));
        Assert.Equal(w.Systems.Single(x => x.Label == "act").RegistrationId, s.Registration);
        Assert.EndsWith("CausalityTests.cs", s.File);
        Assert.True(s.Line > 0);
        Assert.Equal(1f, System.Runtime.InteropServices.MemoryMarshal.Read<Health>(w.Causality!.Before(s)).Value);
        Assert.Equal(5f, System.Runtime.InteropServices.MemoryMarshal.Read<Health>(w.Causality.After(s)).Value);
        Assert.Equal(set, Assert.Single(react.Seen).Trigger.Cause);   // причина Set — в Changed

        var p = Record(w, ping);
        Assert.Equal((CauseKind.Event, root), (p.Kind, p.Parent));
        Assert.Equal(e, System.Runtime.InteropServices.MemoryMarshal.Read<Pinged>(w.Causality.After(p)).Who);
        var c = Record(w, created);
        Assert.Equal(CauseKind.Create, c.Kind);
        Assert.False(c.Entity.IsDeferred);
        Assert.True(w.IsAlive(c.Entity));

        var r = Record(w, reacted);   // реактивная система, одно срабатывание — его причина родителем
        Assert.Equal((CauseKind.Event, set, 2), (r.Kind, r.Parent, r.Stage));
    }

    /// <summary>Несколько срабатываний — родитель по умолчанию пуст: система ставит <c>ctx.Cause</c> на каждое сама.</summary>
    [Fact]
    public void Reactive_ManyTriggers_ParentPerTrigger()
    {
        var parents = new List<(CauseId Trigger, CauseId Parent)>();
        Entity a = default, b = default;
        var react = new Reactor<Changed<Health>>("react", s => s.Emits<Pinged>(), (in SystemContext ctx, ReadOnlySpan<Changed<Health>> t) =>
        {
            ctx.Commands.Emit(new Pinged());
            parents.Add((default, ctx.World.Causality!.TryGet(ctx.Commands.LastCause, out var none) ? none.Parent : default));
            foreach (var x in t)
            {
                ctx.Cause = x.Cause;
                ctx.Commands.Emit(new Pinged());
                parents.Add((x.Cause, ctx.World.Causality.TryGet(ctx.Commands.LastCause, out var r) ? r.Parent : default));
            }
        });
        var w = Worlds.Builder()
            .System("Early", new Sys("set", s => s.Sets<Health>(), (in SystemContext ctx) =>
            {
                ctx.Commands.Set(a, new Health { Value = ctx.Tick });
                ctx.Commands.Set(b, new Health { Value = ctx.Tick });
            }))
            .System("Late", react)
            .Build();
        a = w.CreateImmediate();
        b = w.CreateImmediate();
        w.AddImmediate(a, new Health());
        w.AddImmediate(b, new Health());
        w.SetCausality(new CausalityLog());
        w.RunTick(1);
        Assert.Equal(3, parents.Count);
        Assert.Equal(CauseId.None, parents[0].Parent);
        Assert.All(parents.Skip(1), x => Assert.Equal(x.Trigger, x.Parent));
        Assert.NotEqual(parents[1].Parent, parents[2].Parent);
    }

    /// <summary>Кольцо записей и кольцо значений ограничены: старое вытесняется честно (нет записи, нет значений).</summary>
    [Fact]
    public void Rings_Bounded_OldEvicted()
    {
        var ids = new List<CauseId>();
        Entity e = default;
        var w = Worlds.Builder()
            .System("Early", new Sys("set", s => s.Sets<Health>(), (in SystemContext ctx) =>
            {
                ctx.Commands.Set(e, new Health { Value = ctx.Tick });
                ids.Add(ctx.Commands.LastCause);
            }))
            .Build();
        e = w.CreateImmediate();
        w.AddImmediate(e, new Health());
        var log = new CausalityLog(16, 4 * CausalityLog.MaxValueBytes);
        w.SetCausality(log);
        for (ulong t = 0; t < 2000; t++) w.RunTick(t);
        Assert.Equal(2000, log.Written);
        Assert.False(log.TryGet(ids[0], out _));
        Assert.True(log.TryGet(ids[^1], out var last));
        Assert.True(log.ValuesKept(last));
        Assert.Equal(1999f, System.Runtime.InteropServices.MemoryMarshal.Read<Health>(log.After(last)).Value);
        Assert.Equal(16, Enumerable.Range(0, 3000).Count(i => log.TryAt(log.NextSequence - 1 - i, out _)));
    }

    /// <summary>Побочная таблица связей: по (сущность, канал) — последняя причина; другой ключ — пусто.</summary>
    [Fact]
    public void Links_ByEntityChannelKey()
    {
        var log = new CausalityLog();
        var a = new Entity(3, 1);
        log.Link(a, 2, new CauseId(10), key: 5);
        log.Link(a, 1, new CauseId(11));
        Assert.Equal(new CauseId(10), log.Linked(a, 2, 5));
        Assert.Equal(CauseId.None, log.Linked(a, 2, 6));
        Assert.Equal(new CauseId(11), log.Linked(a, 1));
        Assert.Equal(CauseId.None, log.Linked(new Entity(3, 2), 1));
        log.Link(a, 2, new CauseId(12), key: 6);
        Assert.Equal(new CauseId(12), log.Linked(a, 2, 6));
        for (int i = 0; i < 3 * CausalityLog.LinkSlots; i++) log.Link(new Entity(100 + i, 1), 1, new CauseId(i + 1));   // переполнение — вытеснение
        Assert.Equal(new CauseId(3 * CausalityLog.LinkSlots), log.Linked(new Entity(99 + 3 * CausalityLog.LinkSlots, 1), 1));
    }

    /// <summary>Запись колонки на месте — грубо: регистрация и тик; включение — только между тиками.</summary>
    [Fact]
    public void ColumnWrites_ByRegistration_EnableBetweenTicksOnly()
    {
        World? self = null;
        var w = Worlds.Builder()
            .System("Early", new Sys("move", s => s.Writes<Position>(), (in SystemContext ctx) =>
            {
                foreach (var chunk in ctx.Iterate(ctx.World.Query().Write<Position>())) chunk.Write<Position>()[0].Value.X += 1;
                if (ctx.Tick == 5) Assert.Throws<InvalidOperationException>(() => self!.SetCausality(null));
            }))
            .Build();
        self = w;
        w.AddImmediate(w.CreateImmediate(), new Position());
        var log = new CausalityLog();
        w.SetCausality(log);
        for (ulong t = 0; t < 6; t++) w.RunTick(t);
        int position = w.Reader.Components.Single(c => c.Type == typeof(Position)).Id;
        int move = w.Systems.Single(s => s.Label == "move").RegistrationId;
        Assert.Equal(5UL, log.LastWrite(position, move));
        Assert.Null(log.LastWrite(position, move + 1));
    }

    /// <summary>Режим включён — устойчивый тик команд, событий, черновиков и журналов без выделения памяти (кольца — заранее).</summary>
    [Fact]
    public void On_SteadyState_DoesNotAllocate()
    {
        Entity keeper = default;
        int pings = 0;
        var w = Worlds.Builder()
            .System("Early", new Sys("churn", s => s.Creates().Destroys().Adds<Position>().Emits<Pinged>().Sets<Health>().Writes<Velocity>(),
                (in SystemContext ctx) =>
                {
                    var d = ctx.Commands.Create();
                    ctx.Commands.Add(d, new Position());
                    ctx.Cause = ctx.Commands.Note("шаг", keeper, ctx.Commands.LastCause, 1);
                    ctx.Commands.Emit(new Pinged { Code = 1 });
                    ctx.Commands.Set(keeper, new Health { Value = ctx.Tick });
                    ctx.World.Causality!.Link(keeper, 1, ctx.Commands.LastCause, (long)ctx.Tick);
                    foreach (var chunk in ctx.Iterate(Churned!)) foreach (var e in chunk.Entities) ctx.Commands.Destroy(e);
                    foreach (var chunk in ctx.Iterate(Moved!)) chunk.Write<Velocity>();
                }))
            .System("Late", new Count("pings", n => pings += n))
            .Build();
        keeper = w.CreateImmediate();
        w.AddImmediate(keeper, new Health());
        w.AddImmediate(keeper, new Velocity());
        Churned = w.Query().Read<Position>();
        Moved = w.Query().Write<Velocity>();
        w.SetCausality(new CausalityLog());
        for (ulong t = 0; t < 200; t++) w.RunTick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 200; t < 1200; t++) w.RunTick(t);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"режим причинности выделил {allocated} Б за 1000 тиков");
        Assert.Equal(1200, pings);
    }

    private static Query? Churned, Moved;

    private sealed class Count(string label, Action<int> count) : IReactiveSystem<Event<Pinged>>
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel(label));
        public void React(in SystemContext ctx, ReadOnlySpan<Event<Pinged>> triggers) => count(triggers.Length);
    }
}
