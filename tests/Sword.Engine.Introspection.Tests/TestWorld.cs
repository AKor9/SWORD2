using System.Numerics;
using System.Runtime.CompilerServices;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests;

public enum Gait { Stand, Walk, Run }

public struct Body
{
    public Vector3 Position;
    public float Speed;
}

public struct Marker;

public struct Order
{
    public Vector2 Move;
    public bool Hurry;
    public Gait Gait;
    public Entity Target;
    public byte Priority;
}

[InlineArray(3)]
public struct Three
{
    private float _element;
}

public struct Everything
{
    public Vector3 V;
    public Quaternion Q;
    public Gait Gait;
    public bool Flag;
    public char Letter;
    public ulong Big;
    public sbyte Small;
    public double Precise;
    public float NaN;
    public Entity Who;
    public Three Arr;
    public int Auto { get; set; }
}

/// <summary>Модуль-подопытный: тела и метки, команда Order двигает тела по X на Move.X · (Hurry ? 2 : 1).</summary>
public sealed class TestModule : IModule
{
    public string Id => "test";

    public void Configure(SimulationBuilder b) =>
        b.Component<Body>().Component<Marker>().Command<Order>()
            .System("S1", new Mover())
            .System("S2", new Reader())
            .OnWorldBuilt(w =>
            {
                for (int i = 0; i < 5; i++)
                {
                    var e = w.CreateImmediate();
                    w.AddImmediate(e, new Body { Position = new Vector3(i, 0, 0), Speed = i * 0.5f });
                    if (i % 2 == 0) w.AddImmediate(e, new Marker());
                }
            });

    public sealed class Mover : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("test.mover")).ReadsJournal<Event<Order>>().Writes<Body>();
            _query = s.Query().Write<Body>();
        }

        public void Run(in SystemContext ctx)
        {
            var orders = ctx.Journal<Event<Order>>();
            if (orders.IsEmpty) return;
            var o = orders[^1].Value;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var body in chunk.Write<Body>())
                    body.Position.X += o.Move.X * (o.Hurry ? 2 : 1);
        }
    }

    public sealed class Reader : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("test.reader")).Reads<Body>();

        public void Run(in SystemContext ctx)
        {
        }
    }

    public static Simulation Build() => Simulation.Build(ConfigSet.Empty(), ["S1", "S2"], [new TestModule()]);
}
