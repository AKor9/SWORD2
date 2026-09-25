using System.Text.Json.Nodes;
using Sword.Bake.Cli;

namespace Sword.Bake.Tests;

/// <summary>
/// Агентский вид пекарни: <c>sword-bake status --json</c> и <c>build --json</c> — один JSON-объект одной строкой в stdout
/// (ассеты с актуальностью и причинами, ошибки, записан ли пак). Итог собирает <see cref="CliJson"/>; процесс — sword-bake.exe.
/// </summary>
public sealed class CliJsonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sword-bake-tests", Guid.NewGuid().ToString("N"));
    private readonly string _project, _sources;

    public CliJsonTests()
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

    private string Source(string name, string text)
    {
        string path = Path.Combine(_sources, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static JsonNode Single(string stdout)
    {
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));   // одна строка — один объект
        return JsonNode.Parse(stdout)!;
    }

    private static JsonObject Asset(JsonNode result, string id) =>
        result["assets"]!.AsArray().Single(a => (string)a!["id"]! == id)!.AsObject();

    [Fact]
    public void BuildJson_FailedAssetKeepsPak_ThenBuiltAndStatusUpToDate()
    {
        var project = BakeProject.Create(_project, "Json");
        project.AddAsset("data/good", "file.copy", new Dictionary<string, string> { ["from"] = Source("good.txt", "good") });
        project.AddAsset("data/missing", "file.copy", new Dictionary<string, string> { ["from"] = Path.Combine(_sources, "nope.txt") });
        var builder = new Builder(project);

        // ошибка ассета: пак не записан, у ассета — ошибка с путём исходника
        var events = new BuildEvents();
        var options = new BuildOptions { Jobs = 2 };
        var s = builder.Build(null, options, events.On);
        var failed = CliJson.Build(project, builder, events, s, options, [], builder.CheckPak(), 1);
        Assert.False((bool)failed["ok"]!);
        Assert.False((bool)failed["pakWritten"]!);
        Assert.Equal("failed", (string)Asset(failed, "data/missing")["result"]!);
        Assert.Contains("nope.txt", (string)Asset(failed, "data/missing")["error"]!);
        Assert.NotNull(Asset(failed, "data/missing")["log"]);
        Assert.Equal("built", (string)Asset(failed, "data/good")["result"]!);
        Assert.Equal(1, (int)failed["summary"]!["failed"]!);
        Assert.Equal("missing", (string)failed["pak"]!["status"]!);
        Assert.Contains(failed["messages"]!.AsArray(), m => ((string)m!["text"]!).Contains("прежний пак не тронут", StringComparison.Ordinal));

        // исходник появился — всё собрано, пак записан и актуален
        Source("nope.txt", "here");
        events = new BuildEvents();
        s = builder.Build(null, options, events.On);
        var built = CliJson.Build(project, builder, events, s, options, [], builder.CheckPak(), 0);
        Assert.True((bool)built["ok"]!);
        Assert.True((bool)built["pakWritten"]!);
        Assert.False((bool)built["pakUnchanged"]!);
        Assert.Equal("built", (string)Asset(built, "data/missing")["result"]!);
        Assert.Equal("built", (string)Asset(built, "data/good")["result"]!);   // прежняя сборка пак не записала
        Assert.Equal("пак ещё не собран", (string)Asset(built, "data/good")["reason"]!);
        Assert.Equal("upToDate", (string)built["pak"]!["status"]!);
        Assert.Equal(2, (int)built["pakFiles"]!);

        var checks = builder.CheckAll();
        var status = CliJson.Status(project, builder, project.Assets, checks, builder.CheckPak(checks), 0);
        Assert.True((bool)status["ok"]!);
        Assert.Equal(2, (int)status["counts"]!["upToDate"]!);
        Assert.All(status["assets"]!.AsArray(), a => Assert.Equal("upToDate", (string)a!["status"]!));
        Assert.Equal("пак актуален", (string)status["pak"]!["reason"]!);
    }

    [Fact]
    public void StatusJson_ReasonsOfStaleAssetsAndProjectProblems()
    {
        var project = BakeProject.Create(_project, "Json");
        var asset = project.AddAsset("data/a", "file.copy", new Dictionary<string, string> { ["from"] = Source("a.txt", "a") });
        var builder = new Builder(project);
        Assert.True(builder.Build(null, new BuildOptions { Jobs = 1 }, _ => { }).Ok);

        File.WriteAllText(Path.Combine(_sources, "a.txt"), "changed");
        File.WriteAllText(Path.Combine(_project, "broken" + AssetManifest.Extension), "{ not json");
        project.Reload();
        builder = new Builder(project);
        var checks = builder.CheckAll();
        var status = CliJson.Status(project, builder, project.Assets, checks, builder.CheckPak(checks), 2);

        Assert.False((bool)status["ok"]!);
        var a = Asset(status, asset.Id);
        Assert.Equal("dirty", (string)a["status"]!);
        Assert.Contains("изменён", (string)a["reason"]!);
        Assert.Equal("stale", (string)status["pak"]!["status"]!);
        Assert.Contains(status["pak"]!["details"]!.AsArray(), d => ((string)d!).StartsWith("data/a:", StringComparison.Ordinal));
        Assert.Single(status["project"]!["broken"]!.AsArray());
    }

    [Fact]
    public void CliProcess_InitBuildStatusJson_OneObjectOnStdout()
    {
        var (exit, stdout, stderr) = TestPaths.RunCli("init", _project, "--name", "Cli");
        Assert.True(exit == 0, stderr);
        (exit, _, stderr) = TestPaths.RunCli("--project", _project, "add", "data/greeting", "file.copy", "from=" + Source("greeting.txt", "привет"));
        Assert.True(exit == 0, stderr);

        (exit, stdout, _) = TestPaths.RunCli("--project", _project, "build", "--json");
        var build = Single(stdout);
        Assert.Equal(0, exit);
        Assert.Equal("build", (string)build["command"]!);
        Assert.True((bool)build["pakWritten"]!);
        Assert.Equal("built", (string)Asset(build, "data/greeting")["result"]!);
        Assert.Equal(Path.Combine(_project, "Cli.swpak"), (string)build["pak"]!["path"]!);

        (exit, stdout, _) = TestPaths.RunCli("--project", _project, "status", "--json");
        var status = Single(stdout);
        Assert.Equal(0, exit);
        Assert.True((bool)status["ok"]!);
        Assert.Equal("upToDate", (string)Asset(status, "data/greeting")["status"]!);

        // ошибка до итога — тоже один объект, код выхода 1; --json у других команд — ошибка
        (exit, stdout, _) = TestPaths.RunCli("--project", Path.Combine(_root, "nowhere"), "status", "--json");
        var error = Single(stdout);
        Assert.Equal(1, exit);
        Assert.False((bool)error["ok"]!);
        Assert.Contains("нет проекта пекарни", (string)error["error"]!);
        (exit, stdout, _) = TestPaths.RunCli("--project", _project, "list", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("только у status и build", (string)Single(stdout)["error"]!);
    }
}
