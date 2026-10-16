using System.Text.Json.Nodes;
using Sword.Bake;

namespace Sword.Assets.Tests;

/// <summary>
/// Модель окна без окна: вкладка «Пекарня» — сырой файл → конвертер → рецепт по умолчанию → «Испечь» в фоне → файл
/// в <c>Content/</c>, рецепт записан, дерево перечитано; файл без конвертера — сообщение; флажок «в пак» и «Собрать пак».
/// </summary>
public sealed class AppTests : IDisposable
{
    private readonly string _root = AssetsTestEnv.NewTemp("app");
    private readonly string _src, _bake, _content;

    public AppTests()
    {
        _src = Path.Combine(_root, "src");
        _bake = Path.Combine(_root, "Bake");
        _content = Path.Combine(_root, "Content");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_bake);
        File.WriteAllText(Path.Combine(_bake, SourceRoots.UserFile), new JsonObject { ["roots"] = new JsonObject { ["src"] = _src } }.ToJsonString());
    }

    public void Dispose() => AssetsTestEnv.Delete(_root);

    private AssetsApp Open() =>
        new(new ContentPack(_content, _bake, Path.Combine(_root, "Content.swpak")), new Baker(_bake, _content), () => { }, 0);

    [Fact]
    public void BakeTab_DefaultRecipe_BakesIntoContent()
    {
        string json = Path.Combine(_src, "actions.json");
        File.WriteAllText(json, "{\"a\":1}");
        using var app = Open();
        app.SetSource(json);
        Assert.Null(app.SourceError);
        Assert.Equal(["copy"], app.Candidates.Select(c => c.Id));
        Assert.False(app.RecipeExists);

        var editor = new RecipeEditor();
        editor.Sync(app);
        Assert.NotNull(editor.Params);
        app.BakeSource(editor.Params!);
        AssetsTestEnv.PumpUntil(app, () => app.Idle, "выпечка");

        Assert.Equal(["data/actions.json"], app.LastWritten);
        Assert.True(app.RecipeExists);
        Assert.True(File.Exists(app.Baker.RecipePath(json)));
        Assert.Contains(app.Pack.Files, f => f.Path == "data/actions.json");
        Assert.DoesNotContain(app.Log, e => e.Kind == LogKind.Failed);

        // флажок и сборка пака
        app.ToggleIncluded("data/actions.json");
        Assert.False(app.Pack.IsIncluded("data/actions.json"));
        app.ToggleIncluded("data/actions.json");
        app.BuildPak();
        AssetsTestEnv.PumpUntil(app, () => app.Idle, "сборка пака");
        Assert.Equal(1, app.LastPack!.Files);
    }

    [Fact]
    public void BakeTab_UnknownKind_SaysNoConverter()
    {
        string fbx = Path.Combine(_src, "manny.fbx");
        File.WriteAllText(fbx, "");
        using var app = Open();
        app.SetSource(fbx);
        Assert.Empty(app.Candidates);
        Assert.Contains("конвертера нет", app.SourceError);
        Assert.Null(app.Recipe);
    }

    [Fact]
    public void BakeTab_SeveralConverters_ChoiceResetsForm()
    {
        File.WriteAllBytes(Path.Combine(_src, "thing.glb"), []);
        using var app = Open();
        app.SetSource(Path.Combine(_src, "thing.glb"));
        Assert.Equal(["weapon", "character", "anatomy"], app.Candidates.Select(c => c.Id));
        Assert.Equal("weapon", app.Recipe!.Converter);
        var editor = new RecipeEditor();
        editor.Sync(app);
        Assert.Equal("WeaponParams", editor.Params!.GetType().Name);
        app.ChooseConverter(app.Candidates[2]);
        editor.Sync(app);
        Assert.Equal("anatomy", app.Recipe.Converter);
        Assert.Equal("AnatomyParams", editor.Params!.GetType().Name);
    }
}
