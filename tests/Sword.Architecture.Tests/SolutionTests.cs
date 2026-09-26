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

    /// <summary>
    /// Собранные сборки продукта ссылаются только на разрешённое. Компилятор пишет в метаданные ссылку на каждую сборку,
    /// чьи типы код использует, в том числе доступную лишь транзитивно, — такую ссылку .csproj не показывает.
    /// Сборки берутся той же конфигурации, что и тесты; решение должно быть собрано (dotnet test SWORD.slnx собирает).
    /// </summary>
    [Fact]
    public void CompiledAssemblies_FollowArchitecture()
    {
        var projects = SolutionModel.Load(Root);
        string configuration = SolutionModel.TestConfiguration();
        var problems = new List<string>();
        int checkedCount = 0;
        foreach (var p in projects.Where(p => !p.IsTest))
        {
            string? dll = SolutionModel.BuiltAssembly(Root, p, configuration);
            if (dll is null)
            {
                problems.Add($"{p.Name}: нет собранной {p.Assembly}.dll — соберите решение ({configuration})");
                continue;
            }
            problems.AddRange(ArchitectureRules.CheckCompiled(p, CompiledReferences(dll), projects).Select(v => v.ToString()));
            checkedCount++;
        }
        Assert.True(problems.Count == 0, $"Проверено сборок: {checkedCount}. Нарушения:\n" + string.Join("\n", problems));
    }

    private static List<string> CompiledReferences(string dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
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

    /// <summary>
    /// Сборка без DevTools (<c>-p:WithoutDevTools=true</c>, раздел 4): каждая ссылка exe на сборку DevTools — под условием
    /// <c>'$(WithoutDevTools)' != 'true'</c> (других ссылающихся нет — правило <c>devtools-exe-only</c>). Что такая сборка
    /// собирается и DevTools в ней нет — шаг <c>without-devtools</c> verify.ps1.
    /// </summary>
    [Fact]
    public void DevToolsReferences_AreRemovedWithoutDevTools()
    {
        var problems = new List<string>();
        int conditioned = 0;
        foreach (var p in SolutionModel.Load(Root).Where(p => p.IsExe))
        {
            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(Root, p.RelativePath));
            foreach (var reference in doc.Descendants("ProjectReference"))
            {
                string name = Path.GetFileNameWithoutExtension(((string?)reference.Attribute("Include") ?? "").Replace('\\', '/'));
                if (!ArchitectureRules.DevToolsFamily.Contains(name)) continue;
                string condition = string.Concat(((string?)reference.Parent?.Attribute("Condition") ?? "").Where(c => !char.IsWhiteSpace(c)));
                if (condition == "'$(WithoutDevTools)'!='true'") conditioned++;
                else problems.Add($"{p.Name} → {name}: ссылка не под условием '$(WithoutDevTools)' != 'true'");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(conditioned > 0, "ни один exe не ссылается на DevTools — проверка пуста (Sword.Game.App должен)");
    }

    /// <summary>
    /// Сборка без модуля (<c>-p:Without&lt;Модуль&gt;=true</c>, раздел 7): каждая ссылка продукта на модуль (<c>game/modules</c> — часть симуляции
    /// или клиентская) — под условием <c>'$(Without&lt;Модуль&gt;)' != 'true'</c>, где Модуль — имя без <c>Sword.Game.</c> и <c>.Client</c>
    /// (Bootstrap — модули симуляции, Sword.exe — клиентские). Что такая сборка собирается и стартует — шаг <c>without-modules</c> verify.ps1.
    /// </summary>
    [Fact]
    public void ModuleReferences_AreRemovedWithoutModule()
    {
        var projects = SolutionModel.Load(Root);
        var modules = projects.Where(p => p.IsModule).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var conditioned = new List<string>();
        foreach (var p in projects.Where(p => !p.IsTest && !p.IsModule))
        {
            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(Root, p.RelativePath));
            foreach (var reference in doc.Descendants("ProjectReference"))
            {
                string name = Path.GetFileNameWithoutExtension(((string?)reference.Attribute("Include") ?? "").Replace('\\', '/'));
                if (!modules.Contains(name)) continue;
                string module = name["Sword.Game.".Length..];
                if (module.EndsWith(".Client", StringComparison.Ordinal)) module = module[..^".Client".Length];
                string condition = string.Concat(((string?)reference.Parent?.Attribute("Condition") ?? "").Where(c => !char.IsWhiteSpace(c)));
                if (condition == $"'$(Without{module})'!='true'") conditioned.Add($"{p.Name} → {name}");
                else problems.Add($"{p.Name} → {name}: ссылка не под условием '$(Without{module})' != 'true'");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Contains("Sword.Game.App → Sword.Game.Fire.Client", conditioned);
        Assert.Contains("Sword.Game.Bootstrap → Sword.Game.Locomotion", conditioned);
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
