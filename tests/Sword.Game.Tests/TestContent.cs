using Sword.Engine.Content;

namespace Sword.Game.Tests;

/// <summary>
/// Пак контента игры для тестов на настоящем контенте (категория Content): тот же, что читают игра и Headless
/// (<see cref="ContentLocator"/>) — <c>Content.swpak</c> пекарни в корне репозитория (вне git) или пак из переменной
/// <c>SWORD_CONTENT</c>. На нём записаны эталонные реплеи. Эталонный пак <c>Content.reference.swpak</c>, найденный вместо
/// него, — провал с командой выпечки, а не молчаливая подмена (эталоны на нём отвергаются по хешу контента).
/// Без пака тесты категории Content падают с подсказкой; <c>verify.ps1 -SkipReference</c> их не запускает.
/// </summary>
public static class TestContent
{
    /// <summary>Путь пака игры.</summary>
    public static string GamePak()
    {
        string? pak = ContentLocator.Find();
        Assert.True(pak is not null && File.Exists(pak), $"нет пака игры {ContentLocator.PakName}. {ContentLocator.BakeHint}; " +
            "без пака — verify.ps1 -SkipReference (категория Content не запускается)");
        Assert.True(ContentLocator.ReferenceWarning(pak) is null, ContentLocator.ReferenceWarning(pak));
        return pak;
    }
}
