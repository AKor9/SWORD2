using System.Xml.Linq;

namespace Sword.Architecture.Tests;

/// <summary>Проект, каким его видят правила: слой, сторона, прямые ссылки на проекты и пакеты.</summary>
/// <param name="Name">Имя проекта (имя .csproj без расширения).</param>
/// <param name="RelativePath">Путь .csproj от корня репозитория, через «/».</param>
/// <param name="Layer">Engine | Game | Tool | Test — по папке или из свойства SwordLayer.</param>
/// <param name="Side">Neutral | Simulation | Client; null — не задана.</param>
/// <param name="IsExe">OutputType = Exe.</param>
public sealed record ProjectNode(
    string Name,
    string RelativePath,
    string Layer,
    string? Side,
    bool IsExe,
    IReadOnlyList<string> References,
    IReadOnlyList<PackageRef> Packages)
{
    public bool IsTest => Layer == Layers.Test;

    /// <summary>Модуль игры: всё, что лежит в game/modules/.</summary>
    public bool IsModule => RelativePath.StartsWith("game/modules/", StringComparison.Ordinal);

    /// <summary>Клиентская часть модуля (Sword.Game.Cutting.Client и т. п.).</summary>
    public bool IsClientModule => IsModule && Name.EndsWith(".Client", StringComparison.Ordinal);
}

/// <summary>Ссылка на пакет NuGet; <see cref="PrivateAssets"/> — как в PackageReference.</summary>
public sealed record PackageRef(string Id, string? PrivateAssets);

public static class Layers
{
    public const string Engine = "Engine", Game = "Game", Tool = "Tool", Test = "Test";
}

public static class Sides
{
    public const string Neutral = "Neutral", Simulation = "Simulation", Client = "Client";
}

/// <summary>Граф проектов решения, прочитанный из SWORD.slnx и .csproj без MSBuild.</summary>
public static class SolutionModel
{
    public const string SolutionFile = "SWORD.slnx";

    /// <summary>Корень репозитория — ближайшая сверху папка с <see cref="SolutionFile"/>.</summary>
    public static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile))) return dir.FullName;
        throw new InvalidOperationException($"не найден {SolutionFile} выше {AppContext.BaseDirectory}");
    }

    /// <summary>Пути проектов решения от корня, через «/».</summary>
    public static IReadOnlyList<string> SolutionProjects(string root) =>
        XDocument.Load(Path.Combine(root, SolutionFile))
            .Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .OfType<string>()
            .Select(p => p.Replace('\\', '/'))
            .ToList();

    public static IReadOnlyList<ProjectNode> Load(string root) =>
        SolutionProjects(root).Select(p => LoadProject(root, p)).ToList();

    /// <summary>Проект по пути от корня. Условия в .csproj не вычисляются: ссылка под условием считается ссылкой.</summary>
    public static ProjectNode LoadProject(string root, string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        var doc = XDocument.Load(full);

        string? Property(string name) =>
            doc.Descendants(name).Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0);

        var references = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToList();

        var packages = doc.Descendants("PackageReference")
            .Select(e => new PackageRef(
                (string?)e.Attribute("Include") ?? "",
                (string?)e.Attribute("PrivateAssets") ?? e.Element("PrivateAssets")?.Value))
            .ToList();

        string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return new ProjectNode(
            Name: Path.GetFileNameWithoutExtension(full),
            RelativePath: relative,
            Layer: Property("SwordLayer") ?? LayerOf(relative),
            Side: Property("SwordSide"),
            IsExe: string.Equals(Property("OutputType"), "Exe", StringComparison.OrdinalIgnoreCase),
            References: references,
            Packages: packages);
    }

    /// <summary>Слой по первой папке пути — как в Directory.Build.props.</summary>
    public static string LayerOf(string relativePath) => relativePath.Split('/')[0] switch
    {
        "engine" => Layers.Engine,
        "game" => Layers.Game,
        "tools" => Layers.Tool,
        "tests" => Layers.Test,
        var other => $"?{other}",
    };
}
