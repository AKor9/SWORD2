using System.Text;
using System.Text.Json.Nodes;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

/// <summary>Секция с ручками и строкой, как <c>combat</c> игры.</summary>
public sealed record UserSection
{
    public float PlayerDamage { get; init; } = 2;
    public bool EnemiesAttack { get; init; } = true;
    public string PlayerWeapon { get; init; } = "sword_1h";
}

/// <summary>
/// Файл пользователя <c>sword.user.json</c> («сохранить» и «загрузить» панели F3): запись одной секции бережная — другие
/// секции, другие ключи секции, порядок и комментарии остаются; CRLF, UTF-8 без BOM; файл читается конфигом.
/// </summary>
public sealed class UserConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-user-config", Guid.NewGuid().ToString("N")[..8]);

    public UserConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string UserFile => Path.Combine(_dir, ConfigSet.UserFileName);

    [Fact]
    public void PathFrom_NextToSwordJson()
    {
        string sub = Path.Combine(_dir, "bin", "Debug");
        Directory.CreateDirectory(sub);
        Assert.Equal(Path.Combine(sub, ConfigSet.UserFileName), UserConfig.PathFrom(sub));   // sword.json нет — рядом
        File.WriteAllText(Path.Combine(_dir, ConfigSet.FileName), "{}");
        Assert.Equal(UserFile, UserConfig.PathFrom(sub));
    }

    [Fact]
    public void NewFile_SectionWritten_CrLf_NoBom_ReadByConfig()
    {
        File.WriteAllText(Path.Combine(_dir, ConfigSet.FileName), "{ \"combat\": { \"playerWeapon\": \"katana\" } }");
        UserConfig.WriteSection(UserFile, "combat", [("playerDamage", JsonValue.Create(3.5)), ("enemiesAttack", JsonValue.Create(false)), ("gone", null)]);
        byte[] bytes = File.ReadAllBytes(UserFile);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "UTF-8 без BOM");
        string text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // только CRLF
        Assert.EndsWith("}\r\n", text);
        Assert.DoesNotContain("gone", text);

        var config = ConfigSet.Load(_dir, [], _ => null);
        var section = config.Section<UserSection>("combat");
        Assert.Equal(new UserSection { PlayerDamage = 3.5f, EnemiesAttack = false, PlayerWeapon = "katana" }, section);   // слои сливаются
        Assert.Contains(UserFile, config.Sources);
        var numbers = UserConfig.Numbers(UserConfig.ReadSection(UserFile, "COMBAT"));
        Assert.Equal(new Dictionary<string, double> { ["playerDamage"] = 3.5, ["enemiesAttack"] = 0 }, numbers);
    }

    [Fact]
    public void Merge_KeepsOtherSectionsKeysOrderAndComments()
    {
        File.WriteAllText(UserFile,
            "{\n" +
            "  // мои настройки\n" +
            "  \"arena\": { \"enemies\": 5, \"list\": [1, 2.50, \"a\\u0041\"] },\n" +
            "  \"Combat\": {\n" +
            "    \"playerWeapon\": \"katana\", /* меч */\n" +
            "    \"PLAYERDAMAGE\": 4,\n" +
            "    \"playerSharpness\": 1.5,\n" +
            "  },\n" +
            "  \"sim\": { \"seed\": 7 }\n" +
            "}\n");
        UserConfig.WriteSection(UserFile, "combat",
            [("playerDamage", JsonValue.Create(2.25)), ("playerSharpness", null), ("enemiesAttack", JsonValue.Create(false))]);
        string text = File.ReadAllText(UserFile);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.Contains("мои настройки", text);   // комментарии — на месте (блоком)
        Assert.Contains("меч", text);
        Assert.Contains("2.50", text);           // число — теми же знаками
        Assert.Contains("\"aA\"", text);         // строка — тот же текст
        Assert.DoesNotContain("PLAYERDAMAGE", text);
        Assert.DoesNotContain("playerSharpness", text);
        // Порядок: arena, Combat (playerWeapon, playerDamage — на месте прежнего ключа, новые — в конец), sim.
        int arena = text.IndexOf("\"arena\"", StringComparison.Ordinal), combat = text.IndexOf("\"Combat\"", StringComparison.Ordinal),
            sim = text.IndexOf("\"sim\"", StringComparison.Ordinal), weapon = text.IndexOf("playerWeapon", StringComparison.Ordinal),
            damage = text.IndexOf("\"playerDamage\": 2.25", StringComparison.Ordinal), enemies = text.IndexOf("\"enemiesAttack\": false", StringComparison.Ordinal);
        Assert.True(arena < combat && combat < weapon && weapon < damage && damage < enemies && enemies < sim, text);

        var root = JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        Assert.Equal(5, (int)root["arena"]!["enemies"]!);
        Assert.Equal(7, (int)root["sim"]!["seed"]!);
        Assert.Equal("katana", (string)root["Combat"]!["playerWeapon"]!);
        Assert.Equal(3, root["Combat"]!.AsObject().Count);

        // Та же запись ещё раз — тот же текст (запись устойчива).
        UserConfig.WriteSection(UserFile, "combat", [("playerDamage", JsonValue.Create(2.25)), ("enemiesAttack", JsonValue.Create(false))]);
        Assert.Equal(text, File.ReadAllText(UserFile));
    }

    [Fact]
    public void BrokenFile_Error_FileUntouched()
    {
        const string broken = "{ \"combat\": { \"playerDamage\": 2 ";
        File.WriteAllText(UserFile, broken);
        Assert.Throws<InvalidDataException>(() => UserConfig.WriteSection(UserFile, "combat", [("playerDamage", JsonValue.Create(3))]));
        Assert.Equal(broken, File.ReadAllText(UserFile));
        Assert.Throws<InvalidDataException>(() => UserConfig.ReadSection(UserFile, "combat"));
        File.WriteAllText(UserFile, "{ \"combat\": 5 }");
        Assert.Throws<InvalidDataException>(() => UserConfig.WriteSection(UserFile, "combat", [("playerDamage", JsonValue.Create(3))]));
        Assert.Throws<InvalidDataException>(() => UserConfig.ReadSection(UserFile, "combat"));
        // Корень — не объект секций: ошибка, а не молча пропавшая запись.
        foreach (string root in (string[])["[]", "5", "/* c */ \"x\""])
        {
            File.WriteAllText(UserFile, root);
            Assert.Throws<InvalidDataException>(() => UserConfig.WriteSection(UserFile, "combat", [("playerDamage", JsonValue.Create(3))]));
            Assert.Equal(root, File.ReadAllText(UserFile));
        }
        Assert.Null(UserConfig.ReadSection(Path.Combine(_dir, "none.json"), "combat"));
    }
}
