using Sword.Engine.Animation;
using static Sword.Engine.Animation.Tests.TestClips;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Библиотека клипов — <c>Sword.Anim.Tests/LibraryTests</c> SWORD 2eab13b с теми же ожиданиями. Изменилось только, как
/// тест добирается до кода: файлы — потоки в памяти в порядке контента (<c>IContentSource.List</c> — по пути), а не
/// каталог на диске; скелет передаётся готовым (его отсутствие — забота игры, тест <c>Load_NoSkeleton</c> ушёл к ней).
/// Тест имён импорта (<c>ImportId</c>) — пекарня, переносится с ней.
/// </summary>
public sealed class LibraryTests
{
    [Fact]
    public void Load_GroupsBySourceAndSkipsForeignClips()
    {
        var skipped = new List<string>();
        // порядок путей контента: по возрастанию без учёта регистра
        var lib = ClipLibrary.Load(Skeleton(),
        [
            Marked("alpha", "Idle", 5, 3),
            Marked("alpha", "Other", 5, 4, skeleton: "other"),
            Marked("alpha", "Rate", 5, 6, rate: 60),
            Marked("alpha", "Slash", 3, 2),
            Marked("alpha", "Stale", 5, 5, hash: 1),
            Marked("zeta", "Walk_Loop", 4, 1),
        ], skipped)!;
        Assert.Equal(["alpha/Idle", "alpha/Slash", "zeta/Walk_Loop"], lib.Clips.ToArray().Select(c => c.Name));
        string log = string.Join("\n", skipped);
        Assert.Contains("alpha/Stale", log);
        Assert.Contains("alpha/Rate", log);
        Assert.DoesNotContain("alpha/Other", log);   // чужой скелет — молча
        Assert.Equal("zeta", lib.Clips[2].Source);
        Assert.Equal("Walk_Loop", lib.Clips[2].ShortName);
        Assert.True(lib.Clips[2].Loop);

        // позы и корневое движение скопированы в свои диапазоны
        var slash = lib.Clips[1];
        Assert.Equal(5, slash.Start);
        Assert.Equal(2, lib.LocalPos[slash.Start * Joints.Length].X);
        Assert.Equal(0.2f, lib.RootMotion[slash.Start + 2].Z, 5);

        Assert.Equal(1, lib.FindClip("Slash"));
        Assert.Equal(1, lib.FindClip("alpha/Slash"));
        Assert.Equal(-1, lib.FindClip("beta/Slash"));
    }

    [Fact]
    public void FindClip_AmbiguousShortName_NotFound()
    {
        var lib = Library(Marked("a", "Idle", 2, 0), Marked("b", "Idle", 2, 0));
        Assert.Equal(-1, lib.FindClip("Idle"));
        Assert.Equal(1, lib.FindClip("b/Idle"));
    }

    [Fact]
    public void Load_NoClips_ReturnsNull()
    {
        Assert.Null(ClipLibrary.Load(Skeleton(), []));
        Assert.Null(ClipLibrary.Load(Skeleton(), [Marked("a", "Idle", 2, 0, skeleton: "other")]));
    }

    [Fact]
    public void Load_IgnoresFilesOutsideSourceFolders()
    {
        var deep = Marked("a", "Idle", 2, 0);
        var lib = Library(deep with { Path = "anim/a/sub/Idle.swclip" }, Marked("a", "Walk", 2, 0), deep with { Path = "anim/Idle.swclip" });
        Assert.Equal(["a/Walk"], lib.Clips.ToArray().Select(c => c.Name));
    }

    [Fact]
    public void Load_CorruptClip_IsSkippedWithReason()
    {
        var bad = new ClipSource("anim/a/Bad.swclip", () => new MemoryStream([1, 2, 3]), "mem:bad");
        var skipped = new List<string>();
        var lib = ClipLibrary.Load(Skeleton(), [bad, Marked("a", "Idle", 2, 0)], skipped)!;
        Assert.Equal(["a/Idle"], lib.Clips.ToArray().Select(c => c.Name));
        Assert.Contains(skipped, s => s.StartsWith("a/Bad: пропущен", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_LoopWrapsAndOneShotClamps()
    {
        var lib = Library(Marked("a", "Walk_Loop", 4, 0), Marked("a", "Hit", 4, 0));
        var rot = new System.Numerics.Quaternion[Joints.Length];
        var pos = new System.Numerics.Vector3[Joints.Length];
        // y смещения — номер ключа в файле клипа: кадр f сустава 0 — f·3
        lib.Sample(0, 1.5f / 30, rot, pos);
        Assert.Equal(4.5f, pos[0].Y, 4);
        lib.Sample(0, 4f / 30, rot, pos);   // цикл 3 кадра: 4 → 1
        Assert.Equal(3f, pos[0].Y, 4);
        lib.Sample(0, -1f / 30, rot, pos);  // назад по кругу: −1 → 2
        Assert.Equal(6f, pos[0].Y, 4);
        lib.Sample(1, 10, rot, pos);        // не цикл — последний кадр (номер ключа — внутри своего файла)
        Assert.Equal(3 * 3f, pos[0].Y, 4);
        Assert.Equal(3f / 30, lib.Duration(1), 6);
        Assert.Equal(0.3f / (3f / 30), lib.RootSpeed(1), 4);
    }
}
