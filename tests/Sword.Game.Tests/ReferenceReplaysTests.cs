using Sword.Engine.Content;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;

namespace Sword.Game.Tests;

/// <summary>
/// Эталонные реплеи <c>tests/reference/replays</c> на паке игры: та же логика повторяет их хеши по участникам тик в тик,
/// регрессия называет тик и участника. Намеренное изменение — перезапись: <c>Sword.Headless --replay файл --record файл</c>.
/// Категория Content (<c>verify.ps1 -SkipReference</c> её не запускает).
/// </summary>
[Trait("Category", "Content")]
public sealed class ReferenceReplaysTests
{
    private static IReadOnlyList<string> Names() =>
        [.. Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "reference", "replays"), "*" + Replay.Extension).Select(f => Path.GetFileName(f)).Order()];

    public static TheoryData<string> Files() => [.. Names()];

    private static readonly Lazy<(CharacterAssets Assets, string Content)> Reference = new(() =>
    {
        using var pak = PakSource.Open(TestContent.GamePak());
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
        // Инварианты на эталоне не нарушаются: реплей записан с тем же конфигом, где они включены.
        Assert.True(sim.Invariants.Total == 0, $"{file}: нарушения инвариантов:\n{string.Join("\n", sim.Invariants.Recent())}");
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
