using System.Numerics;
using Sword.Engine.Content;

namespace Sword.Engine.Content.Tests;

public sealed class PakTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-pak-" + Guid.NewGuid().ToString("N"));

    public PakTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Pak(params (string Path, byte[] Bytes)[] files)
    {
        string path = Path.Combine(_dir, "test" + PakFormat.Extension);
        using var w = new PakWriter(path);
        foreach (var (p, bytes) in files) w.Add(p, new MemoryStream(bytes));
        w.Commit();
        return path;
    }

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void RoundTripReadSeekAndList()
    {
        var big = Random(700_000, 1);   // больше буфера потока — крупное чтение мимо буфера
        var small = "{\"id\":\"a\"}"u8.ToArray();
        using var pak = PakSource.Open(Pak(("anim/a/x.swclip", big), ("anim/a/source.json", small), ("fonts/empty.bin", [])));

        Assert.True(pak.Exists("ANIM/A/X.SWCLIP"));
        Assert.False(pak.Exists("anim/a/y.swclip"));
        Assert.Equal(big, pak.ReadAllBytes("anim/a/x.swclip"));
        Assert.Empty(pak.ReadAllBytes("fonts/empty.bin"));

        using (var s = pak.OpenRead("anim/a/x.swclip"))
        {
            Assert.Equal(big.Length, s.Length);
            s.Seek(123_457, SeekOrigin.Begin);
            var chunk = new byte[1000];
            s.ReadExactly(chunk);
            Assert.Equal(big.AsSpan(123_457, 1000).ToArray(), chunk);
            using var r = new BinaryReader(s, System.Text.Encoding.UTF8, leaveOpen: true);
            s.Position = 8;
            Assert.Equal(BitConverter.ToSingle(big, 8), r.ReadSingle());
        }

        Assert.Equal(["anim/a/x.swclip"], pak.List("anim", ".swclip"));
        Assert.Equal(["fonts/empty.bin"], pak.List("", ".bin"));
        Assert.Empty(pak.List("anima", ".swclip"));
        Assert.Empty(pak.Corrupted());
        Assert.All(pak.Entries, e => Assert.Equal(0, e.Offset % PakFormat.Alignment));
    }

    [Fact]
    public void PakReadsSameAsDirectory()
    {
        string content = Path.Combine(_dir, "content");
        var files = new Dictionary<string, byte[]>
        {
            ["characters/manny/mesh.swmesh"] = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray(),
            ["weapons/sword.swmesh"] = [1, 2, 3],
            ["empty.bin"] = [],
        };
        foreach (var (path, bytes) in files)
        {
            string full = Path.Combine(content, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        var dir = new DirectorySource(content);
        string pakPath = Path.Combine(_dir, "lib" + PakFormat.Extension);
        using (var w = new PakWriter(pakPath))
        {
            foreach (var f in Directory.GetFiles(content, "*", SearchOption.AllDirectories))
                using (var s = File.OpenRead(f))
                    w.Add(Path.GetRelativePath(content, f), s);
            w.Commit();
        }
        using var pak = PakSource.Open(pakPath);

        foreach (var path in files.Keys)
        {
            Assert.True(pak.Exists(path), path);
            Assert.Equal(dir.ReadAllBytes(path), pak.ReadAllBytes(path));
        }
    }

    /// <summary>
    /// Хеш контента — по файлам игры: те же файлы с другими сведениями пекарни (<c>.bake/build.json</c> — время сборки)
    /// дают тот же хеш, другой файл — другой.
    /// </summary>
    [Fact]
    public void ContentHash_IgnoresBakeInfo_ChangesWithAnyFile()
    {
        string Write(string name, byte[] skeleton, string buildJson)
        {
            string path = Path.Combine(_dir, name + PakFormat.Extension);
            using var w = new PakWriter(path);
            w.Add("data/a.txt", new MemoryStream("a"u8.ToArray()));
            w.Add("skeletons/manny.skel", new MemoryStream(skeleton));
            w.Add(PakBuildInfo.EntryPath, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(buildJson)));
            w.Commit();
            return path;
        }

        string Hash(string path)
        {
            using var pak = PakSource.Open(path);
            return pak.ContentHash;
        }

        string first = Hash(Write("first", [1, 2, 3], """{"builtUtc":"2026-09-25T10:00:00Z"}"""));
        string rebaked = Hash(Write("rebaked", [1, 2, 3], """{"builtUtc":"2026-09-25T11:30:00Z","assets":{}}"""));
        string other = Hash(Write("other", [1, 2, 4], """{"builtUtc":"2026-09-25T10:00:00Z"}"""));

        Assert.Equal(first, rebaked);
        Assert.NotEqual(first, other);
        Assert.Matches("^[0-9A-F]{16}$", first);
    }

    [Fact]
    public void TruncatedPakIsRejected()
    {
        string path = Pak(("a/b.bin", Random(5000, 2)));
        using (var f = new FileStream(path, FileMode.Open)) f.SetLength(f.Length - 10);
        Assert.Throws<InvalidDataException>(() => PakSource.Open(path));
    }

    [Fact]
    public void CorruptedDataFailsVerify()
    {
        string path = Pak(("a/b.bin", Random(5000, 3)), ("a/c.bin", Random(100, 4)));
        long offset;
        using (var pak = PakSource.Open(path)) offset = pak.Find("a/b.bin")!.Value.Offset;
        using (var f = new FileStream(path, FileMode.Open))
        {
            f.Position = offset + 2500;
            int b = f.ReadByte();
            f.Position = offset + 2500;
            f.WriteByte((byte)(b ^ 0xFF));
        }
        using var reopened = PakSource.Open(path);
        Assert.Equal(["a/b.bin"], reopened.Corrupted().Select(e => e.Path));
    }

    [Fact]
    public void DuplicatePathIsRejected()
    {
        using var w = new PakWriter(Path.Combine(_dir, "dup" + PakFormat.Extension));
        w.Add("a/b.bin", new MemoryStream([1]));
        Assert.Throws<ArgumentException>(() => w.Add("A/B.bin", new MemoryStream([2])));
    }
}
