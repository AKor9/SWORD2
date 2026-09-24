using Sword.Engine.Content;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;

namespace Sword.Game.Tests;

/// <summary>
/// Эталоны SWORD2 (этап 3б; врагов — этап 4; тел — этап 5): реплеи прототипа, анимации, врагов и тел на рельефе в <c>tests/reference/replays</c> на
/// эталонном паке (все файлы папки). Та же сборка логики обязана повторить их хеши по участникам тик в тик — регрессия
/// называет тик и участника. Намеренное изменение
/// поведения — перезапись эталона: <c>Sword.Headless --replay файл --record файл</c> (те же команды, новые хеши) и запись
/// причины в md фичи. Эталонный пак вне git — категория Content (<c>verify.ps1 -SkipReference</c> её не запускает).
/// </summary>
[Trait("Category", "Content")]
public sealed class ReferenceReplaysTests
{
    private static IReadOnlyList<string> Names() =>
        [.. Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "reference", "replays"), "*" + Replay.Extension).Select(f => Path.GetFileName(f)).Order()];

    public static TheoryData<string> Files() => [.. Names()];

    private static readonly Lazy<(CharacterAssets Assets, string Content)> Reference = new(() =>
    {
        using var pak = PakSource.Open(TestContent.ReferencePak());
        return (CharacterAssets.Load(pak), pak.ContentHash);
    });

    [Theory]
    [MemberData(nameof(Files))]
    public void ReferenceReplay_RepeatsEveryHash(string file)
    {
        var replay = Replay.Load(Path.Combine(RepoRoot(), "tests", "reference", "replays", file));
        var (assets, content) = Reference.Value;
        Assert.Equal(content, replay.Content);
        Assert.Equal(1, replay.HashEvery);
        Assert.Equal(replay.Ticks + 1, (ulong)replay.Hashes.Count);

        var sim = SwordSimulation.Create(replay.CreateConfig(), assets);
        var player = sim.Play(replay);
        Assert.Empty(player.Warnings);
        while (sim.Tick < replay.Ticks) sim.Step();
        Assert.True(player.Divergence is null,
            $"{file}: {player.Divergence} — поведение изменилось. Намеренно — перезапишите эталон " +
            $"(Sword.Headless --replay tests/reference/replays/{file} --record tests/reference/replays/{file}) и запишите причину в md фичи");
        Assert.Equal(replay.Hashes.Count, player.HashesChecked);
        // Инварианты (этап 3г) на эталоне не нарушаются: реплей записан с тем же конфигом, где они включены.
        Assert.True(sim.Invariants.Total == 0, $"{file}: нарушения инвариантов:\n{string.Join("\n", sim.Invariants.Recent())}");
    }

    /// <summary>
    /// Условия на эталонном реплее (этап 3в): условие на события совпадает с командами реплея, условие на поле игрока —
    /// с прямым чтением мира тик за тиком; наблюдение даёт те же значения.
    /// </summary>
    [Fact]
    public void Conditions_OnReferenceReplay_MatchReplayAndWorld()
    {
        var replay = Replay.Load(Path.Combine(RepoRoot(), "tests", "reference", "replays", "prototype.swreplay"));
        var (assets, _) = Reference.Value;
        var names = new Sword.Game.Agent.SwordAgent.ArenaNames();

        // Шаг: удерживается в тиках, где у команды реплея Walk (номер тика команды t — tickId t + 1).
        var walkTicks = new List<ulong>();
        for (ulong t = 0; t < replay.Ticks; t++)
            if (replay.CommandsAt(t) is [var c] && c.Leaves[replay.CommandTypes[c.Type].Fields.ToList().FindIndex(f => f.StartsWith("Walk:"))] != 0)
                walkTicks.Add(t + 1);
        var walk = ReplayObservation.Run(SwordSimulation.Create(replay.CreateConfig(), assets), replay, names, new ReplayObserveOptions
        {
            To = replay.Ticks, Events = ["PlayerCommand"],
            Condition = System.Text.Json.Nodes.JsonNode.Parse("""{"event":"PlayerCommand","where":{"field":"Walk","equals":true}}"""),
        });
        Assert.True(walk.Player.Divergence is null);
        Assert.Equal(replay.CommandCount, walk.EventCounts["Event<PlayerCommand>"]);
        Assert.Equal((walkTicks[0], walkTicks[^1], walkTicks.Count), walk.Condition!.Value);

        // Поле игрока: первый tickId, когда X > 1, — тот же, что при прямом чтении Transform тик за тиком.
        var direct = SwordSimulation.Create(replay.CreateConfig(), assets);
        direct.Play(replay);
        var player = GameCore.Player(direct.World);
        ulong? firstDirect = null;
        var xs = new List<float>();
        while (direct.Tick < replay.Ticks)
        {
            direct.Step();
            float x = direct.World.Get<Contracts.Transform>(player).Position.X;
            xs.Add(x);
            if (x > 1 && firstDirect is null) firstDirect = direct.Tick;
        }
        var field = ReplayObservation.Run(SwordSimulation.Create(replay.CreateConfig(), assets), replay, names, new ReplayObserveOptions
        {
            To = replay.Ticks,
            Condition = System.Text.Json.Nodes.JsonNode.Parse("""{"all":[{"entity":"player"},{"field":"Transform.Position.X","greaterThan":1}]}"""),
            Watch = new System.Text.Json.Nodes.JsonObject { ["entities"] = new System.Text.Json.Nodes.JsonArray("player"), ["fields"] = new System.Text.Json.Nodes.JsonArray("Transform.Position.X") },
        });
        Assert.NotNull(firstDirect);
        Assert.Equal(firstDirect, field.Condition!.Value.First);
        Assert.Equal(xs.Count, (int)field.WatchRows);
        var watched = field.Lines.Where(l => l["values"] is not null).Select(l => l["values"]!["Transform.Position.X"]!.GetValue<float>()).ToList();
        Assert.Equal(xs, watched);
    }

    /// <summary>
    /// Точка 5г: <c>replay.observe</c> пишет вычисляемые поля тела (<c>Body.*</c>, <see cref="Sword.Game.Agent.BodyFields"/>) —
    /// те же числа, что вид тел <see cref="Contracts.IBodyView"/> тик за тиком; опечатка в поле тела — ошибка.
    /// </summary>
    [Fact]
    public void Observe_OnReferenceReplay_WritesBodyFields()
    {
        var replay = Replay.Load(Path.Combine(RepoRoot(), "tests", "reference", "replays", "prototype.swreplay"));
        var (assets, _) = Reference.Value;
        var names = new Sword.Game.Agent.SwordAgent.ArenaNames();

        var direct = SwordSimulation.Create(replay.CreateConfig(), assets);
        direct.Play(replay);
        var player = GameCore.Player(direct.World);
        var view = direct.World.View<Contracts.IBodyView>();
        int pelvis = Contracts.BodySegs.Body(Contracts.BodySegs.Pelvis);
        var ys = new List<float>();
        while (direct.Tick < replay.Ticks)
        {
            direct.Step();
            ys.Add(view.Body(direct.World.Get<Contracts.BodyRef>(player), pelvis).Position.Y);
        }

        ReplayObserveResult Observe(string field) => ReplayObservation.Run(SwordSimulation.Create(replay.CreateConfig(), assets), replay, names,
            new ReplayObserveOptions
            {
                To = replay.Ticks,
                Watch = new System.Text.Json.Nodes.JsonObject { ["entities"] = new System.Text.Json.Nodes.JsonArray("player"), ["fields"] = new System.Text.Json.Nodes.JsonArray(field) },
            }, fields: new Sword.Game.Agent.BodyFields());
        var observed = Observe("Body.pelvis.Position.Y");
        Assert.True(observed.Player.Divergence is null);
        Assert.Equal(ys.Count, (int)observed.WatchRows);
        var watched = observed.Lines.Where(l => l["values"] is not null).Select(l => (float)l["values"]!["Body.pelvis.Position.Y"]!.GetValue<double>()).ToList();
        Assert.Equal(ys, watched);
        Assert.Contains("нет сегмента", Assert.Throws<ArgumentException>(() => Observe("Body.tail.Position.Y")).Message);
    }

    [Fact]
    public void References_Exist_ForPrototypeAndAnimation()
    {
        var files = Names();
        Assert.Contains("prototype.swreplay", files);
        Assert.Contains("animation.swreplay", files);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория (SWORD.slnx)");
    }
}
