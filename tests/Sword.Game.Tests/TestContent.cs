using System.Security.Cryptography;
using System.Text.Json;

namespace Sword.Game.Tests;

/// <summary>
/// Пак контента игры для тестов на настоящем контенте (категория Content): <c>Content.reference.swpak</c> в корне репозитория (вне
/// git, только для чтения), закреплённый <c>tools/scripts/reference.ps1 -Pin</c>; его SHA-256 и размер — в
/// <c>tests/reference/reference.json</c>. Пак — контент игры, не сравнение со SWORD (со SWORD не сравниваем — решение 14, точка 7а).
/// Без пака тесты категории Content падают с подсказкой; <c>verify.ps1 -SkipReference</c> их не запускает.
/// </summary>
public static class TestContent
{
    /// <summary>Путь пака контента после проверки SHA-256 против <c>tests/reference/reference.json</c>.</summary>
    public static string ReferencePak()
    {
        string root = RepoRoot();
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "reference", "reference.json")));
        var content = reference.RootElement.GetProperty("content");
        string pak = Path.Combine(root, content.GetProperty("file").GetString()!);
        Assert.True(File.Exists(pak), $"нет пака контента {pak} — закрепите его (tools/scripts/reference.ps1 -Pin) или " +
            "запустите проверку без него (verify.ps1 -SkipReference: категория Content не запускается)");
        string expected = content.GetProperty("sha256").GetString()!;
        using (var s = File.OpenRead(pak))
        {
            string actual = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            Assert.True(actual == expected, $"пак контента изменён: SHA-256 {actual}, закреплён {expected}");
        }
        return pak;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория (SWORD.slnx)");
    }
}
