using System.Text;

namespace Sword.Engine.Content.Tests;

/// <summary>Сведения пекарни о сборке пака (<c>.bake/build.json</c>): запись, чтение, владелец файла, единственный исходник.</summary>
public sealed class PakBuildInfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-buildinfo-" + Guid.NewGuid().ToString("N"));

    public PakBuildInfoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static PakBuildInfo Sample() => new()
    {
        Project = "Тест",
        BuiltUtc = new DateTime(2026, 9, 25, 10, 20, 30, 123, DateTimeKind.Utc),
        Assets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["combat/actions"] = new PakAssetRecord
            {
                Processor = "file.copy",
                Fingerprint = "file.copy|1|Sword.Bake:0123456789ab",
                ParamsHash = "00112233445566778899AABBCCDDEEFF",
                BakedUtc = new DateTime(2026, 9, 25, 10, 20, 0, DateTimeKind.Utc),
                Inputs = [new PakFileRecord { Path = @"D:\src\actions.json", Size = 12, Mtime = 638_000_000_000_000_000, Hash = "ABCDEF" }],
                Outputs = [new PakFileRecord { Path = "combat/actions.json", Size = 12, Hash = "0011223344556677" }],
            },
            ["anim/set"] = new PakAssetRecord
            {
                Processor = "anim.pack",
                InputDirs = [@"D:\src\anims"],
                ContentInputs = ["skeletons/manny.swskeleton"],
                Inputs =
                [
                    new PakFileRecord { Path = @"D:\src\anims\a.json", Size = 1, Hash = "01" },
                    new PakFileRecord { Path = @"D:\src\anims\b.json", Size = 1, Hash = "02" },
                ],
                Outputs = [new PakFileRecord { Path = "anim/set/a.swclip", Size = 1, Hash = "0000000000000001" }],
            },
        },
    };

    private PakSource Pak(byte[] buildInfo)
    {
        string path = Path.Combine(_dir, "test" + PakFormat.Extension);
        using (var w = new PakWriter(path))
        {
            w.Add("combat/actions.json", new MemoryStream("{}"u8.ToArray()));
            w.Add(PakBuildInfo.EntryPath, new MemoryStream(buildInfo));
            w.Commit();
        }
        return PakSource.Open(path);
    }

    [Fact]
    public void WrittenInPak_ReadsBackFieldByField()
    {
        var sample = Sample();
        using var pak = Pak(sample.ToUtf8());

        var info = PakBuildInfo.Read(pak)!;
        Assert.Equal(PakBuildInfo.CurrentVersion, info.Version);
        Assert.Equal("Тест", info.Project);
        Assert.Equal(sample.BuiltUtc, info.BuiltUtc);
        Assert.Equal(DateTimeKind.Utc, info.BuiltUtc.Kind);
        Assert.Equal(["combat/actions", "anim/set"], info.Assets.Keys);
        Assert.True(info.Assets.ContainsKey("COMBAT/ACTIONS"));   // ID без учёта регистра и после чтения
        var rec = info.Assets["combat/actions"];
        Assert.Equal(("file.copy", "file.copy|1|Sword.Bake:0123456789ab", "00112233445566778899AABBCCDDEEFF"), (rec.Processor, rec.Fingerprint, rec.ParamsHash));
        var input = Assert.Single(rec.Inputs);
        Assert.Equal((@"D:\src\actions.json", 12L, 638_000_000_000_000_000L, "ABCDEF"), (input.Path, input.Size, input.Mtime, input.Hash));
        Assert.Equal("0011223344556677", Assert.Single(rec.Outputs).Hash);
        Assert.Equal(["skeletons/manny.swskeleton"], info.Assets["anim/set"].ContentInputs);

        // запись прочитанного — те же байты: формат задают только свойства классов
        Assert.Equal(sample.ToUtf8(), info.ToUtf8());
        Assert.Contains("\"project\": \"Тест\"", Encoding.UTF8.GetString(info.ToUtf8()));   // кириллица не экранируется
    }

    [Fact]
    public void OwnerAndSingleSource()
    {
        var info = Sample();
        Assert.Equal("combat/actions", info.OwnerOf("COMBAT/actions.json"));
        Assert.Null(info.OwnerOf("combat/actions.json", except: "combat/actions"));
        Assert.Null(info.OwnerOf("nowhere.bin"));
        Assert.Equal(@"D:\src\actions.json", info.SingleSourceOf("combat/actions.json"));
        Assert.Null(info.SingleSourceOf("anim/set/a.swclip"));   // каталог-вход и два файла — однозначного исходника нет
    }

    [Fact]
    public void NoFileOrOtherVersionIsNull_BrokenJsonIsInvalidData()
    {
        var empty = new MemorySource();
        Assert.Null(PakBuildInfo.Read(empty));
        Assert.Null(PakBuildInfo.Read(new MemorySource().Add(PakBuildInfo.EntryPath, """{ "version": 2, "project": "x" }"""u8.ToArray())));
        var broken = new MemorySource("пак").Add(PakBuildInfo.EntryPath, "{ \"version\": 1, "u8.ToArray());
        var e = Assert.Throws<InvalidDataException>(() => PakBuildInfo.Read(broken));
        Assert.StartsWith("пак:" + PakBuildInfo.EntryPath, e.Message);
    }
}
