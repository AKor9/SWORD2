using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sword.Engine.Content;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;

namespace Sword.Game.Tests;

/// <summary>
/// Манифесты фич игры (этап 3д, раздел 12 плана): у каждой фичи в <c>Features</c> есть <c>feature.json</c>, он разбирается,
/// его чек-лист — те же пункты, что «Что проверить в игре» в md фичи; на эталонном паке <c>feature.verify</c> каждой фичи
/// зелёный (без видов — их снимает App в verify.ps1), а развилка эталона прототипа расходится на ожидаемом тике.
/// </summary>
public sealed class FeatureManifestsTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория (SWORD.slnx)");
    }

    private static IReadOnlyList<FeatureManifest> All() =>
        FeatureManifest.Find([Path.Combine(RepoRoot(), "game"), Path.Combine(RepoRoot(), "engine")]);

    /// <summary>Нумерованные пункты раздела «Что проверить в игре» md: строки продолжения — через пробел.</summary>
    private static List<string> ReadmeChecklist(string path)
    {
        var items = new List<string>();
        bool inside = false;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = line == "## Что проверить в игре";
                continue;
            }
            if (!inside) continue;
            var m = Regex.Match(line, @"^\d+\. (.*)$");
            if (m.Success) items.Add(m.Groups[1].Value);
            else if (items.Count > 0 && line.StartsWith("   ", StringComparison.Ordinal) && line.Trim().Length > 0) items[^1] += " " + line.Trim();
            else if (line.Trim().Length > 0 && items.Count > 0) break;   // абзац после списка — не пункт
        }
        return items;
    }

    [Fact]
    public void EveryFeature_HasManifest_ChecklistMatchesReadme()
    {
        var manifests = All();
        var features = Directory.GetDirectories(Path.Combine(RepoRoot(), "game", "Sword.Game", "Features")).Select(d => Path.GetFileName(d)).Order().ToList();
        foreach (string feature in features)
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "game", "Sword.Game", "Features", feature, FeatureManifest.FileName)),
                $"у фичи {feature} нет {FeatureManifest.FileName} (раздел 12 плана: без манифеста фича не принимается)");
        Assert.Equal(["animation", "bodies", "control", "determinism", "enemies", "movement", "tools"], manifests.Select(m => m.Id));
        foreach (var m in manifests)
        {
            Assert.True(File.Exists(m.Doc), $"{m.Id}: нет {m.Doc}");
            Assert.Equal(ReadmeChecklist(m.Doc), m.Checklist);
        }
    }

    private static readonly Lazy<(CharacterAssets Assets, string Content)> Reference = new(() =>
    {
        using var pak = PakSource.Open(ReferenceTraceTests.ReferencePak());
        return (CharacterAssets.Load(pak), pak.ContentHash);
    });

    private static Simulation Build(ConfigSet config)
    {
        var sim = SwordSimulation.Create(config, Reference.Value.Assets);
        config.SkipClientSections();
        config.Complete();
        return sim;
    }

    public static TheoryData<string> Ids() => [.. All().Select(m => m.Id)];

    [Theory]
    [Trait("Category", "Content")]
    [MemberData(nameof(Ids))]
    public void FeatureVerify_Green_OnReferencePak(string id)
    {
        var manifest = All().Single(m => m.Id == id);
        string dir = Path.Combine(Path.GetTempPath(), "sword-feature-verify", id);
        var report = FeatureVerifier.Verify(manifest, new FeatureEnvironment
        {
            RepoRoot = RepoRoot(), Build = Build, Info = new ReplayInfo("1.0.0+test", "tests", Reference.Value.Content),
            Names = new Agent.SwordAgent.ArenaNames(), Fields = new Agent.BodyFields(), Optimized = false,
        }, new FeatureVerifyOptions { OutputDirectory = dir, Views = false });
        Assert.True(report.Ok, $"{id}: {string.Join("\n", report.Failures)}");
    }

    /// <summary>
    /// Приёмка 3д: развилка эталона прототипа на тике 300. Другая команда игрока в тике 300 меняет состояние ровно после
    /// 301 тика (до того — база бит в бит); ручка конфига, которую симуляция читает (модуль выключен), — с тика 0; ручка,
    /// которую симуляция не читает (потолок тиков за кадр — клиентский), не меняет ничего.
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void Fork_PrototypeReference_DivergesOnExpectedTick()
    {
        var replay = Replay.Load(Path.Combine(RepoRoot(), "tests", "reference", "replays", "prototype.swreplay"));
        var back = new ScenarioCommand("PlayerCommand", (JsonObject)JsonNode.Parse("""{"Move":{"X":-1,"Y":0}}""")!, 300, 301);
        var branches = Experiments.Fork(replay, 300, replay.Ticks,
        [
            new ForkVariant("back", [], [back], Cut: false),
            new ForkVariant("no-locomotion", ["modules.locomotion.enabled=false"], [], Cut: false),
            new ForkVariant("frame-cap", ["sim.maxTicksPerFrame=4"], [], Cut: false),
        ], [new ForkMeasure("player", "Transform.Position.X")], Build, new ReplayInfo("1.0.0+test", "tests", Reference.Value.Content),
            new Agent.SwordAgent.ArenaNames());

        Assert.All(branches, b => Assert.Null(b.Error));
        Assert.Null(branches[0].PlaybackDivergence);
        var back1 = branches[1].VsBase!;
        Assert.Equal((300ul, 301ul, 300ul), (back1.FirstCommandTick, back1.FirstHashTick, back1.LastCommonHash));
        Assert.Contains("ecs", back1.HashParts);
        Assert.NotEqual(branches[0].Measures["player:Transform.Position.X"], branches[1].Measures["player:Transform.Position.X"]);
        Assert.Equal(0ul, branches[2].VsBase!.FirstHashTick);
        Assert.Contains(branches[2].VsBase!.Header, h => h.Contains("modules.locomotion"));
        Assert.Null(branches[3].VsBase!.FirstHashTick);
        Assert.Contains(branches[3].VsBase!.Header, h => h.Contains("maxTicksPerFrame"));
        Assert.Equal(branches[0].Recorded!.Hashes.Count, branches[3].VsBase!.CommonHashes);
    }
}
