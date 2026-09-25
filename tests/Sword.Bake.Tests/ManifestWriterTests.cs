using Sword.Bake.Processors;

namespace Sword.Bake.Tests;

/// <summary>
/// Запись манифестов (ядро): меняется только правленое поле params, остальное и порядок сохраняются. Перенесено из
/// SWORD (<c>Sword.Anim.Tests</c>); третий случай — на своём обработчике <see cref="PackProcessor"/> вместо плагинного
/// <c>anim.pack</c> (та же форма параметров: необязательное поле, словарь списков, ссылка).
/// </summary>
public sealed class ManifestWriterTests : IDisposable
{
    internal sealed class PackParams
    {
        [AssetRef]
        public string Skeleton { get; set; } = "";
        [Source(SourceKind.Directory)]
        public string Anims { get; set; } = "";
        [Source(SourceKind.Directory)]
        public string? FingerSrc { get; set; }
        public Dictionary<string, List<string>> Sets { get; set; } = [];
    }

    /// <summary>Обработчик только для разбора и записи параметров — не запекает.</summary>
    internal sealed class PackProcessor : AssetProcessor<PackParams>
    {
        public override string Id => "test.pack";
        public override string Title => "Набор (тест)";

        protected override void Process(BakeContext ctx, PackParams p) => throw new NotSupportedException();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-manifest-" + Guid.NewGuid().ToString("N"));

    public ManifestWriterTests() => Directory.CreateDirectory(Path.Combine(_dir, "anim"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AssetManifest Write(string id, string json)
    {
        string file = Path.Combine(_dir, id.Replace('/', Path.DirectorySeparatorChar) + AssetManifest.Extension);
        File.WriteAllText(file, json);
        return AssetManifest.Load(file, _dir);
    }

    [Fact]
    public void ChangesOnlyEditedFieldAndKeepsTheRest()
    {
        var a = Write("anim/copy", """
            {
              "processor": "file.copy",
              "notes": "заметка остаётся",
              "params": { "from": "${src}/a.json", "to": "game/a.json" },
              "dependsOn": [ "fonts/hud" ]
            }
            """);
        var p = new FileCopyProcessor();
        var parameters = (FileCopyParams)a.ReadParams(p);
        parameters.To = "game/b.json";

        Assert.True(ManifestWriter.SaveParams(a, p, parameters));
        var reloaded = AssetManifest.Load(a.File, _dir);
        Assert.Equal("заметка остаётся", reloaded.Notes);
        Assert.Equal(["fonts/hud"], reloaded.DependsOn);
        Assert.Equal("${src}/a.json", reloaded.Params.GetProperty("from").GetString());
        Assert.Equal("game/b.json", reloaded.Params.GetProperty("to").GetString());
        Assert.Contains("заметка остаётся", File.ReadAllText(a.File));   // кириллица не экранируется
    }

    [Fact]
    public void UnchangedParamsDoNotTouchFile()
    {
        var a = Write("anim/same", """{ "processor": "file.copy", "params": { "from": "x", "to": "y" } }""");
        var p = new FileCopyProcessor();
        string before = File.ReadAllText(a.File);
        Assert.False(ManifestWriter.SaveParams(a, p, a.ReadParams(p)));
        Assert.Equal(before, File.ReadAllText(a.File));
    }

    [Fact]
    public void ClearingOptionalFieldRemovesIt()
    {
        var a = Write("anim/pack", """
            { "processor": "test.pack", "params": { "anims": "a", "sets": { "s": [] }, "skeleton": "skeletons/k", "fingerSrc": "f" } }
            """);
        var p = new PackProcessor();
        var parameters = (PackParams)a.ReadParams(p);
        parameters.FingerSrc = null;

        Assert.True(ManifestWriter.SaveParams(a, p, parameters));
        var reloaded = AssetManifest.Load(a.File, _dir);
        Assert.False(reloaded.Params.TryGetProperty("fingerSrc", out _));
        Assert.Equal("a", reloaded.Params.GetProperty("anims").GetString());
    }
}
