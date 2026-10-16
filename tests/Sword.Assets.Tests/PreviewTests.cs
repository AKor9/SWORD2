using Sword.Engine.Content;
using Sword.Engine.Ui;

namespace Sword.Assets.Tests;

/// <summary>Шрифты окна — общие на класс тестов: запекаются один раз во временный кэш.</summary>
public sealed class WindowFontsFixture : IDisposable
{
    public readonly string Cache = AssetsTestEnv.NewTemp("fonts");
    public readonly List<string> Log = [];
    public readonly UiFonts Fonts;

    public WindowFontsFixture() => Fonts = WindowFonts.Load(Log.Add, Cache);

    public void Dispose()
    {
        Fonts.Dispose();
        AssetsTestEnv.Delete(Cache);
    }
}

/// <summary>Тест на <c>Content/</c> репозитория: нет испечённых ресурсов (новый клон) — пропуск.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ContentFactAttribute : FactAttribute
{
    public static string Dir => Path.Combine(AssetsTestEnv.RepoRoot, "Content");

    public ContentFactAttribute()
    {
        if (!File.Exists(Path.Combine(Dir, "skeletons", "ue5_manny.swskeleton")))
            Skip = $"нет испечённых ресурсов в {Dir} — sword-bake --all";
    }
}

/// <summary>
/// Предпросмотр без окна на <c>Content/</c> репозитория: каждый вид ресурса (набор клипов, клип, скелет, меш персонажа,
/// анатомия, оружие и его зоны крови, текстура, шрифт, данные) рисуется кадром интерфейса — сцена построена, треугольники
/// нарисованы или картинки ушли в «видеопамять», ошибок нет; файлы <c>Content/</c> не тронуты.
/// </summary>
public sealed class PreviewTests(WindowFontsFixture fonts) : IClassFixture<WindowFontsFixture>
{
    private sealed class Uploaded : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    [ContentFact]
    public void EveryKind_BuildsAndDrawsScene()
    {
        var pack = new ContentPack(ContentFactAttribute.Dir, Path.Combine(AssetsTestEnv.RepoRoot, "Bake"), Path.Combine(AssetsTestEnv.RepoRoot, "Content.swpak"));
        var stamp = pack.Files.Select(f => (f.Path, f.Size, f.ModifiedUtc)).ToList();
        var content = new DirectorySource(pack.ContentDir);
        string[] selections =
        [
            "animations/locomotion", "skeletons/ue5_manny.swskeleton", "meshes/characters/manny.swmesh", "meshes/characters/anatomy/organs.swmesh",
            "meshes/weapons/katana.swmesh", "data/weapons/katana.swblood", "textures/blood", "fonts/body.swfont", "data/combat/actions.json",
        ];
        int uploads = 0;
        var owners = new List<Uploaded>();
        foreach (string selection in selections)
        {
            var files = pack.Under(selection).ToList();
            Assert.True(files.Count > 0, $"{selection}: нет в Content/");
            using var panel = new PreviewPanel(img =>
            {
                Assert.True(img.Mips.Count >= 1 && img.Mips[0].Length == img.Width * img.Height * 4, $"{selection}: картинка {img.Name}");
                var owner = new Uploaded();
                owners.Add(owner);
                return new PreviewTexture(++uploads, owner);
            }, 0, still: true);
            var ui = new UiContext(fonts.Fonts);
            ui.BeginFrame(new UiInput(), 1600, 900, 1f);
            panel.Draw(ui, new UiRect(0, 0, 1200, 800), content, selection, files);
            ui.EndFrame();

            Assert.True(panel.Error is null, $"{selection}: {panel.Error}");
            var scene = panel.Scene;
            Assert.True(scene is not null, $"{selection}: сцены нет");
            Assert.NotEmpty(scene.Info);
            if (scene.Images.Count > 0) Assert.True(panel.UploadedImages > 0, $"{selection}: картинки не показаны");
            else if (selection.EndsWith(".json", StringComparison.Ordinal)) Assert.Contains(scene.Info, l => l.Contains('{') || l.Contains('"'));
            else
            {
                Assert.True(scene.Triangles.Count > 0 || scene.Lines.Count > 0, $"{selection}: пустая сцена");
                Assert.True(panel.LastDrawnTriangles > 0 || scene.Triangles.Count == 0, $"{selection}: треугольники не нарисованы");
            }
            if (selection.StartsWith("animations/", StringComparison.Ordinal)) Assert.True(scene.Animation!.Clips.Count > 0, $"{selection}: клипов нет");
        }
        Assert.All(owners, o => Assert.Equal(1, o.Disposed));   // картинки освобождены вместе с панелью

        pack.Rescan();
        Assert.Equal(stamp, pack.Files.Select(f => (f.Path, f.Size, f.ModifiedUtc)).ToList());
    }
}
