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
/// зелёный (без видов — их снимает App в verify.ps1; класс на фичу — <see cref="FeatureVerifyTests"/>), а развилка эталона прототипа
/// расходится на ожидаемом тике.
/// </summary>
public sealed class FeatureManifestsTests
{
    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория (SWORD.slnx)");
    }

    internal static IReadOnlyList<FeatureManifest> All() =>
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

    /// <summary>
    /// Фичи в работе — README есть, манифест ещё не написан (этап идёт точками). Сейчас таких нет: «Рэгдолл» (README — 7ж) получил манифест
    /// <c>ragdoll</c> на 7з (сценарии, инварианты, проверки; метрики, виды и бюджеты — 7л). Приёмки у фичи нет, пока нет манифеста.
    /// </summary>
    private static readonly string[] InProgress = [];

    [Fact]
    public void EveryFeature_HasManifest_ChecklistMatchesReadme()
    {
        var manifests = All();
        var features = Directory.GetDirectories(Path.Combine(RepoRoot(), "game", "Sword.Game", "Features")).Select(d => Path.GetFileName(d)).Order().ToList();
        foreach (string feature in features)
        {
            bool manifest = File.Exists(Path.Combine(RepoRoot(), "game", "Sword.Game", "Features", feature, FeatureManifest.FileName));
            // Фича в работе (этап идёт точками) — README есть, манифеста ещё нет; появится он — строку убрать.
            if (InProgress.Contains(feature))
            {
                Assert.False(manifest, $"у фичи {feature} уже есть манифест — уберите её из «в работе»");
                continue;
            }
            Assert.True(manifest, $"у фичи {feature} нет {FeatureManifest.FileName} (раздел 12 плана: без манифеста фича не принимается)");
        }
        Assert.Equal(["animation", "bodies", "combat", "control", "determinism", "enemies", "movement", "profiler", "ragdoll", "tools"],
            manifests.Select(m => m.Id));
        foreach (var m in manifests)
        {
            Assert.True(File.Exists(m.Doc), $"{m.Id}: нет {m.Doc}");
            Assert.Equal(ReadmeChecklist(m.Doc), m.Checklist);
        }
        // Доводка 6ж: приёмка каждой фичи — свой класс-наследник FeatureVerifyTests (классы xUnit идут параллельно): новая фича без
        // него осталась бы без проверки feature.verify.
        var verified = typeof(FeatureVerifyTests).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(FeatureVerifyTests)) && !t.IsAbstract)
            .Select(t => ((FeatureVerifyTests)Activator.CreateInstance(t)!).Id).Order().ToList();
        Assert.Equal(manifests.Select(m => m.Id).Order(), verified);
    }

    /// <summary>Ассеты и отпечаток эталонного пака — один раз на процесс (классы приёмки фич идут параллельно и делят их: только чтение).</summary>
    internal static readonly Lazy<(CharacterAssets Assets, string Content)> Reference = new(() =>
    {
        using var pak = PakSource.Open(TestContent.ReferencePak());
        return (CharacterAssets.Load(pak), pak.ContentHash);
    });

    internal static Simulation Build(ConfigSet config)
    {
        var sim = SwordSimulation.Create(config, Reference.Value.Assets);
        config.SkipClientSections();
        config.Complete();
        return sim;
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

/// <summary>
/// Приёмка фичи на эталонном паке: <c>feature.verify</c> зелёный (без видов — их снимает App в verify.ps1). Класс на фичу (доводка 6ж):
/// xUnit гонит тесты одного класса подряд, а классы — параллельно; одной теорией на все фичи приёмка шла цепочкой (~140 с: combat 47,
/// enemies 43, bodies 21). Параллельные прогоны не мешают друг другу: у каждой фичи свой каталог отчёта, пак и ассеты — общие и только
/// читаются (<see cref="FeatureManifestsTests.Reference"/>), профайлер бюджета — свой у замера. Что каждая фича манифеста здесь есть —
/// <see cref="FeatureManifestsTests.EveryFeature_HasManifest_ChecklistMatchesReadme"/>.
/// </summary>
public abstract class FeatureVerifyTests(string id)
{
    public string Id { get; } = id;

    [Fact]
    [Trait("Category", "Content")]
    public void FeatureVerify_Green_OnReferencePak()
    {
        var manifest = FeatureManifestsTests.All().Single(m => m.Id == Id);
        string dir = Path.Combine(Path.GetTempPath(), "sword-feature-verify", Id);
        var report = FeatureVerifier.Verify(manifest, new FeatureEnvironment
        {
            RepoRoot = FeatureManifestsTests.RepoRoot(), Build = FeatureManifestsTests.Build,
            Info = new ReplayInfo("1.0.0+test", "tests", FeatureManifestsTests.Reference.Value.Content),
            Names = new Agent.SwordAgent.ArenaNames(), Fields = new Agent.BodyFields(), Optimized = false,
        }, new FeatureVerifyOptions { OutputDirectory = dir, Views = false });
        Assert.True(report.Ok, $"{Id}: {string.Join("\n", report.Failures)}");
    }
}

public sealed class AnimationFeatureVerifyTests() : FeatureVerifyTests("animation");

public sealed class BodiesFeatureVerifyTests() : FeatureVerifyTests("bodies");

public sealed class CombatFeatureVerifyTests() : FeatureVerifyTests("combat");

public sealed class ControlFeatureVerifyTests() : FeatureVerifyTests("control");

public sealed class DeterminismFeatureVerifyTests() : FeatureVerifyTests("determinism");

public sealed class EnemiesFeatureVerifyTests() : FeatureVerifyTests("enemies");

public sealed class MovementFeatureVerifyTests() : FeatureVerifyTests("movement");

public sealed class ProfilerFeatureVerifyTests() : FeatureVerifyTests("profiler");

public sealed class RagdollFeatureVerifyTests() : FeatureVerifyTests("ragdoll");

public sealed class ToolsFeatureVerifyTests() : FeatureVerifyTests("tools");
