namespace Sword.Architecture.Tests;

/// <summary>Реальное решение против правил раздела 4 ARCHITECTURE_PLAN.md.</summary>
public sealed class SolutionTests
{
    private static readonly string Root = SolutionModel.FindRepoRoot();

    [Fact]
    public void Solution_FollowsArchitecture()
    {
        var projects = SolutionModel.Load(Root);
        Assert.NotEmpty(projects);

        var violations = ArchitectureRules.Check(projects);
        Assert.True(violations.Count == 0, "Нарушения архитектуры:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Headless_DoesNotReachClientAssemblies()
    {
        var projects = SolutionModel.Load(Root);
        var byName = projects.ToDictionary(p => p.Name);
        var headless = byName["Sword.Game.Headless"];

        var client = ArchitectureRules.Closure(headless, byName).Where(p => p.Side == Sides.Client).Select(p => p.Name).ToList();
        Assert.True(client.Count == 0, "Game.Headless транзитивно достаёт до клиентских сборок: " + string.Join(", ", client));
    }

    /// <summary>Каждый проект репозитория — в решении; заготовки запрещённых ссылок (tests/fixtures) — нет.</summary>
    [Fact]
    public void EveryProject_IsInSolution()
    {
        var inSolution = SolutionModel.SolutionProjects(Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string fixtures = Path.Combine(Root, "tests", "fixtures") + Path.DirectorySeparatorChar;

        var orphans = new[] { "engine", "game", "tools", "tests" }
            .Select(d => Path.Combine(Root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(f => !f.StartsWith(fixtures, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => !inSolution.Contains(f))
            .ToList();

        Assert.True(orphans.Count == 0, "Проекты вне SWORD.slnx: " + string.Join(", ", orphans));
    }

    [Fact]
    public void SolutionFolders_MatchLayers()
    {
        foreach (var p in SolutionModel.Load(Root))
            Assert.Equal(SolutionModel.LayerOf(p.RelativePath), p.Layer);
    }
}
