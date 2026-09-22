using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs.Tests;

/// <summary>Описание мира наружу (агент, инструменты): объявления систем и чтение данных по номеру типа, без типа CLR.</summary>
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
    public void Reader_CopiesByTypeNumber_SelectsPages_ListsComponents()
    {
        var world = Worlds.Builder().Build();
        var entities = new List<Entity>();
        for (int i = 0; i < 6; i++)
        {
            var e = world.CreateImmediate();
            world.AddImmediate(e, new Position { Value = new Vector3(i, 0, 0) });
            if (i % 3 == 0) world.AddImmediate(e, new Frozen());
            entities.Add(e);
        }
        var reader = world.Reader;
        int position = reader.Components.Single(c => c.Type == typeof(Position)).Id;
        int frozen = reader.Components.Single(c => c.Type == typeof(Frozen)).Id;
        Assert.True(reader.Components[frozen].IsTag);
        Assert.True(reader.Components.Single(c => c.Type == typeof(Health)).Reactive);

        var bytes = reader.Copy(entities[4], position)!;
        Assert.Equal(reader.Components[position].Size, bytes.Length);
        Assert.Equal(4f, MemoryMarshal.Read<Position>(bytes).Value.X);
        Assert.Empty(reader.Copy(entities[3], frozen)!);                       // тег — есть, байтов нет
        Assert.Null(reader.Copy(entities[4], frozen));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Copy(entities[0], 999));

        var page = new List<Entity>();
        Assert.Equal(4, reader.Select([position], [frozen], skip: 1, take: 2, page));   // без метки — 1, 2, 4, 5
        Assert.Equal([entities[2], entities[4]], page);

        var ids = new List<int>();
        reader.ComponentsOf(entities[0], ids);
        Assert.Equal(new[] { position, frozen }.Order(), ids);
        world.DestroyImmediate(entities[0]);
        reader.ComponentsOf(entities[0], ids);
        Assert.Empty(ids);
    }

    [Fact]
    public void Resources_PlainDataCopied_ManagedNot()
    {
        var world = Worlds.Builder().Resource(new Position { Value = Vector3.One }).Resource<List<int>>([]).Build();
        var reader = world.Reader;
        var plain = reader.Resources.Single(r => r.Type == typeof(Position));
        var managed = reader.Resources.Single(r => r.Type == typeof(List<int>));
        Assert.Equal(0, managed.Size);
        Assert.Null(reader.CopyResource(managed.Id));
        var bytes = reader.CopyResource(plain.Id)!;
        Assert.Equal(Vector3.One, MemoryMarshal.Read<Position>(bytes).Value);
    }
}
