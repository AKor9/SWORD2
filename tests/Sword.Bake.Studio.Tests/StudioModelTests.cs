using System.Text;
using System.Text.Json;
using Sword.Engine.Content;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio.Tests;

/// <summary>Шрифты окна для форм без GPU: запечённые один раз на класс, кэш — во временном каталоге.</summary>
public sealed class StudioFontsFixture : IDisposable
{
    public readonly string Cache = StudioTestEnv.NewTemp("fonts");
    public readonly List<string> Log = [];
    public readonly UiFonts Fonts;

    public StudioFontsFixture() => Fonts = StudioFonts.Load(Log.Add, Cache);

    public void Dispose()
    {
        Fonts.Dispose();
        StudioTestEnv.Delete(Cache);
    }
}

/// <summary>
/// Модель Studio без окна: проект Bake/ с обоими плагинами (загрузчик ядра, в процессе тестов), состояние ассетов,
/// форма параметров туда и обратно на каждом манифесте, запись манифеста (CRLF и порядок полей), добавление и удаление
/// ассета, сборка из Studio против sword-bake, шрифты окна.
/// </summary>
public sealed class StudioModelTests(StudioFontsFixture fonts) : IClassFixture<StudioFontsFixture>, IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (var dir in _temp) StudioTestEnv.Delete(dir);
    }

    private string Temp(string dir)
    {
        _temp.Add(dir);
        return dir;
    }

    private static StudioApp Open(string root)
    {
        var app = new StudioApp(BakeProject.Open(root), () => { }, 0) { Watch = false };
        StudioTestEnv.PumpUntil(app, () => app.Idle, "проверка статусов");
        return app;
    }

    [Fact]
    public void BakeProject_OpensWithPlugins_StatusesKnown()
    {
        using var app = Open(Temp(StudioTestEnv.CopyBakeProject()));
        var project = app.Project;
        Assert.Empty(project.Broken);
        Assert.Empty(project.Processors.Errors);
        Assert.Equal(["anatomy.mesh", "anim.pack", "anim.skeleton", "file.copy", "font.vector", "skin.mesh", "texture.array", "weapon.static"],
            project.Processors.Processors.Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.True(project.Assets.Count >= 25, $"ассетов {project.Assets.Count}");
        Assert.True(app.StatusesKnown);
        Assert.Equal(project.Assets.Select(a => a.Id).Order(), app.Statuses.Keys.Order());
        Assert.Equal(PakStatus.Missing, app.PakStatus!.Status);   // пак копии не собран
        Assert.Equal(project.Assets.First().Id, app.Selected);
        Assert.Contains(app.Log, e => e.Text.StartsWith($"проект {project.Name}:", StringComparison.Ordinal));
    }

    /// <summary>Кадр формы без окна: виджеты читают параметры и рисуют их, ввода нет.</summary>
    private void DrawForm(ParamsEditor editor, StudioApp app)
    {
        var ui = new UiContext(fonts.Fonts);
        ui.BeginFrame(new UiInput(), 1600, 900, 1f);
        editor.Draw(ui, new UiRect(0, 0, 1200, 860), app);
        ui.EndFrame();
        Assert.True(ui.Output.Count > 0);
    }

    [Fact]
    public void ParamsForm_EveryManifest_RoundTripKeepsBytes()
    {
        using var app = Open(Temp(StudioTestEnv.CopyBakeProject()));
        var editor = new ParamsEditor(app.Project.Processors);
        foreach (var id in app.Project.Assets.Select(a => a.Id).ToList())
        {
            var asset = app.Project.Find(id)!;
            byte[] before = File.ReadAllBytes(asset.File);
            editor.Sync(asset, app);
            Assert.True(editor.Error is null, $"{id}: {editor.Error}");
            DrawForm(editor, app);
            Assert.False(editor.Dirty, $"{id}: форма без ввода отметила правку");
            var processor = app.Project.Processors.Find(asset.Processor)!;
            Assert.False(ManifestWriter.SaveParams(asset, processor, editor.EditedParams!), $"{id}: форма изменила значения");
            editor.MarkEdited();
            editor.Save(app);   // запись «правки» без изменений: файл не трогается
            Assert.True(editor.Error is null, $"{id}: {editor.Error}");
            Assert.Equal(before, File.ReadAllBytes(asset.File));
        }
    }

    private static List<string> Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToList();

    [Fact]
    public void ParamsForm_EditThenRevert_KeepsCrlfOrderAndValues()
    {
        using var app = Open(Temp(StudioTestEnv.CopyBakeProject()));
        var editor = new ParamsEditor(app.Project.Processors);
        // первая единица со строковым параметром, заданным в манифесте
        foreach (var asset in app.Project.Assets.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            editor.Sync(asset, app);
            if (editor.EditedParams is not { } edited || asset.Params.ValueKind != JsonValueKind.Object) continue;
            var property = edited.GetType().GetProperties().FirstOrDefault(p => p.PropertyType == typeof(string) && p.CanWrite
                && p.GetValue(edited) is string { Length: > 0 }
                && asset.Params.EnumerateObject().Any(f => f.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
            if (property is null) continue;

            string file = asset.File, id = asset.Id;
            byte[] original = File.ReadAllBytes(file);
            string value = (string)property.GetValue(edited)!;
            List<string> rootKeys, paramKeys;
            using (var doc = JsonDocument.Parse(original)) (rootKeys, paramKeys) = (Keys(doc.RootElement), Keys(doc.RootElement.GetProperty("params")));

            property.SetValue(edited, value + "_studio");
            editor.MarkEdited();
            editor.Save(app);
            Assert.True(editor.Error is null, editor.Error);
            byte[] changed = File.ReadAllBytes(file);
            string text = Encoding.UTF8.GetString(changed);
            Assert.Contains(value + "_studio", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // только CRLF
            using (var doc = JsonDocument.Parse(changed))
            {
                Assert.Equal(rootKeys, Keys(doc.RootElement));
                Assert.Equal(paramKeys, Keys(doc.RootElement.GetProperty("params")));
            }

            var reloaded = app.Project.Find(id)!;
            Assert.Same(reloaded, editor.Asset);   // после записи форма показывает перечитанный манифест
            property.SetValue(editor.EditedParams, value);
            editor.MarkEdited();
            editor.Save(app);
            // Значения и порядок — прежние, переводы строк — CRLF; раскладку пробелов писатель ядра задаёт свою
            // (однострочные объекты разворачиваются), поэтому байты после правки и отката не обязаны совпасть.
            byte[] reverted = File.ReadAllBytes(file);
            Assert.DoesNotContain("\n", Encoding.UTF8.GetString(reverted).Replace("\r\n", ""));
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(original), System.Text.Json.Nodes.JsonNode.Parse(reverted)),
                Encoding.UTF8.GetString(reverted));
            using (var doc = JsonDocument.Parse(reverted)) Assert.Equal(paramKeys, Keys(doc.RootElement.GetProperty("params")));
            return;
        }
        Assert.Fail("в проекте нет единицы со строковым параметром");
    }

    [Fact]
    public void AddAndDeleteAsset_OnTempProject()
    {
        string root = Temp(StudioTestEnv.NewTemp("add"));
        BakeProject.Create(root, "Studio");
        string data = Path.Combine(root, "src", "readme.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        File.WriteAllText(data, "SWORD\r\n");
        using var app = Open(root);

        Assert.Null(app.AddAsset("data/readme", "file.copy", new Dictionary<string, string> { ["from"] = data }, "проверка Studio"));
        var asset = app.Project.Find("data/readme");
        Assert.NotNull(asset);
        Assert.Equal("data/readme", app.Selected);
        Assert.True(File.Exists(app.Project.ManifestPath("data/readme")));
        Assert.Equal("file.copy", asset.Processor);
        Assert.NotNull(app.AddAsset("data/readme", "file.copy", new Dictionary<string, string>(), ""));   // второй раз — ошибка текстом
        Assert.NotNull(app.AddAsset("Bad Id", "file.copy", new Dictionary<string, string>(), ""));

        app.Delete(asset);
        Assert.Null(app.Project.Find("data/readme"));
        Assert.False(File.Exists(asset.File));
        Assert.Single(Directory.GetFiles(Path.Combine(app.Project.StateDir, "removed")));
        Assert.Contains(app.Log, e => e.Asset == "data/readme" && e.Text.StartsWith("ассет удалён", StringComparison.Ordinal));
    }

    [Fact]
    public void Watch_SourceInProjectRoot_SettlesThenAutoBuildsOnChange()
    {
        // исходник в корне проекта: корень слежения за исходниками — весь проект вместе с .bake, куда пишет проверка статусов
        string root = Temp(StudioTestEnv.NewTemp("watch"));
        var created = BakeProject.Create(root, "Watch");
        string note = Path.Combine(root, "note.txt");
        File.WriteAllText(note, "1");
        created.AddAsset("data/note", "file.copy", new Dictionary<string, string> { ["from"] = note });
        using var app = new StudioApp(BakeProject.Open(root), () => { }, 0);
        Assert.True(app.Watch);
        StudioTestEnv.PumpUntil(app, () => app.Idle, "проверка статусов");
        var quiet = System.Diagnostics.Stopwatch.StartNew();
        while (quiet.Elapsed.TotalSeconds < 1.5)   // дольше паузы слежения: запись кэша в .bake не будит проверку снова
        {
            app.Pump();
            Assert.True(app.Idle, $"модель не затихла: {app.Activity}");
            Thread.Sleep(20);
        }
        Assert.DoesNotContain(app.Log, e => e.Text.StartsWith("автосборка", StringComparison.Ordinal));

        File.WriteAllText(note, "2");   // правка исходника — автосборка устаревшего
        StudioTestEnv.PumpUntil(app, () => app.Idle && app.LastSummary is not null, "автосборка");
        Assert.Contains(app.Log, e => e.Text.StartsWith("автосборка", StringComparison.Ordinal));
        Assert.Equal(AssetStatus.UpToDate, app.StatusOf("data/note").Status);
    }

    [Fact]
    public void BuildFromStudio_SamePakAsSwordBake()
    {
        // два одинаковых проекта с file.copy: один собирает модель Studio, другой — sword-bake build
        string a = Temp(StudioTestEnv.NewTemp("build-studio")), b = Temp(StudioTestEnv.NewTemp("build-cli"));
        foreach (var root in new[] { a, b })
        {
            var project = BakeProject.Create(root, "Same");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "one.txt"), "один\r\n");
            File.WriteAllBytes(Path.Combine(root, "src", "two.bin"), [1, 2, 3, 250]);
            project.AddAsset("data/one", "file.copy", new Dictionary<string, string> { ["from"] = Path.Combine(root, "src", "one.txt") });
            project.AddAsset("data/two", "file.copy", new Dictionary<string, string> { ["from"] = Path.Combine(root, "src", "two.bin") });
        }

        string pakA;
        using (var app = Open(a))
        {
            app.BuildAll();
            StudioTestEnv.PumpUntil(app, () => !app.Busy && app.Idle, "сборка");
            Assert.True(app.LastSummary is { Ok: true, Built: 2 }, $"итог: {app.LastSummary?.Built} запечено, {app.LastSummary?.Failed} ошибок");
            Assert.Contains(app.Log, e => e.Kind == BakeEventKind.Finished && e.Asset == "data/two");
            Assert.Equal(AssetStatus.UpToDate, app.StatusOf("data/one").Status);
            pakA = app.Project.PakPath;
        }
        var (exit, output) = StudioTestEnv.Run(StudioTestEnv.Exe("Sword.Bake.Cli", "sword-bake.exe"), b, TimeSpan.FromMinutes(2), null,
            "--project", b, "build");
        Assert.True(exit == 0, output);
        string pakB = BakeProject.Open(b).PakPath;

        using var sa = PakSource.Open(pakA);
        using var sb = PakSource.Open(pakB);
        static List<(string, long, ulong)> Content(PakSource s) =>
            s.Entries.Where(e => !e.Path.StartsWith(".bake/", StringComparison.Ordinal)).Select(e => (e.Path, e.Size, e.Hash)).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        Assert.Equal(Content(sb), Content(sa));
        Assert.Equal(["data/one", "data/two"], Content(sa).Select(e => e.Item1));
    }

    [Fact]
    public void StudioFonts_SystemFacesTakeAllRoles_SecondLoadFromCache()
    {
        var f = fonts.Fonts;
        Assert.Equal("segoeui", f.FontName(UiFace.Body));
        Assert.Equal("seguisb", f.FontName(UiFace.Head));
        Assert.Equal("consola", f.FontName(UiFace.Num));
        Assert.True(f.GlyphCount > 1000);
        Assert.Equal(StudioFonts.Faces.Length, Directory.GetFiles(fonts.Cache, "*.swfont").Length);
        var log = new List<string>();
        using var again = StudioFonts.Load(log.Add, fonts.Cache);
        Assert.Empty(log);   // из кэша: пекарь не звался
        Assert.Equal(f.GlyphCount, again.GlyphCount);
    }
}
