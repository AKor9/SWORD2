using Sword.Engine.Content;
using Sword.Game.Animation;

namespace Sword.Game.Tests;

/// <summary>
/// Пак игры для тестов категории Content — тот же, что у игры (<see cref="ContentLocator"/>): <c>Content.swpak</c>
/// (собран из <c>Content/</c>) или <c>SWORD_CONTENT</c>. Без пака тесты падают с подсказкой.
/// </summary>
public static class TestContent
{
    public static string GamePak()
    {
        string? pak = ContentLocator.Find();
        Assert.True(pak is not null && File.Exists(pak), $"нет пака игры {ContentLocator.PakName}. {ContentLocator.BakeHint}; " +
            "без пака — verify.ps1 -SkipReference (категория Content не запускается)");
        return pak;
    }

    /// <summary>Ассеты и отпечаток пака игры — один раз на процесс (классы тестов идут параллельно и делят их: только чтение).</summary>
    internal static readonly Lazy<(CharacterAssets Assets, string Content)> Reference = new(() =>
    {
        using var pak = PakSource.Open(GamePak());
        return (CharacterAssets.Load(pak), pak.ContentHash);
    });
}
