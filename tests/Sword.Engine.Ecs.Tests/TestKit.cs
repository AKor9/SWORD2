using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Ecs.Tests;

public struct Position { public Vector3 Value; }
public struct Velocity { public Vector3 Value; }
public struct Frozen;   // тег

[Reactive]
public struct Health { public float Value; }

public struct Link { public Entity Target; }
public struct Nested { public int Kind; public Link Inner; }
public struct Pinged { public Entity Who; public int Code; }

/// <summary>
/// Раскладка, у которой смещения маршалинга и управляемые расходятся: bool при маршалинге — 4 байта, char — 1 байт
/// (ANSI). Разрешитель обязан писать по управляемым смещениям.
/// </summary>
public readonly record struct Owned(bool Flag, Link Owner, char Mark);

public struct Mixed { public bool A; public bool B; public Entity Target; public char C; public Entity Second; public bool D; }

public delegate void RunFn(in SystemContext ctx);
public delegate void ReactFn<T>(in SystemContext ctx, ReadOnlySpan<T> triggers);

/// <summary>Непрерывная система из лямбд.</summary>
public sealed class Sys(string label, Action<SystemSetup>? setup, RunFn run) : IContinuousSystem
{
    public void Setup(SystemSetup s)
    {
        s.Label(new SystemLabel(label));
        setup?.Invoke(s);
    }

    public void Run(in SystemContext ctx) => run(ctx);
}

/// <summary>Реактивная система из лямбд: копит всё, что увидела, по тикам.</summary>
public sealed class Reactor<T>(string label, Action<SystemSetup>? setup = null, ReactFn<T>? react = null) : IReactiveSystem<T> where T : struct
{
    public readonly List<(ulong Tick, T Trigger)> Seen = [];

    public void Setup(SystemSetup s)
    {
        s.Label(new SystemLabel(label));
        setup?.Invoke(s);
    }

    public void React(in SystemContext ctx, ReadOnlySpan<T> triggers)
    {
        foreach (var t in triggers) Seen.Add((ctx.Tick, t));
        react?.Invoke(ctx, triggers);
    }
}

public static class Worlds
{
    public static readonly string[] Stages = ["Begin", "Early", "Late", "End"];

    /// <summary>
    /// Мир тестов — всегда с проверками: тесты ожидают их исключений, а по умолчанию проверки включены только в Debug
    /// (<see cref="WorldOptions.DefaultChecks"/>), и в Release тесты бы молча не проверяли ничего.
    /// </summary>
    public static WorldBuilder Builder() => new WorldBuilder { Options = new WorldOptions { Checks = true } }
        .Stages(Stages)
        .Component<Position>().Component<Velocity>().Component<Frozen>().Component<Health>()
        .Component<Link>().Component<Nested>()
        .Event<Pinged>();
}
