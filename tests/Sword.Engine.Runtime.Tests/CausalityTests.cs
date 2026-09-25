using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Причинность в Runtime: причина записи журнала — вне хеша участника <c>ecs</c> (одинаковые данные с разными причинами —
/// один хеш, описание без причины); режим причинности не меняет хеш и реплей; команда тика — корень с пояснением, откуда она
/// (клиент, агент, реплей); включение — только между тиками.
/// </summary>
public sealed class CausalityTests
{
    /// <summary>Стадия «R» до «S»: событие <see cref="Nudge"/> с заданной причиной — его прочтут бродяги (журнал с записью).</summary>
    private sealed class CauseModule(int cause) : IModule
    {
        public string Id => "cause";

        public void Configure(SimulationBuilder b) => b.System("R", new Emitter(cause));

        private sealed class Emitter(int cause) : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("cause.emit")).Emits<Nudge>();

            public void Run(in SystemContext ctx)
            {
                ctx.Cause = new CauseId(cause);
                ctx.Commands.Emit(new Nudge { X = 0 });
            }
        }
    }

    private static Simulation Build(int cause) =>
        Simulation.Build(ConfigSet.Empty(), ["R", "S"], null, [new WanderModule(), new CauseModule(cause)]);

    [Fact]
    public void JournalCause_NotInHash_NorInDescribe()
    {
        var a = Build(1);
        var b = Build(2);
        a.Step();
        b.Step();
        Assert.Equal(a.HashState().Parts["ecs"], b.HashState().Parts["ecs"]);
        var journal = "journal:Event<" + typeof(Nudge).FullName + ">";
        var entries = JsonNode.Parse(a.DescribeState("ecs", new DescribeQuery(journal)))!["items"]![0]!["entries"]!.AsArray();
        Assert.NotEmpty(entries);
        Assert.Null(entries[0]!["Cause.Value"]);
        Assert.Equal(a.DescribeState("ecs", new DescribeQuery(journal)), b.DescribeState("ecs", new DescribeQuery(journal)));
    }

    /// <summary>
    /// Режим включён — хеш каждого тика и реплей те же; команда тика — корень (<see cref="CauseKind.Command"/>, без родителя) с
    /// пояснением очереди: клиент (<c>Enqueue</c>), агент (путь инструментов <c>EnqueueRaw</c>), реплей.
    /// </summary>
    [Fact]
    public void Mode_SameHashes_CommandsAreRootsWithOrigin()
    {
        var plain = Simulation.Build(ConfigSet.Empty(), ["S"], [new WanderModule()]);
        var caused = Simulation.Build(ConfigSet.Empty(), ["S"], [new WanderModule()]);
        var log = caused.EnableCausality();
        var recorder = caused.Record(new ReplayInfo("test", "tests", "none"));
        byte[] raw = BitConverter.GetBytes(2f);
        for (int t = 0; t < 30; t++)
        {
            foreach (var s in new[] { plain, caused })
            {
                if (t % 3 == 0) s.Enqueue(new Nudge { X = t });
                if (t == 10) s.EnqueueRaw(typeof(Nudge), s.Tick, raw);
                s.Step();
            }
            Assert.Equal(plain.HashState().Total, caused.HashState().Total);
        }
        var roots = new List<CauseRecord>();
        for (long q = log.FirstSequence; q < log.NextSequence; q++)
            if (log.TryAt(q, out var r) && r.Kind == CauseKind.Command) roots.Add(r);
        Assert.Equal(11, roots.Count);
        Assert.All(roots, r => Assert.Equal(CauseId.None, r.Parent));
        Assert.Equal(10, roots.Count(r => r.Note == "клиент"));
        var tool = Assert.Single(roots, r => r.Note == "агент");
        Assert.Equal(10UL, tool.Tick);
        Assert.EndsWith("TickCommands.cs", tool.File);

        // Реплей в режиме — команды реплея корнями «реплей», хеши сверяются.
        var replay = recorder.ToReplay();
        var again = Simulation.Build(replay.CreateConfig(), ["S"], [new WanderModule()]);
        var againLog = again.EnableCausality();
        var player = again.Play(replay);
        while (again.Tick < replay.Ticks) again.Step();
        Assert.Null(player.Divergence);
        Assert.Contains(Enumerable.Range(0, (int)(againLog.NextSequence - againLog.FirstSequence)),
            i => againLog.TryAt(againLog.FirstSequence + i, out var r) && r.Kind == CauseKind.Command && r.Note == "реплей");

        caused.DisableCausality();
        Assert.Null(caused.Causality);
        long written = log.Written;
        caused.Enqueue(new Nudge { X = 1 });
        caused.Step();
        Assert.Equal(written, log.Written);
    }
}
