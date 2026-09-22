using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

/// <summary>Вид только для чтения: владелец мира регистрирует данные вне ECS под интерфейсом, наблюдатель — читает.</summary>
public sealed class ReadViewTests
{
    public interface ICounterView : IReadView
    {
        int Count { get; }
    }

    public interface IOtherView : IReadView;

    private sealed class Counter : ICounterView
    {
        public int Count { get; set; }
    }

    [Fact]
    public void Reader_SeesRegisteredView()
    {
        var counter = new Counter { Count = 3 };
        var world = Worlds.Builder().ReadView<ICounterView>(counter).Build();
        Assert.Equal(3, world.Reader.View<ICounterView>().Count);
        counter.Count = 5;   // вид — окно в данные владельца, не копия
        Assert.Equal(5, world.Reader.View<ICounterView>().Count);
        Assert.True(world.Reader.TryView<ICounterView>(out var v));
        Assert.Same(counter, v);
    }

    [Fact]
    public void MissingView_IsReported()
    {
        var world = Worlds.Builder().Build();
        Assert.False(world.Reader.TryView<IOtherView>(out _));
        Assert.Contains("не зарегистрирован", Assert.Throws<InvalidOperationException>(() => world.Reader.View<IOtherView>()).Message);
    }

    [Fact]
    public void ClassAsView_IsRejected()
    {
        // Класс отдал бы наблюдателю все свои публичные члены, в том числе сеттеры, — только интерфейс.
        var e = Assert.Throws<ArgumentException>(() => Worlds.Builder().ReadView(new Counter()));
        Assert.Contains("не интерфейс", e.Message);
    }

    [Fact]
    public void ViewRegisteredTwice_IsError() =>
        Assert.Throws<InvalidOperationException>(() => Worlds.Builder().ReadView<ICounterView>(new Counter()).ReadView<ICounterView>(new Counter()));
}
