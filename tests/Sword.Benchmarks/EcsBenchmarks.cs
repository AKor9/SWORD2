using System.Numerics;
using BenchmarkDotNet.Attributes;
using Sword.Engine.Ecs;

namespace Sword.Benchmarks;

public struct Position { public Vector3 Value; }
public struct Velocity { public Vector3 Value; }

/// <summary>
/// Цель раздела 5 плана: проход <c>Read</c> + <c>Write</c> по чанкам медленнее сырого цикла по <see cref="Span{T}"/>
/// той же раскладки не более чем на 20–30 %. Тик мира с одной системой против цикла по двум массивам; ECS — в форме
/// цикла из правил ECS (по длине колонки) и в наивной форме (до <c>chunk.Count</c>) — для сравнения, не для вердикта.
/// </summary>
[MemoryDiagnoser]
public class EcsIteration
{
    public const int Entities = 100_000;

    [Params(16 * 1024, 64 * 1024)]
    public int ChunkBytes { get; set; }

    private Position[] _positions = null!;
    private Velocity[] _velocities = null!;
    private World _byLength = null!, _byCount = null!;

    [GlobalSetup]
    public void Setup()
    {
        _positions = new Position[Entities];
        _velocities = new Velocity[Entities];
        for (int i = 0; i < Entities; i++) _velocities[i].Value = new Vector3(i, 1, 2);
        _byLength = Build(new MoveByLength());
        _byCount = Build(new MoveByCount());
    }

    private World Build(IContinuousSystem system)
    {
        var w = new WorldBuilder { Options = new WorldOptions { Checks = false, ChunkBytes = ChunkBytes } }
            .Stages("Move").Component<Position>().Component<Velocity>().System("Move", system).Build();
        for (int i = 0; i < Entities; i++)
        {
            var e = w.CreateImmediate();
            w.AddImmediate(e, new Position());
            w.AddImmediate(e, new Velocity { Value = new Vector3(i, 1, 2) });
        }
        return w;
    }

    [Benchmark(Baseline = true)]
    public void RawSpan()
    {
        var p = _positions.AsSpan();
        var v = _velocities.AsSpan()[..p.Length];
        for (int i = 0; i < p.Length; i++) p[i].Value += v[i].Value * 0.01f;
    }

    [Benchmark]
    public void EcsByColumnLength() => _byLength.RunTick(0);

    [Benchmark]
    public void EcsByChunkCount() => _byCount.RunTick(0);

    private sealed class MoveByLength : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("move"));
            _query = s.Query().Read<Velocity>().Write<Position>();
        }

        public void Run(in SystemContext ctx)
        {
            foreach (var chunk in ctx.Iterate(_query))
            {
                var p = chunk.Write<Position>();
                var v = chunk.Read<Velocity>()[..p.Length];
                for (int i = 0; i < p.Length; i++) p[i].Value += v[i].Value * 0.01f;
            }
        }
    }

    private sealed class MoveByCount : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("move"));
            _query = s.Query().Read<Velocity>().Write<Position>();
        }

        public void Run(in SystemContext ctx)
        {
            foreach (var chunk in ctx.Iterate(_query))
            {
                var v = chunk.Read<Velocity>();
                var p = chunk.Write<Position>();
                for (int i = 0; i < chunk.Count; i++) p[i].Value += v[i].Value * 0.01f;
            }
        }
    }
}

/// <summary>
/// Цель раздела 5 плана: создание, удаление и перенос между архетипами через <c>CommandBuffer</c>, запись и чтение
/// журналов — без выделений в устойчивом режиме. Тик: 64 новых сущности с черновиками, удаление прошлых, снятие и
/// добавление компонента, событие; подписчики на <c>Added</c>, <c>Removed</c> и событие.
/// </summary>
[MemoryDiagnoser]
public class EcsCommandsAndJournals
{
    private World _world = null!;
    private ulong _tick;

    public struct Link { public Entity Target; }
    public struct Pinged { public int Code; }

    [GlobalSetup]
    public void Setup()
    {
        Query? churned = null;
        Entity keeper = default;
        _world = new WorldBuilder { Options = new WorldOptions { Checks = false } }
            .Stages("Early", "Late").Component<Position>().Component<Velocity>().Component<Link>().Event<Pinged>()
            .System("Early", new Churn(() => churned!, () => keeper))
            .System("Late", new Count<Added<Position>>("added"))
            .System("Late", new Count<Removed<Position>>("removed"))
            .System("Late", new Count<Removed<Velocity>>("velocity-removed"))
            .System("Late", new Count<Event<Pinged>>("pinged"))
            .Build();
        churned = _world.Query().Read<Position>();
        keeper = _world.CreateImmediate();
        _world.AddImmediate(keeper, new Velocity());
        for (int i = 0; i < 300; i++) _world.RunTick(_tick++);   // прогрев: ёмкости выросли
    }

    [Benchmark]
    public void Tick() => _world.RunTick(_tick++);

    private sealed class Churn(Func<Query> query, Func<Entity> keeper) : IContinuousSystem
    {
        public void Setup(SystemSetup s) =>
            s.Label(new SystemLabel("churn")).Creates().Destroys().Adds<Position>().Adds<Link>().Adds<Velocity>()
                .Removes<Velocity>().Emits<Pinged>();

        public void Run(in SystemContext ctx)
        {
            foreach (var chunk in ctx.Iterate(query()))
                foreach (var e in chunk.Entities) ctx.Commands.Destroy(e);
            for (int i = 0; i < 64; i++)
            {
                var d = ctx.Commands.Create();
                ctx.Commands.Add(d, new Position());
                ctx.Commands.Add(d, new Link { Target = d.Ref });
            }
            ctx.Commands.Emit(new Pinged { Code = 1 });
            // Перенос между архетипами: снят и добавлен заново на одном барьере — Removed и Added.
            ctx.Commands.Remove<Velocity>(keeper());
            ctx.Commands.Add(keeper(), new Velocity());
        }
    }

    private sealed class Count<T>(string label) : IReactiveSystem<T> where T : struct
    {
        public int Seen;

        public void Setup(SystemSetup s) => s.Label(new SystemLabel(label));

        public void React(in SystemContext ctx, ReadOnlySpan<T> triggers) => Seen += triggers.Length;
    }
}
