using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

public enum Mood : byte { Calm, Angry = 7 }

[InlineArray(3)]
public struct Three
{
    private short _element;
}

/// <summary>Поля всех видов с дырами выравнивания: bool перед float, byte перед Vector3 и т. д.</summary>
public struct Mixed
{
    public bool Flag;
    public float F;
    public byte B;
    public Vector3 V;
    public Mood Mood;
    public Entity Target;
    public double D;
    public Three Arr;
    public char C;
}

public unsafe struct WithFixed
{
    public fixed int Values[2];
}

public struct Wander
{
    public Vector2 Position;
}

public struct Nudge
{
    public float X;
}

/// <summary>Подопытный модуль: бродяги шагают по счётчиковому ГСЧ; команда Nudge сдвигает всех.</summary>
public sealed class WanderModule(int count = 5) : IModule
{
    public static readonly uint StreamId = SimRandom.Stream("test.wander");

    public string Id => "wander";

    public void Configure(SimulationBuilder b) =>
        b.Component<Wander>().Command<Nudge>().System("S", new Walker()).OnWorldBuilt(w =>
        {
            for (int i = 0; i < count; i++) w.AddImmediate(w.CreateImmediate(), new Wander());
        });

    private sealed class Walker : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("wander.walk")).ReadsJournal<Event<Nudge>>().Writes<Wander>();
            _query = s.Query().Write<Wander>();
        }

        public void Run(in SystemContext ctx)
        {
            var rng = ctx.World.Resource<SimRandom>();
            ulong tick = ctx.World.Resource<SimTime>().Tick;
            var nudge = ctx.Journal<Event<Nudge>>();
            float dx = nudge.IsEmpty ? 0 : nudge[^1].Value.X;
            foreach (var chunk in ctx.Iterate(_query))
            {
                var w = chunk.Write<Wander>();
                var e = chunk.Entities;
                for (int i = 0; i < w.Length; i++)
                    w[i].Position += new Vector2(rng.Range(StreamId, tick, e[i], -1, 1) + dx, rng.Range(StreamId, tick, e[i], -1, 1, 1));
            }
        }
    }
}

/// <summary>Подготовка мира в тесте: действие над собранным миром (удаления, выключение систем).</summary>
public sealed class SetupModule(Action<World> setup) : IModule
{
    public string Id => "setup";

    public void Configure(SimulationBuilder b) => b.OnWorldBuilt(setup);
}

/// <summary>Участник хеша вне ECS: одно число, меняется командой через ресурс-счётчик.</summary>
public sealed class Box : IStateContributor
{
    public string Id { get; init; } = "box";
    public int Value;

    public void AppendHash(ref StateHasher h) => h.Add(Value);
}

public class StateHashTests
{
    private static byte[] Bytes<T>(T value) where T : unmanaged
    {
        var b = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(b, in value);
        return b;
    }

    private static ulong Hash(FieldLayout layout, ReadOnlySpan<byte> bytes)
    {
        var h = new StateHasher(0);
        layout.Hash(bytes, ref h);
        return h.Value;
    }

    [Fact]
    public void Layout_FindsEveryLeaf_WithOffsets()
    {
        var layout = FieldLayout.Of(typeof(Mixed));
        Assert.Equal(["Flag", "F", "B", "V.X", "V.Y", "V.Z", "Mood", "Target", "D", "Arr[0]", "Arr[1]", "Arr[2]", "C"],
            layout.Leaves.Select(l => l.Path));
        Assert.Equal((int)Marshal.OffsetOf<Mixed>(nameof(Mixed.F)), layout.Leaves[1].Offset);
        Assert.Equal(FieldLayout.Kind.U8, layout.Leaves.Single(l => l.Path == "Mood").Kind);
        Assert.Equal(FieldLayout.Kind.Entity, layout.Leaves.Single(l => l.Path == "Target").Kind);

        var value = new Mixed { Flag = true, F = 1.5f, B = 9, V = new Vector3(1, 2, 3), Mood = Mood.Angry, Target = new Entity(4, 2), D = -2.25, C = 'Ж' };
        value.Arr[0] = 5;
        value.Arr[2] = -7;
        var bytes = Bytes(value);
        int Leaf(string path) => layout.Leaves.Select((l, i) => (l, i)).Single(p => p.l.Path == path).i;
        Assert.Equal(1UL, layout.Bits(bytes, Leaf("Flag")));
        Assert.Equal(BitConverter.SingleToUInt32Bits(1.5f), layout.Bits(bytes, Leaf("F")));
        Assert.Equal(7UL, layout.Bits(bytes, Leaf("Mood")));
        Assert.Equal(4UL | 2UL << 32, layout.Bits(bytes, Leaf("Target")));
        Assert.Equal(BitConverter.DoubleToUInt64Bits(-2.25), layout.Bits(bytes, Leaf("D")));
        Assert.Equal('Ж', (char)layout.Bits(bytes, Leaf("C")));

        Assert.Equal(["Values[0]", "Values[1]"], FieldLayout.Of(typeof(WithFixed)).Leaves.Select(l => l.Path));
    }

