using Sword.Engine.Content;

namespace Sword.Assets.Tests;

/// <summary>
/// Пак из <c>Content/</c>: путь в паке = путь в <c>Content/</c>, по умолчанию идёт всё, выключенный файл или каталог
/// (<c>Bake/pak.json</c>) в пак не попадает, включение файла из выключенного каталога не включает соседей; ошибка сборки
/// оставляет прежний пак; проверка «пак собран из Content/»; CLI sword-assets pack, list и check.
/// </summary>
public sealed class ContentPackTests : IDisposable
{
    private readonly string _root = AssetsTestEnv.NewTemp("pack");
    private readonly string _content, _bake, _pak;

    public ContentPackTests()
    {
        _content = Path.Combine(_root, "Content");
        _bake = Path.Combine(_root, "Bake");
        _pak = Path.Combine(_root, "Content.swpak");
        Directory.CreateDirectory(_bake);
        Write("animations/katana/Attack.swclip", "a");
        Write("animations/katana/Idle.swclip", "bb");
        Write("animations/unarmed/Punch.swclip", "ccc");
        Write("data/combat/actions.json", "{}");
    }

    public void Dispose() => AssetsTestEnv.Delete(_root);

    private void Write(string path, string text)
    {
        string full = Path.Combine(_content, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private ContentPack Open() => new(_content, _bake, _pak);

    private List<string> PakPaths()
    {
        using var pak = PakSource.Open(_pak);
        return pak.Entries.Select(e => e.Path).ToList();
    }

    [Fact]
    public void Pack_TakesEverything_PathInPakIsPathInContent()
    {
        var result = Open().Pack();
        Assert.Equal(4, result.Files);
        Assert.Equal(0, result.Excluded);
        Assert.Equal(["animations/katana/Attack.swclip", "animations/katana/Idle.swclip", "animations/unarmed/Punch.swclip", "data/combat/actions.json"],
            PakPaths());
        using var pak = PakSource.Open(_pak);
        Assert.Equal("bb", System.Text.Encoding.UTF8.GetString(pak.ReadAllBytes("animations/katana/Idle.swclip")));
        Assert.False(File.Exists(Path.Combine(_bake, ContentPack.ExclusionsFile)));   // исключений нет — файла нет
    }

    [Fact]
    public void DisabledClip_NotInPak_AndRemembered()
    {
        var pack = Open();
        pack.SetIncluded("animations/katana/Idle.swclip", false);
        Assert.Equal(1, pack.Pack().Excluded);
        Assert.DoesNotContain("animations/katana/Idle.swclip", PakPaths());
        Assert.Contains("animations/katana/Attack.swclip", PakPaths());

        // исключение записано в pak.json (CRLF) и читается заново
        string json = File.ReadAllText(Path.Combine(_bake, ContentPack.ExclusionsFile));
        Assert.Contains("animations/katana/Idle.swclip", json);
        Assert.Contains("\r\n", json);
        Assert.False(Open().IsIncluded("animations/katana/Idle.swclip"));

        Open().SetIncluded("animations/katana/Idle.swclip", true);
        Assert.True(Open().IsIncluded("animations/katana/Idle.swclip"));
        Assert.Empty(Open().Exclude);
    }

    [Fact]
    public void DisabledFolder_AndOneFileBackIn()
    {
        var pack = Open();
        pack.SetIncluded("animations/katana", false);
        Assert.False(pack.IsIncluded("animations/katana/Attack.swclip"));
        Assert.False(pack.IsIncluded("animations/katana/Idle.swclip"));
        Assert.True(pack.IsIncluded("animations/unarmed/Punch.swclip"));

        // каталог в pak.json руками — включение одного файла раскладывает его, сосед остаётся выключенным
        File.WriteAllText(Path.Combine(_bake, ContentPack.ExclusionsFile), "{ \"exclude\": [ \"animations/katana/\" ] }");
        pack = Open();
        pack.SetIncluded("animations/katana/Idle.swclip", true);
        Assert.True(pack.IsIncluded("animations/katana/Idle.swclip"));
        Assert.False(pack.IsIncluded("animations/katana/Attack.swclip"));
        Assert.Equal(["animations/katana/Attack.swclip"], pack.Exclude);
    }

    [Fact]
    public void Check_PakMatchesContentAndExclusions()
    {
        var pack = Open();
        pack.Pack();
        Assert.Empty(pack.Check());

        Write("data/combat/actions.json", "{ }");
        Assert.Equal(["другое содержимое: data/combat/actions.json"], Open().Check());
        pack = Open();
        pack.Pack();
        pack.SetIncluded("animations/unarmed", false);
        Assert.Equal(["лишний в паке (выключен или нет в Content/): animations/unarmed/Punch.swclip"], pack.Check());
        Write("animations/katana/New.swclip", "n");
        Assert.Contains("нет в паке: animations/katana/New.swclip", Open().Check());
    }

    [Fact]
    public void FailedPack_KeepsPreviousPak()
    {
        Open().Pack();
        var before = File.ReadAllBytes(_pak);
        var pack = Open();
        pack.SetIncluded("", false);
        Assert.Throws<InvalidDataException>(() => pack.Pack());
        Assert.Equal(before, File.ReadAllBytes(_pak));
    }

    [Fact]
    public void Cli_PackAndList()
    {
        Open().SetIncluded("animations/unarmed", false);
        string exe = AssetsTestEnv.Exe("Sword.Assets", "sword-assets.exe");
        var (exit, output) = AssetsTestEnv.Run(exe, _root, TimeSpan.FromMinutes(1), null, "pack", "--content", _content, "--bake", _bake);
        Assert.True(exit == 0, output);
        Assert.Contains("файлов 3", output);
        Assert.Contains("выключено 1", output);
        Assert.DoesNotContain("animations/unarmed/Punch.swclip", PakPaths());

        (exit, output) = AssetsTestEnv.Run(exe, _root, TimeSpan.FromMinutes(1), null, "list", "--excluded", "--content", _content, "--bake", _bake);
        Assert.True(exit == 0, output);
        Assert.Equal("- animations/unarmed/Punch.swclip 3", output.Trim());

        (exit, output) = AssetsTestEnv.Run(exe, _root, TimeSpan.FromMinutes(1), null, "check", "--content", _content, "--bake", _bake);
        Assert.True(exit == 0, output);
        Open().SetIncluded("animations/unarmed", true);
        (exit, output) = AssetsTestEnv.Run(exe, _root, TimeSpan.FromMinutes(1), null, "check", "--content", _content, "--bake", _bake);
        Assert.Equal(2, exit);
        Assert.Contains("нет в паке: animations/unarmed/Punch.swclip", output);
    }
}
