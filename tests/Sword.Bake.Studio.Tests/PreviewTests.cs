using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sword.Engine.Content;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio.Tests;

/// <summary>
/// Вкладка «Просмотр» без окна: копия проекта Bake/ смотрит в эталонный пак (только чтение; слежение выключено — сборки
/// нет), у каждого обработчика с <see cref="IAssetPreview"/> единицы из <see cref="Program.PreviewShots"/> рисуются
/// кадром интерфейса: сцена построена (треугольники нарисованы или картинки ушли в «видеопамять»), ошибок нет;
/// манифесты и пак после предпросмотров те же.
/// </summary>
public sealed class PreviewTests(StudioFontsFixture fonts) : IClassFixture<StudioFontsFixture>, IDisposable
{
    private readonly string _root = StudioTestEnv.CopyBakeProject();

    public void Dispose() => StudioTestEnv.Delete(_root);

    /// <summary>Эталонный пак — ближайший вверх от тестов (корень репозитория или рабочего дерева).</summary>
    private static string ReferencePak()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string pak = Path.Combine(dir.FullName, ContentLocator.ReferencePakName);
            if (File.Exists(pak)) return pak;
        }
        throw new FileNotFoundException($"нет {ContentLocator.ReferencePakName} выше {AppContext.BaseDirectory}");
    }

    private sealed class Uploaded : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    [Fact]
    public void EveryPreviewer_BuildsScene_AndLeavesAssetDataAlone()
    {
        string pak = ReferencePak();
        string projectFile = Path.Combine(_root, BakeProject.FileName);
        var json = JsonNode.Parse(File.ReadAllText(projectFile))!.AsObject();
        json["pak"] = pak;
        File.WriteAllText(projectFile, json.ToJsonString());
        string manifestsBefore = HashManifests(_root);
        var pakBefore = (new FileInfo(pak).Length, new FileInfo(pak).LastWriteTimeUtc);

        using var app = new StudioApp(BakeProject.Open(_root), () => { }, 0) { Watch = false };
        StudioTestEnv.PumpUntil(app, () => app.Idle, "проверка статусов");
        var contentsBefore = app.PakContents.Select(e => e.Path + ":" + e.Hash).ToList();
        var shots = Program.PreviewShots(app.Project);
        Assert.Equal(["anatomy.mesh", "anim.pack", "anim.skeleton", "font.vector", "skin.mesh", "texture.array", "weapon.static"],
            shots.Select(s => app.Project.Find(s.Id)!.Processor).Distinct().Order(StringComparer.Ordinal));

        int uploads = 0;
        var owners = new List<Uploaded>();
        foreach (var (name, id, mode) in shots)
        {
            var asset = app.Project.Find(id)!;
            using var panel = new PreviewPanel(img =>
            {
                Assert.True(img.Mips.Count >= 1 && img.Mips[0].Length == img.Width * img.Height * 4, $"{name}: картинка {img.Name}");
                uploads++;
                var owner = new Uploaded();
                owners.Add(owner);
                return new PreviewTexture(uploads, owner);
            }, mode, still: true);
            var ui = new UiContext(fonts.Fonts);
            ui.BeginFrame(new UiInput(), 1600, 900, 1f);
            panel.Draw(ui, new UiRect(0, 0, 1200, 800), app, asset);
            ui.EndFrame();

            Assert.True(panel.Error is null, $"{name} ({id}): {panel.Error}");
            var scene = panel.Scene;
            Assert.True(scene is not null, $"{name} ({id}): сцены нет");
            Assert.NotEmpty(scene.Info);
            if (scene.Images.Count > 0) Assert.True(panel.UploadedImages > 0, $"{name}: картинки не показаны");
            else
            {
                Assert.True(scene.Triangles.Count > 0 || scene.Lines.Count > 0, $"{name} ({id}): пустая сцена");
                Assert.True(panel.LastDrawnTriangles > 0 || scene.Triangles.Count == 0, $"{name} ({id}): треугольники не нарисованы");
            }
            if (asset.Processor == "anim.pack") Assert.True(scene.Animation!.Clips.Count > 0, $"{name}: клипов нет");
        }
        Assert.All(owners, o => Assert.Equal(1, o.Disposed));   // картинки освобождены вместе с панелью

        // данные ассетов не тронуты: манифесты, файлы пака и их хеши
        Assert.Equal(manifestsBefore, HashManifests(_root));
        Assert.Equal(pakBefore, (new FileInfo(pak).Length, new FileInfo(pak).LastWriteTimeUtc));
        Assert.Equal(contentsBefore, app.PakContents.Select(e => e.Path + ":" + e.Hash).ToList());
    }

    private static string HashManifests(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetRelativePath(root, file).StartsWith(BakeProject.StateDirName, StringComparison.OrdinalIgnoreCase)) continue;   // кэш модели
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file)));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
