using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// Реплеи и сеанс агента (этап 3б): replay.save/status/check/diff, session.start, time.seek — над подопытной симуляцией
/// с построителем, как у Headless.
/// </summary>
public sealed class ReplayToolsTests : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _loop;
    private readonly AgentHost _host;
    private int _id;

    private static Simulation Build(ConfigSet config)
    {
        var sim = Simulation.Build(config, ["S"], [new PushModule(), new BaseFactModule()]);
        config.Complete();
        return sim;
    }

    public ReplayToolsTests()
    {
        var sim = Build(ConfigSet.Empty());
        _host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless",
            Catalog = new Catalog(sim.World, sim.CommandTypes),
            Time = new TimeControl(paused: true, sim.Tick),
            Service = new ServiceQueue(),
            Log = new LogRing(64),
            ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-agent-replay-tests"),
            Stop = () => { },
            ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        var session = new AgentSimulation(sim, new ReplayInfo("1.0.0+test", "tests", "none"), Build, sets => ConfigSet.Empty([.. sets]));
        _loop = new Thread(() => SimulationLoop.Run(session, _host, _stop.Token)) { IsBackground = true };
        _loop.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _host.Service.Wake();
        _loop.Join(TimeSpan.FromSeconds(5));
    }

    private async Task<JsonObject> Tool(string name, JsonObject? arguments = null, bool error = false)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments ?? [] },
        };
        var r = JsonNode.Parse((await _host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
        Assert.True(error == (r["isError"]?.GetValue<bool>() ?? false), r.ToJsonString());
        return r["structuredContent"]!.AsObject();
    }

    private Task<JsonObject> Push(float x, ulong tick, int repeat = 1) => Tool("command.send", new JsonObject
    {
        ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = x }, ["tick"] = tick, ["repeat"] = repeat,
    });

    private Task<JsonObject> Step(int ticks) => Tool("time.step", new JsonObject { ["ticks"] = ticks });

    private async Task<string> Hash() => (await Tool("state.hash"))["total"]!.GetValue<string>();

    /// <summary>Прогон с командами агента: толчки на 5…14 и 30.</summary>
    private async Task<Dictionary<int, string>> PlayScript(int ticks)
    {
        await Push(0.5f, 5, repeat: 10);
        await Push(-2, 30);
        var hashes = new Dictionary<int, string>();
        for (int t = 1; t <= ticks; t++)
        {
            await Step(1);
            if (t % 10 == 0) hashes[t] = await Hash();
        }
        return hashes;
    }

    [Fact]
    public async Task Save_Status_AndSessionRecordingPlaysBack()
    {
        await PlayScript(40);
        var status = await Tool("replay.status");
        Assert.Equal(40, status["recording"]!["ticks"]!.GetValue<int>());
        Assert.Equal(41, status["recording"]!["hashes"]!.GetValue<int>());
        Assert.Null(status["playback"]);

        var saved = await Tool("replay.save", new JsonObject { ["name"] = "script" });
        string file = saved["file"]!.GetValue<string>();
        Assert.EndsWith("script.swreplay", file);
        Assert.Equal(40, saved["ticks"]!.GetValue<int>());
        Assert.Equal(11, saved["commands"]!.GetValue<int>());
        await Tool("replay.save", new JsonObject { ["name"] = "..\\up" }, error: true);   // только имя файла

        var check = await Tool("replay.check", new JsonObject { ["path"] = "script.swreplay" });
        Assert.True(check["match"]!.GetValue<bool>());
        Assert.Equal(41, check["hashesChecked"]!.GetValue<int>());
        var session = await Tool("replay.check", new JsonObject { ["path"] = "session" });
        Assert.True(session["match"]!.GetValue<bool>());

        // эксперимент: другое зерно поверх реплея — расхождение сразу (зерно — ресурс мира)
        var seeded = await Tool("replay.check", new JsonObject { ["path"] = "script.swreplay", ["set"] = new JsonArray("sim.seed=9") });
        Assert.False(seeded["match"]!.GetValue<bool>());
        Assert.Equal(0, seeded["divergence"]!["tick"]!.GetValue<int>());
        Assert.Null(seeded["divergence"]!["cause"]);   // сведения те же — причины нет

        // 8з: другое сведение реплея (как другая база Motion Matching) — предупреждение и вероятная причина в расхождении.
        var other = await Tool("replay.check", new JsonObject { ["path"] = "script.swreplay", ["set"] = new JsonArray("sim.seed=9", "basefact.base=B") });
        Assert.Contains(other["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("basefact.base: в реплее A, в симуляции B"));
        Assert.Contains("basefact.base: в реплее A, в симуляции B", other["divergence"]!["cause"]!.GetValue<string>());
    }

    /// <summary>Настройки подопытного сведения реплея.</summary>
    public sealed class BaseFactSettings
    {
        public string Base { get; set; } = "A";
    }

    /// <summary>Подопытное сведение реплея (8з): строка настроек в заголовке реплея (<c>facts</c>), как отпечаток базы Motion Matching.</summary>
    private sealed class BaseFactModule : IModule
    {
        public string Id => "basefact";

        public void Configure(SimulationBuilder b)
        {
            string value = b.Settings<BaseFactSettings>().Base;
            b.ReplayFact("basefact.base", () => value);
        }
    }

    [Fact]
    public async Task Seek_Back_RebuildsAndRepeatsTimeline_Forward_Steps()
    {
        var hashes = await PlayScript(50);
        var back = await Tool("time.seek", new JsonObject { ["tick"] = 20 });
        Assert.True(back["rebuilt"]!.GetValue<bool>());
        Assert.Equal(50, back["from"]!.GetValue<int>());
        Assert.Equal(20ul, (await Tool("session.status"))["tick"]!.GetValue<ulong>());
        Assert.Equal(hashes[20], await Hash());   // то же состояние

        // вперёд — сыгранные команды агента (30: толчок −2) повторяются из записи
        await Step(30);
        Assert.Equal(hashes[50], await Hash());
        var playback = (await Tool("replay.status"))["playback"]!;
        Assert.True(playback["match"]!.GetValue<bool>());
        Assert.Equal(51, playback["hashesChecked"]!.GetValue<int>());

        var forward = await Tool("time.seek", new JsonObject { ["tick"] = 70 });
        Assert.False(forward["rebuilt"]!.GetValue<bool>());
        Assert.Equal(70ul, (await Tool("session.status"))["tick"]!.GetValue<ulong>());
        Assert.True((await Tool("session.status"))["paused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Seek_Forward_Timeout_KeepsReachedTick_TimeInSync()
    {
        // 10 000 000 тиков за 1 с не сыграть: время выходит посреди перемотки, сыгранное остаётся
        var error = await Tool("time.seek", new JsonObject { ["tick"] = 10_000_000, ["timeoutSeconds"] = 1 }, error: true);
        Assert.Equal(nameof(TimeoutException), error["kind"]!.GetValue<string>());
        Assert.Contains("дошли до тика", error["error"]!.GetValue<string>());
        var status = await Tool("session.status");
        ulong reached = status["tick"]!.GetValue<ulong>();
        Assert.InRange(reached, 1ul, 9_999_999ul);
        Assert.True(status["paused"]!.GetValue<bool>());
        Assert.Equal(0, status["remainingTicks"]!.GetValue<int>());

        // время сеанса — по симуляции: пройденный тик не играется заново, шаг — ровно шаг
        await Tool("time.runUntil", new JsonObject { ["tick"] = 1 });
        Assert.Equal(reached, (await Tool("session.status"))["tick"]!.GetValue<ulong>());
        Assert.Equal(reached + 3, (await Step(3))["tick"]!.GetValue<ulong>());
        Assert.Equal(reached + 3, (await Tool("replay.status"))["recording"]!["ticks"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task SessionStart_FromReplay_Plays_AgentCommandBranches()
    {
        var hashes = await PlayScript(40);
        await Tool("replay.save", new JsonObject { ["name"] = "base" });

        var started = await Tool("session.start", new JsonObject { ["replay"] = "base.swreplay" });
        Assert.Equal(0, started["tick"]!.GetValue<int>());
        Assert.Equal(2, started["run"]!.GetValue<int>());
        Assert.True(File.Exists(started["previousReplay"]!.GetValue<string>()));   // прежняя симуляция — в артефакты
        Assert.Equal(0ul, (await Tool("session.status"))["tick"]!.GetValue<ulong>());
        await Step(40);
        Assert.Equal(hashes[40], await Hash());
        var status = await Tool("replay.status");
        Assert.EndsWith("base.swreplay", status["source"]!.GetValue<string>());
        Assert.True(status["playback"]!["finished"]!.GetValue<bool>());
        Assert.True(status["playback"]!["match"]!.GetValue<bool>());
        Assert.False((await Tool("session.status"))["causality"]!.GetValue<bool>());

        // режим причинности (6д): та же запись — те же хеши; журнал пишется; режим переживает перемотку назад
        var caused = await Tool("session.start", new JsonObject { ["replay"] = "base.swreplay", ["causality"] = true });
        Assert.True(caused["causality"]!.GetValue<bool>());
        await Step(40);
        Assert.Equal(hashes[40], await Hash());
        Assert.True((await Tool("session.status"))["causality"]!["written"]!.GetValue<long>() > 0);
        await Tool("time.seek", new JsonObject { ["tick"] = 10 });
        Assert.True((await Tool("session.status"))["causality"] is JsonObject);
        await Tool("session.start", new JsonObject { ["replay"] = "base.swreplay", ["causality"] = false });
        Assert.False((await Tool("session.status"))["causality"]!.GetValue<bool>());

        // ветвление: команда агента в тике 30 перебивает команду реплея — расхождение ровно после тика 30
        await Tool("session.start", new JsonObject { ["replay"] = "base.swreplay" });
        await Push(1, 30);
        await Step(40);
        var divergence = (await Tool("replay.status"))["playback"]!["divergence"]!;
        Assert.Equal(31, divergence["tick"]!.GetValue<int>());
        Assert.Equal(30, divergence["lastMatch"]!.GetValue<int>());
        Assert.Equal("ecs", divergence["parts"]![0]!["part"]!.GetValue<string>());

        // запись ветки против файла: первые разные команды — тик 30, первый разный хеш — после 31 тика
        var diff = await Tool("replay.diff", new JsonObject { ["a"] = "base.swreplay", ["b"] = "session" });
        Assert.False(diff["same"]!.GetValue<bool>());
        Assert.Equal(30, diff["commands"]!["firstTick"]!.GetValue<int>());
        Assert.Equal(31, diff["hashes"]!["firstTick"]!.GetValue<int>());
        Assert.Equal(30, diff["hashes"]!["lastCommon"]!.GetValue<int>());

        // без реплея — конфиг запуска с ключами
        var fresh = await Tool("session.start", new JsonObject { ["set"] = new JsonArray("sim.seed=3") });
        Assert.Null(fresh["replay"]);
        Assert.Contains("--set sim.seed=3", fresh["configSources"]!.AsArray().Select(s => s!.GetValue<string>()));
        await Tool("session.start", new JsonObject { ["replay"] = "nope.swreplay" }, error: true);
    }

    [Fact]
    public async Task ReplayObserve_EventsWatchCondition_ToFile()
    {
        await PlayScript(40);
        await Tool("replay.save", new JsonObject { ["name"] = "obs" });
        var r = await Tool("replay.observe", new JsonObject
        {
            ["path"] = "obs.swreplay",
            ["events"] = new JsonArray("Push"),
            ["watch"] = new JsonObject { ["entities"] = new JsonArray("2v1"), ["fields"] = new JsonArray("Body.Position.X") },
            // Тело 2v1: X = 2 + 0,5 на тиках 5…14, −2 на тике 30 → X > 5 после тиков 12…30.
            ["condition"] = JsonNode.Parse("""{"field":"Body.Position.X","greaterThan":5}"""),
        });
        Assert.Equal(40ul, r["ticks"]!.GetValue<ulong>());
        Assert.Equal(11, r["events"]!["Event<Push>"]!.GetValue<int>());
        Assert.Equal(40, r["watchRows"]!.GetValue<int>());
        Assert.Equal(12ul, r["condition"]!["firstTickId"]!.GetValue<ulong>());
        Assert.Equal(30ul, r["condition"]!["lastTickId"]!.GetValue<ulong>());
        Assert.Equal(19, r["condition"]!["ticks"]!.GetValue<int>());
        Assert.True(r["playback"]!["match"]!.GetValue<bool>());
        var lines = File.ReadAllLines(r["file"]!.GetValue<string>());
        Assert.Equal(r["lines"]!.GetValue<int>(), lines.Length);
        Assert.Contains(lines, l => l.Contains("\"condition\":true") && l.Contains("\"tickId\":12"));
        Assert.Equal(0, (await Tool("session.status"))["tick"]!.GetValue<int>() - 40);   // сеанс не тронут

        var window = await Tool("replay.observe", new JsonObject { ["path"] = "obs.swreplay", ["events"] = new JsonArray("Push"), ["from"] = 10, ["to"] = 20 });
        Assert.Equal(5, window["events"]!["Event<Push>"]!.GetValue<int>());   // тики 10…14
        // Окно for считает тики и до from: X > 5 держится с tickId 12 — к первому записанному tickId 16 уже 5 тиков подряд.
        var held = await Tool("replay.observe", new JsonObject
        {
            ["path"] = "obs.swreplay", ["from"] = 15, ["to"] = 20,
            ["condition"] = JsonNode.Parse("""{"for":5,"that":{"field":"Body.Position.X","greaterThan":5}}"""),
        });
        Assert.Equal(16ul, held["condition"]!["firstTickId"]!.GetValue<ulong>());
        await Tool("replay.observe", new JsonObject { ["path"] = "obs.swreplay" }, error: true);   // нечего наблюдать
    }

    /// <summary>
    /// Нарушение инварианта в реплее (этап 3г): NaN-толчок тика 12 — replay.observe находит первое нарушение глубже истории;
    /// инвариант агента переживает перемотку назад.
    /// </summary>
    [Fact]
    public async Task ReplayObserve_FindsViolation_AgentInvariantSurvivesSeek()
    {
        await Tool("command.send", new JsonObject { ["command"] = "Push", ["fields"] = new JsonObject { ["X"] = "NaN" }, ["tick"] = 12 });
        await Step(20);
        await Tool("replay.save", new JsonObject { ["name"] = "nan" });
        var r = await Tool("replay.observe", new JsonObject { ["path"] = "nan.swreplay", ["events"] = new JsonArray("InvariantViolation") });
        Assert.Equal(24, r["violations"]!["count"]!.GetValue<int>());                       // три тела × тики 12…19
        Assert.Equal(("ecs.finite", 12ul), (r["violations"]!["first"]!["id"]!.GetValue<string>(), r["violations"]!["first"]!["tick"]!.GetValue<ulong>()));
        Assert.Equal(24, r["events"]!["InvariantViolation"]!.GetValue<int>());

        await Tool("invariant.define", new JsonObject { ["id"] = "agent.anyBody", ["condition"] = JsonNode.Parse("""{"count":{"component":"Body"},"equals":3}""") });
        await Tool("time.seek", new JsonObject { ["tick"] = 5 });
        var list = await Tool("invariant.list", new JsonObject { ["filter"] = "agent" });
        var agent = list["invariants"]!.AsArray().Single()!;
        Assert.Equal(("agent.anyBody", "agent"), (agent["id"]!.GetValue<string>(), agent["owner"]!.GetValue<string>()));
        Assert.Equal(5, agent["evaluations"]!.GetValue<int>());                               // перемотка — повтор с тика 0
    }

    [Fact]
    public async Task Seek_Back_RebuildsHistory_ByReplay()
    {
        await PlayScript(40);
        await Tool("time.seek", new JsonObject { ["tick"] = 20 });
        // История снята и при повторе таймлайна: толчки тиков 5…14 видны после перемотки назад.
        var events = await Tool("events.history", new JsonObject { ["types"] = new JsonArray("Push") });
        Assert.Equal(10, events["total"]!.GetValue<int>());
        Assert.Equal(19ul, events["window"]!["newest"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task Seek_Back_DuringPlayback_KeepsFileFuture()
    {
        var hashes = await PlayScript(40);
        await Tool("replay.save", new JsonObject { ["name"] = "future" });
        await Tool("session.start", new JsonObject { ["replay"] = "future.swreplay" });
        await Step(20);
        await Tool("time.seek", new JsonObject { ["tick"] = 10 });
        await Step(30);   // тики 20…39 — из файла: таймлайн — запись до 20 и будущее реплея
        Assert.Equal(hashes[40], await Hash());
        Assert.True((await Tool("replay.status"))["playback"]!["match"]!.GetValue<bool>());
    }
}