    [Fact]
    public void Hash_IgnoresPadding_AndCanonicalizesZeroAndNaN()
    {
        var layout = FieldLayout.Of(typeof(Mixed));
        var a = Bytes(new Mixed { Flag = true, F = 0f, D = double.NaN, V = new Vector3(float.NaN, 0, 0) });
        var b = (byte[])a.Clone();
        // мусор в дырах выравнивания: между Flag и F — три байта
        b[1] = 0xAB;
        b[2] = 0xCD;
        MemoryMarshal.Write(b.AsSpan(layout.Leaves[1].Offset), -0f);   // F = -0
        MemoryMarshal.Write(b.AsSpan(layout.Leaves.Single(l => l.Path == "D").Offset), BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001));   // другой NaN
        MemoryMarshal.Write(b.AsSpan(layout.Leaves.Single(l => l.Path == "V.X").Offset), BitConverter.Int32BitsToSingle(unchecked((int)0xFFC0_0001)));
        b[0] = 2;   // bool «истина» другим байтом
        Assert.Equal(Hash(layout, a), Hash(layout, b));

        MemoryMarshal.Write(b.AsSpan(layout.Leaves[1].Offset), 1e-30f);
        Assert.NotEqual(Hash(layout, a), Hash(layout, b));
    }

    [Fact]
    public void Layout_RejectsReferences()
    {
        Assert.Throws<ArgumentException>(() => FieldLayout.Of(typeof(string)));
        Assert.Throws<ArgumentException>(() => FieldLayout.Of(typeof(int?)));
    }

    private static Simulation Wanderers(ulong seed = 1, IStateContributor? extra = null) =>
        Simulation.Build(ConfigSet.Empty($"sim.seed={seed}"), ["S"], extra is null ? null : b => b.StateContributor(extra), [new WanderModule()]);

    [Fact]
    public void TwoRuns_SameCommands_SameHashEveryTick()
    {
        var a = Wanderers();
        var b = Wanderers();
        Assert.Equal(["ecs"], a.StateParts);
        for (int t = 0; t < 200; t++)
        {
            if (t % 37 == 0)
            {
                a.Enqueue(new Nudge { X = t * 0.01f });
                b.Enqueue(new Nudge { X = t * 0.01f });
            }
            a.Step();
            b.Step();
            Assert.Equal(a.HashState(), b.HashState(), new StateHashComparer());
        }
    }

    [Fact]
    public void DifferentCommand_Or_Seed_ChangesHash()
    {
        var a = Wanderers();
        var b = Wanderers();
        var c = Wanderers(seed: 2);
        for (int t = 0; t < 10; t++)
        {
            if (t == 5) b.Enqueue(new Nudge { X = 1e-3f });
            a.Step();
            b.Step();
            c.Step();
            if (t < 5) Assert.Equal(a.HashState().Total, b.HashState().Total);
        }
        Assert.NotEqual(a.HashState().Total, b.HashState().Total);
        Assert.NotEqual(a.HashState().Total, c.HashState().Total);
    }

    [Fact]
    public void Contributors_ById_EachPartSeparately()
    {
        var box = new Box();
        var sim = Wanderers(extra: box);
        Assert.Equal(["box", "ecs"], sim.StateParts);   // по Id, не по порядку регистрации
        var before = sim.HashState();
        box.Value = 42;
        var after = sim.HashState();
        Assert.NotEqual(before.Parts["box"], after.Parts["box"]);
        Assert.Equal(before.Parts["ecs"], after.Parts["ecs"]);   // расхождение называет участника
        Assert.NotEqual(before.Total, after.Total);

        // одинаковые данные у участников с разными Id — разные хеши
        var twin = Wanderers(extra: new Box { Id = "box2" });
        Assert.NotEqual(before.Parts["box"], twin.HashState().Parts["box2"]);

    }

    [Theory]
    [InlineData("ecs")]
    [InlineData("Box")]
    [InlineData("a.b")]
    public void Contributor_BadId_Refused(string id) =>
        Assert.Throws<InvalidOperationException>(() => Wanderers(extra: new Box { Id = id }));

    [Fact]
    public void Contributor_Twice_Refused() =>
        Assert.Throws<InvalidOperationException>(() => Simulation.Build(ConfigSet.Empty(), ["S"],
            b => b.StateContributor(new Box()).StateContributor(new Box()), []));

    private static Simulation WithSetup(Action<World> setup) =>
        Simulation.Build(ConfigSet.Empty(), ["S"], null, [new WanderModule(), new SetupModule(setup)]);

    [Fact]
    public void EcsPart_SeesFreeIndexOrder_AndFreeGenerations()
    {
        // живые сущности те же (2v1, 3v1, 4v1), но следующая созданная получит разный номер
        var a = WithSetup(w => { w.DestroyImmediate(new Entity(0, 1)); w.DestroyImmediate(new Entity(1, 1)); });
        var b = WithSetup(w => { w.DestroyImmediate(new Entity(1, 1)); w.DestroyImmediate(new Entity(0, 1)); });
        Assert.NotEqual(a.HashState().Parts["ecs"], b.HashState().Parts["ecs"]);
        Assert.Equal([0, 1], a.World.FreeIndices.ToArray());
        Assert.Equal(2, a.World.GenerationAt(0));

        // тот же набор свободных слотов, но у одного другое поколение
        var c = WithSetup(w =>
        {
            w.DestroyImmediate(new Entity(0, 1));
            w.DestroyImmediate(w.CreateImmediate());   // занял слот 0 (0v2) и освободил его с поколением 3
            w.DestroyImmediate(new Entity(1, 1));
        });
        Assert.NotEqual(a.HashState().Parts["ecs"], c.HashState().Parts["ecs"]);
    }

    [Fact]
    public void EcsPart_SeesJournalsAndSystems()
    {
        // команда Nudge с X = 0 не сдвигает бродяг, но её запись ещё в журнале — её прочтут системы следующего тика
        var a = Wanderers();
        var b = Wanderers();
        a.Enqueue(new Nudge { X = 0 });
        a.Step();
        b.Step();
        int journal = a.World.Journals.Single(j => j.Name == "Event<" + typeof(Nudge).FullName + ">").Id;
        Assert.Equal(Wanders(a), Wanders(b));
        Assert.NotEqual(a.HashState().Parts["ecs"], b.HashState().Parts["ecs"]);
        Assert.Equal(1, a.World.JournalLength(journal));
        var entry = a.World.JournalEntry(journal, 0, out long age);
        Assert.Equal(0f, MemoryMarshal.Read<Event<Nudge>>(entry).Value.X);
        Assert.True(age > 0);

        // оборот тика — запись ушла, курсоры дочитаны: состояния снова равны (хеш не помнит номеров записей и барьеров)
        a.Step();
        b.Step();
        Assert.Equal(a.HashState().Parts["ecs"], b.HashState().Parts["ecs"]);

        // выключенная система — другое состояние, хотя данные те же
        var off = WithSetup(w => w.SetActive("wander.walk@S", false));
        var on = WithSetup(_ => { });
        Assert.NotEqual(off.HashState().Parts["ecs"], on.HashState().Parts["ecs"]);
        int system = off.World.Systems.ToList().FindIndex(x => x.Label == "wander.walk");   // номер — место в порядке исполнения
        Assert.False(off.World.SystemActive(system));
        Assert.True(on.World.SystemActive(system));
    }

    private static Wander[] Wanders(Simulation sim) =>
        [.. Enumerable.Range(0, sim.World.EntitySlots).Select(sim.World.EntityAt).Where(e => !e.IsNull).Select(sim.World.Get<Wander>)];

    [Fact]
    public void HashState_DoesNotAllocate()
    {
        var sim = Wanderers(extra: new Box());
        Span<ulong> parts = stackalloc ulong[sim.StateParts.Count];
        sim.Step();
        sim.HashState(parts);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) sim.HashState(parts);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Random_IsPureFunction_OfItsInputs()
    {
        var r = new SimRandom(7);
        var e = new Entity(3, 1);
        uint s = SimRandom.Stream("x");
        Assert.Equal(r.Bits(s, 10, e), r.Bits(s, 10, e));
        Assert.NotEqual(r.Bits(s, 10, e), r.Bits(s, 11, e));
        Assert.NotEqual(r.Bits(s, 10, e), r.Bits(s, 10, new Entity(3, 2)));
        Assert.NotEqual(r.Bits(s, 10, e), r.Bits(s, 10, e, 1));
        Assert.NotEqual(r.Bits(s, 10, e), r.Bits(SimRandom.Stream("y"), 10, e));
        Assert.NotEqual(r.Bits(s, 10, e), new SimRandom(8).Bits(s, 10, e));

        double sum = 0;
        var counts = new int[6];
        for (uint i = 0; i < 20_000; i++)
        {
            float f = r.Float(s, i, e);
            Assert.InRange(f, 0f, 0.99999994f);
            sum += f;
            counts[r.Index(s, i, e, 6, 3)]++;
        }
        Assert.InRange(sum / 20_000, 0.49, 0.51);
        Assert.All(counts, c => Assert.InRange(c, 3000, 3700));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Index(s, 0, e, 0));
    }

    private sealed class StateHashComparer : IEqualityComparer<StateHash>
    {
        public bool Equals(StateHash? x, StateHash? y) =>
            x!.Tick == y!.Tick && x.Total == y.Total && x.Parts.OrderBy(p => p.Key).SequenceEqual(y.Parts.OrderBy(p => p.Key));

        public int GetHashCode(StateHash obj) => obj.Total.GetHashCode();
    }
}
