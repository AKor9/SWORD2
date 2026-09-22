using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Детерминизм игры (этап 3, точка 3а): хеш состояния по участникам <c>ecs</c> и <c>animation</c> — два прогона с одними
/// командами совпадают тик в тик, запросы агента хеш не меняют, инструменты <c>state.hash</c> и <c>state.describe</c>.
/// </summary>
public sealed class DeterminismTests
{
    /// <summary>Сценарий: стоит, бежит вперёд, шагом вбок, бежит назад-влево, стоит.</summary>
    private static PlayerCommand CommandAt(int tick) => tick switch
    {
        < 30 => new PlayerCommand(),
        < 150 => new PlayerCommand { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 50) },
        < 220 => new PlayerCommand { Move = new Vector2(1, 0), Walk = true },
        < 330 => new PlayerCommand { Move = new Vector2(-1, -1), HasGaze = true, Gaze = new Vector3(40, 0, 0) },
        _ => new PlayerCommand(),
    };

    [Fact]
    public void TwoGames_SameCommands_SameHashEveryTick()
    {
        var a = TestAssets.Simulation();
        var b = TestAssets.Simulation();
        Assert.Equal(["animation", "ecs"], a.StateParts);
        var seen = new HashSet<ulong>();
        for (int t = 0; t < 400; t++)
        {
            a.Enqueue(CommandAt(t));
            b.Enqueue(CommandAt(t));
            a.Step();
            b.Step();
            var ha = a.HashState();
            var hb = b.HashState();
            Assert.True(ha.Total == hb.Total && ha.Parts["animation"] == hb.Parts["animation"], $"{ha} ≠ {hb}");
            seen.Add(ha.Parts["animation"]);
        }
        Assert.True(seen.Count > 300, $"поза менялась лишь {seen.Count} раз — участник animation не видит позу?");
    }

    [Fact]
    public void AnimationPart_SeesPose_NotOnlyEntities()
    {
        var a = TestAssets.Simulation();
        var b = TestAssets.Simulation(ConfigSet.Empty("modules.locomotion.enabled=false"));
        a.Step();
        b.Step();
        // без модуля локомоции в пуле одна поза (бойца) — и она поза привязки
        Assert.NotEqual(a.HashState().Parts["animation"], b.HashState().Parts["animation"]);
        var described = JsonNode.Parse(a.DescribeState("animation", new DescribeQuery("0v1:pelvis")))!;
        Assert.Equal(3, described["poses"]!.GetValue<int>());
        var pelvis = described["items"]![0]!["pelvis"]!;
        Assert.Equal(0.95, pelvis["model"]!["m42"]!.GetValue<double>(), 3);
        Assert.Single(described["items"]![0]!.AsObject(), p => p.Key != "id");   // отбор по суставу
    }

    [Fact]
    public async Task AgentQueries_DoNotChangeHash_StateToolsWork()
    {
        var reference = TestAssets.Simulation();
        var sim = TestAssets.Simulation();
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true, sim.Tick),
            Service = new ServiceQueue(), Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"),
            Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        SwordAgent.Register(host);
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<JsonObject> Call(string tool, JsonObject? args = null)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] },
                };
                var r = JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
                Assert.False(r["isError"]?.GetValue<bool>() ?? false, r.ToJsonString());
                return r["structuredContent"]!.AsObject();
            }

            // одна и та же команда бега — агентом в живую симуляцию, напрямую — в эталонную
            var move = new JsonObject { ["command"] = "PlayerCommand", ["fields"] = new JsonObject { ["Move"] = new JsonObject { ["Y"] = 1 } }, ["repeat"] = 60 };
            await Call("command.send", move);
            for (int t = 0; t < 60; t++)
            {
                reference.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
                reference.Step();
            }
            for (int i = 0; i < 6; i++)
            {
                await Call("time.step", new JsonObject { ["ticks"] = 10 });
                // шквал чтений между тиками
                await Call("world.query", new JsonObject { ["with"] = new JsonArray("Transform") });
                await Call("anim.pose", new JsonObject { ["joints"] = new JsonArray("pelvis", "foot_l") });
                await Call("state.describe", new JsonObject { ["part"] = "animation", ["filter"] = "foot" });
                await Call("state.describe", new JsonObject { ["part"] = "ecs", ["filter"] = "0v1" });
            }

            var hash = await Call("state.hash");
            var expected = reference.HashState();
            Assert.Equal(60ul, hash["receipt"]!["tickId"]!.GetValue<ulong>());
            Assert.Equal($"{expected.Total:X16}", hash["total"]!.GetValue<string>());
            Assert.Equal($"{expected.Parts["animation"]:X16}", hash["parts"]!["animation"]!.GetValue<string>());

            var ecs = await Call("state.describe", new JsonObject { ["part"] = "ecs", ["filter"] = "0v1" });
            var transform = ecs["items"]![0]![typeof(Transform).FullName!]!;
            Assert.Equal(reference.World.Get<Transform>(GameCore.Player(reference.World)).Position.Z, transform["Position.Z"]!.GetValue<float>());

            // все позы целиком: на тестовом скелете (13 суставов) ответ мал и приходит сразу; большой уходит в файл
            // (CoreToolsTests.StateDescribe_LargeAnswer_GoesToArtifact)
            var all = await Call("state.describe", new JsonObject { ["part"] = "animation" });
            Assert.Equal(3, all["matched"]!.GetValue<int>());
            Assert.Equal(3, all["items"]!.AsArray().Count);
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }
}
