using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

/// <summary>Команда со всеми видами листьев — проверка точной записи значений в файле реплея.</summary>
public struct AllKinds
{
    public bool Flag;
    public sbyte I8;
    public byte U8;
    public short I16;
    public ushort U16;
    public char C;
    public int I32;
    public uint U32;
    public long I64;
    public ulong U64;
    public float F;
    public double D;
    public Entity Target;
    public Vector3 V;
    public Mood Mood;
}

public sealed class KindsModule : IModule
{
    public string Id => "kinds";

    public void Configure(SimulationBuilder b) => b.Command<AllKinds>();
}

public class ReplayTests
{
    private static readonly ReplayInfo Info = new("1.0.0+test", "tests", "none");

    private static Simulation Wanderers(ConfigSet? config = null, params IModule[] extra) =>
        Simulation.Build(config ?? ConfigSet.Empty(), ["S"], [new WanderModule(), .. extra]);

    /// <summary>Прогон с командами: Nudge на отрезках, иногда две за тик.</summary>
    private static Replay RecordRun(int ticks = 200, int hashEvery = 1, ConfigSet? config = null)
    {
        var sim = Wanderers(config);
        var recorder = sim.Record(Info, hashEvery);
        for (int t = 0; t < ticks; t++)
        {
            if (t is >= 20 and < 60) sim.Enqueue(new Nudge { X = 0.25f });
            if (t == 90)
            {
                sim.Enqueue(new Nudge { X = -1 });
                sim.Enqueue(new Nudge { X = 3 });
            }
            sim.Step();
        }
        return recorder.ToReplay();
    }

    private static ReplayPlayer PlayAll(Replay replay, Action<Simulation, ulong>? beforeTick = null)
    {
        var sim = Wanderers(replay.CreateConfig());
        var player = sim.Play(replay);
        while (sim.Tick < replay.Ticks)
        {
            beforeTick?.Invoke(sim, sim.Tick);
            sim.Step();
        }
        return player;
    }

    [Fact]
    public void Record_ThenPlayFromFile_SameHashesEveryTick()
    {
        var replay = Replay.Parse(RecordRun().ToJson());
        Assert.Equal(200ul, replay.Ticks);
        Assert.Equal(201, replay.Hashes.Count);   // тик 0 и каждый тик
        Assert.Equal(["ecs"], replay.Parts);
        Assert.Equal(["wander"], replay.Modules);
        var player = PlayAll(replay);
        Assert.Null(player.Divergence);
        Assert.Equal(201, player.HashesChecked);
        Assert.Equal(200ul, player.LastMatch);
        Assert.Empty(player.Warnings);
    }

    [Fact]
    public void Runs_MergeEqualTicks_KeepOrderInsideTick()
    {
        var replay = RecordRun();
        Assert.Equal(2, replay.Runs.Count);
        Assert.Equal((20ul, 40), (replay.Runs[0].First, replay.Runs[0].Count));
        var two = replay.Runs[1];
        Assert.Equal((90ul, 1), (two.First, two.Count));
        Assert.Equal([BitConverter.SingleToUInt32Bits(-1)], two.Commands[0].Leaves.Select(l => (uint)l));
        Assert.Equal([BitConverter.SingleToUInt32Bits(3)], two.Commands[1].Leaves.Select(l => (uint)l));
        Assert.Equal(42, replay.CommandCount);
        Assert.Empty(replay.CommandsAt(19));
        Assert.Single(replay.CommandsAt(59));
        Assert.Empty(replay.CommandsAt(60));

        // ещё одна запись — тот же текст: запись детерминирована целиком (без даты)
        Assert.Equal(Strip(replay.ToJson()), Strip(RecordRun().ToJson()));
        static string Strip(string json) => string.Join("\r\n", json.Split("\r\n").Where(l => !l.Contains("recordedUtc")));
    }

