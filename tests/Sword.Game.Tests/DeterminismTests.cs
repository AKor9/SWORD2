using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>Детерминизм: хеш по участникам <c>ecs</c> и <c>animation</c> у двух прогонов с одними командами совпадает тик в тик.</summary>
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
        Assert.Equal(["animation", "ecs", "physics", "skin"], a.StateParts);   // skin — привязки скина мёртвых
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
    public void Game_RecordThenReplay_SameHashes_CorruptionNamesTickAndParts()
    {
        var a = TestAssets.Simulation();
        var recorder = a.Record(new ReplayInfo("test", "tests", "none"));
        for (int t = 0; t < 400; t++)
        {
            a.Enqueue(CommandAt(t));
            a.Step();
        }
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        Assert.Equal(["animation", "ecs", "physics", "skin"], replay.Parts);
        Assert.Equal(5, replay.Runs.Count);   // сценарий — пять отрезков одинаковых команд

        var b = TestAssets.Simulation(replay.CreateConfig());
        var player = b.Play(replay);
        while (b.Tick < replay.Ticks) b.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(401, player.HashesChecked);
        Assert.Equal(a.HashState().Total, b.HashState().Total);

        // испорченный реплей: в тике 200 шаг вместо бега (отрезок 150…219 — шагом вбок; делаем бег) — тик и участники
        var corrupted = Replay.Parse(replay.ToJson().Replace("[150, 70, [0, 1, 0, true,", "[150, 70, [0, 1, 0, false,"));
        var c = TestAssets.Simulation(corrupted.CreateConfig());
        var broken = c.Play(corrupted);
        while (c.Tick < corrupted.Ticks) c.Step();
        Assert.Equal(151ul, broken.Divergence!.Tick);
        Assert.Equal(150ul, broken.Divergence.LastMatch);
        Assert.Contains(broken.Divergence.Parts, p => p.Part == "ecs");
    }

    [Fact]
    public void AnimationPart_SeesPose_NotOnlyEntities()
    {
        var (a, pool) = TestAssets.WithResource<PosePool>(ConfigSet.Empty());
        var b = TestAssets.Simulation(ConfigSet.Empty("modules.locomotion.enabled=false"));
        a.Step();
        b.Step();
        // без модуля локомоции в пуле одна поза (бойца) — и она поза привязки
        Assert.NotEqual(a.HashState().Parts["animation"], b.HashState().Parts["animation"]);
        Assert.Equal(3, pool.Count);
        var pose = pool.HandleAt(0);
        Assert.Equal("0v1", pose.ToString());
        int pelvis = pool.Skeleton.JointNames.IndexOf("pelvis");
        Assert.Equal(0.95, pool.Model(pose)[pelvis].M42, 3);
    }

}
