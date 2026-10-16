using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Sword.Bake.Anim;
using Sword.Engine.Formats;

namespace Sword.Bake.Tests;

/// <summary>
/// Импорт клипов (<see cref="AnimImport"/>): ID наборов и скелета, хеш исходника клипа (без MVID сборок: те же исходники —
/// те же байты клипа в любой сборке пекарни).
/// </summary>
public sealed class ImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-import-" + Guid.NewGuid().ToString("N"));
    private readonly string _raw, _content;

    public ImportTests()
    {
        _raw = Path.Combine(_dir, "raw");
        _content = Path.Combine(_dir, "content");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("gasp", true)]
    [InlineData("1hm_sword", true)]
    [InlineData("ue5.manny-2", true)]
    [InlineData("gasp.", false)]   // Windows отбрасывает точку: это папка «gasp»
    [InlineData("con", false)]
    [InlineData("nul.x", false)]
    [InlineData("Gasp", false)]
    public void ImportId_RejectsNamesWindowsRewrites(string id, bool valid) =>
        Assert.Equal(valid, AnimImport.IsValidId(id));

    private RawManifest WriteExport(params RawClip[] clips)
    {
        var man = SynthSkeleton.Manifest();
        RawAnimIO.Write(_raw, man, clips);
        return RawAnimIO.ReadManifest(_raw);
    }

    private static RawClip[] ThreeClips() =>
    [
        SynthSkeleton.Walk("Walk_F", 31, loop: true, distance: 1.2f, turn: 0, swing: 0.4f, path: "/Game/Synth/Walk"),
        SynthSkeleton.Walk("Turn_R", 21, loop: false, distance: 0.3f, turn: MathF.PI / 2, swing: 0.2f, path: "/Game/Synth/Turn"),
        SynthSkeleton.Walk("Idle", 11, loop: true, distance: 0, turn: 0, swing: 0.05f, path: "/Game/Synth/Idle"),
    ];

    private string Output(string set, string clip)
    {
        string path = Path.Combine(_content, set, clip + ".swclip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static readonly Dictionary<string, List<string>> OneSet = new() { ["loco"] = [] };

    [Fact]
    public void SourceHash_ChangesWithSourceNotWithRebuild()
    {
        var man = WriteExport(ThreeClips());
        string walk = AnimImport.SourceHash(_raw, man, man.Clips[0], null);
        Assert.Equal(32, walk.Length);
        Assert.Equal(walk, AnimImport.SourceHash(_raw, RawAnimIO.ReadManifest(_raw), man.Clips[0], null));
        Assert.NotEqual(walk, AnimImport.SourceHash(_raw, man, man.Clips[1], null));

        // исходник .bin, параметры клипа и пространство экспорта — в хеше
        byte[] bin = File.ReadAllBytes(Path.Combine(_raw, "clips", "Walk_F.bin"));
        bin[100] ^= 1;
        File.WriteAllBytes(Path.Combine(_raw, "clips", "Walk_F.bin"), bin);
        Assert.NotEqual(walk, AnimImport.SourceHash(_raw, man, man.Clips[0], null));
        bin[100] ^= 1;
        File.WriteAllBytes(Path.Combine(_raw, "clips", "Walk_F.bin"), bin);
        Assert.Equal(walk, AnimImport.SourceHash(_raw, man, man.Clips[0], null));
        man.Clips[0].Loop = false;
        Assert.NotEqual(walk, AnimImport.SourceHash(_raw, man, man.Clips[0], null));
        man.Clips[0].Loop = true;
        man.Units = "m";
        Assert.NotEqual(walk, AnimImport.SourceHash(_raw, man, man.Clips[0], null));
    }

    /// <summary>
    /// Две сборки конвертера с разными MVID (как Debug и Release) — один хеш: копия сборки пекарни с подменённым MVID
    /// грузится в свой контекст и считает хеш тем же кодом. В SWORD MVID входил в хеш — смена конфигурации перепекала все клипы.
    /// </summary>
    [Fact]
    public void SourceHash_DoesNotDependOnConverterBuild()
    {
        var man = WriteExport(ThreeClips());
        string expected = AnimImport.SourceHash(_raw, man, man.Clips[1], null);

        var original = typeof(AnimImport).Assembly;
        byte[] image = File.ReadAllBytes(original.Location);
        byte[] mvid = original.ManifestModule.ModuleVersionId.ToByteArray(), other = Guid.NewGuid().ToByteArray();
        int replaced = 0;
        for (int at = image.AsSpan().IndexOf(mvid); at >= 0; at = image.AsSpan().IndexOf(mvid))
        {
            other.CopyTo(image, at);
            replaced++;
        }
        Assert.True(replaced > 0, "MVID не найден в образе сборки");

        var context = new AssemblyLoadContext("mvid-copy", isCollectible: true);
        try
        {
            var copy = context.LoadFromStream(new MemoryStream(image));
            Assert.NotEqual(original.ManifestModule.ModuleVersionId, copy.ManifestModule.ModuleVersionId);
            Assert.NotSame(typeof(AnimImport), copy.GetType(typeof(AnimImport).FullName!));
            var io = copy.GetType(typeof(RawAnimIO).FullName!)!;
            object copyMan = io.GetMethod(nameof(RawAnimIO.ReadManifest))!.Invoke(null, [_raw])!;
            var clips = (IList)copyMan.GetType().GetProperty(nameof(RawManifest.Clips))!.GetValue(copyMan)!;
            var hash = copy.GetType(typeof(AnimImport).FullName!)!
                .GetMethod(nameof(AnimImport.SourceHash), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(null, [_raw, copyMan, clips[1], null]);
            Assert.Equal(expected, hash);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void BadNames_AreErrors()
    {
        WriteExport(ThreeClips());
        var man = RawAnimIO.ReadManifest(_raw);
        var setOf = AnimImport.Route(man, OneSet);
        Assert.Throws<ArgumentException>(() => AnimImport.Run(_raw, setOf, "Bad Id", Output, TextWriter.Null));
        setOf.Remove("Idle");
        Assert.Throws<ArgumentException>(() => AnimImport.Run(_raw, setOf, "synth", Output, TextWriter.Null));
    }
}