    [Fact]
    public void File_KeepsEveryLeafBitExact()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new KindsModule()]);
        var recorder = sim.Record(Info);
        var values = new[]
        {
            new AllKinds { Flag = true, I8 = -128, U8 = 255, I16 = -32768, U16 = 65535, C = 'Ж', I32 = int.MinValue, U32 = uint.MaxValue,
                I64 = long.MinValue, U64 = ulong.MaxValue, F = -0f, D = double.Epsilon, Target = new Entity(12, 3), V = new Vector3(1e-30f, float.MaxValue, -float.Epsilon), Mood = Mood.Angry },
            new AllKinds { F = BitConverter.UInt32BitsToSingle(0x7FC0_1234), D = -0d, V = new Vector3(float.PositiveInfinity, float.NegativeInfinity, 0.1f) },
        };
        foreach (var v in values)
        {
            sim.Enqueue(v);
            sim.Step();
        }
        var original = recorder.ToReplay();
        var parsed = Replay.Parse(original.ToJson());
        Assert.Equal(original.CommandTypes.Single().Fields, parsed.CommandTypes.Single().Fields);
        Assert.Contains("Mood:u8", parsed.CommandTypes[0].Fields);
        Assert.Contains("Target:entity", parsed.CommandTypes[0].Fields);
        for (int r = 0; r < original.Runs.Count; r++)
            Assert.Equal(original.Runs[r].Commands[0].Leaves, parsed.Runs[r].Commands[0].Leaves);
        Assert.Equal(original.ToJson(), parsed.ToJson());   // запись → чтение → запись — тот же текст

        // и обратно в команду — те же байты
        var again = Simulation.Build(parsed.CreateConfig(), ["S"], [new KindsModule()]);
        again.Play(parsed);
        var recorder2 = again.Record(Info);
        again.Step();
        again.Step();
        Assert.Equal(original.Runs.Select(r => r.Commands[0].Leaves), recorder2.ToReplay().Runs.Select(r => r.Commands[0].Leaves));
    }

    [Fact]
    public void CorruptedCommand_NamesTickAndPart()
    {
        var replay = RecordRun();
        var run = replay.Runs[0];
        // тик 30: другая команда — отрезок 20…59 делится
        var corrupted = new Replay
        {
            Build = replay.Build, TickRate = replay.TickRate, Seed = replay.Seed, Ticks = replay.Ticks, HashEvery = replay.HashEvery,
            Parts = replay.Parts, CommandTypes = replay.CommandTypes, Config = replay.Config, Hashes = replay.Hashes,
            Runs =
            [
                new ReplayRun(20, 10, run.Commands),
                new ReplayRun(30, 1, [new ReplayCommand(0, [BitConverter.SingleToUInt32Bits(0.2500001f)])]),
                new ReplayRun(31, 29, run.Commands),
                .. replay.Runs.Skip(1),
            ],
        };
        var player = PlayAll(Replay.Parse(corrupted.ToJson()));
        Assert.NotNull(player.Divergence);
        Assert.Equal(31ul, player.Divergence.Tick);        // сыграно 31 тик: расхождение — в тике 30
        Assert.Equal(30ul, player.Divergence.LastMatch);
        Assert.Equal("ecs", Assert.Single(player.Divergence.Parts).Part);
        Assert.Equal(32, player.HashesChecked);            // тики 0…31; после расхождения не сверяется
    }

    [Fact]
    public void HashEvery_DivergenceBetweenHashes_GivesWindow()
    {
        var replay = RecordRun(hashEvery: 10);
        Assert.Equal(21, replay.Hashes.Count);
        var changed = Replay.Parse(replay.ToJson().Replace("[90, 1, [0, -1], [0, 3]]", "[90, 1, [0, -1], [0, 2]]"));
        var player = PlayAll(changed);
        Assert.Equal(100ul, player.Divergence!.Tick);
        Assert.Equal(90ul, player.Divergence.LastMatch);   // в тиках 90…99
    }

    [Fact]
    public void Config_ComesFromReplay_NotFromEnvironment()
    {
        var replay = RecordRun(config: ConfigSet.Empty("sim.seed=5"));
        Assert.Equal(5ul, replay.Seed);
        var sim = Wanderers(replay.CreateConfig());
        Assert.Equal(5ul, sim.Settings.Seed);
        Assert.Contains("реплей", sim.Config.Sources);
        Assert.Null(PlayAll(replay).Divergence);

        // эксперимент: другое зерно поверх реплея — предупреждение и расхождение сразу, на тике 0 (зерно — ресурс мира)
        var other = Wanderers(replay.CreateConfig(["sim.seed=6"]));
        var player = other.Play(replay);
        Assert.Contains(player.Warnings, w => w.Contains("зерно"));
        Assert.Equal(0ul, player.Divergence!.Tick);
        Assert.Null(player.Divergence.LastMatch);
    }

    [Fact]
    public void Binding_UnknownCommandOrField_Refused_MissingField_Zero()
    {
        var replay = RecordRun(ticks: 30);
        string json = replay.ToJson();
        var unknown = Replay.Parse(json.Replace(typeof(Nudge).FullName!, "Some.Other"));
        Assert.Contains("Some.Other", Assert.Throws<InvalidDataException>(() => Wanderers(unknown.CreateConfig()).Play(unknown)).Message);
        Assert.Throws<InvalidDataException>(() => Replay.Parse(json.Replace("\"X:f32\"", "\"X:i32\"")));   // 0.25 — не i32
        var badField = Replay.Parse(json.Replace("\"X:f32\"", "\"Y:f32\""));
        Assert.Throws<InvalidDataException>(() => Wanderers(badField.CreateConfig()).Play(badField));

        Assert.Throws<InvalidDataException>(() => Replay.Parse("{\"format\": \"other\"}"));
        Assert.Throws<InvalidDataException>(() => Replay.Parse("{\"format\": \"sword-replay\", \"commandTypes\": [{\"name\": \"A\", \"fields\": [\"X:float\"]}]}"));
    }

    [Fact]
    public void Script_WithoutHeaderAndHashes_Plays_AndReRecordGivesHashes()
    {
        string script = $$"""
            {
              "format": "sword-replay",
              "commandTypes": [ { "name": "{{typeof(Nudge).FullName}}", "fields": ["X:f32"] } ],
              "commands": [ [10, 5, [0, 0.5]] ],
              "ticks": 40
            }
            """;
        var replay = Replay.Parse(script);
        Assert.Equal(40ul, replay.Ticks);
        var sim = Wanderers(replay.CreateConfig());
        var player = sim.Play(replay);
        var recorder = sim.Record(Info);   // перезапись: те же команды, свои хеши
        while (sim.Tick < replay.Ticks) sim.Step();
        Assert.Equal(0, player.HashesChecked);
        var rebased = recorder.ToReplay();
        Assert.Equal(41, rebased.Hashes.Count);
        Assert.Equal((10ul, 5), (rebased.Runs.Single().First, rebased.Runs.Single().Count));
        Assert.Null(PlayAll(rebased).Divergence);
    }

    [Fact]
    public void ToolCommands_AfterReplay_Branch()
    {
        var replay = RecordRun();
        // Nudge берёт последнюю команду тика: команда инструмента (после реплея) перебивает реплей — ветвление
        var player = PlayAll(replay, (sim, tick) =>
        {
            if (tick == 40) sim.EnqueueRaw(typeof(Nudge), tick, BitConverter.GetBytes(0.26f));
        });
        Assert.Equal(41ul, player.Divergence!.Tick);
    }

    [Fact]
    public void RecordAndPlay_OnlyFromStart()
    {
        var sim = Wanderers();
        sim.Step();
        Assert.Throws<InvalidOperationException>(() => sim.Record(Info));
        Assert.Throws<InvalidOperationException>(() => sim.Play(RecordRun(ticks: 5)));
        var fresh = Wanderers();
        fresh.Record(Info);
        Assert.Throws<InvalidOperationException>(() => fresh.Record(Info));
    }

    [Fact]
    public void Diff_FirstCommandAndHash()
    {
        var a = RecordRun();
        var b = Replay.Parse(a.ToJson().Replace("[90, 1, [0, -1], [0, 3]]", "[90, 1, [0, -1], [0, 2]]"));
        // хеши b — от a; перезапись даёт настоящие
        var sim = Wanderers(b.CreateConfig());
        sim.Play(b);
        var recorder = sim.Record(Info);
        while (sim.Tick < b.Ticks) sim.Step();
        var d = ReplayDiff.Compare(a, recorder.ToReplay());
        Assert.Equal(90ul, d.FirstCommandTick);
        Assert.Contains("X=3", d.CommandDetail);
        Assert.Equal(91ul, d.FirstHashTick);
        Assert.Equal(90ul, d.LastCommonHash);
        Assert.Equal(["ecs"], d.HashParts);
        Assert.Empty(d.Header);

        var same = ReplayDiff.Compare(a, a);
        Assert.Null(same.FirstCommandTick);
        Assert.Null(same.FirstHashTick);
        Assert.Equal(201, same.CommonHashes);
    }

    /// <summary>
    /// Этап 3д: начало реплея — команды до тика и хеши не дальше него; без хешей — только команды. Ветка с началом играет
    /// те же тики бит в бит и дальше идёт без команд реплея.
    /// </summary>
    [Fact]
    public void Head_KeepsCommandsAndHashesBeforeTick()
    {
        var a = RecordRun();
        var head = a.Head(40);
        Assert.Equal(40ul, head.Ticks);
        Assert.All(head.Runs, r => Assert.True(r.End <= 40));
        Assert.Equal(20L, head.CommandCount);                                // Nudge на 20…39
        Assert.Equal(41, head.Hashes.Count);
        Assert.Equal(a.Hashes.Take(41).Select(h => h.Parts[0]), head.Hashes.Select(h => h.Parts[0]));
        var bare = a.Head(a.Ticks, hashes: false);
        Assert.Empty(bare.Hashes);
        Assert.Equal(0, bare.HashEvery);
        Assert.Equal(a.CommandCount, bare.CommandCount);
        var player = PlayAll(head);
        Assert.Null(player.Divergence);
        Assert.Equal(41, player.HashesChecked);
    }

    /// <summary>
    /// Этап 3д (бюджеты фич нашли): воспроизведение реплея не выделяет памяти на тик — команды тика выдаются по индексу, а
    /// не перечислителем через интерфейс (32 байта на тик).
    /// </summary>
    [Fact]
    public void Playback_SteadyTick_DoesNotAllocate()
    {
        // Команда каждый тик — чередуясь, чтобы отрезки не сливались: выдача идёт в каждом тике замера.
        var rec = Wanderers();
        var recorder = rec.Record(Info);
        for (int t = 0; t < 400; t++)
        {
            rec.Enqueue(new Nudge { X = t % 2 == 0 ? 0.25f : -0.25f });
            rec.Step();
        }
        var replay = recorder.ToReplay().Head(400, hashes: false);
        Assert.Equal(400L, replay.CommandCount);
        var sim = Wanderers(replay.CreateConfig());
        sim.Play(replay);
        while (sim.Tick < 100) sim.Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (sim.Tick < 400) sim.Step();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
