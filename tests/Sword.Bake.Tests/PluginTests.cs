using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Content;

namespace Sword.Bake.Tests;

/// <summary>
/// Несколько плагинов по порядку списка plugins (как Sword.Bake.Engine, затем Sword.Bake.Game): плагин B наследует тип
/// плагина A и читает параметры его единицы, а сборка A в процессе одна — та, что загрузил загрузчик плагинов по списку.
/// Тестовые плагины — tests/Sword.Bake.Tests.PluginA и PluginB.
/// </summary>
public sealed class PluginTests : IDisposable
{
    private const string A = "Sword.Bake.Tests.PluginA", B = "Sword.Bake.Tests.PluginB";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project, _sources;

    public PluginTests()
    {
        _project = Path.Combine(_root, "project");
        _sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_sources);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Проект с плагинами в заданном порядке (полные пути: ${config} хоста тестов — конфигурация testhost, а не решения).</summary>
    private void CreateProject(params string[] plugins)
    {
        BakeProject.Create(_project, "Plugins");
        string file = Path.Combine(_project, BakeProject.FileName);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["plugins"] = new JsonArray([.. plugins.Select(p => (JsonNode)TestPaths.Plugin(p))]);
        TextFiles.WriteJson(file, json);
    }

    private string Source(string name, string text)
    {
        string path = Path.Combine(_sources, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void SecondPluginUsesFirstWithoutSecondCopy()
    {
        CreateProject(A, B);
        var project = BakeProject.Open(_project);

        Assert.Empty(project.Problems);
        Assert.Equal([A, B], project.Processors.Plugins.Select(p => p.Assembly.GetName().Name));
        var skeleton = project.Processors.Find("testa.skeleton")!;
        var weapon = project.Processors.Find("testb.weapon")!;
        Assert.NotNull(skeleton);
        Assert.NotNull(weapon);

        // сборка A одна — из теневой копии загрузчика; тип параметров A, каким его видит B, — тот же тип
        var loadedA = Assert.Single(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == A);
        Assert.Same(loadedA, skeleton.GetType().Assembly);
        Assert.Same(project.Processors.Plugins[0].Assembly, loadedA);
        var seenByB = (Type)weapon.GetType().GetProperty("SkeletonParamsType")!.GetValue(null)!;
        Assert.Same(skeleton.ParamsType, seenByB);
        Assert.Same(loadedA, weapon.GetType().BaseType!.Assembly);   // основа обработчика B — из A

        // код A входит в отпечаток обработчика B: правка A пересобирает ассеты B
        var builder = new Builder(project);
        Assert.Contains(A + ":", builder.Fingerprint(weapon));
        Assert.DoesNotContain("Sword.Bake:", builder.Fingerprint(weapon));

        // ссылка B → единица A (ctx.Ref<SkeletonParams>) и сборка через оба плагина
        project.AddAsset("skeletons/manny", "testa.skeleton", new Dictionary<string, string> { ["joints"] = Source("joints.txt", "pelvis,hand_r") });
        var katana = project.AddAsset("weapons/katana", "testb.weapon");
        var parameters = katana.ReadParams(weapon);
        weapon.ParamsType.GetProperty("Skeleton")!.SetValue(parameters, "skeletons/manny");
        Assert.True(ManifestWriter.SaveParams(katana, weapon, parameters));
        project.Reload();

        var s = builder.Build(null, new BuildOptions { Jobs = 2 }, _ => { });
        Assert.True(s.Ok);
        Assert.Equal(2, s.Built);
        using var pak = PakSource.Open(project.PakPath);
        Assert.Equal("skeleton:pelvis,hand_r", Encoding.UTF8.GetString(pak.ReadAllBytes("skeletons/manny.skel")));
        Assert.Equal("skeleton:pelvis,hand_r|grip:hand_r", Encoding.UTF8.GetString(pak.ReadAllBytes("weapons/katana.weapon")));
        // исходник скелета — вход и оружия: B регистрирует его сам
        Assert.Contains(PakBuildInfo.Read(pak)!.Assets["weapons/katana"].Inputs, i => i.Path.EndsWith("joints.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void CliLoadsPluginsInOrder_WrongOrderIsAProjectProblem()
    {
        // отдельный процесс: в процессе тестов A может быть уже загружен другим тестом
        CreateProject(A, B);
        var (exit, stdout, _) = TestPaths.RunCli("--project", _project, "status", "--json");
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal(2, exit);   // пак ещё не собран
        Assert.Empty(json["project"]!["problems"]!.AsArray());
        Assert.Equal(["file.copy", "testa.skeleton", "testb.weapon"], json["project"]!["processors"]!.AsArray().Select(p => (string)p!));
        Assert.All(json["project"]!["plugins"]!.AsArray(), p => Assert.True((bool)p!["loaded"]!));

        // B раньше A: основа обработчика B не находится — B без обработчика, причина — в проблемах проекта
        Directory.Delete(_project, true);
        Directory.CreateDirectory(_project);
        CreateProject(B, A);
        (exit, stdout, _) = TestPaths.RunCli("--project", _project, "status", "--json");
        json = JsonNode.Parse(stdout)!;
        Assert.Equal(2, exit);
        var problems = json["project"]!["problems"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.Contains(problems, p => p.Contains(B + ".dll", StringComparison.Ordinal) && p.Contains(A, StringComparison.Ordinal));
        Assert.DoesNotContain("testb.weapon", json["project"]!["processors"]!.AsArray().Select(p => (string)p!));
    }
}
