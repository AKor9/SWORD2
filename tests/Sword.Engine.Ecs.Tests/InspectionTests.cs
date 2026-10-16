using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs.Tests;

/// <summary>Описание мира наружу: объявления систем и чтение данных по номеру типа, без типа CLR.</summary>
public class InspectionTests
{
    private sealed class Healer : IReactiveSystem<Added<Frozen>>
    {
        public void Setup(SystemSetup s) =>
            s.Label(new SystemLabel("healer")).Reads<Position>().Sets<Health>().Emits<Pinged>().Adds<Velocity>()
                .ReadsJournal<Removed<Position>>().After(new SystemLabel("mover"));

        public void React(in SystemContext ctx, ReadOnlySpan<Added<Frozen>> triggers)
        {
        }
    }

    private sealed class Mover : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("mover")).Writes<Position>().Creates();

        public void Run(in SystemContext ctx)
        {
        }
    }

    [Fact]
    public void SystemInfo_CarriesDeclarations_OrderAndSource()
    {
        var world = Worlds.Builder().System("Late", new Healer(), "heal", source: "Healer.cs:7").System("Late", new Mover()).Build();
        var systems = world.Reader.Systems;
        var mover = systems.Single(s => s.Label == "mover");
        var healer = systems.Single(s => s.Label == "healer");
        Assert.Equal(0, mover.Order);                  // After(mover): порядок стадии по объявлениям
        Assert.Equal(1, healer.Order);
        Assert.Equal([typeof(Position).FullName!], healer.Reads);
        Assert.Equal([typeof(Health).FullName!], healer.Sets);
        Assert.Equal([typeof(Velocity).FullName!], healer.Adds);
        Assert.Equal([typeof(Pinged).FullName!], healer.Emits);
        Assert.Equal(["mover"], healer.After);
        Assert.Equal("Added<Frozen>", healer.Trigger);
        Assert.Contains(typeof(Frozen).FullName!, healer.ConsumesData);   // срабатывание — тоже журнал
        Assert.Contains(typeof(Position).FullName!, healer.ConsumesData);   // и дополнительный журнал
        Assert.Equal("heal", healer.Module);
        Assert.Equal("Healer.cs:7", healer.Source);
        Assert.True(mover.CreatesEntities);
        Assert.Null(mover.Source);
        Assert.EndsWith("+Healer", healer.SystemType);
    }

    [Fact]
    public void Reader_ComponentsByNumber_TagAndReactive()
    {
        var reader = Worlds.Builder().Build().Reader;
        int position = reader.Components.Single(c => c.Type == typeof(Position)).Id;
        int frozen = reader.Components.Single(c => c.Type == typeof(Frozen)).Id;
        Assert.Equal(position, reader.Components[position].Id);
        Assert.False(reader.Components[position].IsTag);
        Assert.True(reader.Components[frozen].IsTag);
        Assert.True(reader.Components.Single(c => c.Type == typeof(Health)).Reactive);
    }

    [Fact]
    public void Resources_PlainDataRead_ManagedNot()
    {
        var world = Worlds.Builder().Resource(new Position { Value = Vector3.One }).Resource<List<int>>([]).Build();
        var reader = world.Reader;
        var plain = reader.Resources.Single(r => r.Type == typeof(Position));
        var managed = reader.Resources.Single(r => r.Type == typeof(List<int>));
        Assert.Equal(0, managed.Size);
        Assert.False(reader.TryReadResource(managed.Id, out _));
        Assert.True(reader.TryReadResource(plain.Id, out var bytes));
        Assert.Equal(Vector3.One, MemoryMarshal.Read<Position>(bytes).Value);
    }

    [Fact]
    public void SlotsAndInPlaceReads_WalkLiveEntitiesByIndex()
    {
        var world = Worlds.Builder().Resource(new Health { Value = 7 }).Resource<List<int>>([]).Build();
        var a = world.CreateImmediate();
        var b = world.CreateImmediate();
        var c = world.CreateImmediate();
        world.AddImmediate(a, new Position { Value = new Vector3(1, 2, 3) });
        world.AddImmediate(c, new Frozen());
        world.DestroyImmediate(b);
        var reader = world.Reader;

        var live = Enumerable.Range(0, reader.EntitySlots).Select(reader.EntityAt).Where(e => !e.IsNull).ToList();
        Assert.Equal([a, c], live);   // по индексу, мёртвый слот пропущен
        Assert.Equal(Entity.Null, reader.EntityAt(b.Index));
        Assert.Equal(Entity.Null, reader.EntityAt(reader.EntitySlots));

        int position = reader.Components.Single(t => t.Type == typeof(Position)).Id;
        int frozen = reader.Components.Single(t => t.Type == typeof(Frozen)).Id;
        Assert.True(reader.TryRead(a, position, out var bytes));
        Assert.Equal(new Vector3(1, 2, 3), MemoryMarshal.Read<Position>(bytes).Value);
        Assert.True(reader.TryRead(c, frozen, out var tag));
        Assert.True(tag.IsEmpty);                                    // тег есть, байтов нет
        Assert.False(reader.TryRead(c, position, out _));
        Assert.False(reader.TryRead(b, position, out _));            // мёртвая

        int health = reader.Resources.Single(r => r.Type == typeof(Health)).Id;
        int list = reader.Resources.Single(r => r.Type == typeof(List<int>)).Id;
        Assert.True(reader.TryReadResource(health, out var hb));
        Assert.Equal(7f, MemoryMarshal.Read<Health>(hb).Value);
        Assert.False(reader.TryReadResource(list, out _));
    }

    [Fact]
    public void FreeIndices_Journals_Cursors_BetweenTicks()
    {
        var world = Worlds.Builder().System("Late", new Healer(), "heal").System("Late", new Mover()).Build();
        var reader = world.Reader;

        // свободные номера: выдаются с конца, у свободного слота — поколение следующей сущности
        var e0 = world.CreateImmediate();
        world.CreateImmediate();
        var e2 = world.CreateImmediate();
        world.DestroyImmediate(e0);
        world.DestroyImmediate(e2);
        Assert.Equal([0, 2], reader.FreeIndices.ToArray());
        Assert.Equal(2, reader.GenerationAt(0));
        Assert.Equal(new Entity(2, 2), world.CreateImmediate());
        Assert.Equal([0], reader.FreeIndices.ToArray());

        // журналы — по имени; запись немедленной операции — возраст 0, у срабатывания healer — одна непрочитанная
        Assert.Equal(["Added<" + typeof(Frozen).FullName + ">", "Removed<" + typeof(Position).FullName + ">"], reader.Journals.Select(j => j.Name));
        int added = 0, removed = 1;
        var e = world.CreateImmediate();
        world.AddImmediate(e, new Position { Value = new Vector3(4, 5, 6) });
        world.AddImmediate(e, new Frozen());
        world.RemoveImmediate<Position>(e);
        Assert.Equal(1, reader.JournalLength(added));
        Assert.Equal(e, MemoryMarshal.Read<Added<Frozen>>(reader.JournalEntry(added, 0, out long age)).Entity);
        Assert.Equal(0, age);
        Assert.Equal(new Vector3(4, 5, 6), MemoryMarshal.Read<Removed<Position>>(reader.JournalEntry(removed, 0, out _)).Value.Value);

        int healer = reader.Systems.Single(s => s.Label == "healer").Order;
        int mover = reader.Systems.Single(s => s.Label == "mover").Order;
        Assert.Equal(0, reader.CursorCount(mover));
        Assert.Equal(2, reader.CursorCount(healer));   // срабатывание первым, затем ReadsJournal
        Assert.Equal(1, reader.CursorUnread(healer, 0, out int journal));
        Assert.Equal(added, journal);

        // тик: healer прочёл, записи ещё живут (оборот тика) и стали старше; следующий тик их убирает
        world.RunTick(0);
        Assert.Equal(0, reader.CursorUnread(healer, 0, out _));
        Assert.Equal(1, reader.JournalLength(added));
        reader.JournalEntry(added, 0, out age);
        Assert.Equal(Worlds.Stages.Length, age);
        world.RunTick(1);
        Assert.Equal(0, reader.JournalLength(added));

        Assert.True(reader.SystemActive(healer));
        world.SetActive("healer@Late", false);
        Assert.False(reader.SystemActive(healer));
    }
}
